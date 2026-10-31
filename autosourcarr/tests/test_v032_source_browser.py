"""Browser maintenance owns the JVM before touching its retained profile."""
import asyncio
import json
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

import httpx
import pytest
from app import source_browser as browser, source_engine as engine
from app.config import settings
from app.suwayomi import SuwayomiClient, SourceEngineUnavailable


def test_preferences_roundtrip_reset_and_environment_cannot_inject_java_options(tmp_path):
    ua='Mozilla/5.0 custom -javaagent:/evil.jar $(no-shell)'
    prefs=browser.validate_preferences('custom',ua)
    browser.save_preferences(prefs,tmp_path)
    assert browser.read_preferences(tmp_path)==prefs
    env=browser.user_agent_environment(tmp_path,tmp_path/'data')
    assert env['JAVA_TOOL_OPTIONS']=='-javaagent:/app/source-browser-agent.jar'
    assert env['AUTOSOURCARR_SOURCE_USER_AGENT']==ua
    assert len(env['AUTOSOURCARR_SOURCE_UA_REVISION'])==64
    browser.save_preferences(browser.validate_preferences('default','ignored'),tmp_path)
    assert browser.user_agent_environment(tmp_path,tmp_path/'data')=={}
    assert browser.read_preferences(tmp_path)=={'preset':'default','user_agent':''}


@pytest.mark.parametrize('value',['','x\r\nInjected: yes','\nx','x\t','é','x'*1025])
def test_invalid_custom_headers_are_rejected(value):
    with pytest.raises(ValueError):browser.validate_preferences('custom',value)


def test_fixed_presets_are_authoritative():
    assert browser.validate_preferences('chrome-windows','ignored')['user_agent']==browser.DEFAULT_UA
    with pytest.raises(ValueError):browser.validate_preferences('nonexistent','ua')


def profile(root):
    files=['settings/cookie_store.xml','cache/kcef/Cookies','cache/kcef/Local Storage/data',
           'settings/server.conf','settings/extensions.xml','bin/kcef/chrome','cache/other-cache',
           'database.mv.db','extensions/source.apk','downloads/chapter.zip']
    for name in files:
        p=root/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(name.encode())
    return files


def test_clear_preserves_every_non_browser_file(tmp_path):
    files=profile(tmp_path)
    assert browser.clear_local_browser_data(tmp_path)==['settings/cookie_store.xml','cache/kcef']
    for name in files:
        p=tmp_path/name
        if name=='settings/cookie_store.xml' or name.startswith('cache/kcef/'):assert not p.exists()
        else:assert p.read_bytes()==name.encode()
    browser.clear_local_browser_data(tmp_path)  # repeat with no profile is harmless


def test_clear_validates_all_targets_before_deleting_any(tmp_path):
    (tmp_path/'settings').mkdir();cookie=tmp_path/'settings/cookie_store.xml';cookie.write_text('retained')
    outside=tmp_path/'other';outside.mkdir();(outside/'important').write_text('safe')
    (tmp_path/'cache').mkdir();(tmp_path/'cache/kcef').symlink_to(outside, target_is_directory=True)
    with pytest.raises(ValueError):browser.clear_local_browser_data(tmp_path)
    assert cookie.read_text()=='retained' and (outside/'important').read_text()=='safe'


@pytest.fixture
def managed(tmp_path,monkeypatch):
    monkeypatch.setattr(settings,'source_engine_mode','integrated')
    monkeypatch.setattr(settings,'settings_config_dir',tmp_path/'config')
    command=tmp_path/'engine';command.touch()
    monkeypatch.setattr(engine,'ENGINE_COMMAND',command)
    monkeypatch.setattr(engine,'ENGINE_DATA',tmp_path/'source');(tmp_path/'source').mkdir()
    calls=[]
    runtime=SimpleNamespace(child=object(),state='ready',challenges={})
    async def stop():calls.append('stop');runtime.child=None;runtime.state='stopped'
    def start():calls.append('start');runtime.state='ready'
    runtime.stop=stop;runtime.start=start
    monkeypatch.setattr(engine,'source_engine',runtime)
    manager=browser.SourceBrowser()
    manager._assert_runtime_absent=AsyncMock()
    return manager,runtime,calls


