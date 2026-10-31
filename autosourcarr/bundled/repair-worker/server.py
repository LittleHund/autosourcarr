"""Authenticated private API; long work is serialized outside the HTTP loop."""
import asyncio
import base64
from concurrent.futures import ThreadPoolExecutor
from contextlib import asynccontextmanager
import json
import os
import secrets
import time
import threading
import uuid
import re

from fastapi import Depends, FastAPI, Header, HTTPException, Request
from fastapi.responses import FileResponse
from fastapi.responses import Response
from control import Control

from contracts import MAX_REQUEST, digest, public_job, status_job, request_payload
from runner import Runner

runner = None
control = None
executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix='source-repair')
pending = set()
provider_check_lock = asyncio.Lock()
provider_checks = set()
# Terminal review/install transitions must read and persist one state at a time.
job_state_lock = threading.Lock()
input_export_lock = threading.Lock()


@asynccontextmanager
async def lifespan(app):
    global runner, control
    root = os.environ.get('REPAIR_ROOT', '')
    if not root.startswith('/') or len(os.environ.get('REPAIR_TOKEN', '')) < 32:
        raise RuntimeError('Configure an absolute REPAIR_ROOT and a private REPAIR_TOKEN with the setup script')
    internal = os.environ.get('REPAIR_RUNTIME') == 'integrated'
    proxy = None
    if internal:
        from internal_runner import InternalRunner, InternalControl
        from egress import handle
        runner = InternalRunner(root)
        proxy = await asyncio.start_unix_server(handle, path=str(runner.sockets/'egress.sock'))
        os.chmod(runner.sockets/'egress.sock', 0o600)
    else:
        runner = Runner('/repairs', root, os.environ.get('REPAIR_JOB_NETWORK', 'autosourcarr-repair-jobs'))
    await asyncio.to_thread(runner.cleanup_interrupted)
    control = InternalControl(runner) if internal else Control(runner)
    try:
        yield
    finally:
        runner.cancelled.update(pending)
        await control.close()
        await asyncio.gather(*provider_checks, return_exceptions=True)
        await asyncio.to_thread(executor.shutdown, wait=True, cancel_futures=False)
        if proxy:
            proxy.close(); await proxy.wait_closed()
            runner.socket_temp.cleanup()


app = FastAPI(lifespan=lifespan, docs_url=None, redoc_url=None)


def authorize(authorization: str = Header(default='')):
    expected = os.environ.get('REPAIR_TOKEN', '')
    if len(expected) < 32 or not secrets.compare_digest(authorization, 'Bearer '+expected):
        raise HTTPException(401, 'Repair worker authentication failed')


def read_job(job_id):
    try:
        return json.loads((runner.path(job_id)/'job.json').read_text())
    except (ValueError, OSError):
        raise HTTPException(404, 'Repair job not found')


@app.get('/status', dependencies=[Depends(authorize)])
def status():
    from ai_provider import snapshot
    captured_at = time.time()
    try: provider = snapshot(runner.root)
    except (ValueError, OSError): provider = {'provider':'unavailable'}
    jobs = []
    for path in (runner.root/'jobs').glob('*/job.json'):
        try: jobs.append(status_job(json.loads(path.read_text())))
        except (ValueError, OSError): continue
    return {'configured': True, 'captured_at': captured_at, 'auth_saved': (runner.root/'auth/auth.json').is_file(), 'ai_provider': provider,
            'active': runner.active, 'pending_jobs': bool(pending),
            'cleanup_uncertain': bool(getattr(runner,'cleanup_failed',False)),
            'jobs': sorted(jobs, key=lambda j:j['created_at'], reverse=True)[:100],
            'account_busy':bool(control and (control.busy or getattr(control,'_retained_lock',None))),
            'login_note': 'Manage tools and sign in from Sources → Repair AI. A saved login does not confirm remaining usage.',
            'web_control': True, 'runtime': os.environ.get('REPAIR_RUNTIME','external'),
            'isolation': getattr(runner,'isolation',None),
            'repository': 'https://github.com/keiyoushi/extensions-source', 'version': '0.4.5'}


@app.middleware('http')
async def private_responses(request, call_next):
    response = await call_next(request)
    response.headers['Cache-Control'] = 'no-store'
    return response


@app.get('/control', dependencies=[Depends(authorize)])
async def control_state():
    return {**control.snapshot(), 'repair_busy': bool(pending)}


@app.get('/provider', dependencies=[Depends(authorize)])
def provider_state():
    from ai_provider import snapshot
    try: return snapshot(runner.root)
    except (ValueError, OSError): raise HTTPException(409, 'Review the repair AI connection in setup, then restart AutoSourcarr.')


