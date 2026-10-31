/* Exercise shipped UI request ownership with deterministic DOM and HTTP seams. */
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const staticRoot=process.env.AUTOSOURCARR_UI_STATIC||path.join(__dirname,'../app/static');
const app=fs.readFileSync(path.join(staticRoot,'app.js'),'utf8');
const repairs=fs.readFileSync(path.join(staticRoot,'repair-tasks.js'),'utf8');
const tags=fs.readFileSync(path.join(staticRoot,'tag-review.js'),'utf8');
const block=(source,start,end)=>source.slice(source.indexOf(start),source.indexOf(end,source.indexOf(start)));
const tick=()=>new Promise(resolve=>setImmediate(resolve));
function deferred(){let resolve,reject;const promise=new Promise((yes,no)=>{resolve=yes;reject=no;});return {promise,resolve,reject};}
class Node{
  constructor(){this.hidden=false;this.open=false;this.disabled=false;this.value='';this.textContent='';this.innerHTML='';this.children=[];this.listeners={};this.dataset={};this.classes=new Set();this.classList={add:name=>this.classes.add(name),remove:name=>this.classes.delete(name),contains:name=>this.classes.has(name)};}
  addEventListener(name,fn){this.listeners[name]=fn;}
  append(...items){this.children.push(...items);}
  replaceChildren(...items){this.children=items;}
  querySelectorAll(){return [];}
}
function harness(extra={}){
  const nodes=new Map(),requests=[],timers=[],notices=[],snapshots=new Map();
  const $=id=>{if(!nodes.has(id))nodes.set(id,new Node());return nodes.get(id);};
  const context={console,AbortController,DOMException,structuredClone,Number,String,Date,JSON,Promise,Map,Set,Array,Uint8Array,encodeURIComponent,crypto:require('node:crypto').webcrypto,
    $,document:{body:new Node(),querySelector:()=>null,querySelectorAll:()=>[],getElementById:id=>$('#'+id),createElement:()=>new Node()},
    setTimeout:(fn,ms)=>{const timer={fn,ms,cancelled:false};timers.push(timer);return timer;},clearTimeout:timer=>{if(timer)timer.cancelled=true;},
    requestAnimationFrame:()=>{},syncModalScrollLock:()=>{},flushDeferredUiRenders:()=>{},
    token:'admin',toast:message=>notices.push(message),esc:String,shortText:String,langLabel:String,groupForSource:()=>null,
    sourceRows:[{source_id:'a',name:'A',lang:'en',enabled_language:true,enabled_search:true,available:true},{source_id:'b',name:'B',lang:'nl',enabled_language:true,enabled_search:true,available:true}],
    entryCoverUrl:id=>'/entry/'+id,sourceCoverUrl:id=>'/source/'+id,loadSources:async()=>{},
    noteEntryThumbnail:()=>{},applyEntrySnapshotToDataset:(_dataset,entry)=>snapshots.set(Number(entry.canonical_id||entry.id),structuredClone(entry)),patchOpenInspectorSnapshot:()=>{},
    subbedData:{},matrixData:{},browseState:{items:[]},requestSurfaceRender:()=>{},patchBrowseCardNode:()=>{},entrySyncTimers:new Map(),
    api:(url,options={})=>{const pending=deferred();requests.push({url,options,...pending});return pending.promise;},...extra};
  context.window=context;vm.createContext(context);
  return {context,$,requests,timers,notices,snapshots,run:code=>vm.runInContext(code,context)};
}
function matchHarness(){
  const h=harness();
  h.run(block(app,'let matchBrowserState = null;','let matchSearchAbort = null;')+'let matchSearchAbort=null,matchEnrichAbort=null;');
  h.run(block(app,'function applyEntrySnapshotEverywhere(','function removeEntryEverywhere('));
  h.run(block(app,'async function syncEntrySnapshot(','function patchOpenInspectorTitle('));
  h.run(block(app,'function matchBrowserEnabledSources(){','let duplicateSplitData='));
  h.run('runMatchSearch=async()=>{};enrichMatchResults=async()=>{};');
  return h;
}
const entry=(id,mangaId=0)=>({id,canonical_id:id,preferred_title:'Entry '+id,titles:[],matches:mangaId?[{source_id:'a',source_manga_id:mangaId,active:1}]:[],source_variants:[]});
async function opened(h,id=1,sourceId='a'){
  const opening=h.context.openMatchBrowser(id,sourceId),request=h.requests.at(-1);
  assert(request,'Open failed before HTTP: '+h.$('#matchTarget').innerHTML);request.resolve(entry(id));await opening;
  h.run('matchBrowserState.results=[{id:11,title:"First",description:"Original"},{id:12,title:"Second"}];');
}
const state=h=>h.run('matchBrowserState');
const cases=[];
const test=(name,run)=>cases.push({name,run});

