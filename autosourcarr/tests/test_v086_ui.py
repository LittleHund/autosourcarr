"""Execute the production browser functions with deterministic HTTP/DOM stubs."""
import json
import subprocess
from pathlib import Path

SOURCE=Path(__file__).resolve().parents[1]/"app"/"static"/"app.js"


def block(start,end):
    source=SOURCE.read_text()
    return source[source.index(start):source.index(end,source.index(start))]


def workspace_block(start,end):
    source=SOURCE.with_name("workbench.js").read_text()
    return source[source.index(start):source.index(end,source.index(start))]


def node(script):
    # Avoid Windows' command-line length limit and caller-dependent fixture paths.
    result=subprocess.run(["node","-"],input=script,cwd=SOURCE.parents[3],text=True,capture_output=True,timeout=10)
    assert result.returncode==0,result.stdout+result.stderr


def test_tasks_polling_shares_one_request_preserves_snapshot_and_recovers():
    code=block("async function loadTasks()","function startTaskPolling()")
    node('''const assert=require('assert');
      let tasksState={running:1,items:[{id:'keep'}]},tasksLoadPromise=null,tasksLastSuccess=0,tasksPollFailures=0;
      let calls=0,release;
      const summary={textContent:''};const $=()=>summary;const document={querySelector:()=>true};
      const surfaceIsActive=()=>false;const renderTasks=()=>{};const requestSurfaceRender=()=>{};
      let api=()=>{calls++;return new Promise(resolve=>release=resolve)};
      const apiV1=()=>({getTasks:()=>api('/api/v1/tasks')});
    '''+code+'''
      (async()=>{
        const a=loadTasks(),b=loadTasks(),c=loadTasks();assert.equal(calls,1);
        release({running:2,items:[{id:'new'}]});assert.deepEqual(await Promise.all([a,b,c]),[true,true,true]);
        assert.equal(tasksState.running,2);assert.equal(tasksLoadPromise,null);
        api=async()=>{throw new Error('Request timed out')};
        assert.equal(await loadTasks(),false);assert.equal(tasksState.items[0].id,'new');
        assert.equal(tasksPollFailures,1);assert.match(summary.textContent,/last snapshot/);
        assert.match(summary.textContent,/retrying automatically/);
        api=async()=>({running:0,items:[]});assert.equal(await loadTasks(),true);assert.equal(tasksPollFailures,0);
      })().catch(e=>{console.error(e);process.exitCode=1});
    ''')


def test_queued_waiting_cards_have_no_working_animation_or_eta_and_are_escaped():
    code=block("function taskProgressText(","function renderTaskSection(")
    esc=block("function esc(","function fmtScore(")
    node("const assert=require('assert');"+esc+code+'''
      for(const activity of ['queued','waiting']){
        const html=taskCardHtml({id:'safe',name:'Import',state:'running',activity,cancellable:true,current:2,total:10,
          eta_seconds:20,waiting_seconds:125,context:'<img src=x onerror=alert(1)>'});
        assert(!html.includes('taskProgressBar'));assert(!html.includes('ETA'));assert(!html.includes('Working…'));
        assert(html.includes('Waiting 2m 5s'));assert(html.includes('Cancel'));assert(html.includes('&lt;img'));
        assert(!html.includes('<img'));assert(html.includes('>'+activity+'</span>'));
      }
      const active=taskCardHtml({name:'Active',state:'running',activity:'running',current:2,total:4,eta_seconds:10});
      assert(active.includes('taskProgressBar'));assert(active.includes('50%'));assert(active.includes('ETA ~10s'));
    ''')


def test_tasks_summary_reports_active_queued_and_waiting_separately():
    code=block("function renderTasks()","window.cancelTask=")
    node("const assert=require('assert');"+'''
      const badge={},summary={};const $=s=>s==='#taskNavBadge'?badge:s==='#tasksSummary'?summary:null;
      const taskNewestFirst=()=>0;const renderTaskSection=()=>{};
      const tasksState={running:4,active:1,queued:2,waiting:1,items:[
        {state:'running',activity:'running'},{state:'running',activity:'queued'},
        {state:'running',activity:'queued'},{state:'running',activity:'waiting'}]};
    '''+code+'''
      renderTasks();assert.equal(badge.textContent,'4');
      assert.equal(summary.textContent,'1 active · 2 queued · 1 waiting · 0 failed or blocked');
    ''')


