const {harness,tick}=require('./ui_setup.js');
const assert=require('assert/strict'),fs=require('fs'),vm=require('vm');
const toReview=async h=>{for(let i=0;i<11;i++){if(h.nodes.get('setupTitle').textContent==='Review and finish')return;await h.next();}throw Error('No review step');};
const deferred=()=>{let resolve;const promise=new Promise(r=>resolve=r);return {promise,resolve};};
(async()=>{
 let h=harness();
 h.data.fields.push({key:'COMICVINE_API_KEY',kind:'secret',step:'matching',label:'Comic Vine',description:'key',value:'',configured:true});
 await h.context.AutoSourcarrSetup.open();await h.next();await h.next();await h.next();
 h.nodes.get('setupClear-COMICVINE_API_KEY').events.click();
 await toReview(h);assert(h.nodes.get('setupBody').innerHTML.includes('saved credential will be removed'));await h.next();
 let payload=JSON.parse(h.calls.find(c=>c[0].endsWith('/finish'))[1].body);
 assert.equal(payload.changes.COMICVINE_API_KEY,'');assert(!('AUTOSOURCARR_READER_API_TOKEN' in payload.changes));

 // The custom provider renders actual connection controls, without saving drafts.
 h=harness();h.data.fields.push(
  {key:'REPAIR_AI_PROVIDER',step:'repair',kind:'choice',label:'AI',options:[['chatgpt','ChatGPT'],['custom','Custom']],description:'',value:'chatgpt'},
  {key:'REPAIR_AI_API_KEY',step:'repair',kind:'secret',label:'AI key',description:'',value:'',configured:true});
 await h.context.AutoSourcarrSetup.open();for(let i=0;i<7;i++)await h.next();
 const select=h.nodes.get('setup-REPAIR_AI_PROVIDER');assert.equal(select.value,'chatgpt');
 select.value='custom';select.events.change();h.nodes.get('setup-REPAIR_AI_API_KEY').value='sensitive-draft';
 await h.next();h.context.AutoSourcarrSetup.cancel();
 assert(!h.calls.some(c=>c[1]?.method==='POST'));assert(!JSON.stringify([...h.storage]).includes('sensitive-draft'));

 // A pending login cannot be escaped through a completion shortcut. On close,
 // an owned active sign-in is cancelled, and late polling cannot restore its code.
 h=harness();let logged=false,cancelled=0,operation_id='';const login=deferred();const base=h.context.api;
 h.context.api=async(path,opts)=>{
   if(path.endsWith('/control/login')){await login.promise;logged=true;operation_id=JSON.parse(opts.body).operation_id;return {busy:true,operation_id};}
   if(path.endsWith('/control/cancel-login')){cancelled++;logged=false;return {};}
   if(path.endsWith('/control'))return {busy:logged,operation_id,action:logged?'login':'',device_code:logged?'ABCD-EFGH':'',verification_url:'https://auth.openai.com/codex/device',expires_at:Date.now()/1000+120};
   return base(path,opts);
 };
 await h.context.AutoSourcarrSetup.open();await toReview(h);await h.next();await tick();
 const action=h.nodes.get('setupAccount-login').events.click();
 await h.nodes.get('setupSources').events.click();assert(!h.nodes.get('setupModal').hidden);assert(!h.calls.some(c=>c[0]==='tab'));
 login.resolve();await action;await tick();assert(h.nodes.get('setupDeviceCode').innerHTML.includes('ABCD-EFGH'));
 h.context.AutoSourcarrSetup.cancel();await tick();assert.equal(cancelled,1);assert(h.nodes.get('setupModal').hidden);
 for(const callback of h.timers)callback();await tick();assert.equal(h.nodes.get('setupBody').innerHTML,'');

 // Status opens the existing tab identifier.
 h=harness();await h.context.AutoSourcarrSetup.open();await toReview(h);await h.next();
 await h.nodes.get('setupStatus').events.click();assert(h.calls.some(c=>c[0]==='tab'&&c[1]==='about'));

 // Production API rotates a previously cached admin token after a 401.
 const source=fs.readFileSync(__dirname+'/../app/static/app.js','utf8');
 const api=source.slice(source.indexOf('async function api('),source.indexOf("window.addEventListener('unhandledrejection'"));
 const sent=[],storage=new Map([['sc_admin_token','old-token']]);let prompts=0;
 const context={token:'old-token',adminAuthDismissed:false,performance,AbortController,setTimeout,clearTimeout,clientErrors:[],
  localStorage:{setItem:(k,v)=>storage.set(k,v),removeItem:k=>storage.delete(k)},prompt:()=>{prompts++;return 'new-token';},
  scheduleMutationRefresh(){},rememberClientError(){},fetch:async(url,options)=>{
   sent.push(options);const good=options.headers['X-Admin-Token']==='new-token';
   return {status:good?200:401,ok:good,headers:{get:()=>''},json:async()=>({ok:true})};
  }};
 vm.createContext(context);vm.runInContext(api,context);
 assert((await context.api('/api/admin/setup')).ok);assert.equal(prompts,1);assert.equal(storage.get('sc_admin_token'),'new-token');
 assert.equal(sent.length,2);assert(!('_adminAuthRetried' in sent[1]));
 console.log('Setup connections: explicit secret removal, custom AI drafts, login ownership, stale polling, Status navigation and admin-token rotation passed');
})().catch(e=>{console.error(e);process.exitCode=1;});
