/* PERF-5: the unfiltered Matrix loads page-first (50 rows, keyset cursor) and appends more on request without
 * moving what is shown (UI-12); reloads and the UI-6 restore keep the loaded page chain. */
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const projectRoot=process.env.AUTOSOURCARR_PROJECT_ROOT||path.resolve(__dirname,'../..');
const staticDir=path.join(projectRoot,'autosourcarr/app/static');
const app=fs.readFileSync(path.join(staticDir,'app.js'),'utf8');
const nav=fs.readFileSync(path.join(staticDir,'navigation-state.js'),'utf8');
const block=(start,end)=>{const i=app.indexOf(start),j=app.indexOf(end,i);assert(i>=0&&j>i,start);return app.slice(i,j);};
const tick=()=>new Promise(resolve=>setImmediate(resolve));

const entries=(from,count)=>Array.from({length:count},(_,i)=>({id:from+i,preferred_title:'Entry '+(from+i)}));
function page(from,count,{next=null,extraSource=null}={}){
  const list=entries(from,count);
  return {entries:list,matches:list.map(e=>({entry_id:e.id,source_id:'s1'})),variants:[],discovery_outcomes:[],
    sources:[{source_id:'s1'},...(extraSource?[{source_id:extraSource}]:[])],next_cursor:next,has_more:!!next};
}
function harness(){
  const requests=[],renders=[],hydrated=[],saves=[];let dataset=null;
  const elements={matrixLoadMore:{hidden:true,disabled:false,textContent:''},matrixPageNote:{textContent:''}};
  const search={value:''},source={value:''},count={textContent:''};
  const context={console,JSON,Number,String,Math,Set,Map,Array,Promise,encodeURIComponent,
    document:{getElementById:id=>elements[id]||null},
    matrixControls:()=>({search,source,count}),matrixRequests:{matrix:new Map(),subbed:new Map()},
    matrixDataset:()=>dataset,setMatrixDataset:(kind,data)=>{dataset=data;},preserveMatrixChapterStats(){},
    renderMatrix:kind=>renders.push(kind),requestSurfaceRender:kind=>renders.push('request:'+kind),activeSurface:()=>true,
    hydrateMatrixDuplicates:(kind,data)=>{hydrated.push(data.entries.length);},hydrateMatrixChapters(){},
    api:url=>new Promise((resolve,reject)=>requests.push({url,params:new URL('http://x'+url).searchParams,resolve,reject})),
    AutoSourcarrNavigation:{save:()=>saves.push(1)},$:()=>null,toast(){},api_unused:null};
  context.globalThis=context;context.window=context;
  vm.createContext(context);
  vm.runInContext(block('function loadMatrixView(','async function refreshBothMatrices('),context);
  return {context,requests,renders,hydrated,saves,elements,search,source,data:()=>dataset,paging:()=>vm.runInContext('matrixPaging',context)};
}
const tests=[];const test=(name,fn)=>tests.push({name,fn});

test('first load asks for 50 rows without a cursor and offers the next page',async()=>{
  const h=harness();const loading=h.context.loadMatrix();
  assert.equal(h.requests.length,1);const first=h.requests[0];
  assert.equal(first.params.get('limit'),'50');assert.equal(first.params.get('cursor'),null);
  first.resolve(page(1,50,{next:'c50'}));await loading;
  assert.equal(h.data().entries.length,50);assert.equal(h.elements.matrixLoadMore.hidden,false);
  assert.equal(h.elements.matrixLoadMore.textContent,'Load 50 more');assert.match(h.elements.matrixPageNote.textContent,/50 newest/);
});

