/* Shipped health controller: retained results, mutations, visibility and escaping. */
const fs=require('fs'),path=require('path'),vm=require('vm'),assert=require('assert/strict');
class Element{
  constructor(){this.listeners={};this.textContent='';this.innerHTML='';this.hidden=false;this.open=false;this.disabled=false;this.checked=false;this.value='all';this.dataset={};}
  addEventListener(name,fn){this.listeners[name]=fn;}
  querySelector(){return this.body||(this.body=new Element());}
  scrollIntoView(){this.scrolls=(this.scrolls||0)+1;}
}
const names=['sourceHealthPanel','sources','sourceHealthStatus','sourceHealthDaily','checkEnabledSourceHealth','refreshSourceHealth','sourceHealthTable','sourceHealthFilter','sourceHealthDetails','sourceHealthEvidence','sourceHealthTasks'];
const elements=Object.fromEntries(names.map(name=>[name,new Element()]));let active=false,observer;
for(const name of ['sources','sourceHealthPanel'])elements[name].classList={contains:()=>active};
const calls=[],pending=[],timers=[];
const context={console,AbortController,Date,Set,document:{hidden:false,getElementById:id=>elements[id],addEventListener(){}},
  MutationObserver:class{constructor(fn){observer=fn;}observe(){}},
  esc:value=>String(value??'').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;'),
  api:(url,options={})=>{calls.push({url,options});return new Promise((resolve,reject)=>pending.push({resolve,reject}));},
  setTimeout:(fn,ms)=>{timers.push({fn,ms});return timers.length;},clearTimeout:()=>{},toast:()=>{},loadTasks:async()=>{},switchTab:()=>{}};
context.window=context;vm.createContext(context);
vm.runInContext(fs.readFileSync(path.join(__dirname,'../app/static/source-health.js'),'utf8'),context);
const snapshot={daily:{enabled:true,next_due_at:0},active:{},checking:[],items:[
 {source_id:'1',name:'<img src=x>',lang:'en',available:1,state:'unchecked',checks:[],history:[{at:100,state:'working',origin:'manual',message:'Retained sample'}]},
 {source_id:'2',name:'Removed working source',available:0,state:'working'},
 {source_id:'3',name:'Removed failed source',available:'0',state:'failed'},
 {source_id:'4',name:'Installed disabled language',available:true,enabled_language:0,state:'unchecked'},
 {source_id:'5',name:'Installed working source',available:1,state:'working'}]};
const tick=()=>new Promise(resolve=>setImmediate(resolve));
(async()=>{
 active=true;const first=context.loadSourceHealth();await tick();
 pending[0].resolve(snapshot);await first;
 const body=elements.sourceHealthTable.querySelector('tbody');assert(body.innerHTML.includes('&lt;img'));
 assert(!body.innerHTML.includes('<img src=x>'));assert(body.innerHTML.includes('Never checked')&&body.innerHTML.includes('Not recorded'));
 for(const filter of ['all','attention','working']){
  elements.sourceHealthFilter.value=filter;elements.sourceHealthFilter.listeners.change();
  assert(!body.innerHTML.includes('Removed working source')&&!body.innerHTML.includes('Removed failed source'),filter+' must hide uninstalled sources');
  assert.equal(body.innerHTML.includes('Installed disabled language'),filter!=='working');
  assert.equal(body.innerHTML.includes('Installed working source'),filter!=='attention');
 }
 elements.sourceHealthFilter.value='all';elements.sourceHealthFilter.listeners.change();
 let button={disabled:false,dataset:{healthCheck:'1'}};
 elements.sourceHealthPanel.listeners.click({target:{closest:()=>button}});await tick();
 assert.equal(calls[1].options.method,'POST');assert.deepEqual(JSON.parse(calls[1].options.body),{source_ids:['1']});
 elements.sourceHealthPanel.listeners.click({target:{closest:()=>button}});await tick();assert.equal(calls.length,2,'Duplicate mutations must not issue another POST');
 pending[1].reject(new Error('Controlled acknowledgement loss'));await tick();
 assert.equal(calls.filter(call=>call.options.method==='POST').length,1,'No automatic replay');
 pending[2].resolve(snapshot);await tick();assert(elements.sourceHealthStatus.textContent.includes('acknowledgement loss'));
 assert(!elements.sourceHealthDaily.disabled);
 button={disabled:false,dataset:{healthEvidence:'1'}};
 elements.sourceHealthPanel.listeners.click({target:{closest:()=>button}});
 assert.equal(elements.sourceHealthDetails.scrolls,1);
 elements.sourceHealthDetails.open=false;
 const refresh=context.loadSourceHealth();await tick();pending[3].resolve({...snapshot,items:snapshot.items.map(row=>row.source_id==='1'?{...row,available:0}:row)});await refresh;
 assert.equal(elements.sourceHealthDetails.scrolls,1,'Polling must not scroll the user');
 assert(elements.sourceHealthDetails.hidden&&!elements.sourceHealthDetails.open,'Removing a source must hide its previously selected evidence');
 assert.equal(elements.sourceHealthEvidence.innerHTML,'');
 const reinstall=context.loadSourceHealth();await tick();pending[4].resolve(snapshot);await reinstall;
 assert(elements.sourceHealthDetails.hidden,'Reinstall must not reopen evidence without selection');
 elements.sourceHealthPanel.listeners.click({target:{closest:()=>button}});
 assert(elements.sourceHealthEvidence.innerHTML.includes('Retained sample'),'Reinstalled source keeps recorded history');
 const hidden=context.loadSourceHealth();await tick();active=false;observer();
 pending[5].resolve({...snapshot,items:[{...snapshot.items[0],name:'Late hidden update'}]});await hidden;
 assert(!body.innerHTML.includes('Late hidden update'));
 console.log('Source health UI: unknown dates, escaping, single mutations, reconciliation, evidence and visibility races pass');
})().catch(error=>{console.error(error);process.exitCode=1;});
