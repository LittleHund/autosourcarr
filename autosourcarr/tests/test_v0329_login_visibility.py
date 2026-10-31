"""Returning from login must refresh its code independently of unrelated work."""
import os
import json
from pathlib import Path
import subprocess
import sys
import pytest
from test_v086_ui import node

ROOT = Path(os.environ.get('AUTOSOURCARR_REPRO_ROOT', Path(__file__).resolve().parents[2]))
sys.path.insert(0, str(ROOT / 'autosourcarr/tests'))
from test_v0315_repair_ui import DOM


def _run_source_case(boundary, case):
    static = ROOT / 'autosourcarr/app/static'
    # Keep the existing controlled DOM, but exercise the app's production API.
    fixture = DOM[:DOM.index('const api=async')] + DOM[DOM.index('const click='):]
    app = (static / 'app.js').read_text()
    api = app[app.index('async function api('):app.index("window.addEventListener('unhandledrejection'")]
    source = (static / 'repair-login.js').read_text() + (static / 'source-repairs.js').read_text()
    result = subprocess.run(['node', '-e', fixture + boundary + api + source + case], cwd=ROOT, text=True, capture_output=True, timeout=10)
    assert result.returncode == 0, result.stdout + result.stderr


def test_return_from_openai_tab_polls_code_without_waiting_for_installed_inventory():
    boundary = r'''
let token='',adminAuthDismissed=false;const clientErrors=[];
const scheduleMutationRefresh=()=>{},rememberClientError=()=>{};
let releaseInventory;const inventoryWait=new Promise(resolve=>releaseInventory=resolve);
let acceptedOperation='',controlReads=0;
const official='https://auth.openai.com/codex/device';
globalThis.open=()=>({opener:'caller',location:{replace:url=>{
 assert.equal(url,official);
 // A foreground sign-in tab hides the app while its HTTP POST is pending.
 queueMicrotask(()=>{document.hidden=true;document.listeners.visibilitychange();});
}}});
const fetch=async(path,options={})=>{
 requests.push({path,opts:options});
 let data,status=200;
 if(path==='/api/admin/source-maintenance')data={mode:'integrated',configured:true,reachable:true,web_control:true,ai_provider:{provider:'chatgpt'},jobs:[],watch:{}};
 else if(path.endsWith('/control')){controlReads++;data=control;}
 else if(path.endsWith('/installed'))return {status,ok:true,headers:{get:()=>''},json:()=>inventoryWait};
 else if(path.endsWith('/control/login')){
  acceptedOperation=JSON.parse(options.body).operation_id;status=202;
  control={busy:true,state:'running',action:'login',operation_id:acceptedOperation,message:'Requesting a code',device_code:'',verification_url:'',expires_at:0};data=control;
 }else throw Error('Unexpected HTTP request '+path);
 return {status,ok:true,headers:{get:()=>''},json:async()=>data};
};
'''
    case = r'''
(async()=>{
 active=true;const inventoryLoading=loadSourceRepairs();await tick();
 assert(!ids.repairLogin.disabled,'Login becomes usable before source inventory finishes');
 await click('repairLogin');assert(document.hidden,'The OpenAI page is in the foreground');
 control={...control,device_code:'ABCD-EFGH',verification_url:official,expires_at:Date.now()/1000+900,message:'Enter your code'};
 document.hidden=false;document.listeners.visibilitychange();await tick();await tick();
 // This requirement is independent from repair-job/source dropdown loading.
 assert(controlReads>=2,'Returning from the OpenAI tab must request the current account code immediately while /installed is unresolved');
 assert(!ids.repairDeviceLogin.hidden,'Current device-code region is visible on return');
 assert.equal(ids.repairDeviceCode.textContent,'ABCD-EFGH');
 releaseInventory({items:[]});await inventoryLoading;
 console.log('PASS foreground login return is independent of source inventory');
})().catch(error=>{console.error(error);process.exitCode=1});
'''
    _run_source_case(boundary, case)