test('later editor owns out-of-order entry opens',async()=>{
  const h=matchHarness(),one=h.context.openMatchBrowser(1,'a'),first=h.requests.at(-1);
  const two=h.context.openMatchBrowser(2,'b');h.requests.at(-1).resolve(entry(2));await two;
  first.resolve(entry(1));await one;
  assert.equal(state(h).entryId,2);assert.equal(state(h).sourceId,'b');assert.match(h.$('#matchTarget').innerHTML,/Entry 2/);
});
test('closing or reopening same entry invalidates older open and failure',async()=>{
  const h=matchHarness(),one=h.context.openMatchBrowser(1,'a'),first=h.requests.at(-1);
  h.context.closeMatchBrowser();first.resolve(entry(1));await one;assert.equal(state(h),null);assert.equal(h.$('#matchBrowser').hidden,true);
  const stale=h.context.openMatchBrowser(1,'a'),old=h.requests.at(-1);
  await opened(h,1,'b');old.reject(Error('Old open failed'));await stale;
  assert.equal(state(h).sourceId,'b');assert.match(h.$('#matchTarget').innerHTML,/Entry 1/);
});
test('save captures submitted entry source and candidate before navigation',async()=>{
  const h=matchHarness();await opened(h);
  const saving=h.context.useManualMatch(11),request=h.requests.at(-1),submitted=JSON.parse(request.options.body);
  assert.equal(request.url,'/api/admin/entries/1/manual-match');assert.equal(submitted.source_id,'a');assert.equal(submitted.candidate.description,'Original');
  state(h).results[0].description='Enriched after click';await opened(h,2,'b');
  request.resolve(entry(1,11));await saving;
  assert.equal(state(h).entryId,2);assert.equal(h.snapshots.get(1).matches[0].source_manga_id,11);
});
test('old success cannot replace a newer optimistic selection or global snapshot',async()=>{
  const h=matchHarness();await opened(h);
  const first=h.context.useManualMatch(11),old=h.requests.at(-1),second=h.context.useManualMatch(12),newer=h.requests.at(-1);
  newer.resolve(entry(1,12));await second;old.resolve(entry(1,11));await first;
  assert.equal(state(h).entry.matches[0].source_manga_id,12);assert.equal(h.snapshots.get(1).matches[0].source_manga_id,12);
});
test('old failure cannot roll back a newer optimistic selection',async()=>{
  const h=matchHarness();await opened(h);
  const first=h.context.useManualMatch(11),old=h.requests.at(-1),second=h.context.useManualMatch(12),newer=h.requests.at(-1);
  old.reject(Error('Failed first save'));await first;
  assert.equal(state(h).entry.matches[0].source_manga_id,12);assert.equal(h.snapshots.get(1).matches[0].source_manga_id,12);
  newer.resolve(entry(1,12));await second;
});
test('old failure updates its actual global entry but never the new editor',async()=>{
  const h=matchHarness();await opened(h);
  const save=h.context.useManualMatch(11),old=h.requests.at(-1);await opened(h,2,'b');old.reject(Error('Failed'));await save;
  assert.equal(state(h).entryId,2);assert.equal(h.snapshots.get(1).matches.length,0);
});
test('current failed save rolls back its own optimistic editor and global snapshot',async()=>{
  const h=matchHarness();await opened(h);
  const before=state(h).entry,saving=h.context.useManualMatch(11),request=h.requests.at(-1);
  assert.equal(state(h).entry.matches[0].source_manga_id,11);request.reject(Error('Save failed'));await saving;
  assert.equal(state(h).entry,before);assert.equal(h.snapshots.get(1).matches.length,0);assert.match(h.notices.at(-1),/Unable to save match/);
});
test('clear separate and allow replies retain source and editor ownership',async()=>{
  for(const action of ['clear','separate','allow']){
    const h=matchHarness();h.context.confirm=()=>true;await opened(h);
    const original=state(h),saving=action==='clear'?h.$('#clearCurrentMatch').onclick():action==='separate'?h.context.separateManualCandidate(11):h.context.allowManualCandidate(11),request=h.requests.at(-1);
    assert(request.url.startsWith('/api/admin/entries/1/'));
    await opened(h,2,'b');request.resolve(action==='clear'?entry(1):action==='separate'?{entry:entry(1),separate_entry_id:3}:{});await saving;
    assert.equal(state(h).entryId,2);assert.equal(state(h).dirty,false);assert.equal(original.results[0].manual_separate,undefined);
  }
});
test('same entry source switch avoids implicit editor writes from global snapshots',async()=>{
  const h=matchHarness();h.context.confirm=()=>true;await opened(h);
  const saving=h.$('#clearCurrentMatch').onclick(),request=h.requests.at(-1),before=state(h).entry;
  h.$('#matchSource').value='b';await h.$('#matchSource').onchange();request.resolve(entry(1,11));await saving;
  assert.equal(state(h).sourceId,'b');assert.equal(state(h).entry,before);assert.equal(h.snapshots.get(1).matches[0].source_manga_id,11);
});
test('reconciliation started before a mutation cannot undo current optimism',async()=>{
  const h=matchHarness();await opened(h);
  const sync=h.context.syncEntrySnapshot(1),read=h.requests.at(-1),save=h.context.useManualMatch(12),write=h.requests.at(-1);
  read.resolve(entry(1));assert.equal(await sync,null);assert.equal(state(h).entry.matches[0].source_manga_id,12);
  write.resolve(entry(1,12));await save;
});

