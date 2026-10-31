"""Exercise orphan classification controls and inventory reconciliation in shipped JS."""
from pathlib import Path
from test_v086_ui import node, block
from test_v0311_ui import DOM

STATIC=Path(__file__).resolve().parents[1]/'app/static'
BASE=r'''
const assert=require('node:assert/strict'),window=globalThis;
const nodes={},$=id=>nodes[id]??=( {value:'',hidden:false,disabled:false,textContent:'',innerHTML:'',open:false} );
nodes['#sourceExtensionLanguage']={value:'all'};
const uiPreferencesState={values:{sourceSections:{}}},applySourceSectionPreferences=()=>{};
const jsq=String,esc=String,extensionLangLabel=String;
const notices=[],toast=x=>notices.push(x);
let sourceExtensionState={loaded:true,loading:false,stores:[],extensions:[]};
const extension=(pkg,extra={})=>({pkgName:pkg,name:pkg,lang:'en',isInstalled:true,is_orphaned:false,...extra});
'''


def test_discover_groups_do_not_duplicate_orphans_or_drop_settings_and_mark_controls():
    code=block('function extensionActionButton(ext){','function renderSourceExtensionStores(')
    node(BASE+code+r'''
sourceExtensionState.extensions=[extension('active'),extension('missing',{can_mark_orphan:true}),extension('local',{is_orphaned:true}),extension('local-update',{is_orphaned:true,hasUpdate:true}),extension('available',{isInstalled:false})];
renderSourceExtensions();
const installed=$('#sourceExtensionInstalledList').innerHTML;
assert(installed.includes('active')&&installed.includes('missing')&&!installed.includes('local'));
assert(installed.includes('Source settings')&&installed.includes('Mark as orphaned'));
assert(!sourceExtensionCard(extension('active')).includes('Mark as orphaned'));
assert.equal($('#sourceExtensionUpdatesSection').hidden,true);
assert(!$('#sourceExtensionUpdatesList').innerHTML.includes('local-update'));
assert($('#sourceExtensionAvailableList').innerHTML.includes('available'));
assert.equal(sourceExtensionState.extensions.length,5,'full inventory retained for source settings');
assert.equal($('#sourceExtensionInstalledCount').textContent,'2');
assert.equal($('#updateAllSourceExtensions').textContent,'Update all (1)','orphan updates still available');
''')


def test_installed_orphan_preserves_actions_and_distinguishes_manual_origin():
    source=(STATIC/'orphan-sources.js').read_text()
    code=source[source.index('  function card(row){'):source.index('  function render(){')]
    node(DOM+r'''
const calls=[],state={data:{worker_configured:false}},states={};
const button=(text,action,style='')=>{const n=document.createElement('button');n.textContent=text;n.onclick=()=>action(n);return n;};
const setSourceSubtab=()=>{};
window.openExtensionSourcePreferences=pkg=>calls.push(['settings',pkg]);
window.manageSourceExtension=(pkg,action)=>calls.push([action,pkg]);
window.setSourceOrphan=(pkg,value)=>calls.push(['orphan',pkg,value]);
'''+code+r'''
const pkg='eu.kanade.tachiyomi.extension.en.local';
const el=card({pkgName:pkg,name:'Local',installed:true,orphan_reason:'manual-upload',can_unmark_orphan:true,hasUpdate:true});
for(const title of ['Source settings','Return to Installed','Update','Uninstall'])el.querySelectorAll('button').find(x=>x.textContent===title).click();
assert.deepEqual(calls,[['settings',pkg],['orphan',pkg,false],['update',pkg],['uninstall',pkg]]);
assert(el.querySelectorAll('strong').some(x=>x.textContent==='Manually added · orphaned'));
assert(!el.querySelectorAll('button').some(x=>x.textContent==='Install archived version'));
''')