@pytest.mark.parametrize('old_error', [False, True])
def test_return_during_held_control_read_schedules_current_successor(old_error):
    boundary = r'''
let token='',adminAuthDismissed=false;const clientErrors=[];
const scheduleMutationRefresh=()=>{},rememberClientError=()=>{};
const official='https://auth.openai.com/codex/device';
let controlReads=0,holdControl=false,releaseControl,rejectControl,opened=0;
const controlWait=new Promise((resolve,reject)=>{releaseControl=resolve;rejectControl=reject;});
globalThis.open=()=>{opened++;throw Error('Background refresh must not open a sign-in tab');};
control={busy:true,state:'running',action:'login',operation_id:'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',message:'Enter code',device_code:'AAAA-1111',verification_url:official,expires_at:Date.now()/1000+900};
const fetch=async(path,options={})=>{
 requests.push({path,opts:options});
 let data,status=200;
 if(path==='/api/admin/source-maintenance')data={mode:'integrated',configured:true,reachable:true,web_control:true,ai_provider:{provider:'chatgpt'},jobs:[],watch:{}};
 else if(path.endsWith('/installed'))data={items:[]};
 else if(path.endsWith('/control')){
  controlReads++;
  if(holdControl){holdControl=false;return {status,ok:true,headers:{get:()=>''},json:()=>controlWait};}
  data=control;
 }else throw Error('Unexpected HTTP request '+path);
 return {status,ok:true,headers:{get:()=>''},json:async()=>data};
};
'''
    case = r'''
(async()=>{
 active=true;await loadSourceRepairs();
 assert.equal(ids.repairDeviceCode.textContent,'AAAA-1111');
 holdControl=true;const oldRead=loadSourceRepairs();await tick();
 assert.equal(controlReads,2,'The current control read is now held at its body boundary');
 document.hidden=true;document.listeners.visibilitychange();
 assert(ids.repairDeviceLogin.hidden,'Leaving the app removes the old code from view');
 // Another admin can complete the first login and start a new one while hidden.
 control={...control,operation_id:'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',device_code:'BBBB-2222'};
 document.hidden=false;document.listeners.visibilitychange();await tick();await tick();
 assert(controlReads>=3,'Returning while an aborted old /control body is held must immediately start its current successor');
 assert.equal(ids.repairDeviceCode.textContent,'BBBB-2222','A hung invalidated request must not block the current code');
 if(OLD_ERROR)rejectControl(Error('Late obsolete transport failure'));
 else releaseControl({...control,operation_id:'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',device_code:'AAAA-1111'});
 await oldRead;await tick();await tick();
 assert(controlReads>=3,'Returning during an old /control read must queue a current successor without waiting for a polling timer');
 assert(!ids.repairDeviceLogin.hidden);
 assert.equal(ids.repairDeviceCode.textContent,'BBBB-2222','The invalidated old control snapshot must not restore its code');
 assert.equal(opened,0,'Return visibility polling must not reopen the OpenAI page');
 console.log('PASS held control read has an owned current successor on return');
})().catch(error=>{console.error(error);process.exitCode=1});
'''
    _run_source_case(boundary, case.replace('OLD_ERROR', 'true' if old_error else 'false'))


