import asyncio
import importlib
from pathlib import Path
import sys
from types import SimpleNamespace

import pytest

PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT/'repair-worker'))
sys.path.insert(0, str(PROJECT/'autosourcarr'))


def test_account_refuses_after_repair_teardown_uncertainty(tmp_path):
    from internal_runner import InternalRunner, InternalControl
    runner = InternalRunner(tmp_path/'repair')
    try:
        runner.cleanup_failed = True
        runner.probe = lambda: {'available': True, 'message': 'mock sandbox available'}
        account = InternalControl(runner)
        with pytest.raises(RuntimeError, match='(?i)(stop|restart|cleanup)'):
            asyncio.run(account.account_command('logout'))
    finally:
        runner.socket_temp.cleanup()


def test_helper_stops_descendants_before_waiting_for_pipe_eof(monkeypatch):
    from app import internal_services as module

    async def scenario():
        manager = module.InternalServices()
        drained = asyncio.Event()
        stopped = asyncio.Event()
        leader = SimpleNamespace(returncode=2, pid=1234)
        manager.environment = lambda name: {}
        manager.command = lambda name, env: (['fixture'], 'http://unused/')
        async def spawn(*args): return leader
        async def drain(process):
            await drained.wait()
            return 2
        async def stop(process, *args, **kwargs):
            stopped.set()
            drained.set()
        class Client:
            def __init__(self, **kwargs): pass
            async def __aenter__(self): return self
            async def __aexit__(self, *args): pass
        manager._spawn = spawn
        manager._drain = drain
        monkeypatch.setattr(module, 'stop_group', stop)
        monkeypatch.setattr(module.httpx, 'AsyncClient', Client)
        task = asyncio.create_task(manager._serve('solver'))
        try:
            # Simulates a dead helper leader with a descendant retaining stdout:
            # only terminating its owned process group makes the drain complete.
            try:
                await asyncio.wait_for(stopped.wait(), .1)
            except asyncio.TimeoutError:
                raise AssertionError('Helper waits forever for pipe EOF before stopping its surviving descendants')
        finally:
            manager.stopping = True
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)
    asyncio.run(scenario())


def test_real_ui_state_and_settings_contracts():
    import subprocess
    result=subprocess.run(['node',str(Path(__file__).with_name('ui_v0317.js'))],capture_output=True,text=True,timeout=20)
    assert result.returncode==0,result.stdout+result.stderr


def test_internal_credentials_are_private_and_persistent(tmp_path,monkeypatch):
    from app.internal_services import InternalServices,settings
    monkeypatch.setattr(settings,'app_services_mode','integrated')
    monkeypatch.setattr(settings,'data_dir',tmp_path)
    monkeypatch.setattr(settings,'repair_worker_url','http://old:8766')
    monkeypatch.setattr(settings,'repair_worker_token','old'*20)
    service=InternalServices();service.configure();first=settings.repair_worker_token
    assert settings.repair_worker_url=='http://127.0.0.1:8766'
    assert len(first)==64 and (tmp_path/'tools/repair-token').stat().st_mode&0o777==0o600
    service.configure();assert settings.repair_worker_token==first
    assert first not in str(service.snapshot())


def test_saved_external_connection_cannot_replace_internal_connection(tmp_path,monkeypatch):
    import json
    from app import repair_connection as c
    monkeypatch.setattr(c.settings,'app_services_mode','integrated')
    monkeypatch.setattr(c.settings,'settings_config_dir',tmp_path)
    monkeypatch.setattr(c.settings,'repair_worker_url','http://127.0.0.1:8766')
    monkeypatch.setattr(c.settings,'repair_worker_token','a'*64)
    (tmp_path/'repair-connection.json').write_text(json.dumps({'url':'http://old:8766','token':'b'*64}))
    c.restore_connection()
    assert c.settings.repair_worker_url=='http://127.0.0.1:8766'
    assert c.settings.repair_worker_token=='a'*64


def test_workspace_retains_pin_and_separates_instances(tmp_path,monkeypatch):
    from app import internal_services as m
    tools=tmp_path/'bundle';(tools/'build/extension').mkdir(parents=True)
    (tools/'build/extension/keiyoushi-commit.lock').write_text('bundled')
    (tools/'build/VERSION').write_text('new')
    monkeypatch.setattr(m,'TOOLS',tools)
    monkeypatch.setattr(m.settings,'data_dir',tmp_path/'one')
    one=m.InternalServices();first=one._prepare_workspace()
    (first/'extension/keiyoushi-commit.lock').write_text('retained')
    one._prepare_workspace()
    assert (first/'extension/keiyoushi-commit.lock').read_text()=='retained'
    monkeypatch.setattr(m.settings,'data_dir',tmp_path/'two')
    second=m.InternalServices()._prepare_workspace()
    assert second!=first and (second/'extension/keiyoushi-commit.lock').read_text()=='bundled'


