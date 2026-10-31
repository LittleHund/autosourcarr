"""Instance-owned admission, raw input and confirmation/revert supervision.

This controller alone can install into the live engine. Worker execution sees
only immutable per-job code/binary snapshots. Request IDs and private admission
receipts reconcile uncertain RPC writes without replaying model work.
"""
import asyncio
import base64
import hashlib
import json
import re
import time
import threading
import uuid

from .async_utils import run_blocking
from .repair_history import RepairHistory, HistoryConflict
from .source_raws import source_raws
from bundled.source_tools.archive import metadata, package_name
from bundled.source_tools.ecosystem import source_origin
from bundled.source_tools.fs import directory, atomic_write, json_bytes, read_regular

TERMINAL={'blocked','failed','cancelled','interrupted','discarded'}


def identity_hash(extension):
    rows=extension.get('source',{}).get('nodes',[])
    value=sorted((str(row['id']),str(row.get('lang','')),str(row.get('name',''))) for row in rows)
    return hashlib.sha256(json.dumps(value).encode()).hexdigest()


def info_only(snapshot):
    keys={'package','version','module','ecosystem','recipe','kind','origin','parent','extension_lib','upstream_module','overlay_sources'}
    return metadata({key:value for key,value in snapshot.items() if key in keys})


class RepairWorkflow:
    def __init__(self):
        self.history=None;self.admissions={};self.supervisor=None;self.lock=None;self.stopping=False;self.error=''
        self.cache_lock=threading.RLock();self.inflight=set()

    def initialize_safe(self):
        try:self.initialize();self.error=''
        except Exception as exc:
            from .support_logs import support_logs
            self.history=None;self.admissions={}
            self.error='Repair history could not be opened ('+type(exc).__name__+'). Existing files were retained; check diagnostics and storage permissions.'
            support_logs.record('repair-workflow',self.error)

    def initialize(self):
        from .source_maintenance import storage
        self.root=directory(storage()/'admissions');self.history=RepairHistory(storage()/'history');self.history.load();self.admissions={}
        for path in sorted(self.root.glob('*.json'))[:4096]:
            if not re.fullmatch(r'[0-9a-f]{32}',path.stem):continue
            row=json.loads(read_regular(path,16384))
            if not isinstance(row,dict) or row.get('id')!=path.stem:raise ValueError('Repair admission journal is damaged')
            package_name(row.get('package'));self.admissions[row['id']]=row

    def save(self,row):
        atomic_write(self.root/(row['id']+'.json'),json_bytes(row))
        with self.cache_lock:self.admissions[row['id']]=dict(row)

    def admission_rows(self):
        with self.cache_lock:return [dict(row) for row in self.admissions.values()]

    def start(self,lock):
        self.lock=lock;self.stopping=False
        if self.history and source_raws.catalog:self.supervisor=asyncio.create_task(self.watch())

    def pending_items(self,observed_ids):
        return [{'id':'repair-admission:'+row['id'],'kind':'source-repair-preparation','name':'Confirm repair admission',
            'context':row['package'],'state':'running','activity':'waiting','origin':'manual','cancellable':False,
            'message':'Checking whether the worker retained this request. The request is not replayed automatically.',
            'current':0,'total':0,'created_at':'','updated_at':''}
            for row in self.admission_rows() if row['state']=='admitting' and row['id'] not in observed_ids][:100]

    async def stop(self):
        self.stopping=True
        if self.supervisor:
            self.supervisor.cancel();await asyncio.gather(self.supervisor,return_exceptions=True)

    async def create(self,extension,body,*,raw_only=False):
        from .source_maintenance import worker,sha
        from .repair_baseline import repair_input
        from .source_orphans import source_orphans
        if not self.history or not source_raws.catalog:raise HistoryConflict('Source repair storage is not ready')
        issue=body.get('issue','Archive missing original source code for future source repair.')
        if not isinstance(issue,str) or not 10<=len(issue.strip())<=6000:raise HistoryConflict('Describe the source problem in10–6000 characters')
        if body.get('origin','manual') not in {'manual','automatic'}:raise HistoryConflict('Invalid repair origin')
        if body.get('previous_attempt') and not re.fullmatch(r'[0-9a-f]{32}',str(body['previous_attempt'])):raise HistoryConflict('Invalid previous repair identity')
        extension=(await run_blocking(source_orphans.annotate_extensions,[extension]))[0]
        baseline=await run_blocking(repair_input,extension,raw_first=True)
        if raw_only:
            baseline.pop('source_apk',None);baseline.pop('original_apk_basis',None)
        binary=sha(base64.b64decode(baseline['baseline']));pkg=extension['pkgName']
        current=await run_blocking(self.history.snapshot,pkg)
        raw_id=current['current'].get('raw_id') if current['current'] and current['current']['binary_sha256']==binary else None
        cached=source_raws.catalog.status(pkg)
        override=None
        if body.get('code_origin'):
            override=body['code_origin']
            cached=await run_blocking(source_raws.catalog.begin,extension,binary_sha256=binary,supplied=override,force=True)
            raw_id=None
        elif body.get('revision'):
            origin=(cached or {}).get('origin') or source_origin(extension)
            if not origin.get('repository'):raise HistoryConflict('Provide the original source repository before selecting its commit; no repository was guessed')
            override={'repository':origin['repository'],'revision':body['revision'],'module':body.get('module') or origin['module'],
                      'ecosystem':origin.get('ecosystem','tachiyomi')}
            cached=await run_blocking(source_raws.catalog.begin,extension,binary_sha256=binary,supplied=override,force=True)
            raw_id=None
        if cached and cached['version']==extension['versionName'] and cached.get('binary_sha256')==binary:
            # An explicit replacement-code request supersedes the installed
            # attempt's code selection while retaining every old snapshot.
            raw_id=cached.get('raw_id') if cached['state']=='available' else None
        elif raw_id:
            cached=await run_blocking(source_raws.catalog.adopt,extension,binary,raw_id)
        if raw_id:
            store,snapshot=await run_blocking(source_raws.catalog.resolve,pkg,raw_id)
            raw_status=None
        else:
            raw_status=cached if override else await run_blocking(source_raws.catalog.begin,extension,binary_sha256=binary)
            if raw_status['state']=='acquiring':source_raws.schedule_intent(raw_status,origin=body.get('origin','manual'))
            raw_id=raw_status.get('raw_id')
            store,snapshot=await run_blocking(source_raws.catalog.resolve,pkg,raw_id) if raw_id else (None,None)
        identifier=uuid.uuid4().hex
        ref={'binary_sha256':binary,'raw_id':raw_id,'version':extension['versionName'],'entrypoint':None,
             'extension_lib':None,'source_ids':sorted(str(row['id']) for row in extension.get('source',{}).get('nodes',[])),
             'identity_sha256':identity_hash(extension),'job_id':None}
        origin=raw_status['origin'] if raw_status else info_only(snapshot)
        payload={**baseline,'pkg_name':pkg,'module':snapshot['module'] if snapshot else origin['module'],
            'revision':'','origin':body.get('origin','manual'),'issue':issue.strip(),
            'request_id':identifier,'auto_install':not raw_only,'raw_only':raw_only,
            'reconstruct':body.get('reconstruct') is True}
        original=origin.get('origin',origin)
        provenance={key:original[key] for key in ('repository','revision','index_url') if original.get(key)}
        payload['reconstruction_origin']={'package':pkg,'version':extension['versionName'],'module':'.',
            'upstream_module':origin.get('upstream_module',origin['module']),'ecosystem':origin.get('ecosystem','tachiyomi'),
            'recipe':'jvm','kind':'reconstructed','origin':{**provenance,'basis':'binary-reconstruction'}}
        if body.get('previous_attempt'):payload['previous_attempt']=body['previous_attempt']
        if snapshot and not payload['reconstruct']:
            payload.update(raw_meta=info_only(snapshot),raw_code=base64.b64encode(await run_blocking(store.export_zip,pkg,raw_id)).decode('ascii'))
        admission={'id':identifier,'package':pkg,'state':'admitting','created_at':time.time(),'baseline_sha256':binary,
                   'raw_only':raw_only,'auto_install':not raw_only,'auto_attempted':False}
        if not raw_only:await run_blocking(self.history.admit,pkg,identifier,ref)
        self.inflight.add(identifier)
        try:
            try:await run_blocking(self.save,admission)
            except BaseException:
                if not raw_only:await run_blocking(self.history.finish,pkg,identifier,'failed')
                raise
            try:
                job=await worker('POST','/jobs',payload=payload)
            except Exception as exc:
                # Keep uncertain writes for GET reconciliation, never a replay.
                from fastapi import HTTPException
                if isinstance(exc,HTTPException) and exc.status_code<500:
                    admission.update(state='rejected',message=str(exc.detail)[:1000]);await run_blocking(self.save,admission)
                    if not raw_only:await run_blocking(self.history.finish,pkg,identifier,'failed')
                raise
            admission.update(state='accepted',worker_state=job.get('state'));await run_blocking(self.save,admission)
            return job
        finally:self.inflight.discard(identifier)

    async def raw_payload(self,pkg,raw_id):
        store,snapshot=await run_blocking(source_raws.catalog.resolve,pkg,raw_id)
        return {'raw_meta':info_only(snapshot),'raw_code':base64.b64encode(await run_blocking(store.export_zip,pkg,raw_id)).decode('ascii')}

    async def resume(self,job,*,reconstruct=False):
        from .source_maintenance import worker,ensure_repair_ready
        await ensure_repair_ready()  # Before reading/compressing/transferring a code ZIP.
        payload={'expected_updated_at':job['updated_at'],'reconstruct':reconstruct}
        if not reconstruct:
            row=source_raws.catalog.status(job['pkg_name'])
            if not row or row['state']!='available':raise HistoryConflict('Provide raw code before resuming this repair')
            if row.get('binary_sha256') and row['binary_sha256']!=job['baseline_sha256']:raise HistoryConflict('Raw code was provided for a different installed binary')
            payload.update(await self.raw_payload(job['pkg_name'],row['raw_id']))
        return await worker('POST','/jobs/'+job['id']+'/raws',payload=payload)

    async def watch(self):
        from .source_maintenance import worker,source_maintenance
        from .support_logs import support_logs
        from .repair_tasks import repair_tasks,TERMINAL as TASK_TERMINAL
        polls={}
        while True:
            await asyncio.sleep(3)
            for row in self.admission_rows():
                if row['state'] not in {'admitting','accepted'}:continue
                if row['id'] in self.inflight:continue
                try:
                    if row.get('worker_state')=='awaiting-raws':
                        status=source_raws.catalog.status(row['package'])
                        with repair_tasks.lock:
                            observed=repair_tasks.jobs.get(row['id'])
                            fresh=repair_tasks.reachable is True and time.monotonic()-repair_tasks.observed<=20
                            busy=fresh and any(job.get('state') not in {*TASK_TERMINAL,'awaiting-raws','unknown','idle','complete'}
                                               for job in repair_tasks.jobs.values())
                        if not observed or observed['state']=='awaiting-raws':
                            available=status and status['state']=='available'
                            if available and busy:continue
                            if not available and time.monotonic()-polls.get(row['id'],0)<60:continue
                    polls[row['id']]=time.monotonic()
                    job=await worker('GET','/jobs/'+row['id'])
                    if row['state']=='admitting':
                        row['state']='accepted';await run_blocking(self.save,row)
                    if row.get('worker_state')!=job['state']:
                        row['worker_state']=job['state'];await run_blocking(self.save,row)
                    if job['state']=='awaiting-raws':
                        status=source_raws.catalog.status(row['package'])
                        if status and status['state']=='available':await self.resume(job)
                        continue
                    if job['state'] in TERMINAL:
                        if not row['raw_only']:await run_blocking(self.history.finish,row['package'],row['id'],job['state'])
                        row['state']=job['state'];await run_blocking(self.save,row);continue
                    if job['state']=='raws-retained':
                        payload=await worker('GET','/jobs/'+row['id']+'/raw-code',binary=True)
                        await run_blocking(source_raws.catalog.retain,row['package'],payload,job['raw_meta'],activate=True,expected_binary=row['baseline_sha256'])
                        row['state']='raws-retained';await run_blocking(self.save,row);continue
                    if job['state']=='ready' and row.get('auto_install') and not row.get('auto_attempted'):
                        # Changed address ownership that still needs review must
                        # remain visible; a general Repair click is not its proof.
                        if job.get('address_migrations') and not job.get('automatic_address_trust'):continue
                        if source_maintenance.task and not source_maintenance.task.done():continue
                        row['auto_attempted']=True;await run_blocking(self.save,row)
                        reviewed=job['candidate_sha256'] if job.get('automatic_address_trust') else None
                        source_maintenance.operation={'state':'queued','id':row['id']}
                        source_maintenance.task=asyncio.create_task(source_maintenance.install(row['id'],self.lock,reviewed_candidate_sha256=reviewed))
                    if job['state']=='ready' and row.get('auto_attempted') and source_maintenance.task and source_maintenance.task.done():
                        history=await run_blocking(self.history.snapshot,row['package'])
                        if history['current'] and history['current'].get('job_id')==row['id']:
                            await worker('POST','/jobs/'+row['id']+'/installed')
                            row['state']='installed';await run_blocking(self.save,row)
                        elif source_maintenance.operation.get('id')==row['id'] and source_maintenance.operation.get('state') in {'failed','rolled-back','recovery-needed'}:
                            await run_blocking(self.history.finish,row['package'],row['id'],'failed')
                            row['state']='promotion-failed';await run_blocking(self.save,row)
                    if job['state']=='installed':
                        row['state']='installed';await run_blocking(self.save,row)
                except asyncio.CancelledError:raise
                except Exception as exc:
                    # Never replay a lost POST. A retained intent with an actual
                    # 404 can be released after reconciliation, not on timeout.
                    from fastapi import HTTPException
                    if isinstance(exc,HTTPException) and exc.status_code==409:continue
                    if row['state']=='admitting' and isinstance(exc,HTTPException) and exc.status_code==404 and time.time()-row['created_at']>=120:
                        if not row['raw_only']:await run_blocking(self.history.finish,row['package'],row['id'],'interrupted')
                        row.update(state='interrupted',message='The worker did not retain this request; start a new explicit repair.');await run_blocking(self.save,row)
                    else:support_logs.record('repair-workflow',str(exc)[:1000])

    async def install_plan(self,job,extension,artifact):
        from .source_maintenance import worker,storage,atomic,sha
        if not self.history:raise HistoryConflict(self.error or 'Repair history is not ready')
        baseline_code=await worker('GET','/jobs/'+job['id']+'/raw-code',binary=True)
        if sha(baseline_code)!=job.get('raw_sha256'):raise HistoryConflict('Original raw code changed after validation')
        baseline_snapshot=await run_blocking(source_raws.catalog.retain,job['pkg_name'],baseline_code,job['raw_meta'])
        await run_blocking(self.history.bind_baseline_code,job['pkg_name'],job['id'],baseline_snapshot['id'])
        payload=await worker('GET','/jobs/'+job['id']+'/raw-code?candidate=true',binary=True)
        if sha(payload)!=job.get('candidate_raw_sha256'):raise HistoryConflict('Candidate raw code changed after validation')
        info=job.get('candidate_raw_meta')
        if not info:raise HistoryConflict('Candidate raw code has no build provenance')
        snapshot=await run_blocking(source_raws.catalog.retain,job['pkg_name'],payload,info)
        contract=job.get('baseline_contract') or {}
        ref={'binary_sha256':job['candidate_sha256'],'raw_id':snapshot['id'],'version':extension['versionName'],
             'entrypoint':contract.get('entrypoint'),'extension_lib':contract.get('library'),
             'source_ids':sorted(job['expected_ids']),'identity_sha256':identity_hash(extension),'job_id':job['id']}
        state=await run_blocking(self.history.snapshot,job['pkg_name'])
        plan=await run_blocking(self.history.plan_install,job['pkg_name'],job['id'],ref,expected_revision=state['revision'])
        await run_blocking(atomic,storage()/'backups'/sha(artifact)/'installed.jar',artifact)
        return plan


