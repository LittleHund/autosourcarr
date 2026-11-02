const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const projectRoot=process.env.AUTOSOURCARR_PROJECT_ROOT||path.resolve(__dirname,'../..');
const app=fs.readFileSync(path.join(projectRoot,'autosourcarr/app/static/app.js'),'utf8');
const block=(start,end)=>{const i=app.indexOf(start),j=app.indexOf(end,i);assert(i>=0&&j>i,start);return app.slice(i,j);};
const tick=()=>new Promise(resolve=>setImmediate(resolve));
function harness(){
 const timers=new Map(),calls=[],subs=[];let sequence=0;const h={};
 const context={console,Number,JSON,Promise,Math,Map,Set,document:{hidden:false,querySelector:()=>null},activeTab:'duplicates',
  setTimeout:(fn,delay)=>{const id=++sequence;timers.set(id,{fn,delay});return id;},clearTimeout:id=>timers.delete(id),
  duplicateRefreshTimer:null,duplicateRetryTimer:null,duplicateLoadSerial:0,duplicateViewRevision:0,duplicateConfirmedReviews:new Map(),duplicateData:null,
  AutoSourcarrMatchingReview:{historyActive:true,activateHistory:async()=>calls.push(['history'])},
  activeSurface:tab=>context.activeTab===tab,loadDuplicates:async(...args)=>calls.push(['loadDuplicates',...args]),
  api:async(...args)=>{calls.push(['api',...args]);return h.reply||{groups:[],pending:true};},
  liveSubscribe:config=>{subs.push({...config,topics:new Set(config.topics)});return {unsubscribe(){},refreshNow(){}};},liveConnected:()=>false,
  $:()=>({innerHTML:''}),pruneDuplicateReviewGroups:groups=>groups,
  renderDuplicateAutomationStatus(){},structuralRenderBlocked:()=>false,renderDuplicates:()=>calls.push(['renderDuplicates']),requestSurfaceRender:()=>calls.push(['render']),
 };context.window=context;vm.createContext(context);
 vm.runInContext(block('function scheduleDuplicateLiveRefresh(','function scheduleMutationRefresh('),context);
 return Object.assign(h,{context,calls,timers,subs,fire:async id=>{const item=timers.get(id);assert(item,id);timers.delete(id);await item.fn();await tick();return item.delay;}});
}
(async()=>{
 // UI-13: the candidate view no longer polls. It subscribes to job outcome events and refreshes only while visible.
 const h=harness(),c=h.context;
 vm.runInContext(block('const liveJobOutcome=','function startLiveUpdates('),c);
 c.subscribeDuplicateUpdates();
 assert.equal(h.subs.length,1);const sub=h.subs[0];
 assert.deepEqual(Array.from(sub.topics),['jobs'],'Duplicates follow job events');assert.equal(h.timers.size,0,'Subscribing starts no timer');
 assert.equal(sub.filter({topic:'jobs',data:{change:'progress'}}),false,'Progress ticks do not reload candidates');
 assert.equal(sub.filter({topic:'jobs',data:{change:'completed'}}),true,'A finished snapshot or sweep does');
 assert.equal(sub.when(),false,'Hidden history is not refreshed');
 await sub.refresh({events:[]});assert.equal(h.calls.length,0,'History never reads candidate endpoints');
 c.scheduleDuplicateLiveRefresh();await h.fire(c.duplicateRefreshTimer);assert.equal(h.calls.length,0,'Mutation refresh remains quiet in history');
 c.AutoSourcarrMatchingReview.historyActive=false;c.document.hidden=true;assert.equal(sub.when(),false,'Hidden document is not refreshed');
 c.document.hidden=false;c.activeTab='tasks';assert.equal(sub.when(),false,'Inactive duplicates tab is not refreshed');
 c.activeTab='duplicates';assert.equal(sub.when(),true);await sub.refresh({events:[]});assert.equal(h.calls.length,1,'Visible candidate view refreshes on an event');
 c.scheduleDuplicateLiveRefresh();await h.fire(c.duplicateRefreshTimer);assert.equal(h.calls.length,2,'Visible mutation refresh still works');
 const retry=harness(),r=retry.context;
 vm.runInContext(block('async function loadDuplicates(','const duplicateReviewBusy='),r);
 r.activeTab='duplicates';r.AutoSourcarrMatchingReview.historyActive=false;
 await r.loadDuplicates(null,{render:false,quiet:true});
 assert.equal(r.duplicateRetryTimer,null,'A pending snapshot waits for its job outcome event, not a timer');
 retry.reply={groups:[],pending:false,subscription_pending:true};await r.loadDuplicates(null,{render:false,quiet:true});
 assert.equal(await retry.fire(r.duplicateRetryTimer),3000,'Library membership (no event yet) keeps one slow re-check without live events');
 r.liveConnected=()=>true;await r.loadDuplicates(null,{render:false,quiet:true});assert.equal(retry.timers.get(r.duplicateRetryTimer).delay,10000,'and a slower one with live events');
 r.activeTab='tasks';const base=retry.calls.filter(x=>x[0]==='api').length;await retry.fire(r.duplicateRetryTimer);
 assert.equal(retry.calls.filter(x=>x[0]==='api').length,base,'The re-check makes no GET after leaving its tab');
 r.activeTab='duplicates';r.AutoSourcarrMatchingReview.historyActive=true;await r.loadDuplicates(null,{render:false,quiet:true});const history=retry.calls.filter(x=>x[0]==='api').length;
 await retry.fire(r.duplicateRetryTimer);assert.equal(retry.calls.filter(x=>x[0]==='api').length,history,'Switching to history stops the re-check');
 retry.reply={groups:[],pending:true};
 Object.assign(r,{browseState:{items:[]},importsState:{items:[]},matrixDataset:()=>null,duplicateData:{groups:[{entries:[{id:1,description:'',thumbnail_url:'',cover_cached:false}]}]}});
 vm.runInContext(block('function sparseEntryIds(','function patchCompactSummary('),r);
 assert.deepEqual(Array.from(r.sparseEntryIds()),[],'History does not hydrate hidden candidate covers/metadata');
 r.AutoSourcarrMatchingReview.historyActive=false;assert.deepEqual(Array.from(r.sparseEntryIds()),[1],'Candidate covers still hydrate');
 vm.runInContext(block('async function loadActiveTab(','function ensureSettingsPane('),r);
 r.AutoSourcarrMatchingReview.historyActive=true;const before=retry.calls.filter(x=>x[0]==='api').length;await r.loadActiveTab('duplicates');assert.equal(retry.calls.filter(x=>x[0]==='api').length,before);assert(retry.calls.some(x=>x[0]==='history'),'History boot routes to its existing request');
 console.log(JSON.stringify({passed:['subscription on job outcomes, no timer','history quiet','history mutation refresh quiet','hidden document quiet','inactive tab quiet','visible event refresh','visible mutation refresh preserved','pending waits for event','membership slow re-check','inactive re-check quiet','history re-check quiet','history hidden metadata hydration quiet','candidate metadata hydration preserved','restored history routes without candidate GET']}));
})().catch(e=>{console.error(e);process.exitCode=1;});
