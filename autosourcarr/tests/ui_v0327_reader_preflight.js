/* Exercise the shipped setup/services scripts at their DOM and HTTP boundaries. */
const fs=require('fs'),path=require('path'),vm=require('vm'),assert=require('assert/strict'),crypto=require('crypto').webcrypto;
const staticDir=process.env.AUTOSOURCARR_UI_STATIC||path.join(__dirname,'../app/static');
const html=fs.readFileSync(path.join(staticDir,'index.html'),'utf8');
const scripts=Object.fromEntries(['repair-login.js','setup.js','internal-services.js'].map(name=>[name,fs.readFileSync(path.join(staticDir,name),'utf8')]));
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const contract=process.env.AUTOSOURCARR_READER_CONTRACT||path.join(__dirname,'../../extension/reader_build_config.py');
function evaluate(values){
  const result=require('child_process').spawnSync(process.env.AUTOSOURCARR_TEST_PYTHON||'python3',[contract,'--json'],{encoding:'utf8',env:{PATH:process.env.PATH,...values}});
  if(result.error)throw result.error;assert.notEqual(result.status,null,'Reader contract did not complete');return JSON.parse(result.stdout);
}
const good=evaluate({AUTOSOURCARR_READER_API_TOKEN:'x'.repeat(64),AUTOSOURCARR_INTERNAL_URL:'http://autosourcarr:8282',AUTOSOURCARR_PUBLIC_URL:'https://reader.example'});
const bad=evaluate({AUTOSOURCARR_READER_API_TOKEN:'',AUTOSOURCARR_INTERNAL_URL:'http://autosourcarr:8283',AUTOSOURCARR_PUBLIC_URL:'http://reader.example'});
assert.equal(good.ready,true);assert(bad.errors.length&&bad.warnings.length,'Contract fixture must include errors and warnings');
const ready=()=>structuredClone(good),invalid=()=>structuredClone(bad);
const clone=value=>structuredClone(value);
const secret='fixture-private-reader-key';
const decode=value=>String(value).replace(/&(?:amp|quot|#39|lt|gt);/g,s=>({'&amp;':'&','&quot;':'"','&#39;':"'",'&lt;':'<','&gt;':'>'}[s]));
function fixture(){return {completed:true,revision:'saved-v1',integrated:true,reader_build:ready(),cleanup:{status:'completed',checked:12,removed:[]},fields:[
  {key:'MAIN_SUWAYOMI_URL',kind:'url',step:'library',label:'Library',description:'Address',value:'http://library:30058'},
  {key:'AUTOSOURCARR_INTERNAL_URL',kind:'url',step:'readers',label:'Suwayomi reader address',description:'Inside Docker',value:'http://autosourcarr:8282'},
  {key:'AUTOSOURCARR_PUBLIC_URL',kind:'url',step:'readers',label:'Android reader address',description:'Outside Docker',value:'https://reader.example'},
  {key:'AUTOSOURCARR_READER_API_TOKEN',kind:'secret',generate:true,step:'readers',label:'Reader key',description:'Retain or replace',value:'',configured:true},
  {key:'BROWSE_PAGE_SIZE',kind:'integer',step:'performance',label:'Page size',description:'Size',value:50,minimum:1,maximum:500}
]};}
function services(build=ready(),state='idle',mode='integrated'){return {mode,reader_build:build,services:{builder:{state,message:state==='running'?'Building reader extensions':'Ready to build'},repair:{state:'idle'},translation:{state:'idle'},solver:{state:'idle'}}};}
function harness({setup=true,tools=false}={}){
  const nodes=new Map(),requests=[],timers=[],storage=new Map(),toasts=[],downloads=[];
  let document;
  function unregister(node){for(const child of node.children)unregister(child);if(node.id&&nodes.get(node.id)===node)nodes.delete(node.id);}
  class Node{
    constructor(tag='div'){this.tagName=tag.toUpperCase();this.children=[];this.parentElement=null;this.dataset={};this.attributes={};this.events=new Map();this.hidden=false;this.disabled=false;this.checked=false;this.value='';this._text='';this._html='';this.className='';this.onclick=null;this.open=false;}
    set id(value){if(this._id&&nodes.get(this._id)===this)nodes.delete(this._id);this._id=value;if(value)nodes.set(value,this);}
    get id(){return this._id||'';}
    setAttribute(key,value){value=decode(value);this.attributes[key]=value;if(key==='id')this.id=value;else if(key==='class')this.className=value;else if(key==='hidden'||key==='disabled'||key==='checked'||key==='selected')this[key]=true;else if(key==='value')this.value=value;else if(key.startsWith('data-'))this.dataset[key.slice(5).replace(/-([a-z])/g,(_,c)=>c.toUpperCase())]=value;}
    get classList(){return {contains:value=>this.className.split(/\s+/).includes(value),add:value=>{if(!this.className.split(/\s+/).includes(value))this.className+=' '+value;},remove:value=>{this.className=this.className.split(/\s+/).filter(c=>c!==value).join(' ');}};}
    append(...children){this._html='';this._text='';for(const item of children){const child=typeof item==='string'?Object.assign(new Node('#text'),{_text:item}):item;child.parentElement=this;this.children.push(child);}}
    replaceChildren(...children){for(const child of this.children)unregister(child);this.children=[];this._text='';this._html='';this.append(...children);}
    set textContent(value){this.replaceChildren();this._text=String(value??'');}
    get textContent(){return this._text+this.children.map(child=>child.textContent).join('');}
    set innerHTML(value){
      this.replaceChildren();this._html=String(value);const stack=[this];
      for(const token of String(value).match(/<!--[\s\S]*?-->|<[^>]*>|[^<]+/g)||[]){
        if(token.startsWith('<!--'))continue;
        if(token.startsWith('</')){const tag=token.slice(2,-1).trim().toUpperCase();for(let i=stack.length-1;i>0;i--)if(stack[i].tagName===tag){stack.length=i;break;}continue;}
        if(token.startsWith('<')){const match=/^<([\w-]+)\b([^>]*)>/.exec(token);if(!match)continue;const node=new Node(match[1]);attributes(node,match[2]);stack.at(-1).append(node);if(!['INPUT','BR','HR','IMG','META','LINK'].includes(node.tagName)&&!token.endsWith('/>'))stack.push(node);}
        else stack.at(-1).append(decode(token));
      }
      for(const select of this.querySelectorAll('select')){const options=select.querySelectorAll('option');select.value=(options.find(option=>option.selected)||options[0])?.value||'';}
      for(const textarea of this.querySelectorAll('textarea'))textarea.value=textarea.textContent;
    }
    get innerHTML(){return this._html;}
    querySelectorAll(selector){const out=[];const selectors=selector.split(',').map(s=>s.trim());const match=node=>selectors.some(s=>s==='[data-setup-key]'?Object.hasOwn(node.dataset,'setupKey'):s.startsWith('#')?node.id===s.slice(1):s.startsWith('[tabindex')?node.attributes.tabindex==='0':node.tagName===s.toUpperCase());function visit(node){for(const child of node.children){if(match(child))out.push(child);visit(child);}}visit(this);return out;}
    addEventListener(type,fn){if(!this.events.has(type))this.events.set(type,[]);this.events.get(type).push(fn);}
    dispatch(type,event={}){const handlers=[...(type==='click'&&this.onclick?[this.onclick]:[]),...(this.events.get(type)||[])];return Promise.all(handlers.map(fn=>fn({target:this,preventDefault(){},...event})));}
    click(){return this.disabled||this.hidden?Promise.resolve():this.dispatch('click');}
    focus(){document.activeElement=this;}
    checkValidity(){return true;}
    reportValidity(){}
    getClientRects(){return this.hidden?[]:[{}];}
    after(...children){if(!this.parentElement)return;const parent=this.parentElement,index=parent.children.indexOf(this);children.forEach(child=>child.parentElement=parent);parent.children.splice(index+1,0,...children);}
  }
  function attributes(node,source){for(const match of source.matchAll(/([\w:-]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+)))?/g))node.setAttribute(match[1],match[2]??match[3]??match[4]??'');}
  for(const match of html.matchAll(/<([\w-]+)\b([^>]*\bid="[^"]+"[^>]*)>/g)){const node=new Node(match[1]);attributes(node,match[2]);}
  document={hidden:false,activeElement:null,querySelector:selector=>nodes.get(selector.slice(1))||null,getElementById:id=>nodes.get(id)||null,createElement:tag=>new Node(tag),events:new Map(),addEventListener(type,fn){if(!this.events.has(type))this.events.set(type,[]);this.events.get(type).push(fn);}};
  for(const id of ['setupTitle','setupProgress','setupBody','setupError','setupCancel','setupBack','setupNext','setupReload'])nodes.get('setupModal').append(nodes.get(id));
  nodes.get('about').classList.add('active');
  const h={nodes,requests,timers,storage,toasts,downloads,data:fixture(),finishResult:{completed:true,restart_required:false,reader_build:ready()},finishDeferred:false};
  const context={document,crypto,Uint8Array,URL,console,JSON,Promise,structuredClone,confirm:()=>true,settingsState:{dirty:false,saving:false,editRevision:0},sessionStorage:{getItem:key=>storage.get(key)||null,setItem:(key,value)=>storage.set(key,value),removeItem:key=>storage.delete(key)},setTimeout:(fn,ms)=>{const timer={fn,ms,cancelled:false};timers.push(timer);return timer;},clearTimeout:timer=>{if(timer)timer.cancelled=true;},syncModalScrollLock(){},switchTab(){},setSourceSubtab(){},toast:message=>toasts.push(message),downloadBlob:(blob,name)=>downloads.push({blob,name}),loadApkDownload:()=>{nodes.get('downloadApk').disabled=false;nodes.get('apkDownloadStatus').textContent='Current APK available';},api:(url,options={})=>{
    const request={url,options,settled:false};requests.push(request);
    if(url==='/api/admin/setup'&&options.method!=='POST'){request.settled=true;return Promise.resolve(clone(h.data));}
    if(url.endsWith('/source-maintenance/provider')){request.settled=true;return Promise.resolve({provider:'chatgpt'});}
    if(url.endsWith('/source-maintenance/control')){request.settled=true;return Promise.resolve({busy:false});}
    if(url==='/api/admin/extension-jar'){request.settled=true;return Promise.resolve({available:true,version:'27'});}
    if(url.includes('/extension-jar/download')){request.settled=true;return Promise.resolve(h.jarBlob||{type:'application/java-archive',size:4});}
    if(url.endsWith('/setup/finish')&&!h.finishDeferred){request.settled=true;return h.finishResult instanceof Error?Promise.reject(h.finishResult):Promise.resolve(clone(h.finishResult));}
    return new Promise((resolve,reject)=>{request.resolve=value=>{request.settled=true;resolve(clone(value));};request.reject=error=>{request.settled=true;reject(error);};});
  }};
  context.window=context;vm.createContext(context);if(setup){vm.runInContext(scripts['repair-login.js'],context,{filename:'repair-login.js'});vm.runInContext(scripts['setup.js'],context,{filename:'setup.js'});}if(tools)vm.runInContext(scripts['internal-services.js'],context,{filename:'internal-services.js'});
  Object.assign(h,{context,get:id=>nodes.get(id),pending:(url,method)=>requests.find(request=>!request.settled&&request.url===url&&(!method||request.options.method===method)),count:url=>requests.filter(request=>request.url===url).length,async next(){await nodes.get('setupNext').click();await tick();},async edit(key,value,event='input'){const input=nodes.get('setup-'+key);input.value=value;await input.dispatch(event);await tick();},async check(){const promise=nodes.get('setupReaderCheck').click();await tick();return {request:[...requests].reverse().find(request=>!request.settled&&request.url==='/api/admin/setup/reader-preflight'),promise};},async review(){for(let i=0;i<6;i++)await h.next();},async load(data){const promise=context.loadInternalServices();await tick();h.pending('/api/admin/services').resolve(data);await promise;await tick();},builderCard(){return nodes.get('internalServices').children.find(card=>card.querySelectorAll('h3').some(title=>title.textContent==='Reader extension builder'));}});
  return h;
}
const checks=[];
function test(name,fn){checks.push({name,fn});}
function assertAttention(h){assert.match(h.get('setupReaderBuildStatus').textContent,/needs attention/);}
function assertUnchecked(h){assert.match(h.get('setupReaderBuildStatus').textContent,/not been checked/);}
function assertUnavailable(h){assert.match(h.get('setupReaderBuildStatus').textContent,/unavailable/);}
function finishRequests(h){return h.requests.filter(request=>request.url.endsWith('/setup/finish'));}
function resolveCheck(check,value=ready(),revision='saved-v1'){check.request.resolve({revision,reader_build:value});return check.promise;}

test('Readers opener stays a draft and displays saved readiness',async()=>{
  const h=harness();await h.context.AutoSourcarrSetup.openReaders();assert.equal(h.get('setupTitle').textContent,'Reader extensions');assert.match(h.get('setupReaderBuildStatus').textContent,/Saved reader settings pass/);assert.equal(h.requests.length,1);assert.equal(h.requests[0].url,'/api/admin/setup');assert.equal(h.storage.size,0);
});
test('Explicit draft check sends revision and changed fields without masked saved secret',async()=>{
  const h=harness();await h.context.AutoSourcarrSetup.openReaders();await h.edit('AUTOSOURCARR_PUBLIC_URL','https://changed.example');const check=await h.check();const body=JSON.parse(check.request.options.body);assert.equal(body.revision,'saved-v1');assert.deepEqual(body.changes,{AUTOSOURCARR_PUBLIC_URL:'https://changed.example'});assert.equal(check.request.options.timeoutMs,5000);assert.equal(check.request.options.noMutationRefresh,true);assert.equal(finishRequests(h).length,0);assert.equal(h.get('setupReaderCheck').disabled,true);await resolveCheck(check);assert.match(h.get('setupReaderBuildStatus').textContent,/Draft reader settings pass/);assert.equal(h.get('setupReaderCheck').disabled,false);
});
test('Review performs one check without polling or automatic retry after an error',async()=>{
  const h=harness();await h.context.AutoSourcarrSetup.openReaders();await h.review();assert.equal(h.count('/api/admin/setup/reader-preflight'),1);h.pending('/api/admin/setup/reader-preflight').reject(new Error('transport '+secret));await tick();assertUnavailable(h);assert.equal(h.get('setupBody').textContent.includes(secret),false);assert.equal(h.timers.filter(timer=>!timer.cancelled).length,0);await tick();assert.equal(h.count('/api/admin/setup/reader-preflight'),1);assert.equal(h.get('setupReaderCheck').disabled,false);
});
test('Input edit invalidates pending readiness and does not issue another check',async()=>{
  const h=harness();await h.context.AutoSourcarrSetup.openReaders();const check=await h.check();await h.edit('AUTOSOURCARR_PUBLIC_URL','');assertUnchecked(h);assert.equal(h.get('setupReaderCheck').disabled,false);await resolveCheck(check);assertUnchecked(h);assert.equal(h.count('/api/admin/setup/reader-preflight'),1);
});
test('Newer draft failure wins over an older successful check',async()=>{
  const h=harness();await h.context.AutoSourcarrSetup.openReaders();const old=await h.check();await h.edit('AUTOSOURCARR_PUBLIC_URL','');const current=await h.check();await resolveCheck(current,invalid());assertAttention(h);await resolveCheck(old);assertAttention(h);assert.match(h.get('setupReaderBuildIssues').textContent,/AUTOSOURCARR_PUBLIC_URL: Android reader address must use HTTPS/);
});
test('Old failure cannot clear newer readiness',async()=>{
  const h=harness();await h.context.AutoSourcarrSetup.openReaders();const old=await h.check();await h.edit('AUTOSOURCARR_PUBLIC_URL','https://new.example');const current=await h.check();await resolveCheck(current);old.request.reject(new Error(secret));await old.promise;assert.match(h.get('setupReaderBuildStatus').textContent,/Draft reader settings pass/);assert.equal(h.get('setupBody').textContent.includes(secret),false);
});
test('Navigation and detached input callbacks cannot revive the old draft check',async()=>{
  const h=harness();await h.context.AutoSourcarrSetup.openReaders();const input=h.get('setup-AUTOSOURCARR_PUBLIC_URL'),check=await h.check();await h.next();await h.get('setupBack').click();await tick();const currentInput=h.get('setup-AUTOSOURCARR_PUBLIC_URL');assert.notEqual(input,currentInput);input.value='https://detached.example';await input.dispatch('input');await resolveCheck(check);assertUnchecked(h);assert.equal(currentInput.value,'https://reader.example');const current=await h.check();assert.deepEqual(JSON.parse(current.request.options.body).changes,{});await resolveCheck(current);
});
test('Close and reopen fences responses from a previous setup snapshot',async()=>{
  const h=harness();await h.context.AutoSourcarrSetup.openReaders();const old=await h.check();await h.context.AutoSourcarrSetup.cancel();assert(h.get('setupModal').hidden);h.data.reader_build=invalid();h.data.revision='saved-v2';await h.context.AutoSourcarrSetup.openReaders();assertAttention(h);await resolveCheck(old);assertAttention(h);assert.equal(finishRequests(h).length,0);
});
test('Explicit secret removal and undo use the effective saved-value contract',async()=>{
  const h=harness();await h.context.AutoSourcarrSetup.openReaders();const old=await h.check();await h.get('setupClear-AUTOSOURCARR_READER_API_TOKEN').click();const removed=await h.check();assert.equal(JSON.parse(removed.request.options.body).changes.AUTOSOURCARR_READER_API_TOKEN,'');await resolveCheck(removed,invalid());await resolveCheck(old);assertAttention(h);await h.get('setupClear-AUTOSOURCARR_READER_API_TOKEN').click();assertUnchecked(h);const kept=await h.check();assert.equal(Object.hasOwn(JSON.parse(kept.request.options.body).changes,'AUTOSOURCARR_READER_API_TOKEN'),false);await resolveCheck(kept);
});
test('Generate after removal supplies a replacement and invalidates a pending check',async()=>{
  const h=harness();await h.context.AutoSourcarrSetup.openReaders();await h.get('setupClear-AUTOSOURCARR_READER_API_TOKEN').click();const old=await h.check();await h.get('setupGenerateKey').click();const token=h.get('setup-AUTOSOURCARR_READER_API_TOKEN').value;assert.match(token,/^[a-f0-9]{64}$/);assertUnchecked(h);const current=await h.check();assert.equal(JSON.parse(current.request.options.body).changes.AUTOSOURCARR_READER_API_TOKEN,token);await resolveCheck(current);await resolveCheck(old,invalid());assert.match(h.get('setupReaderBuildStatus').textContent,/Draft reader settings pass/);assert.equal(h.get('setupBody').textContent.includes(token),false);assert.equal(JSON.stringify([...h.storage]).includes(token),false);await h.review();const review=h.pending('/api/admin/setup/reader-preflight');review.resolve({revision:'saved-v1',reader_build:ready()});await tick();await h.next();assert.equal(JSON.parse(finishRequests(h)[0].options.body).changes.AUTOSOURCARR_READER_API_TOKEN,token);
});
test('Malformed and wrong-revision checks fail closed until explicit retry',async()=>{
  const h=harness();await h.context.AutoSourcarrSetup.openReaders();const malformed=await h.check();await resolveCheck(malformed,{ready:true,errors:[],warnings:[{}]});assertUnavailable(h);const stale=await h.check();await resolveCheck(stale,ready(),'different-revision');assertUnavailable(h);assert.equal(h.count('/api/admin/setup/reader-preflight'),2);const retried=await h.check();await resolveCheck(retried);assert.match(h.get('setupReaderBuildStatus').textContent,/Draft reader settings pass/);
});
test('Readiness shows warnings and treats backend issue text as plain text',async()=>{
  const h=harness();await h.context.AutoSourcarrSetup.openReaders();const check=await h.check();const result=ready();result.warnings=[{key:'AUTOSOURCARR_INTERNAL_URL',code:0,message:'<img src=x onerror=leak()> Local address advisory.'}];await resolveCheck(check,result);assert.match(h.get('setupReaderBuildStatus').textContent,/pass the build check/);assert.match(h.get('setupReaderBuildIssues').textContent,/Warning · AUTOSOURCARR_INTERNAL_URL: <img/);assert.equal(h.get('setupReaderBuildIssues').querySelectorAll('img').length,0);const contradictory=await h.check();await resolveCheck(contradictory,{...invalid(),ready:true});assertUnavailable(h);
});
test('Optional reader configuration does not block Finish while a check is pending',async()=>{
  const h=harness();h.data.integrated=false;h.data.reader_build=invalid();h.finishResult.reader_build=invalid();await h.context.AutoSourcarrSetup.openReaders();await h.edit('AUTOSOURCARR_PUBLIC_URL','');await h.review();const check=h.pending('/api/admin/setup/reader-preflight');assert.equal(h.get('setupNext').disabled,false);await h.next();assert.equal(finishRequests(h).length,1);assert.equal(h.get('setupTitle').textContent,'Setup saved');assertAttention(h);check.resolve({revision:'saved-v1',reader_build:ready()});await tick();assertAttention(h);assert.equal(JSON.parse(finishRequests(h)[0].options.body).changes.AUTOSOURCARR_PUBLIC_URL,'');
});
test('Failed Finish retains the draft and prevents an older check from claiming readiness',async()=>{
  const h=harness();h.finishResult=new Error('Settings changed elsewhere');await h.context.AutoSourcarrSetup.openReaders();await h.edit('AUTOSOURCARR_PUBLIC_URL','https://edited.example');await h.review();const check=h.pending('/api/admin/setup/reader-preflight');await h.next();assert.match(h.get('setupError').textContent,/draft is retained/);assert.equal(h.get('setupReload').hidden,false);check.resolve({revision:'saved-v1',reader_build:ready()});await tick();assertUnchecked(h);await h.next();assert.equal(finishRequests(h).length,2);assert.equal(JSON.parse(finishRequests(h)[1].options.body).changes.AUTOSOURCARR_PUBLIC_URL,'https://edited.example');
});
test('Reloading after a conflict fences old checks and uses the new saved snapshot',async()=>{
  const h=harness();h.finishResult=new Error('Conflict');await h.context.AutoSourcarrSetup.openReaders();await h.review();const old=h.pending('/api/admin/setup/reader-preflight');await h.next();h.data.revision='saved-v2';h.data.reader_build=invalid();await h.get('setupReload').click();for(let i=0;i<4;i++)await h.next();assertAttention(h);old.resolve({revision:'saved-v1',reader_build:ready()});await tick();assertAttention(h);const current=await h.check();assert.equal(JSON.parse(current.request.options.body).revision,'saved-v2');await resolveCheck(current,invalid(),'saved-v2');
});
test('Successful Finish owns saved readiness even when the old draft check fails later',async()=>{
  const h=harness();await h.context.AutoSourcarrSetup.openReaders();await h.review();const old=h.pending('/api/admin/setup/reader-preflight');await h.next();assert.match(h.get('setupReaderBuildStatus').textContent,/Saved reader settings pass/);old.reject(new Error(secret));await tick();assert.match(h.get('setupReaderBuildStatus').textContent,/Saved reader settings pass/);assert.equal(h.get('setupBody').textContent.includes(secret),false);
});

test('Build starts disabled until the running configuration has been checked',async()=>{
  const h=harness({setup:false,tools:true});assert.equal(h.get('buildReaderExtensions').disabled,true);await h.get('buildReaderExtensions').click();assert.equal(h.requests.length,0);
});
test('Invalid running configuration disables both build controls and offers targeted setup',async()=>{
  const h=harness({setup:true,tools:true});await h.load(services(invalid()));assert.equal(h.get('buildReaderExtensions').disabled,true);const card=h.builderCard(),build=card.querySelectorAll('button').find(button=>button.textContent==='Build extensions');assert.equal(build.disabled,true);assert.match(h.get('readerBuildPreflight').textContent,/AUTOSOURCARR_PUBLIC_URL: Android reader address must use HTTPS/);assert.match(card.textContent,/Android reader address must use HTTPS/);await h.get('buildReaderExtensions').click();assert.equal(h.requests.some(request=>request.options.method==='POST'),false);await h.get('configureReaderBuild').click();assert.equal(h.get('setupTitle').textContent,'Reader extensions');assert.equal(h.requests.filter(request=>request.options.method==='POST').length,0);await h.context.AutoSourcarrSetup.cancel();await card.querySelectorAll('button').find(button=>button.textContent==='Review reader settings').click();assert.equal(h.get('setupTitle').textContent,'Reader extensions');assert.equal(finishRequests(h).length,0);
});
test('Ready configuration with advisory warnings enables both build controls',async()=>{
  const h=harness({setup:false,tools:true}),result=ready();result.warnings=[{key:'AUTOSOURCARR_PUBLIC_URL',code:0,message:'HTTP reader connections are unencrypted.'}];await h.load(services(result));assert.equal(h.get('buildReaderExtensions').disabled,false);assert.equal(h.builderCard().querySelectorAll('button').find(button=>button.textContent==='Build extensions').disabled,false);assert.match(h.get('readerBuildPreflight').textContent,/Warning · AUTOSOURCARR_PUBLIC_URL: HTTP/);
});
test('Running invalid build preserves Cancel and existing APK/JAR downloads',async()=>{
  const h=harness({setup:false,tools:true});await h.load(services(invalid(),'running'));assert.equal(h.get('buildReaderExtensions').disabled,true);assert.equal(h.get('cancelReaderBuild').hidden,false);assert.equal(h.get('cancelReaderBuild').disabled,false);assert.equal(h.get('downloadJar').disabled,false);assert.equal(h.get('downloadApk').disabled,false);await h.get('downloadJar').click();assert.equal(h.downloads.length,1);assert.equal(h.downloads[0].name,'AutoSourcarr-v27-Suwayomi.jar');assert.equal(h.get('downloadJar').disabled,false);
});
test('Cancel works with invalid configuration and reconciles the resulting state',async()=>{
  const h=harness({setup:false,tools:true});await h.load(services(invalid(),'running'));const promise=h.get('cancelReaderBuild').click();await tick();const post=h.pending('/api/admin/services/builder/cancel','POST');assert(post);assert.equal(h.get('cancelReaderBuild').disabled,true);post.resolve(services(invalid(),'idle'));await tick();h.pending('/api/admin/services').resolve(services(invalid(),'idle'));await promise;assert.equal(h.get('cancelReaderBuild').hidden,true);assert.equal(h.get('buildReaderExtensions').disabled,true);assert.equal(h.requests.some(request=>request.url.endsWith('/builder/start')),false);
});
test('Pending Build is single-flight and queued invalid results retain Cancel',async()=>{
  const h=harness({setup:false,tools:true});await h.load(services());const promise=h.get('buildReaderExtensions').click();await tick();assert.equal(h.get('buildReaderExtensions').disabled,true);await h.get('buildReaderExtensions').click();const post=h.pending('/api/admin/services/builder/start','POST');assert.equal(h.count('/api/admin/services/builder/start'),1);post.resolve(services(invalid(),'queued'));await tick();assert.equal(h.get('cancelReaderBuild').hidden,false);h.pending('/api/admin/services').resolve(services(invalid(),'running'));await promise;assert.equal(h.get('cancelReaderBuild').hidden,false);assert.equal(h.get('cancelReaderBuild').disabled,false);assert.equal(h.get('buildReaderExtensions').disabled,true);
});
test('Old GET cannot overwrite a newer builder mutation or its reconciled snapshot',async()=>{
  const h=harness({setup:false,tools:true});await h.load(services());const oldPromise=h.context.loadInternalServices();await tick();const old=h.pending('/api/admin/services');const action=h.get('buildReaderExtensions').click();await tick();h.pending('/api/admin/services/builder/start','POST').resolve(services(invalid(),'queued'));await tick();const current=h.requests.find(request=>!request.settled&&request.url==='/api/admin/services'&&request!==old);assert(current);current.resolve(services(invalid(),'running'));await action;old.resolve(services());await oldPromise;assert.equal(h.get('cancelReaderBuild').hidden,false);assert.equal(h.get('buildReaderExtensions').disabled,true);assert.match(h.get('readerBuildPreflight').textContent,/needs attention/);
});
test('Obsolete action failure cannot replace a newer running configuration result',async()=>{
  const h=harness({setup:false,tools:true});await h.load(services());const action=h.get('buildReaderExtensions').click();await tick();const post=h.pending('/api/admin/services/builder/start','POST');const refreshed=h.context.loadInternalServices();await tick();h.pending('/api/admin/services').resolve(services(ready(),'running'));await refreshed;assert.match(h.get('readerBuildPreflight').textContent,/Running reader settings pass/);post.reject(new Error(secret));await tick();assert.match(h.get('readerBuildPreflight').textContent,/Running reader settings pass/);assert.equal(h.toasts.length,0);assert.equal(h.get('cancelReaderBuild').hidden,false);h.pending('/api/admin/services').resolve(services(ready(),'running'));await action;assert.match(h.get('readerBuildPreflight').textContent,/Running reader settings pass/);
});
test('Failed status refresh disables Build while preserving running Cancel and downloads',async()=>{
  const h=harness({setup:false,tools:true});await h.load(services(ready(),'running'));const promise=h.context.loadInternalServices();await tick();h.pending('/api/admin/services').reject(new Error(secret));await promise;assert.equal(h.get('buildReaderExtensions').disabled,true);assert.equal(h.get('cancelReaderBuild').hidden,false);assert.equal(h.get('cancelReaderBuild').disabled,false);assert.equal(h.get('downloadJar').disabled,false);assert.equal(h.get('downloadApk').disabled,false);assert.equal(h.get('readerBuildStatus').textContent.includes(secret),false);assert.match(h.get('readerBuildPreflight').textContent,/unavailable/);
});
test('Missing malformed or contradictory build readiness fails closed in services',async()=>{
  const h=harness({setup:false,tools:true});for(const value of [undefined,{ready:true,errors:[],warnings:[{}]},{...invalid(),ready:true}]){const payload=services(value);if(value===undefined)delete payload.reader_build;await h.load(payload);assert.equal(h.get('buildReaderExtensions').disabled,true);assert.equal(h.builderCard().querySelectorAll('button').find(button=>button.textContent==='Build extensions').disabled,true);}await h.load(services(ready(),'idle','external'));assert.equal(h.get('buildReaderExtensions').disabled,true);
});
test('Rejected build request is generic and reconciles once without replaying a POST',async()=>{
  const h=harness({setup:false,tools:true});await h.load(services());const promise=h.get('buildReaderExtensions').click();await tick();const error=Object.assign(new Error('400 '+secret),{status_code:400});h.pending('/api/admin/services/builder/start','POST').reject(error);await tick();assert.equal(h.get('buildReaderExtensions').disabled,true);assert.equal(h.toasts.some(message=>message.includes(secret)),false);h.pending('/api/admin/services').resolve(services(invalid()));await promise;assert.equal(h.count('/api/admin/services/builder/start'),1);assert.equal(h.count('/api/admin/services'),2);assert.match(h.get('readerBuildPreflight').textContent,/Android reader address must use HTTPS/);
});
test('JAR download still rejects an invalid body while build configuration is invalid',async()=>{
  const h=harness({setup:false,tools:true});await h.load(services(invalid()));h.jarBlob={type:'text/html',size:4};await h.get('downloadJar').click();assert.equal(h.downloads.length,0);assert.match(h.get('readerBuildStatus').textContent,/No valid JAR/);assert.equal(h.get('downloadJar').disabled,false);assert.equal(h.get('buildReaderExtensions').disabled,true);
});

(async()=>{
  const filter=process.env.AUTOSOURCARR_UI_CASE;
  const selected=checks.filter(check=>!filter||check.name.includes(filter));assert(selected.length,'No matching UI behavior checks');
  for(const {name,fn} of selected){let watchdog;try{await Promise.race([fn(),new Promise((_,reject)=>{watchdog=setTimeout(()=>reject(new Error('Timed out: '+name)),5000);})]);console.log('PASS '+name);}finally{clearTimeout(watchdog);}}
  console.log(`Reader preflight UI: ${selected.length} controlled-DOM scenarios passed`);
})().catch(error=>{console.error(error);process.exitCode=1;});
