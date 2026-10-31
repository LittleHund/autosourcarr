"""Explicit login clicks open the official page before the asynchronous POST."""
from test_v086_ui import node
from test_v0315_repair_ui import DOM, SOURCE, STATIC


def test_login_opens_official_page_with_immediate_progress_and_visible_code():
    helper = (STATIC / 'repair-login.js').read_text()
    fixture = DOM.replace("if(path.endsWith('/control/login'))return control=", "if(path.endsWith('/control/login')){await loginWait;}\n if(path.endsWith('/control/login'))return control=")
    node(fixture + helper + SOURCE + r'''
(async()=>{
 let finish;const loginWait=new Promise(r=>finish=r);globalThis.loginWait=loginWait;
 const opened=[];globalThis.open=(url,target)=>{assert.equal(url,'about:blank');assert.equal(target,'_blank');const popup={opener:'caller',location:{replace:url=>{assert.equal(popup.opener,null);opened.push(url);}}};return popup;};
 active=true;await loadSourceRepairs();const pending=click('repairLogin');
 assert.deepEqual(opened,['https://auth.openai.com/codex/device']);
 assert.match(ids.repairControlStatus.textContent,/Starting ChatGPT sign-in/);
 assert(!ids.repairDeviceLogin.hidden,'Fallback page link is available before the code');
 assert.match(ids.repairDeviceExpiry.textContent,/Waiting.*code/i);
 finish();await pending;assert.equal(ids.repairDeviceCode.textContent,'ABCD-12345');
 assert.equal(opened.length,1);await loadSourceRepairs();assert.equal(opened.length,1,'Polling does not open another page');
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_popup_blocked_fallback_and_login_failure_remain_visible():
    helper = (STATIC / 'repair-login.js').read_text()
    fixture = DOM.replace("if(path.endsWith('/control/login'))return control=", "if(path.endsWith('/control/login'))throw Error('Login request failed');\n if(path.endsWith('/control/login'))return control=")
    node(fixture + helper + SOURCE + r'''
(async()=>{
 globalThis.open=()=>null;active=true;await loadSourceRepairs();
 const pending=click('repairLogin');assert.match(ids.repairDeviceExpiry.textContent,/browser.*Open.*sign-in page/i);
 await pending;assert.equal(ids.repairControlStatus.textContent,'Login request failed');
 assert(ids.repairDeviceLogin.hidden);assert(!ids.repairLogin.disabled,'Failure permits retry');
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_stale_source_control_read_cannot_clear_new_login_and_cancel_is_scoped():
    fixture = DOM.replace("return control;}", "if(globalThis.oldRead){const wait=oldRead;oldRead=null;return wait;}return control;}")
    node(fixture + SOURCE + r'''
(async()=>{
 active=true;await loadSourceRepairs();let release;globalThis.oldRead=new Promise(r=>release=r);
 const pending=loadSourceRepairs();await tick();await click('repairLogin');
 const operation=JSON.parse(requests.find(x=>x.path.endsWith('/control/login')).opts.body).operation_id;
 release({busy:false});await pending;assert.equal(ids.repairDeviceCode.textContent,'ABCD-12345','Old GET cannot erase a newer accepted login');
 await click('repairCancelLogin');assert.equal(JSON.parse(requests.find(x=>x.path.endsWith('/control/cancel-login')).opts.body).operation_id,operation);
 assert(ids.repairDeviceLogin.hidden);
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_stale_outer_source_status_cannot_hide_new_login():
    fixture=DOM.replace("if(path.endsWith('/source-maintenance'))return", "if(path.endsWith('/source-maintenance')&&globalThis.oldOuter){const wait=oldOuter;oldOuter=null;return wait;}\n if(path.endsWith('/source-maintenance'))return")
    node(fixture + SOURCE + r'''
(async()=>{
 active=true;await loadSourceRepairs();let release;globalThis.oldOuter=new Promise(r=>release=r);
 const pending=loadSourceRepairs();await tick();await click('repairLogin');
 release({mode:'integrated',configured:true,reachable:false,message:'Old worker unavailable'});await pending;
 assert.equal(ids.repairDeviceCode.textContent,'ABCD-12345');assert(!ids.repairDeviceLogin.hidden);assert.notEqual(ids.repairControlStatus.textContent,'Old worker unavailable');
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_wizard_opens_from_click_and_stale_poll_cannot_resurrect_cancelled_code():
    node(r'''
const {harness,tick}=require('./autosourcarr/tests/ui_setup.js');const assert=require('assert/strict');
(async()=>{
 const h=harness(),opened=[];let release,hold=false,state={busy:false};const base=h.context.api;
 h.context.open=()=>({opener:'caller',location:{replace:url=>opened.push(url)}});
 h.context.api=async(path,options)=>{
   if(path.endsWith('/control/login')){const operation=JSON.parse(options.body).operation_id;state={busy:true,action:'login',operation_id:operation,device_code:'ABCD-EFGH',verification_url:'https://auth.openai.com/codex/device',expires_at:Date.now()/1000+120};return state;}
   if(path.endsWith('/control/cancel-login')){assert.equal(JSON.parse(options.body).operation_id,state.operation_id);state={busy:false,state:'cancelled',message:'Cancelled'};return state;}
   if(path.endsWith('/control')){if(hold){hold=false;return new Promise(r=>release=r);}return state;}
   return base(path,options);
 };
 await h.context.AutoSourcarrSetup.open();for(let i=0;i<11&&h.nodes.get('setupTitle').textContent!=='Review and finish';i++)await h.next();await h.next();await tick();
 assert.equal(opened.length,0,'Background reads never open pages');
 const pending=h.nodes.get('setupAccount-login').events.click();assert.equal(opened[0],'https://auth.openai.com/codex/device');assert.match(h.nodes.get('setupAccountStatus').textContent,/Starting ChatGPT/);await pending;
 const previous={...state};hold=true;h.timers.at(-1)();await tick();
 await h.nodes.get('setupAccount-cancel-login').events.click();assert.equal(h.nodes.get('setupDeviceCode').innerHTML,'');
 release(previous);await tick();assert.equal(h.nodes.get('setupDeviceCode').innerHTML,'','Stale poll cannot revive cancelled code');assert.equal(opened.length,1);
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_wizard_blocked_popup_keeps_link_and_request_error_after_refresh():
    node(r'''
const {harness,tick}=require('./autosourcarr/tests/ui_setup.js');const assert=require('assert/strict');
(async()=>{
 const h=harness();let fail;const base=h.context.api;h.context.open=()=>null;
 h.context.api=async(path,options)=>{if(path.endsWith('/control/login'))return new Promise((resolve,reject)=>fail=reject);return base(path,options);};
 await h.context.AutoSourcarrSetup.open();for(let i=0;i<11&&h.nodes.get('setupTitle').textContent!=='Review and finish';i++)await h.next();await h.next();await tick();
 const pending=h.nodes.get('setupAccount-login').events.click();assert(h.nodes.get('setupDeviceCode').innerHTML.includes('Open sign-in page'));assert.match(h.nodes.get('setupDeviceCode').innerHTML,/browser did not open/);
 const error=new Error('OpenAI login could not start');error.status_code=409;fail(error);await pending;assert.equal(h.nodes.get('setupAccountStatus').textContent,error.message);assert.equal(h.nodes.get('setupDeviceCode').innerHTML,'');assert(!h.nodes.get('setupNext').disabled);
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_new_wizard_view_does_not_retain_previous_request_error():
    node(r'''
const {harness,tick}=require('./autosourcarr/tests/ui_setup.js');const assert=require('assert/strict');
(async()=>{
 const h=harness();h.data.fields.push({key:'REPAIR_AI_PROVIDER',step:'repair',kind:'choice',label:'AI',options:[['chatgpt','ChatGPT']],value:'chatgpt'});const base=h.context.api;let failed=true;
 h.context.api=async(path,options)=>{if(path.endsWith('/control/login')){const e=Error('Old request failure');e.status_code=409;throw e;}if(path.endsWith('/control'))return failed?{busy:false}:{busy:false,auth_saved:true,message:'Signed in successfully'};return base(path,options);};
 await h.context.AutoSourcarrSetup.openRepairAI();await tick();await h.nodes.get('setupAccount-login').events.click();assert.equal(h.nodes.get('setupAccountStatus').textContent,'Old request failure');
 await h.context.AutoSourcarrSetup.cancel();failed=false;await h.context.AutoSourcarrSetup.openRepairAI();await tick();assert.match(h.nodes.get('setupAccountStatus').textContent,/Signed in successfully/);
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_source_login_failure_keeps_request_ownership_until_reconciliation_finishes():
    fixture=DOM.replace("return control;}", "if(globalThis.recover){recover=false;return new Promise(r=>globalThis.finishRecovery=r);}return control;}")
    fixture=fixture.replace("if(path.endsWith('/control/login'))return control=", "if(path.endsWith('/control/login')){recover=true;const e=Error('Login failed');e.status_code=409;throw e;}\n if(path.endsWith('/control/login'))return control=")
    node(fixture + SOURCE + r'''
(async()=>{
 active=true;await loadSourceRepairs();const pending=click('repairLogin');await tick();assert.equal(ids.repairControlStatus.textContent,'Login failed','Error appears before the recovery read completes');
 await click('refreshSourceRepairs');await tick();assert(ids.repairLogin.disabled,'Refresh cannot enable a second login while first recovery is pending');
 await click('repairLogin');assert.equal(requests.filter(x=>x.path.endsWith('/control/login')).length,1,'No second POST or popup while recovery owns the request');
 finishRecovery({busy:false});await pending;assert(!ids.repairLogin.disabled);assert.equal(ids.repairControlStatus.textContent,'Login failed');
})().catch(e=>{console.error(e);process.exitCode=1});
''')
