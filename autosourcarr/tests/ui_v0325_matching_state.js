/* Independent behavior tests for the 0.3.25 matching/history navigation changes.
 * Point AUTOSOURCARR_PROJECT_ROOT at a project containing autosourcarr/app/static.
 * No browser rendering claim; production public actions/normalizer run in Node VM.
 */
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const projectRoot=process.env.AUTOSOURCARR_PROJECT_ROOT||path.resolve(__dirname,'../..');
const staticRoot=path.join(projectRoot,'autosourcarr/app/static');
const app=fs.readFileSync(path.join(staticRoot,'app.js'),'utf8');
const KEY='autosourcarr.navigation.v1';
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const deferred=()=>{let resolve,reject;const promise=new Promise((a,b)=>{resolve=a;reject=b;});return {resolve,reject,promise};};
const slice=(start,end)=>{const i=app.indexOf(start),j=app.indexOf(end,i);assert(i>=0&&j>i,start);return app.slice(i,j);};
function harness(saved=null,initialTab='duplicates'){
const nodes=new Map(),calls=[],inspected=[],splits=[];
class Node {
 constructor(id=''){this.id=id;this.value='';this.dataset={};this.events={};this.disabled=false;this.children=[];this.textContent='';this.hidden=false;this.options=[];}
 addEventListener(name,fn){this.events[name]=fn;}setAttribute(key,value){this[key]=value;}focus(){}scrollIntoView(){}replaceChildren(){this.children=[];this.html='';}
 set innerHTML(value){
  this.html=value;this.children=[];
  if(this.id==='matchingPolicyTarget'){this.options=[...value.matchAll(/<option value="([^"]+)"/g)].map(m=>({value:m[1]}));this.value=this.options[0]?.value||'';}
  if(this.id==='matchingPolicyForms')for(const m of value.matchAll(/<form\b[^>]*data-direction="([^"]+)"[^>]*>([\s\S]*?)<\/form>/g)){
   const n=new Node();n.dataset.direction=m[1];n.elements={};n.fieldset=new Node();n.fieldset.disabled=/<fieldset disabled/.test(m[2]);n.status=new Node();n.help=new Node();n.origin=new Node();n.button=new Node();n.elements.preset=new Node();
   n.elements.mode=new Node();n.elements.mode.value=/<option value="([^"]+)" selected/.exec(m[2])?.[1]||'inherit';
   for(const input of m[2].matchAll(/<input\b([^>]+)>/g)){const attrs=input[1],name=/name="([^"]+)"/.exec(attrs)?.[1];if(!name)continue;const control=new Node();control.value=/value="([^"]+)"/.exec(attrs)?.[1]||'';control.checked=/\bchecked\b/.test(attrs);n.elements[name]=control;}
   this.children.push(n);
  }
  if(this.id==='mergeHistoryList'){
   for(const m of value.matchAll(/<button[^>]*data-(inspect|split)="([^"]+)"[^>]*>/g)){const n=new Node();n.dataset[m[1]]=m[2];this.children.push(n);}
   for(const m of value.matchAll(/<details data-event="([^"]+)">/g)){const n=new Node();n.dataset.event=m[1];n.target=new Node();this.children.push(n);}
  }
 }
 get innerHTML(){return this.html||'';}
 querySelector(selector){return selector==='fieldset'?this.fieldset:selector==='.ruleSaveStatus'?this.status:selector==='.rulePresetHelp'?this.help:selector==='.ruleOrigin'?this.origin:selector==='.mergeHistoryDetail'?this.target:null;}
 querySelectorAll(selector){if(selector==='input')return Object.entries(this.elements).filter(([k])=>!['mode','preset'].includes(k)).map(([,v])=>v);if(selector==='input, select, fieldset')return [...Object.values(this.elements),this.fieldset];if(selector==='form')return this.children;if(selector==='button')return this.children.map(n=>n.button);if(selector==='details')return this.children.filter(n=>n.dataset.event);if(selector==='[data-inspect]')return this.children.filter(n=>n.dataset.inspect);if(selector==='[data-split]')return this.children.filter(n=>n.dataset.split);return [];}
}
for(const id of ['matchingPoliciesPanel','matchingPolicyTarget','matchingPolicyForms','matchingPolicyStatus','matchingPolicyReload','duplicateCandidatesPanel','duplicateHistoryPanel','duplicatePossibleView','duplicateHistoryView','mergeHistoryCount','mergeHistoryPrevious','mergeHistoryNext','mergeHistoryList','mergeHistoryQuery','mergeHistorySearch'])nodes.set(id,new Node(id));
const config={revision:'r1',groups:[{id:7,name:'My <group>'}],sources:[{id:'a',name:'Source A',lang:'en',group_id:7}],rules:[{scope:'group',subject:'7',direction:'external',rule:{enabled:false,checks:{}}}]};
let pendingHistory=null,pendingSave=null;
const context={console,Number,String,Date,JSON,Promise,encodeURIComponent,document:{getElementById:id=>nodes.get(id)},esc:value=>String(value??'').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;'),
 openBrowseEntry:(id,event)=>{assert(event?.isTrusted,'Current app requires a trusted click event');inspected.push(id);},openUnmatchDuplicatesForEntry:id=>splits.push(id),
 api:async(path,options)=>{
  calls.push([path,options]);if(options?.method==='PUT'){const body=JSON.parse(options.body);assert.equal(body.revision,config.revision);if(pendingSave)await pendingSave;config.revision+='x';return structuredClone(config);}
  if(path==='/api/admin/matching-policies')return structuredClone(config);
  if(path.startsWith('/api/admin/duplicates/history?')){if(pendingHistory)return pendingHistory;return {items:[{id:9,left_title:'<img src=x>',right_title:'Second title',merged_at:'2026-10-04T12:00:00Z',state:'Recorded merge',reason:'Saved proof',target_entry_id:3,duplicate_entry_id:4,current_entry_id:5}],total:51};}
  return {before:[{role:'Surviving entry',available:true,entry:{id:3,preferred_title:'Original title',description:'<script>bad()</script>'},titles:['Alias'],records:[]}],evidence:{reason:'Saved evidence',direct_pairs:2}};
 }};context.window=context;vm.createContext(context);vm.runInContext(fs.readFileSync(path.join(staticRoot,'matching-review.js'),'utf8'),context);

