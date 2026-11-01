// Execute production duplicate-review functions with controlled HTTP and DOM.
const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict');
const source=fs.readFileSync(process.argv[2]||__dirname+'/../app/static/app.js','utf8');
function extract(start,end){
  const a=source.indexOf(start),b=source.indexOf(end,a);
  assert(a>=0&&b>a,`Production function markers missing: ${start} -> ${end}`);
  return source.slice(a,b);
}
const tests=[];
function test(name,body){tests.push({name,body});}
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const copy=x=>JSON.parse(JSON.stringify(x));
function snapshot(pairs=[[1,2],[3,4],[1,5]]){
  const ids=[...new Set(pairs.flat())];
  return {groups:pairs.length?[{entry_ids:ids,entries:ids.map(id=>({id,preferred_title:`Work ${id}`})),pairs:pairs.map(([left_id,right_id])=>({left_id,right_id}))}]:[],pending:false,stale:false};
}
function pairKeys(data){return Array.from(data.groups||[]).flatMap(g=>Array.from(g.pairs||[],p=>[p.left_id,p.right_id].sort((a,b)=>a-b).join(':'))).sort();}
function makeButton(left,right){return {dataset:{reviewLeft:String(left),reviewRight:String(right)},disabled:false,attributes:{},setAttribute(k,v){this.attributes[k]=v;}};}
function harness(initial=snapshot()){
  const requests=[],toasts=[],buttons=[makeButton(1,2),makeButton(1,2),makeButton(1,5),makeButton(3,4)],nodes={};
  const scheduled=[];
  const ctx={duplicateData:copy(initial),duplicateManagerEntryId:null,duplicateRetryTimer:null,
    document:{querySelectorAll:selector=>selector==='[data-review-left]'?buttons:[],querySelector:()=>null},
    $:selector=>nodes[selector]||(nodes[selector]={innerHTML:''}),
    api:(url,options={})=>new Promise((resolve,reject)=>requests.push({url,options,resolve,reject})),
    confirm:()=>true,toast:message=>toasts.push(message),requestSurfaceRender:()=>{},renderDuplicateAutomationStatus:()=>{},renderDuplicates:()=>{},structuralRenderBlocked:()=>false,
    setTimeout:fn=>{scheduled.push(fn);return scheduled.length;},clearTimeout:()=>{},scheduleDuplicateLiveRefresh:()=>{},
    refreshBothMatrices:async()=>{},health:async()=>{},loadManualGroups:async()=>{},
    duplicateBrowserIndex:()=>new Map(),openDuplicateManager:()=>{},closeDuplicateManager:()=>{},
    esc:String,jsq:String,shortText:String,entryCoverUrl:id=>`/cover/${id}`};
  ctx.window=ctx;
  vm.createContext(ctx);
  vm.runInContext(extract('async function loadDuplicates(','$(\'#closeDuplicateManager\').onclick='),ctx);
  return {ctx,requests,toasts,buttons,scheduled};
}
function answer(request,decision='not_duplicate',groups=snapshot().groups){request.resolve({decision,groups:copy(groups),pending:false,canonical_entry_id:1});}

test('Unrelated duplicate pairs submit simultaneously and share neither disabled state nor result ownership',async()=>{
  const {ctx,requests,buttons}=harness();
  const first=ctx.reviewDuplicate(1,2,'not_duplicate');
  assert.equal(requests.length,1);
  assert.deepEqual(buttons.map(b=>b.disabled),[true,true,true,false]);
  const second=ctx.reviewDuplicate(3,4,'not_duplicate');
  assert.equal(requests.length,2,'Unrelated pair should send a POST before the first resolves');
  assert.deepEqual(requests.map(r=>JSON.parse(r.options.body).left_entry_id),[1,3]);
  answer(requests[0]);await first;
  assert.deepEqual(buttons.map(b=>b.disabled),[false,false,false,true]);
  answer(requests[1]);await second;
  assert(buttons.every(b=>!b.disabled));
  assert.deepEqual(pairKeys(ctx.duplicateData),['1:5']);
});

test('Repeated or overlapping-entry pairs are blocked while an unrelated pair remains available',async()=>{
  const {ctx,requests}=harness();
  const job=ctx.reviewDuplicate(1,2,'not_duplicate');
  await ctx.reviewDuplicate(2,1,'not_duplicate');
  await ctx.reviewDuplicate(1,5,'not_duplicate');
  assert.equal(requests.length,1);
  assert.match(ctx.duplicateReviewButtonAttributes(2,1),/aria-busy="true" disabled/);
  assert(!ctx.duplicateReviewButtonAttributes(3,4).includes('disabled'));
  answer(requests[0]);await job;
  const next=ctx.reviewDuplicate(1,5,'not_duplicate');
  assert.equal(requests.length,2,'Completed entry ownership must be released');
  answer(requests[1]);await next;
});

test('A GET started before a manual decision cannot restore the reviewed pair after the POST completes',async()=>{
  const {ctx,requests}=harness();
  const oldGet=ctx.loadDuplicates();
  const review=ctx.reviewDuplicate(1,2,'not_duplicate');
  answer(requests[1]);await review;
  requests[0].resolve(snapshot());await oldGet;
  assert.deepEqual(pairKeys(ctx.duplicateData),['1:5','3:4']);
});

