"""Bounded source samples and persistent observations, separate from preferences."""
import asyncio
from contextlib import asynccontextmanager
import json
import math
import re
import threading
import time
from urllib.parse import urlsplit

import httpx
from PIL import Image
from fastapi import APIRouter, Depends, HTTPException, Request

from .async_utils import run_blocking
from .db import db
from .image_safety import open_raster
from .source_errors import source_error_kind, source_error_summary
from .support_logs import support_logs
from .suwayomi import HumanAssistanceRequired, SourceWorkDeferred, source_host, source_work_budget
from .task_registry import task_registry

DAY = 86400
DAILY_KEY = 'source_health_daily_v1'
STATES = {'working', 'failed', 'inconclusive', 'deferred', 'verification', 'rate_limited', 'cancelled'}


class Inconclusive(RuntimeError):
    pass


def stamp(value):
    return value if type(value) in (int, float) and math.isfinite(value) and 0 < value < 10**11 else 0


def classify(error):
    if isinstance(error, HumanAssistanceRequired) or source_error_kind(error) == 'captcha': return 'verification'
    if isinstance(error, SourceWorkDeferred) or source_error_kind(error) == 'deferred': return 'deferred'
    if source_error_kind(error) == 'rate_limit': return 'rate_limited'
    if isinstance(error, (Inconclusive, TimeoutError, httpx.TimeoutException, httpx.RequestError)):
        return 'inconclusive'
    if source_error_kind(error) in {'timeout', 'access_denied', 'origin_unavailable'}: return 'inconclusive'
    if any(word in str(error).lower() for word in ('unsupportedoperationexception','not implemented')): return 'inconclusive'
    if 'buffered-analysis limit' in str(error): return 'inconclusive'
    return 'failed'


