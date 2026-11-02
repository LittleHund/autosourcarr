/* UI-13 / PERF-8: each page follows the precise live topic for what it shows, filtered by the ids it has on screen,
 * and keeps no re-check timers once the server publishes the change. */
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const projectRoot=process.env.AUTOSOURCARR_PROJECT_ROOT||path.resolve(__dirname,'../..');
const staticDir=path.join(projectRoot,'autosourcarr/app/static');
const read=name=>fs.readFileSync(path.join(staticDir,name),'utf8');
const app=read('app.js');
const block=(start,end)=>{const i=app.indexOf(start),j=app.indexOf(end,i);assert(i>=0&&j>i,start);return app.slice(i,j);};

function appHarness(){
  const subs=[];
  const context={console,JSON,Number,String,Set,Map,Array,Promise,document:{hidden:false},active:'matrix',
    activeSurface:tab=>context.active===tab,liveSubscribe:config=>{subs.push(config);return {unsubscribe(){},refreshNow(){}};},
    sparseEntryIds:()=>context.sparse,sparse:[11,12],refreshSparseEntryMetadata:async()=>{},matrixDataset:()=>context.dataset,dataset:null,
    duplicateCandidatesVisible:()=>true,duplicateLiveRefresh:()=>{},duplicateData:null,hydrateMatrixDuplicates:async()=>{},loadSubbed:async()=>{},
    importsState:{runId:'run-a'},pendingImportActions:new Map(),structuralRenderBlocked:()=>false,loadImports:async()=>{},refreshPendingImportActions:async()=>{},
    refreshTasksLive:async()=>{},loadAttention:async()=>{},loadErrors:async()=>{}};
  vm.createContext(context);
  vm.runInContext(block('const liveJobOutcome=','function startLiveUpdates('),context);
  vm.runInContext(block('// Covers and summaries fill in','function scheduleMetadataBurst('),context);
  vm.runInContext(block('function subscribeMatrixUpdates(','function scheduleMutationRefresh('),context);
  vm.runInContext(block('// The open import run follows topic imports','// Only a failed list read is retried'),context);
  vm.runInContext(block('function subscribeTaskUpdates(','// Settings editor:'),context);
  return {context,subs,sub:name=>subs.find(item=>item.name===name)};
}
const ev=(topic,data)=>({kind:'event',topic,data});
const tests=[];const test=(name,fn)=>tests.push({name,fn});

test('entry metadata follows topic catalogue for the entries on screen only',()=>{
  const h=appHarness();h.context.subscribeEntryMetadataUpdates();
  const meta=h.sub('entry-metadata');assert.deepEqual([...meta.topics],['catalogue']);
  assert.equal(meta.filter(ev('catalogue',{change:'metadata',entry_ids:[11],truncated:false})),true);
  assert.equal(meta.filter(ev('catalogue',{change:'titles',entry_ids:[99],truncated:false})),false,'Changes of entries not on screen are ignored');
  assert.equal(meta.filter(ev('catalogue',{change:'updated',entry_ids:[99],truncated:true})),true,'A truncated list may include ours');
  assert.equal(meta.filter(ev('catalogue',{change:'tags',entry_ids:[12],truncated:false})),true,'Tag changes carry entry ids too');
  assert.equal(meta.filter(ev('catalogue',{change:'tags',entry_ids:[],truncated:true})),true,'A truncated tag event touches every entry');
  assert.equal(meta.filter(ev('catalogue',{change:'tags'})),true,'An event without an id list is treated as touching everything');
  h.context.sparse=[];assert.equal(meta.when(),false,'No sparse entries on screen: nothing to re-read');
  const covers=h.sub('entry-covers');assert.deepEqual([...covers.topics],['jobs'],'Cover files have no topic: job outcomes stand in');
  assert.equal(covers.filter(ev('jobs',{change:'progress'})),false);assert.equal(covers.filter(ev('jobs',{change:'completed'})),true);
});

test('matrix duplicates follow topic duplicates; the library page follows library status',()=>{
  const h=appHarness();h.context.subscribeMatrixUpdates();
  const matrix=h.sub('duplicate-snapshot-matrix');assert.deepEqual([...matrix.topics],['duplicates']);
  assert.equal(matrix.filter(ev('duplicates',{change:'snapshot'})),true);assert.equal(matrix.filter(ev('duplicates',{change:'stale'})),false,'Stale only announces a rebuild');
  h.context.dataset={duplicates_pending:true};assert.equal(matrix.when(),true);h.context.dataset={duplicates_pending:false};assert.equal(matrix.when(),false);
  const library=h.sub('subbed-membership');assert.deepEqual([...library.topics],['library']);
  assert.equal(library.filter(ev('library',{change:'status'})),true);assert.equal(library.filter(ev('library',{change:'suwayomi-chapters'})),false);
  h.context.active='subbed';assert.equal(library.when(),true,'Any open Library page re-reads when the status refresh finishes');
});

test('imports follow topic imports for the open run and for runs with a pending decision',()=>{
  const h=appHarness();h.context.subscribeImportUpdates();
  const list=h.sub('imports');assert.deepEqual([...list.topics],['imports']);
  assert.equal(list.filter(ev('imports',{change:'updated',import_ids:['run-a']})),true);
  assert.equal(list.filter(ev('imports',{change:'updated',import_ids:['run-b']})),false,'Another run does not reload this one');
  h.context.active='imports';assert.equal(list.when(),true);
  const actions=h.sub('import-actions');assert.equal(actions.when(),false,'No pending decision: nothing to follow');
  h.context.pendingImportActions.set('run-b:1',{runId:'run-b'});assert.equal(actions.when(),true);
  assert.equal(actions.filter(ev('imports',{import_ids:['run-b']})),true);assert.equal(actions.filter(ev('imports',{import_ids:['run-a']})),false);
});

test('source pages use source-health, orphans, repairs and sources topics',()=>{
  const health=read('source-health.js'),orphans=read('orphan-sources.js'),repairs=read('source-repairs.js'),services=read('internal-services.js'),workbench=read('workbench.js');
  assert.match(health,/name:'source-health',topics:\['source-health','sources'\]/);
  assert.match(orphans,/name:'source-orphans',topics:\['orphans','repairs','jobs'\]/);
  assert.match(repairs,/name:'source-repairs',topics:\['repairs','tasks','sources'\]/);
  assert.match(services,/name:'internal-services',topics:\['sources','connections','tasks'\]/);
  assert.match(workbench,/name:'source-browser',topics:\['sources','connections'\]/);
  assert.ok(health.includes("hint:globalThis.AutoSourcarrLive?.hintButton?.(()=>byId('sourceHealthStatus'))"),'Source health shows an Updated hint while a refresh waits');
  assert.ok(repairs.includes("hint:globalThis.AutoSourcarrLive?.hintButton?.(()=>byId('sourceRepairStatus'))"),'and so does source repairs');
});

test('no re-check timers remain for library membership, duplicate membership or the orphan inventory',()=>{
  assert.doesNotMatch(app,/matrixViewRetry|duplicateRetryTimer/,'Library/duplicate membership re-checks are gone');
  assert.doesNotMatch(read('orphan-sources.js'),/inventory_refreshing\)\s*state\.timer=setTimeout/,'Orphan inventory re-check is gone');
  assert.doesNotMatch(read('orphan-sources.js'),/state\.timer=setTimeout\(load/);
});

(async()=>{
  for(const {name,fn} of tests){await fn();console.log('PASS '+name);}
  console.log(`Live topics UI: ${tests.length} scenarios passed`);
})().catch(error=>{console.error(error);process.exitCode=1;});
