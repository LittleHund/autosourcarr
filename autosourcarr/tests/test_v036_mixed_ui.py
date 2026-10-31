from pathlib import Path
from test_v086_ui import node, block, workspace_block

SOURCE = (Path(__file__).resolve().parents[1] / 'app/static/mixed-entries.js').read_text()
SETUP = '''
const assert=require('assert');const nodes={};
const document={querySelector:id=>nodes[id]||(nodes[id]={value:'',innerHTML:'',textContent:'',disabled:false,querySelectorAll:()=>[]})};
const requests=[],messages=[],navigation=[];
const api=(url,options)=>new Promise((resolve,reject)=>requests.push({url,options,resolve,reject}));
const toast=message=>messages.push(message),entryCoverUrl=id=>'/cover/'+id,sourceCoverUrl=id=>'/source-cover/'+id;
const switchTab=tab=>navigation.push(tab),openInlineEntry=(...args)=>navigation.push(args);
const tick=()=>new Promise(resolve=>setImmediate(resolve));
'''
HELPERS = block('function esc(', 'function fmtScore(') + workspace_block('function reviewText(', 'function duplicateComparisonCard(')


def test_render_uses_source_evidence_and_theme_cover_roles_escapes_metadata():
    node(SETUP + HELPERS + SOURCE + '''
      const data={entry_id:1,preferred_title:'<img onerror=evil>',records:[{key:'a:1',source_name:'Source',source_manga_id:1,title:'<unsafe>',description:'<script>oops</script>',author:'Writer',media_mode:'manga',color_mode:'unknown'}],evidence:[{left:'a:1',right:'a:1',reasons:['<conflict>']}],fingerprint:'a'.repeat(64)};
      renderMixedDetail(data);
      const html=nodes['#mixedDetail'].innerHTML;
      assert(!html.includes('<script>'));assert(!html.includes('<unsafe>'));assert(html.includes('&lt;conflict&gt;'));
      assert(html.includes('comparisonEvidence'));assert(html.includes('comparisonCard'));assert(html.includes('comparisonCover'));
      const event={isTrusted:true};nodes['#mixedOpenMatrix'].onclick(event);
      assert.deepEqual(navigation,['matrix',['matrix',1,event]]);
      mixedEntriesState.items=[{entry_id:1,preferred_title:'<unsafe>',record_count:2,state:'needs_review'}];renderMixedQueue();
      assert(nodes['#mixedQueue'].innerHTML.includes('class="queueCover"'));assert(nodes['#mixedQueue'].innerHTML.includes('class="queueText"'));
    ''')


def test_stale_detail_does_not_replace_selected_entry():
    node(SETUP + HELPERS + SOURCE + '''
    (async()=>{
      const old=selectMixedEntry(1),fresh=selectMixedEntry(2);
      requests[1].resolve({entry_id:2,preferred_title:'Current',records:[],evidence:[]});await fresh;
      requests[0].resolve({entry_id:1,preferred_title:'Stale',records:[],evidence:[]});await old;
      assert.equal(mixedEntriesState.detail.entry_id,2);assert(!nodes['#mixedDetail'].innerHTML.includes('Stale'));
    })().catch(error=>{console.error(error);process.exitCode=1});
    ''')


def test_review_removes_item_from_queue_after_authoritative_refresh():
    node(SETUP + HELPERS + SOURCE + '''
    (async()=>{
      mixedEntriesState.items=[{entry_id:1}];mixedEntriesState.selected=1;
      const reviewing=reviewMixedEntry({entry_id:1,fingerprint:'f'.repeat(64)});
      assert.equal(JSON.parse(requests[0].options.body).decision,'keep');
      requests[0].resolve({ok:true});await tick();
      assert(requests[1].url.startsWith('/api/admin/mixed-entries?'));
      requests[1].resolve({items:[],total:0,counts:{needs_review:0,reviewed:1,all:1}});await reviewing;
      assert.equal(mixedEntriesState.items.length,0);assert.equal(mixedEntriesState.detail,null);
      assert(nodes['#mixedSummary'].textContent.includes('1 reviewed'));
    })().catch(error=>{console.error(error);process.exitCode=1});
    ''')


def test_latest_filter_response_wins():
    node(SETUP + HELPERS + SOURCE + '''
    (async()=>{
      const old=loadMixedEntries();const fresh=loadMixedEntries(true);
      requests[1].resolve({items:[],total:0,counts:{needs_review:0,reviewed:5,all:5}});await fresh;
      requests[0].resolve({items:[{entry_id:1}],total:1,counts:{needs_review:1}});await old;
      assert.equal(mixedEntriesState.items.length,0);assert(nodes['#mixedSummary'].textContent.includes('5 reviewed'));
    })().catch(error=>{console.error(error);process.exitCode=1});
    ''')
