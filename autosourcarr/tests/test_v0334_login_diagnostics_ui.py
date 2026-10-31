"""Both account screens display safe, copyable facts for failed code requests."""
from test_v086_ui import node
from test_v0315_repair_ui import DOM, SOURCE, STATIC


def test_source_account_failure_displays_and_copies_only_selected_facts():
    node(DOM+SOURCE+r'''
(async()=>{
 let copied;Object.defineProperty(globalThis,'navigator',{value:{clipboard:{writeText:async text=>{copied=text;}}},configurable:true});
 active=true;control={busy:false,action:'login',state:'failed',message:'Request rejected',diagnostics:{reason:'request-denied',phase:'requesting-code',exit_code:1,http_status:403,code_received:false,access_token:'private-secret',raw_output:'private-cookie'}};
 await loadSourceRepairs();const details=ids.repairAccountDiagnostics.children[0];
 assert(details.open,'The immediate failure is visible without another click');
 assert.equal(details.children[0].textContent,'Account error details');
 await details.children[2].listeners.click();const info=JSON.parse(copied);
 assert.deepEqual(info,{reason:'request-denied',phase:'requesting-code',exit_code:1,http_status:403,code_received:false});
 assert(!copied.includes('private-secret')&&!copied.includes('private-cookie'));
 assert(ids.repairDeviceLogin.hidden&&!ids.repairLogin.disabled);
 await click('repairLogin');assert.equal(ids.repairAccountDiagnostics.children.length,0,'A new attempt clears old diagnostics');
 assert(!ids.repairDeviceLogin.hidden,'The normal visible-code flow still works');
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_wizard_error_survives_return_from_popup_and_copies_safe_details():
    node(r'''
const {harness,tick}=require('./autosourcarr/tests/ui_setup.js');const assert=require('assert/strict');
(async()=>{
 const h=harness();let copied;h.context.navigator={clipboard:{writeText:async value=>{copied=value;}}};
 h.data.fields.push({key:'REPAIR_AI_PROVIDER',step:'repair',kind:'choice',label:'AI',options:[['chatgpt','ChatGPT']],value:'chatgpt'});
 const base=h.context.api,state={action:'login',state:'failed',busy:false,message:'Invalid response',diagnostics:{reason:'invalid-response',phase:'requesting-code',exit_code:1,http_status:null,code_received:false,raw_output:'private-secret'}};
 h.context.api=async(path,opts)=>path.endsWith('/control')?state:base(path,opts);
 await h.context.AutoSourcarrSetup.openRepairAI();await tick();
 h.context.document.hidden=true;h.context.document.events.visibilitychange();
 h.context.document.hidden=false;h.context.document.events.visibilitychange();await tick();await tick();
 const details=h.nodes.get('setupAccountDiagnostics').children[0];assert(details.open);
 await details.children[2].events.click();assert.equal(JSON.parse(copied).reason,'invalid-response');assert(!copied.includes('private-secret'));
 assert(h.nodes.get('setupAccountStatus').textContent.includes('Invalid response'));
 assert.equal(h.nodes.get('setupDeviceCode').innerHTML,'');
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_diagnostic_renderer_rejects_unexpected_text_and_hides_stale_states():
    helper=(STATIC/'repair-login.js').read_text()
    node(DOM+helper+r'''
const safe={reason:'request-denied',phase:'requesting-code',exit_code:1,http_status:403,code_received:false};
for(const bad of [{...safe,reason:'private-secret'},{...safe,phase:'private-cookie'}, {...safe,code_received:'private-secret'}]){
 AutoSourcarrRepairLogin.renderDiagnostics(ids.repairAccountDiagnostics,{action:'login',state:'failed',diagnostics:bad});
 assert.equal(ids.repairAccountDiagnostics.children.length,0);
}
for(const state of ['running','completed','cancelled','idle']){
 AutoSourcarrRepairLogin.renderDiagnostics(ids.repairAccountDiagnostics,{action:'login',state,diagnostics:safe});
 assert.equal(ids.repairAccountDiagnostics.children.length,0);
}
const info=AutoSourcarrRepairLogin.diagnostics({action:'login',state:'failed',diagnostics:{...safe,exit_code:'private-secret',http_status:'private-cookie'}});
assert.equal(info.exit_code,null);assert.equal(info.http_status,null);
console.log('PASS diagnostics whitelist and stale-state rejection');
''')


def test_local_launcher_facts_are_copyable_but_untrusted_values_are_omitted():
    helper=(STATIC/'repair-login.js').read_text()
    node(DOM+helper+r'''
const state={action:'login',state:'failed',diagnostics:{reason:'resource-limit',phase:'starting',exit_code:70,http_status:null,code_received:false,launcher_stage:'exec',os_error:11,raw_output:'private-secret'}};
const data=AutoSourcarrRepairLogin.diagnostics(state);
assert.equal(data.launcher_stage,'exec');assert.equal(data.os_error,11);assert(!JSON.stringify(data).includes('private-secret'));
for(const bad of [{launcher_stage:'private-path',os_error:'private-error'},{launcher_stage:{},os_error:true},{launcher_stage:[],os_error:5000}]){
 const value=AutoSourcarrRepairLogin.diagnostics({...state,diagnostics:{...state.diagnostics,...bad}});
 assert(!Object.hasOwn(value,'launcher_stage'));assert(!Object.hasOwn(value,'os_error'));
}
console.log('PASS bounded local launcher metadata');
''')

