"""Readiness retries use real state files and authenticated MockTransport clients."""
import asyncio
import json
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

from fastapi import HTTPException
import httpx
import pytest

from app import source_maintenance as module
from app.config import settings
from app.source_engine import source_engine
from app.suwayomi import source_host

DAY=86400
WEEK=7*DAY


@pytest.fixture
def watch_fixture(tmp_path,monkeypatch):
    clock=SimpleNamespace(now=1700000000.0)
    monkeypatch.setattr(module,'time',SimpleNamespace(time=lambda:clock.now))
    monkeypatch.setattr(settings,'settings_config_dir',tmp_path/'config')
    monkeypatch.setattr(settings,'data_dir',tmp_path/'data')
    monkeypatch.setattr(settings,'repair_worker_url','http://127.0.0.1:8766')
    monkeypatch.setattr(settings,'repair_worker_token','controlled-fixture-'+'x'*40)
    monkeypatch.setattr(settings,'app_services_mode','integrated')
    monkeypatch.setattr(settings,'source_engine_mode','integrated')
    monkeypatch.setattr(source_engine,'state','ready')
    state_path=tmp_path/'config/source-maintenance.json'
    def persisted():return json.loads(state_path.read_text())
    fixture=SimpleNamespace(clock=clock,path=state_path,persisted=persisted,
        requests=[],posts=[],status={'active':None,'jobs':[]},status_code=200,
        read_error=None,post_error=None,read_handler=None)
    async def send(request):
        fixture.requests.append((request.method,request.url.path))
        assert request.headers['authorization']=='Bearer '+settings.repair_worker_token
        if request.method=='GET':
            assert request.url.path==httpx.URL(settings.repair_worker_url.rstrip('/')).path.rstrip('/')+'/status'
            if fixture.read_handler:return await fixture.read_handler(request)
            if fixture.read_error:raise fixture.read_error('Controlled helper read failure',request=request)
            return httpx.Response(fixture.status_code,json=fixture.status)
        assert request.method=='POST' and request.url.path=='/jobs'
        # The durable package guard must precede a possibly accepted POST.
        body=json.loads(request.content);saved=persisted()
        assert saved['next_check']>=clock.now+DAY
        assert saved['attempts'][body['pkg_name']]==clock.now
        fixture.posts.append(body)
        if fixture.post_error:raise fixture.post_error('Controlled acknowledgement lost',request=request)
        return httpx.Response(202,json={'id':'a'*32})
    actual_client=httpx.AsyncClient
    monkeypatch.setattr(module.httpx,'AsyncClient',lambda **kwargs:actual_client(transport=httpx.MockTransport(send),**kwargs))
    async def catalog(**kwargs):
        saved=persisted()
        assert saved['next_check']>=clock.now+DAY
        assert 'local_retry_after' not in saved
        return {'extensions':[]}
    fixture.catalog=AsyncMock(side_effect=catalog)
    fixture.inventory=AsyncMock(return_value=[])
    monkeypatch.setattr(source_host,'extensions',fixture.catalog)
    monkeypatch.setattr(module,'inventory',fixture.inventory)
    fixture.manager=module.SourceMaintenance();fixture.manager.watch_state['enabled']=True
    def restart():
        manager=module.SourceMaintenance();manager.read_watch();return manager
    fixture.restart=restart
    return fixture


@pytest.mark.asyncio
async def test_local_failure_does_not_reserve_a_day_and_recovery_runs_after_persisted_short_retry(watch_fixture):
    fixture=watch_fixture;fixture.status_code=503
    await fixture.manager.watch_once()
    saved=fixture.persisted()
    assert saved['next_check']==0 and saved['attempts']=={}
    assert saved['local_retry_after']==fixture.clock.now+60
    assert 'No source check or repair request' in saved['message']
    fixture.catalog.assert_not_awaited();fixture.inventory.assert_not_awaited()
    fixture.status_code=200
    restarted=fixture.restart()
    await restarted.watch_once()
    fixture.clock.now+=59;await restarted.watch_once()
    assert fixture.requests==[('GET','/status')]
    fixture.clock.now+=1;await restarted.watch_once()
    assert fixture.requests==[('GET','/status'),('GET','/status')]
    fixture.catalog.assert_awaited_once_with(refresh=True)
    assert fixture.persisted()['next_check']==fixture.clock.now+DAY
    assert 'local_retry_after' not in fixture.persisted()


