"""All provider contracts and the reported host denial, without live accounts."""
import asyncio
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from io import BytesIO
import http.client
import json
from pathlib import Path
import sys
import threading
import time
from unittest.mock import Mock
from zipfile import ZipFile

import httpx
from fastapi import FastAPI, HTTPException
import pytest

from app.config import Settings
from app import repair_connection, repair_host_setup, setup_store, source_maintenance
from test_setup_connections import save, tcp_relay, relay_request

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'repair-worker'))
import ai_provider
import internal_sandbox
import native_editor
from internal_runner import InternalRunner, InternalControl
from validate import SourceBlocked

KINDS=[('openai','responses'),('claude','anthropic'),('gemini','chat_completions'),('openrouter','chat_completions'),('ollama','chat_completions')]+[('custom',kind) for kind in ('responses','anthropic','chat_completions')]
REPORTED={'apparmor':'docker-default (enforce)','max_user_namespaces':'245812','unprivileged_userns_clone':'not reported','Seccomp':'2','NoNewPrivs':'1'}
ERROR='bwrap: No permissions to create new namespace, likely because the kernel does not allow non-privileged user namespaces.'


@pytest.fixture
def check_transport(monkeypatch):
    # Exercise the production relay and check over real HTTP. This executor denies
    # Unix sockets; only the local transport is replaced, never upstream behavior.
    monkeypatch.setattr(ai_provider,'relay',tcp_relay)
    monkeypatch.setattr(ai_provider,'relay_connection',lambda address,timeout:http.client.HTTPConnection(*address,timeout=timeout))


def answer(kind,token=None):
    if kind=='responses':
        content=[{'type':'function_call','call_id':'call-1','name':ai_provider.PROBE_NAME,'arguments':'{}'}] if token is None else [{'type':'message','content':[{'type':'output_text','text':token}]}]
        return {'object':'response','status':'completed','output':content}
    if kind=='anthropic':
        content=[{'type':'thinking','thinking':'','signature':'empty-thinking-signed'},{'type':'redacted_thinking','data':'unchanged-private-block'},{'type':'tool_use','id':'call-1','name':ai_provider.PROBE_NAME,'input':{}}] if token is None else [{'type':'text','text':token}]
        return {'type':'message','stop_reason':'tool_use' if token is None else 'end_turn','content':content}
    message={'role':'assistant','content':token}
    if token is None: message['tool_calls']=[{'id':'call-1','type':'function','function':{'name':ai_provider.PROBE_NAME,'arguments':'{}'},'extra_content':{'google':{'thought_signature':'exact-gemini-signature'}}}]
    return {'choices':[{'finish_reason':'tool_calls' if token is None else 'stop','message':message}]}


@contextmanager
def upstream(kind,*,status=200,mode='valid'):
    records=[]
    class Handler(BaseHTTPRequestHandler):
        def log_message(self,*args): pass
        def do_POST(self):
            body=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            records.append((self.path,dict(self.headers),body))
            token=None
            if kind=='responses':
                outputs=[b for b in body.get('input',[]) if isinstance(b,dict) and b.get('type')=='function_call_output']
                if outputs: token=outputs[-1]['output']
            else:
                history=body.get('messages',[])
                if history and history[-1].get('role')=='tool': token=history[-1]['content']
                elif history and isinstance(history[-1].get('content'),list) and history[-1]['content'][0].get('type')=='tool_result': token=history[-1]['content'][0]['content']
            value=answer(kind,token)
            if mode=='text-only': value=answer(kind,'OK')
            if mode=='wrong-token' and token is not None: value=answer(kind,'invented-token')
            raw=json.dumps(value).encode() if status==200 else b'{"error":{"message":"echoed-private-key-and-prompt"}}'
            if mode=='recursive': raw=b'{"choices":'+b'['*15000+b'0'+b']'*15000+b'}'
            self.send_response(status);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(raw)));self.end_headers();self.wfile.write(raw)
    server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
    thread=threading.Thread(target=server.serve_forever,kwargs={'poll_interval':.01},daemon=True);thread.start()
    try: yield f'http://127.0.0.1:{server.server_port}',records
    finally: server.shutdown();server.server_close();thread.join(timeout=2)