test('Out-of-order POST snapshots cannot restore either already reviewed pair',async()=>{
  const {ctx,requests}=harness();
  const first=ctx.reviewDuplicate(1,2,'not_duplicate');
  const second=ctx.reviewDuplicate(3,4,'not_duplicate');
  answer(requests[1],'not_duplicate',snapshot([[1,2],[1,5]]).groups);await second;
  assert.deepEqual(pairKeys(ctx.duplicateData),['1:2','1:5']);
  answer(requests[0],'not_duplicate',snapshot([[3,4],[1,5]]).groups);await first;
  assert.deepEqual(pairKeys(ctx.duplicateData),['1:5']);
});

test('A fresh authoritative empty GET clears previous duplicate results',async()=>{
  const {ctx,requests}=harness();
  const loading=ctx.loadDuplicates();requests[0].resolve(snapshot([]));await loading;
  assert.deepEqual(pairKeys(ctx.duplicateData),[]);
  assert.equal(ctx.duplicateData.groups.length,0);
});

test('Newer GET completion wins over an older concurrent GET even without a manual action',async()=>{
  const {ctx,requests}=harness();
  const older=ctx.loadDuplicates(),newer=ctx.loadDuplicates();
  requests[1].resolve(snapshot([[3,4]]));await newer;
  requests[0].resolve(snapshot());await older;
  assert.deepEqual(pairKeys(ctx.duplicateData),['3:4']);
});

test('Confirmed merge prunes every affected relationship while leaving unrelated work reviewable',async()=>{
  const {ctx,requests}=harness();
  const merge=ctx.reviewDuplicate(1,2,'duplicate');
  answer(requests[0],'duplicate');await merge;
  assert.deepEqual(pairKeys(ctx.duplicateData),['3:4']);
  assert.deepEqual(Array.from(ctx.duplicateData.groups[0].entries,e=>e.id),[3,4]);
});

test('Declining merge confirmation sends no request and acquires no review ownership',async()=>{
  const {ctx,requests,buttons}=harness();ctx.confirm=()=>false;
  await ctx.reviewDuplicate(1,2,'duplicate');
  assert.equal(requests.length,0);assert(buttons.every(b=>!b.disabled));
  assert.equal(ctx.duplicatePairBusy(1,2),false);
});

test('Failed POST keeps a failure visible and releases its controls after reconciling with the server',async()=>{
  const {ctx,requests,buttons,toasts}=harness();
  const job=ctx.reviewDuplicate(1,2,'not_duplicate');requests[0].reject(new Error('Request timed out'));await tick();
  assert.equal(requests.length,2);assert.equal(requests[1].url,'/api/v1/duplicates');
  requests[1].resolve(snapshot());await job;
  assert(toasts.some(x=>x.includes('Duplicate review failed: Request timed out')));
  assert(!toasts.some(x=>x==='Marked as different works'));
  assert(buttons.every(b=>!b.disabled));
});

test('Both direct-entry and evidence controls render the same pair ownership attributes',()=>{
  const {ctx}=harness();
  vm.runInContext(extract('function duplicateEntryCard(','function duplicateBrowserIndex('),ctx);
  const e={id:2,preferred_title:'Candidate',titles:[],sources:[]};
  const direct=ctx.duplicateEntryCard(e,1);
  const evidence=ctx.pairEvidence({left_id:1,right_id:2},[{id:1},e]);
  assert.equal((direct.match(/data-review-left="1" data-review-right="2"/g)||[]).length,2);
  assert.equal((evidence.match(/data-review-left="1" data-review-right="2"/g)||[]).length,2);
});

test('Post-review stale backend snapshot does not restore a decision awaiting index refresh',async()=>{
  const {ctx,requests}=harness();
  const review=ctx.reviewDuplicate(1,2,'not_duplicate');
  requests[0].resolve({decision:'not_duplicate',pending:true,groups:snapshot().groups});await review;
  assert.deepEqual(pairKeys(ctx.duplicateData),['1:5','3:4']);
  const refresh=ctx.loadDuplicates();requests[1].resolve({...snapshot(),stale:true,pending:true});await refresh;
  assert.deepEqual(pairKeys(ctx.duplicateData),['1:5','3:4'],'Known stale groups restored the just-reviewed pair');
});
test('Review completion preserves the manager the user selected while another decision saved',async()=>{
  const {ctx,requests}=harness();const opened=[];
  ctx.duplicateManagerEntryId=1;
  ctx.duplicateBrowserIndex=()=>new Map([[1,{}],[3,{}]]);
  ctx.openDuplicateManager=id=>{opened.push(id);ctx.duplicateManagerEntryId=id;};
  const review=ctx.reviewDuplicate(1,2,'not_duplicate');
  ctx.duplicateManagerEntryId=3;
  answer(requests[0]);await review;
  assert.equal(ctx.duplicateManagerEntryId,3,'An earlier response reopened the previous selected manager');
});
(async()=>{let failed=0;for(const t of tests){try{await t.body();console.log('PASS '+t.name);}catch(e){failed++;console.log('FAIL '+t.name+'\n'+e.stack);}}console.log(`${tests.length-failed}/${tests.length} passed`);process.exitCode=failed?1:0;})();