test('early diagnostics health rejection is handled while history remains pending',async()=>{
  const renders=[],h=harness({clientErrors:[],lastBackendErrors:{items:[]},lastHealthSnapshot:null,clientHealthError:null,
    renderErrorsSnapshot:(_backend,_health,error)=>renders.push(error),renderStatus:()=>{},groupBrowserDiagnostics:()=>[]});
  h.run(block(app,'async function loadErrors(){','function openErrors(){'));
  const unhandled=[],listener=error=>unhandled.push(error);process.on('unhandledRejection',listener);
  try{
    const loading=h.context.loadErrors(),history=h.requests.find(r=>r.url.includes('/errors?')),health=h.requests.find(r=>r.url==='/api/v1/health');
    health.reject(Error('Health unavailable'));await tick();assert.deepEqual(unhandled,[]);
    history.resolve({items:[{message:'Saved issue'}],active_count:1});const result=await loading;
    assert.equal(result.active,1);assert.match(h.context.clientHealthError.message,/Health unavailable/);assert(renders.length>=3);
  }finally{process.removeListener('unhandledRejection',listener);}
});

function uploadHarness(){
  const h=harness({importsState:{runId:''},loadTasks:async()=>{},switchTab:tab=>h.tabs.push(tab)});h.tabs=[];
  h.run('let backupUploadPending=null;');
  h.run(block(app,app.includes('function newBackupImportId(){')?'function newBackupImportId(){':'async function uploadMihonBackup(){',"if($('#importBackup'))"));
  h.$('#backupImportFile').files=[new File(['backup'],'日本語 100%.tachibk')];h.$('#backupImportFile').value='selected';
  return h;
}
test('backup UTF-8 filename stays ASCII in headers and receives stable identity',async()=>{
  const h=uploadHarness();let sent;
  h.context.fetch=async(url,options)=>{new Request('http://app'+url,options);sent=options;return {ok:true,status:200,json:async()=>({queued:true,import_id:options.headers['X-Import-ID'],task_id:'task'})};};
  await h.context.uploadMihonBackup();
  assert.equal(decodeURIComponent(sent.headers['X-Filename-UTF8']),'日本語 100%.tachibk');assert.equal(sent.headers['X-Filename'],undefined);assert.match(sent.headers['X-Import-ID'],/^[0-9a-f]{32}$/);
  assert.equal(h.$('#backupImportFile').value,'');assert.equal(h.$('#importBackup').disabled,false);assert.deepEqual(h.tabs,['imports']);
});
test('backup deadline covers response JSON and reconciles an accepted upload',async()=>{
  const h=uploadHarness();let sent;const body=deferred();
  h.context.fetch=async(_url,options)=>{sent=options;options.signal.addEventListener('abort',()=>body.reject(new DOMException('Aborted','AbortError')));return {ok:true,status:200,json:()=>body.promise};};
  const uploading=h.context.uploadMihonBackup();await tick();
  const deadline=h.timers.find(timer=>timer.ms===120000);assert.equal(deadline.cancelled,false,'Response headers retain the body deadline');assert.equal(h.$('#importBackup').disabled,true);
  deadline.fn();await tick();assert.equal(h.requests.length,1);assert.equal(h.requests[0].url,'/api/admin/imports/'+sent.headers['X-Import-ID']+'/receipt');assert.equal(h.requests[0].options.timeoutMs,10000);
  h.requests[0].resolve({queued:true,import_id:sent.headers['X-Import-ID'],filename:'日本語 100%.tachibk'});await uploading;
  assert.equal(h.$('#importBackup').disabled,false);assert.equal(h.$('#backupImportFile').value,'');assert.equal(h.context.importsState.runId,sent.headers['X-Import-ID']);
});
test('uncertain backup failure never replays POST and explicit retry retains identity',async()=>{
  const h=uploadHarness(),ids=[];
  h.context.fetch=async(_url,options)=>{ids.push(options.headers['X-Import-ID']);throw Error('Connection dropped');};
  const first=h.context.uploadMihonBackup();await tick();h.requests[0].reject(Error('Unknown receipt'));await first;
  assert.equal(ids.length,1);assert.match(h.$('#backupImportStatus').textContent,/acknowledgment unavailable/);assert.equal(h.$('#backupImportFile').value,'selected');assert.equal(h.$('#importBackup').disabled,false);
  const second=h.context.uploadMihonBackup();await tick();h.requests[1].resolve({queued:true,import_id:ids[1]});await second;
  assert.equal(ids[0],ids[1]);assert.equal(h.$('#backupImportFile').value,'');
});
test('known upload rejection reads error body within deadline and skips receipt',async()=>{
  const h=uploadHarness(),body=deferred();
  h.context.fetch=async(_url,options)=>{options.signal.addEventListener('abort',()=>body.reject(new DOMException('Aborted','AbortError')));return {ok:false,status:400,text:()=>body.promise};};
  const loading=h.context.uploadMihonBackup();await tick();const timer=h.timers.find(item=>item.ms===120000);assert.equal(timer.cancelled,false);timer.fn();await loading;
  assert.equal(h.requests.length,0);assert.equal(h.$('#importBackup').disabled,false);assert.equal(timer.cancelled,true);
});

