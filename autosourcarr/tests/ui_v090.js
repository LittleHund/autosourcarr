const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict');
const source=fs.readFileSync(process.argv[2]||__dirname+'/../app/static/app.js','utf8');
const extract=(start,end)=>source.slice(source.indexOf(start),source.indexOf(end,source.indexOf(start)));
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const tests=[];
function test(name,body){tests.push({name,body});}

test('Browse and task polling start while preferences and source inventory remain unresolved',async()=>{
  const calls=[],pending=[];
  const ctx={applyUiPreferences:()=>calls.push('local'),loadUiPreferences:()=>new Promise(r=>pending.push(r)),
    uiPreferencesState:{},updateUiPreferenceOwner:()=>{},rememberClientError:()=>{},
    startTaskPolling:()=>calls.push('tasks'),health:()=>calls.push('health'),scheduleMetadataLivePoll:()=>{},scheduleDuplicateHeartbeat:()=>{},
    browseState:{},$:()=>({value:''}),loadBrowse:async()=>calls.push('browse'),loadSources:()=>new Promise((r,j)=>pending.push(j)),toast:e=>calls.push(e)};
  Object.assign(ctx,{navigationGeneration:0,switchTab:()=>{},activeSurface:()=>true,loadActiveTab:()=>ctx.loadBrowse(1)});ctx.browseState.items=[{}];
  vm.createContext(Object.assign(ctx,{performance}));vm.runInContext(extract('function browsePageSizeNeedsReload(){',"if(typeof initializeWorkbench==='function')"),ctx);
  const boot=ctx.bootstrapAutoSourcarr();
  assert(calls.includes('browse'));assert(calls.includes('tasks'));assert.equal(pending.length,2);
  pending[1](new Error('inventory unavailable'));await tick();assert(calls.some(x=>String(x).includes('inventory unavailable')));
  pending[0]();await boot;
});

test('Unfiltered Browse preserves broad-source cooldown; selected filters send explicit IDs',()=>{
  const nodes={browseSource:{value:''},browseLanguage:{value:''},browseSort:{value:'relevance'},browsePageSize:{value:'50'},browseMatchCount:{value:''},browseSearch:{value:'query'}};
  const ctx={URLSearchParams,browseState:{mode:'POPULAR'},$:s=>nodes[s.slice(1)],browseSelectedSourceIds:()=>['1','2']};
  vm.createContext(Object.assign(ctx,{performance}));vm.runInContext(extract('function browseQueryUrl(','function browseCardHtml('),ctx);
  let url=new URL(ctx.browseQueryUrl(1),'http://test');assert.equal(url.searchParams.getAll('sources').length,0);
  nodes.browseSource.value='source-group';url=new URL(ctx.browseQueryUrl(1),'http://test');assert.deepEqual(url.searchParams.getAll('sources'),['1','2']);
  nodes.browseSource.value='';nodes.browseLanguage.value='en';url=new URL(ctx.browseQueryUrl(1),'http://test');assert.deepEqual(url.searchParams.getAll('sources'),['1','2']);
});

test('Failed Browse visibly reports failure, preserves old cards and re-enables retry',async()=>{
  const nodes={},ctx={browseState:{loading:false,items:[{id:1}],page:1},$:id=>nodes[id]||(nodes[id]={}),
    browseQueryUrl:()=>'/api/admin/browse',api:async()=>{throw new Error('network timeout')},renderBrowse:()=>{throw new Error('should not erase results')}};
  vm.createContext(Object.assign(ctx,{performance}));vm.runInContext(extract('async function loadBrowse(','function browseInspectorIsOpen('),ctx);
  await assert.rejects(ctx.loadBrowse(1),/network timeout/);
  assert.match(nodes['#browseStatus'].textContent,/Browse refresh failed.*showing previous results/);
  assert.equal(ctx.browseState.items[0].id,1);assert.equal(ctx.browseState.loading,false);assert.equal(nodes['#runBrowse'].disabled,false);
});

test('Task status explains estimate and stale updates without declaring a task stuck',()=>{
  const ctx={Date,esc:s=>String(s),jsq:s=>String(s)};vm.createContext(Object.assign(ctx,{performance}));
  vm.runInContext(extract('function taskProgressText(','function renderTaskSection('),ctx);
  const stale={name:'Repair',state:'running',activity:'running',current:0,total:12,elapsed_seconds:3180,updated_at:new Date(Date.now()-3180000).toISOString()};
  const html=ctx.taskCardHtml(stale);assert.match(html,/ETA after first item/);assert.match(html,/Last task update 53m ago/);assert(!html.includes('stuck'));
  assert.equal(ctx.taskUpdateText({...stale,updated_at:'invalid'}),'');assert.equal(ctx.taskUpdateText({...stale,state:'completed'}),'');
  const waiting=ctx.taskCardHtml({...stale,activity:'waiting',waiting_seconds:125});assert.match(waiting,/Waiting 2m 5s/);assert(!waiting.includes('ETA'));assert(!waiting.includes('taskProgressBar'));
});

