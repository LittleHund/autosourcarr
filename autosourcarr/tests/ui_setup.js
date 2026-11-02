const fs=require('fs'),vm=require('vm'),assert=require('assert/strict'),crypto=require('crypto').webcrypto;
const source=fs.readFileSync(__dirname+'/../app/static/setup.js','utf8');
const tick=()=>new Promise(r=>setImmediate(r));
// `local` is the browser's localStorage; pass the same Map to a second harness to model a page reload.
function harness(completed=true,local=new Map()){
 const nodes=new Map(),calls=[],storage=new Map(),timers=[];
 const decode=s=>s.replaceAll('&quot;','"').replaceAll('&#39;',"'").replaceAll('&amp;','&');
 class Node{
  constructor(id){this.id=id;this.hidden=id==='setupModal';this.disabled=false;this.dataset={};this.value='';this.children=[];this.textContent='';this.events={};this.parentElement=null;}
  set innerHTML(s){this.html=s;this.children=[];for(const match of s.matchAll(/<(input|textarea|button|p|div|select)\b([^>]+)>/g)){const attrs=match[2],id=/id="([^"]+)"/.exec(attrs)?.[1];if(!id)continue;const node=new Node(id);node.tagName=match[1].toUpperCase();node.value=decode(/value="([^"]*)"/.exec(attrs)?.[1]||'');node.dataset.setupKey=/data-setup-key="([^"]*)"/.exec(attrs)?.[1];if(node.tagName==='SELECT'){const inner=s.slice(match.index+match[0].length).split('</select>')[0];node.value=decode(/<option[^>]*value="([^"]*)"[^>]*selected/.exec(inner)?.[1]||/<option[^>]*value="([^"]*)"/.exec(inner)?.[1]||'');}node.checked=/\bchecked\b/.test(attrs);node.parentElement=this;this.children.push(node);nodes.set(id,node);}}
  get innerHTML(){return this.html||'';}
  querySelectorAll(selector){if(selector==='[data-setup-key]')return this.children.filter(n=>n.dataset.setupKey);if(selector==='input')return this.children.filter(n=>n.tagName==='INPUT');return this.children;}
  addEventListener(name,fn){this.events[name]=fn;}
  focus(){document.activeElement=this;}
  checkValidity(){return true;}reportValidity(){}getClientRects(){return [1];}after(){}
  append(...items){this.children.push(...items);for(const item of items)item.parentElement=this;}
  replaceChildren(...items){this.children=[];this.append(...items);}
 }
 for(const id of ['setupModal','setupTitle','setupProgress','setupBody','setupError','setupCancel','setupBack','setupSkip','setupNext','setupReload','runSetup'])nodes.set(id,new Node(id));
 const document={hidden:false,events:{},addEventListener(type,fn){this.events[type]=fn;},querySelector:s=>nodes.get(s.slice(1))||null,activeElement:null,createElement:()=>new Node('')};
 let data={completed,revision:'v1',integrated:true,cleanup:{status:'completed',checked:12,removed:[]},fields:[
  {key:'MAIN_SUWAYOMI_URL',kind:'url',step:'library',label:'Library',description:'Address',value:'http://main:30058'},
  {key:'AUTOSOURCARR_READER_API_TOKEN',kind:'secret',generate:true,step:'readers',label:'Key',description:'Keep or replace',value:'',configured:true},
  {key:'BROWSE_PAGE_SIZE',kind:'integer',step:'performance',label:'Page size',description:'Size',value:50,minimum:1,maximum:500}]};
 const context={document,crypto,Uint8Array,console,JSON,Promise,URL,clearTimeout(){},setTimeout:fn=>timers.push(fn),confirm:()=>true,sessionStorage:{getItem:k=>storage.get(k),setItem:(k,v)=>storage.set(k,v),removeItem:k=>storage.delete(k)},localStorage:{getItem:k=>local.has(k)?local.get(k):null,setItem:(k,v)=>local.set(k,String(v)),removeItem:k=>local.delete(k)},switchTab:tab=>calls.push(['tab',tab]),syncModalScrollLock(){},toast:s=>calls.push(['toast',s]),settingsState:{dirty:false,saving:false,editRevision:0},api:async(path,opts)=>{calls.push([path,opts]);if(path.endsWith('/provider'))return {provider:'chatgpt'};if(path.endsWith('/control'))return {busy:false};if(opts.method==='POST'){data.completed=true;return {completed:true,restart_required:false};}return structuredClone(data);}};
 context.window=context;vm.createContext(context);vm.runInContext(fs.readFileSync(__dirname+'/../app/static/generated/api-client.js','utf8'),context);vm.runInContext(fs.readFileSync(__dirname+'/../app/static/repair-login.js','utf8'),context);vm.runInContext(source,context);
 const h={context,nodes,calls,storage,local,timers,get data(){return data;},set data(v){data=v;},next:async()=>{nodes.get('setupNext').onclick();await tick();},
  // Continue until the step with this title (step positions are not part of the contract).
  toStep:async title=>{for(let i=0;i<12;i++){if(nodes.get('setupTitle').textContent===title)return;await h.next();}throw new Error('No setup step titled '+title);}};
 return h;
}
const draftOf=h=>JSON.parse(h.local.get('autosourcarr.setup.draft')||'null');
if(require.main===module)(async()=>{
 // SET-1: required steps come first, have no Skip and cannot be bypassed; optional steps follow and can be skipped.
 let h=harness(false);const boot=h.context.AutoSourcarrSetup.initialize();await tick();
 const order=[];for(let i=0;i<11;i++){order.push([h.nodes.get('setupTitle').textContent,h.nodes.get('setupProgress').textContent.split(' · ')[0],h.nodes.get('setupSkip').hidden]);if(i<10)await h.next();}
 assert.deepEqual(order.map(o=>o[0]),['Your AutoSourcarr','Connect your library','Sources and access','Reader extensions','Access and identity','Matching and discovery','Speed and capacity','Translation and tools','Repair and AI','Your other apps','Review and finish']);
 assert.deepEqual(order.map(o=>o[1]),[...Array(5).fill('Required'),...Array(5).fill('Optional'),'Review']);
 assert.deepEqual(order.map(o=>o[2]),[true,true,true,true,true,false,false,false,false,false,true]);
 h.context.AutoSourcarrSetup.cancel();assert(!h.nodes.get('setupModal').hidden,'first-run setup cannot be cancelled');
 await h.next();assert.equal(h.nodes.get('setupTitle').textContent,'Setup saved');await h.next();await boot;assert(h.nodes.get('setupModal').hidden);
 // Before the first Finish a shortcut into a later step starts at the beginning instead.
 h=harness(false);await h.context.AutoSourcarrSetup.openRepairAI();assert.equal(h.nodes.get('setupTitle').textContent,'Your AutoSourcarr');
 h.context.AutoSourcarrSetup.cancel();
 h=harness(true);await h.context.AutoSourcarrSetup.openRepairAI();assert.equal(h.nodes.get('setupTitle').textContent,'Repair and AI');
 h.context.AutoSourcarrSetup.cancel();

 // Skip keeps the step's saved values; Go to review keeps edits made so far and jumps.
 h=harness();await h.nodes.get('runSetup').onclick();await tick();assert(!h.nodes.get('setupModal').hidden,'Run setup again opens the wizard');
 await h.toStep('Speed and capacity');assert(h.nodes.get('setupOptionalNotice'));
 h.nodes.get('setup-BROWSE_PAGE_SIZE').value='99';h.nodes.get('setup-BROWSE_PAGE_SIZE').events.input();
 h.nodes.get('setupSkip').onclick();assert.equal(h.nodes.get('setupTitle').textContent,'Translation and tools');
 h.nodes.get('setupBack').onclick();assert.equal(h.nodes.get('setup-BROWSE_PAGE_SIZE').value,'50');
 h.nodes.get('setup-BROWSE_PAGE_SIZE').value='60';h.nodes.get('setupJumpReview').events.click();
 assert.equal(h.nodes.get('setupTitle').textContent,'Review and finish');assert.match(h.nodes.get('setupBody').innerHTML,/1 settings changed/);
 await h.next();let post=h.calls.find(c=>c[0].endsWith('/finish'));assert.deepEqual(JSON.parse(post[1].body).changes,{BROWSE_PAGE_SIZE:60});

 // SET-2: Cancel keeps the draft on this device, without secrets, and a rerun (or a reload) continues from it.
 h=harness();await h.context.AutoSourcarrSetup.open();await h.next();h.nodes.get('setup-MAIN_SUWAYOMI_URL').value='http://changed:30058';await h.next();
 await h.toStep('Reader extensions');h.nodes.get('setupGenerateKey').events.click();const secret=h.nodes.get('setup-AUTOSOURCARR_READER_API_TOKEN').value;assert.match(secret,/^[a-f0-9]{64}$/);
 h.context.AutoSourcarrSetup.cancel();await tick();
 assert(h.nodes.get('setupModal').hidden);assert(!h.calls.some(c=>c[1]?.method==='POST'),'Cancel never writes settings');
 const kept=draftOf(h);assert.deepEqual(kept.values,{MAIN_SUWAYOMI_URL:{base:'http://main:30058',value:'http://changed:30058'}});assert.equal(kept.step,'readers');
 assert(!JSON.stringify([...h.local,...h.storage]).includes(secret),'a generated key is never kept in a draft');
 const reloaded=harness(true,h.local);await reloaded.context.AutoSourcarrSetup.open();
 assert.equal(reloaded.nodes.get('setupTitle').textContent,'Reader extensions');assert.match(reloaded.nodes.get('setupDraftNotice').innerHTML||reloaded.nodes.get('setupBody').innerHTML,/draft restored/);
 assert.equal(reloaded.nodes.get('setup-AUTOSOURCARR_READER_API_TOKEN').value,'');
 await reloaded.toStep('Review and finish');assert.match(reloaded.nodes.get('setupBody').innerHTML,/Library<\/strong> <small class="muted">required<\/small>: http:\/\/changed:30058/);
 await reloaded.next();post=reloaded.calls.find(c=>c[0].endsWith('/finish'));
 assert.deepEqual(JSON.parse(post[1].body).changes,{MAIN_SUWAYOMI_URL:'http://changed:30058'},'Finish writes only the changed value');
 assert.equal(reloaded.local.size,0,'Finish clears the kept draft');assert.equal(reloaded.nodes.get('setupTitle').textContent,'Setup saved');
 await reloaded.next();assert(reloaded.nodes.get('setupModal').hidden);

 // A kept value whose saved setting changed meanwhile is dropped, not forced over the new value.
 h=harness();await h.context.AutoSourcarrSetup.open();await h.next();h.nodes.get('setup-MAIN_SUWAYOMI_URL').value='http://draft:30058';h.nodes.get('setup-MAIN_SUWAYOMI_URL').events.input();h.context.AutoSourcarrSetup.cancel();await tick();
 let stale=harness(true,h.local);stale.data.fields[0].value='http://elsewhere:30058';await stale.context.AutoSourcarrSetup.open();
 assert.match(stale.nodes.get('setupBody').innerHTML,/out of date/);assert.equal(stale.local.size,0);
 await stale.next();assert.equal(stale.nodes.get('setup-MAIN_SUWAYOMI_URL').value,'http://elsewhere:30058');
 // Discard draft returns to the saved values and forgets the draft; an unedited cancel keeps nothing.
 h=harness();await h.context.AutoSourcarrSetup.open();await h.next();h.nodes.get('setup-MAIN_SUWAYOMI_URL').value='http://draft:30058';h.nodes.get('setup-MAIN_SUWAYOMI_URL').events.input();h.context.AutoSourcarrSetup.cancel();await tick();
 const again=harness(true,h.local);await again.context.AutoSourcarrSetup.open();assert.equal(again.nodes.get('setup-MAIN_SUWAYOMI_URL').value,'http://draft:30058');
 again.nodes.get('setupDiscardDraft').events.click();assert.equal(again.nodes.get('setup-MAIN_SUWAYOMI_URL').value,'http://main:30058');assert.equal(again.local.size,0);
 again.context.AutoSourcarrSetup.cancel();await tick();assert.equal(again.local.size,0);
 // A removed saved credential is kept as an intent only; the removal still needs Finish.
 h=harness();await h.context.AutoSourcarrSetup.openReaders();h.nodes.get('setupClear-AUTOSOURCARR_READER_API_TOKEN').events.click();h.context.AutoSourcarrSetup.cancel();await tick();
 assert.deepEqual(draftOf(h).removed,['AUTOSOURCARR_READER_API_TOKEN']);
 const removal=harness(true,h.local);await removal.context.AutoSourcarrSetup.openReaders();await removal.toStep('Review and finish');assert.match(removal.nodes.get('setupBody').innerHTML,/saved credential will be removed/);

 // Finish sends changed keys only; a generated secret stays in memory.
 h=harness();await h.context.AutoSourcarrSetup.open();await h.toStep('Reader extensions');
 h.nodes.get('setupGenerateKey').events.click();const generated=h.nodes.get('setup-AUTOSOURCARR_READER_API_TOKEN').value;
 await h.toStep('Speed and capacity');h.nodes.get('setup-BROWSE_PAGE_SIZE').value='77';await h.toStep('Review and finish');await h.next();
 post=h.calls.find(c=>c[0].endsWith('/finish'));assert(post);assert.equal(post[1].timeoutMs,15000);const body=JSON.parse(post[1].body);
 assert.deepEqual(Object.keys(body.changes).sort(),['AUTOSOURCARR_READER_API_TOKEN','BROWSE_PAGE_SIZE']);assert.equal(body.changes.BROWSE_PAGE_SIZE,77);assert.equal(body.changes.AUTOSOURCARR_READER_API_TOKEN,generated);
 assert(!JSON.stringify([...h.storage,...h.local]).includes(generated));assert.equal(h.nodes.get('setupTitle').textContent,'Setup saved');await h.next();assert(h.nodes.get('setupModal').hidden);
 // UI-5: Escape and a click outside close a rerun like Cancel (draft kept); first-run setup ignores both.
 h=harness();await h.context.AutoSourcarrSetup.open();await h.next();h.nodes.get('setup-MAIN_SUWAYOMI_URL').value='http://esc:30058';h.nodes.get('setup-MAIN_SUWAYOMI_URL').events.input();
 const modal=h.nodes.get('setupModal');modal.events.keydown({key:'Escape',defaultPrevented:false,target:h.nodes.get('setupBody'),preventDefault(){}});await tick();
 assert(modal.hidden);assert.equal(draftOf(h).values.MAIN_SUWAYOMI_URL.value,'http://esc:30058');
 await h.context.AutoSourcarrSetup.open();assert(!modal.hidden);modal.events.click({target:h.nodes.get('setupBody')});assert(!modal.hidden,'a click inside keeps it open');
 modal.events.click({target:modal});await tick();assert(modal.hidden);
 h=harness(false);h.context.AutoSourcarrSetup.initialize();await tick();h.nodes.get('setupModal').events.keydown({key:'Escape',target:h.nodes.get('setupBody'),preventDefault(){}});h.nodes.get('setupModal').events.click({target:h.nodes.get('setupModal')});await tick();assert(!h.nodes.get('setupModal').hidden);
 // A completed-install refresh never waits for the setup endpoint.
 h=harness();h.storage.set('autosourcarr.setup.complete','1');h.context.api=()=>new Promise(()=>{});assert.equal(h.context.AutoSourcarrSetup.initialize(),undefined);
 // Refuse rerun while ordinary Settings has unsaved edits.
 h=harness();h.context.settingsState.dirty=true;await h.context.AutoSourcarrSetup.open();assert.equal(h.calls.filter(c=>c[0].startsWith('/api/')).length,0);
 // Required setup can recover from a conflict without a cancel escape hatch.
 h=harness(false);const first=h.context.AutoSourcarrSetup.initialize();await tick();await h.toStep('Review and finish');const api=h.context.api;h.context.api=async(path,opts)=>{if(path.endsWith('/finish'))throw new Error('Settings changed elsewhere');return api(path,opts);};await h.next();assert(!h.nodes.get('setupReload').hidden);await h.nodes.get('setupReload').onclick();assert.equal(h.nodes.get('setupTitle').textContent,'Your AutoSourcarr');h.context.api=api;await h.toStep('Review and finish');await h.next();await h.next();await first;
 console.log('Setup UI: required-first order, optional skip and review jump, first-run gate, Escape/outside-click close, draft kept on cancel and reload (no secrets, stale values dropped, discard), changed-only finish, warm refresh, dirty guard and conflict recovery passed');
})().catch(e=>{console.error(e);process.exitCode=1;});

module.exports={harness,tick};