@app.post('/provider/test', dependencies=[Depends(authorize)])
async def provider_test():
    from ai_provider import check, RelayCleanupUncertain
    if getattr(runner, 'cleanup_failed', False): raise HTTPException(409, 'Restart AutoSourcarr to clear uncertain repair cleanup.')
    if provider_check_lock.locked(): raise HTTPException(409, 'An AI connection check is already running.')
    async with provider_check_lock:
        task = asyncio.create_task(asyncio.to_thread(check, runner.root))
        provider_checks.add(task)
        cancelled = False
        try:
            while not task.done():
                try: await asyncio.shield(task)
                except asyncio.CancelledError: cancelled = True
            result = await task
            if cancelled: raise asyncio.CancelledError
            return result
        except RelayCleanupUncertain:
            runner.cleanup_failed=True
            raise HTTPException(409, 'AI connection cleanup could not be confirmed. Restart AutoSourcarr.')
        finally: provider_checks.discard(task)


@app.post('/control/{action}', dependencies=[Depends(authorize)], status_code=202)
async def control_action(action: str, request: Request):
    raw=await request.body()
    if len(raw)>512: raise HTTPException(413,'Account request is too large')
    try:
        body=json.loads(raw) if raw else {}
        if body is None: body={}
        if not isinstance(body,dict): raise ValueError()
        operation_id=str(uuid.UUID(body['operation_id'])) if body.get('operation_id') else None
    except (ValueError,TypeError,AttributeError): raise HTTPException(400,'Invalid account operation identifier')
    if action == 'cancel-login':
        return await control.cancel_login(operation_id)
    if pending:
        raise HTTPException(409, 'A source repair is queued or running. Finish or cancel it before changing the shared worker account or tools.')
    try:
        return control.start(action,operation_id)
    except ValueError as exc:
        raise HTTPException(400, str(exc))
    except RuntimeError as exc:
        raise HTTPException(409, str(exc))


@app.post('/jobs', dependencies=[Depends(authorize)], status_code=202)
async def create_job(request: Request):
    raw = bytearray()
    async for chunk in request.stream():
        raw.extend(chunk)
        if len(raw) > MAX_REQUEST:
            raise HTTPException(413, 'Repair request is too large')
    try:
        body = json.loads(raw)
        values, data, source_apk = request_payload(body)
        request_id=body.get('request_id')
        if request_id is not None and (not isinstance(request_id,str) or not re.fullmatch(r'[0-9a-f]{32}',request_id)):
            raise ValueError('Invalid repair request identifier')
        raw_code=decode_raw(body,values) if values.get('raw_first') else b''
    except (KeyError, TypeError, ValueError) as exc:
        raise HTTPException(400, str(exc))
    if request_id and (runner.path(request_id)/'job.json').is_file():
        existing=read_job(request_id)
        if existing.get('request_sha256')!=digest(bytes(raw)):raise HTTPException(409,'This request identifier already belongs to different repair inputs')
        return public_job(existing)
    # The request stream yielded; another admission may have won in the meantime.
    if pending or (control and (control.busy or getattr(control, '_retained_lock', None))):
        raise HTTPException(409, 'A repair is already running')
    isolation = getattr(runner, 'isolation', None)
    if isinstance(isolation, dict) and isolation.get('available') is False and isolation.get('reason') not in (None, 'not-checked'):
        raise HTTPException(409, isolation.get('message') or 'Repair isolation is unavailable. Check repair tools first.')
    job = {**values, 'id': request_id or uuid.uuid4().hex, 'created_at': time.time(), 'baseline_sha256': digest(data), 'version': '0.4.5',
           'request_sha256':digest(bytes(raw))}
    directory = runner.path(job['id'])
    directory.mkdir(parents=True, mode=0o700)
    (directory/'baseline.jar').write_bytes(data)
    if source_apk:
        (directory/'source.apk').write_bytes(source_apk)
    if raw_code:
        (directory/'raw.zip').write_bytes(raw_code);job['raw_sha256']=digest(raw_code)
    if job.get('raw_first') and not raw_code and not job.get('reconstruct'):
        runner.save(job,'awaiting-raws','Provide the original source project in Tasks → Repairs, or choose Unavailable to reconstruct the saved installed source. No AI/build slot is occupied.')
        return public_job(job)
    runner.save(job, 'queued', 'Pinned original orphan recovery queued; no live source is installed yet.' if values.get('recovery') else 'Installed binary preserved; waiting for the private worker.')
    enqueue(job)
    return public_job(job)


def decode_raw(body,values):
    encoded=body.get('raw_code')
    if encoded is None:return b''
    if not isinstance(encoded,str) or len(encoded)>32*1024*1024*4//3+4 or 'raw_meta' not in values:raise ValueError('Invalid raw-code input')
    payload=base64.b64decode(encoded,validate=True)
    if not 0<len(payload)<=32*1024*1024:raise ValueError('Raw source ZIP exceeds32MiB')
    # Validate at admission, before a model or compiler receives these paths.
    from source_tools.archive import _raw_files
    _raw_files(payload,values['raw_meta'])
    return payload


def enqueue(job):
    pending.add(job['id'])
    future = asyncio.get_running_loop().run_in_executor(executor, runner.run, job)
    future.add_done_callback(lambda _: pending.discard(job['id']))


