"""Run production UI behavior with deterministic, out-of-order HTTP responses."""
from test_v086_ui import block, node, workspace_block


IMPORT_SEARCH_SETUP = workspace_block('function importCandidateChoices(', 'function selectImportCandidate(') + '''
const assert=require('assert');const window=globalThis;
const importSearchRequests=new Map();const workbenchState={importCandidates:new Map()};
const item={id:1,state:'unresolved',suggestions:[]};
const importsState={runId:'run',revision:0,items:[item]};
const importViewKey=()=>`${importsState.revision}|${importsState.runId}`;
const importActionKey=(run,id)=>`${run}:${id}`;
const input={value:'old'},box={innerHTML:''};
const button={disabled:false,textContent:'Search',isConnected:true};
const $=id=>id==='#importSearch-1'?input:box;
const messages=[],matches=[],requests=[];
const toast=m=>messages.push(m);const updateImportReviewCounts=()=>{};
const scheduleMetadataBurst=()=>{};
const importCandidateHtml=(run,item,c)=>String(c.id);
const matchImportItem=(run,item,id)=>matches.push(id);
const api=(url,options)=>new Promise((resolve,reject)=>requests.push({url,options,resolve,reject}));
const tick=()=>new Promise(resolve=>setTimeout(resolve,5));
'''


def test_import_search_stale_live_result_cannot_replace_or_auto_match_new_query():
    code = block('window.searchImportCandidates=', 'function manualMemberCard(')
    node(IMPORT_SEARCH_SETUP + code + '''
      (async()=>{
        const old=searchImportCandidates('run',1,button);
        requests[0].resolve({suggestions:[{id:10}]});await tick();
        assert.equal(requests.length,2);
        input.value='new';const fresh=searchImportCandidates('run',1,button);
        requests[2].resolve({suggestions:[{id:20}]});await tick();
        assert.equal(box.innerHTML,'20');assert.equal(requests.length,4);
        requests[1].resolve({suggestions:[{id:11}],auto_match_entry_id:11});await old;await tick();
        assert.equal(box.innerHTML,'20');assert.deepEqual(matches,[]);
        assert.equal(button.disabled,true);
        requests[3].resolve({suggestions:[{id:21}]});await fresh;
        assert.equal(box.innerHTML,'21');assert.equal(button.disabled,false);
      })().catch(e=>{console.error(e);process.exitCode=1});
    ''')


def test_empty_import_refresh_removes_old_cards_and_updates_pagination():
    code = block('function renderImportItems()', 'function loadImportItems()')
    render = block('function performSurfaceRender(', 'function requestSurfaceRender(')
    node('''const assert=require('assert');
      const importsState={items:[],runs:[{id:'run'}],runId:'run',loadedKey:'current',total:0};
      const importViewKey=()=> 'current';const nodes={'#importItems':{innerHTML:'OLD CARD'}};
      const $=id=>nodes[id]||(nodes[id]={innerHTML:'OLD CARD',textContent:''});
      const importPassesStateFilter=()=>true,importPassesReviewFilter=()=>true;
      const updateImportReviewCounts=()=>{};let pagination=0;
      const renderImportPagination=()=>pagination++;
    ''' + code + render + '''
      performSurfaceRender('imports');
      assert.match(nodes['#importItems'].innerHTML,/No items match/);
      assert.match(nodes['#importItemsStatus'].textContent,/0 matching items/);
      assert.equal(pagination,1);
    ''')


DETAIL_SETUP = block('function inspectorRoot(', 'function revealEntryInspector(') + '''
const assert=require('assert');const window=globalThis;
const inspectorDetailRequests=new WeakMap();
const activeInspector={matrix:1,subbed:null};const entryInspectorGeneration={matrix:1,subbed:0};
let chapterBox={innerHTML:'unchanged'},historyBox={textContent:'unchanged'};
const $=id=>id.startsWith('#historyBox')?historyBox:chapterBox;
const matrixControls=()=>({inspector:{querySelectorAll:()=>[]}});
const esc=String,fmtScore=String;const requests=[];
const api=()=>new Promise(resolve=>requests.push(resolve));
'''


def test_chapter_detail_latest_request_wins_and_navigation_discards_response():
    code = block('window.openChapter=', 'window.qualityCandidate=')
    node(DETAIL_SETUP + code + '''
      (async()=>{
        const old=openChapter(10,null,'matrix'),fresh=openChapter(20,null,'matrix');
        requests[1]({chapter:{display_name:'NEW CHAPTER'},candidates:[]});await fresh;
        assert.match(chapterBox.innerHTML,/NEW CHAPTER/);
        requests[0]({chapter:{display_name:'OLD CHAPTER'},candidates:[]});await old;
        assert.match(chapterBox.innerHTML,/NEW CHAPTER/);
        const leaving=openChapter(30,null,'matrix');activeInspector.matrix=2;
        chapterBox={innerHTML:'new inspector'};
        requests[2]({chapter:{display_name:'WRONG ENTRY'},candidates:[]});await leaving;
        assert.equal(chapterBox.innerHTML,'new inspector');
      })().catch(e=>{console.error(e);process.exitCode=1});
    ''')