@pytest.mark.asyncio
async def test_clear_stops_before_solver_and_local_cookie_reset(managed,monkeypatch):
    manager,runtime,calls=managed
    async def configuration(client):calls.append('read-solver');return {'session':'owned'}
    async def solver(values):assert runtime.child is None;calls.append('clear-solver')
    manager._solver_configuration=configuration;manager._clear_solver_session=solver
    original=browser.clear_local_browser_data
    def clear(path):assert runtime.child is None;calls.append('clear-local');return original(path)
    monkeypatch.setattr(browser,'clear_local_browser_data',clear)
    from app.source_webview import sessions
    sessions.sessions['old-session']={}
    lock=asyncio.Lock();await lock.acquire()
    manager.queue('clear-data',lock)
    assert manager.state=='queued' and calls==[]
    with pytest.raises(ValueError):manager.queue('clear-data',lock)
    lock.release();await manager.task
    assert calls==['read-solver','stop','clear-solver','clear-local','start']
    assert manager.state=='completed' and not manager.busy and not sessions.sessions
    assert manager.result['warnings']==[]


@pytest.mark.asyncio
async def test_clear_solver_failure_is_partial_and_engine_recovers(managed):
    manager,_,calls=managed
    manager._solver_configuration=AsyncMock(return_value={'session':'owned'})
    manager._clear_solver_session=AsyncMock(side_effect=httpx.ConnectError('offline'))
    manager.queue('clear-data',asyncio.Lock());await manager.task
    assert manager.state=='completed'
    assert len(manager.result['warnings'])==1 and 'solver' in manager.result['warnings'][0]
    assert calls==['stop','start']


@pytest.mark.asyncio
async def test_failed_clear_restarts_engine_and_reports_failure(managed,monkeypatch):
    manager,_,calls=managed
    manager._solver_configuration=AsyncMock(return_value={})
    def fail(path):raise OSError('read-only profile')
    monkeypatch.setattr(browser,'clear_local_browser_data',fail)
    manager.queue('clear-data',asyncio.Lock());await manager.task
    assert manager.state=='failed' and manager.error=='read-only profile'
    assert calls==['stop','start'] and not manager.busy


@pytest.mark.asyncio
async def test_shutdown_drains_engine_stop_before_cancelling_action(managed):
    manager,runtime,calls=managed
    manager._solver_configuration=AsyncMock(return_value={})
    started,release=asyncio.Event(),asyncio.Event()
    async def stop():
        calls.append('stop');started.set();await release.wait();runtime.child=None;calls.append('stopped')
    runtime.stop=stop
    manager.queue('clear-data',asyncio.Lock());await started.wait()
    shutdown=asyncio.create_task(manager.stop());await asyncio.sleep(0)
    manager.task.cancel();await asyncio.sleep(0)
    assert not shutdown.done() and runtime.child is not None
    release.set();await shutdown
    assert calls==['stop','stopped'] and manager.state=='cancelled' and not manager.busy


@pytest.mark.asyncio
async def test_user_agent_reset_is_saved_and_restart_owned(managed):
    manager,_,calls=managed
    browser.save_preferences({'preset':'custom','user_agent':'old'})
    manager.queue('user-agent',asyncio.Lock(),preferences=browser.validate_preferences('default',''))
    await manager.task
    assert browser.read_preferences()['preset']=='default'
    assert manager.state=='completed' and calls==['stop','start']


@pytest.mark.asyncio
async def test_missing_agent_does_not_save_or_stop_runtime(managed,monkeypatch):
    manager,_,calls=managed
    original=Path.is_file
    monkeypatch.setattr(Path,'is_file',lambda p:False if str(p)=='/app/source-browser-agent.jar' else original(p))
    manager.queue('user-agent',asyncio.Lock(),preferences=browser.validate_preferences('custom','UA'))
    await manager.task
    assert manager.state=='failed' and 'lacks user-agent support' in manager.error
    assert calls==[] and browser.read_preferences()['preset']=='default'


@pytest.mark.asyncio
async def test_solver_only_destroys_owned_named_session(monkeypatch):
    seen=[]
    def response(request):seen.append(json.loads(request.content));return httpx.Response(200,json={'status':'ok'})
    original=httpx.AsyncClient
    monkeypatch.setattr(browser.httpx,'AsyncClient',lambda **kwargs:original(transport=httpx.MockTransport(response),**kwargs))
    manager=browser.SourceBrowser()
    await manager._clear_solver_session({'flareSolverrUrl':'http://solver.test','flareSolverrSessionName':'owned'})
    await manager._clear_solver_session({'flareSolverrUrl':'http://solver.test','flareSolverrSessionName':''})
    assert seen==[{'cmd':'sessions.destroy','session':'owned'}]


