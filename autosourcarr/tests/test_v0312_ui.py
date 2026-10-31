"""Discover opens native source preferences directly, including archived packages."""
from pathlib import Path
from test_v086_ui import block, node
from test_v0311_ui import DOM

STATIC = Path(__file__).resolve().parents[1] / 'app' / 'static'
SOURCE = (STATIC / 'source-controls.js').read_text()
DISCOVER = r'''
let sourceExtensionState={extensions:[],stores:[]};
const installed=(pkg='eu.kanade.tachiyomi.extension.all.example',extra={})=>({pkgName:pkg,name:'Example',isInstalled:true,source:{nodes:[{id:'9223372036854775806',name:'Example',lang:'en',isConfigurable:true},{id:'-9223372036854775806',name:'Example',lang:'nl',isConfigurable:true}]},...extra});
'''


def test_discover_settings_use_live_inventory_ids_without_source_family_screen():
    node(DOM + DISCOVER + SOURCE + r'''
(async()=>{
 sourceGroups.splice(0);sourceExtensionState.extensions=[installed()];const focus=document.activeElement;
 const opening=openExtensionSourcePreferences(installed().pkgName);
 assert.equal(requests.length,1);assert.equal(requests[0].url,'/api/v1/sources/9223372036854775806/preferences');
 assert.deepEqual(ids.sourcePreferencesVariant.children.map(n=>n.value),['9223372036854775806','-9223372036854775806']);
 pending.shift().resolve(snapshot([pref(0,'text','English')]));await opening;
 ids.sourcePreferencesVariant.value='-9223372036854775806';const language=ids.sourcePreferencesVariant.onchange({target:ids.sourcePreferencesVariant});
 assert.equal(requests[1].url,'/api/v1/sources/-9223372036854775806/preferences');
 pending.shift().resolve(snapshot([pref(0,'text','Dutch')],'nl1'));await language;
 inputFor(0).value='Dutch draft';inputFor(0).oninput();closeSourcePreferences();assert.equal(document.activeElement,focus);
 const reopening=openExtensionSourcePreferences(installed().pkgName);pending.shift().resolve(snapshot([pref(0,'text','English')]));await reopening;
 ids.sourcePreferencesVariant.value='-9223372036854775806';const again=ids.sourcePreferencesVariant.onchange({target:ids.sourcePreferencesVariant});
 pending.shift().resolve(snapshot([pref(0,'text','Dutch refreshed')],'nl2'));await again;assert.equal(inputFor(0).value,'Dutch draft');
 const saving=formSubmit();assert.equal(requests.at(-1).url,'/api/v1/sources/-9223372036854775806/preferences');
 assert.deepEqual(JSON.parse(requests.at(-1).opts.body),{index:0,key:'key0',type:'text',value:'Dutch draft',revision:'nl2'});
 pending.shift().resolve(snapshot([pref(0,'text','Dutch draft')],'nl3'));await saving;
 assert.equal(ids.sourcePreferencesSave.disabled,true);
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_installed_orphan_resolves_package_inventory_on_demand_and_no_settings_is_normal():
    node(DOM + DISCOVER + SOURCE + r'''
(async()=>{
 sourceGroups.splice(0);const opening=openExtensionSourcePreferences('original.archived');
 assert.equal(ids.sourcePreferencesModal.hidden,false);assert.equal(requests.length,1);assert.equal(requests[0].url,'/api/v1/source-extensions');
 pending.shift().resolve({extensions:[installed('original.archived',{name:'Archived source'})]});await tick();
 assert.equal(requests[1].url,'/api/v1/sources/9223372036854775806/preferences');
 pending.shift().resolve({...snapshot([]),configurable:false});await opening;
 assert.equal(ids.sourcePreferencesTitle.textContent,'Archived source settings');
 assert.equal(ids.sourcePreferencesFields.children[0].textContent,'This extension does not expose source settings.');
 assert.equal(ids.sourcePreferencesSave.disabled,true);assert.equal(requests.length,2);
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_discover_inventory_navigation_aborts_and_ignores_stale_package_results():
    node(DOM + DISCOVER + SOURCE + r'''
(async()=>{
 const first=openExtensionSourcePreferences('orphan.one'),late=pending.shift();
 sourceExtensionState.extensions=[installed('installed.two',{name:'Other source'})];
 const second=openExtensionSourcePreferences('installed.two');assert.equal(requests[0].opts.signal.aborted,true);
 pending.shift().resolve(snapshot([pref(0,'text','Keep this')]));await second;
 late.resolve({extensions:[installed('orphan.one',{name:'Stale source'})]});await first;
 assert.equal(ids.sourcePreferencesTitle.textContent,'Other source settings');assert.equal(inputFor(0).value,'Keep this');assert.equal(requests.length,2);
 const closing=openExtensionSourcePreferences('orphan.three');const last=pending.shift();closeSourcePreferences();
 assert.equal(requests[2].opts.signal.aborted,true);last.resolve({extensions:[installed('orphan.three')]});await closing;
 assert.equal(ids.sourcePreferencesModal.hidden,true);assert.equal(requests.length,3);
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_discover_uninstalled_and_inventory_failure_stay_in_modal_without_mutations():
    node(DOM + DISCOVER + SOURCE + r'''
(async()=>{
 const removed=openExtensionSourcePreferences('removed');pending.shift().resolve({extensions:[installed('removed',{isInstalled:false})]});await removed;
 assert(ids.sourcePreferencesStatus.textContent.includes('not currently installed'));assert.equal(ids.sourcePreferencesSave.disabled,true);assert.equal(requests.length,1);
 const failed=openExtensionSourcePreferences('unknown');pending.shift().reject(Error('Source engine warming up'));await failed;
 assert(ids.sourcePreferencesStatus.textContent.includes('Unable to load installed sources'));assert.equal(requests[1].opts.suppressClientError,true);assert.equal(ids.sourcePreferencesSave.disabled,true);
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_discover_installed_and_updates_render_settings_but_available_packages_do_not():
    code = block('function extensionActionButton(ext){', 'function renderSourceExtensionGroup(')
    node(DOM + DISCOVER + r'''
const jsq=s=>String(s),esc=s=>String(s),extensionLangLabel=String;
''' + code + SOURCE + r'''
(async()=>{
 const x=installed();sourceExtensionState.extensions=[x];
 const card=sourceExtensionCard(x);assert(card.includes('Source settings'));assert(card.includes('Uninstall'));
 const action=card.match(/onclick="(openExtensionSourcePreferences\('[^']+'\))"/)[1];
 const opening=eval(action);pending.shift().resolve(snapshot([]));await opening;
 assert.equal(requests[0].url,'/api/v1/sources/9223372036854775806/preferences');
 const updating=sourceExtensionCard({...x,hasUpdate:true});assert(updating.includes('Source settings'));assert(updating.includes('Update'));
 const available=sourceExtensionCard({...x,isInstalled:false});assert(!available.includes('Source settings'));assert(available.includes('Install'));
 const nonconfigurable=sourceExtensionCard({...x,source:{nodes:[{id:'3',isConfigurable:false}]}});assert(nonconfigurable.includes('No source settings'));assert(!nonconfigurable.includes('openExtensionSourcePreferences'));
 const mixed=sourceExtensionCard({...x,source:{nodes:[{id:'3',isConfigurable:false},{id:'4',isConfigurable:true}]}});assert(mixed.includes('openExtensionSourcePreferences'));
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_installed_orphan_card_opens_settings_for_exact_original_package_only():
    source = (STATIC / 'orphan-sources.js').read_text()
    code = source[source.index('  function card(row){'):source.index('  function render(){')]
    node(DOM + r'''
let chosen;window.openExtensionSourcePreferences=pkg=>{chosen=pkg};const state={data:{worker_configured:false}},states={};
const button=(text,action,style='')=>{const n=document.createElement('button');n.textContent=text;n.onclick=()=>action(n);return n;};
const setSourceSubtab=()=>{};
''' + code + r'''
const installed=card({pkgName:'eu.kanade.tachiyomi.extension.en.archived',installed:true});
const settings=installed.querySelectorAll('button').find(n=>n.textContent==='Source settings');assert(settings);settings.click();
assert.equal(chosen,'eu.kanade.tachiyomi.extension.en.archived');
const available=card({pkgName:'archive.available',installed:false});assert(!available.querySelectorAll('button').some(n=>n.textContent==='Source settings'));
''')
