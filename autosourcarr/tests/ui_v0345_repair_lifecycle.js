/* Shipped lifecycle/input handlers with deterministic DOM and HTTP seams. */
const fs=require('fs'),path=require('path'),vm=require('vm'),assert=require('assert/strict');
const root=path.join(__dirname,'../app/static'),tick=()=>new Promise(resolve=>setImmediate(resolve));
const esc=value=>String(value).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
class Element{
  constructor(){this.listeners={};this.dataset={};this.value='';this.textContent='';this.files=[];this.children=[];this.classList={contains:()=>true,toggle(){}};}
  addEventListener(type,fn){this.listeners[type]=fn;}scrollIntoView(){}setAttribute(){}
  replaceChildren(){this.children=[];this.value='';}append(node){this.children.push(node);if(!this.value)this.value=node.value;}
}
function harness(names){
  const elements=Object.fromEntries(names.map(id=>[id,new Element()])),calls=[],pending=[],toasts=[];
  const context={document:{hidden:false,getElementById:id=>elements[id],querySelectorAll:()=>[],createElement:()=>new Element(),addEventListener(){}},
    AbortController,console,esc,toast:value=>toasts.push(value),confirm:()=>true,prompt:()=> 'Search still fails',
    MutationObserver:class{observe(){}},switchTab:async()=>{},loadTasks:async()=>{},
    api:(url,options={})=>{calls.push({url,options});return new Promise((resolve,reject)=>pending.push({resolve,reject}));}};
  context.window=context;vm.createContext(context);return {context,elements,calls,pending,toasts};
}
(async()=>{
  const a=harness(['tasks','tasksGeneralPanel','tasksRepairsPanel','sourceRepairDetails','sourceRepairEvidence','taskRepairStatus']);
  vm.runInContext(fs.readFileSync(path.join(root,'repair-tasks.js'),'utf8'),a.context);
  const id='a'.repeat(32),hash='b'.repeat(64),task={repair_id:id,repair_state:'installed',repair_is_current:true,repair_can_revert:true,
    repair_history_revision:7,installed_sha256:hash,context:'test.source.<unsafe>',kind:'source-repair'};
  const actions=a.context.AutoSourcarrRepairTasks.actions(task);
  for(const text of ['Fixed','Doesn’t work / Retry','Revert','Download source code'])assert(actions.includes(text));
  assert(actions.includes('&lt;unsafe&gt;')&&!actions.includes('test.source.<unsafe>'));
  assert(a.context.AutoSourcarrRepairTasks.actions({kind:'source-raw-input',raw_package:'test.source.input'}).includes('Provide code / Unavailable'));
  assert(!a.context.AutoSourcarrRepairTasks.actions({...task,repair_state:'confirmed'}).includes('>Fixed<'));
  const button={disabled:false,dataset:{repairId:id,repairPackage:'test.source.fixture',repairAction:'fixed',repairRevision:'7',repairHash:hash}};
  a.elements.tasks.listeners.click({target:{closest:()=>button}});await tick();
  assert(a.calls[0].url.endsWith('/feedback'));assert.equal(JSON.parse(a.calls[0].options.body).revision,7);
  assert.equal(JSON.parse(a.calls[0].options.body).installed_sha256,hash);
  a.pending[0].resolve({});await tick();
  button.dataset.repairAction='retry';a.elements.tasks.listeners.click({target:{closest:()=>button}});await tick();
  assert.equal(JSON.parse(a.calls[1].options.body).feedback,'Search still fails');
  a.pending[1].resolve({});await tick();assert.equal(a.calls.length,2,'No automatic retry loop');
  button.dataset.repairAction='revert';button.dataset.repairHash='stale';a.elements.tasks.listeners.click({target:{closest:()=>button}});await tick();
  assert.equal(a.calls.length,2);assert(a.toasts.at(-1).includes('Refresh'));

  const ids=['sourceRawInput','sourceRawStatus','sourceRawModule','sourceRawProject','sourceRawRepository','sourceRawRevision',
    'provideSourceRawProject','fetchSourceRawProject','sourceRawsUnavailable','replaceSourceRawProject','downloadSourceRawProject','openSelectedSourceRaw','sourceRepairPackage','openSourceRawArchive','sourceRawInstalledPackage','discoverSourceRawProject','sourceRawKnownProject','refreshSourceRawProject'];
  const b=harness(ids);let opened=null;b.context.openSourceRepairTask=async value=>{opened=value;};
  vm.runInContext(fs.readFileSync(path.join(root,'source-raws.js'),'utf8'),b.context);
  const row=(pkg,revision=3,state='needs-input')=>({package:pkg,revision,version:'1.4.23',state,message:'Original code required.',origin:{module:'src/en/fixture',ecosystem:'tachiyomi'}});
  const first=b.context.openSourceRawInput('test.source.old');await tick();
  const second=b.context.openSourceRawInput('test.source.new');await tick();
  assert(b.calls[0].options.signal.aborted);
  b.pending[1].resolve(row('test.source.new'));await second;
  b.pending[0].resolve(row('test.source.old'));await first;
  assert(b.elements.sourceRawStatus.textContent.startsWith('test.source.new'));
  const file={name:'original-project.zip',size:1000};b.elements.sourceRawProject.files=[file];
  const submit=b.elements.provideSourceRawProject.listeners.click();await tick();
  assert.equal(b.calls[2].options.headers['X-Raw-Revision'],'3');assert.equal(b.calls[2].options.headers['X-Source-Version'],'1.4.23');
  assert.equal(b.calls[2].options.body,file);
  b.elements.provideSourceRawProject.listeners.click();await tick();assert.equal(b.calls.length,3,'Duplicate upload is suppressed');
  b.pending[2].resolve({...row('test.source.new',4,'available'),visibility:'private'});await submit;
  assert(b.elements.provideSourceRawProject.disabled&&!b.elements.downloadSourceRawProject.hidden);
  const unavailable=b.elements.sourceRawsUnavailable.listeners.click();await tick();
  assert(b.calls[3].url.endsWith('/unavailable'));assert.equal(JSON.parse(b.calls[3].options.body).revision,4);
  b.pending[3].resolve({id,state:'queued',message:'Explicit reconstruction requested'});await unavailable;
  assert.equal(opened,id);
  const inventory=b.elements.openSourceRawArchive.listeners.click();await tick();
  assert(b.calls[4].url.endsWith('/installed'),'Archive browsing uses the installed inventory, without an AI account request');
  b.pending[4].resolve({items:[{pkgName:'test.source.archive',name:'Archive source',versionName:'1.4.23'}]});await tick();
  assert(b.calls[5].url.endsWith('/raws/test.source.archive'));
  b.pending[5].resolve({...row('test.source.archive',5,'available'),visibility:'shared'});await inventory;
  assert(b.elements.sourceRawStatus.textContent.startsWith('test.source.archive'));
  console.log('Raw-first UI: generation/hash-bound lifecycle actions, code input, private upload, explicit Unavailable and duplicate/race guards pass');
})().catch(error=>{console.error(error);process.exitCode=1;});
