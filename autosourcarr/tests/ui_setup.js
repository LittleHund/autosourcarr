const fs=require('fs'),vm=require('vm'),assert=require('assert/strict'),crypto=require('crypto').webcrypto;
const source=fs.readFileSync(__dirname+'/../app/static/setup.js','utf8');
const tick=()=>new Promise(r=>setImmediate(r));
function harness(completed=true){
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
 for(const id of ['setupModal','setupTitle','setupProgress','setupBody','setupError','setupCancel','setupBack','setupNext','setupReload','runSetup'])nodes.set(id,new Node(id));
 const document={hidden:false,events:{},addEventListener(type,fn){this.events[type]=fn;},querySelector:s=>nodes.get(s.slice(1))||null,activeElement:null,createElement:()=>new Node('')};
 let data={completed,revision:'v1',integrated:true,cleanup:{status:'completed',checked:12,removed:[]},fields:[
  {key:'MAIN_SUWAYOMI_URL',kind:'url',step:'library',label:'Library',description:'Address',value:'http://main:30058'},
  {key:'AUTOSOURCARR_READER_API_TOKEN',kind:'secret',generate:true,step:'readers',label:'Key',description:'Keep or replace',value:'',configured:true},
  {key:'BROWSE_PAGE_SIZE',kind:'integer',step:'performance',label:'Page size',description:'Size',value:50,minimum:1,maximum:500}]};
 const context={document,crypto,Uint8Array,console,JSON,Promise,URL,clearTimeout(){},setTimeout:fn=>timers.push(fn),confirm:()=>true,sessionStorage:{getItem:k=>storage.get(k),setItem:(k,v)=>storage.set(k,v),removeItem:k=>storage.delete(k)},switchTab:tab=>calls.push(['tab',tab]),syncModalScrollLock(){},toast:s=>calls.push(['toast',s]),settingsState:{dirty:false,saving:false,editRevision:0},api:async(path,opts)=>{calls.push([path,opts]);if(path.endsWith('/provider'))return {provider:'chatgpt'};if(path.endsWith('/control'))return {busy:false};if(opts.method==='POST'){data.completed=true;return {completed:true,restart_required:false};}return structuredClone(data);}};
 context.window=context;vm.createContext(context);vm.runInContext(fs.readFileSync(__dirname+'/../app/static/repair-login.js','utf8'),context);vm.runInContext(source,context);
 return {context,nodes,calls,storage,timers,get data(){return data;},set data(v){data=v;},next:async()=>{nodes.get('setupNext').onclick();await tick();}};
}
if(require.main===module)(async()=>{
 // Cancel rerun after edits, without any mutation or persistence of the draft.
 let h=harness();await h.context.AutoSourcarrSetup.open();await h.next();h.nodes.get('setup-MAIN_SUWAYOMI_URL').value='http://changed:30058';await h.next();h.context.AutoSourcarrSetup.cancel();
 assert(h.nodes.get('setupModal').hidden);assert(!h.calls.some(c=>c[1]?.method==='POST'));assert.equal(h.storage.size,0);
 await h.context.AutoSourcarrSetup.open();await h.next();assert.equal(h.nodes.get('setup-MAIN_SUWAYOMI_URL').value,'http://main:30058');
 // Finish sends changed keys only; secret generated in the draft, no storage.
 h.nodes.get('setup-MAIN_SUWAYOMI_URL').value='http://changed:30058';await h.next();await h.next();await h.next();
 h.nodes.get('setupGenerateKey').events.click();const generated=h.nodes.get('setup-AUTOSOURCARR_READER_API_TOKEN').value;assert.match(generated,/^[a-f0-9]{64}$/);
 await h.next();h.nodes.get('setup-BROWSE_PAGE_SIZE').value='77';await h.next();for(let i=0;i<5;i++)await h.next();
 const post=h.calls.find(c=>c[0].endsWith('/finish'));assert(post);assert.equal(post[1].timeoutMs,15000);const body=JSON.parse(post[1].body);assert.equal(body.changes.BROWSE_PAGE_SIZE,77);assert.equal(body.changes.AUTOSOURCARR_READER_API_TOKEN,generated);
 assert(!JSON.stringify([...h.storage]).includes(generated));assert.equal(h.nodes.get('setupTitle').textContent,'Setup saved');await h.next();assert(h.nodes.get('setupModal').hidden);
 // First-run blocks dismissal until Finish, then resolves bootstrap.
 h=harness(false);const boot=h.context.AutoSourcarrSetup.initialize();await tick();h.context.AutoSourcarrSetup.cancel();assert(!h.nodes.get('setupModal').hidden);for(let i=0;i<11;i++)await h.next();await h.next();await boot;
 // A completed-install refresh never waits for the setup endpoint.
 h=harness();h.storage.set('autosourcarr.setup.complete','1');h.context.api=()=>new Promise(()=>{});assert.equal(h.context.AutoSourcarrSetup.initialize(),undefined);
 // Refuse rerun while ordinary Settings has unsaved edits.
 h=harness();h.context.settingsState.dirty=true;await h.context.AutoSourcarrSetup.open();assert.equal(h.calls.filter(c=>c[0].startsWith('/api/')).length,0);
 // Required setup can recover from a conflict without a cancel escape hatch.
 h=harness(false);const first=h.context.AutoSourcarrSetup.initialize();await tick();for(let i=0;i<10;i++)await h.next();const api=h.context.api;h.context.api=async(path,opts)=>{if(path.endsWith('/finish'))throw new Error('Settings changed elsewhere');return api(path,opts);};await h.next();assert(!h.nodes.get('setupReload').hidden);await h.nodes.get('setupReload').onclick();assert.equal(h.nodes.get('setupTitle').textContent,'Your AutoSourcarr');h.context.api=api;for(let i=0;i<12;i++)await h.next();await first;
 console.log('Setup UI: cancellation, draft-only key generation, changed-only finish, first-run gate, warm refresh, dirty guard and conflict recovery passed');
})().catch(e=>{console.error(e);process.exitCode=1;});

module.exports={harness,tick};