class HealthStore:
    def __init__(self, database=db): self.db = database

    def daily(self):
        value = self.db.setting(DAILY_KEY, {'enabled': True})
        return {'enabled': isinstance(value, dict) and value.get('enabled') is True}

    def configure(self, enabled):
        self.db.set_setting(DAILY_KEY, {'enabled': bool(enabled)})

    def snapshot(self):
        with self.db.catalog() as conn:
            rows = [dict(row) for row in conn.execute('''SELECT sc.source_id,sc.name,sc.lang,sc.base_url,sc.available,
                sc.enabled_language,sc.enabled_search,sc.enabled_chapters,sc.supports_latest,
                h.state,h.message,h.checked_at,h.started_at,h.last_success_at,h.last_repaired_at,h.last_changed_at,h.next_check_at,
                h.package_name,h.checked_version,h.repair_job_id,h.checks_json,h.history_json
                FROM source_config sc LEFT JOIN source_health h ON h.source_id=sc.source_id
                WHERE sc.available=1
                ORDER BY sc.name COLLATE NOCASE,sc.lang COLLATE NOCASE''')]
        for row in rows:
            for key in ('checked_at','started_at','last_success_at','last_repaired_at','last_changed_at','next_check_at'): row[key] = row[key] or 0
            row['state'] = row['state'] or 'unchecked'; row['message'] = row['message'] or ''
            row['eligible'] = bool(row['available'] and row['enabled_language'] and (row['enabled_search'] or row['enabled_chapters']))
            row['needs_recheck'] = max(row['last_repaired_at'],row['last_changed_at']) > row['started_at']
            row['stale'] = bool(row['checked_at'] and time.time() - row['checked_at'] > DAY)
            for key in ('checks','history'):
                try: value = json.loads(row.pop(key+'_json') or '[]')
                except ValueError: value = []
                row[key] = value if isinstance(value, list) else []
        due = [row['next_check_at'] for row in rows if row['eligible']]
        return {'items': rows, 'daily': {**self.daily(), 'next_due_at': min(due) if due else None}}

    def reserve(self, sid):
        with self.db.catalog() as conn:
            conn.execute('''INSERT INTO source_health(source_id,next_check_at) VALUES(?,?)
                ON CONFLICT(source_id) DO UPDATE SET next_check_at=excluded.next_check_at''', (sid,time.time()+DAY))

    def source(self, sid):
        with self.db.catalog() as conn:
            row=conn.execute('''SELECT sc.available,sc.enabled_language,sc.enabled_search,sc.enabled_chapters,coalesce(h.next_check_at,0) next_check_at
                FROM source_config sc LEFT JOIN source_health h ON h.source_id=sc.source_id
                WHERE sc.source_id=?''',(sid,)).fetchone()
        if not row: return None
        result=dict(row); result['eligible']=bool(row['available'] and row['enabled_language'] and (row['enabled_search'] or row['enabled_chapters']))
        return result

    def finish(self, sid, state, message, checks, origin, *, stamp=None, started_at=None, package='', version=''):
        if state not in STATES: raise ValueError('Invalid source health state')
        when = time.time() if stamp is None else stamp
        began = when if started_at is None else started_at
        message = support_logs.privacy.text(str(message))[:600]
        with self.db.catalog() as conn:
            old = conn.execute('SELECT history_json FROM source_health WHERE source_id=?',(sid,)).fetchone()
            try: history = json.loads(old['history_json']) if old else []
            except ValueError: history=[]
            if not isinstance(history,list): history=[]
            history = history[-7:] + [{'state': state, 'at': when, 'origin': origin, 'message': message}]
            conn.execute('''INSERT INTO source_health(source_id,state,message,checked_at,started_at,last_success_at,next_check_at,
                package_name,checked_version,checks_json,history_json) VALUES(?,?,?,?,?,?,?,?,?,?,?)
                ON CONFLICT(source_id) DO UPDATE SET state=excluded.state,message=excluded.message,
                checked_at=excluded.checked_at,started_at=excluded.started_at,last_success_at=CASE WHEN excluded.state='working'
                THEN excluded.checked_at ELSE source_health.last_success_at END,next_check_at=excluded.next_check_at,
                package_name=CASE WHEN excluded.package_name!='' THEN excluded.package_name ELSE source_health.package_name END,
                checked_version=excluded.checked_version,checks_json=excluded.checks_json,history_json=excluded.history_json''',
                (sid,state,message,when,began,when if state=='working' else 0,when+(300 if state=='deferred' else DAY),
                 package,version,json.dumps(checks),json.dumps(history)))
        support_logs.record('source-health',json.dumps({'source_id':sid,'state':state,'checked_at':when,
            'origin':origin,'checks':checks,'message':message}))

    def repairs(self, jobs, operation):
        events = []
        for job in jobs:
            if job.get('state') == 'installed':
                events.append((job.get('expected_ids'),stamp(job.get('updated_at')),job.get('id'),job.get('pkg_name')))
        if operation.get('state') in {'installed','recovered'}:
            extension = operation.get('extension') or {}
            events.append(([row.get('id') for row in extension.get('source',{}).get('nodes',[])],
                           stamp(operation.get('installed_at')),operation.get('id'),extension.get('pkgName')))
        if operation.get('state')=='rolled-back' and stamp(operation.get('changed_at')):
            ids=[str(row.get('id')) for row in (operation.get('extension') or {}).get('source',{}).get('nodes',[])]
            with self.db.catalog() as conn:
                prior={row['source_id']:row['last_changed_at'] for row in conn.execute('SELECT source_id,last_changed_at FROM source_health')}
                for sid in ids:
                    if re.fullmatch(r'-?[0-9]{1,20}',sid) and operation['changed_at']>prior.get(sid,0):
                        conn.execute('''INSERT INTO source_health(source_id,last_changed_at) VALUES(?,?)
                            ON CONFLICT(source_id) DO UPDATE SET last_changed_at=max(source_health.last_changed_at,excluded.last_changed_at)''',
                            (sid,operation['changed_at']))
        with self.db.catalog() as conn:
            prior={row['source_id']:row['last_repaired_at'] for row in conn.execute('SELECT source_id,last_repaired_at FROM source_health')}
            for ids,when,identifier,package in events:
                if not when or not isinstance(ids,list) or len(ids)>200 or not re.fullmatch('[0-9a-f]{32}',str(identifier or '')):
                    continue
                for value in ids:
                    sid = str(value)
                    if not re.fullmatch(r'-?[0-9]{1,20}',sid) or when<=prior.get(sid,0): continue
                    conn.execute('''INSERT INTO source_health(source_id,last_repaired_at,repair_job_id,package_name)
                        VALUES(?,?,?,?) ON CONFLICT(source_id) DO UPDATE SET last_repaired_at=excluded.last_repaired_at,
                        repair_job_id=excluded.repair_job_id,package_name=excluded.package_name
                        WHERE excluded.last_repaired_at>source_health.last_repaired_at''', (sid,when,identifier,str(package or '')[:240]))
                    prior[sid]=when