def test_repeated_cancel_does_not_abandon_owned_cleanup():
    from app.internal_services import drain_owned
    async def scenario():
        entered=asyncio.Event();release=asyncio.Event();done=[]
        async def cleanup():
            entered.set();await release.wait();done.append(True)
        task=asyncio.create_task(drain_owned(cleanup()))
        await entered.wait();task.cancel();await asyncio.sleep(0);task.cancel();await asyncio.sleep(0)
        assert not task.done()
        release.set();await task;assert done==[True]
    asyncio.run(scenario())


def test_duplicate_builder_requests_coalesce(monkeypatch):
    from app import internal_services as m
    monkeypatch.setattr(m.settings,'app_services_mode','integrated')
    monkeypatch.setattr(m.settings,'autosourcarr_reader_api_token','a'*32)
    monkeypatch.setattr(m.settings,'autosourcarr_public_url','https://reader.example.invalid')
    async def scenario():
        manager=m.InternalServices();release=asyncio.Event();calls=[]
        async def build(): calls.append(True);await release.wait()
        manager._build=build
        manager.build();manager.build();await asyncio.sleep(0)
        assert calls==[True]
        release.set();await manager.tasks['builder']
    asyncio.run(scenario())


def test_bundled_source_tree_is_current():
    import subprocess
    result=subprocess.run([sys.executable,str(PROJECT/'scripts/sync-bundled-tools.py'),'--check'],capture_output=True,text=True)
    assert result.returncode==0,result.stdout+result.stderr


def test_default_compose_has_no_helper_containers():
    import yaml
    for name,expected in [('docker-compose.yml',{'autosourcarr','suwayomi'}),('docker-compose.two-instances.yml',{'autosourcarr','private-autosourcarr','suwayomi','private-suwayomi'})]:
        services=yaml.safe_load((PROJECT/name).read_text())['services']
        assert set(services)==expected
        for key,service in services.items():
            if 'autosourcarr' not in key:continue
            assert service['environment']['APP_SERVICES_MODE']=='integrated'
            assert '/var/run/docker.sock' not in str(service)
            image_users=[line.split(None,1)[1] for line in (PROJECT/'autosourcarr/Dockerfile').read_text().splitlines() if line.startswith('USER ')]
            assert not service.get('privileged') and service.get('user',image_users[-1])=='568:568'
            assert service['ports'][0].endswith(':8282')
        if 'private-autosourcarr' in services:
            assert services['autosourcarr']['volumes']!=services['private-autosourcarr']['volumes']


def test_sandbox_mounts_only_isolated_job_capabilities(tmp_path):
    from internal_sandbox import Sandbox
    for name in ('code','work','sockets','auth','profile'):(tmp_path/name).mkdir()
    args=Sandbox(tmp_path/'code',tool_roots=()).command(tmp_path/'work',['true'],sockets=tmp_path/'sockets',auth=tmp_path/'auth')
    for flag in ('--unshare-user','--unshare-net','--unshare-pid','--cap-drop','--die-with-parent'):assert flag in args
    assert '--share-net' not in args and '--ro-bind' in args
    assert '/config' not in args and '/data' not in args and '/var/run/docker.sock' not in args
    assert str(tmp_path/'work') in args and str(tmp_path/'auth') in args
    assert args[args.index('--chdir')+1]=='/work'


def test_lifetime_witness_waits_for_inherited_writer():
    import os,subprocess
    from internal_sandbox import SyncPipe
    with SyncPipe() as witness:
        process=subprocess.Popen([sys.executable,'-c','import time;time.sleep(.15)'],pass_fds=witness.pass_fds)
        witness.spawned()
        with pytest.raises(RuntimeError,match='confirmed stopped'):witness.wait_eof(.02)
        process.wait();witness.wait_eof(.2)