@app.post('/jobs/{job_id}/raws',dependencies=[Depends(authorize)],status_code=202)
async def provide_raws(job_id:str,request:Request):
    raw=bytearray()
    async for chunk in request.stream():
        raw.extend(chunk)
        if len(raw)>MAX_REQUEST:raise HTTPException(413,'Raw-code request exceeds its limit')
    with job_state_lock:
        job=read_job(job_id)
        if job['state']!='awaiting-raws':raise HTTPException(409,'This repair is no longer waiting for code; refresh Tasks')
        if pending or control and (control.busy or getattr(control,'_retained_lock',None)):raise HTTPException(409,'Another repair/account operation is running')
        try:
            body=json.loads(raw)
            if not isinstance(body,dict):raise ValueError('Invalid raw-code choice')
            if body.get('expected_updated_at')!=job['updated_at']:raise ValueError('The repair changed; refresh before providing code')
            if body.get('reconstruct') is True:
                job['reconstruct']=True
            else:
                from source_tools.archive import metadata
                info=metadata(body.get('raw_meta'))
                if info['package']!=job['pkg_name']:raise ValueError('Raw-code package differs from the repair')
                payload=decode_raw(body,{'raw_meta':info})
                if not payload:raise ValueError('Provide the original project ZIP or choose Unavailable')
                (runner.path(job_id)/'raw.zip').write_bytes(payload)
                job.update(raw_meta=info,module=info['module'],raw_sha256=digest(payload))
        except (ValueError,TypeError) as exc:raise HTTPException(400,str(exc)) from None
        runner.save(job,'queued','Raw-code choice accepted; waiting for the private repair runner.')
        enqueue(job)
        return public_job(job)


@app.post('/jobs/{job_id}/cancel', dependencies=[Depends(authorize)], status_code=202)
def cancel(job_id: str):
    with job_state_lock:
        value=read_job(job_id)
        if value['state']=='awaiting-raws':
            runner.save(value,'cancelled','Raw-code request cancelled; saved code and binary retained.')
            return {'requested':True}
        if job_id in pending:runner.cancelled.add(job_id)
        return {'requested':job_id in pending}


@app.post('/jobs/{job_id}/discard', dependencies=[Depends(authorize)])
def discard(job_id: str):
    with job_state_lock:
        value = read_job(job_id)
        if value['state'] == 'discarded':
            return public_job(value)  # A retry does not duplicate review history.
        if value['state'] != 'ready' or job_id in pending or runner.active == job_id:
            raise HTTPException(409, 'Only an idle ready candidate can be discarded. Cancel a running repair instead.')
        runner.save(value, 'discarded', 'Validated repair discarded by review. Candidate files, conversation and test evidence retained; start a new repair to reconsider.',
                    discarded_at=time.time())
        return public_job(value)


@app.post('/jobs/{job_id}/installed', dependencies=[Depends(authorize)])
def installed(job_id: str):
    with job_state_lock:
        value = read_job(job_id)
        if value['state'] not in {'ready', 'installed'}:
            raise HTTPException(409, 'Only a validated candidate can be marked installed')
        runner.save(value, 'installed', 'Application confirmed installation; backups and test evidence retained.')
        return public_job(value)


@app.get('/jobs/{job_id}', dependencies=[Depends(authorize)])
def job(job_id: str):
    value = public_job(read_job(job_id))
    path = runner.path(job_id)/'conversation.json'
    value['conversation'] = json.loads(path.read_text()) if path.is_file() else []
    patch = runner.path(job_id)/'diff.txt'
    value['patch'] = patch.read_text() if patch.is_file() else ''
    return value


@app.get('/jobs/{job_id}/artifact', dependencies=[Depends(authorize)])
def artifact(job_id: str):
    value = read_job(job_id)
    path = runner.path(job_id)/'candidate.jar'
    if value['state'] not in {'ready', 'discarded','installed'} or not path.is_file() or digest(path.read_bytes()) != value.get('candidate_sha256'):
        raise HTTPException(409, 'No validated candidate is available')
    return FileResponse(path, filename='candidate.jar', media_type='application/java-archive')


@app.get('/jobs/{job_id}/raw-code',dependencies=[Depends(authorize)])
def raw_code(job_id:str,candidate:bool=False):
    value=read_job(job_id);name='candidate-raw.zip' if candidate else 'raw.zip'
    path=runner.path(job_id)/name;key='candidate_raw_sha256' if candidate else 'raw_sha256'
    if path.is_symlink() or not path.is_file() or not value.get(key) or digest(path.read_bytes())!=value[key]:
        raise HTTPException(409,'No retained raw-code output is available')
    return FileResponse(path,filename=name,media_type='application/zip')


@app.get('/jobs/{job_id}/inputs', dependencies=[Depends(authorize)])
def inputs(job_id: str):
    from repair_inputs import input_archive
    value=read_job(job_id)
    if not input_export_lock.acquire(blocking=False):raise HTTPException(429,'A comparison download is already being prepared')
    try:
        try:data=input_archive(runner.path(job_id),value)
        except (ValueError,OSError):raise HTTPException(409,'Saved source comparison inputs are unavailable, changed or exceed the export limit') from None
        return Response(data,media_type='application/zip',headers={'Cache-Control':'no-store','X-Content-Type-Options':'nosniff'})
    finally:input_export_lock.release()
