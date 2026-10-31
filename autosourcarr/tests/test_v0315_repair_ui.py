"""Exercise shipped repair setup event handlers with deterministic HTTP/DOM boundaries."""
import json
import re
from pathlib import Path
from test_v086_ui import node

STATIC=Path(__file__).resolve().parents[1]/'app/static'
SOURCE=(STATIC/'repair-login.js').read_text()+(STATIC/'source-repairs.js').read_text()
IDS=re.findall(r'\bid="([^"]+)"',(STATIC/'index.html').read_text())
DOM=r'''
const assert=require('node:assert/strict'),window=globalThis;let active=false,observer;
class Node{
 constructor(){this.value='';this.textContent='';this.hidden=false;this.disabled=false;this.checked=false;this.children=[];this.listeners={};this.classList={contains:()=>active};}
 addEventListener(type,fn){this.listeners[type]=fn;}
 append(...nodes){this.children.push(...nodes);}
 replaceChildren(...nodes){this.children=nodes;}
 querySelector(){return this.submit||(this.submit=new Node());}
 get options(){return this.children;}
 dispatchEvent(event){return this.listeners[event.type]?.({target:this});}
 focus(){document.activeElement=this;} select(){}
 scrollIntoView(){}
 setAttribute(name,value){(this.attributes??={})[name]=value;}
 removeAttribute(name){delete (this.attributes??={})[name];}
}
const ids=Object.fromEntries(ID_NAMES.map(id=>[id,new Node()]));
const document={hidden:false,getElementById:id=>ids[id],createElement:()=>new Node(),listeners:{},addEventListener(type,fn){this.listeners[type]=fn;}};
const MutationObserver=class{constructor(fn){observer=fn}observe(){}};
const timers=[];const setTimeout=(fn,ms)=>{timers.push({fn,ms});return timers.length};const clearTimeout=()=>{};
const toast=()=>{},confirm=()=>true,Event=class{constructor(type){this.type=type}};
const requests=[];let connected=true,failControl=false,mode='integrated',serviceState={state:'ready',message:'Internal repair ready'};
let control={state:'idle',action:'',message:'Ready',busy:false};
const api=async(path,opts={})=>{
 requests.push({path,opts});
 if(path.endsWith('/source-maintenance'))return {mode,service:serviceState,configured:connected,reachable:connected,web_control:true,jobs:[],watch:{}};
 if(path.endsWith('/connection'))return opts.method==='PUT'?{revision:'new-rev'}:{url:'http://worker:8766',revision:'old-rev',token_saved:true};
 if(path.endsWith('/installed'))return {items:[]};
 if(path.endsWith('/control')){if(failControl)throw Error('Worker disconnected');return control;}
 if(path.endsWith('/control/login'))return control={operation_id:JSON.parse(opts.body).operation_id,busy:true,state:'running',action:'login',message:'Sign in',verification_url:'https://auth.openai.com/codex/device',device_code:'ABCD-12345',expires_at:Date.now()/1000+900};
 if(path.endsWith('/control/cancel-login'))return control={busy:false,state:'cancelled',action:'login',message:'Cancelled'};
 if(path.endsWith('/setup-preview'))return {compose:'services: private-preview',message:'Preview only'};
 throw Error('Unexpected '+path);
};
const click=id=>ids[id].listeners.click({target:ids[id]});
const submit=id=>ids[id].listeners.submit({target:ids[id],preventDefault(){}});
const tick=()=>new Promise(resolve=>setImmediate(resolve));
'''.replace('ID_NAMES',json.dumps(IDS))