function repairHarness(){
  const h=harness({repairVisible:true});h.context.byId=id=>h.$('#'+id);h.context.visible=()=>h.context.repairVisible;
  h.run(block(repairs,'  const valid=','  async function open('));return h;
}
test('repair evidence belongs to last selected job and stale errors stay quiet',async()=>{
  const h=repairHarness(),first=h.context.detail('a'.repeat(32)),old=h.requests.at(-1),second=h.context.detail('b'.repeat(32));h.requests.at(-1).resolve({id:'b'});await second;old.resolve({id:'a'});await first;
  assert.match(h.$('#sourceRepairEvidence').textContent,/"b"/);
  const stale=h.context.detail('a'.repeat(32)),failed=h.requests.at(-1),current=h.context.detail('b'.repeat(32));h.requests.at(-1).resolve({id:'b'});await current;failed.reject(Error('Old detail failed'));await stale;
  assert.match(h.$('#sourceRepairEvidence').textContent,/"b"/);
});
test('closing or hiding repair evidence prevents pending response from reopening it',async()=>{
  const h=repairHarness(),loading=h.context.detail('a'.repeat(32));h.$('#sourceRepairDetails').open=false;h.$('#sourceRepairDetails').listeners.toggle?.();h.requests.at(-1).resolve({id:'a'});await loading;
  assert.equal(h.$('#sourceRepairDetails').open,false);
  const hidden=h.context.detail('b'.repeat(32));h.context.repairVisible=false;h.context.invalidate?.();h.requests.at(-1).resolve({id:'b'});await hidden;
  assert.doesNotMatch(h.$('#sourceRepairEvidence').textContent,/"b"/);
});

function tagHarness(){
  const h=harness();h.context.byId=id=>h.$('#'+id);h.context.state={generation:0};h.context.updateButtons=()=>{};
  h.context.note=(node,text)=>{node.replaceChildren();node.textContent=text;};h.context.decisionRow=(row,reviewed)=>({row,reviewed});
  h.run(block(tags,'  async function load(){','  async function loadCatalog('));return h;
}
const tagRow=(id)=>({left:{id,name:'Left'},right:{id:id+1,name:'Right'},decision:'same'});
test('manual tag decisions paint before slow suggestions settle',async()=>{
  const h=tagHarness(),loading=h.context.load();
  h.requests.find(item=>item.url.endsWith('/reviews')).resolve({items:[tagRow(1)]});await tick();
  assert.equal(h.$('#tagDecisions').children.length,1);assert.equal(h.$('#tagDecisions').children[0].reviewed,true);
  h.requests.find(item=>item.url.endsWith('/suggestions')).resolve({items:[tagRow(3)]});await loading;assert.equal(h.$('#tagSuggestions').children.length,1);
});
test('tag generations isolate reordered refreshes and independent list failure',async()=>{
  const h=tagHarness(),old=h.context.load(),oldRequests=h.requests.slice(),fresh=h.context.load(),freshRequests=h.requests.slice(2);
  freshRequests.find(item=>item.url.endsWith('/reviews')).resolve({items:[tagRow(11)]});freshRequests.find(item=>item.url.endsWith('/suggestions')).reject(Error('Suggestions unavailable'));await fresh;
  oldRequests.forEach(item=>item.resolve({items:[tagRow(1)]}));await old;
  assert.equal(h.$('#tagDecisions').children[0].row.left.id,11);assert.match(h.$('#tagSuggestions').textContent,/Suggestions unavailable/);assert.match(h.$('#tagReviewStatus').textContent,/could not be loaded/);
});

