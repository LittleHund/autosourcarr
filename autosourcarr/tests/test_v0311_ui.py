"""Source preferences and APK installation exercise shipped UI behavior, not a live engine."""
from pathlib import Path
from test_v086_ui import node, block

SOURCE=(Path(__file__).resolve().parents[1]/'app/static/source-controls.js').read_text()
DOM=r'''
const assert=require('node:assert/strict'),window=globalThis;
class Node {
 constructor(tag='div'){this.tagName=tag.toUpperCase();this.children=[];this.dataset={};this.listeners={};this.value='';this.textContent='';this.disabled=false;this.hidden=false;this.checked=false;this.classList={add:()=>{},remove:()=>{}};}
 append(...items){for(const child of items){child.parent=this;this.children.push(child);}}
 replaceChildren(...items){this.children=[];this.append(...items);}
 setAttribute(key,value){this[key]=value;}
 addEventListener(type,fn){this.listeners[type]=fn;}
 contains(item){return item===this||this.children.some(child=>child.contains(item));}
 querySelectorAll(selector){const matches=n=>selector.split(',').some(s=>{const tag=s.trim().match(/^\w+/)?.[0];return tag&&n.tagName===tag.toUpperCase()&&(!s.includes(':not([disabled])')||!n.disabled);});return this.children.flatMap(n=>[...(matches(n)?[n]:[]),...n.querySelectorAll(selector)]);}
 focus(){document.activeElement=this;}
 click(){return this.onclick?.({target:this});}
}
const ids=Object.fromEntries(['sourcePreferencesModal','sourcePreferencesTitle','sourcePreferencesVariant','sourcePreferencesClose','sourcePreferencesStatus','sourcePreferencesFields','sourcePreferencesSave','sourcePreferencesDiscard','sourcePreferencesReload','sourcePreferencesForm','sourceApkStatus','sourceApkResults','sourceApkClear','sourceApkDropZone','sourceApkFiles','sourceApkBrowse'].map(id=>[id,new Node()]));
ids.sourcePreferencesModal.hidden=true;
const document={activeElement:new Node('button'),getElementById:id=>ids[id],createElement:tag=>new Node(tag)};
const sourceGroups=[{name:'Example',variants:[{source_id:'9223372036854775806',name:'Example',lang:'en',available:true},{source_id:'22',name:'Example',lang:'nl',available:true}]}];
const langLabel=String;let refreshes=0;const refreshManagedSourcesAfterMutation=()=>{refreshes++},syncModalScrollLock=()=>{};
const requests=[],pending=[];const api=(url,opts={})=>{requests.push({url,opts});return new Promise((resolve,reject)=>pending.push({resolve,reject}));};
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const pref=(index,type,value,extra={})=>({index,key:'key'+index,type,value,title:'Setting '+index,enabled:true,visible:true,...extra});
const snapshot=(preferences,revision='r1')=>({source_id:'9223372036854775806',configurable:true,revision,preferences});
const inputFor=index=>ids.sourcePreferencesFields.querySelectorAll('input,select').find(n=>n.id==='sourcePreference-'+index);
const formSubmit=()=>ids.sourcePreferencesForm.onsubmit({preventDefault(){}});
'''