def test_jar_publication_is_version_bound_and_detects_tampering(tmp_path):
    import importlib.util
    from app.extension_download import jar_status,jar_bytes,ApkUnavailable
    spec=importlib.util.spec_from_file_location('jar_publisher',PROJECT/'extension/publish-jar.py')
    publisher=importlib.util.module_from_spec(spec);spec.loader.exec_module(publisher)
    source=tmp_path/'test.jar';source.write_bytes(b'fixture-native-jar')
    publisher.publish(source,tmp_path/'config','0.3.17')
    assert jar_status(tmp_path/'config','0.3.17')['available']
    assert not jar_status(tmp_path/'config','0.3.16')['available']
    assert jar_bytes(tmp_path/'config','0.3.17')==source.read_bytes()
    artifact=next((tmp_path/'config/extension-downloads').glob('*.jar'))
    artifact.write_bytes(b'X'*len(source.read_bytes()))
    with pytest.raises(ApkUnavailable,match='integrity'):jar_bytes(tmp_path/'config','0.3.17')


def test_public_proxy_preserves_response_after_request_half_close(monkeypatch):
    import egress

    class Writer:
        def __init__(self): self.data=bytearray();self.closed=False;self.eof=False
        def write(self, data): self.data.extend(data)
        async def drain(self): pass
        def write_eof(self): self.eof=True
        def close(self): self.closed=True
        async def wait_closed(self): pass

    async def scenario():
        request=asyncio.StreamReader()
        request.feed_data(b'GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n')
        request.feed_eof()
        response=asyncio.StreamReader()
        upstream=Writer(); client=Writer()
        async def connect(*args,**kwargs): return response,upstream
        async def delayed_response():
            await asyncio.sleep(.03)
            response.feed_data(b'HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK')
            response.feed_eof()
        monkeypatch.setattr(egress,'public_address',lambda host,port:'93.184.216.34')
        monkeypatch.setattr(egress.asyncio,'open_connection',connect)
        delayed=asyncio.create_task(delayed_response())
        await egress.handle(request,client)
        await delayed
        assert bytes(client.data).endswith(b'\r\n\r\nOK'), 'Proxy truncated the delayed response after client request EOF'
    asyncio.run(scenario())


@pytest.mark.parametrize('stop_early', [False, True])
def test_helper_supervisor_reaps_detached_children(tmp_path, stop_early):
    import os, subprocess, time
    from app import child_process
    probe=subprocess.Popen([sys.executable,'-c','import time;time.sleep(2)'])
    try:
        try:parent=int(Path(f'/proc/{probe.pid}/stat').read_text().rsplit(')',1)[1].split()[1])
        except FileNotFoundError:pytest.skip('Executor child PID is absent from /proc')
        if parent!=os.getpid():pytest.skip('Executor virtualizes PIDs without a matching /proc tree')
    finally:probe.terminate();probe.wait()
    marker=tmp_path/'pid'
    child="import os,time;from pathlib import Path;Path("+repr(str(marker))+").write_text(str(os.getpid()));time.sleep(30)"
    leader="import subprocess,sys,time;subprocess.Popen([sys.executable,'-c',"+repr(child)+"],start_new_session=True);time.sleep("+('30' if stop_early else '0.3')+")"
    process=subprocess.Popen([sys.executable,child_process.__file__,sys.executable,'-c',leader],start_new_session=True)
    descendant=None
    try:
        deadline=time.monotonic()+5
        while not marker.exists() and time.monotonic()<deadline:time.sleep(.02)
        assert marker.exists(), 'Owned descendant did not start'
        descendant=int(marker.read_text())
        if stop_early:process.terminate()
        process.wait(timeout=10)
        assert not Path(f'/proc/{descendant}').exists(), 'Detached child survived helper completion/cancellation'
    finally:
        if process.poll() is None:process.kill();process.wait()
        if descendant:
            try:os.kill(descendant,9)
            except ProcessLookupError:pass


def test_process_tree_scope_excludes_other_helpers(tmp_path, monkeypatch):
    from app import child_process as module
    # /proc fields after comm: state, ppid, ... starttime at offset 19.
    for pid,parent in [(101,50),(102,101),(201,50),(202,201),(103,101)]:
        directory=tmp_path/str(pid);directory.mkdir()
        fields=['S',str(parent)]+['0']*17+[str(pid*10)]
        (directory/'stat').write_text(f'{pid} (helper with spaces) '+ ' '.join(fields))
    real_path=Path
    monkeypatch.setattr(module,'Path',lambda value:tmp_path if value=='/proc' else real_path(value))
    assert module.children(101)=={102:'1020',103:'1030'}
    assert 201 not in module.children(101) and 202 not in module.children(101)


