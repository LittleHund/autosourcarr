"""Connection drafts, isolation capabilities and credential boundaries."""
import asyncio
from contextlib import contextmanager
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import socket
import sys
import threading
import uuid

import httpx
import pytest

from app.config import Settings
from app import setup_store as store

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'repair-worker'))
import ai_provider


def save(settings, changes):
    root=settings.settings_config_dir
    return store.finish(root,settings,changes,store.snapshot(root,settings)['revision'],str(uuid.uuid4()))


def test_connections_save_redact_retain_remove_and_restart(tmp_path):
    settings=Settings(settings_config_dir=tmp_path,app_services_mode='integrated',
        comicvine_api_key='comic-secret', main_suwayomi_password=' untouched ')
    save(settings,{'REPAIR_AI_PROVIDER':'custom','REPAIR_AI_BASE_URL':'http://model:11434/v1',
        'REPAIR_AI_MODEL':'local-coder','REPAIR_AI_API_KEY':'private-model-key',
        'SUWAYOMI_BROWSER_URL':'http://nas:30058','ADMIN_TOKEN':'admin-secret',
        'TRANSLATION_MODE':'external','TRANSLATION_API_URL':'http://translation:5000'})
    view=json.dumps(store.snapshot(tmp_path,settings))
    for secret in ['private-model-key','comic-secret','admin-secret',' untouched ']: assert secret not in view
    boot=store.startup_overrides(tmp_path)
    assert boot['repair_ai_provider']=='custom' and boot['repair_ai_api_key']=='private-model-key'
    assert boot['comicvine_api_key']=='comic-secret'
    assert boot['main_suwayomi_password']==' untouched '
    running=Settings(settings_config_dir=tmp_path, app_services_mode='integrated',**boot)
    assert not store.snapshot(tmp_path,running)['restart_required']
    save(running,{'COMICVINE_API_KEY':''})
    assert store.startup_overrides(tmp_path)['comicvine_api_key']==''
    assert (tmp_path/'autosourcarr.env').stat().st_mode&0o777==0o600


@pytest.mark.parametrize('changes',[
    {'REPAIR_AI_PROVIDER':'unknown'},
    {'REPAIR_AI_PROVIDER':'custom'},
    {'REPAIR_AI_MODEL':'x'*257},
    {'REPAIR_AI_API_KEY':'no-\u2603'},
    {'REPAIR_AI_BASE_URL':'http://host/v1/%2e%2e/admin'},
    {'REPAIR_AI_BASE_URL':'http://user:pass@host/v1'},
    {'KOMGA_BROWSER_URL':'javascript:alert(1)'},
    {'TRANSLATION_MODE':'external','TRANSLATION_API_URL':''},
    {'MAIN_SUWAYOMI_USERNAME':'bad:name'},
])
def test_invalid_connections_do_not_save(tmp_path,changes):
    settings=Settings(settings_config_dir=tmp_path,app_services_mode='integrated')
    with pytest.raises(ValueError): save(settings,changes)
    assert not (tmp_path/'setup-state.json').exists()


def test_changed_ai_destination_cannot_reuse_key_silently(tmp_path):
    settings=Settings(settings_config_dir=tmp_path,app_services_mode='integrated',
        repair_ai_base_url='http://old/v1',repair_ai_api_key='old-key')
    with pytest.raises(ValueError,match='explicitly replace or remove'):
        save(settings,{'REPAIR_AI_BASE_URL':'http://new/v1'})
    save(settings,{'REPAIR_AI_BASE_URL':'http://new/v1','REPAIR_AI_API_KEY':''})
    assert store.startup_overrides(tmp_path)['repair_ai_api_key']==''


def test_custom_ai_not_claimed_for_legacy_external_worker(tmp_path):
    settings=Settings(settings_config_dir=tmp_path)
    with pytest.raises(ValueError,match='integrated'):
        save(settings,{'REPAIR_AI_PROVIDER':'custom','REPAIR_AI_BASE_URL':'http://model/v1','REPAIR_AI_MODEL':'coder'})


def test_translation_modes_use_selected_server_without_internal_start(monkeypatch):
    from app import translation as module
    monkeypatch.setattr(module.settings,'app_services_mode','integrated')
    monkeypatch.setattr(module.settings,'translation_api_url','http://custom:5000')
    for mode,expected in [('auto','http://127.0.0.1:5000'),('external','http://custom:5000'),('builtin','http://127.0.0.1:5000'),('off','')]:
        monkeypatch.setattr(module.settings,'translation_mode',mode)
        client=module.TranslationClient()
        try: assert client.base_url==expected
        finally: asyncio.run(client.close())