@pytest.mark.parametrize('error',[httpx.ConnectError,httpx.ReadTimeout])
@pytest.mark.asyncio
async def test_local_transport_failures_remain_short_persisted_deferrals(watch_fixture,error):
    fixture=watch_fixture;fixture.read_error=error
    await fixture.manager.watch_once()
    assert fixture.persisted()['next_check']==0
    assert fixture.persisted()['local_retry_after']==fixture.clock.now+60
    fixture.catalog.assert_not_awaited();fixture.inventory.assert_not_awaited()
    assert not fixture.posts


@pytest.mark.parametrize('status',[None,{}, {'active':None,'jobs':'invalid'},
                                  {'active':None,'jobs':[{}]}, {'active':False,'jobs':[]}])
@pytest.mark.asyncio
async def test_incomplete_loopback_status_is_not_upstream_admission(watch_fixture,status):
    fixture=watch_fixture;fixture.status=status
    await fixture.manager.watch_once()
    assert fixture.persisted()['next_check']==0
    assert fixture.persisted()['local_retry_after']==fixture.clock.now+60
    fixture.catalog.assert_not_awaited();assert not fixture.posts


@pytest.mark.asyncio
async def test_readiness_deadline_cancels_owned_status_wait_without_a_source_request(watch_fixture,monkeypatch):
    fixture=watch_fixture;cancelled=asyncio.Event()
    async def hung(request):
        try:await asyncio.Future()
        finally:cancelled.set()
    fixture.read_handler=hung
    monkeypatch.setattr(module,'LOCAL_REPAIR_STATUS_TIMEOUT',.01)
    await fixture.manager.watch_once()
    assert cancelled.is_set()
    assert fixture.persisted()['next_check']==0
    assert fixture.persisted()['local_retry_after']==fixture.clock.now+60
    fixture.catalog.assert_not_awaited();assert not fixture.posts


@pytest.mark.asyncio
async def test_cancelling_readiness_retains_only_short_gate_and_drains_the_get(watch_fixture):
    fixture=watch_fixture;entered=asyncio.Event();cancelled=asyncio.Event()
    async def held(request):
        entered.set()
        try:await asyncio.Future()
        finally:cancelled.set()
    fixture.read_handler=held
    checking=asyncio.create_task(fixture.manager.watch_once())
    await asyncio.wait_for(entered.wait(),1)
    checking.cancel()
    with pytest.raises(asyncio.CancelledError):await checking
    assert cancelled.is_set()
    assert fixture.persisted()['next_check']==0
    assert fixture.persisted()['local_retry_after']==fixture.clock.now+60
    fixture.catalog.assert_not_awaited();assert not fixture.posts


@pytest.mark.asyncio
async def test_readiness_gate_is_durable_before_get_and_serializes_concurrent_checks(watch_fixture):
    fixture=watch_fixture;entered=asyncio.Event();release=asyncio.Event()
    async def held(request):
        assert fixture.persisted()['local_retry_after']==fixture.clock.now+60
        assert fixture.persisted()['next_check']==0
        entered.set();await release.wait()
        return httpx.Response(200,json={'active':None,'jobs':[]})
    fixture.read_handler=held
    first=asyncio.create_task(fixture.manager.watch_once())
    try:
        await asyncio.wait_for(entered.wait(),1)
        await fixture.manager.watch_once()
        assert fixture.requests==[('GET','/status')]
    finally:release.set()
    await first
    fixture.catalog.assert_awaited_once()


@pytest.mark.parametrize('url,mode',[
    ('http://worker.invalid:8766','integrated'),
    ('http://127.0.0.1:8766','external'),
    ('http://127.0.0.1:8766/proxy/source','integrated'),
])
@pytest.mark.asyncio
async def test_only_integrated_loopback_readiness_gets_short_retry(watch_fixture,monkeypatch,url,mode):
    fixture=watch_fixture;fixture.status_code=503
    monkeypatch.setattr(settings,'repair_worker_url',url)
    monkeypatch.setattr(settings,'app_services_mode',mode)
    with pytest.raises(HTTPException):await fixture.manager.watch_once()
    assert fixture.persisted()['next_check']==fixture.clock.now+DAY
    assert 'local_retry_after' not in fixture.persisted()
    fixture.catalog.assert_not_awaited();assert not fixture.posts