@pytest.mark.parametrize('provider,kind',KINDS)
def test_all_api_providers_verify_inert_tool_continuation(tmp_path,provider,kind,check_transport):
    base={'responses':'/v1','anthropic':'/v1','chat_completions':'/v1'}[kind]
    if provider=='gemini': base='/v1beta/openai'
    if provider=='openrouter': base='/api/v1'
    (tmp_path/'source.kt').write_text('retained source')
    with upstream(kind) as (url,records):
        config={'provider':provider,'protocol':kind,'base_url':url+base,'model':'available-model','api_key':'controller-private-key','workspace_id':'wrkspc_test' if kind=='anthropic' else ''}
        (tmp_path/'ai-provider.json').write_text(json.dumps(config))
        result=ai_provider.check(tmp_path)
    assert result['ok'] and result['api_ok'] and result['tools_ok'],result
    assert len(records)==2
    assert (tmp_path/'source.kt').read_text()=='retained source'
    for path,headers,body in records:
        suffix={'responses':'/responses','anthropic':'/messages','chat_completions':'/chat/completions'}[kind]
        assert path==base+suffix and body['model']=='available-model'
        assert 'controller-private-key' not in json.dumps(body)
        if kind=='anthropic':
            assert headers['x-api-key']=='controller-private-key'
            assert headers['anthropic-workspace-id']=='wrkspc_test'
            assert body['tool_choice']=={'type':'auto'}
        else:
            assert headers['Authorization']=='Bearer controller-private-key'
            assert 'anthropic-workspace-id' not in headers
        if provider=='openrouter': assert body['provider']['require_parameters'] is True
    if kind=='anthropic': assert records[1][2]['messages'][1]['content']==answer(kind)['content']
    if kind=='chat_completions': assert records[1][2]['messages'][1]==answer(kind)['choices'][0]['message']


@pytest.mark.parametrize('kind',['responses','anthropic','chat_completions'])
@pytest.mark.parametrize('mode',['text-only','wrong-token'])
def test_a_plain_answer_or_invented_token_cannot_pass_readiness(tmp_path,kind,mode,check_transport):
    with upstream(kind,mode=mode) as (url,records):
        (tmp_path/'ai-provider.json').write_text(json.dumps({'provider':'custom','protocol':kind,'base_url':url+'/v1','model':'model'}))
        result=ai_provider.check(tmp_path)
    assert result['ok'] is False and result['tools_ok'] is False
    assert result['api_ok'] and result['reason']=='tool-support'
    assert len(records)==(1 if mode=='text-only' else 2)


@pytest.mark.parametrize('status,category',[(400,'request'),(401,'credentials'),(402,'billing'),(403,'permission'),(404,'model-or-endpoint'),(429,'quota'),(503,'provider')])
def test_relay_reports_safe_provider_error_categories(status,category):
    with upstream('chat_completions',status=status) as (url,records):
        with tcp_relay({'provider':'gemini','base_url':url+'/v1beta/openai','model':'model','api_key':'controller-private-key'}) as relay:
            code,raw=relay_request(relay,route='/v1/chat/completions',body={'model':'ignored','messages':[]})
    assert code==status and json.loads(raw)['error']['category']==category
    assert b'echoed-private-key' not in raw and b'controller-private-key' not in raw
    assert len(records)==1


@pytest.mark.parametrize('status,category',[(401,'credentials'),(402,'billing'),(403,'permission'),(404,'model-or-endpoint'),(429,'quota'),(503,'provider')])
def test_api_error_checks_are_independent_of_sandbox_readiness(tmp_path,status,category,check_transport,monkeypatch):
    blocked=Mock(side_effect=AssertionError('API check must not probe a sandbox'))
    monkeypatch.setattr(internal_sandbox.Sandbox,'probe',blocked)
    with upstream('anthropic',status=status) as (url,records):
        (tmp_path/'ai-provider.json').write_text(json.dumps({'provider':'claude','base_url':url+'/v1','model':'model','api_key':'private-key'}))
        result=ai_provider.check(tmp_path)
    assert result['ok'] is False and result['reason']==category and result['http_status']==status
    assert 'private-key' not in json.dumps(result) and len(records)==1
    blocked.assert_not_called()