def test_main_suwayomi_auth_never_reaches_other_origins():
    from app.suwayomi import SuwayomiClient
    async def scenario():
        client=SuwayomiClient('https://main:30058',credentials=('alice',' pass '))
        try:
            for url,wanted in [('https://main:30058/api/graphql',True),('http://main:30058/api/graphql',False),('https://source.example/cover.jpg',False)]:
                request=httpx.Request('GET',url)
                await client._check_engine_request(request)
                assert ('Authorization' in request.headers)==wanted
        finally: await client.close()
    asyncio.run(scenario())


@pytest.fixture
def model_server():
    records=[]
    class Handler(BaseHTTPRequestHandler):
        def log_message(self,*args): pass
        def do_POST(self):
            body=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            records.append((self.path,dict(self.headers),body))
            result={'object':'response','output':[{'type':'message','content':[{'type':'output_text','text':'OK'}]}]}
            if body.get('tools'):
                outputs=[item for item in body.get('input',[]) if isinstance(item,dict) and item.get('type')=='function_call_output']
                result['output']=[{'type':'message','content':[{'type':'output_text','text':outputs[0]['output']}]}] if outputs else [{'type':'function_call','call_id':'probe-call','name':body['tools'][0]['name'],'arguments':'{}'}]
            raw=json.dumps(result).encode()
            status=302 if body.get('redirect_test') else 401 if body.get('error_test') else 200
            if status!=200: raw=b'provider-private-key'
            self.send_response(status);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(raw)))
            self.send_header('Location','http://127.0.0.1:1/leak');self.end_headers();self.wfile.write(raw)
    server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
    thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
    try: yield {'provider':'custom','base_url':f'http://127.0.0.1:{server.server_port}/v1','model':'selected-model','api_key':'provider-private-key'},records
    finally: server.shutdown();server.server_close();thread.join(timeout=2)


def relay_request(path,route='/v1/responses',body=None,headers=None):
    connection=http.client.HTTPConnection('relay',timeout=5)
    stream=socket.socket(socket.AF_INET if isinstance(path,tuple) else socket.AF_UNIX,socket.SOCK_STREAM);stream.settimeout(5);stream.connect(path if isinstance(path,tuple) else str(path));connection.sock=stream
    try:
        connection.request('POST',route,json.dumps(body or {'model':'untrusted-model','input':'OK'}),headers or {})
        response=connection.getresponse();return response.status,response.read()
    finally: connection.close()


@contextmanager
def tcp_relay(config):
    """Same production handler/lifecycle; TCP harness for Unix-restricted CI."""
    class LocalRelay(ai_provider.Relay):
        address_family=socket.AF_INET
        def server_bind(self):
            self.server_address=('127.0.0.1',0)
            super().server_bind()
    server=LocalRelay('fixture',ai_provider.Target(config))
    thread=threading.Thread(target=server.serve_forever,kwargs={'poll_interval':.05},daemon=True);thread.start()
    try: yield server.server_address
    finally: server.stop();thread.join(timeout=2)


def test_fixed_target_relay_request_handling_over_tcp_fixture(model_server):
    config,records=model_server
    with tcp_relay(config) as path:
        status,body=relay_request(path,headers={'Authorization':'Bearer injected','Cookie':'private-cookie','X-Forwarded-Host':'other-host'})
        assert status==200 and b'provider-private-key' not in body
        target,headers,payload=records[-1]
        assert target=='/v1/responses' and payload['model']=='selected-model'
        assert headers['Authorization']=='Bearer provider-private-key'
        assert 'Cookie' not in headers and 'X-Forwarded-Host' not in headers
        assert relay_request(path,'http://other-host/v1/responses')[0]==400
        assert relay_request(path,'/v1/models')[0]==400
        assert relay_request(path,body={'redirect_test':True})[0]==502
        status,raw=relay_request(path,body={'error_test':True})
        assert status==401 and b'provider-private-key' not in raw


def require_unix():
    try: stream=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM)
    except PermissionError: pytest.skip('Executor prohibits Unix sockets; production relay protocol exercised over TCP fixture')
    stream.close()


def test_actual_unix_relay_and_responses_check(model_server,tmp_path):
    require_unix()
    config,_=model_server
    with ai_provider.relay(config) as path: assert relay_request(path)[0]==200
    assert not path.exists()
    (tmp_path/'ai-provider.json').write_text(json.dumps(config))
    assert ai_provider.check(tmp_path)['ok']
    assert config['api_key'] not in json.dumps(ai_provider.snapshot(tmp_path))


