const fs=require('fs'),vm=require('vm'),assert=require('assert/strict');
const tick=()=>new Promise(resolve=>setImmediate(resolve));
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
config.presets=[{id:'title',name:'High title similarity',description:'Require 97% titles.',rule:{enabled:true,checks:{title:.97}}},{id:'creators',name:'Titles + creators',description:'Required creators must be available.',rule:{enabled:true,checks:{title:.97,author:.90}}}];
let pendingHistory=null,pendingSave=null;
const context={console,Number,String,Date,JSON,Promise,encodeURIComponent,document:{getElementById:id=>nodes.get(id)},esc:value=>String(value??'').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;'),
 openBrowseEntry:(id,event)=>{assert(event?.isTrusted,'Current app requires a trusted click event');inspected.push(id);},openUnmatchDuplicatesForEntry:id=>splits.push(id),
 api:async(path,options)=>{
  calls.push([path,options]);if(options?.method==='PUT'){const body=JSON.parse(options.body);assert.equal(body.revision,config.revision);if(pendingSave)await pendingSave;config.revision+='x';return structuredClone(config);}
  if(path==='/api/admin/matching-policies')return structuredClone(config);
  if(path.startsWith('/api/admin/duplicates/history?')){if(pendingHistory)return pendingHistory;return {items:[{id:9,left_title:'<img src=x>',right_title:'Second title',merged_at:'2026-10-04T12:00:00Z',state:'Recorded merge',reason:'Saved proof',target_entry_id:3,duplicate_entry_id:4,current_entry_id:5}],total:51};}
  return {before:[{role:'Surviving entry',available:true,entry:{id:3,preferred_title:'Original title',description:'<script>bad()</script>'},titles:['Alias'],records:[]}],evidence:{reason:'Saved evidence',direct_pairs:2}};
 }};context.window=context;vm.createContext(context);vm.runInContext(fs.readFileSync(__dirname+'/../app/static/matching-review.js','utf8'),context);
(async()=>{
 assert.equal(calls.length,0,'No catalogue boot requests');context.openMatchingPolicy('source','a');await tick();
 assert.equal(nodes.get('matchingPolicyTarget').value,'source:a');
 let [internal,external]=nodes.get('matchingPolicyForms').children;assert.equal(internal.elements.mode.value,'inherit');assert(internal.fieldset.disabled);assert(nodes.get('matchingPolicyForms').innerHTML.includes('The group disables this direction'));
 internal.elements.mode.value='custom';internal.elements.mode.events.change();assert(!internal.fieldset.disabled);
 internal.events.submit({preventDefault(){}});await tick();assert(internal.status.textContent.includes('Select at least'));assert.equal(calls.length,1);
 internal.elements.title.checked=true;internal.elements.titlePercent.value='99';external.elements.summaryPercent.value='88';
 internal.events.submit({preventDefault(){}});await tick();const saved=JSON.parse(calls.at(-1)[1].body);assert.equal(saved.rule.checks.title,.99);assert.equal(calls.at(-1)[0],'/api/admin/matching-policies/source/a/internal');assert.equal(external.elements.summaryPercent.value,'88','Other direction draft preserved');
 external.events.submit({preventDefault(){}});await tick();assert.equal(JSON.parse(calls.at(-1)[1].body).rule,null);assert(!nodes.get('matchingPolicyTarget').disabled);
 let finishSave;pendingSave=new Promise(resolve=>finishSave=resolve);
 internal.events.submit({preventDefault(){}});await tick();
 assert(internal.elements.titlePercent.disabled,'Submitted thresholds frozen while saving');assert(internal.elements.mode.disabled);assert(internal.fieldset.disabled);
 assert(!external.elements.mode.disabled,'Other direction remains an editable draft');external.elements.summaryPercent.value='89';
 finishSave();await tick();pendingSave=null;
 assert(!internal.elements.mode.disabled);assert(!internal.fieldset.disabled);assert.equal(external.elements.summaryPercent.value,'89');
 let failSave;pendingSave=new Promise((resolve,reject)=>failSave=reject);external.events.submit({preventDefault(){}});await tick();
 failSave(new Error('Save failed'));await tick();pendingSave=null;
 assert(!external.elements.mode.disabled);assert(external.fieldset.disabled,'Inherited controls remain disabled after failed save');assert.equal(external.status.textContent,'Save failed');
 const beforePreset=calls.length;internal.elements.preset.value='creators';internal.elements.preset.events.change();
 assert.equal(calls.length,beforePreset,'Selecting a preset never sends a save');assert.equal(internal.elements.mode.value,'custom');assert(!internal.fieldset.disabled);
 assert(internal.elements.title.checked&&internal.elements.author.checked&&!internal.elements.summary.checked&&!internal.elements.tags.checked);
 assert.equal(internal.elements.titlePercent.value,97);assert.equal(internal.elements.authorPercent.value,90);assert.equal(external.elements.summaryPercent.value,'89','Other direction draft retained');
 internal.events.submit({preventDefault(){}});await tick();assert.deepEqual(JSON.parse(calls.at(-1)[1].body).rule,{enabled:true,checks:{title:.97,author:.9}});
 internal.elements.titlePercent.value='98';internal.elements.titlePercent.events.input();assert.equal(internal.elements.preset.value,'');assert.equal(internal.status.textContent,'Unsaved draft.');
 external.elements.preset.value='title';external.elements.preset.events.change();assert.equal(external.elements.mode.value,'custom');assert(!external.origin.textContent.includes('group disables'),'Preset override removes obsolete inheritance advice');
 let rejectPreset;pendingSave=new Promise((resolve,reject)=>rejectPreset=reject);external.events.submit({preventDefault(){}});await tick();rejectPreset(Error('Rules changed elsewhere'));await tick();pendingSave=null;
 assert.equal(external.elements.preset.value,'title');assert.equal(external.elements.titlePercent.value,97);assert.equal(external.status.textContent,'Rules changed elsewhere','Failed save preserves editable preset draft');
 nodes.get('duplicateHistoryView').events.click();await tick();assert(nodes.get('duplicateCandidatesPanel').hidden);assert(!nodes.get('duplicateHistoryPanel').hidden);assert(nodes.get('mergeHistoryList').innerHTML.includes('&lt;img'));assert(!nodes.get('mergeHistoryList').innerHTML.includes('<img'));
 nodes.get('mergeHistoryList').querySelectorAll('[data-inspect]')[0].events.click({isTrusted:true});assert.deepEqual(inspected,[5]);
 nodes.get('mergeHistoryList').querySelectorAll('[data-split]')[0].events.click();assert.deepEqual(splits,[5]);
 const detail=nodes.get('mergeHistoryList').querySelectorAll('details')[0];detail.open=true;detail.events.toggle();await tick();assert(detail.target.innerHTML.includes('Saved evidence'));assert(detail.target.innerHTML.includes('&lt;script&gt;'));
 nodes.get('mergeHistoryNext').events.click();await tick();assert(calls.at(-1)[0].includes('offset=50'));
 let resolve;pendingHistory=new Promise(r=>resolve=r);nodes.get('mergeHistorySearch').events.click();await tick();const old=nodes.get('mergeHistoryList').innerHTML;nodes.get('duplicatePossibleView').events.click();resolve({items:[],total:0});await tick();assert.equal(nodes.get('mergeHistoryList').innerHTML,old,'Stale response cannot replace hidden/current view');
 console.log('Matching review UI: lazy load, inheritance, validation, independent drafts, revision saves, history paging, escaped evidence, trusted inspection and stale-response guards passed');
})().catch(e=>{console.error(e);process.exitCode=1;});