def test_api_deadline_stops_a_trickling_http_close_response(tmp_path,check_transport,monkeypatch):
    # HTTP/1.0 detaches the socket into HTTPResponse before its body is read.
    # A per-read timeout alone is reset by every chunk and cannot bound the job.
    done=threading.Event();release=threading.Event();writes=[]
    class Slow(BaseHTTPRequestHandler):
        def log_message(self,*args): pass
        def do_POST(self):
            self.rfile.read(int(self.headers['Content-Length']))
            raw=json.dumps(answer('chat_completions')).encode()
            self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(raw)));self.end_headers()
            try:
                for pos in range(0,len(raw),10):
                    self.wfile.write(raw[pos:pos+10]);self.wfile.flush();writes.append(pos)
                    if release.wait(.05): break
            except (BrokenPipeError,ConnectionResetError): pass
            finally: done.set()
    server=ThreadingHTTPServer(('127.0.0.1',0),Slow)
    thread=threading.Thread(target=server.serve_forever,kwargs={'poll_interval':.01},daemon=True);thread.start()
    monkeypatch.setattr(ai_provider,'CHECK_TIMEOUT',.25)
    (tmp_path/'ai-provider.json').write_text(json.dumps({'provider':'custom','protocol':'chat_completions','model':'model','base_url':f'http://127.0.0.1:{server.server_port}/v1'}))
    started=time.monotonic()
    try:
        result=ai_provider.check(tmp_path)
        elapsed=time.monotonic()-started
        assert result['ok'] is False and result['reason']=='connection',result
        assert elapsed<.9,'a slow body must not outlive the shared check deadline'
        assert len(writes)<10,'the timer must stop the response-owned socket'
    finally:
        release.set();assert done.wait(2);server.shutdown();server.server_close();thread.join(timeout=2)


def test_recursive_provider_json_returns_a_safe_format_error(tmp_path,check_transport):
    with upstream('chat_completions',mode='recursive') as (url,records):
        (tmp_path/'ai-provider.json').write_text(json.dumps({'provider':'custom','protocol':'chat_completions','base_url':url+'/v1','model':'model'}))
        result=ai_provider.check(tmp_path)
    assert result['ok'] is False and result['reason']=='api-format'
    assert result['api_ok'] and not result['tools_ok'] and len(records)==1


@pytest.mark.parametrize('kind',['responses','anthropic','chat_completions'])
def test_probe_rejects_malformed_calls_truncation_and_continuations(kind):
    _,body=ai_provider.probe_body(kind,'model')
    for malformed in ({},answer(kind,'OK'),{'output':[],'content':[],'choices':[]}):
        assert ai_provider.probe_followup(kind,body,malformed,'token') is None
    first=answer(kind)
    call=first['output'][0] if kind=='responses' else first['content'][-1] if kind=='anthropic' else first['choices'][0]['message']['tool_calls'][0]['function']
    call['name']='run_shell'
    assert ai_provider.probe_followup(kind,body,first,'token') is None
    assert not ai_provider.probe_finished(kind,answer(kind),'token')
    assert not ai_provider.probe_finished(kind,answer(kind,'wrong'),'token')
    assert ai_provider.probe_finished(kind,answer(kind,'token'),'token')


@pytest.mark.parametrize('status',['incomplete','failed','cancelled','in_progress','queued',[],{}])
def test_responses_probe_requires_completed_output(status):
    _,body=ai_provider.probe_body('responses','model')
    value=answer('responses');value['status']=status
    assert ai_provider.probe_followup('responses',body,value,'token') is None
    value=answer('responses','token');value['status']=status
    assert not ai_provider.probe_finished('responses',value,'token')


@pytest.mark.parametrize('kind',['responses','anthropic'])
def test_probe_never_replays_an_unknown_or_server_tool(kind):
    _,body=ai_provider.probe_body(kind,'model')
    value=answer(kind)
    value['output' if kind=='responses' else 'content'].append({'type':'custom_tool_call' if kind=='responses' else 'server_tool_use','name':'run_shell'})
    assert ai_provider.probe_followup(kind,body,value,'token') is None