test('load more appends the next keyset page below, merging only its own records',async()=>{
  const h=harness();const loading=h.context.loadMatrix();h.requests[0].resolve(page(1,50,{next:'c50'}));await loading;
  const before=h.data();
  const more=h.context.loadMoreMatrix();assert.equal(h.elements.matrixLoadMore.disabled,true,'Disabled while loading');
  h.context.loadMoreMatrix();assert.equal(h.requests.length,2,'A second click sends no second request');
  const request=h.requests[1];assert.equal(request.params.get('cursor'),'c50');assert.equal(request.params.get('limit'),'50');
  const next=page(51,50,{next:'c100',extraSource:'s2'});next.entries.unshift({id:50,preferred_title:'Entry 50 (moved)'});next.matches.push({entry_id:50,source_id:'s9'});
  request.resolve(next);await more;
  const after=h.data();
  assert.equal(after.entries.length,100,'The duplicate row from a shifted page is not added twice');
  assert.equal(JSON.stringify(after.entries.slice(0,50).map(e=>e.id)),JSON.stringify(before.entries.map(e=>e.id)),'Rows already shown keep their order (nothing above moves)');
  assert.equal(after.matches.filter(m=>m.entry_id===50).length,1,'Records of rows already shown are not duplicated');
  assert.equal(JSON.stringify(after.sources.map(s=>s.source_id)),'["s1","s2"]');
  assert.equal(h.renders.at(-1),'request:matrix','Rendered through the UI-12 guarded surface render');
  assert.equal(h.hydrated.at(-1),100,'Duplicates are fetched for the appended rows');
  assert.equal(h.paging().loaded,100);assert.equal(h.saves.length,1,'The page chain is saved for UI-6');
  assert.equal(h.elements.matrixLoadMore.hidden,false);assert.equal(h.elements.matrixLoadMore.disabled,false);
});

test('a reload keeps the loaded chain in one request; the last page hides the button',async()=>{
  const h=harness();h.paging().loaded=100;
  const loading=h.context.loadMatrix();assert.equal(h.requests[0].params.get('limit'),'100');
  h.requests[0].resolve(page(1,100,{next:null}));await loading;
  assert.equal(h.elements.matrixLoadMore.hidden,true);assert.equal(h.elements.matrixPageNote.textContent,'');
  assert.equal(await h.context.loadMoreMatrix(),undefined);assert.equal(h.requests.length,1,'No cursor: nothing to load');
});

test('the 500-row ceiling stops loading and says how to find the rest',async()=>{
  const h=harness();h.paging().loaded=500;
  const loading=h.context.loadMatrix();assert.equal(h.requests[0].params.get('limit'),'500');
  h.requests[0].resolve(page(1,500,{next:'c500'}));await loading;
  assert.equal(h.elements.matrixLoadMore.hidden,true);assert.match(h.elements.matrixPageNote.textContent,/Search or choose a source/);
  await h.context.loadMoreMatrix();assert.equal(h.requests.length,1);
});

test('search and source filters are not paged by the server: one bounded answer, no button',async()=>{
  const h=harness();h.search.value='one piece';
  const loading=h.context.loadMatrix();assert.equal(h.requests[0].params.get('limit'),'500');assert.equal(h.requests[0].params.get('search'),'one piece');
  h.requests[0].resolve(page(1,3));await loading;assert.equal(h.elements.matrixLoadMore.hidden,true);
  assert.equal(h.paging().loaded,50,'The unfiltered chain length is kept for when the search is cleared');
});

test('a reload while a page is loading wins; the late page is dropped',async()=>{
  const h=harness();let loading=h.context.loadMatrix();h.requests[0].resolve(page(1,50,{next:'c50'}));await loading;
  const more=h.context.loadMoreMatrix();
  loading=h.context.loadMatrix();h.requests[2].resolve(page(1,50,{next:'c50b'}));await loading;
  h.requests[1].resolve(page(51,50,{next:'c100'}));await more;
  assert.equal(h.data().entries.length,50,'The stale page does not land on the new dataset');
  assert.equal(h.paging().nextCursor,'c50b');
});

test('UI-6: the loaded Matrix chain length survives a refresh',()=>{
  const context={document:{getElementById:()=>null,querySelector:()=>null,querySelectorAll:()=>[],addEventListener(){}},console,JSON,Object,Array,Number,Math,Set,Map};
  context.window=context;vm.createContext(context);vm.runInContext(nav,context);
  const normalize=context.AutoSourcarrNavigation.normalize;
  assert.equal(normalize({version:1,matrixLoaded:150}).matrixLoaded,150);
  assert.equal(normalize({version:1,matrixLoaded:120}).matrixLoaded,150,'Rounded up to whole pages');
  assert.equal(normalize({version:1,matrixLoaded:9000}).matrixLoaded,50,'Out of range: first page');
  assert.equal(normalize({version:1}).matrixLoaded,50);
  assert.match(nav,/if\(typeof matrixPaging!=='undefined'\)matrixPaging\.loaded=saved\.matrixLoaded;/);
  assert.match(nav,/result\.matrixLoaded=matrixPaging\.loaded/);
});

(async()=>{
  for(const {name,fn} of tests){await fn();await tick();console.log('PASS '+name);}
  console.log(`Matrix pages UI: ${tests.length} scenarios passed`);
})().catch(error=>{console.error(error);process.exitCode=1;});