def test_custom_command_uses_edit_only_socket_and_no_chatgpt_auth(model_server,tmp_path,monkeypatch):
    from internal_runner import InternalRunner
    config,_=model_server
    runner=InternalRunner(tmp_path/'repair')
    (runner.root/'ai-provider.json').write_text(json.dumps(config))
    edit=tmp_path/'edit';edit.mkdir()
    captured={}
    @contextmanager
    def fake_relay(config): yield Path('/synthetic/ai.sock')
    monkeypatch.setattr(ai_provider,'relay',fake_relay)
    def sandbox(work,args,**kwargs):
        captured['sandbox']=kwargs
        return args
    monkeypatch.setattr(type(runner.sandbox),'command',lambda self,*args,**kwargs:sandbox(*args,**kwargs))
    monkeypatch.setattr(runner,'require_isolation',lambda:None)
    def command(job,args,**kwargs):
        captured['args']=args;return 'fixture-output'
    monkeypatch.setattr(runner,'command',command)
    try:
        assert runner.edit_command({},edit,'repair this source')=='fixture-output'
        args=captured['args'];serialized=json.dumps(args)
        assert config['api_key'] not in serialized and str(runner.root/'auth') not in serialized
        assert captured['sandbox']['ai_socket']==Path('/synthetic/ai.sock') and 'auth' not in captured['sandbox'] and '--search' not in args
        assert 'model_provider="autosourcarr"' in args
        regular=runner.sandbox.command(edit,['/bin/true'],sockets=runner.sockets)
        assert 'ai_socket' not in captured['sandbox'] and 'auth' not in captured['sandbox']
    finally: runner.socket_temp.cleanup()


def test_bad_saved_ai_config_is_actionable(tmp_path):
    (tmp_path/'ai-provider.json').write_text('{bad')
    assert ai_provider.check(tmp_path)['ok'] is False


def test_legacy_public_egress_still_rejects_private_ai_hosts():
    from egress import destination,public_address
    with pytest.raises(ValueError): destination('http://localhost:11434/v1')
    with pytest.raises(ValueError): public_address('127.0.0.1',80)


def test_relay_close_drains_waiting_upstream_threads():
    entered=threading.Event();release=threading.Event();finished=threading.Event()
    class Slow(BaseHTTPRequestHandler):
        def log_message(self,*args): pass
        def do_POST(self):
            self.rfile.read(int(self.headers['Content-Length']))
            entered.set();release.wait(5)
    upstream=ThreadingHTTPServer(('127.0.0.1',0),Slow)
    serve=threading.Thread(target=upstream.serve_forever,daemon=True);serve.start()
    config={'provider':'custom','base_url':f'http://127.0.0.1:{upstream.server_port}/v1','model':'fixture'}
    request=None
    try:
        with tcp_relay(config) as address:
            def invoke():
                try: relay_request(address)
                except (OSError,http.client.HTTPException): pass
                finally: finished.set()
            request=threading.Thread(target=invoke);request.start()
            assert entered.wait(2)
        assert finished.wait(2),'closing the relay must interrupt the waiting request'
    finally:
        release.set();upstream.shutdown();upstream.server_close();serve.join(timeout=2)
        if request: request.join(timeout=2)


def test_cancelled_connection_check_retains_ownership_until_thread_stops(tmp_path,monkeypatch):
    import importlib.util
    from types import SimpleNamespace
    from fastapi import HTTPException
    spec=importlib.util.spec_from_file_location('setup_provider_server',Path(__file__).resolve().parents[2]/'repair-worker/server.py')
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    module.runner=SimpleNamespace(root=tmp_path,cleanup_failed=False)
    entered=threading.Event();release=threading.Event()
    def check(root): entered.set();release.wait(5);return {'ok':True}
    monkeypatch.setattr(ai_provider,'check',check)
    async def scenario():
        task=asyncio.create_task(module.provider_test())
        try:
            assert await asyncio.to_thread(entered.wait,2)
            task.cancel();await asyncio.sleep(.01)
            assert module.provider_check_lock.locked() and module.provider_checks
            with pytest.raises(HTTPException) as exc: await module.provider_test()
            assert exc.value.status_code==409
        finally: release.set()
        with pytest.raises(asyncio.CancelledError): await task
        assert not module.provider_check_lock.locked() and not module.provider_checks
    asyncio.run(scenario())
    module.executor.shutdown(wait=True)
