/* Execute shipped Tasks rendering and handlers with deterministic DOM/HTTP seams. */
const fs=require('fs'),path=require('path'),vm=require('vm'),assert=require('assert/strict');
const staticRoot=path.join(__dirname,'../app/static');
const html=fs.readFileSync(path.join(staticRoot,'index.html'),'utf8');
const source=fs.readFileSync(path.join(staticRoot,'repair-tasks.js'),'utf8');
const app=fs.readFileSync(path.join(staticRoot,'app.js'),'utf8');
const taskStart=html.indexOf('<section class="tab tasksTab"'),taskEnd=html.indexOf('<section class="tab settingsTab"');
assert(taskStart>0&&taskEnd>taskStart);
assert(html.slice(taskStart,taskEnd).includes('id="sourceRepairDetails"'));
assert(!html.slice(0,taskStart).includes('id="sourceRepairDetails"'));
assert(!html.includes('id="sourceRepairJobs"'),'Sources must not retain a duplicate job list');
assert(!html.includes('id="taskRepairTools"'),'Tasks subtab replaces the redundant Open Repairs shortcut');
class Element{
  constructor(){this.listeners={};this.textContent='';this.open=false;this.hidden=false;this.disabled=false;}
  addEventListener(type,handler){this.listeners[type]=handler;}
  scrollIntoView(){this.scrolled=true;}
}
const elements=Object.fromEntries(['tasks','tasksGeneralPanel','tasksRepairsPanel','sourceRepairDetails','sourceRepairEvidence','taskRepairStatus','repairSetupPanel'].map(id=>[id,new Element()]));
let active=true,repairActive=false,observer,refreshes=0;
elements.tasks.classList={contains:()=>active};
elements.tasksRepairsPanel.classList={contains:()=>repairActive,toggle:(_name,value)=>{repairActive=value;}};
elements.tasksGeneralPanel.classList={toggle:()=>{}};
const document={hidden:false,listeners:{},getElementById:id=>elements[id],addEventListener(type,fn){this.listeners[type]=fn;}};
const calls=[],pending=[],toasts=[];
const context={document,AbortController,console,confirm:()=>true,toast:value=>toasts.push(value),
  switchTab:async(tab)=>{active=tab==='tasks';},setSourceSubtab:()=>{},loadTasks:async()=>{refreshes++;},
  api:(url,options={})=>{calls.push({url,options});return new Promise((resolve,reject)=>pending.push({resolve,reject}));},
  MutationObserver:class{constructor(fn){observer=fn;}observe(){}}};
context.window=context;
vm.createContext(context);vm.runInContext(source,context);
const esc=value=>String(value).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
context.esc=esc;context.jsq=value=>String(value);context.taskProgressText=()=>'';context.taskTimingText=()=>'';
context.taskUpdateText=()=>'';context.taskFinishedText=()=>'';
const renderStart=app.indexOf('function taskCardHtml(t){'),renderEnd=app.indexOf('\nfunction renderTaskSection',renderStart);
vm.runInContext(app.slice(renderStart,renderEnd),context);
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const a='a'.repeat(32),b='b'.repeat(32);
(async()=>{
  const ready={kind:'source-repair',repair_id:a,repair_state:'ready',state:'completed',repair_version:'0.3.40',origin:'manual',context:'eu.kanade.'+'x'.repeat(180),message:'<script>private evidence</script>',cancellable:false};
  const card=context.taskCardHtml(ready);
  assert(card.includes('Install validated repair')&&card.includes('Discard validated repair'));
  assert(card.includes('Evidence & session')&&card.includes('repairTaskCard'));
  assert(card.includes('&lt;script&gt;')&&!card.includes('<script>'));
  assert(context.taskCardHtml({...ready,stale:true}).includes('status unavailable'));
  assert(!context.AutoSourcarrRepairTasks.actions({...ready,stale:true}).includes('Install'));
  assert(context.AutoSourcarrRepairTasks.actions({...ready,repair_state:'failed',repair_reviewable:true}).includes('Discard validated repair'));
  assert(!context.AutoSourcarrRepairTasks.actions({...ready,repair_state:'installing',repair_reviewable:false}).includes('Install'));
  assert(context.AutoSourcarrRepairTasks.metadata({...ready,repair_version:'unknown'}).includes('version not recorded'));
  assert(context.AutoSourcarrRepairTasks.actions({...ready,repair_state:'validating',cancellable:true}).includes('Cancel repair'));
  const first=context.openSourceRepairTask(a);await tick();
  assert(elements.sourceRepairDetails.scrolled,'Opening evidence should bring its panel into view');
  const second=context.openSourceRepairTask(b);await tick();
  assert(calls[0].options.signal.aborted,'New evidence selection must cancel the owned older GET');
  pending[1].resolve({id:b,message:'New job evidence'});await second;
  pending[0].resolve({id:a,message:'Delayed old evidence'});await first;
  assert(elements.sourceRepairEvidence.textContent.includes('New job evidence'));
  assert(!elements.sourceRepairEvidence.textContent.includes('Delayed old evidence'));
  const leaving=context.openSourceRepairTask(a);await tick();active=false;observer();
  pending[2].resolve({id:a,message:'Hidden update'});await leaving;
  assert(!elements.sourceRepairEvidence.textContent.includes('Hidden update'));
  let completeNavigation;
  context.switchTab=()=>new Promise(resolve=>{completeNavigation=resolve;});
  const navigating=context.openSourceRepairTask(a);await tick();active=false;observer();
  completeNavigation();await navigating;
  assert.equal(calls.length,3,'Leaving during tab loading must not open late evidence');
  context.switchTab=async tab=>{active=tab==='tasks';};
  active=true;
  const button={disabled:false,dataset:{repairId:a,repairAction:'cancel'}};
  elements.tasks.listeners.click({target:{closest:()=>button}});await tick();
  assert(button.disabled&&calls[3].url.endsWith('/cancel'));
  elements.tasks.listeners.click({target:{closest:()=>button}});await tick();assert.equal(calls.length,4);
  pending[3].resolve({requested:true});await tick();
  assert(!button.disabled&&refreshes===1&&toasts[0].includes('requested'));
  assert(!toasts[0].includes('cancelled'),'Cancellation must await worker cleanup');
  const prompts=[];let allowDiscard=false;
  context.confirm=message=>{prompts.push(message);return allowDiscard;};
  button.dataset={repairId:a,repairAction:'discard',repairPackage:'Source fixture'};
  elements.tasks.listeners.click({target:{closest:()=>button}});await tick();
  assert.equal(calls.length,4);assert(!button.disabled);
  assert(prompts[0].includes('Source fixture')&&prompts[0].includes('evidence are retained'));
  allowDiscard=true;elements.tasks.listeners.click({target:{closest:()=>button}});await tick();
  assert(calls[4].url.endsWith('/discard'));
  pending[4].resolve({state:'discarded'});await tick();
  assert.equal(refreshes,2);assert(!button.disabled);
  const discardedActions=context.AutoSourcarrRepairTasks.actions({...ready,repair_state:'discarded'});
  assert(discardedActions.includes('Evidence & session')&&!discardedActions.includes('Discard'));
  context.AutoSourcarrRepairTasks.renderStatus({source_repairs:{stale:false,isolation:{available:false,reason:'namespace-denied',message:'Host denied namespaces'}}});
  assert.equal(elements.taskRepairStatus.textContent,'Host denied namespaces');
  console.log('Repair Tasks: real row rendering, review/cancel actions, escaping, old records, stale status and evidence races pass');
})().catch(error=>{console.error(error);process.exitCode=1;});
