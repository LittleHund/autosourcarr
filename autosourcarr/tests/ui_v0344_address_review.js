/* Existing repair actions retain single-flight ownership across address review. */
const fs=require('fs'),path=require('path'),vm=require('vm'),assert=require('assert/strict');
let active=true,allow=false,refresh=0;const pending=[],calls=[],prompts=[],downloads=[],toasts=[];
const elements={};for(const id of ['tasks','tasksGeneralPanel','tasksRepairsPanel','sourceRepairDetails','sourceRepairEvidence','taskRepairStatus'])elements[id]={
 addEventListener(type,fn){this[type]=fn;},classList:{contains:()=>active,toggle(){}},open:false,hidden:false,textContent:''};
const context={URL,AbortController,console,document:{hidden:false,getElementById:id=>elements[id],querySelectorAll:()=>[],addEventListener(){}},
 MutationObserver:class{observe(){}},confirm:message=>{prompts.push(message);return allow;},esc:String,
 toast:text=>toasts.push(text),downloadBlob:(blob,name)=>downloads.push(name),loadTasks:async()=>{refresh++;},
 api:(url,options={})=>{calls.push({url,options});return new Promise((resolve,reject)=>pending.push({resolve,reject}));}};
context.window=context;vm.createContext(context);
vm.runInContext(fs.readFileSync(path.join(__dirname,'../app/static/repair-tasks.js'),'utf8'),context);
const id='a'.repeat(32),tick=()=>new Promise(resolve=>setImmediate(resolve));
const data={id,state:'ready',candidate_sha256:'c'.repeat(64),address_migrations:[{previous_url:'https://old.example/',new_url:'https://new.example/'}]};
const button={disabled:false,dataset:{repairId:id,repairAction:'install',repairAddressCount:'1'}};
function click(){elements.tasks.click({target:{closest:()=>button}});}
(async()=>{
 click();await tick();assert(button.disabled);click();assert.equal(calls.length,1);
 pending[0].resolve(data);await tick();assert(!button.disabled&&calls.length===1,'Declined address review must not install');
 assert(prompts[0].includes('https://old.example → https://new.example'));
 allow=true;click();await tick();pending[1].resolve(data);await tick();
 assert(calls[2].url.endsWith('/install'));
 assert.equal(JSON.parse(calls[2].options.body).reviewed_candidate_sha256,data.candidate_sha256);
 pending[2].resolve({queued:true});await tick();assert.equal(refresh,1);
 click();await tick();active=false;pending[3].resolve(data);await tick();assert.equal(calls.length,4,'Leaving before review must not cause a late installation');
 active=true;button.dataset={repairId:id,repairAction:'inputs'};click();await tick();
 assert(calls[4].options.responseType==='blob'&&calls[4].options.cache==='no-store');
 pending[4].resolve({size:1024,type:'application/zip'});await tick();assert.equal(downloads[0],'AutoSourcarr-repair-inputs-'+id+'.zip');
 assert(!button.disabled);
 const promptCount=prompts.length;button.dataset={repairId:id,repairAction:'install',repairAddressCount:'1'};
 click();await tick();context.AutoSourcarrRepairTasks.originChanged();pending[5].resolve(data);await tick();
 assert.equal(calls.length,6);assert.equal(prompts.length,promptCount,'Changing origin filters must fence the pending address review');
 assert(!button.disabled);
 button.dataset={repairId:id,repairAction:'inputs'};click();await tick();pending[6].resolve({size:100,type:'text/html'});await tick();
 assert.equal(downloads.length,1,'An authentication page must not be saved as source inputs');assert(!button.disabled);
 console.log('Address review: candidate binding, decline/navigation/filter fences, single-flight and validated source download pass');
})().catch(error=>{console.error(error);process.exitCode=1;});