@pytest.mark.parametrize('old_error', [False, True])
def test_wizard_return_refreshes_code_and_ignores_obsolete_poll(old_error):
    node(r'''
const {harness,tick}=require('./autosourcarr/tests/ui_setup.js');const assert=require('assert/strict');
(async()=>{
 const h=harness(),base=h.context.api;let hold=false,finish,fail,reads=0;
 h.data.fields.push({key:'REPAIR_AI_PROVIDER',kind:'text',step:'repair',label:'AI provider',value:'chatgpt'});
 let state={busy:true,action:'login',operation_id:'00000000-0000-4000-8000-000000000001',device_code:'AAAA-11111',verification_url:'https://auth.openai.com/codex/device',expires_at:Date.now()/1000+900};
 h.context.api=async(path,options)=>{if(path.endsWith('/control')){reads++;if(hold){hold=false;return new Promise((resolve,reject)=>{finish=resolve;fail=reject;});}return state;}return base(path,options);};
 let opened=0;h.context.open=()=>{opened++;return null;};
 await h.context.AutoSourcarrSetup.openRepairAI();await tick();
 const old=structuredClone(state);hold=true;h.timers.at(-1)();await tick();
 h.context.document.hidden=true;h.context.document.events.visibilitychange?.();
 state={...state,device_code:'BBBB-22222'};h.context.document.hidden=false;
 assert.equal(typeof h.context.document.events.visibilitychange,'function','Wizard must refresh account state when returning from the login tab');
 h.context.document.events.visibilitychange();await tick();await tick();
 assert(reads>=3);assert(h.nodes.get('setupDeviceCode').innerHTML.includes('BBBB-22222'));
 if(OLD_ERROR)fail(Error('Obsolete connection failure'));else finish(old);await tick();await tick();
 assert(h.nodes.get('setupDeviceCode').innerHTML.includes('BBBB-22222'),'Obsolete poll cannot replace the fresh code');
 assert(!h.nodes.get('setupAccountStatus').textContent.includes('Obsolete'));assert.equal(opened,0,'Foreground refresh does not open another page');
})().catch(error=>{console.error(error);process.exitCode=1});
'''.replace('OLD_ERROR', 'true' if old_error else 'false'))


def test_wizard_foreground_return_recovers_held_post_login_read_and_keeps_cancel_usable():
    program = 'const {harness,tick}=require(' + json.dumps(str(ROOT / 'autosourcarr/tests/ui_setup.js')) + ');\n' + r'''
const assert=require('node:assert/strict');
(async()=>{
 const h=harness(),base=h.context.api,official='https://auth.openai.com/codex/device';
 h.data.fields.push({key:'REPAIR_AI_PROVIDER',step:'repair',kind:'choice',label:'AI provider',options:[['chatgpt','ChatGPT']],value:'chatgpt'});
 let state={busy:false},controlReads=0,cancels=0,holdNext=false,finishOld,opened=0;
 const oldRead=new Promise(resolve=>finishOld=resolve);
 h.context.open=()=>({opener:'caller',location:{replace:url=>{
  assert.equal(url,official);opened++;queueMicrotask(()=>{h.context.document.hidden=true;h.context.document.events.visibilitychange();});
 }}});
 h.context.api=async(path,options={})=>{
  if(path.endsWith('/control/login')){
   state={busy:true,state:'running',action:'login',operation_id:JSON.parse(options.body).operation_id,message:'Requesting code',device_code:'',verification_url:'',expires_at:0};holdNext=true;return state;
  }
  if(path.endsWith('/control/cancel-login')){assert.equal(JSON.parse(options.body).operation_id,state.operation_id);cancels++;return state={busy:false,state:'cancelled',message:'Cancelled'};}
  if(path.endsWith('/control')){controlReads++;if(holdNext){holdNext=false;return oldRead;}return state;}
  return base(path,options);
 };
 await h.context.AutoSourcarrSetup.openRepairAI();await tick();
 const pending=h.nodes.get('setupAccount-login').events.click();await tick();await tick();
 assert(h.context.document.hidden&&controlReads===2,'Popup is in front and the post-login control read is held');
 state={...state,device_code:'ABCD-EFGH',verification_url:official,expires_at:Date.now()/1000+900};
 h.context.document.hidden=false;h.context.document.events.visibilitychange();await tick();await tick();
 assert(controlReads>=3,'Returning to the wizard must refresh accepted login independently of its held reconciliation read');
 assert(h.nodes.get('setupDeviceCode').innerHTML.includes('ABCD-EFGH'),'The current code appears before the obsolete GET settles');
 await h.nodes.get('setupAccount-cancel-login').events.click();await tick();
 assert.equal(cancels,1,'Cancel is usable after login acceptance even if an obsolete GET is hanging');
 assert.equal(h.nodes.get('setupDeviceCode').innerHTML,'');
 finishOld({...state,busy:true,action:'login',device_code:'AAAA-1111',verification_url:official,expires_at:Date.now()/1000+900});
 await pending;await tick();assert.equal(h.nodes.get('setupDeviceCode').innerHTML,'','The old GET cannot revive the cancelled login code');
 assert.equal(opened,1,'Return and cancellation never open another popup');
 console.log('PASS wizard held post-login read recovery and scoped cancellation');
})().catch(error=>{console.error(error);process.exitCode=1});
'''
    result = subprocess.run(['node', '-e', program], cwd=ROOT, text=True, capture_output=True, timeout=10)
    assert result.returncode == 0, result.stdout + result.stderr