@pytest.mark.asyncio
async def test_maintenance_defers_new_and_interrupted_work_without_source_failure(monkeypatch):
    monkeypatch.setattr(settings,'source_engine_mode','integrated')
    monkeypatch.setattr(browser.source_browser,'busy',False)
    monkeypatch.setattr(engine.source_engine,'state','ready')
    client=SuwayomiClient('http://source.invalid',managed_engine=True)
    attention=SimpleNamespace(context=AsyncMock(return_value={}),check=AsyncMock(),failed=AsyncMock())
    client.attention=attention
    async def interrupted():
        browser.source_browser.busy=True
        raise httpx.ConnectError('process restarted')
    try:
        with pytest.raises(SourceEngineUnavailable):await client._run_source_work(interrupted,{'kind':'pages','id':1})
        attention.failed.assert_not_called()
        factory=AsyncMock()
        with pytest.raises(SourceEngineUnavailable):await client._run_source_work(factory)
        factory.assert_not_called()
        # Direct external image requests are not blocked by engine maintenance.
        await client._check_engine_request(httpx.Request('GET','https://cdn.test/cover.jpg'))
    finally:await client.close()

@pytest.mark.asyncio
async def test_unowned_runtime_blocks_clearing_before_any_mutation(managed):
    manager,_,calls=managed
    manager._solver_configuration=AsyncMock(return_value={})
    manager._assert_runtime_absent=AsyncMock(side_effect=RuntimeError('Another process is still listening'))
    manager._clear_solver_session=AsyncMock()
    files=profile(engine.ENGINE_DATA)
    manager.queue('clear-data',asyncio.Lock());await manager.task
    assert manager.state=='failed' and 'Another process' in manager.error
    manager._clear_solver_session.assert_not_called()
    assert calls==['stop','start']
    assert all((engine.ENGINE_DATA/name).exists() for name in files)


@pytest.mark.asyncio
async def test_runtime_absence_probe_fails_closed(monkeypatch):
    manager=browser.SourceBrowser()
    writer=SimpleNamespace(close=lambda:None,wait_closed=AsyncMock())
    monkeypatch.setattr(asyncio,'open_connection',AsyncMock(return_value=(None,writer)))
    with pytest.raises(RuntimeError,match='still listening'):await manager._assert_runtime_absent()
    for exc in [TimeoutError(),OSError('uncertain')]:
        monkeypatch.setattr(asyncio,'open_connection',AsyncMock(side_effect=exc))
        with pytest.raises(RuntimeError,match='Could not confirm'):await manager._assert_runtime_absent()
    monkeypatch.setattr(asyncio,'open_connection',AsyncMock(side_effect=ConnectionRefusedError()))
    await manager._assert_runtime_absent()


@pytest.mark.asyncio
async def test_admin_api_authorizes_validates_and_acknowledges_without_waiting(monkeypatch):
    from app.main import app
    from app import main
    monkeypatch.setattr(settings,'admin_token','secret')
    queued=[]
    fake=SimpleNamespace(queue=lambda *args,**kwargs:queued.append((args,kwargs)),snapshot=AsyncMock(return_value={'busy':False}))
    monkeypatch.setattr(main,'source_browser',fake)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://test') as client:
        for method,path in [('GET',''),('PUT','/user-agent'),('POST','/clear-data')]:
            r=await client.request(method,'/api/admin/source-browser'+path,json={'preset':'default'})
            assert r.status_code==401
        assert not queued
        headers={'X-Admin-Token':'secret'}
        r=await client.put('/api/admin/source-browser/user-agent',headers=headers,json={'preset':'custom','user_agent':'UA\nInjected'})
        assert r.status_code==400 and not queued
        r=await client.put('/api/admin/source-browser/user-agent',headers=headers,json={'preset':'chrome-windows','user_agent':'ignore'})
        assert r.status_code==202 and r.json()['queued']
        assert queued[-1][1]['preferences']['user_agent']==browser.DEFAULT_UA
        assert (await client.post('/api/admin/source-browser/clear-data',headers=headers)).status_code==202
        assert queued[-1][0][0]=='clear-data'