test('In-flight unauthenticated request retries the token another request just obtained',async()=>{
  const requests=[],ctx={token:'',AbortController,setTimeout,clearTimeout,clientErrors:[],scheduleMutationRefresh:()=>{},rememberClientError:()=>{},
    fetch:(url,opts)=>new Promise(resolve=>requests.push({opts,resolve})),prompt:()=>{throw new Error('unnecessary prompt')}};
  vm.createContext(Object.assign(ctx,{performance}));vm.runInContext(extract('async function api(','window.addEventListener(\'unhandledrejection\''),ctx);
  const job=ctx.api('/api/admin/tasks');assert.equal(requests.length,1);
  ctx.token='new-token';requests[0].resolve({status:401,ok:false});await tick();assert.equal(requests.length,2);assert.equal(requests[1].opts.headers['X-Admin-Token'],'new-token');
  requests[1].resolve({status:200,ok:true,json:async()=>({running:1})});assert.equal((await job).running,1);
});

test('Rejected token allows one replacement without an authentication retry loop',async()=>{
  let calls=0,prompts=0;const ctx={token:'bad',adminAuthDismissed:false,AbortController,setTimeout,clearTimeout,clientErrors:[],scheduleMutationRefresh:()=>{},rememberClientError:()=>{},
    fetch:async()=>{calls++;return {status:401,ok:false,text:async()=>'unauthorized'}},prompt:()=>{prompts++;return 'still-bad';}};
  vm.createContext(Object.assign(ctx,{performance}));vm.runInContext(extract('async function api(','window.addEventListener(\'unhandledrejection\''),ctx);
  await assert.rejects(ctx.api('/api/admin/tasks'),/Admin token required or rejected/);assert.equal(calls,2);assert.equal(prompts,1);
  await assert.rejects(ctx.api('/api/admin/tasks'),/Reload/);assert.equal(prompts,1);
});

test('JSON errors retain readable detail, HTTP status and retry delay',async()=>{
  const recorded=[],ctx={token:'',AbortController,setTimeout,clearTimeout,clientErrors:[],scheduleMutationRefresh:()=>{},rememberClientError:(message,meta)=>recorded.push({message,meta}),
    fetch:async()=>new Response(JSON.stringify({detail:'Runtime unavailable'}),{status:503,headers:{'Retry-After':'30'}})};
  vm.createContext(Object.assign(ctx,{performance}));vm.runInContext(extract('async function api(',"window.addEventListener('unhandledrejection'"),ctx);
  await assert.rejects(ctx.api('/api/admin/source-extensions'),e=>e.message==='Runtime unavailable'&&e.status_code===503&&e.retry_after==='30');
  assert.equal(recorded[0].message,'Runtime unavailable');assert.equal(recorded[0].meta.status_code,503);assert.equal(JSON.parse(recorded[0].meta.detail).detail,'Runtime unavailable');
});

test('Late server page size refreshes page one with current filters without waiting for source inventory',async()=>{
  const controls={size:'25',filter:'old'},requests=[],releases=[];
  let releasePreferences,releaseSources;
  const state={loading:false,loadedPageSize:0};
  const ctx={browseState:state,$:()=>({value:controls.size}),applyUiPreferences:()=>{},
    loadUiPreferences:()=>new Promise(r=>releasePreferences=r),
    loadSources:()=>new Promise(r=>releaseSources=r),
    loadBrowse:page=>{const size=Number(controls.size);state.loading=true;requests.push({page,size,filter:controls.filter});return new Promise(r=>releases.push(()=>{state.loadedPageSize=size;state.loading=false;r();}));},
    startTaskPolling:()=>{},health:()=>{},scheduleMetadataLivePoll:()=>{},scheduleDuplicateHeartbeat:()=>{},toast:e=>{throw Error(e)}};
  Object.assign(ctx,{navigationGeneration:0,switchTab:()=>{},activeSurface:()=>true,loadActiveTab:()=>ctx.loadBrowse(1)});ctx.browseState.items=[{}];
  vm.createContext(Object.assign(ctx,{performance}));vm.runInContext(extract('function browsePageSizeNeedsReload(){',"if(typeof initializeWorkbench==='function')"),ctx);
  const boot=ctx.bootstrapAutoSourcarr();assert.equal(requests.length,1);
  controls.size='100';controls.filter='current';releasePreferences();await tick();
  assert.equal(requests.length,1,'Do not overlap the initial Browse request');
  releases[0]();await tick();
  assert.deepEqual(requests[1],{page:1,size:100,filter:'current'});
  releases[1]();releaseSources();await boot;
});

