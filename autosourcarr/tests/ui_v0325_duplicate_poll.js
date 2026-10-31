const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const projectRoot=process.env.AUTOSOURCARR_PROJECT_ROOT||path.resolve(__dirname,'../..');
const app=fs.readFileSync(path.join(projectRoot,'autosourcarr/app/static/app.js'),'utf8');
const block=(start,end)=>{const i=app.indexOf(start),j=app.indexOf(end,i);assert(i>=0&&j>i,start);return app.slice(i,j);};
const tick=()=>new Promise(resolve=>setImmediate(resolve));
function harness(){
 const timers=new Map(),calls=[];let sequence=0;
 const context={console,Number,JSON,Promise,Math,Map,Set,document:{hidden:false,querySelector:()=>null},activeTab:'duplicates',
  setTimeout:(fn,delay)=>{const id=++sequence;timers.set(id,{fn,delay});return id;},clearTimeout:id=>timers.delete(id),
  duplicateHeartbeatTimer:null,duplicateRefreshTimer:null,duplicateRetryTimer:null,duplicateLoadSerial:0,duplicateViewRevision:0,duplicateConfirmedReviews:new Map(),duplicateData:null,
  AutoSourcarrMatchingReview:{historyActive:true,activateHistory:async()=>calls.push(['history'])},
  activeSurface:tab=>context.activeTab===tab,loadDuplicates:async(...args)=>calls.push(['loadDuplicates',...args]),
  api:async(...args)=>{calls.push(['api',...args]);return {groups:[],pending:true};},
  $:()=>({innerHTML:''}),pruneDuplicateReviewGroups:groups=>groups,
  renderDuplicateAutomationStatus(){},structuralRenderBlocked:()=>false,renderDuplicates:()=>calls.push(['renderDuplicates']),requestSurfaceRender:()=>calls.push(['render']),
 };context.window=context;vm.createContext(context);
 vm.runInContext(block('function scheduleDuplicateLiveRefresh(','function scheduleMutationRefresh('),context);
 return {context,calls,timers,fire:async id=>{const item=timers.get(id);assert(item,id);timers.delete(id);await item.fn();await tick();return item.delay;}};
}
(async()=>{
 const h=harness(),c=h.context;
 c.scheduleDuplicateHeartbeat();assert.equal(await h.fire(c.duplicateHeartbeatTimer),4500);assert.equal(h.calls.length,0,'Hidden history does not poll candidate endpoints');
 c.scheduleDuplicateLiveRefresh();await h.fire(c.duplicateRefreshTimer);assert.equal(h.calls.length,0,'Mutation refresh remains quiet in history');
 c.AutoSourcarrMatchingReview.historyActive=false;c.document.hidden=true;await h.fire(c.duplicateHeartbeatTimer);assert.equal(h.calls.length,0,'Hidden document does not poll');
 c.document.hidden=false;c.activeTab='tasks';await h.fire(c.duplicateHeartbeatTimer);assert.equal(h.calls.length,0,'Inactive duplicates tab does not poll');
 c.activeTab='duplicates';await h.fire(c.duplicateHeartbeatTimer);assert.equal(h.calls.length,1,'Visible candidate view continues to poll');
 c.scheduleDuplicateLiveRefresh();await h.fire(c.duplicateRefreshTimer);assert.equal(h.calls.length,2,'Visible mutation refresh still works');
 const retry=harness(),r=retry.context;
 vm.runInContext(block('async function loadDuplicates(','const duplicateReviewBusy='),r);
 r.activeTab='tasks';await r.loadDuplicates(null,{render:false,quiet:true});const base=retry.calls.length;
 assert.equal(await retry.fire(r.duplicateRetryTimer),1800);assert.equal(retry.calls.length,base,'Pending candidate retry makes no GET after leaving its tab');
 r.activeTab='duplicates';r.AutoSourcarrMatchingReview.historyActive=false;await r.loadDuplicates(null,{render:false,quiet:true});const visible=retry.calls.filter(x=>x[0]==='api').length;
 await retry.fire(r.duplicateRetryTimer);assert.equal(retry.calls.filter(x=>x[0]==='api').length,visible+1,'Visible pending candidate retry continues');
 r.AutoSourcarrMatchingReview.historyActive=true;const history=retry.calls.filter(x=>x[0]==='api').length;await retry.fire(r.duplicateRetryTimer);assert.equal(retry.calls.filter(x=>x[0]==='api').length,history,'Switching to history stops pending retries');
 Object.assign(r,{browseState:{items:[]},importsState:{items:[]},matrixDataset:()=>null,duplicateData:{groups:[{entries:[{id:1,description:'',thumbnail_url:'',cover_cached:false}]}]}});
 vm.runInContext(block('function sparseEntryIds(','function patchCompactSummary('),r);
 assert.deepEqual(Array.from(r.sparseEntryIds()),[],'History does not hydrate hidden candidate covers/metadata');
 r.AutoSourcarrMatchingReview.historyActive=false;assert.deepEqual(Array.from(r.sparseEntryIds()),[1],'Candidate covers still hydrate');
 vm.runInContext(block('async function loadActiveTab(','function ensureSettingsPane('),r);
 r.AutoSourcarrMatchingReview.historyActive=true;const before=retry.calls.filter(x=>x[0]==='api').length;await r.loadActiveTab('duplicates');assert.equal(retry.calls.filter(x=>x[0]==='api').length,before);assert(retry.calls.some(x=>x[0]==='history'),'History boot routes to its existing request');
 console.log(JSON.stringify({passed:['history heartbeat quiet','history mutation refresh quiet','hidden document quiet','inactive tab quiet','visible heartbeat preserved','visible mutation refresh preserved','inactive pending retry quiet','visible retry preserved','history pending retry quiet','history hidden metadata hydration quiet','candidate metadata hydration preserved','restored history routes without candidate GET']}));
})().catch(e=>{console.error(e);process.exitCode=1;});
