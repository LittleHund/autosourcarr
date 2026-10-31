import json
from pathlib import Path
import sys

import pytest
from app.config import Settings
from app import setup_store
from test_setup_connections import save, tcp_relay, relay_request
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'repair-worker'))
import ai_provider
import native_editor
from internal_sandbox import isolation_failure


@pytest.mark.parametrize('provider,kind',[('openai','responses'),('claude','anthropic'),('gemini','chat_completions'),('openrouter','chat_completions'),('ollama','chat_completions'),('custom','anthropic')])
def test_provider_settings_save_and_select_real_protocol(tmp_path,provider,kind):
    settings=Settings(settings_config_dir=tmp_path,app_services_mode='integrated')
    values={'REPAIR_AI_PROVIDER':provider,'REPAIR_AI_BASE_URL':'https://api.example/v1','REPAIR_AI_MODEL':'selected-model','REPAIR_AI_API_KEY':'test-key','REPAIR_AI_PROTOCOL':'anthropic'}
    save(settings,values)
    running=setup_store.startup_overrides(tmp_path)
    assert running['repair_ai_provider']==provider
    assert ai_provider.protocol({'provider':provider,'protocol':'anthropic'})==kind
    assert 'test-key' not in json.dumps(setup_store.snapshot(tmp_path,settings))


def test_provider_switch_cannot_retain_hidden_key(tmp_path):
    settings=Settings(settings_config_dir=tmp_path,app_services_mode='integrated',repair_ai_base_url='https://api.example/v1',repair_ai_model='model',repair_ai_api_key='old-secret')
    with pytest.raises(ValueError,match='explicitly replace or remove'):
        save(settings,{'REPAIR_AI_PROVIDER':'claude'})


@pytest.mark.parametrize('kind',['anthropic','chat_completions'])
def test_native_editor_tool_roundtrip_and_fixed_relay(tmp_path,kind):
    from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
    import threading
    records=[];module=tmp_path/'src/en/example/src';module.mkdir(parents=True)
    file=module/'Source.kt';file.write_text('val endpoint = "/old/api"')
    class Handler(BaseHTTPRequestHandler):
        def log_message(self,*a):pass
        def do_POST(self):
            body=json.loads(self.rfile.read(int(self.headers['Content-Length'])));records.append((self.path,dict(self.headers),body))
            if len(records)==1:
                args={'path':'src/en/example/src/Source.kt','old':'/old/api','new':'/new/api'}
                result={'type':'message','stop_reason':'tool_use','content':[{'type':'thinking','thinking':'private reasoning','signature':'preserve-me'},{'type':'tool_use','id':'call1','name':'replace_text','input':args}]} if kind=='anthropic' else {'choices':[{'finish_reason':'tool_calls','message':{'role':'assistant','content':None,'tool_calls':[{'id':'call1','type':'function','function':{'name':'replace_text','arguments':json.dumps(args)},'extra_content':{'signature':'preserve-me'}}]}}]}
            else:
                result={'type':'message','stop_reason':'end_turn','content':[{'type':'text','text':'Updated API path. Functional validation still required.'}]} if kind=='anthropic' else {'choices':[{'finish_reason':'stop','message':{'role':'assistant','content':'Updated API path. Functional validation still required.'}}]}
            raw=json.dumps(result).encode();self.send_response(200);self.send_header('Content-Type','application/json');self.end_headers();self.wfile.write(raw)
    server=ThreadingHTTPServer(('127.0.0.1',0),Handler);thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
    try:
        config={'provider':'custom','protocol':kind,'base_url':f'http://127.0.0.1:{server.server_port}/v1','model':'forced-model','api_key':'controller-only'}
        with tcp_relay(config) as endpoint:
            answer=native_editor.run(kind,'untrusted-model','Fix parser','src/en/example/src',root=tmp_path,ask=lambda k,m,history:native_editor.request(k,m,history,endpoint))
            assert relay_request(endpoint,route='/v1/responses')[0]==400
        assert file.read_text()=='val endpoint = "/new/api"'
        assert 'validation still required' in answer
        assert len(records)==2
        for path,headers,body in records:
            assert path==('/v1/messages' if kind=='anthropic' else '/v1/chat/completions')
            assert body['model']=='forced-model'
            assert 'controller-only' not in json.dumps(body)
            if kind=='anthropic':
                assert headers['x-api-key']=='controller-only' and headers['anthropic-version']=='2023-06-01'
                assert 'Authorization' not in headers
            else:assert headers['Authorization']=='Bearer controller-only'
        assert 'preserve-me' in json.dumps(records[1][2]['messages'])
        assert not list(tmp_path.rglob('*auth*'))
    finally:server.shutdown();server.server_close();thread.join(timeout=2)