def test_history_latest_request_wins_and_reopened_inspector_discards_response():
    code = block('window.loadHistory=', 'window.openChapter=')
    node(DETAIL_SETUP + code + '''
      (async()=>{
        const old=loadHistory(1,'matrix'),fresh=loadHistory(1,'matrix');
        requests[1]({history:[{served_at:'today',chapter:{display_name:'CURRENT'},source_name:'Source',reason:'test'}]});
        await fresh;assert.match(historyBox.textContent,/CURRENT/);
        requests[0]({history:[]});await old;assert.match(historyBox.textContent,/CURRENT/);
        const leaving=loadHistory(1,'matrix');entryInspectorGeneration.matrix++;
        historyBox={textContent:'reopened inspector'};
        requests[2]({history:[]});await leaving;assert.equal(historyBox.textContent,'reopened inspector');
        const valid=loadHistory(1,'matrix');requests[3]({history:[]});await valid;
        assert.equal(historyBox.textContent,'No chapter has been served yet.');
      })().catch(e=>{console.error(e);process.exitCode=1});
    ''')


def test_import_search_editing_query_or_leaving_run_cancels_late_actions():
    code = block('window.searchImportCandidates=', 'function manualMemberCard(')
    node(IMPORT_SEARCH_SETUP + code + '''
      (async()=>{
        const search=searchImportCandidates('run',1,button);
        requests[0].resolve({suggestions:[{id:10}]});await tick();
        input.value='unsent new title';
        requests[1].resolve({suggestions:[{id:11}],auto_match_entry_id:11});await search;await tick();
        assert.equal(box.innerHTML,'10');assert.deepEqual(matches,[]);
        const next=searchImportCandidates('run',1,button);
        importsState.runId='different-run';
        requests[2].resolve({suggestions:[{id:99}]});await tick();
        assert.equal(requests.length,3);
        await next;assert.equal(box.innerHTML,'10');assert.deepEqual(matches,[]);
      })().catch(e=>{console.error(e);process.exitCode=1});
    ''')


def test_import_search_current_result_can_still_auto_match():
    code = block('window.searchImportCandidates=', 'function manualMemberCard(')
    node(IMPORT_SEARCH_SETUP + code + '''
      (async()=>{
        const search=searchImportCandidates('run',1,button);
        requests[0].resolve({suggestions:[]});await tick();
        requests[1].resolve({suggestions:[{id:12}],auto_match_entry_id:12});
        await search;await tick();assert.deepEqual(matches,[12]);
        assert.equal(button.disabled,false);
      })().catch(e=>{console.error(e);process.exitCode=1});
    ''')


def test_matrix_enter_search_loads_once_and_reports_http_errors():
    code = block("for(const kind of ['matrix','subbed']){\n  ['MinMatches'", '// Inline entry inspector')
    node('''const assert=require('assert');const handlers={},calls=[],errors=[];
      const $=id=>({addEventListener:(event,callback)=>handlers[id]=callback});
      let reject=false;
      const loadMatrix=()=>{calls.push('matrix');return reject?Promise.reject(new Error('offline')):Promise.resolve()};
      const loadSubbed=()=>{calls.push('subbed');return Promise.resolve()};
      const renderMatrix=()=>{};const toast=message=>errors.push(message);
    ''' + code + '''
      (async()=>{
        handlers['#matrixSearch']({key:'Escape'});assert.deepEqual(calls,[]);
        handlers['#matrixSearch']({key:'Enter'});
        handlers['#subbedSearch']({key:'Enter'});
        assert.deepEqual(calls,['matrix','subbed']);
        reject=true;handlers['#matrixSearch']({key:'Enter'});
        await new Promise(resolve=>setImmediate(resolve));assert.deepEqual(errors,['offline']);
      })().catch(e=>{console.error(e);process.exitCode=1});
    ''')


def test_metadata_snapshot_preserves_blurred_title_draft_and_refreshes_clean_input():
    code = block('function patchOpenInspectorSnapshot(', 'function applyEntrySnapshotEverywhere(')
    node('''const assert=require('assert');
      const matrixInput={value:'Unsubmitted title',defaultValue:'Original'};
      const browseInput={value:'Unsubmitted browse title',defaultValue:'Original'};
      const heading={textContent:''};
      const root={querySelector:selector=>selector.startsWith('#mainTitle-')?matrixInput:
        selector==='.inspectorTop h2'?heading:null};
      const activeInspector={matrix:1,subbed:null};const browseState={selectedEntryId:1};
      const document={activeElement:null};const matrixControls=()=>({inspector:root});
      const browseInspectorIsOpen=()=>true;const esc=String;
      const $=selector=>selector==='#browseMainTitle'?browseInput:root;
    ''' + code + '''
      patchOpenInspectorSnapshot({id:1,preferred_title:'Background title'});
      assert.equal(matrixInput.value,'Unsubmitted title');
      assert.equal(browseInput.value,'Unsubmitted browse title');
      assert.equal(heading.textContent,'Background title');
      matrixInput.value=matrixInput.defaultValue;
      browseInput.value=browseInput.defaultValue;
      patchOpenInspectorSnapshot({id:1,preferred_title:'Background title'});
      assert.equal(matrixInput.value,'Background title');
      assert.equal(browseInput.value,'Background title');
      patchOpenInspectorSnapshot({id:1,preferred_title:'Later background title'});
      assert.equal(matrixInput.value,'Later background title');
      assert.equal(browseInput.value,'Later background title');
      matrixInput.value='Saved title';browseInput.value='Saved title';
      patchOpenInspectorSnapshot({id:1,preferred_title:'Saved title'});
      patchOpenInspectorSnapshot({id:1,preferred_title:'After save'});
      assert.equal(matrixInput.value,'After save');assert.equal(browseInput.value,'After save');
    ''')
