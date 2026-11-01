"""Regression reproductions using the production import handlers and deferred HTTP."""
import pytest
from test_v086_ui import block, node

SETUP = '''
const assert=require('assert'),window=globalThis;
let completed=false;process.on('beforeExit',()=>{if(!completed&&!process.exitCode){console.error('Async regression did not finish');process.exitCode=1;}});
const item={id:1,position:1,state:'unresolved',decision_revision:0,suggestions:[{id:42}],suggestion_checked:0};
const run={id:'run',unresolved:1,added:0};
const importsState={revision:0,runId:'run',filter:'unresolved',review:'all',offset:0,items:[item],runs:[run],loadedKey:'0|run|unresolved|all|0',total:1};
const importViewKey=()=>[importsState.revision,importsState.runId,importsState.filter,importsState.review,importsState.offset].join('|');
const importItemRequests=new Map(),pendingImportActions=new Map();let importsPendingPollTimer=null;
const timers=[],requests=[],messages=[];
const setTimeout=(fn,delay)=>{timers.push({fn,delay});return timers.length;};
const api=(url,opts)=>new Promise((resolve,reject)=>requests.push({url,opts,resolve,reject}));
const toast=msg=>messages.push(msg),patchImportItemDom=()=>{},loadTasks=async()=>{},requestSurfaceRender=()=>{},confirm=()=>true;
const nodes={};const $=id=>nodes[id]||(nodes[id]={innerHTML:'',textContent:''});
const renderImportItems=()=>{},renderImportPagination=()=>{},updateImportCounts=()=>{},esc=String;
const tick=()=>new Promise(resolve=>setImmediate(resolve));
'''
CODE = block('function loadImportItems()', 'function loadImports()') + block('function importActionKey(', 'window.searchImportCandidates=')


@pytest.mark.parametrize('kind', ['match', 'retry', 'reject'])
def test_pre_ack_poll_cannot_finish_manual_action(kind):
    invoke = {'match': "matchImportItem('run',1,42)", 'retry': "retryImportItem('run',1)", 'reject': "rejectImportMatch('run',1)"}[kind]
    node(SETUP + CODE + '''
    (async()=>{
      INVOKE;
      const beforeAck=refreshPendingImportActions();assert.equal(requests.length,0,'No poll before POST accepted');await beforeAck;
      const submitting=timers.find(timer=>timer.delay===0).fn();assert.equal(requests.length,1);
      requests[0].resolve({queued:true,decision_revision:1});await submitting;
      assert.equal(pendingImportActions.size,1);
      const stale=refreshPendingImportActions();requests[1].resolve({item:{id:1,state:'unresolved',decision_revision:0,suggestion_checked:1}});await stale;
      assert.equal(pendingImportActions.size,1,'Older decision cannot finish accepted action');
      const done=refreshPendingImportActions();requests[2].resolve({item:{id:1,state:'added',decision_revision:1,suggestion_checked:1},run:{id:'run',added:1,unresolved:0}});await done;
      assert.equal(pendingImportActions.size,0);assert.equal(importsState.items[0].state,'added');assert.equal(run.unresolved,0);
      assert(requests[3].url.endsWith('/api/v1/imports/run/items')&&JSON.parse(requests[3].opts.body).state==='unresolved','Completion fetches a fresh filtered page');
      requests[3].resolve({items:[],total:0});await tick();assert.equal(importsState.items.length,0);
    })().then(()=>{completed=true}).catch(error=>{console.error(error);process.exitCode=1});
    '''.replace('INVOKE', invoke))


def test_old_page_cannot_overwrite_completed_match_or_consume_completion_refresh():
    node(SETUP + CODE + '''
    (async()=>{
      const old=loadImportItems();
      matchImportItem('run',1,42);const submit=timers.find(t=>t.delay===0).fn();
      requests[1].resolve({queued:true,decision_revision:1});await submit;
      const poll=refreshPendingImportActions();requests[2].resolve({item:{id:1,state:'added',decision_revision:1},run:{id:'run',added:1,unresolved:0}});await poll;
      assert.equal(requests.length,4,'Fresh page request must not coalesce with pre-click GET');
      requests[0].resolve({items:[{id:1,state:'unresolved',decision_revision:0}],total:1,run:{unresolved:1}});await old;
      assert.equal(importsState.items[0].state,'added');assert.equal(run.unresolved,0);
      requests[3].resolve({items:[],total:0});await tick();assert.equal(importsState.items.length,0);
    })().then(()=>{completed=true}).catch(error=>{console.error(error);process.exitCode=1});
    ''')


def test_poll_in_flight_cannot_remove_replacement_action():
    node(SETUP + CODE + '''
    (async()=>{
      const old={runId:'run',itemId:1,type:'match',accepted:true,decisionRevision:1};
      pendingImportActions.set('run:1',old);
      const polling=refreshPendingImportActions();
      const replacement={...old,accepted:false,decisionRevision:2};pendingImportActions.set('run:1',replacement);
      requests[0].resolve({item:{id:1,state:'added',decision_revision:1}});await polling;
      assert.equal(pendingImportActions.get('run:1'),replacement);assert.equal(item.state,'unresolved');
    })().then(()=>{completed=true}).catch(error=>{console.error(error);process.exitCode=1});
    ''')


def test_ambiguous_post_timeout_reconciles_committed_decision():
    node(SETUP + CODE + '''
    (async()=>{
      matchImportItem('run',1,42);const submit=timers.find(t=>t.delay===0).fn();
      requests[0].reject(new Error('Request timed out'));await submit;
      assert.equal(item.state,'resolving');assert.equal(pendingImportActions.size,1);
      const poll=refreshPendingImportActions();requests[1].resolve({item:{id:1,state:'existing',decision_revision:1},run:{id:'run',existing:1,unresolved:0}});await poll;
      assert.equal(pendingImportActions.size,0);assert.equal(importsState.items[0].state,'existing');
    })().then(()=>{completed=true}).catch(error=>{console.error(error);process.exitCode=1});
    ''')


def test_page_read_during_submission_keeps_optimistic_row():
    node(SETUP + CODE + '''
    (async()=>{
      matchImportItem('run',1,42);const page=loadImportItems();
      requests[0].resolve({items:[{id:1,state:'unresolved',decision_revision:0}],total:1});await page;
      assert.equal(importsState.items[0].state,'resolving');assert.equal(pendingImportActions.size,1);
    })().then(()=>{completed=true}).catch(error=>{console.error(error);process.exitCode=1});
    ''')


def test_skip_reconciles_and_fences_off_pre_decision_page():
    node(SETUP + CODE + """
    (async()=>{
      const stale=loadImportItems();skipImportItem('run',1);
      const submit=timers.find(t=>t.delay===0).fn();
      requests[0].resolve({items:[{id:1,state:'unresolved',decision_revision:0}],total:1});await stale;
      assert.equal(importsState.items[0].state,'skipped');
      requests[1].resolve({ok:true,state:'skipped',decision_revision:1});await submit;
      const poll=refreshPendingImportActions();requests[2].resolve({item:{id:1,state:'skipped',decision_revision:1},run:{id:'run',unresolved:0,skipped:1}});await poll;
      assert.equal(pendingImportActions.size,0);assert.equal(run.unresolved,0);
      requests[3].resolve({items:[],total:0});await tick();assert.equal(importsState.items.length,0);
    })().then(()=>{completed=true}).catch(error=>{console.error(error);process.exitCode=1});
    """)