@pytest.mark.parametrize('late_failure', ['network', 'unavailable'])
def test_late_source_status_failure_cannot_erase_newer_independent_control_code(late_failure):
    boundary = r'''
let token='',adminAuthDismissed=false;const clientErrors=[];
const scheduleMutationRefresh=()=>{},rememberClientError=()=>{};
const official='https://auth.openai.com/codex/device';
let holdStatus=false,finishStatus,rejectStatus,controlReads=0;
const statusWait=new Promise((resolve,reject)=>{finishStatus=resolve;rejectStatus=reject;});
control={busy:true,state:'running',action:'login',operation_id:'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',message:'Enter code',device_code:'AAAA-1111',verification_url:official,expires_at:Date.now()/1000+900};
const fetch=async(path,options={})=>{
 requests.push({path,opts:options});let data;
 if(path==='/api/admin/source-maintenance'){
  if(holdStatus){holdStatus=false;return statusWait;}
  data={mode:'integrated',configured:true,reachable:true,web_control:true,ai_provider:{provider:'chatgpt'},jobs:[],watch:{}};
 }else if(path.endsWith('/control')){controlReads++;data=control;}
 else if(path.endsWith('/installed'))data={items:[]};
 else throw Error('Unexpected HTTP request '+path);
 return {status:200,ok:true,headers:{get:()=>''},json:async()=>data};
};
'''
    case = r'''
(async()=>{
 active=true;await loadSourceRepairs();holdStatus=true;
 const oldStatus=loadSourceRepairs();await tick();
 control={...control,device_code:'BBBB-2222'};
 await click('refreshSourceRepairs');await tick();
 assert.equal(controlReads,2,'An explicit refresh uses independent control polling while the old status request is held');
 assert.equal(ids.repairDeviceCode.textContent,'BBBB-2222');
 if(NETWORK_FAILURE)rejectStatus(Error('Old source-status timeout'));
 else finishStatus({status:200,ok:true,headers:{get:()=>''},json:async()=>({mode:'integrated',configured:true,reachable:false,web_control:false,service:{state:'unavailable',message:'Old source-status timeout'},jobs:[],watch:{}})});
 await oldStatus;await tick();
 assert(!ids.repairDeviceLogin.hidden,'A late source-job status failure cannot hide the newer successful account read');
 assert.equal(ids.repairDeviceCode.textContent,'BBBB-2222');
 assert.match(ids.sourceRepairStatus.textContent,/Old source-status timeout/,'Job status failure remains visible in its own status');
 console.log('PASS source job status failure is independent from current account code');
})().catch(error=>{console.error(error);process.exitCode=1});
'''
    _run_source_case(boundary, case.replace('NETWORK_FAILURE', 'true' if late_failure == 'network' else 'false'))



if __name__ == '__main__':
    test_return_from_openai_tab_polls_code_without_waiting_for_installed_inventory()
    test_return_during_held_control_read_schedules_current_successor(False)
    test_return_during_held_control_read_schedules_current_successor(True)
