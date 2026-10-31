"""Declining a validated repair persists review without touching live packages."""
import asyncio
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
from unittest.mock import AsyncMock

import httpx
from fastapi import FastAPI
import pytest

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'repair-worker'))
import contracts
import runner as runner_module
import server as worker_server
from internal_runner import InternalRunner
from app import source_maintenance as maintenance
from app.config import settings

TOKEN='controlled-worker-token-'+'x'*40
HEADERS={'Authorization':'Bearer '+TOKEN}
JOB_ID='a'*32


@pytest.fixture
def ready_job(tmp_path,monkeypatch):
    runner=runner_module.Runner(tmp_path,tmp_path,'fixture-only',owner=None)
    directory=runner.path(JOB_ID);directory.mkdir()
    candidate=b'retained-validated-fixture'
    evidence={'candidate.jar':candidate,'baseline.jar':b'retained-baseline',
              'conversation.json':json.dumps([{'role':'assistant','content':'Review evidence'}]).encode(),
              'diff.txt':b'-before\n+after\n'}
    for name,data in evidence.items(): (directory/name).write_bytes(data)
    job={'id':JOB_ID,'created_at':time.time(),'pkg_name':'eu.kanade.tachiyomi.extension.en.fixture',
         'module':'src/en/fixture','checks':[{'passed':True}],'expected_ids':['22'],
         'validation_time':time.time(),'candidate_sha256':contracts.digest(candidate)}
    runner.save(job,'ready','Validated candidate awaiting review')
    monkeypatch.setattr(worker_server,'runner',runner)
    monkeypatch.setattr(worker_server,'pending',set())
    monkeypatch.setenv('REPAIR_TOKEN',TOKEN)
    return runner,job,directory,evidence


@pytest.mark.asyncio
async def test_discard_is_authenticated_persisted_idempotent_and_retains_evidence(ready_job):
    runner,job,directory,evidence=ready_job
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=worker_server.app),base_url='http://worker') as client:
        path='/jobs/'+JOB_ID+'/discard'
        assert (await client.post(path)).status_code==401
        assert worker_server.read_job(JOB_ID)['state']=='ready'
        response=await client.post(path,headers=HEADERS)
        assert response.status_code==200,response.text
        saved=worker_server.read_job(JOB_ID)
        assert saved['state']=='discarded' and saved['discarded_at']>0
        assert saved['history'][-1]['state']=='discarded'
        history=list(saved['history'])
        retry=await client.post(path,headers=HEADERS)
        assert retry.json()['history']==history
        details=await client.get('/jobs/'+JOB_ID,headers=HEADERS)
        assert details.json()['conversation']==json.loads(evidence['conversation.json'])
        assert details.json()['patch']==evidence['diff.txt'].decode()
        assert details.json()['checks']==job['checks']
        artifact=await client.get('/jobs/'+JOB_ID+'/artifact',headers=HEADERS)
        assert artifact.status_code==200 and artifact.content==evidence['candidate.jar']
        assert (await client.post('/jobs/'+JOB_ID+'/installed',headers=HEADERS)).status_code==409
        # Cancel retains its existing meaning and cannot turn a decline back into a ready job.
        assert (await client.post('/jobs/'+JOB_ID+'/cancel',headers=HEADERS)).json()=={'requested':False}
    assert all((directory/name).read_bytes()==data for name,data in evidence.items())
    assert worker_server.read_job(JOB_ID)['history']==history


@pytest.mark.parametrize('state',['queued','editing','blocked','failed','cancelled','interrupted','installed'])
@pytest.mark.asyncio
async def test_discard_rejects_every_nonready_state_without_mutating_files(ready_job,state):
    runner,job,directory,evidence=ready_job
    runner.save(job,state,'fixture')
    before=(directory/'job.json').read_bytes()
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=worker_server.app),base_url='http://worker') as client:
        response=await client.post('/jobs/'+JOB_ID+'/discard',headers=HEADERS)
    assert response.status_code==409
    assert (directory/'job.json').read_bytes()==before


@pytest.mark.asyncio
async def test_ready_pending_cannot_be_discarded_and_running_cancel_is_unchanged(ready_job):
    runner,job,directory,evidence=ready_job
    worker_server.pending.add(JOB_ID)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=worker_server.app),base_url='http://worker') as client:
        assert (await client.post('/jobs/'+JOB_ID+'/discard',headers=HEADERS)).status_code==409
        assert (await client.post('/jobs/'+JOB_ID+'/cancel',headers=HEADERS)).json()=={'requested':True}
    assert JOB_ID in runner.cancelled and worker_server.read_job(JOB_ID)['state']=='ready'


@pytest.mark.parametrize('first',['discard','installed'])
@pytest.mark.asyncio
async def test_terminal_transition_is_serialized_before_the_next_read(ready_job,monkeypatch,first):
    runner,job,directory,evidence=ready_job
    entered=threading.Event();release=threading.Event()
    original=runner.save
    def pause(job,state,message,**values):
        if state==('discarded' if first=='discard' else 'installed'):
            entered.set()
            assert release.wait(5),'test failed to release the state writer'
        return original(job,state,message,**values)
    monkeypatch.setattr(runner,'save',pause)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=worker_server.app),base_url='http://worker') as client:
        first_request=asyncio.create_task(client.post('/jobs/'+JOB_ID+'/'+first,headers=HEADERS))
        try:
            assert await asyncio.to_thread(entered.wait,5)
            other='installed' if first=='discard' else 'discard'
            second=asyncio.create_task(client.post('/jobs/'+JOB_ID+'/'+other,headers=HEADERS))
            await asyncio.sleep(.02)
            assert not second.done(),'another transition read the still-ready state during publication'
        finally: release.set()
        assert (await first_request).status_code==200
        assert (await second).status_code==409
    assert worker_server.read_job(JOB_ID)['state']==('discarded' if first=='discard' else 'installed')