def test_late_inventory_cannot_undo_successful_orphan_mark():
    code=block('let sourceExtensionReadRevision=0;','function refreshManagedSourcesAfterMutation(')
    node(BASE+r'''
const requests=[],pending=[],renders=[];
const api=(path,options={})=>{requests.push({path,options});return new Promise((resolve,reject)=>pending.push({resolve,reject}));};
const populateSourceExtensionLanguages=()=>{},renderSourceExtensionStores=()=>{};
const renderSourceExtensions=()=>renders.push(sourceExtensionState.extensions[0]?.is_orphaned);
let orphanRefreshes=0;window.loadSourceOrphans=async()=>{orphanRefreshes++};
const refreshManagedSourcesAfterMutation=()=>{};
const tick=()=>new Promise(resolve=>setImmediate(resolve));
'''+code+r'''
(async()=>{
const original=extension('local',{can_mark_orphan:true});sourceExtensionState.extensions=[original];
const old=loadSourceExtensions(),oldResponse=pending.shift();
const button={disabled:false},mark=setSourceOrphan('local',true,button);assert(button.disabled);
assert.equal(requests[1].path,'/api/v1/source-orphans/local/mark');
pending.shift().resolve({extension:{...original,is_orphaned:true},message:'Marked'});await mark;
assert.equal($('#sourceInstalledOrphanPanel').open,true);assert.equal(button.disabled,false);
oldResponse.resolve({extensions:[original]});await tick();
assert.equal(requests.length,3,'stale read causes a fresh inventory query');
pending.shift().resolve({extensions:[{...original,is_orphaned:true}]});await old;
assert(renders.length>=2&&renders.every(Boolean),'stale response was never rendered');
assert(orphanRefreshes>=2);
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_failed_mark_retains_existing_card_and_refreshes_uncertain_status():
    code=block('let sourceExtensionReadRevision=0;','function refreshManagedSourcesAfterMutation(')
    node(BASE+r'''
const api=async()=>{throw Error('Source inventory unavailable')};
const populateSourceExtensionLanguages=()=>{},renderSourceExtensionStores=()=>{},renderSourceExtensions=()=>{};
let refreshes=0;const refreshManagedSourcesAfterMutation=()=>{refreshes++};
'''+code+r'''
(async()=>{
sourceExtensionState.extensions=[extension('missing',{can_mark_orphan:true})];
const button={disabled:false};await setSourceOrphan('missing',true,button);
assert(!sourceExtensionState.extensions[0].is_orphaned);assert.equal(button.disabled,false);assert.equal(refreshes,1);
assert(notices[0].includes('Unable to change orphan label'));
})().catch(e=>{console.error(e);process.exitCode=1});
''')


def test_upload_and_extension_mutations_refresh_both_inventory_views():
    code=block('function refreshManagedSourcesAfterMutation(){','window.manageSourceExtension=')
    node(r'''
const assert=require('node:assert/strict'),window=globalThis,timers=[];let orphan=0,inventory=0;
window.loadSourceOrphans=()=>{orphan++};const setTimeout=(fn,delay)=>{timers.push([fn,delay])};
const loadSourceExtensions=async()=>{inventory++},loadSources=async()=>{},rerenderSourceDrivenUi=()=>{},rememberClientError=()=>{};
'''+code+r'''
refreshManagedSourcesAfterMutation();assert.equal(orphan,1);
timers.find(x=>x[1]===180)[0]();assert.equal(inventory,1);
''')


def test_source_snapshot_copy_keeps_matrices_independent_and_preserves_missing_views():
    code=block('function syncSourceRowsToMatrices(){','function applySourcePatch(')
    node(r'''
const assert=require('node:assert/strict');
let sourceRows=[{source_id:'fixture',priority:10}],matrixData={},subbedData={};
'''+code+r'''
syncSourceRowsToMatrices();assert.deepEqual(matrixData.sources,sourceRows);assert.deepEqual(subbedData.sources,sourceRows);
matrixData.sources[0].priority=50;assert.equal(sourceRows[0].priority,10);assert.equal(subbedData.sources[0].priority,10);
matrixData=null;sourceRows=[{source_id:'new',priority:20}];syncSourceRowsToMatrices();assert.deepEqual(subbedData.sources,sourceRows);
''')