test('Late preference GET preserves a user edit even after its save cleared pending values',async()=>{
  let release;const local={theme:'blue'};let applied;
  const ctx={UI_PREF_LOCAL_KEYS:{theme:'theme'},uiPreferencesState:{values:{theme:'blue'},pending:{},saving:false},
    readLocalUiPreference:k=>local[k],writeLocalUiPreference:(k,v)=>{local[k]=v;},
    api:()=>new Promise(r=>release=r),applyUiPreferences:p=>{applied=p;},updateUiPreferenceOwner:()=>{},
    setTimeout:()=>1,clearTimeout:()=>{},flushUiPreferenceSave:()=>{}};
  vm.createContext(Object.assign(ctx,{performance}));
  const wb=fs.readFileSync(__dirname+'/../app/static/workbench.js','utf8');vm.runInContext(wb.slice(wb.indexOf('function migrateColorRoles('),wb.indexOf('function commitTertiaryColor('))+extract('function normalizeHexColor(', 'function syncCustomColorInputs('),ctx);
  vm.runInContext(extract('function queueUiPreferenceSave(','async function flushUiPreferenceSave(')+extract('async function loadUiPreferences(){','function rememberClientError('),ctx);
  const loading=ctx.loadUiPreferences();
  ctx.queueUiPreferenceSave('theme','green');
  ctx.uiPreferencesState.pending={}; // the newer PUT has already succeeded
  release({values:{theme:'blue'},user:'Current user'});await loading;
  assert.equal(applied.theme,'green');assert.equal(ctx.uiPreferencesState.values.theme,'green');
});

test('Preference reconciliation waits for an already running Browse request',async()=>{
  const controls={value:'25'};let releasePreferences,releaseSources;
  const state={loading:false,loadedPageSize:25};let calls=0;
  const ctx={browseState:state,$:()=>controls,applyUiPreferences:()=>{},
    loadUiPreferences:()=>new Promise(r=>releasePreferences=r),loadSources:()=>new Promise(r=>releaseSources=r),
    loadBrowse:async()=>{calls++;},startTaskPolling:()=>{},health:()=>{},scheduleMetadataLivePoll:()=>{},scheduleDuplicateHeartbeat:()=>{},toast:e=>{throw Error(e)}};
  Object.assign(ctx,{navigationGeneration:0,switchTab:()=>{},activeSurface:()=>true,loadActiveTab:()=>ctx.loadBrowse(1)});ctx.browseState.items=[{}];
  vm.createContext(Object.assign(ctx,{performance}));vm.runInContext(extract('function browsePageSizeNeedsReload(){',"if(typeof initializeWorkbench==='function')"),ctx);
  const boot=ctx.bootstrapAutoSourcarr();await tick();
  state.loading=true;controls.value='100';releasePreferences();await tick();
  assert.equal(calls,1);assert.equal(state.pendingPreferenceReload,true);
  releaseSources();await boot;
});

test('In-flight Browse finishes before a pending page-size correction reloads current settings',async()=>{
  const nodes={'#browsePageSize':{value:'25'}},requests=[],scheduled=[];
  const state={loading:false,items:[]};
  const ctx={browseState:state,$:key=>nodes[key]||(nodes[key]={}),renderBrowse:()=>{},
    browseQueryUrl:page=>`/browse?page=${page}&page_size=${nodes['#browsePageSize'].value}`,
    api:url=>new Promise(resolve=>requests.push({url,resolve})),setTimeout:fn=>scheduled.push(fn),toast:e=>{throw Error(e)}};
  vm.createContext(Object.assign(ctx,{performance}));vm.runInContext(extract('async function loadBrowse(','function browseInspectorIsOpen(')+extract('function browsePageSizeNeedsReload(){','async function bootstrapAutoSourcarr(){'),ctx);
  const first=ctx.loadBrowse(1);nodes['#browsePageSize'].value='100';state.pendingPreferenceReload=true;
  requests[0].resolve({items:[{id:1}],page_size:25});await first;
  assert.equal(scheduled.length,1);assert.equal(requests.length,1);
  const corrected=scheduled[0]();assert.match(requests[1].url,/page_size=100/);
  requests[1].resolve({items:[{id:2}],page_size:100});await corrected;
  assert.equal(state.loadedPageSize,100);assert.equal(state.items[0].id,2);assert.equal(scheduled.length,1);
});

(async()=>{let failed=0;for(const t of tests){try{await t.body();console.log('PASS '+t.name);}catch(e){failed++;console.log('FAIL '+t.name+'\n'+e.stack);}}console.log(`${tests.length-failed}/${tests.length} passed`);process.exitCode=failed?1:0;})();