@pytest.mark.parametrize('kind',['anthropic','chat_completions'])
def test_native_editor_retains_sequential_parallel_provider_signatures(tmp_path,kind):
    module=tmp_path/'src';module.mkdir();target=module/'Source.kt';target.write_text('val path="/old"')
    histories=[];assistant=[]
    def ask(_kind,model,history):
        histories.append(json.loads(json.dumps(history)))
        step=len(histories)
        commands=[('read','read_file',{'path':'src/Source.kt'}),('list','list_files',{'path':'src'})] if step==1 else [('edit','replace_text',{'path':'src/Source.kt','old':'/old','new':'/new'})]
        if step==3: return {'type':'message','stop_reason':'end_turn','content':[{'type':'text','text':'Updated path'}]} if kind=='anthropic' else {'choices':[{'finish_reason':'stop','message':{'role':'assistant','content':'Updated path'}}]}
        if kind=='anthropic':
            content=[{'type':'thinking','thinking':'' if step==1 else 'thought','signature':'signed-'+str(step)},{'type':'redacted_thinking','data':'redacted-'+str(step)}]+[{'type':'tool_use','id':ident,'name':name,'input':args} for ident,name,args in commands]
            message={'role':'assistant','content':content};result={'type':'message','stop_reason':'tool_use','content':content}
        else:
            calls=[{'id':ident,'type':'function','function':{'name':name,'arguments':json.dumps(args)}} for ident,name,args in commands]
            calls[0]['extra_content']={'google':{'thought_signature':'signed-'+str(step)}}
            message={'role':'assistant','content':None,'tool_calls':calls};result={'choices':[{'finish_reason':'tool_calls','message':message}]}
        assistant.append(json.loads(json.dumps(message)));return result
    assert native_editor.run(kind,'model','Update path','src',root=tmp_path,ask=ask)=='Updated path'
    assert target.read_text()=='val path="/new"' and len(histories)==3
    assert [m for m in histories[-1] if m['role']=='assistant']==assistant
    if kind=='anthropic':
        results=[m for m in histories[-1] if m['role']=='user' and isinstance(m['content'],list)]
        assert [len(m['content']) for m in results]==[2,1]
        assert all(b['type']=='tool_result' for m in results for b in m['content'])
    else:
        assert [m['tool_call_id'] for m in histories[-1] if m['role']=='tool']==['read','list','edit']


@pytest.mark.parametrize('variant',[ERROR,'bwrap: No permissions to creating new namespace','bwrap: Creating new namespace failed: Operation not permitted'])
def test_exact_namespace_denial_has_host_guidance_for_every_provider(variant):
    result=internal_sandbox.isolation_failure(variant,1,REPORTED.copy())
    assert result['reason']=='namespace-denied' and not result['available']
    assert result['host_setup'] and result['api_checks_available']
    assert result['checks']==REPORTED and 'every provider' in result['message']
    assert 'Docker default AppArmor' in result['steps'][0]
    assert 'not which rules' in result['steps'][1]


def test_missing_optional_host_knobs_are_not_reported_as_disabled(monkeypatch):
    original=Path.read_text
    def read(path,*args,**kwargs):
        if path.as_posix().startswith('/proc/sys/kernel/apparmor_restrict'): raise FileNotFoundError()
        return original(path,*args,**kwargs)
    monkeypatch.setattr(Path,'read_text',read)
    checks=internal_sandbox.host_checks()
    assert checks['apparmor_restrict_unprivileged_userns']=='not reported'
    assert checks['apparmor_restrict_unprivileged_userns_force']=='not reported'


@pytest.mark.parametrize('provider',sorted(ai_provider.PROVIDERS))
def test_denied_sandbox_prevents_every_ai_provider_from_launching(tmp_path,monkeypatch,provider):
    runner=InternalRunner(tmp_path/'repair')
    (runner.root/'ai-provider.json').write_text(json.dumps({'provider':provider}))
    result=internal_sandbox.isolation_failure(ERROR,1,REPORTED)
    monkeypatch.setattr(type(runner.sandbox),'probe',lambda self:result)
    launch=Mock(side_effect=AssertionError('must not launch'))
    monkeypatch.setattr(runner,'command',launch)
    try:
        with pytest.raises(SourceBlocked,match='every provider'): runner.edit_command({},tmp_path,'repair')
        launch.assert_not_called()
    finally: runner.socket_temp.cleanup()