def verify_image(data):
    try:
        with open_raster(data, max_pixels=16_000_000) as image:
            if image.width < 2 or image.height < 2: raise ValueError('Placeholder image')
            image.load()
    except Exception as exc:
        if isinstance(exc,Image.DecompressionBombError) or 'analysis limit' in str(exc):
            raise Inconclusive('Sample image exceeds the bounded image-analysis budget') from exc
        raise ValueError('Sample image could not be decoded as a usable bounded image') from exc


async def probe_source(client, source, checks, *, pace=3):
    sid = str(source['source_id'])
    async def step(name, call):
        if checks: await asyncio.sleep(pace)
        checks.append({'stage': name, 'state': 'checking'})
        async with source_work_budget(30, admission_timeout=3):
            value = await call()
        checks[-1]['state'] = 'passed'
        return value
    feed = await step('catalog', lambda: client.fetch_source_manga(sid, 'POPULAR'))
    works = feed.get('mangas') or []
    if not works: raise Inconclusive('Catalog returned no titles to sample')
    if source.get('supports_latest'):
        latest=await step('latest catalog',lambda:client.fetch_source_manga(sid,'LATEST'))
        if not latest.get('mangas'): raise Inconclusive('Supported latest catalog returned no titles to verify')
    work = works[0]; mid = int(work['id']); title = str(work.get('title') or '').strip()
    if mid <= 0 or str(work.get('sourceId')) != sid or not title: raise ValueError('Catalog sample identity is invalid')
    search = await step('search', lambda: client.fetch_source_manga(sid, 'SEARCH', query_text=title))
    if not any(str(row.get('id')) == str(mid) for row in search.get('mangas') or []):
        raise Inconclusive('Search did not retrieve the sampled title; check source preferences or search support')
    data = await step('metadata/chapters', lambda: client.fetch_manga_and_chapters(mid))
    manga,chapters = data.get('manga') or {},data.get('chapters') or []
    if str(manga.get('id'))!=str(mid) or str(manga.get('sourceId')) != sid or not manga.get('title') or not manga.get('url') or not chapters:
        raise ValueError('Sample metadata, source identity or chapter list is incomplete')
    if not chapters[0].get('name') or not chapters[0].get('url') or int(chapters[0].get('id') or 0)<=0:
        raise ValueError('Sample chapter identity or metadata is incomplete')
    if not any(manga.get(key) for key in ('description','author','artist','genre')):
        raise Inconclusive('The sampled title has no descriptive metadata to verify')
    async def image(route):
        content,_ = await client._run_source_work(lambda: client.fetch_binary(route))
        await run_blocking(verify_image, content)
    await step('cover', lambda: image(f'/api/v1/manga/{mid}/thumbnail'))
    pages = await step('page list', lambda: client.fetch_chapter_pages(int(chapters[0]['id'])))
    if not pages or not isinstance(pages[0],str) or not re.fullmatch(fr'/api/v1/manga/{mid}/chapter/[0-9]+/page/[0-9]+',pages[0]):
        raise ValueError('Sample page list is empty or contains an unexpected reader route')
    await step('page image', lambda: image(pages[0]))