// A merge can change the canonical identity while requests are in flight.
test('older alias save cannot overwrite a newer canonical optimistic choice',async()=>{
  const h=matchHarness();await opened(h,1);
  const oldSave=h.context.useManualMatch(11),old=h.requests.at(-1);
  await opened(h,2);const newSave=h.context.useManualMatch(12),newer=h.requests.at(-1);
  old.resolve(entry(2,11));await oldSave;
  assert.equal(state(h).entryId,2);assert.equal(state(h).entry.matches[0].source_manga_id,12);
  assert.equal(h.snapshots.get(2).matches[0].source_manga_id,12);
  newer.resolve(entry(2,12));await newSave;
});
test('alias reconciliation cannot overwrite a pending canonical choice',async()=>{
  const h=matchHarness();await opened(h,1);
  const read=h.context.syncEntrySnapshot(1),old=h.requests.at(-1);
  await opened(h,2);const save=h.context.useManualMatch(12),write=h.requests.at(-1);
  old.resolve(entry(2,11));assert.equal(await read,null);
  assert.equal(state(h).entry.matches[0].source_manga_id,12);assert.equal(h.snapshots.get(2).matches[0].source_manga_id,12);
  write.resolve(entry(2,12));await save;
});
test('alias reconciliation cannot overwrite a newer completed canonical choice',async()=>{
  const h=matchHarness();await opened(h,1);
  const read=h.context.syncEntrySnapshot(1),old=h.requests.at(-1);
  await opened(h,2);const save=h.context.useManualMatch(12);h.requests.at(-1).resolve(entry(2,12));await save;
  old.resolve(entry(2,11));assert.equal(await read,null);
  assert.equal(state(h).entry.matches[0].source_manga_id,12);assert.equal(h.snapshots.get(2).matches[0].source_manga_id,12);
});
test('current alias mutation publishes and retains returned canonical ownership',async()=>{
  const h=matchHarness();await opened(h,1);
  const save=h.context.useManualMatch(11);h.requests.at(-1).resolve(entry(2,11));await save;
  assert.equal(state(h).entryId,2);assert.equal(h.snapshots.get(2).matches[0].source_manga_id,11);
  assert.equal(h.run('matchEntryMutationRevisions.get(2)'),h.run('matchEntryMutationRevisions.get(1)'));
});
test('current alias reconciliation renders the returned canonical editor',async()=>{
  const h=matchHarness();await opened(h,2);
  const read=h.context.syncEntrySnapshot(1);h.requests.at(-1).resolve({...entry(2),preferred_title:'Fresh survivor title'});await read;
  assert.match(h.$('#matchTarget').innerHTML,/Fresh survivor title/);assert.equal(state(h).entryId,2);
});