repair_workflow=RepairWorkflow()


def workflow_router(admin_guard,lock):
    from fastapi import APIRouter,Depends,HTTPException,Request
    from fastapi.responses import Response
    from .source_maintenance import inventory,installed_path,worker,sha,ensure_repair_ready,source_maintenance
    router=APIRouter(dependencies=[Depends(admin_guard)])

    async def selected(pkg):
        if not source_raws.catalog:raise HTTPException(503,source_raws.error or 'Source-code storage is not ready')
        extension=next((row for row in await inventory() if row['pkgName']==pkg and row.get('isInstalled')),None)
        if not extension:raise HTTPException(404,'Select an installed source')
        return extension,sha(await run_blocking(installed_path(extension).read_bytes))

    def current_request(pkg,extension,binary,revision):
        row=source_raws.catalog.status(pkg)
        if (not row or row['revision']!=revision or row['version']!=extension['versionName']
                or row.get('binary_sha256') and row['binary_sha256']!=binary):
            raise HTTPException(409,'Installed source or raw-code request changed; refresh before acting')
        return row

    @router.get('/raws/{pkg}')
    async def raw_status(pkg:str):
        from .source_orphans import source_orphans
        async with lock:
            extension,binary=await selected(pkg)
            extension=(await run_blocking(source_orphans.annotate_extensions,[extension]))[0]
            cached=source_raws.catalog.status(pkg)
            history=await run_blocking(repair_workflow.history.snapshot,pkg) if repair_workflow.history else {}
            current=history.get('current') or {}
            if current.get('raw_id') and current.get('binary_sha256')==binary and (not cached or cached.get('binary_sha256')!=binary):
                row=await run_blocking(source_raws.catalog.adopt,extension,binary,current['raw_id'])
            else:row=await run_blocking(source_raws.catalog.begin,extension,binary_sha256=binary)
        if row['state']=='acquiring':source_raws.schedule_intent(row)
        return row

    @router.post('/raws/{pkg}/project')
    async def raw_project(pkg:str,request:Request):
        data=bytearray()
        try:revision=int(request.headers.get('X-Raw-Revision',''));version=request.headers['X-Source-Version']
        except (ValueError,KeyError):raise HTTPException(400,'Refresh the raw-code request before uploading') from None
        async for chunk in request.stream():
            data.extend(chunk)
            if len(data)>32*1024*1024:raise HTTPException(413,'Source project ZIP exceeds32MiB')
        async with lock:
            extension,binary=await selected(pkg)
            if extension['versionName']!=version:raise HTTPException(409,'Source version changed; refresh before supplying code')
            try:
                return await run_blocking(source_raws.catalog.provide,extension,bytes(data),expected_revision=revision,binary_sha256=binary,
                    module=request.headers.get('X-Source-Module') or None)
            except ValueError as exc:raise HTTPException(409,str(exc)) from None

    @router.post('/raws/{pkg}/request-input')
    async def new_raw_input(pkg:str,request:Request):
        body=await request.json()
        if not isinstance(body,dict):raise HTTPException(400,'Expected a raw-code input request')
        async with lock:
            extension,binary=await selected(pkg)
            current_request(pkg,extension,binary,body.get('revision'))
            try:return await run_blocking(source_raws.catalog.request_input,pkg,expected_revision=body.get('revision'),binary_sha256=binary)
            except ValueError as exc:raise HTTPException(409,str(exc)) from None

    @router.get('/raws/{pkg}/code')
    async def retained_raw_code(pkg:str):
        row=source_raws.catalog.status(pkg)
        if not row or row.get('state')!='available':raise HTTPException(409,'No retained raw code is selected for this source')
        store,snapshot=await run_blocking(source_raws.catalog.resolve,pkg,row['raw_id'])
        data=await run_blocking(store.export_zip,pkg,snapshot['id'],portable=True)
        return Response(data,media_type='application/zip',headers={'Content-Disposition':'attachment; filename="AutoSourcarr-source-code.zip"','Cache-Control':'no-store'})

    @router.post('/raws/{pkg}/origin',status_code=202)
    async def raw_origin(pkg:str,request:Request):
        body=await request.json()
        if not isinstance(body,dict):raise HTTPException(400,'Provide the original source repository')
        async with lock:
            extension,binary=await selected(pkg)
            status=current_request(pkg,extension,binary,body.get('revision'))
            try:row=await run_blocking(source_raws.catalog.begin,extension,binary_sha256=binary,supplied=body.get('origin'),force=True)
            except ValueError as exc:raise HTTPException(400,str(exc)) from None
        source_raws.schedule_intent(row);return row

    @router.post('/raws/{pkg}/discover',status_code=202)
    async def discover_raw_origin(pkg:str,request:Request):
        body=await request.json()
        if not isinstance(body,dict) or set(body)!={'revision'} or type(body.get('revision')) is not int or body['revision']<1:raise HTTPException(400,'Refresh the installed code request before discovery')
        async with lock:
            extension,binary=await selected(pkg)
            row=current_request(pkg,extension,binary,body.get('revision'))
            if row['state']!='needs-input':raise HTTPException(409,'This source already has code or an active acquisition')
        source_raws.discover_intent(row,extension)
        return {**row,'message':'Repository lookup queued. Refresh this source after the archive task finishes; original installed bytes remain unchanged.'}

    @router.post('/raws/{pkg}/unavailable',status_code=202)
    async def unavailable(pkg:str,request:Request):
        body=await request.json()
        if not isinstance(body,dict):raise HTTPException(400,'Expected a raw-code choice')
        await ensure_repair_ready()
        async with lock:
            extension,binary=await selected(pkg)
            status=current_request(pkg,extension,binary,body.get('revision'))
            if body.get('job_id'):
                job=await worker('GET','/jobs/'+str(body['job_id']))
                if job.get('pkg_name')!=pkg or job.get('baseline_sha256')!=binary:raise HTTPException(409,'This repair belongs to different installed bytes')
                if job.get('state')=='awaiting-raws':return await repair_workflow.resume(job,reconstruct=True)
                if job.get('state') not in TERMINAL:raise HTTPException(409,'Wait or cancel this repair before starting reconstruction')
                await run_blocking(repair_workflow.history.finish,pkg,job['id'],job['state'])
                return await repair_workflow.create(extension,{'reconstruct':True,'previous_attempt':job['id'],
                    'issue':'Original matching raws are unavailable. Reconstruct this installed source, check website/API locations and correct the previously blocked repair.'})
            try:return await repair_workflow.create(extension,{'reconstruct':True},raw_only=True)
            except ValueError as exc:raise HTTPException(409,str(exc)) from None

    @router.get('/jobs/{job_id}/code')
    async def download_code(job_id:str,candidate:bool=False):
        if not re.fullmatch(r'[0-9a-f]{32}',job_id):raise HTTPException(404,'Invalid repair identifier')
        admission=repair_workflow.admissions.get(job_id)
        history=await run_blocking(repair_workflow.history.snapshot,admission['package']) if admission else None
        current=(history or {}).get('current') or {}
        if candidate and current.get('job_id')==job_id and current.get('raw_id'):
            store,info=await run_blocking(source_raws.catalog.resolve,admission['package'],current['raw_id'])
            data=await run_blocking(store.export_zip,admission['package'],info['id'],portable=True)
        else:
            job=await worker('GET','/jobs/'+job_id)
            data=await worker('GET','/jobs/'+job_id+'/raw-code'+('?candidate=true' if candidate else ''),binary=True)
            info=job.get('candidate_raw_meta' if candidate else 'raw_meta')
            if not info:raise HTTPException(409,'Retained code has no exportable provenance')
            # Publish a portable code-only snapshot, never a job/binary/profile archive.
            retained=await run_blocking(source_raws.catalog.retain,job['pkg_name'],data,info)
            store,_=await run_blocking(source_raws.catalog.resolve,job['pkg_name'],retained['id'])
            data=await run_blocking(store.export_zip,job['pkg_name'],retained['id'],portable=True)
        return Response(data,media_type='application/zip',headers={'Content-Disposition':'attachment; filename="AutoSourcarr-source-code-'+job_id+'.zip"','Cache-Control':'no-store'})

    @router.post('/jobs/{job_id}/feedback',status_code=202)
    async def feedback(job_id:str,request:Request):
        body=await request.json()
        if not isinstance(body,dict) or body.get('action') not in {'fixed','retry','revert'}:raise HTTPException(400,'Select Fixed, Retry or Revert')
        admission=repair_workflow.admissions.get(job_id)
        if not admission:raise HTTPException(409,'This retained legacy repair has no per-source confirmation history; start a fresh repair')
        async with lock:
            if source_maintenance.task and not source_maintenance.task.done():raise HTTPException(409,'A source change is running; wait for it to finish')
            extension,binary=await selected(admission['package'])
            if binary!=body.get('installed_sha256'):raise HTTPException(409,'Source binary changed; refresh before acting')
            options={'expected_revision':body.get('revision'),'observed_sha256':binary}
            try:
                if body['action']=='fixed':
                    state=await run_blocking(repair_workflow.history.snapshot,admission['package'])
                    raw_id=(state.get('current') or {}).get('raw_id')
                    if not raw_id:raise HistoryConflict('Retained working code is required before Fixed')
                    store,_=await run_blocking(source_raws.catalog.resolve,admission['package'],raw_id)
                    await run_blocking(store.export_zip,admission['package'],raw_id)
                    if sha(await run_blocking(installed_path(extension).read_bytes))!=binary:raise HistoryConflict('Installed source changed while its code backup was checked')
                    return await run_blocking(repair_workflow.history.confirm,admission['package'],job_id,**options)
                if body['action']=='revert':
                    await run_blocking(repair_workflow.history.plan_revert,admission['package'],job_id,**options)
                    source_maintenance.operation={'state':'queued','id':job_id,'message':'Revert queued'}
                    source_maintenance.task=asyncio.create_task(source_maintenance.revert(job_id,lock,
                        expected_revision=options['expected_revision'],expected_sha256=binary))
                    return {'queued':True}
                problem=body.get('feedback','')
                await run_blocking(repair_workflow.history.report_failed,admission['package'],job_id,feedback=problem,**options)
                return await repair_workflow.create(extension,{'issue':'The previous installed repair still does not work. '+(problem or 'Repeat the functional checks and investigate the remaining failure using the previous code.'),
                    'previous_attempt':job_id})
            except ValueError as exc:raise HTTPException(409,str(exc)) from None
    return router