def test_workspace_setting_survives_restart_without_exposing_the_key(tmp_path):
    settings=Settings(settings_config_dir=tmp_path,app_services_mode='integrated')
    save(settings,{'REPAIR_AI_PROVIDER':'claude','REPAIR_AI_BASE_URL':'https://api.anthropic.com/v1','REPAIR_AI_MODEL':'available-model','REPAIR_AI_API_KEY':'private-key','REPAIR_AI_WORKSPACE_ID':'wrkspc_test'})
    assert setup_store.startup_overrides(tmp_path)['repair_ai_workspace_id']=='wrkspc_test'
    assert 'private-key' not in json.dumps(setup_store.snapshot(tmp_path,settings))
    for invalid in ('bad workspace','x\nsecret','☃','x'*129):
        with pytest.raises(ValueError): save(settings,{'REPAIR_AI_WORKSPACE_ID':invalid})
        with pytest.raises(ValueError): ai_provider.target({'base_url':'https://api.anthropic.com/v1','model':'model','workspace_id':invalid})


def test_host_policy_export_contains_only_the_reviewed_files():
    root=Path(__file__).resolve().parents[2]
    with ZipFile(BytesIO(repair_host_setup.export_host_setup())) as archive:
        assert archive.testzip() is None
        assert set(archive.namelist())=={'README.md','security/repair-seccomp.json','security/autosourcarr-internal.apparmor','security/LICENSE-moby'}
        for name in repair_host_setup.FILES:
            target='README.md' if name.endswith('.md') else 'security/'+name
            assert archive.read(target)==(root/'security'/name).read_bytes()


def test_host_policy_export_fails_closed_when_missing_or_linked(tmp_path,monkeypatch):
    monkeypatch.setattr(repair_host_setup,'POLICY_ROOTS',(tmp_path,))
    with pytest.raises(ValueError): repair_host_setup.export_host_setup()
    target=tmp_path/'secret';target.write_text('not a policy')
    (tmp_path/'repair-seccomp.json').symlink_to(target)
    with pytest.raises(ValueError): repair_host_setup.export_host_setup()


@pytest.mark.asyncio
async def test_host_policy_download_remains_authenticated_and_does_not_call_worker():
    worker=Mock(side_effect=AssertionError('download does not start repair tools'))
    async def guard(): raise HTTPException(401,'auth required')
    app=FastAPI();app.include_router(source_maintenance.maintenance_router(guard,asyncio.Lock()))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://app') as client:
        assert (await client.get('/api/admin/source-maintenance/host-setup')).status_code==401
    app=FastAPI();app.include_router(repair_connection.connection_router(worker,asyncio.Lock()))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://app') as client:
        response=await client.get('/host-setup')
    assert response.status_code==200 and response.headers['Cache-Control']=='no-store'
    assert response.headers['Content-Type']=='application/zip'
    assert ZipFile(BytesIO(response.content)).testzip() is None
    worker.assert_not_called()


