from test_v086_ui import block, node


def test_manual_search_distinguishes_deferrals_from_exhaustive_misses():
    code = block('async function runMatchSearch(', 'function renderMatchResults(')
    node('''const assert=require('assert');
      let matchBrowserState={entryId:1,sourceId:'source'},matchSearchAbort=null,matchEnrichAbort=null,matchBrowserGeneration=0;
      const nodes={};const $=id=>nodes[id]||(nodes[id]={value:'',textContent:'',innerHTML:''});
      const esc=x=>String(x);let rendered=[];
      const renderMatchResults=x=>rendered=x;const enrichMatchResults=async()=>{};
      let response;const api=async()=>response;
    ''' + code + '''
      (async()=>{
        response={mangas:[],deferred_count:3,partial:true,live_queries:['one'],queries:['one','two','three']};
        await runMatchSearch();assert.match(nodes['#matchResults'].innerHTML,/deferred|budget|busy/i);
        assert(!nodes['#matchResults'].innerHTML.includes('No matches found'));
        response={mangas:[{id:7}],deferred_count:1,partial:true,cached_results:1,live_queries:['one'],queries:['one','two']};
        await runMatchSearch();assert.equal(rendered[0].id,7);
        assert.match(nodes['#matchSearchStatus'].textContent,/deferred/);
        assert.match(nodes['#matchSearchStatus'].textContent,/1 live quer/);
        response={mangas:[],queries:['one','two'],live_queries:['one']};
        await runMatchSearch();assert(!nodes['#matchResults'].innerHTML.includes('every known title'));
      })().catch(e=>{console.error(e);process.exitCode=1});
    ''')


def test_obsolete_manual_search_response_cannot_replace_new_query():
    code = block('async function runMatchSearch(', 'function renderMatchResults(')
    node('''const assert=require('assert');
      let matchBrowserState={entryId:1,sourceId:'source'},matchSearchAbort=null,matchEnrichAbort=null,matchBrowserGeneration=0;
      const nodes={};const $=id=>nodes[id]||(nodes[id]={value:'',textContent:'',innerHTML:''});
      const esc=x=>String(x);let rendered=[];
      const renderMatchResults=x=>rendered=x;const enrichMatchResults=async()=>{};
      const releases=[];const api=()=>new Promise(resolve=>releases.push(resolve));
    ''' + code + '''
      (async()=>{
        const old=runMatchSearch(),fresh=runMatchSearch();
        releases[1]({mangas:[{id:2}]});await fresh;
        releases[0]({mangas:[{id:1}]});await old;
        assert.equal(rendered[0].id,2);
      })().catch(e=>{console.error(e);process.exitCode=1});
    ''')