const storage=new Map(saved?[[KEY,JSON.stringify({...saved,owner:saved.owner||'local:Alice'})]]:[]),eventHandlers=new Map();
const activeTab=new Node(initialTab);
Object.assign(context,{
  Map,Set,MutationObserver:class{observe(){}},queueMicrotask,
  sessionStorage:{getItem:key=>storage.get(key)||null,setItem:(key,value)=>storage.set(key,value),removeItem:key=>storage.delete(key)},
  localStorage:{getItem:()=>'local:Alice'},
  scrollX:0,scrollY:0,setTimeout:()=>1,clearTimeout(){},requestAnimationFrame:()=>1,cancelAnimationFrame(){},
  addEventListener:(name,fn)=>eventHandlers.set(name,fn),
  browseState:{mode:'POPULAR',page:1},importsState:{runId:'',filter:'all',review:'all',offset:0},
  workbenchState:{importKey:'',importId:null,duplicateLeft:null,duplicateRight:null,taskLane:'all',libraryScope:'all'},
  manualActionsSubtab:'matches',duplicateScope:'subbed',matrixData:null,subbedData:null,duplicateData:null,
  readLocalUiPreference:()=>'',setManualActionsSubtab(){},setSourceSubtab(){},setSettingsSubtab(){},
  syncDuplicateScopeButtons(){},syncBrowseConnectionControls(){},setBrowseMode(){},
});
context.document.querySelector=selector=>selector==='main > .tab.active'?activeTab:selector==='main'?new Node('main'):null;
context.document.querySelectorAll=()=>[];
context.document.addEventListener=(name,fn)=>eventHandlers.set(name,fn);
vm.runInContext(fs.readFileSync(path.join(staticRoot,'navigation-state.js'),'utf8'),context);

context.activeSurface=tab=>activeTab.id===tab;context.document.hidden=false;
context.loadDuplicates=async(...args)=>calls.push(['loadDuplicates',args]);
context.toast=message=>calls.push(['toast',message]);
return {context,nodes,calls,storage,activeTab,config,setHistoryPromise:value=>pendingHistory=value,originalApi:context.api};
}

