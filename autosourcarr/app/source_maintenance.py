"""Per-instance source repairs; live promotion is owned by this application."""
import asyncio
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import tempfile
import time
from urllib.parse import urlsplit

from fastapi import APIRouter, Depends, HTTPException, Request
import httpx

from .async_utils import run_blocking
from .config import settings
from .diagnostics import scrub
from .support_logs import support_logs
from .repair_baseline import save_original_apk
from .repair_tasks import repair_tasks, isolation_blocker

INVENTORY = '''query { extensions { nodes { pkgName name lang apkName apkUrl isInstalled isObsolete hasUpdate
versionName storeIndexUrl source { nodes { id name lang baseUrl } } } } }'''
MAX_PACKAGE = 32*1024*1024
LOCAL_REPAIR_STATUS_TIMEOUT = 5
LOCAL_REPAIR_READINESS_RETRY = 60


def loopback_repair_helper():
    # Only the instance-owned helper can be checked before source admission.
    # Keep the existing pre-I/O daily reservation for remote compatibility URLs.
    try: address = urlsplit(settings.repair_worker_url.strip())
    except ValueError: return False
    return (settings.app_services_mode == 'integrated'
            and address.scheme in {'http', 'https'}
            and address.hostname in {'127.0.0.1', '::1', 'localhost'}
            and address.path in {'', '/'} and not address.query and not address.fragment
            and not address.username and not address.password)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def verify_repaired_addresses(extension, migrations):
    """Require the restarted live source to expose the reviewed candidate address."""
    if not migrations:
        return
    if not isinstance(migrations, list) or len(migrations) > 200:
        raise ValueError('Repair address evidence is invalid')
    def address(value):
        if not isinstance(value, str): raise ValueError('Repaired source website address is missing')
        parsed = urlsplit(value)
        if (parsed.scheme != 'https' or not parsed.hostname or parsed.username or parsed.password
                or parsed.query or parsed.fragment):
            raise ValueError('Repaired source website address is invalid')
        return parsed.hostname.lower().encode('idna').decode('ascii').rstrip('.'), parsed.port or 443, parsed.path.rstrip('/')
    current = {str(row['id']): row.get('baseUrl') for row in extension.get('source', {}).get('nodes', [])}
    for row in migrations:
        if not isinstance(row, dict) or address(current.get(str(row.get('source_id')))) != address(row.get('new_url')):
            raise ValueError('Live source website address does not match the reviewed repair; installation was not accepted')