def test_editor_paths_truncation_and_limits(tmp_path):
    module=tmp_path/'src';module.mkdir();target=module/'Source.kt';target.write_text('old')
    outside=tmp_path/'build.kt';outside.write_text('old')
    for path in ('../escape.kt','/tmp/escape.kt','build.kt'):
        with pytest.raises(ValueError):native_editor.tool(tmp_path,module,'replace_text',{'path':path,'old':'old','new':'bad'})
    (module/'link.kt').symlink_to(outside)
    with pytest.raises(ValueError):native_editor.tool(tmp_path,module,'read_file',{'path':'src/link.kt'})
    with pytest.raises(RuntimeError,match='incomplete'):
        native_editor.run('anthropic','m','p','src',root=tmp_path,ask=lambda *a:{'content':[],'stop_reason':'max_tokens'})
    counter=[]
    def endless(*a):
        counter.append(1)
        return {'content':[{'type':'tool_use','id':'x','name':'read_file','input':{'path':'src/Source.kt'}}],'stop_reason':'tool_use'}
    with pytest.raises(RuntimeError,match='turn limit'):native_editor.run('anthropic','m','p','src',root=tmp_path,ask=endless)
    assert len(counter)==24 and target.read_text()=='old'


@pytest.mark.parametrize('stderr,checks,reason',[
    ('bwrap: Creating new namespace failed: Operation not permitted',{'NoNewPrivs':'1'},'namespace-denied'),
    ('bwrap: mount /proc: Permission denied',{'NoNewPrivs':'1'},'mount-denied'),
    ('AssertionError',{'NoNewPrivs':'0'},'container-policy'),
    ('Operation not permitted',{'NoNewPrivs':'1','max_user_namespaces':'0'},'host-userns'),
    ('bwrap: execvp python: No such file',{},'runtime-missing'),
    ('AssertionError: private directory exposed',{'NoNewPrivs':'1'},'self-check')])
def test_isolation_failures_remain_closed_and_actionable(stderr,checks,reason):
    value=isolation_failure(stderr,1,checks)
    assert value['available'] is False and value['reason']==reason
    assert value['exit_code']==1 and stderr in value['details']
    assert 'unconfined' not in value['message']


@pytest.mark.asyncio
async def test_account_cancel_only_targets_owned_operation(tmp_path,monkeypatch):
    import asyncio
    import uuid
    from types import SimpleNamespace
    from control import Control
    (tmp_path/'locks').mkdir();(tmp_path/'auth').mkdir()
    c=Control(SimpleNamespace(root=tmp_path))
    entered=asyncio.Event()
    async def pending_command(action):
        entered.set();await asyncio.Future()
    monkeypatch.setattr(c,'account_command',pending_command)
    first=str(uuid.uuid4());second=str(uuid.uuid4())
    assert c.start('login',first)['operation_id']==first
    await entered.wait()
    assert c.start('login',first)['operation_id']==first  # retry reuses work
    await c.cancel_login(second);assert c.busy
    await c.cancel_login(first);assert not c.busy
    entered.clear();c.start('login',second);await entered.wait()
    await c.cancel_login(first);assert c.busy  # stale tab cannot stop new login
    await c.cancel_login(second);assert not c.busy