class SourceHealth:
    def __init__(self, store=None, client=None, service=None):
        self.store = store or HealthStore(); self.client = client or source_host; self.service = service
        self.jobs = {}; self.locks = {}; self.site_last = {}; self.checking = {}; self.state_lock=threading.RLock()
        self.loop = None; self.stopping = False

    def owner(self):
        if self.service is None:
            from .service import service
            self.service = service
        return self.service

    def snapshot(self):
        from .repair_tasks import repair_tasks
        from .source_maintenance import source_maintenance
        with repair_tasks.lock: jobs = [dict(job) for job in repair_tasks.jobs.values()]
        self.store.repairs(jobs,dict(source_maintenance.operation))
        with self.state_lock:
            active={kind:value[0] for kind,value in self.jobs.items() if not value[1].done()}
            checking=list(self.checking)
        result=self.store.snapshot()
        from .repair_workflow import repair_workflow
        histories=repair_workflow.history.summaries(include_source_ids=True) if repair_workflow.history else {}
        for row in result.get('items',[]):
            source_history=histories.get(row.get('package_name'))
            if not source_history:
                source_history=next((value for value in histories.values() if str(row['source_id']) in (value.get('current') or {}).get('source_ids',[])),{})
            row['last_confirmed_working_at']=source_history.get('working_at') or 0
            row['repair_confirmation']=(source_history.get('current') or {}).get('status','unknown')
        return {**result, 'active':active, 'checking':checking}

    def queue(self, rows, origin):
        owner = self.owner()
        if self.stopping or owner._background_stopping: raise HTTPException(503,'Source checks paused for maintenance')
        old = self.jobs.get(origin)
        ids={row['source_id'] for row in rows}
        if old and not old[1].done():
            if origin=='manual' and not ids<=old[2]: raise HTTPException(409,'A manual source check is already running; finish or cancel it in Tasks')
            return {'queued':False,'task_id':old[0],'coalesced':True}
        task_id = task_registry.create('Check source health',total=len(rows),message='Queued source samples',
                                       origin=origin,activity='queued',context=f'{len(rows)} source variant(s)')
        task = owner._track_auxiliary(self.run(rows,origin,task_id))
        with self.state_lock: self.jobs[origin] = (task_id,task,ids)
        task_registry.bind_runtime(task_id,task)
        return {'queued':True,'task_id':task_id}

    async def run(self, rows, origin, task_id):
        owner = self.owner(); states = {}
        try:
            for row in rows:
                if origin=='automatic' and not (await run_blocking(self.store.daily))['enabled']: break
                sid = str(row['source_id']); checks=[]
                if origin=='automatic':
                    task_registry.mark_waiting(task_id,message='Waiting for an idle browser before daily source checks')
                    await owner._wait_for_background_idle()
                try: site=urlsplit(row.get('base_url') or '').hostname or sid
                except ValueError: site=sid
                lock = self.locks.setdefault(site,asyncio.Lock())
                if lock.locked(): task_registry.mark_waiting(task_id,message='Waiting for this website’s current source check')
                async with lock:
                    # A manual check can satisfy a daily sample while its lane waits.
                    current = await run_blocking(self.store.source,sid)
                    if not current or not current['available'] or (origin=='automatic' and (not current['eligible'] or current['next_check_at']>time.time())):
                        task_registry.advance(task_id,1); continue
                    await asyncio.sleep(max(0,self.site_last.get(site,0)+3-time.monotonic()))
                    task_registry.mark_waiting(task_id,message='Waiting for a '+('manual' if origin=='manual' else 'background')+' task slot',queued=True)
                    lane = owner._manual_task_slot() if origin=='manual' else owner._background_task_slot()
                    async with lane:
                        task_registry.mark_active(task_id,reset_estimate=False)
                        current=await run_blocking(self.store.source,sid)
                        if not current or not current['available'] or (origin=='automatic' and not current['eligible']):
                            task_registry.advance(task_id,1); continue
                        began=time.time(); package=version=''
                        with self.state_lock: self.checking[sid]=task_id
                        task_registry.update(task_id,message='Checking '+row['name']+' · '+row['lang'])
                        from .suwayomi import background_source_work
                        @asynccontextmanager
                        async def scope():
                            if origin=='automatic':
                                async with background_source_work(): yield
                            else: yield
                        try:
                            await run_blocking(self.store.reserve,sid)
                            # Local installed metadata only; no repository refresh.
                            from .source_maintenance import inventory
                            try:
                                extensions=await asyncio.wait_for(inventory(),8)
                                extension=next((item for item in extensions if item.get('isInstalled') and
                                    any(str(source.get('id'))==sid for source in item.get('source',{}).get('nodes',[]))),{})
                                package=str(extension.get('pkgName') or '')[:240]; version=str(extension.get('versionName') or '')[:100]
                            except asyncio.CancelledError: raise
                            except Exception as exc: raise SourceWorkDeferred('Installed source metadata is unavailable; no website sample was requested') from exc
                            if not extension: raise Inconclusive('No compatible installed extension is recorded for this source; sync sources first')
                            async with scope(), asyncio.timeout(180):
                                await probe_source(self.client,row,checks)
                            state,message = 'working','Supported catalogs, search, metadata, cover and one chapter image sample passed.'
                        except asyncio.CancelledError:
                            if checks: checks[-1]['state']='cancelled'
                            await run_blocking(self.store.finish,sid,'cancelled','Source check cancelled',checks,origin,started_at=began)
                            raise
                        except Exception as exc:
                            state,message = classify(exc),source_error_summary(exc,row['name'])
                            if checks: checks[-1]['state']=state
                        finally:
                            with self.state_lock: self.checking.pop(sid,None)
                            self.site_last[site]=time.monotonic()
                        await run_blocking(self.store.finish,sid,state,message,checks,origin,started_at=began,package=package,version=version)
                        states[state]=states.get(state,0)+1
                task_registry.advance(task_id,1)
            message = ', '.join(f'{count} {state.replace("_"," ")}' for state,count in states.items()) or 'No source samples were due'
            if any(key!='working' for key in states): task_registry.fail(task_id,message,message=message)
            else: task_registry.complete(task_id,message=message)
        except asyncio.CancelledError:
            task_registry.cancel(task_id,message='Source health check cancelled'); raise
        except Exception as exc:
            task_registry.fail(task_id,support_logs.privacy.text(str(exc))); raise

    async def tick(self, lock):
        async with lock:
            if self.stopping or self.owner()._background_stopping: return
            snapshot = await run_blocking(self.snapshot)
            if not snapshot['daily']['enabled']: return
            from .source_engine import source_engine
            if source_engine.state!='ready': return
            rows = [row for row in snapshot['items'] if row['eligible'] and row['next_check_at']<=time.time()]
            if rows: self.queue(rows,'automatic')

    def start(self, lock):
        self.stopping=False
        async def watch():
            while not self.stopping:
                await asyncio.sleep(60)
                try: await self.tick(lock)
                except asyncio.CancelledError: raise
                except Exception as exc: support_logs.record('source-health','Daily admission failed: '+type(exc).__name__)
        self.loop=asyncio.create_task(watch(),name='source-health-schedule')

    async def stop(self):
        self.stopping=True
        tasks = [value[1] for value in self.jobs.values() if not value[1].done()]
        if self.loop and not self.loop.done(): tasks.append(self.loop)
        for task in tasks: task.cancel()
        await asyncio.gather(*tasks,return_exceptions=True)


