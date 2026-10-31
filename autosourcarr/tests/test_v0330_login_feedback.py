"""A login failure belongs to the explicit attempt even when its popup hides the app."""
import os
from pathlib import Path
import subprocess
import sys

import pytest


_default_root = Path(__file__).resolve().parents[2]
ROOT = Path(os.environ.get('AUTOSOURCARR_REPRO_ROOT', _default_root))
sys.path.insert(0, str(ROOT / 'autosourcarr/tests'))
from test_v0315_repair_ui import DOM


BOUNDARY = r'''
let token='',adminAuthDismissed=false;const clientErrors=[];
const scheduleMutationRefresh=()=>{},rememberClientError=()=>{};
const official='https://auth.openai.com/codex/device',failure='The sign-in request failed';
let popupHides=true,opened=0,attempt='',holdCancel=false,finishCancel;
const cancelWait=new Promise(resolve=>finishCancel=resolve);
globalThis.open=()=>({opener:'caller',location:{replace:url=>{
 assert.equal(url,official);opened++;
 if(popupHides)queueMicrotask(()=>{document.hidden=true;document.listeners.visibilitychange();});
}}});
const fetch=async(path,options={})=>{
 requests.push({path,opts:options});let data,status=200;
 if(path==='/api/admin/source-maintenance')data={mode:'integrated',configured:true,reachable:true,web_control:true,ai_provider:{provider:'chatgpt'},jobs:[],watch:{}};
 else if(path.endsWith('/installed'))data={items:[]};
 else if(path.endsWith('/control'))data={busy:false,state:'idle',message:'Ready'};
 else if(path.endsWith('/control/login')){
  attempt=JSON.parse(options.body).operation_id;
  if(OUTCOME==='network')throw Error(failure);
  status=OUTCOME==='http409'?409:503;data={detail:failure};
 }else if(path.endsWith('/control/cancel-login')){
  status=202;if(holdCancel)return {status,ok:true,headers:{get:()=>''},json:()=>cancelWait};
  data={busy:false,state:'cancelled',action:'login',message:'Cancelled'};
 }
 else throw Error('Unexpected HTTP request '+path);
 return {status,ok:status<400,headers:{get:()=>''},text:async()=>JSON.stringify(data),json:async()=>data};
};
'''


def run_case(outcome, case):
    static = ROOT / 'autosourcarr/app/static'
    fixture = DOM[:DOM.index('const api=async')] + DOM[DOM.index('const click='):]
    app = (static / 'app.js').read_text()
    api = app[app.index('async function api('):app.index("window.addEventListener('unhandledrejection'")]
    source = (static / 'repair-login.js').read_text() + (static / 'source-repairs.js').read_text()
    program = fixture + BOUNDARY.replace('OUTCOME', repr(outcome)) + api + source
    program += '(async()=>{\n' + case + '\n})().catch(error=>{console.error(error);process.exitCode=1});'
    result = subprocess.run(['node', '-e', program], cwd=ROOT, text=True, capture_output=True, timeout=10)
    assert result.returncode == 0, result.stdout + result.stderr


@pytest.mark.parametrize('outcome', ['http503', 'http409', 'network'])
def test_popup_hidden_login_failure_remains_visible_after_idle_return(outcome):
    run_case(outcome, r'''
 active=true;await loadSourceRepairs();assert.equal(ids.repairControlStatus.textContent,'Ready');
 await click('repairLogin');assert(document.hidden,'The explicit popup foregrounds OpenAI');
 document.hidden=false;document.listeners.visibilitychange();await tick();await tick();
 assert.equal(ids.repairControlStatus.textContent,failure,'The owned login failure must survive foreground return and a successful idle /control read');
 assert(ids.repairDeviceLogin.hidden,'A failed request cannot show an unrelated device code');
 assert(!ids.repairLogin.disabled,'A settled failure permits a fresh explicit attempt');
 const cancels=requests.filter(request=>request.path.endsWith('/control/cancel-login'));
 if('OUTCOME'==='network')assert.equal(cancels.length,1,'Ambiguous network failure must best-effort cancel its own attempt');
 if('OUTCOME'==='http409')assert.equal(cancels.length,0,'A definitive worker rejection must not cancel an operation');
 // Main proxy HTTP503 may mean its worker response was lost after admission.
 // Whatever that contract chooses, cancellation must remain operation-scoped.
 for(const cancel of cancels)assert.equal(JSON.parse(cancel.opts.body).operation_id,attempt);
 assert.equal(requests.filter(request=>request.path.endsWith('/control/login')).length,1,'Return polling never replays login');
 assert.equal(opened,1,'Return polling never opens another popup');
 console.log('PASS hidden '+ 'OUTCOME' +' failure retained with owned cancellation');
'''.replace('OUTCOME', outcome))


def test_already_painted_failure_is_not_erased_by_later_page_hide():
    run_case('http409', r'''
 active=true;await loadSourceRepairs();popupHides=false;
 await click('repairLogin');assert.equal(ids.repairControlStatus.textContent,failure,'The current view initially displays its request error');
 document.hidden=true;document.listeners.visibilitychange();
 document.hidden=false;document.listeners.visibilitychange();await tick();await tick();
 assert.equal(ids.repairControlStatus.textContent,failure,'Visibility changes alone must not discard the owned request error');
 assert.equal(requests.filter(request=>request.path.endsWith('/control/cancel-login')).length,0);
 assert.equal(opened,1);
 console.log('PASS later hide does not erase an observed login rejection');
''')


def test_return_paints_known_failure_while_owned_recovery_cancel_is_held():
    run_case('network', r'''
 active=true;await loadSourceRepairs();holdCancel=true;
 const pending=click('repairLogin');await tick();await tick();
 assert(document.hidden);
 const cancel=requests.find(request=>request.path.endsWith('/control/cancel-login'));
 assert(cancel,'The ambiguous response initiates owned recovery');
 assert.equal(JSON.parse(cancel.opts.body).operation_id,attempt);
 document.hidden=false;document.listeners.visibilitychange();await tick();await tick();
 assert.equal(ids.repairControlStatus.textContent,failure,'An already-known error must appear on return before its cancellation response settles');
 assert(ids.repairLogin.disabled&&ids.repairLogout.disabled&&ids.repairPrepare.disabled,'Recovery retains its mutation lock');
 assert(ids.repairDeviceLogin.hidden,'An ambiguous failure does not reveal an old code');
 await click('refreshSourceRepairs');await tick();
 assert.equal(ids.repairControlStatus.textContent,failure,'Refresh cannot erase the failure while its recovery is pending');
 await click('repairLogin');
 assert.equal(requests.filter(request=>request.path.endsWith('/control/login')).length,1,'No replay or competing login while cancel is held');
 assert.equal(requests.filter(request=>request.path.endsWith('/control/cancel-login')).length,1);
 assert.equal(opened,1);
 finishCancel({busy:false,state:'cancelled',action:'login',message:'Cancelled'});await pending;await tick();
 assert.equal(ids.repairControlStatus.textContent,failure,'An idle recovery snapshot retains the original request failure');
 assert(!ids.repairLogin.disabled,'A settled recovery releases controls for an explicit retry');
 console.log('PASS failure visible during a held UUID-scoped cancellation');
''')