def test_repairs_and_preparation_are_separate_from_general_and_health_tasks():
    code=block("function renderTasks()","window.cancelTask=")
    node("const assert=require('assert');"+'''
      const badge={},summary={},repairCount={};const $=s=>({'#taskNavBadge':badge,'#tasksSummary':summary,'#repairTaskSectionCount':repairCount}[s]||null);
      const taskNewestFirst=()=>0,lists={};const renderTaskSection=(id,_count,items)=>{lists[id]=items.map(item=>item.id)};
      const tasksState={running:3,active:3,queued:0,waiting:0,items:[
        {id:'health',state:'running',kind:''},{id:'repair',state:'running',kind:'source-repair'},
        {id:'prepare',state:'running',kind:'source-repair-preparation'}]};
    '''+code+'''
      renderTasks();assert.deepEqual(lists['#tasksRunningList'],['health']);
      assert.deepEqual(lists['#repairTasksRunningList'],['repair','prepare']);assert.equal(repairCount.textContent,'2');
      assert.equal(badge.textContent,'3');
    ''')


def test_metadata_heartbeat_applies_every_row_even_after_first_row_changes():
    code=block("async function refreshSparseEntryMetadata()","function scheduleMetadataLivePoll(")
    node('''const assert=require('assert');let seen=[];
      const sparseEntryIds=()=>[1,2,3];const requestSurfaceRender=()=>{};
      const api=async()=>({items:[{id:1},{id:2},{id:3}]});
      const patchCompactSummary=x=>{seen.push(x.id);return true};
    '''+code+'''
      (async()=>{await refreshSparseEntryMetadata();assert.deepEqual(seen,[1,2,3]);})().catch(e=>{console.error(e);process.exitCode=1});
    ''')


def test_read_only_post_lookups_never_trigger_mutation_refreshes():
    code=block("function scheduleMutationRefresh(","// Navigation")
    node('''const assert=require('assert');let calls=0,catalogViewRevision=0;
      const scheduleDuplicateLiveRefresh=()=>calls++;const setTimeout=()=>calls++;
    '''+code+'''
      scheduleMutationRefresh('/api/admin/entries/summaries','POST');
      scheduleMutationRefresh('/api/admin/duplicate-candidates','POST');assert.equal(calls,0);
      scheduleMutationRefresh('/api/admin/matrix/chapter-stats','POST');assert.equal(calls,0);assert.equal(catalogViewRevision,0);
      scheduleMutationRefresh('/api/admin/duplicates/review','POST');assert.equal(calls,1);
      assert.equal(catalogViewRevision,1);
    ''')


def test_paused_discovery_keeps_candidates_without_a_false_searching_indicator():
    code=(workspace_block('function reviewText(', 'function duplicateComparisonCard(')
          +workspace_block('function importCandidateChoices(', 'function selectImportCandidate(')
          +block('function importItemHtml(','function updateImportCounts('))
    filters=block('function importHasEnglishHint(','function importPassesStateFilter(')
    esc=block('function esc(','function fmtScore(')
    node("const assert=require('assert');"+esc+'''
      const cleanImportDisplayTitle=x=>x,importStateLabel=x=>x;
      const importReasonHtml=()=>'',importCandidateHtml=()=>'<span>Existing candidate</span>';
      const workbenchState={importCandidates:new Map()};const importsState={review:'searching',items:[]};const count={};
      const $=s=>s==='#importReviewCount-searching'?count:null;
    '''+code+filters+'''
      for(const suggestions of [[],[{id:4}]]){
        const item={id:1,state:'unresolved',title:'Example',suggestion_state:'paused',suggestion_checked:0,suggestions};
        const html=importItemHtml({id:'run'},item);
        assert(html.includes('Discovery paused'));assert(html.includes('Retry unresolved'));
        assert(!html.includes('importSearchingCandidates'));
        if(suggestions.length)assert(html.includes('Existing candidate'));
        assert.equal(importPassesReviewFilter(item),false);
        importsState.items=[item,{...item,id:2,suggestion_state:'searching'}];
        updateImportReviewCounts();assert.equal(count.textContent,'1');
      }
    ''')