@pytest.mark.parametrize('runtime',['integrated','external'])
def test_discard_remains_terminal_across_both_cleanup_paths(ready_job,monkeypatch,runtime):
    runner,job,directory,evidence=ready_job
    runner.save(job,'discarded','Reviewed decline',discarded_at=time.time())
    before=(directory/'job.json').read_bytes()
    if runtime=='integrated': InternalRunner.cleanup_interrupted(runner)
    else:
        def docker(args,**kwargs):
            output='[{"Internal":true,"Containers":{}}]' if args[1:3]==['network','inspect'] else ''
            return subprocess.CompletedProcess(args,0,stdout=output,stderr='')
        monkeypatch.setattr(runner_module.subprocess,'run',docker)
        runner.cleanup_interrupted()
    assert (directory/'job.json').read_bytes()==before


@pytest.mark.asyncio
async def test_authenticated_app_proxy_declines_and_rejects_later_install(ready_job,monkeypatch):
    from app.main import admin_guard
    runner,job,directory,evidence=ready_job
    manager=maintenance.SourceMaintenance()
    monkeypatch.setattr(maintenance,'source_maintenance',manager)
    monkeypatch.setattr(settings,'admin_token','fixture-admin')
    monkeypatch.setattr(settings,'source_engine_mode','integrated')
    monkeypatch.setattr(settings,'repair_worker_url','http://worker')
    monkeypatch.setattr(settings,'repair_worker_token',TOKEN)
    app=FastAPI();app.include_router(maintenance.maintenance_router(admin_guard,asyncio.Lock()))
    real_client=httpx.AsyncClient
    client=real_client(transport=httpx.ASGITransport(app=app),base_url='http://app')
    # Keep the actual bounded/authenticated worker() client; replace its network transport only.
    monkeypatch.setattr(maintenance.httpx,'AsyncClient',lambda **kw:real_client(transport=httpx.ASGITransport(app=worker_server.app),**kw))
    async with client:
        path='/api/admin/source-maintenance/jobs/'+JOB_ID
        assert (await client.post(path+'/discard')).status_code==401
        headers={'Authorization':'Bearer fixture-admin'}
        assert (await client.post(path+'/discard',headers=headers)).status_code==200
        assert (await client.post(path+'/install',headers=headers)).status_code==409
        assert (await client.get(path,headers=headers)).json()['state']=='discarded'
        assert (await client.post('/api/admin/source-maintenance/jobs/invalid/discard',headers=headers)).status_code==404
    assert manager.task is None and manager.operation=={'state':'idle'}


@pytest.mark.parametrize('operation',['running','installed','recovered'])
@pytest.mark.asyncio
async def test_app_refuses_discard_during_or_after_a_live_promotion(ready_job,monkeypatch,operation):
    manager=maintenance.SourceMaintenance()
    monkeypatch.setattr(maintenance,'source_maintenance',manager)
    worker=AsyncMock();monkeypatch.setattr(maintenance,'worker',worker)
    if operation=='running': manager.task=asyncio.create_task(asyncio.sleep(60))
    else: manager.operation={'id':JOB_ID,'state':operation}
    app=FastAPI();app.include_router(maintenance.maintenance_router(lambda:None,asyncio.Lock()))
    try:
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://app') as client:
            assert (await client.post('/api/admin/source-maintenance/jobs/'+JOB_ID+'/discard')).status_code==409
        worker.assert_not_awaited()
    finally:
        if manager.task:
            manager.task.cancel();await asyncio.gather(manager.task,return_exceptions=True)


@pytest.mark.asyncio
async def test_persisted_discard_unblocks_the_next_maintenance_check(ready_job,tmp_path,monkeypatch):
    from app.source_engine import source_engine
    from app.suwayomi import source_host
    runner,job,directory,evidence=ready_job
    monkeypatch.setattr(settings,'settings_config_dir',tmp_path/'config')
    monkeypatch.setattr(settings,'repair_worker_url','http://worker')
    monkeypatch.setattr(settings,'source_engine_mode','integrated')
    monkeypatch.setattr(source_engine,'state','ready')
    real_client=httpx.AsyncClient
    async def status(method,path,**kw):
        assert method=='GET' and path=='/status'
        async with real_client(transport=httpx.ASGITransport(app=worker_server.app),base_url='http://worker') as client:
            response=await client.get(path,headers=HEADERS)
            return response.json()
    monkeypatch.setattr(maintenance,'worker',status)
    catalog=AsyncMock(return_value={'extensions':[]});monkeypatch.setattr(source_host,'extensions',catalog)
    manager=maintenance.SourceMaintenance();manager.watch_state={'enabled':True,'next_check':0,'attempts':{}}
    await manager.watch_once();catalog.assert_not_awaited()
    runner.save(job,'discarded','Reviewed decline',discarded_at=time.time())
    manager.watch_state['next_check']=0
    await manager.watch_once();catalog.assert_awaited_once_with(refresh=True)
    assert 'awaiting review' not in manager.watch_state['message']
    assert (tmp_path/'config/source-maintenance.json').is_file()


def test_ui_confirms_decline_retains_evidence_and_keeps_running_cancel():
    # This flow moved to Tasks; execute its current renderer and event handlers.
    from test_v086_ui import node
    script = Path(__file__).with_name('ui_v0340_repair_tasks.js')
    node(script.read_text().replace("__dirname", repr(str(script.parent))))
