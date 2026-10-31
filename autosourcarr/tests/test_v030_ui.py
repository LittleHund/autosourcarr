"""Execute shipped grouping and request-ownership functions in Node."""
from test_v086_ui import block, node


def test_browse_coalesces_and_new_query_owns_response():
    code = block('async function loadBrowse(', 'function browseInspectorIsOpen(')
    setup = r"""
const assert=require('assert');
const nodeValue={value:'A'},nodes={};
const $=s=>nodes[s]||(nodes[s]={value:s==='#browsePageSize'?'50':'',textContent:'',disabled:false});
const browseState={items:[],loading:false},requests=[],renders=[];
const browseQueryUrl=(page,force)=>'/api/admin/browse?q='+nodeValue.value+'&page='+page+'&force='+force;
const api=(url,opts)=>new Promise((resolve,reject)=>requests.push({url,opts,resolve,reject}));
const renderBrowse=()=>renders.push(browseState.items[0]?.id);const toast=()=>{};
let matrixData=null,subbedData=null,duplicateData=null;
"""
    node(setup + code + r"""
(async()=>{
 const old=loadBrowse(1),same=loadBrowse(1);assert.equal(requests.length,1);
 nodeValue.value='B';const fresh=loadBrowse(1);assert.equal(requests.length,2);
 assert(requests[0].opts.signal.aborted);
 requests[1].resolve({items:[{id:'B'}],page_size:50});await fresh;
 requests[0].resolve({items:[{id:'A'}],page_size:50});await Promise.all([old,same]);
 assert.equal(browseState.items[0].id,'B');assert.deepEqual(renders,['B']);
 assert.equal(browseState.loading,false);assert.equal($('#runBrowse').disabled,false);
})().catch(e=>{console.error(e);process.exitCode=1});
""")


def test_browser_groups_preserve_occurrence_and_resolution_counts():
    code = block('function groupBrowserDiagnostics(', 'function renderErrorsSnapshot(')
    node("const assert=require('assert');" + code + r"""
const record=(id,extra={})=>({id:String(id),client_side:true,component:'Browser request',message:'Request timed out',path:'/api/admin/browse?page='+id,status_code:0,method:'GET',count:2,resolved:false,...extra});
const raw=[record(1),record(2)],before=JSON.stringify(raw),groups=groupBrowserDiagnostics(raw);
assert.equal(groups.length,1);assert.equal(groups[0].count,4);assert.equal(groups[0].record_count,2);
assert.equal(JSON.stringify(raw),before);assert.deepEqual(groupBrowserDiagnostics(raw),groups);
raw[0].resolved=true;assert.equal(groupBrowserDiagnostics(raw).length,2);
assert.equal(groupBrowserDiagnostics([record(1,{status_code:403}),record(2,{status_code:429})]).length,2);
assert.equal(groupBrowserDiagnostics([record(1,{path:'/api/v1/chapters/1/pages'}),record(2,{path:'/api/v1/chapters/2/pages'})]).length,2);
""")


def test_unchanged_duplicate_results_do_not_rebuild_matrix():
    code = block('async function hydrateMatrixDuplicates(', 'function loadMatrixView(')
    node(r"""
const assert=require('assert');
const d={entries:[{id:1}],duplicates:{},duplicates_pending:true};
const duplicateHydrateRetry={matrix:null};const matrixDataset=()=>d;
const duplicateSnapshotRequests=new Map(),duplicatePollAttempts={matrix:0,subbed:0};
let renders=0,response={duplicates:{},pending:false};
const api=async()=>response;const requestSurfaceRender=()=>renders++;
const document={querySelector:()=>true};
"""+code+r"""
(async()=>{
 await hydrateMatrixDuplicates('matrix',d);assert.equal(renders,0);assert.equal(d.duplicates_pending,false);
 response={duplicates:{'1':{entry_ids:[2]}},pending:false};await hydrateMatrixDuplicates('matrix',d);assert.equal(renders,1);
})().catch(e=>{console.error(e);process.exitCode=1});
""")