def test_preferences_render_native_types_hidden_disabled_and_secret_fields():
    node(DOM+SOURCE+r'''
(async()=>{
 const opening=openSourcePreferences(0);assert(requests[0].url.includes('9223372036854775806/preferences'));
 pending.shift().resolve(snapshot([pref(0,'switch',true),pref(1,'text','hiddenvalue',{key:'password',summary:'Saved password: hiddenvalue'}),pref(2,'list','b',{entries:['Alpha','Beta'],entry_values:['a','b']}),pref(3,'multiselect',['a'],{entries:['Alpha','Beta'],entry_values:['a','b']}),pref(4,'text','do not edit',{enabled:false}),pref(5,'text','hidden',{visible:false}),pref(6,'unknown',null)]));await opening;
 assert.equal(inputFor(0).checked,true);assert.equal(inputFor(1).type,'password');assert.equal(inputFor(1).value,'hiddenvalue');assert.equal(inputFor(2).value,'b');assert.equal(inputFor(4).disabled,true);assert.equal(inputFor(5),undefined);
 assert.equal(ids.sourcePreferencesFields.children.length,6);assert(!ids.sourcePreferencesFields.children[1].children.some(n=>n.textContent.includes('hiddenvalue')),'Password summaries must not expose the current secret');
 const checks=ids.sourcePreferencesFields.children[3].querySelectorAll('input');assert.equal(checks[0].checked,true);checks[1].checked=true;checks[1].onchange();assert.equal(ids.sourcePreferencesSave.disabled,false);
 assert.equal(requests[0].opts.suppressClientError,true,'Sensitive preference details do not enter global browser diagnostics');
 const saving=formSubmit();assert.deepEqual(JSON.parse(requests[1].opts.body),{index:3,key:'key3',type:'multiselect',value:['a','b'],revision:'r1'});
 pending.shift().resolve(snapshot([pref(3,'multiselect',['a','b'],{entries:['Alpha','Beta'],entry_values:['a','b']})],'r2'));await saving;
 assert.equal(ids.sourcePreferencesSave.disabled,true);
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_preference_navigation_discards_stale_responses_but_keeps_unsaved_drafts():
    node(DOM+SOURCE+r'''
(async()=>{
 const opening=openSourcePreferences(0);const first=pending.shift();
 ids.sourcePreferencesVariant.value='22';const secondLoad=ids.sourcePreferencesVariant.onchange({target:ids.sourcePreferencesVariant});assert.equal(requests[0].opts.signal.aborted,true);
 pending.shift().resolve(snapshot([pref(0,'text','Dutch')],'nl'));await secondLoad;
 first.resolve(snapshot([pref(0,'text','Old English')],'en'));await opening;assert.equal(inputFor(0).value,'Dutch');
 inputFor(0).value='Dutch draft';inputFor(0).oninput();closeSourcePreferences();assert.equal(ids.sourcePreferencesModal.hidden,true);
 const reopened=openSourcePreferences(0);pending.shift().resolve(snapshot([pref(0,'text','English')]));await reopened;
 ids.sourcePreferencesVariant.value='22';const returned=ids.sourcePreferencesVariant.onchange({target:ids.sourcePreferencesVariant});pending.shift().resolve(snapshot([pref(0,'text','Latest Dutch')],'nl2'));await returned;
 assert.equal(inputFor(0).value,'Dutch draft');assert.equal(ids.sourcePreferencesSave.disabled,false);
 ids.sourcePreferencesDiscard.click();assert.equal(inputFor(0).value,'Latest Dutch');assert.equal(ids.sourcePreferencesSave.disabled,true);
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_preference_sequential_revision_partial_failure_and_reopen_during_save():
    node(DOM+SOURCE+r'''
(async()=>{
 const opening=openSourcePreferences(0);pending.shift().resolve(snapshot([pref(0,'text','old'),pref(1,'checkbox',false)]));await opening;
 inputFor(0).value='new';inputFor(0).oninput();inputFor(1).checked=true;inputFor(1).onchange();
 const saving=formSubmit();assert.equal(inputFor(0).disabled,true);pending.shift().resolve(snapshot([pref(0,'text','new'),pref(1,'checkbox',false)],'r2'));await tick();
 assert.equal(JSON.parse(requests.at(-1).opts.body).revision,'r2');assert.equal(requests.at(-1).opts.suppressClientError,true);
 closeSourcePreferences();await openSourcePreferences(0);assert.equal(requests.length,3,'Reopening while save runs does not load a stale preference snapshot');
 pending.shift().reject(Error('Source changed; reload settings'));await saving;
 assert.equal(inputFor(0).value,'new');assert.equal(inputFor(1).checked,true);assert.equal(inputFor(0).disabled,false);assert.equal(ids.sourcePreferencesSave.disabled,false);
 assert(ids.sourcePreferencesStatus.textContent.includes('1 setting(s) saved'));assert(ids.sourcePreferencesStatus.textContent.includes('retained'));
 const reload=ids.sourcePreferencesReload.click();pending.shift().resolve(snapshot([pref(0,'text','new'),pref(1,'checkbox',false)],'r3'));await reload;
 const retry=formSubmit();assert.equal(JSON.parse(requests.at(-1).opts.body).index,1);assert.equal(JSON.parse(requests.at(-1).opts.body).revision,'r3');pending.shift().resolve(snapshot([pref(0,'text','new'),pref(1,'checkbox',true)],'r4'));await retry;assert.equal(ids.sourcePreferencesSave.disabled,true);
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_apk_drop_serializes_files_continues_after_failure_and_preserves_results():
    node(DOM+SOURCE+r'''
(async()=>{
 const files=[{name:'one.apk',size:100},{name:'not-a-source.zip',size:100},{name:'empty.apk',size:0},{name:'huge.apk',size:32*1024*1024+1},{name:'two APK.apk',size:100}];
 let prevented=false;ids.sourceApkDropZone.listeners.drop({preventDefault(){prevented=true},dataTransfer:{files}});assert(prevented);assert.equal(requests.length,1);assert.equal(requests[0].opts.body,files[0]);assert.equal(requests[0].opts.headers['Content-Type'],'application/vnd.android.package-archive');
 assert.equal(ids.sourceApkResults.children.filter(n=>n.dataset.state==='failed').length,3);
 pending.shift().reject(Error('Engine rejected package'));await tick();assert.equal(requests.length,2);assert.equal(requests[1].opts.headers['X-Filename'],'two%20APK.apk');
 const third={name:'third.apk',size:200};ids.sourceApkFiles.files=[third];ids.sourceApkFiles.onchange();assert.equal(requests.length,2,'Files selected during install wait their turn');
 pending.shift().resolve({message:'Installed'});await tick();assert.equal(requests.length,3);pending.shift().resolve({message:'Installed'});await tick();
 assert.equal(refreshes,1);assert.equal(ids.sourceApkResults.children.length,6);assert(ids.sourceApkResults.children[0].textContent.includes('Engine rejected'));assert.equal(ids.sourceApkClear.disabled,false);ids.sourceApkClear.click();assert.equal(ids.sourceApkResults.children.length,0);
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_api_preserves_explicit_upload_content_type_and_raw_file():
    code=block('async function api(url, opts = {}) {',"window.addEventListener('unhandledrejection'")
    node(r'''
const assert=require('node:assert/strict');let token='admin',clientErrors=[];const scheduleMutationRefresh=()=>{};let sent;
const fetch=async(url,opts)=>{sent=opts;return {ok:true,status:200,headers:{get:()=>''},json:async()=>({ok:true})};};
'''+code+r'''
(async()=>{const file={bytes:'apk'};await api('/api/admin/source-extensions/upload',{method:'POST',headers:{'Content-Type':'application/vnd.android.package-archive','X-Filename':'example.apk'},body:file,noMutationRefresh:true});assert.equal(sent.headers['Content-Type'],'application/vnd.android.package-archive');assert.equal(sent.body,file);assert.equal(sent.headers['X-Admin-Token'],'admin');await api('/api/admin/sources',{body:'{}'});assert.equal(sent.headers['Content-Type'],'application/json');})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_apk_timeout_requests_inventory_check_without_retrying_install():
    node(DOM+SOURCE+r'''
(async()=>{
 ids.sourceApkFiles.files=[{name:'uncertain.apk',size:1000}];ids.sourceApkFiles.onchange();assert.equal(requests[0].opts.timeoutMs,240000);
 const error=Error('Request timed out');error.name='TimeoutError';pending.shift().reject(error);await tick();
 assert.equal(requests.length,1,'An uncertain installation is not automatically submitted again');assert.equal(refreshes,1);assert.equal(ids.sourceApkResults.children[0].dataset.state,'unconfirmed');assert(ids.sourceApkResults.children[0].textContent.includes('before retrying'));
})().catch(e=>{console.error(e);process.exitCode=1});
''')