source_health = SourceHealth()


def health_router(admin_guard, lock):
    router=APIRouter(prefix='/api/admin/source-health',dependencies=[Depends(admin_guard)])
    async def body_json(request):
        raw=bytearray()
        async for chunk in request.stream():
            raw.extend(chunk)
            if len(raw)>16384: raise HTTPException(413,'Source health request exceeds 16 KiB')
        try:
            value=json.loads(raw)
            if not isinstance(value,dict): raise ValueError()
            return value
        except (ValueError,UnicodeDecodeError): raise HTTPException(400,'Expected a source health request object') from None
    @router.get('')
    async def snapshot(): return await run_blocking(source_health.snapshot)
    @router.put('/daily')
    async def daily(request:Request):
        body=await body_json(request)
        if not isinstance(body,dict) or type(body.get('enabled')) is not bool: raise HTTPException(400,'Expected enabled boolean')
        async with lock: await run_blocking(source_health.store.configure,body['enabled'])
        if not body['enabled']:
            job=source_health.jobs.get('automatic')
            if job and not job[1].done():
                job[1].cancel(); await asyncio.gather(job[1],return_exceptions=True)
        return await run_blocking(source_health.snapshot)
    @router.post('/check',status_code=202)
    async def check(request:Request):
        body=await body_json(request)
        if not isinstance(body,dict): raise HTTPException(400,'Expected a source check request')
        async with lock:
            rows=(await run_blocking(source_health.snapshot))['items']
            ids=body.get('source_ids')
            if body.get('all_enabled') is True: rows=[row for row in rows if row['eligible']]
            elif isinstance(ids,list) and 1<=len(ids)<=500 and all(isinstance(sid,str) and re.fullmatch(r'-?[0-9]{1,20}',sid) for sid in ids):
                selected=set(ids); rows=[row for row in rows if row['source_id'] in selected and row['available']]
                if {row['source_id'] for row in rows}!=selected: raise HTTPException(404,'Installed source not found; refresh sources')
            else: raise HTTPException(400,'Select installed sources or all_enabled')
            if not rows: raise HTTPException(409,'No enabled installed sources to check; sync sources first')
            return source_health.queue(rows,'manual')
    return router