// Exercise the actual shipped API helper, ledger renderers and action callbacks.
function manualLedgerHarness(){
  const h=harness({performance,adminAuthDismissed:false,clientErrors:[],jsq:String,confirm:()=>true,
    localStorage:{setItem(){},removeItem(){}},rememberClientError:()=>{},scheduleMutationRefresh:()=>{},
    refreshBothMatrices:async()=>{},loadDuplicates:async()=>{},duplicateConfirmedReviews:new Map(),duplicateViewRevision:0});
  h.context.ledgerVisible=true;h.context.activeSurface=name=>name==='manual-actions'&&h.context.ledgerVisible;
  h.context.fetch=(url,options)=>{const pending=deferred();h.requests.push({url,options,...pending});return pending.promise;};
  h.context.loadTagReview=async()=>{h.tagLoads++;};h.tagLoads=0;
  h.run(block(app,'let manualGroupsState =','let importsPollTimer ='));
  h.run(block(app,'async function api(',"window.addEventListener('unhandledrejection'"));
  h.run(block(app,'function manualMemberCard(','// User-visible background task monitor.'));
  h.reply=(request,payload={})=>request.resolve({ok:true,status:200,json:async()=>payload});
  h.ledger=()=>h.run('manualActionsState');
  h.flushReconcile=async()=>{
    const timer=h.timers.find(item=>item.ms===50&&!item.cancelled&&!item.ran);
    if(timer){timer.ran=true;timer.fn();await tick();}
  };
  return h;
}
function ledgerSnapshot(label,decisions=true){
  const count=decisions?1:0;
  return {matches:{groups:decisions?[{id:'group',title:label,members:[],member_count:1}]:[],individual:[],group_count:count,manual_match_count:count},
    titles:decisions?[{entry_id:1,title:label,entry_title:label}]:[],
    separate_entries:decisions?[{target_entry_id:1,source_id:'a',source_manga_id:11,source_title:label}]:[],
    rejected_chapters:decisions?[{candidate_id:1,entry_id:1,name:label}]:[],
    duplicate_decisions:decisions?[{left_entry_id:1,right_entry_id:2,left_title:label,decision:'not_duplicate'}]:[],
    counts:{matches:count,titles:count,separate_entries:count,rejected_chapters:count,duplicate_decisions:count}};
}
async function seedManualLedger(h){const loading=h.context.loadManualActions();h.reply(h.requests.at(-1),ledgerSnapshot('Saved'));await loading;}
function assertEmptyLedger(h){
  for(const id of ['Matches','Titles','Separate','Chapter','Duplicate'])assert.equal(Number(h.$('#manualAction'+id+'Count').textContent),0,id);
  assert.equal(h.ledger().matches.groups.length,0);assert.equal(h.ledger().titles.length,0);
  assert.doesNotMatch(h.$('#manualGroupsList').innerHTML,/Saved|Stale/);
  assert.equal(h.$('#manualActionsStatus').hidden,true);
}

test('manual group removal cannot be undone on screen by its older ledger GET',async()=>{
  const h=manualLedgerHarness();await seedManualLedger(h);
  const oldLoad=h.context.loadManualActions(),old=h.requests.at(-1);
  const removing=h.context.removeManualGroupMember('group','a',11,new Node()),write=h.requests.at(-1);
  h.reply(write);await tick();const fresh=h.requests.at(-1);assert.equal(fresh.url,'/api/admin/manual-actions');
  h.reply(fresh,ledgerSnapshot('Removed',false));await removing;assertEmptyLedger(h);
  h.reply(old,ledgerSnapshot('Stale'));await oldLoad;assertEmptyLedger(h);
});
test('manual ledger reads retain the latest refresh across every action type',async()=>{
  const h=manualLedgerHarness(),oldLoad=h.context.loadManualGroups(),old=h.requests.at(-1),freshLoad=h.context.loadManualActions(),fresh=h.requests.at(-1);
  h.reply(fresh,ledgerSnapshot('Removed',false));await freshLoad;h.reply(old,ledgerSnapshot('Stale'));await oldLoad;assertEmptyLedger(h);
});

const ledgerWrites=[
  ['group add','POST','/api/admin/manual-groups/a%2Fb/members'],
  ['group remove','DELETE','/api/admin/manual-groups/a%2Fb/members/source%2Fname/11'],
  ['main title','POST','/api/admin/entries/1/preferred-title'],
  ['alternate title','POST','/api/admin/entries/1/aliases'],
  ['remove titles','DELETE','/api/admin/entries/1/manual-titles'],
  ['manual match','POST','/api/admin/entries/1/manual-match'],
  ['separate source','POST','/api/admin/entries/1/separate-source'],
  ['allow source','DELETE','/api/admin/entries/1/separate-source/source%2Fname/11'],
  ['clear primary','DELETE','/api/admin/entries/1/matches/source%2Fname'],
  ['remove variant','DELETE','/api/admin/entries/1/variants/source%2Fname/11'],
  ['promote variant','POST','/api/admin/entries/1/variants/source%2Fname/11/promote'],
  ['remove entry','DELETE','/api/admin/entries/1?force=true'],
  ['chapter reject','POST','/api/admin/candidates/1/reject'],
  ['chapter restore','POST','/api/admin/candidates/1/restore'],
  ['duplicate review or clear','POST','/api/admin/duplicates/review'],
  ['explicit duplicate split','POST','/api/admin/entries/1/unmatch-duplicates'],
  ['unbind automatic attachments','POST','/api/admin/entries/1/unbind-auto-matches'],
  ['redo automation','POST','/api/admin/entries/1/redo-automation'],
  ['import manual Match anchor','POST','/api/admin/imports/import-id/items/1/match/2?background=true'],
  ['reset groups','POST','/api/admin/maintenance/reset-manual-groups'],
  ['reset matches','POST','/api/admin/maintenance/reset-manual-matches'],
  ['reset all decisions','POST','/api/admin/maintenance/reset-all-manual-actions'],
  ['database rebuild','POST','/api/admin/maintenance/reset-database'],
  ['full reset','POST','/api/admin/maintenance/full-reset'],
];
for(const [name,method,url] of ledgerWrites)test('manual ledger ownership starts and completes for '+name,async()=>{
  const h=manualLedgerHarness();await seedManualLedger(h);
  const oldLoad=h.context.loadManualActions(),old=h.requests.at(-1),saved=h.$('#manualGroupsList').innerHTML;
  const writing=h.context.api(url,{method,noMutationRefresh:true}),write=h.requests.at(-1);
  h.reply(old,ledgerSnapshot('Stale'));await oldLoad;
  assert.equal(h.$('#manualGroupsList').innerHTML,saved,'Write start must fence the old GET');
  const before=h.requests.length,waiting=h.context.loadManualActions();assert.equal(h.requests.length,before,'Wait for outstanding writes before reconciling');await waiting;
  h.reply(write);await writing;await h.flushReconcile();const fresh=h.requests.at(-1);
  assert.equal(fresh.url,'/api/admin/manual-actions');assert.equal(fresh.options.cache,'no-store');
  h.reply(fresh,ledgerSnapshot('Removed',false));await tick();assertEmptyLedger(h);
});