def test_integrated_translation_preserves_explicit_disable(monkeypatch):
    from app.translation import TranslationClient, settings
    monkeypatch.setattr(settings, 'app_services_mode', 'integrated')
    monkeypatch.setattr(settings, 'translation_api_url', '')
    disabled=TranslationClient()
    assert not disabled.enabled and disabled.client is None
    monkeypatch.setattr(settings, 'translation_api_url', 'http://obsolete-translator:5000')
    enabled=TranslationClient()
    assert enabled.base_url=='http://127.0.0.1:5000'
    asyncio.run(enabled.close())


def test_service_actions_require_admin_and_same_origin(monkeypatch):
    from fastapi import FastAPI, Header, HTTPException
    from fastapi.testclient import TestClient
    from app import internal_services as module
    def guard(x_admin_token: str=Header(default='')):
        if x_admin_token!='fixture':raise HTTPException(401,'Unauthorized')
    app=FastAPI();app.include_router(module.services_router(guard))
    calls=[]
    async def action(name, verb):calls.append((name,verb));return {'mode':'integrated','services':{}}
    monkeypatch.setattr(module.internal_services,'action',action)
    client=TestClient(app)
    assert client.post('/api/admin/services/builder/start').status_code==401
    assert client.post('/api/admin/services/builder/start',headers={'X-Admin-Token':'fixture','Origin':'https://unrelated.invalid'}).status_code==403
    response=client.post('/api/admin/services/builder/start',headers={'X-Admin-Token':'fixture','Origin':'http://testserver'})
    assert response.status_code==202 and response.headers['cache-control']=='no-store'
    assert calls==[('builder','start')]


def test_uncertain_helper_cleanup_blocks_reuse(monkeypatch):
    from app import internal_services as module
    monkeypatch.setattr(module.settings,'app_services_mode','integrated')
    async def scenario():
        manager=module.InternalServices()
        process=SimpleNamespace(returncode=75)
        assert await manager._stop('builder',process) is False
        with pytest.raises(ValueError,match='cleanup'):manager.build()
        assert manager.snapshot()['services']['builder']['stage']=='cleanup'
        assert await manager._stop('solver',process) is False
        assert manager.ensure('solver') is False
        assert not manager.tasks
    asyncio.run(scenario())


def test_exited_supervisor_does_not_signal_reused_process_group():
    from app.internal_services import stop_group
    process=SimpleNamespace(returncode=0,pid=1)
    # There are intentionally no terminate/kill/wait methods on an exited handle.
    asyncio.run(stop_group(process))


def test_descendant_signals_use_bound_identity(tmp_path,monkeypatch):
    from app import child_process as module
    directory=tmp_path/'102';directory.mkdir()
    (directory/'stat').write_text('102 (child) '+' '.join(['S','101']+['0']*17+['123']))
    monkeypatch.setattr(module,'children',lambda root:{102:'123'})
    real_path=Path
    monkeypatch.setattr(module,'Path',lambda value:directory/'stat' if value=='/proc/102/stat' else real_path(value))
    calls=[]
    monkeypatch.setattr(module.os,'pidfd_open',lambda pid:calls.append(('open',pid)) or 9)
    monkeypatch.setattr(module.os,'close',lambda fd:calls.append(('close',fd)))
    monkeypatch.setattr(module.signal,'pidfd_send_signal',lambda fd,sig:calls.append(('signal',fd,sig)))
    module.signal_children(15)
    assert calls==[('open',102),('signal',9,15),('close',9)]


@pytest.mark.parametrize('code', [-9, -11, 75])
def test_abnormal_supervisor_exit_cannot_authorize_reuse(code):
    from app.internal_services import stop_group,CleanupUncertain
    with pytest.raises(CleanupUncertain):asyncio.run(stop_group(SimpleNamespace(returncode=code)))


def test_forced_stop_does_not_wait_for_descendant_pipe_eof():
    from app.internal_services import stop_group,CleanupUncertain
    class Process:
        returncode=None
        def terminate(self):pass
        def kill(self):self.returncode=-9
        async def wait(self):await asyncio.Event().wait()
    async def scenario():
        with pytest.raises(CleanupUncertain):
            await asyncio.wait_for(stop_group(Process(),grace=.01),.2)
    asyncio.run(scenario())
