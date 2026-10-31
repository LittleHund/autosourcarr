"""Main-Suwayomi membership, exact download observations and bounded monitoring."""
import asyncio
from contextlib import asynccontextmanager
import hashlib
import json
import re
from datetime import datetime,timezone
from .async_utils import run_blocking
from .db import now_iso
from .task_registry import task_registry
from .due_scheduler import DueScheduler
from .runtime_observability import metrics


def revision(value):
    return hashlib.sha256(json.dumps(value,sort_keys=True,separators=(',',':')).encode()).hexdigest()[:32]


QUEUE_FIELDS='state queue { state progress tries position chapter { id name } manga { id title sourceId } }'


class LibraryManager:
    def __init__(self,database,service,client):
        self.db=database; self.service=service; self.client=client
        self.lock=asyncio.Lock(); self.worker=None; self.stopping=False
        self.admin_lock=None; self.interval=60
        self.due=DueScheduler(database)

    def require_client(self):
        if self.client is None: raise ValueError('Configure main Suwayomi in Settings first')

    def configuration(self):
        with self.db.catalog() as c:
            sources=[tuple(r) for r in c.execute('SELECT source_id,available,enabled_language,enabled_search,enabled_chapters,group_id FROM source_config ORDER BY source_id')]
        return revision(['library-due-v1',sources])

    def tracking(self,entry_id):
        with self.db.catalog() as c:
            if not c.execute('SELECT 1 FROM entries WHERE id=?',(entry_id,)).fetchone(): raise KeyError(entry_id)
            row=c.execute('SELECT * FROM library_tracking WHERE entry_id=?',(entry_id,)).fetchone()
            complete=c.execute('SELECT * FROM library_complete_observations WHERE entry_id=?',(entry_id,)).fetchone()
        result=dict(row) if row else {'entry_id':entry_id,'revision':0,'monitored':0,'main_manga_id':None,'observation_json':'{}','observed_at':''}
        result['observation']=json.loads(result.pop('observation_json'))
        result['due']=self.due.state('library',entry_id)
        result['last_complete_observation']={**json.loads(complete['observation_json']),'observed_at':complete['observed_at']} if complete else None
        return result

    def save_tracking(self,entry_id,*,expected=None,monitored=None,main_id=None,observation=None):
        with self.db.catalog() as c:
            c.execute('BEGIN IMMEDIATE')
            current=self.tracking(entry_id)
            if expected is not None and current['revision']!=expected: raise ValueError('Library controls changed; reload before applying')
            if monitored is not None and bool(monitored)!=bool(current['monitored']):
                c.execute("DELETE FROM due_work WHERE kind='library' AND work_key=?",(str(entry_id),))
            c.execute('''INSERT INTO library_tracking(entry_id,monitored,revision,main_manga_id,observation_json,observed_at,updated_at)
             VALUES(?,?,?,?,?,?,?) ON CONFLICT(entry_id) DO UPDATE SET monitored=excluded.monitored,revision=excluded.revision,
             main_manga_id=excluded.main_manga_id,observation_json=excluded.observation_json,observed_at=excluded.observed_at,updated_at=excluded.updated_at''',
             (entry_id,int(current['monitored'] if monitored is None else monitored),current['revision']+1,
              main_id if main_id is not None else current['main_manga_id'],
              json.dumps(current['observation'] if observation is None else observation),
              now_iso() if observation is not None else current['observed_at'],now_iso()))
            if observation is not None and observation.get('download_state')=='checked' and observation.get('coverage_state')=='checked':
                c.execute('INSERT OR REPLACE INTO library_complete_observations VALUES(?,?,?)',(entry_id,json.dumps(observation),now_iso()))
        return self.tracking(entry_id)

    async def manga_state(self,manga_id):
        data=await self.client.graphql('query ManagedManga($id:Int!){manga(id:$id){id sourceId url inLibrary}}',{'id':int(manga_id)})
        row=data.get('manga')
        if not isinstance(row,dict) or type(row.get('id')) is not int or row.get('id')!=int(manga_id) or type(row.get('inLibrary')) is not bool:
            raise ValueError('Main Suwayomi returned incomplete manga membership')
        return row

    async def exact_manga(self,entry_id):
        self.require_client()
        source_id=await self.service._main_autosourcarr_source_id()
        state=await run_blocking(self.tracking,entry_id)
        candidates=[]
        if state.get('main_manga_id'): candidates.append(await self.manga_state(state['main_manga_id']))
        if not candidates:
            with self.db.catalog() as c:
                row=c.execute('SELECT preferred_title FROM entries WHERE id=?',(entry_id,)).fetchone()
            found=await self.service._main_manga_for_autosourcarr_entry(entry_id,source_id,[row['preferred_title']])
            if found: candidates.append(await self.manga_state(found['id']))
        for candidate in candidates:
            raw=str(candidate.get('url') or '').strip().rstrip('/')
            match=re.fullmatch(r'(?:.*/)?(\d+)',raw)
            if match and str(candidate.get('sourceId'))==str(source_id) and self.service.canonical_entry_id(int(match.group(1)))==entry_id:
                return candidate
        raise ValueError('Main Suwayomi has no exact AutoSourcarr identity for this work')

    async def membership(self,entry_id,in_library,monitored,expected):
        self.require_client()
        async with self.lock:
            current=await run_blocking(self.tracking,entry_id)
            if current['revision']!=expected: raise ValueError('Library controls changed; reload before applying')
            manga=await self.exact_manga(entry_id)
            targets={manga['id']:manga}
            if not in_library:
                library=await self.service.subbed_entry_ids(force=True)
                if library.get('ok') is not True:raise ValueError('Main library membership is unknown; refresh before removing')
                for item in library.get('items') or []:
                    if str(item.get('sourceId'))!=str(manga['sourceId']):continue
                    match=re.fullmatch(r'(?:.*/)?(\d+)',str(item.get('url') or '').strip().rstrip('/'))
                    if match and self.service.canonical_entry_id(int(match.group(1)))==entry_id:
                        exact=await self.manga_state(item['id'])
                        if exact.get('sourceId')!=manga.get('sourceId') or str(exact.get('url'))!=str(item.get('url')):
                            raise ValueError('Main library identity changed; reload before removing')
                        targets[exact['id']]=exact
                if len(targets)>100:raise ValueError('More than 100 retained aliases need explicit review before removal')
            # Persist intent before I/O, so a stale double-click cannot replay an
            # uncertain mutation. Removal changes membership only, never downloads.
            await run_blocking(self.save_tracking,entry_id,expected=expected,monitored=False if not in_library else monitored,main_id=manga['id'])
            error=None
            for target in targets.values():
                if target['inLibrary']!=in_library:
                    try: await self.client.set_manga_in_library(target['id'],in_library)
                    except Exception as exc: error=exc
                observed=await self.manga_state(target['id'])
                if observed['inLibrary']!=in_library:
                    raise ValueError('Main library outcome is not confirmed; refresh before trying again') from error
            self.service._subbed_cache=None
            final=await self.service.subbed_entry_ids(force=True)
            if not in_library and final.get('ok') is True and entry_id in final.get('entry_ids',[]):
                raise ValueError('Another retained subscription still owns this work; refresh and review its main-library identity')
            return {'in_library':in_library,'reconciled':error is not None,**(await run_blocking(self.tracking,entry_id))}

    async def chapters(self,manga_id):
        rows=[]; after=None; seen=set();seen_ids=set()
        for _ in range(100):
            payload=await self.client.graphql('''query ManagedChapters($id:Int!,$after:Cursor){
              chapters(condition:{mangaId:$id},first:500,after:$after){nodes{id mangaId url name isDownloaded} pageInfo{hasNextPage endCursor}}}''',{'id':int(manga_id),'after':after})
            node=payload.get('chapters')
            if not isinstance(node,dict) or not isinstance(node.get('nodes'),list) or not isinstance(node.get('pageInfo'),dict):
                raise ValueError('Incomplete chapter observation; downloads remain unknown')
            for row in node['nodes']:
                if not isinstance(row,dict) or row.get('mangaId')!=int(manga_id) or type(row.get('isDownloaded')) is not bool or type(row.get('id')) is not int:
                    raise ValueError('Incomplete chapter identity/download state')
                if row['id'] in seen_ids:raise ValueError('Chapter pagination repeated an identity; observation incomplete')
                seen_ids.add(row['id'])
                rows.append(row)
            page=node['pageInfo']
            if page.get('hasNextPage') is False: return rows
            after=page.get('endCursor')
            if not after or after in seen: raise ValueError('Chapter pagination did not finish')
            seen.add(after)
        raise ValueError('Chapter observation exceeded its safety limit')

    async def observe(self,entry_id,*,refresh=False):
        config=await run_blocking(self.configuration)
        await run_blocking(self.due.attempted,'library',entry_id,config)
        try:
            with metrics.phase('observation'):
                result=await self._observe(entry_id,refresh=refresh)
            complete=all(result['observation'].get(k)=='checked' for k in ('download_state','coverage_state'))
            await run_blocking(self.due.finished,'library',entry_id,complete=complete,success=True,
                              reason='' if complete else 'Source chapter observation remains incomplete')
            return await run_blocking(self.tracking,entry_id)
        except BaseException as exc:
            # A failed/abandoned local observation cannot keep presenting its
            # previous counts as a fresh confirmed missing-download result.
            try:
                current=await run_blocking(self.tracking,entry_id)
                state='deferred' if type(exc).__name__ in {'SourceWorkDeferred','CancelledError'} else 'verification' if type(exc).__name__=='HumanAssistanceRequired' else 'unknown'
                await run_blocking(self.save_tracking,entry_id,observation={**current['observation'],'download_state':state,'coverage_state':'verification','incomplete_reason':type(exc).__name__})
                await run_blocking(self.due.finished,'library',entry_id,deferred=state=='deferred',reason=type(exc).__name__)
            except Exception:pass
            raise

    async def _observe(self,entry_id,*,refresh=False):
        manga=await self.exact_manga(entry_id)
        if not manga['inLibrary']: raise ValueError('Add this work to the main library before checking downloads')
        refresh_started=datetime.now(timezone.utc)
        if refresh:
            await self.service.refresh_entry(entry_id,fetch_details=False,fetch_chapters=True)
            await self.client.fetch_manga_and_chapters(manga['id'],False,True)
        chapters=await self.chapters(manga['id'])
        # Missing comes from exact main chapter IDs with an explicit false flag,
        # never a difference between aggregate counts from different snapshots.
        missing=[r['id'] for r in chapters if r['isDownloaded'] is False]
        stats=await run_blocking(self.service.source_record_chapter_stats,[entry_id])
        values=list(stats.values())
        coverage_checked=bool(values) and all(r.get('chapters_checked') and not r.get('chapter_check_error') for r in values)
        checked_at=[str(r.get('chapter_checked_at') or '') for r in values]
        if refresh and coverage_checked:
            try:coverage_checked=all(datetime.fromisoformat(value.replace('Z','+00:00'))>=refresh_started for value in checked_at)
            except (ValueError,TypeError):coverage_checked=False
        observation={'download_state':'checked','chapter_count':len(chapters),'downloaded':len(chapters)-len(missing),
          'missing_downloads':len(missing),'coverage_state':'checked' if coverage_checked else 'verification',
          'source_gaps':sum(int(r.get('missing_chapter_count') or 0) for r in values if r.get('chapters_checked')),
          'coverage_checked_at':min(checked_at) if checked_at and all(checked_at) else '',
          'main_manga_id':manga['id']}
        return await run_blocking(self.save_tracking,entry_id,main_id=manga['id'],observation=observation)

    async def queue(self):
        self.require_client()
        payload=await self.client.graphql('query ManagedQueue{downloadStatus{'+QUEUE_FIELDS+'}}')
        value=payload.get('downloadStatus')
        if not isinstance(value,dict) or value.get('state') not in {'STARTED','STOPPED'} or not isinstance(value.get('queue'),list):
            raise ValueError('Main Suwayomi returned incomplete queue state')
        if len(value['queue'])>10000:raise ValueError('Queue exceeds the 10000-item control safety limit; manage it in main Suwayomi')
        for row in value['queue']:
            if not isinstance(row,dict) or not isinstance(row.get('chapter'),dict) or type(row['chapter'].get('id')) is not int or not isinstance(row.get('manga'),dict) or type(row['manga'].get('id')) is not int or type(row.get('position')) is not int:
                raise ValueError('Main Suwayomi returned an incomplete queue identity')
        return {**value,'revision':revision(value)}

    async def queue_action(self,action,expected,chapter_id=None,to=None):
        self.require_client()
        async with self.lock:
            before=await self.queue()
            if before['revision']!=expected: raise ValueError('Queue changed; refresh before applying')
            actions={'start':('startDownloader','StartDownloaderInput',{}),'stop':('stopDownloader','StopDownloaderInput',{})}
            if action in {'remove','move'}:
                if not any((r.get('chapter') or {}).get('id')==chapter_id for r in before['queue']): raise ValueError('Chapter is no longer queued')
                if action=='move':
                    if to is None or not 0<=to<len(before['queue']): raise ValueError('Invalid queue position')
                    actions[action]=('reorderChapterDownload','ReorderChapterDownloadInput',{'chapterId':chapter_id,'to':to})
                else: actions[action]=('dequeueChapterDownload','DequeueChapterDownloadInput',{'id':chapter_id})
            if action not in actions: raise ValueError('Unknown queue action')
            operation,kind,values=actions[action]
            error=None
            try: await self.client.graphql(f'mutation ManagedQueueAction($input:{kind}!){{{operation}(input:$input){{clientMutationId}}}}',{'input':values})
            except Exception as exc: error=exc
            after=await self.queue()
            confirmed=(after['state']==('STARTED' if action=='start' else 'STOPPED')) if action in {'start','stop'} else (
                not any((r.get('chapter') or {}).get('id')==chapter_id for r in after['queue']) if action=='remove' else
                any((r.get('chapter') or {}).get('id')==chapter_id and r.get('position')==to for r in after['queue']))
            if not confirmed: raise ValueError('Queue outcome is uncertain; refresh and review before retrying') from error
            return {**after,'reconciled':error is not None}

    async def enqueue_missing(self,entry_id):
        async with self.lock:
            manga=await self.exact_manga(entry_id)
            if not manga['inLibrary']: raise ValueError('Work is not in the main library')
            chapters=await self.chapters(manga['id']); queue=await self.queue()
            queued={(row.get('chapter') or {}).get('id') for row in queue['queue']}
            ids=[r['id'] for r in chapters if r['isDownloaded'] is False and r['id'] not in queued][:100]
            if not ids: return {'queued':0,'remaining':0}
            try: await self.client.graphql('mutation ManagedDownload($input:EnqueueChapterDownloadsInput!){enqueueChapterDownloads(input:$input){clientMutationId}}',{'input':{'ids':ids}})
            except Exception:
                # The upstream may already have enqueued before its response timed
                # out. Reconcile both finished chapters and the live queue once.
                pass
            after=await self.queue(); chapters_after=await self.chapters(manga['id'])
            present={(r.get('chapter') or {}).get('id') for r in after['queue']}|{r['id'] for r in chapters_after if r['isDownloaded']}
            if not set(ids)<=present: raise ValueError('Download request is unconfirmed; review the queue before retrying')
            return {'queued':len(ids),'remaining':sum(not r['isDownloaded'] and r['id'] not in present for r in chapters_after)}

    async def monitoring_loop(self):
        while not self.stopping:
            await asyncio.sleep(self.interval)
            if self.stopping: break
            # Monitoring is opt-in per work and yields to browser activity. It
            # uses main Suwayomi's downloader; it never downloads in the source host.
            await self.service._wait_for_background_idle()
            config=await run_blocking(self.configuration)
            with self.db.catalog() as c:
                ids=[r[0] for r in c.execute('''SELECT lt.entry_id FROM library_tracking lt JOIN entries e ON e.id=lt.entry_id
                  LEFT JOIN due_work d ON d.kind='library' AND d.work_key=CAST(lt.entry_id AS TEXT)
                  WHERE lt.monitored=1 AND (d.next_due IS NULL OR d.next_due<=? OR d.config_revision<>?)
                  ORDER BY COALESCE(d.next_due,''),COALESCE(d.last_complete,''),lt.entry_id LIMIT 20''',(now_iso(),config))]
            for entry_id in ids:
                if self.stopping: break
                await self.service._wait_for_background_idle()
                task=task_registry.create('Monitor library downloads',kind='library-monitor',context=str(entry_id),total=1)
                runtime=asyncio.create_task(self.monitor_one(entry_id,task))
                task_registry.bind_runtime(task,runtime)
                try:await runtime
                except asyncio.CancelledError:
                    if self.stopping:raise
                await asyncio.sleep(1)

    async def monitor_one(self,entry_id,task):
        try:
            async with self.admin_lock if self.admin_lock is not None else self.local_admission():
                tracking=await run_blocking(self.tracking,entry_id)
                if not tracking['monitored']:
                    task_registry.cancel(task,message='Monitoring disabled before this check started');return
                from .suwayomi import background_source_work
                async with background_source_work():
                    async with self.lock:await self.observe(entry_id,refresh=True)
                await self.enqueue_missing(entry_id)
                task_registry.complete(task)
        except asyncio.CancelledError:
            task_registry.cancel(task);raise
        except Exception as exc:
            try:
                current=await run_blocking(self.tracking,entry_id)
                state='deferred' if getattr(exc,'local_source_deferral',False) else current['observation'].get('download_state','unknown')
                observation={**current['observation'],'download_state':state,'coverage_state':'verification','incomplete_reason':type(exc).__name__}
                await run_blocking(self.save_tracking,entry_id,observation=observation)
            except KeyError:pass
            task_registry.fail(task,exc)

    @asynccontextmanager
    async def local_admission(self):yield

    def start(self,admin_lock=None):
        if admin_lock is not None:self.admin_lock=admin_lock
        if self.client and (self.worker is None or self.worker.done()):
            self.stopping=False; self.worker=asyncio.create_task(self.monitoring_loop())

    async def stop(self):
        self.stopping=True
        if self.worker and not self.worker.done(): self.worker.cancel()
        if self.worker: await asyncio.gather(self.worker,return_exceptions=True)