const tests=[];const test=(name,fn)=>tests.push({name,fn});
test('saved matching state roundtrips through Navigation.initialize without persisting drafts',async()=>{
 const h=harness(),{context:c,nodes,calls,storage}=h;c.AutoSourcarrNavigation.initialize();assert.equal(calls.length,0);
 nodes.get('duplicateHistoryView').events.click();await tick();
 nodes.get('mergeHistoryQuery').value='a saved title';nodes.get('mergeHistorySearch').events.click();await tick();
 nodes.get('mergeHistoryNext').events.click();await tick();c.openMatchingPolicy('source','a');await tick();
 nodes.get('matchingPolicyForms').children[1].elements.summaryPercent.value='draft-secret';nodes.get('mergeHistoryQuery').value='unsubmitted query';
 c.AutoSourcarrNavigation.save();const saved=JSON.parse(storage.get(KEY));
 assert.deepEqual(saved.matching,{view:'history',query:'a saved title',offset:50,target:'source:a',open:true});
 assert(!JSON.stringify(saved).includes('draft-secret'));assert(!JSON.stringify(saved).includes('unsubmitted query'));
 const restored=harness(saved),r=restored.context.AutoSourcarrMatchingReview;
 assert.equal(restored.context.AutoSourcarrNavigation.initialize().tab,'duplicates');
 assert.deepEqual(JSON.parse(JSON.stringify(r.getViewState())),saved.matching);
 assert.equal(restored.calls.length,0,'Restored selection and disclosure stay lazy');
 assert(restored.nodes.get('duplicateCandidatesPanel').hidden);assert(!restored.nodes.get('duplicateHistoryPanel').hidden);
 assert.equal(restored.nodes.get('matchingPolicyForms').children.length,0,'Unsaved thresholds are not reconstructed');
});
test('normalizer validates bounded matching state and drops unknown fields',async()=>{
 const h=harness(),c=h.context.AutoSourcarrNavigation;
 const normalized=c.normalize({version:1,matching:{view:'history',query:'x'.repeat(250),offset:71,target:'source:<script>',open:'true',secret:'secret'}});
 assert.equal(normalized.matching.offset,50);assert.equal(normalized.matching.query.length,200);assert.equal(normalized.matching.target,'');assert.equal(normalized.matching.open,false);assert(!JSON.stringify(normalized).includes('secret'));
});
test('restored history request coalesces and corrects an obsolete empty page',async()=>{
 const h=harness({version:1,tab:'duplicates',matching:{view:'history',query:'saved',offset:50}}),c=h.context,r=c.AutoSourcarrMatchingReview;
 c.AutoSourcarrNavigation.initialize();const pending=deferred();h.setHistoryPromise(pending.promise);
 const a=r.activateHistory(),b=r.activateHistory();assert.equal(a,b);assert.equal(h.calls.length,1);assert(h.calls[0][0].includes('offset=50'));
 pending.resolve({items:[],total:0});await a;await tick();h.setHistoryPromise(null);
 assert.equal(r.getViewState().offset,0);assert(h.calls.some(([p])=>p.includes('offset=0')));assert(h.nodes.get('mergeHistoryPrevious').disabled);assert(h.nodes.get('mergeHistoryNext').disabled);
 const count=h.calls.length;await r.activateHistory();assert.equal(h.calls.length,count,'Loaded history is reused on tab activation');
});
test('restored history corrects a page beyond the last recorded merge',async()=>{
 const h=harness({version:1,tab:'duplicates',matching:{view:'history',offset:1000000}}),c=h.context;
 c.AutoSourcarrNavigation.initialize();await c.AutoSourcarrMatchingReview.activateHistory();
 assert.equal(c.AutoSourcarrMatchingReview.getViewState().offset,50);assert(h.calls[0][0].includes('offset=1000000'));assert(h.calls.at(-1)[0].includes('offset=50'));
});
test('restored rules load only on Sources and replace obsolete GET ownership',async()=>{
 const h=harness({version:1,tab:'duplicates',matching:{target:'source:a',open:true}}),c=h.context,r=c.AutoSourcarrMatchingReview;
 c.AutoSourcarrNavigation.initialize();h.nodes.get('matchingPoliciesPanel').events.toggle();await tick();assert.equal(h.calls.length,0);
 h.activeTab.id='sources';const old=deferred(),fresh=deferred();let ruleRequests=0;
 c.api=(p,o)=>{h.calls.push([p,o]);assert.equal(p,'/api/admin/matching-policies');return ++ruleRequests===1?old.promise:fresh.promise;};
 const first=r.activateRules();r.restoreViewState({target:'source:a',open:true});const second=r.activateRules();assert.equal(ruleRequests,2,'Restore cannot leave loading stuck on an obsolete GET');
 fresh.resolve({...structuredClone(h.config),revision:'new',groups:[{id:7,name:'New rules'}]});await second;
 old.resolve({...structuredClone(h.config),revision:'old',groups:[{id:7,name:'Old rules'}]});await first;
 assert(h.nodes.get('matchingPolicyTarget').innerHTML.includes('New rules'));assert(!h.nodes.get('matchingPolicyTarget').innerHTML.includes('Old rules'));assert.equal(h.nodes.get('matchingPolicyTarget').value,'source:a');
});
test('actual Refresh button dispatches to the displayed duplicate view',async()=>{
 const h=harness(),c=h.context;c.AutoSourcarrNavigation.initialize();c.$=selector=>h.nodes.get(selector.slice(1));h.nodes.set('refreshDuplicates',{onclick:null});
 const refreshLine=app.split('\n').find(line=>line.startsWith("$('#refreshDuplicates').onclick="));assert(refreshLine);vm.runInContext(refreshLine,c);
 h.nodes.get('duplicateHistoryView').events.click();await tick();const before=h.calls.length;h.nodes.get('refreshDuplicates').onclick();await tick();
 assert.equal(h.calls.length,before+1);assert(h.calls.at(-1)[0].startsWith('/api/admin/duplicates/history?'));assert(!h.calls.some(([p])=>p==='loadDuplicates'));
 c.AutoSourcarrMatchingReview.showCandidates();h.nodes.get('refreshDuplicates').onclick();await tick();assert.equal(h.calls.at(-1)[0],'loadDuplicates');
});
test('identity reset clears matching state without requests or draft restoration',async()=>{
 const h=harness({version:1,owner:'local:Alice',tab:'duplicates',matching:{view:'history',query:'old user query',offset:50,target:'source:a',open:true}}),c=h.context;
 c.AutoSourcarrNavigation.initialize();c.AutoSourcarrNavigation.identityChanged('local:Bob');
 assert.deepEqual(JSON.parse(JSON.stringify(c.AutoSourcarrMatchingReview.getViewState())),{view:'possible',query:'',offset:0,target:'',open:false});
 assert.equal(h.calls.length,0);assert.equal(h.nodes.get('mergeHistoryQuery').value,'');assert(!h.nodes.get('matchingPoliciesPanel').open);assert.equal(h.nodes.get('matchingPolicyForms').children.length,0);assert(!h.storage.has(KEY));
});
test('obsolete rule save cannot install old-owner configuration or alter new controls',async()=>{
 const h=harness({version:1,owner:'local:Alice',tab:'sources',matching:{target:'source:a',open:true}},'sources'),c=h.context,r=c.AutoSourcarrMatchingReview;
 c.AutoSourcarrNavigation.initialize();await r.activateRules();const originalApi=c.api,oldSave=deferred(),newSave=deferred();let writes=0;
 c.api=(p,o)=>{if(o?.method==='PUT'){h.calls.push([p,o]);return ++writes===1?oldSave.promise:newSave.promise;}return originalApi(p,o);};
 const oldForm=h.nodes.get('matchingPolicyForms').children[0];oldForm.events.submit({preventDefault(){}});await tick();
 c.AutoSourcarrNavigation.identityChanged('local:Bob');assert(!h.nodes.get('matchingPolicyTarget').disabled);
 h.nodes.get('matchingPoliciesPanel').open=true;h.nodes.get('matchingPoliciesPanel').events.toggle();await tick();
 const freshForm=h.nodes.get('matchingPolicyForms').children[0];assert(freshForm,'New owner reloads rules without waiting for old save');
 freshForm.events.submit({preventDefault(){}});await tick();assert(h.nodes.get('matchingPolicyTarget').disabled);
 oldSave.resolve({...structuredClone(h.config),revision:'obsolete-owner'});await tick();
 assert(h.nodes.get('matchingPolicyTarget').disabled,'Old finally cannot re-enable a newly saving form');
 newSave.resolve({...structuredClone(h.config),revision:'current-owner'});await tick();
 assert(!h.nodes.get('matchingPolicyTarget').disabled);assert.equal(freshForm.status.textContent,'Saved. Existing entries unchanged.');
 const third=deferred();c.api=(p,o)=>o?.method==='PUT'?(h.calls.push([p,o]),third.promise):originalApi(p,o);
 freshForm.events.submit({preventDefault(){}});await tick();assert.equal(JSON.parse(h.calls.at(-1)[1].body).revision,'current-owner');
 third.resolve({...structuredClone(h.config),revision:'next'});await tick();
});
test('entry deep-link leaves history and fetches candidate view exactly once',async()=>{
 const h=harness({version:1,tab:'duplicates',matching:{view:'history'}}),c=h.context;c.AutoSourcarrNavigation.initialize();
 c.switchTab=(tab,options)=>{h.calls.push(['switchTab',tab,options]);h.activeTab.id=tab;return Promise.resolve();};
 const line=app.split('\n').find(line=>line.startsWith('window.goDuplicates=async'));assert(line);vm.runInContext(line,c);await c.goDuplicates(99);
 assert(!c.AutoSourcarrMatchingReview.historyActive);assert.equal(h.calls.filter(x=>x[0]==='loadDuplicates').length,1);assert.deepEqual(h.calls.find(x=>x[0]==='loadDuplicates')[1],[99]);assert.equal(h.calls.find(x=>x[0]==='switchTab')[2].load,false);
});
(async()=>{let count=0;for(const t of tests){await t.fn();console.log('PASS '+t.name);count++;}console.log(JSON.stringify({tests:count,failed:0,projectRoot}));})().catch(e=>{console.error(e);process.exitCode=1;});