def test_account_ui_login_cancellation_and_navigation_clear_code():
    node(DOM+SOURCE+r'''
(async()=>{
 active=true;await loadSourceRepairs();assert(!ids.repairLogin.disabled);
 await click('repairLogin');assert(!ids.repairDeviceLogin.hidden);assert.equal(ids.repairDeviceCode.textContent,'ABCD-12345');
 assert(ids.repairLogin.disabled&&ids.repairLogout.disabled);assert(!ids.repairCancelLogin.hidden);
 assert(requests.find(x=>x.path.endsWith('/control/login')).opts.suppressClientError);
 active=false;observer();assert(ids.repairDeviceLogin.hidden&&ids.repairDeviceCode.textContent==='');
 active=true;await loadSourceRepairs();assert.equal(ids.repairDeviceCode.textContent,'ABCD-12345','worker login survives reopening tab');
 await click('repairCancelLogin');assert(ids.repairDeviceLogin.hidden&&!ids.repairLogin.disabled);
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_ui_never_renders_nonofficial_or_expired_login_and_clears_on_failure():
    node(DOM+SOURCE+r'''
(async()=>{
 active=true;control={busy:true,action:'login',verification_url:'https://evil.example/codex/device',device_code:'ABCD-12345',expires_at:Date.now()/1000+900};
 await loadSourceRepairs();assert(ids.repairDeviceLogin.hidden);
 control.verification_url='https://auth.openai.com/codex/device';control.expires_at=Date.now()/1000-1;
 await loadSourceRepairs();assert(ids.repairDeviceLogin.hidden);
 control.expires_at=Date.now()/1000+900;await loadSourceRepairs();assert(!ids.repairDeviceLogin.hidden);
 failControl=true;await loadSourceRepairs();assert(ids.repairDeviceLogin.hidden&&ids.repairDeviceCode.textContent==='');
 assert(ids.repairLogin.disabled&&ids.repairTestAI.disabled&&!ids.configureRepairAI.disabled);
 assert(ids.sourceRepairFormSection.hidden&&ids.sourceRepairWatch.disabled);
 assert.equal(ids.repairControlStatus.textContent,'Worker disconnected');
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_ui_ai_setup_opens_in_current_instance_without_connection_requests():
    node(DOM+SOURCE+r'''
(async()=>{
 let opened=0;window.AutoSourcarrSetup={openRepairAI:()=>{opened++;}};
 await click('configureRepairAI');assert.equal(opened,1);
 assert.equal(requests.length,0,'No worker URL, token, Compose or other instance is needed');
 active=true;await loadSourceRepairs();
 assert(!requests.some(x=>x.path.endsWith('/connection')||x.path.endsWith('/setup-preview')));
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_ui_missing_internal_runtime_keeps_setup_available():
    node(DOM+SOURCE+r'''
(async()=>{
 active=true;control={blocked:true,message:'Cleanup needed'};await loadSourceRepairs();
 assert(!ids.repairCleanup.hidden&&!ids.repairCleanup.disabled);
 connected=false;serviceState={state:'unavailable',message:'Bundled repair could not start. Check image build.'};
 active=true;await loadSourceRepairs();assert(ids.repairSetupPanel.open&&ids.repairLogin.disabled);
 assert.equal(ids.sourceRepairStatus.textContent,serviceState.message);
 assert.equal(ids.repairControlStatus.textContent,serviceState.message);
 assert(!ids.configureRepairAI.disabled&&!ids.openRepairSetup.disabled);
 assert(ids.repairCleanup.hidden&&ids.repairCleanup.disabled);
 assert(ids.sourceRepairFormSection.hidden&&ids.sourceRepairWatch.disabled);
 assert(!ids.repairComposeInput&&!ids.repairNormalService&&!ids.repairPrivateService);
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_known_isolation_denial_disables_repair_until_check_passes_and_keeps_login_available():
    node(DOM+SOURCE+r'''
(async()=>{
 active=true;control={busy:false,state:'idle',isolation:{available:false,reason:'namespace-denied',message:'Host denied namespaces'}};
 await loadSourceRepairs();
 assert(ids.sourceRepairForm.querySelector().disabled);
 assert(!ids.repairLogin.disabled,'Account sign-in is independent of repair namespaces');
 assert(ids.sourceRepairStatus.textContent.includes('Host denied namespaces'));
 control.isolation={available:true,message:'Isolation available'};
 await loadSourceRepairs();assert(!ids.sourceRepairForm.querySelector().disabled);
})().catch(e=>{console.error(e);process.exitCode=1});
''')