@pytest.mark.parametrize('status',[
    {'active':'a'*32,'jobs':[]},
    {'active':None,'jobs':[{'id':'a'*32,'state':'queued'}]},
    {'active':None,'jobs':[{'id':'a'*32,'state':'ready'}]},
])
@pytest.mark.asyncio
async def test_existing_active_and_ready_review_deferrals_keep_the_daily_window(watch_fixture,status):
    fixture=watch_fixture;fixture.status=status
    await fixture.manager.watch_once()
    assert fixture.persisted()['next_check']==fixture.clock.now+DAY
    assert 'local_retry_after' not in fixture.persisted()
    fixture.catalog.assert_not_awaited();assert not fixture.posts


@pytest.mark.asyncio
async def test_upstream_failure_does_not_release_daily_reservation_for_a_short_retry(watch_fixture):
    fixture=watch_fixture
    fixture.catalog.side_effect=RuntimeError('Controlled upstream failure after admission')
    with pytest.raises(RuntimeError):await fixture.manager.watch_once()
    assert fixture.persisted()['next_check']==fixture.clock.now+DAY
    fixture.clock.now+=3600
    await fixture.restart().watch_once()
    fixture.catalog.assert_awaited_once();assert fixture.requests==[('GET','/status')]


@pytest.mark.asyncio
async def test_a_failed_daily_reservation_write_cannot_issue_upstream_io(watch_fixture,monkeypatch):
    fixture=watch_fixture;original=fixture.manager.save_watch;writes=0
    def fail_daily_write():
        nonlocal writes
        writes+=1
        if writes==2:raise OSError('Controlled reservation write failure')
        original()
    monkeypatch.setattr(fixture.manager,'save_watch',fail_daily_write)
    with pytest.raises(OSError):await fixture.manager.watch_once()
    assert fixture.persisted()['next_check']==0
    assert fixture.persisted()['local_retry_after']==fixture.clock.now+60
    fixture.catalog.assert_not_awaited();fixture.inventory.assert_not_awaited();assert not fixture.posts


@pytest.mark.asyncio
async def test_uncertain_issued_post_preserves_durable_daily_and_weekly_guards_across_restart(watch_fixture,tmp_path,monkeypatch):
    from app import source_engine as engine_module
    fixture=watch_fixture
    engine=tmp_path/'engine';extensions=engine/'extensions';extensions.mkdir(parents=True)
    binary=b'controlled installed baseline '*20
    (extensions/'fixture.jar').write_bytes(binary)
    monkeypatch.setattr(engine_module,'ENGINE_DATA',engine)
    pkg='eu.kanade.tachiyomi.extension.en.fixture'
    extension={'pkgName':pkg,'apkName':'fixture.apk','isInstalled':True,'isObsolete':True,
               'source':{'nodes':[{'id':'22','name':'Fixture','lang':'en'}]}}
    async def catalog(**kwargs):
        assert fixture.persisted()['next_check']==fixture.clock.now+DAY
        return {'extensions':[extension]}
    fixture.catalog.side_effect=catalog
    fixture.inventory.return_value=[extension]
    fixture.post_error=httpx.ReadTimeout
    issued_at=fixture.clock.now
    with pytest.raises(HTTPException):await fixture.manager.watch_once()
    assert len(fixture.posts)==1
    saved=fixture.persisted()
    assert saved['next_check']==issued_at+DAY and saved['attempts'][pkg]==issued_at
    assert (settings.data_dir/'source-maintenance/backups'/module.sha(binary)/'installed.jar').read_bytes()==binary
    fixture.clock.now+=60
    await fixture.restart().watch_once();assert len(fixture.posts)==1
    # Even an empty helper status after a lost acknowledgement cannot replay next day.
    fixture.clock.now=issued_at+DAY+1
    await fixture.restart().watch_once();assert len(fixture.posts)==1
    assert fixture.persisted()['attempts'][pkg]==issued_at
    # A fresh weekly window admits one new job, with the same pre-POST persistence.
    fixture.clock.now=issued_at+WEEK+1;fixture.post_error=None
    await fixture.restart().watch_once();assert len(fixture.posts)==2
    assert fixture.persisted()['attempts'][pkg]==fixture.clock.now