def atomic(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(dir=path.parent, prefix='.repair-')
    try:
        if path.exists():
            existing = path.stat()
            os.fchmod(fd, stat.S_IMODE(existing.st_mode))
            os.fchown(fd, existing.st_uid, existing.st_gid)
        with os.fdopen(fd, 'wb') as stream:
            stream.write(data)
            stream.flush(); os.fsync(stream.fileno())
        os.replace(temporary, path)
        directory = os.open(path.parent, os.O_RDONLY)
        try: os.fsync(directory)
        finally: os.close(directory)
    finally:
        Path(temporary).unlink(missing_ok=True)


def storage():
    return settings.data_dir/'source-maintenance'


def installed_path(extension):
    from .source_engine import ENGINE_DATA
    filename = str(extension.get('apkName') or '')
    if not re.fullmatch(r'[A-Za-z0-9_.+\-]+\.(?:apk|jar)', filename):
        raise ValueError('Installed extension filename is unavailable or invalid; no files were changed')
    root = ENGINE_DATA/'extensions'
    path = root/(filename[:-4]+'.jar')
    if root.is_symlink() or path.is_symlink() or not path.is_file():
        raise ValueError('Installed JAR is missing or linked; check the retained source data mount')
    if path.stat().st_size > MAX_PACKAGE:
        raise ValueError('Installed extension exceeds the 32 MiB repair limit')
    return path


def save_backup(extension):
    path = installed_path(extension)
    data = path.read_bytes()
    directory = storage()/'backups'/sha(data)
    directory.mkdir(parents=True, exist_ok=True)
    atomic(directory/'installed.jar', data)
    atomic(directory/'identity.json', json.dumps(extension).encode())
    return data


async def worker(method, path, *, payload=None, binary=False, credentials=None):
    url, token = credentials or (settings.repair_worker_url.strip().rstrip('/'), settings.repair_worker_token.strip())
    if not url or not token:
        raise HTTPException(503, 'Repair worker is not configured. Open Sources → Repair AI → AI setup and account.')
    try:
        async with httpx.AsyncClient(timeout=httpx.Timeout(40 if path == '/provider/test' else 20, connect=3), trust_env=False) as client:
            async with client.stream(method, url+path, json=payload, headers={'Authorization': 'Bearer '+token}) as response:
                data = bytearray()
                async for chunk in response.aiter_bytes():
                    data.extend(chunk)
                    if len(data) > MAX_PACKAGE:
                        raise ValueError('Repair worker response exceeds the size limit')
                if not response.is_success:
                    support_logs.record('repair-helper',method+' '+path+' returned HTTP '+str(response.status_code))
                    try: detail = json.loads(data).get('detail', 'Repair worker request failed')
                    except ValueError: detail = 'Repair worker returned HTTP '+str(response.status_code)
                    raise HTTPException(response.status_code, scrub(str(detail)))
                if binary:
                    return bytes(data)
                result = json.loads(data)
                if path == '/control' or path.startswith('/control/'):
                    support_logs.observe_control(result)
                    repair_tasks.observe(result)
                elif path == '/status' or path == '/jobs' or path.startswith('/jobs/'):
                    support_logs.observe_repair(result)
                    repair_tasks.observe(result)
                return result
    except HTTPException:
        raise
    except (httpx.HTTPError, ValueError) as exc:
        if path == '/status': repair_tasks.unavailable()
        support_logs.record('repair-helper',method+' '+path+' failed: '+type(exc).__name__)
        raise HTTPException(503, 'Repair worker is unavailable: '+str(scrub(str(exc)))) from exc


async def ensure_repair_ready():
    state=await worker('GET','/status')
    if not isinstance(state,dict):raise HTTPException(503,'Repair process returned an invalid status; no repair was queued')
    message = isolation_blocker(state)
    if message:
        raise HTTPException(409, message + ' Open Tasks → Repairs → Check repair tools to check readiness and host policies. No repair was queued.')
    jobs=state.get('jobs') if isinstance(state,dict) else None
    if state.get('active') or state.get('account_busy') or isinstance(jobs,list) and any(isinstance(row,dict) and row.get('state')=='queued' for row in jobs):
        raise HTTPException(409,'A repair or account operation is already running. Waiting source code is retained; execution will resume when the worker is free.')


async def inventory():
    from .suwayomi import source_host
    return (await source_host.graphql(INVENTORY))['extensions']['nodes']


def identities(extension):
    return sorted(str(row['id']) for row in extension.get('source', {}).get('nodes', []))


class SourceMaintenance:
    def __init__(self):
        self.task = None
        self.operation = {'state': 'idle'}
        self.watcher = None
        self.watch_state = {'enabled': False, 'next_check': 0, 'attempts': {}}

    def read_watch(self):
        path = settings.settings_config_dir/'source-maintenance.json'
        self.watch_state = json.loads(path.read_text()) if path.is_file() else {'enabled': False, 'next_check': 0, 'attempts': {}}

    def save_watch(self):
        atomic(settings.settings_config_dir/'source-maintenance.json', json.dumps(self.watch_state).encode())

    def start(self):
        self.watcher = asyncio.create_task(self.watch())

    async def watch_once(self):
        from .source_engine import source_engine
        from .suwayomi import source_host
        if not self.watch_state.get('enabled') or not settings.repair_worker_url or settings.source_engine_mode != 'integrated': return
        now = time.time()
        if source_engine.state != 'ready' or now < self.watch_state.get('next_check', 0): return
        state = None
        if loopback_repair_helper():
            if now < self.watch_state.get('local_retry_after', 0): return
            # Read-only loopback readiness has its own durable, short retry gate.
            # No catalogue/source request or repair POST is issued in this phase.
            self.watch_state.update(local_retry_after=now+LOCAL_REPAIR_READINESS_RETRY,
                                    message='Checking local repair process readiness')
            await run_blocking(self.save_watch)
            try:
                state = await asyncio.wait_for(worker('GET', '/status'), LOCAL_REPAIR_STATUS_TIMEOUT)
                jobs = state.get('jobs') if isinstance(state, dict) else None
                if (not isinstance(jobs, list) or 'active' not in state
                        or state['active'] is not None and not isinstance(state['active'], str)
                        or any(not isinstance(job, dict) or not isinstance(job.get('id'), str)
                               or not isinstance(job.get('state'), str) for job in jobs)):
                    raise ValueError('Local repair process returned an incomplete status')
            except (HTTPException, TimeoutError, ValueError):
                self.watch_state['message'] = 'Local repair process is not ready; retrying readiness shortly. No source check or repair request was issued.'
                await run_blocking(self.save_watch)
                return
            blocked = isolation_blocker(state)
            if blocked:
                self.watch_state['message'] = blocked + ' Check repair tools in Tasks; no source check or repair was issued.'
                await run_blocking(self.save_watch)
                return
        # Persist the full window before any possibly issued upstream/source I/O.
        # Remote helpers retain their original reservation-before-status ordering.
        self.watch_state.update(next_check=time.time()+86400, message='Checking maintained-source availability')
        self.watch_state.pop('local_retry_after', None)
        await run_blocking(self.save_watch)
        if state is None: state = await worker('GET', '/status')
        if state.get('active') or any(j['state'] in {'queued', 'ready'} and not (self.operation.get('state') == 'installed' and self.operation.get('id') == j['id']) for j in state.get('jobs', [])):
            self.watch_state['message'] = 'A repair is active or awaiting review; install or discard a ready candidate. Daily check deferred'
            await run_blocking(self.save_watch)
            return
        catalog = await asyncio.wait_for(source_host.extensions(refresh=True), 45)
        candidates = [e for e in catalog['extensions'] if e.get('isInstalled') and e.get('isObsolete')]
        for item in candidates:
            pkg = item['pkgName']
            attempts = self.watch_state.setdefault('attempts', {})
            if time.time()-attempts.get(pkg, 0) < 7*86400: continue
            extension = next((e for e in await inventory() if e['pkgName'] == pkg and e.get('isInstalled')), None)
            if not extension: continue
            attempts[pkg] = time.time()
            self.watch_state['attempts'] = dict(sorted(attempts.items(), key=lambda x:x[1], reverse=True)[:1000])
            await run_blocking(self.save_watch)
            from .repair_workflow import repair_workflow
            await repair_workflow.create(extension,{'pkg_name':pkg,
                'origin': 'automatic',
                'issue': 'The installed package is reported missing from the source catalog. Treat this only as a hint, not proof of retirement. Check its original ecosystem code and website/API addresses; retain source identity and verify functionality.'})
            self.watch_state['message'] = 'Queued one isolated availability/repair check for '+pkg
            await run_blocking(self.save_watch)
            return
        self.watch_state['message'] = 'No unreviewed missing-source candidates need a repair check'
        await run_blocking(self.save_watch)

    async def watch(self):
        await run_blocking(self.read_watch)
        while True:
            await asyncio.sleep(30)
            try: await self.watch_once()
            except asyncio.CancelledError: raise
            except Exception as exc:
                self.watch_state['message'] = 'Daily source check deferred: '+str(scrub(str(exc)))
                await run_blocking(self.save_watch)

    def recover(self):
        """Restore worker connection and interrupted file promotion before startup."""
        from .repair_connection import restore_connection
        restore_connection()
        path = storage()/'promotion.json'
        if not path.is_file():
            return
        record = json.loads(path.read_text())
        if record.get('state')=='reverting' and record.get('history_plan'):
            from .repair_workflow import repair_workflow
            plan=record['history_plan'];key=plan['target']['binary_sha256']
            if not re.fullmatch(r'[0-9a-f]{64}',key):raise ValueError('Invalid source revert fingerprint')
            target=installed_path(record['extension']);observed=sha(target.read_bytes())
            if observed not in {record['from_sha256'],key}:raise ValueError('Source changed after Revert admission; startup refused to overwrite it')
            data=(storage()/'backups'/key/'installed.jar').read_bytes()
            if sha(data)!=key:raise ValueError('Revert backup failed its integrity check')
            atomic(target,data)
            repair_workflow.history.commit(plan,observed_sha256=key)
            record.update(state='reverted',message='Prior source binary/code and confirmation restored.',changed_at=time.time())
            atomic(path,json.dumps(record).encode());self.operation=record
            return
        if record.get('state') != 'installing':
            self.operation = record
            return
        target = installed_path(record['extension'])
        key = str(record['baseline_sha256'])
        if not re.fullmatch('[0-9a-f]{64}', key):
            raise ValueError('Invalid repair rollback fingerprint')
        data = (storage()/'backups'/key/'installed.jar').read_bytes()
        if sha(data) != key:
            raise ValueError('Repair rollback backup failed its integrity check')
        # Never clobber an independently changed package after a crash.
        if sha(target.read_bytes()) not in {key, record['candidate_sha256']}:
            raise ValueError('Source package changed after interrupted repair; inspect its backup before starting')
        atomic(target, data)
        if record.get('history_plan'):
            from .repair_workflow import repair_workflow
            repair_workflow.history.restore_before(record['history_plan'],observed_sha256=key)
        record.update(state='rolled-back', changed_at=time.time(), message='Interrupted repair restored the previous installed binary.')
        atomic(path, json.dumps(record).encode())
        self.operation = record

    async def ready(self):
        from .source_engine import source_engine
        for _ in range(90):
            if source_engine.state == 'ready':
                return
            if source_engine.state == 'setup-required':
                raise RuntimeError(source_engine.error)
            await asyncio.sleep(1)
        raise RuntimeError('Source engine did not become ready after the package change')

    async def install(self, job_id, lock, *, reviewed_candidate_sha256=None):
        from .source_engine import source_engine
        from .source_browser import source_browser
        from .source_webview import sessions
        from .service import service
        from .suwayomi import SuwayomiClient
        record, target, baseline, changed = None, None, None, False
        restart_safe = True
        async with lock:
            try:
                job = await worker('GET', '/jobs/'+job_id)
                if job.get('state') != 'ready' or not job.get('checks') or not job.get('expected_ids'):
                    raise ValueError('The candidate has not passed functional validation')
                if job.get('address_migrations') and (not isinstance(reviewed_candidate_sha256,str) or not re.fullmatch(r'[0-9a-f]{64}',reviewed_candidate_sha256) or reviewed_candidate_sha256!=job.get('candidate_sha256')):
                    raise ValueError('Review this exact candidate website-address change before installation')
                if time.time()-float(job.get('validation_time', 0)) > 86400:
                    raise ValueError('Validation is older than 24 hours; run a fresh repair check')
                artifact = await worker('GET', '/jobs/'+job_id+'/artifact', binary=True)
                if sha(artifact) != job.get('candidate_sha256'):
                    raise ValueError('Candidate checksum changed after validation')
                if job.get('recovery'):
                    from .source_orphans import source_orphans
                    retained=None
                    if job.get('candidate_raw_meta'):
                        from .source_raws import source_raws
                        code=await worker('GET','/jobs/'+job_id+'/raw-code?candidate=true',binary=True)
                        if sha(code)!=job.get('candidate_raw_sha256'):raise ValueError('Recovered source code export changed')
                        retained=await run_blocking(source_raws.catalog.retain,job['pkg_name'],code,job['candidate_raw_meta'])
                    extension = await source_orphans.install_recovered(job, artifact)
                    if retained:
                        try:
                            actual=sha(await run_blocking(installed_path(extension).read_bytes))
                            await run_blocking(source_raws.catalog.adopt,extension,actual,retained['id'])
                        except (ValueError,OSError) as exc:
                            support_logs.record('source-raws','Recovered source is installed and code retained; selecting its archive reference failed: '+type(exc).__name__)
                    self.operation = {'state': 'recovered', 'id': job_id, 'extension': extension, 'installed_at':time.time(),
                                      'message': 'Original orphan recovered and installed after functional checks. No previous installation exists to roll back; use Uninstall if needed.'}
                    # Supersede any older unrelated repair receipt. A first-time
                    # recovered install must not expose rollback of another source.
                    await run_blocking(atomic, storage()/'promotion.json', json.dumps(self.operation).encode())
                    try: await worker('POST', '/jobs/'+job_id+'/installed')
                    except Exception: pass
                    return
                extension = next((x for x in await inventory() if x['pkgName'] == job['pkg_name'] and x.get('isInstalled')), None)
                if not extension or identities(extension) != sorted(job['expected_ids']):
                    raise ValueError('Installed source identity changed; run a fresh repair check')
                target = installed_path(extension)
                baseline = await run_blocking(save_backup, extension)
                if sha(baseline) != job.get('baseline_sha256'):
                    raise ValueError('Installed source was updated after this repair; run a fresh check')
                history_plan=None
                if job.get('raw_first'):
                    from .repair_workflow import repair_workflow
                    history_plan=await repair_workflow.install_plan(job,extension,artifact)
                record = {'id': job_id, 'state': 'installing', 'extension': extension,
                          'baseline_sha256': sha(baseline), 'candidate_sha256': sha(artifact),
                          'message': 'Installing validated repair; previous binary retained.'}
                if history_plan:record['history_plan']=history_plan
                if job.get('raw_first'):record['raw_first']=True
                await run_blocking(atomic, storage()/'promotion.json', json.dumps(record).encode())
                self.operation = record
                # Drain the source process, reject new source requests and invalidate
                # WebView sessions. Do not alter the source DB or installed identity.
                source_browser.busy = True
                sessions.sessions.clear()
                await source_browser._stop_engine(source_engine)
                await source_browser._assert_runtime_absent()
                # Compare again after draining: an admitted update may have finished.
                if sha(await run_blocking(target.read_bytes)) != sha(baseline):
                    raise ValueError('Source changed during maintenance admission; repair was not installed')
                changed = True
                await run_blocking(atomic, target, artifact)
                source_engine.start()
                await self.ready()
                client = SuwayomiClient(settings.effective_source_url)  # owned maintenance client
                try:
                    current = (await client.graphql(INVENTORY))['extensions']['nodes']
                    current = next(x for x in current if x['pkgName'] == job['pkg_name'] and x.get('isInstalled'))
                    if identities(current) != identities(extension):
                        raise ValueError('Live source identity does not match the validated candidate')
                    verify_repaired_addresses(current, job.get('address_migrations'))
                finally:
                    await client.close()
                if history_plan:
                    await run_blocking(repair_workflow.history.commit,history_plan,observed_sha256=sha(await run_blocking(target.read_bytes)))
                    from .source_raws import source_raws
                    await run_blocking(source_raws.catalog.adopt,extension,job['candidate_sha256'],history_plan['target']['raw_id'])
                record.update(state='installed', installed_at=time.time(), message='Validated repair installed. Package version retained; candidate hash identifies the local repair.')
                await run_blocking(atomic, storage()/'promotion.json', json.dumps(record).encode())
                self.operation = record
                from .source_orphans import source_orphans
                await source_orphans.record_repair(extension, job_id)
                try: await worker('POST', '/jobs/'+job_id+'/installed')
                except Exception: pass  # Installed journal remains authoritative after a lost acknowledgement.
            except BaseException as exc:
                if changed:
                    try:
                        await source_browser._stop_engine(source_engine)
                        await source_browser._assert_runtime_absent()
                        await run_blocking(atomic, target, baseline)
                        if record and record.get('history_plan'):
                            from .repair_workflow import repair_workflow
                            await run_blocking(repair_workflow.history.restore_before,record['history_plan'],observed_sha256=sha(baseline))
                        source_engine.start()
                    except BaseException:
                        restart_safe = False
                        source_engine.state = 'setup-required'
                        source_engine.error = 'Repair rollback needs attention. Retained backup and promotion journal must be recovered before source startup.'
                        self.operation = {'state': 'recovery-needed', 'message': source_engine.error}
                        raise
                if record:
                    record.update(state='rolled-back' if changed else 'failed', message=str(scrub(str(exc) or type(exc).__name__)))
                    await run_blocking(atomic, storage()/'promotion.json', json.dumps(record).encode())
                    self.operation = record
                else:
                    self.operation = {'state': 'failed', 'id':job_id,'message': str(scrub(str(exc)))}
                if isinstance(exc, asyncio.CancelledError):
                    raise
            finally:
                source_browser.busy = False
                if restart_safe:
                    if source_engine.state == 'stopped': source_engine.start()
                    service.schedule_source_reconciliation(delay=1)

    async def stop(self):
        if self.watcher:
            self.watcher.cancel()
            await asyncio.gather(self.watcher, return_exceptions=True)
        # Complete bounded promotion/rollback before the final engine shutdown.
        if self.task and not self.task.done():
            await asyncio.shield(self.task)

    async def rollback(self, lock):
        from .source_engine import source_engine
        from .source_browser import source_browser
        from .source_webview import sessions
        from .service import service
        async with lock:
            try:
                path = storage()/'promotion.json'
                record = json.loads(await run_blocking(path.read_text))
                if record.get('history_plan'):raise ValueError('Use this installed repair’s Revert button in Tasks → Repairs')
                if record.get('state') != 'installed':
                    raise ValueError('There is no installed repair to roll back')
                target = installed_path(record['extension'])
                if sha(await run_blocking(target.read_bytes)) != record['candidate_sha256']:
                    raise ValueError('Package changed since this repair; automatic rollback refused')
                record['state'] = 'installing'  # crash recovery has the same rollback contract
                await run_blocking(atomic, path, json.dumps(record).encode())
                source_browser.busy = True
                sessions.sessions.clear()
                await source_browser._stop_engine(source_engine)
                await source_browser._assert_runtime_absent()
                await run_blocking(self.recover)
                from .source_orphans import source_orphans
                await source_orphans.record_repair(record['extension'], None)
                source_engine.start()
                await self.ready()
                service.schedule_source_reconciliation(delay=1)
            except Exception as exc:
                self.operation = {'state': 'rollback-failed', 'message': str(scrub(str(exc)))}
                if source_engine.state == 'stopped':
                    source_engine.state = 'setup-required'
                    source_engine.error = 'Source rollback needs attention; retained package/journal recovery must complete before source startup.'
            finally:
                source_browser.busy = False

    async def revert(self,job_id,lock,*,expected_revision,expected_sha256):
        from .repair_workflow import repair_workflow
        from .source_engine import source_engine
        from .source_browser import source_browser
        from .source_webview import sessions
        from .service import service
        from .source_raws import source_raws
        async with lock:
            record=None
            try:
                admission=repair_workflow.admissions.get(job_id)
                if not admission:raise ValueError('This repair has no per-source history')
                extension=next((row for row in await inventory() if row['pkgName']==admission['package'] and row.get('isInstalled')),None)
                if not extension:raise ValueError('The installed source is missing')
                observed=sha(await run_blocking(installed_path(extension).read_bytes))
                if observed!=expected_sha256:raise ValueError('Installed source changed; refresh before Revert')
                plan=await run_blocking(repair_workflow.history.plan_revert,admission['package'],job_id,
                    expected_revision=expected_revision,observed_sha256=observed)
                target=plan['target']
                if not target:raise ValueError('This is a first installation without a previous binary; use its source Uninstall action')
                if target.get('raw_id'):await run_blocking(source_raws.catalog.resolve,admission['package'],target['raw_id'])
                record={'id':job_id,'state':'reverting','extension':extension,'history_plan':plan,'from_sha256':observed,
                        'message':'Restoring this source’s prior binary/code and working confirmation.'}
                await run_blocking(atomic,storage()/'promotion.json',json.dumps(record).encode())
                self.operation=record;source_browser.busy=True;sessions.sessions.clear()
                await source_browser._stop_engine(source_engine);await source_browser._assert_runtime_absent()
                await run_blocking(self.recover)
                source_engine.start();await self.ready()
                from .source_orphans import source_orphans
                await source_orphans.record_repair(extension,target.get('job_id'))
                service.schedule_source_reconciliation(delay=1)
            except Exception as exc:
                self.operation={'id':job_id,'state':'rollback-failed','message':str(scrub(str(exc)))}
                if source_engine.state=='stopped':
                    source_engine.state='setup-required';source_engine.error='Revert needs retained journal recovery before source startup.'
            finally:source_browser.busy=False


source_maintenance = SourceMaintenance()


def maintenance_router(admin_guard, lock):
    router = APIRouter(prefix='/api/admin/source-maintenance', dependencies=[Depends(admin_guard)])
    from .repair_connection import connection_router
    router.include_router(connection_router(worker, lock))
    from .repair_workflow import workflow_router
    router.include_router(workflow_router(admin_guard,lock))

    @router.get('')
    async def snapshot():
        from .internal_services import internal_services
        from .repair_workflow import repair_workflow
        from .source_raws import source_raws
        local = {'mode': settings.app_services_mode,
                 'storage_error':repair_workflow.error or source_raws.error,
                 'service': internal_services.snapshot()['services']['repair'],
                 'operation': {k:v for k,v in source_maintenance.operation.items() if k not in {'extension','history_plan'}},
                 'watch': source_maintenance.watch_state}
        if not settings.repair_worker_url:
            return {'configured': False, 'reachable': False, 'jobs': [], **local}
        try:
            result = await worker('GET', '/status')
        except HTTPException as exc:
            return {'configured': True, 'reachable': False, 'message': exc.detail, 'jobs': [], **local}
        return {**result, 'reachable': True, **local}

    @router.put('/watch')
    async def watch(request: Request):
        body = await request.json()
        if not isinstance(body, dict) or type(body.get('enabled')) is not bool: raise HTTPException(400, 'Expected enabled boolean')
        if body['enabled'] and not settings.repair_worker_url: raise HTTPException(409, 'Configure the worker first')
        source_maintenance.watch_state.update(enabled=body['enabled'], next_check=0)
        await run_blocking(source_maintenance.save_watch)
        return source_maintenance.watch_state

    @router.get('/installed')
    async def installed():
        return {'items': [e for e in await inventory() if e.get('isInstalled')]}

    @router.post('/rollback', status_code=202)
    async def rollback():
        if settings.source_engine_mode != 'integrated': raise HTTPException(409, 'Integrated engine required')
        if source_maintenance.task and not source_maintenance.task.done(): raise HTTPException(409, 'A source change is running')
        source_maintenance.operation = {'state': 'queued', 'message': 'Restoring previous source package'}
        source_maintenance.task = asyncio.create_task(source_maintenance.rollback(lock))
        return {'queued': True}

    @router.post('/jobs', status_code=202)
    async def create(request: Request):
        if settings.source_engine_mode != 'integrated':
            raise HTTPException(409, 'Source repair requires the integrated engine')
        body = await request.json()
        if not isinstance(body, dict): raise HTTPException(400, 'Expected a repair request')
        await ensure_repair_ready()
        async with lock:
            extension = next((e for e in await inventory() if e['pkgName'] == body.get('pkg_name') and e.get('isInstalled')), None)
            if not extension: raise HTTPException(404, 'Installed source package not found')
            from .repair_workflow import repair_workflow
            try:return await repair_workflow.create(extension,body)
            except ValueError as exc:raise HTTPException(409,str(exc)) from None

    @router.post('/original-apk/{pkg_name}')
    async def original_apk(pkg_name: str, request: Request):
        if settings.source_engine_mode != 'integrated':
            raise HTTPException(409, 'Original APK evidence requires the integrated engine')
        if not re.fullmatch(r'eu\.kanade\.tachiyomi\.extension\.[a-z0-9]+\.[a-z0-9_]+', pkg_name):
            raise HTTPException(400, 'Select the installed original extension package')
        if request.headers.get('X-AutoSourcarr-Original-Apk-Confirmed') != 'true':
            raise HTTPException(400, 'Confirm that this is the original APK for the selected installed version')
        reviewed_version = request.headers.get('X-AutoSourcarr-Original-Apk-Version', '')
        if not re.fullmatch(r'[0-9][A-Za-z0-9.+_-]{0,99}', reviewed_version):
            raise HTTPException(400, 'Refresh installed sources and confirm the displayed version')
        data = bytearray()
        async for chunk in request.stream():
            data.extend(chunk)
            if len(data) > MAX_PACKAGE:
                raise HTTPException(413, 'Original APK exceeds 32 MiB')
        async with lock:
            extension = next((e for e in await inventory() if e.get('pkgName') == pkg_name and e.get('isInstalled')), None)
            if not extension:
                raise HTTPException(404, 'Select an installed source package')
            if extension.get('versionName') != reviewed_version:
                raise HTTPException(409, 'Installed source version changed. Refresh sources and confirm its original APK again.')
            try:
                return await run_blocking(save_original_apk, extension, bytes(data), confirmed=True)
            except ValueError as exc:
                raise HTTPException(400, str(exc)) from None

    @router.get('/jobs/{job_id}')
    async def details(job_id: str):
        if not re.fullmatch('[0-9a-f]{32}', job_id): raise HTTPException(404, 'Invalid repair ID')
        return await worker('GET', '/jobs/'+job_id)

    @router.get('/jobs/{job_id}/inputs')
    async def inputs(job_id: str):
        from fastapi.responses import Response
        if not re.fullmatch('[0-9a-f]{32}',job_id):raise HTTPException(404,'Invalid repair ID')
        data=await worker('GET','/jobs/'+job_id+'/inputs',binary=True)
        return Response(data,media_type='application/zip',headers={
            'Content-Disposition':'attachment; filename="AutoSourcarr-repair-inputs-'+job_id+'.zip"',
            'Cache-Control':'no-store','X-Content-Type-Options':'nosniff'})

    @router.post('/jobs/{job_id}/cancel', status_code=202)
    async def cancel(job_id: str):
        if not re.fullmatch('[0-9a-f]{32}', job_id): raise HTTPException(404, 'Invalid repair ID')
        return await worker('POST', '/jobs/'+job_id+'/cancel')

    @router.post('/jobs/{job_id}/discard')
    async def discard(job_id: str):
        if not re.fullmatch('[0-9a-f]{32}', job_id): raise HTTPException(404, 'Invalid repair ID')
        if source_maintenance.task and not source_maintenance.task.done(): raise HTTPException(409, 'A package change is running; wait before discarding a candidate')
        # The same admission lock owns installation, so a decline cannot race a
        # live package promotion or its acknowledgement to the private worker.
        async with lock:
            if source_maintenance.task and not source_maintenance.task.done(): raise HTTPException(409, 'A package change is running; wait before discarding a candidate')
            if source_maintenance.operation.get('id') == job_id and source_maintenance.operation.get('state') in {'installed', 'recovered'}:
                raise HTTPException(409, 'This repair is already installed; use its recovery actions instead')
            return await worker('POST', '/jobs/'+job_id+'/discard')

    @router.post('/jobs/{job_id}/install', status_code=202)
    async def install(job_id: str, request: Request):
        if settings.source_engine_mode != 'integrated': raise HTTPException(409, 'Integrated engine required')
        if not re.fullmatch('[0-9a-f]{32}', job_id): raise HTTPException(404, 'Invalid repair ID')
        if source_maintenance.task and not source_maintenance.task.done(): raise HTTPException(409, 'Another package change is running')
        async with lock:
            if source_maintenance.task and not source_maintenance.task.done(): raise HTTPException(409, 'Another package change is running')
            job = await worker('GET', '/jobs/'+job_id)
            if job.get('state') != 'ready': raise HTTPException(409, 'Only a ready validated repair can be installed. Start a new repair after discarding a candidate.')
            reviewed=None
            if job.get('address_migrations'):
                try:body=await request.json()
                except ValueError:body={}
                candidate=job.get('candidate_sha256')
                if not isinstance(candidate,str) or not re.fullmatch(r'[0-9a-f]{64}',candidate) or not isinstance(body,dict) or body.get('approve_address_changes') is not True or body.get('reviewed_candidate_sha256')!=candidate:
                    raise HTTPException(409,'Review and approve this candidate website-address change before installation')
                reviewed=body['reviewed_candidate_sha256']
            # install() rechecks persisted state under this lock before loading
            # any artifact. The admitted task blocks further discard requests.
            source_maintenance.operation = {'state': 'queued', 'id': job_id}
            source_maintenance.task = asyncio.create_task(source_maintenance.install(job_id, lock,reviewed_candidate_sha256=reviewed))
            return {'queued': True}

    return router
