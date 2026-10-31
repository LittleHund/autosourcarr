"""Execute production UI request ownership, cache and bounded diagnostic helpers."""
from test_v086_ui import block, node


def test_coverage_preserves_existing_counts_until_confirmed_and_clears_true_empty():
    code=block('const ENTRY_CHAPTER_FIELDS=', 'async function hydrateMatrixChapters(')
    node("const assert=require('assert');"+code+r"""
const old={entries:[{id:1,served_chapter_count:4,served_latest_chapter:4,chapter_records_checked:true}],matches:[{entry_id:1,source_id:'a',source_manga_id:10,chapter_count:4,chapters_checked:true}]};
const data={chapter_stats_pending:true,entries:[{id:1,preferred_title:'New title',chapter_stats_pending:true}],matches:[{entry_id:1,source_id:'a',source_manga_id:10,chapter_stats_pending:true},{entry_id:1,source_id:'b',source_manga_id:11,chapter_stats_pending:true}]};
preserveMatrixChapterStats(data,old);assert.equal(data.entries[0].served_chapter_count,4);assert.equal(data.entries[0].preferred_title,'New title');assert.equal(data.matches[1].chapter_stats_pending,true);
assert(mergeMatrixChapterStats(data,{entry_stats:{},record_stats:[]}));assert.equal(data.entries[0].served_chapter_count,0);assert.equal(data.matches[0].chapter_count,0);assert.equal(data.chapter_stats_pending,false);
assert.equal(mergeMatrixChapterStats(data,{entry_stats:{},record_stats:[]}),false);
""")


def test_coverage_newer_request_and_manual_revision_own_results():
    code=block('function updateMatrixCoverageLabel(', 'function loadMatrixView(')
    node(r"""
const assert=require('assert');let data={entries:[{id:1}],matches:[]},catalogViewRevision=0;
const matrixStatsRequests={},requests=[],renders=[];const api=(url,opts)=>new Promise((resolve,reject)=>requests.push({opts,resolve,reject}));
const matrixDataset=()=>data,activeSurface=()=>true,requestSurfaceRender=k=>renders.push(k),matrixControls=()=>({count:{textContent:''}});
"""+code+r"""
(async()=>{
const first=hydrateMatrixChapters('matrix',data),second=hydrateMatrixChapters('matrix',data);assert(requests[0].opts.signal.aborted);
requests[1].resolve({entry_stats:{'1':{served_chapter_count:9}},record_stats:[]});await second;
requests[0].resolve({entry_stats:{'1':{served_chapter_count:2}},record_stats:[]});await first;assert.equal(data.entries[0].served_chapter_count,9);
const third=hydrateMatrixChapters('matrix',data);catalogViewRevision++;data.entries[0].served_chapter_count=11;
requests[2].resolve({entry_stats:{'1':{served_chapter_count:3}},record_stats:[]});await third;assert.equal(data.entries[0].served_chapter_count,11);assert.equal(requests.length,4);
requests[3].resolve({entry_stats:{'1':{served_chapter_count:11}},record_stats:[]});await new Promise(r=>setImmediate(r));assert.equal(data.entries[0].served_chapter_count,11);
})().catch(e=>{console.error(e);process.exitCode=1});
""")


def test_failed_coverage_keeps_available_entries_and_reports_failure():
    code=block('function updateMatrixCoverageLabel(', 'function loadMatrixView(')
    node(r"""
const assert=require('assert'),data={entries:[{id:1,preferred_title:'Retained title',chapter_stats_pending:true}],matches:[]},matrixStatsRequests={};let catalogViewRevision=0,renders=0;
const api=async()=>{throw new Error('timeout')},matrixDataset=()=>data,activeSurface=()=>true,requestSurfaceRender=()=>renders++;
const count={textContent:'1 shown'},matrixControls=()=>({count});
"""+code+r"""
(async()=>{await hydrateMatrixChapters('matrix',data);assert.equal(data.entries[0].preferred_title,'Retained title');assert.equal(data.entries[0].served_chapter_count,undefined);assert.equal(data.entries[0].chapter_stats_error,'timeout');assert.match(count.textContent,/coverage unavailable/);assert.equal(renders,1);})().catch(e=>{console.error(e);process.exitCode=1});
""")


def test_cover_revisions_survive_reload_and_invalid_storage_is_ignored():
    code=block('function restoreCoverRevisions(', 'const knownEntryThumbnails')
    node(r"""
const assert=require('assert'),vm=require('vm'),saved=new Map();const sessionStorage={getItem:k=>saved.get(k)||null,setItem:(k,v)=>saved.set(k,v)};
"""+"const code="+repr(code)+r""";
function context(){const c={sessionStorage};vm.createContext(c);vm.runInContext(code+';globalThis.values=()=>[coverRenderNonce,...entryCoverRevisions];globalThis.put=(id,v)=>{entryCoverRevisions.set(id,v);saveCoverRevisions("autosourcarr.entryCoverRevisions",entryCoverRevisions)}',c);return c;}
const first=context();first.put(10,12345);const second=context();assert.deepEqual(JSON.parse(JSON.stringify(first.values())),JSON.parse(JSON.stringify(second.values())));
for(let i=1;i<550;i++)second.put(i,i);assert.equal(context().values().length,501);
saved.set('autosourcarr.entryCoverRevisions','not json');assert.equal(context().values().length,1);
""")


def test_browser_timing_history_is_bounded_and_omits_query_and_entry_ids():
    code=block('const uiTimingSamples=', 'async function api(')
    node("const assert=require('assert');"+code+r"""
for(let i=0;i<140;i++)recordUiTiming('request','/api/admin/entries/123?token=SECRET&q=Private',i,{method:'GET'});
const rows=uiTimingSnapshot();assert.equal(rows.length,120);assert.equal(rows[0].ms,20);assert.equal(rows[0].label,'/api/admin/entries/:id');assert(!JSON.stringify(rows).includes('SECRET'));rows[0].label='changed';assert.notEqual(uiTimingSnapshot()[0].label,'changed');
""")