test('overlapping manual writes reconcile only after the last outcome settles',async()=>{
  const h=manualLedgerHarness();await seedManualLedger(h);
  const first=h.context.api('/api/admin/entries/1/aliases',{method:'POST'}),old=h.requests.at(-1);
  const second=h.context.api('/api/admin/candidates/1/reject',{method:'POST'}),current=h.requests.at(-1);
  h.reply(current);await second;await h.flushReconcile();assert.equal(h.requests.length,3);
  const before=h.requests.length,waiting=h.context.loadManualActions();assert.equal(h.requests.length,before);await waiting;
  h.reply(old);await first;await h.flushReconcile();assert.equal(h.requests.length,4);
  h.reply(h.requests.at(-1),ledgerSnapshot('Removed',false));await tick();assertEmptyLedger(h);
});
test('manual write ownership covers the response body and lost acknowledgement',async()=>{
  const h=manualLedgerHarness();await seedManualLedger(h);const body=deferred();
  const writing=h.context.api('/api/admin/candidates/1/restore',{method:'POST'}),write=h.requests.at(-1);
  write.resolve({ok:true,status:200,json:()=>body.promise});await tick();
  const before=h.requests.length,waiting=h.context.loadManualActions();assert.equal(h.requests.length,before);await waiting;
  const rejected=assert.rejects(writing,/Lost acknowledgement/);body.reject(Error('Lost acknowledgement'));await rejected;
  assert.match(h.$('#manualActionsStatus').textContent,/request failed; checking/);
  await h.flushReconcile();h.reply(h.requests.at(-1),ledgerSnapshot('Removed',false));await tick();assertEmptyLedger(h);
});
test('failed manual request reconciles an accepted result without replaying its write',async()=>{
  const h=manualLedgerHarness();await seedManualLedger(h);
  const oldLoad=h.context.loadManualActions(),old=h.requests.at(-1),removing=h.context.removeManualTitleAction(1,new Node()),write=h.requests.at(-1);
  write.reject(Error('Connection dropped after commit'));await removing;assert.match(h.$('#manualActionsStatus').textContent,/checking saved manual decisions/);
  await h.flushReconcile();h.reply(h.requests.at(-1),ledgerSnapshot('Removed',false));await tick();
  h.reply(old,ledgerSnapshot('Stale'));await oldLoad;assertEmptyLedger(h);
  assert.equal(h.requests.filter(item=>item.options.method==='DELETE').length,1);
});
test('failed reconciliation retains rows with an explicit retry state and no retry loop',async()=>{
  const h=manualLedgerHarness();await seedManualLedger(h);const saved=h.$('#manualGroupsList').innerHTML;
  const removing=h.context.allowManualSeparateAction(1,'a',11,new Node());h.requests.at(-1).reject(Error('Connection lost'));await removing;
  await h.flushReconcile();h.requests.at(-1).reject(Error('Ledger unavailable'));await tick();
  assert.equal(h.$('#manualGroupsList').innerHTML,saved);assert.equal(h.$('#manualActionsStatus').hidden,false);
  assert.match(h.$('#manualActionsStatus').textContent,/Ledger unavailable.*may be out of date; use Refresh/);
  const before=h.requests.length;await h.flushReconcile();assert.equal(h.requests.length,before);
  const retry=h.context.loadManualActions();h.reply(h.requests.at(-1),ledgerSnapshot('Removed',false));await retry;assertEmptyLedger(h);
});
test('obsolete manual ledger failures cannot replace a newer snapshot or its status',async()=>{
  const h=manualLedgerHarness(),oldLoad=h.context.loadManualActions(),old=h.requests.at(-1),freshLoad=h.context.loadManualActions();
  h.reply(h.requests.at(-1),ledgerSnapshot('Removed',false));await freshLoad;
  old.reject(Error('Old ledger failed'));await oldLoad;assertEmptyLedger(h);
});
test('hidden manual mutation defers reconciliation until the ledger is opened',async()=>{
  const h=manualLedgerHarness();await seedManualLedger(h);h.context.ledgerVisible=false;
  const writing=h.context.api('/api/admin/maintenance/reset-manual-matches',{method:'POST'});h.reply(h.requests.at(-1));await writing;await h.flushReconcile();assert.equal(h.requests.length,2);
  h.context.ledgerVisible=true;const loading=h.context.loadManualActions();h.reply(h.requests.at(-1),ledgerSnapshot('Removed',false));await loading;assertEmptyLedger(h);
});
test('Tags keeps its independent loader while a dirty ledger refreshes on return',async()=>{
  const h=manualLedgerHarness();await seedManualLedger(h);h.context.setManualActionsSubtab('tags');const tags=h.tagLoads;
  const writing=h.context.api('/api/admin/maintenance/reset-all-manual-actions',{method:'POST',noMutationRefresh:true});h.reply(h.requests.at(-1));await writing;await h.flushReconcile();assert.equal(h.requests.length,2);assert.equal(h.$('#manualActionsStatus').hidden,true);
  await h.context.loadManualActions();assert.equal(h.tagLoads,tags+1);assert.equal(h.requests.length,2);
  h.context.setManualActionsSubtab('titles');await h.flushReconcile();h.reply(h.requests.at(-1),ledgerSnapshot('Removed',false));await tick();assertEmptyLedger(h);
});
test('credential replacement holds manual write ownership until both attempts settle',async()=>{
  const h=manualLedgerHarness();await seedManualLedger(h);h.context.token='old';h.context.prompt=()=> 'replacement';
  const writing=h.context.api('/api/admin/entries/1/manual-titles',{method:'DELETE'});h.requests.at(-1).resolve({ok:false,status:401});await tick();
  assert.equal(h.requests.at(-1).options.headers['X-Admin-Token'],'replacement');
  const before=h.requests.length,waiting=h.context.loadManualActions();assert.equal(h.requests.length,before);await waiting;
  h.reply(h.requests.at(-1));await writing;assert.doesNotMatch(h.$('#manualActionsStatus').textContent,/request failed/);
  await h.flushReconcile();h.reply(h.requests.at(-1),ledgerSnapshot('Removed',false));await tick();assertEmptyLedger(h);
});
test('read-only POSTs and unrelated actions do not invalidate a manual ledger GET',async()=>{
  for(const [method,url] of [['POST','/api/admin/entries/summaries'],['POST','/api/admin/duplicate-candidates'],['POST','/api/admin/matrix/chapter-stats'],['POST','/api/admin/source-repositories/preview'],['POST','/api/admin/tags/review'],['POST','/api/admin/candidates/1/quality'],['GET','/api/admin/maintenance/reset-all-manual-actions'],['GET','/api/admin/manual-groups/group/candidates?q=test']]){
    const h=manualLedgerHarness(),loading=h.context.loadManualActions(),read=h.requests.at(-1),operation=h.context.api(url,{method});
    h.reply(h.requests.at(-1));await operation;h.reply(read,ledgerSnapshot('Saved'));await loading;
    assert.match(h.$('#manualGroupsList').innerHTML,/Saved/);assert.equal(h.$('#manualActionsStatus').hidden,true,url);
  }
});

(async()=>{
  const results=[];
  for(const item of cases){
    let deadline;
    try{
      await Promise.race([item.run(),new Promise((_,reject)=>{deadline=setTimeout(()=>reject(Error('Scenario did not settle: '+item.name)),5000);})]);
      results.push({name:item.name,passed:true});
    }catch(error){results.push({name:item.name,passed:false,error:String(error.stack||error)});}
    finally{clearTimeout(deadline);}
  }
  console.log(JSON.stringify({passed:results.filter(item=>item.passed).map(item=>item.name),failed:results.filter(item=>!item.passed)},null,2));
  if(results.some(item=>!item.passed))process.exitCode=1;
})().catch(error=>{console.error(error);process.exitCode=1;});