def test_setup_presets_cover_every_provider_and_clear_foreign_credentials():
    from app.setup_schema import FIELDS
    from test_v086_ui import node
    settings=Settings()
    fields=[{**f,'value':getattr(settings,f['key'].lower()),'configured':f['key']=='REPAIR_AI_API_KEY'} for f in FIELDS if f['step']=='repair']
    source=r'''
const {harness,tick}=require('./autosourcarr/tests/ui_setup.js');const assert=require('node:assert/strict');
(async()=>{
 const h=harness();h.data.fields.push(...FIELDS);await h.context.AutoSourcarrSetup.openRepairAI();await tick();
 const presets={openai:['https://api.openai.com/v1','responses'],claude:['https://api.anthropic.com/v1','anthropic'],gemini:['https://generativelanguage.googleapis.com/v1beta/openai','chat_completions'],openrouter:['https://openrouter.ai/api/v1','chat_completions'],ollama:['','chat_completions']};
 for(const [provider,[base,kind]] of Object.entries(presets)){
   const select=h.nodes.get('setup-REPAIR_AI_PROVIDER');select.value=provider;select.events.change();
   assert.equal(h.nodes.get('setup-REPAIR_AI_BASE_URL').value,base);assert.equal(h.nodes.get('setup-REPAIR_AI_API_KEY').value,'');
   assert.equal(h.nodes.get('setup-REPAIR_AI_MODEL').value,'');
   const body=h.nodes.get('setupBody').innerHTML;assert(body.includes('tool call'));assert(body.includes('target="_blank"'));
   assert.equal(body.includes('id="setup-REPAIR_AI_WORKSPACE_ID"'),provider==='claude');
   if(provider==='claude')h.nodes.get('setup-REPAIR_AI_WORKSPACE_ID').value='wrkspc_old';
   if(provider==='ollama'){assert(body.includes('TrueNAS/Linux'));assert(body.includes('MODEL-SERVER-IP'));}
 }
 let select=h.nodes.get('setup-REPAIR_AI_PROVIDER');select.value='custom';select.events.change();
 let protocol=h.nodes.get('setup-REPAIR_AI_PROTOCOL');protocol.value='anthropic';protocol.events.change();
 assert(h.nodes.get('setupBody').innerHTML.includes('id="setup-REPAIR_AI_WORKSPACE_ID"'));
 assert.equal(h.nodes.get('setup-REPAIR_AI_WORKSPACE_ID').value,'');
 protocol=h.nodes.get('setup-REPAIR_AI_PROTOCOL');protocol.value='responses';protocol.events.change();
 assert(!h.nodes.get('setupBody').innerHTML.includes('id="setup-REPAIR_AI_WORKSPACE_ID"'));
 select=h.nodes.get('setup-REPAIR_AI_PROVIDER');select.value='chatgpt';select.events.change();await tick();
 assert(h.nodes.get('setupBody').innerHTML.includes('Repair tools and ChatGPT account'));
 assert(!h.nodes.get('setupBody').innerHTML.includes('id="setup-REPAIR_AI_API_KEY"'));
 assert(!h.calls.some(([p,o])=>o?.method==='POST'));h.context.AutoSourcarrSetup.cancel();
})().catch(e=>{console.error(e);process.exitCode=1;});
'''
    node(source.replace('...FIELDS','...'+json.dumps(fields)))


def test_host_diagnostics_open_and_download_the_authenticated_policy_bundle():
    from test_v0315_repair_ui import DOM, STATIC
    from test_v086_ui import node
    # Keep the production click handler and replace only the API/DOM download boundary.
    dom=DOM.replace('const api=async','let api=async')
    node(dom+(STATIC/'repair-isolation.js').read_text()+r'''
(async()=>{
 const calls=[],clicked=[],revoked=[];document.body=new Node();Node.prototype.remove=function(){};Node.prototype.click=function(){clicked.push(this.download)};
 globalThis.URL={createObjectURL:value=>{assert.equal(value,'policy-blob');return 'blob:fixture'},revokeObjectURL:value=>revoked.push(value)};
 api=async(path,opts)=>{calls.push({path,opts});return 'policy-blob'};
 const target=ids.repairIsolation;renderRepairIsolation(target,{available:false,reason:'namespace-denied',host_setup:true,steps:['Docker default AppArmor is active.'],checks:{apparmor:'docker-default (enforce)',Seccomp:'2'}});
 const detail=target.children[0];assert.equal(detail.open,true);
 const advanced=detail.children.find(x=>x.children.some(y=>y.textContent==='Host permissions for running source repairs'));assert(advanced);assert.notEqual(advanced.open,true);
 assert(detail.children.some(x=>x.textContent.includes('ChatGPT sign-in')&&x.textContent.includes('without this repair check or Compose changes')));
 const button=advanced.children.find(x=>x.textContent==='Download host policies and instructions');assert(button);
 await button.listeners.click();assert.equal(button.disabled,false);
 assert.equal(calls[0].path,'/api/v1/source-maintenance/host-setup');assert.equal(calls[0].opts.responseType,'blob');
 assert.deepEqual(clicked,['autosourcarr-repair-host-setup.zip']);timers.at(-1).fn();assert.deepEqual(revoked,['blob:fixture']);
 api=async()=>{throw Error('Download blocked')};await button.listeners.click();assert(advanced.children.some(x=>x.textContent==='Download blocked'));
})().catch(e=>{console.error(e);process.exitCode=1;});
''')
