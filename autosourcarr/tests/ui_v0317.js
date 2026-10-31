/* Read-only review tests against production JavaScript, without a browser. */
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const project=path.resolve(__dirname,'../..');
const app=fs.readFileSync(path.join(project,'autosourcarr/app/static/app.js'),'utf8');
const nav=fs.readFileSync(path.join(project,'autosourcarr/app/static/navigation-state.js'),'utf8');
const html=fs.readFileSync(path.join(project,'autosourcarr/app/static/index.html'),'utf8');
const slice=(begin,end)=>{const i=app.indexOf(begin),j=app.indexOf(end,i);assert(i>=0&&j>i,begin);return app.slice(i,j);};
const settings=slice('function settingsFromApi(','window.goDuplicates=');
const apiV1Source=slice('function apiV1(','\n');
const apiClient=fs.readFileSync(path.join(project,'autosourcarr/app/static/generated/api-client.js'),'utf8');
const navigation=slice('let navigationGeneration=','document.querySelectorAll(\'nav button\').forEach');
const bootstrap=slice('async function bootstrapAutoSourcarr(){',"if(typeof initializeWorkbench===");
const prefs=slice('function applyUiPreferences(values){','async function loadUiPreferences(){');
const mark=slice('function markSettingsDirty(){','function updateSettingsDirtyLabel(){');
const prefSetter=slice('function setSettingsSubtab(value,save=true){',"applyDensity(readLocalUiPreference");
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const deferred=()=>{let resolve,reject;const promise=new Promise((a,b)=>{resolve=a;reject=b;});return {promise,resolve,reject};};
class Node {
 constructor(id='',tag='div',classes=''){this.id=id;this.tagName=tag.toUpperCase();this.dataset={};this.hidden=false;this.disabled=false;this.options=[];this._value='';this.textContent='';this.children=[];this.attrs={};this.defaultValue='';this.classes=new Set(classes.split(/\s+/).filter(Boolean));this.classList={contains:x=>this.classes.has(x),add:x=>this.classes.add(x),remove:x=>this.classes.delete(x),toggle:(x,on)=>{if(on===undefined)on=!this.classes.has(x);on?this.classes.add(x):this.classes.delete(x);return on;}};}
 set value(value){this._value=this.tagName==='SELECT'&&!this.options.some(option=>option.value===String(value))?'':String(value);}
 get value(){return this._value;}
 append(child){this.children.push(child);if(this.tagName==='SELECT')this.options.push(child);}
 setAttribute(key,value){this.attrs[key]=String(value);}
 getAttribute(key){return this.attrs[key]||null;}
 querySelectorAll(selector){return selector==='[data-env-key]'?this.children:[];}
 querySelector(){return null;}
 addEventListener(){}
 matches(selector){return selector==='details'&&this.tagName==='DETAILS';}
}
function harness(saved={version:1,tab:'browse'}) {
 const nodes=new Map(),tabs=[],buttons=[],events=new Map(),calls=[],timers=new Map();let timer=0;
 for(const match of html.matchAll(/<(\w+)\b([^>]*\bid="([^"]+)"[^>]*)>/g)) {
   const [,tag,attrs,id]=match,node=new Node(id,tag,/class="([^"]*)"/.exec(attrs)?.[1]||'');nodes.set(id,node);
   if(node.classes.has('tab'))tabs.push(node);
   if(tag==='select'){
     const rest=html.slice(match.index+match[0].length);const inside=rest.slice(0,rest.indexOf('</select>'));
     for(const option of inside.matchAll(/<option[^>]*value="([^"]*)"[^>]*>/g)){const opt=new Node('','option');opt._value=option[1];node.append(opt);}
     node.value=node.options[0]?.value||'';
   }
 }
 for(const match of html.matchAll(/<button\b([^>]*\bdata-tab="([^"]+)"[^>]*)>/g)){const n=new Node('','button',/class="([^"]*)"/.exec(match[1])?.[1]||'');n.dataset.tab=match[2];buttons.push(n);}
 const active=()=>tabs.find(n=>n.classList.contains('active'));
 const document={readyState:'loading',getElementById:id=>nodes.get(id)||null,createElement:tag=>new Node('',tag),querySelector:selector=>{
   if(selector==='main')return new Node('main');
   if(selector==='main > .tab.active')return active();
   const nav=/^nav button\[data-tab="([^"]+)"\]$/.exec(selector);if(nav)return buttons.find(n=>n.dataset.tab===nav[1])||null;
   const simple=/^#([\w-]+)(\.active)?$/.exec(selector);if(simple){const n=nodes.get(simple[1]);return !simple[2]||n?.classList.contains('active')?n||null:null;}
   return null;
 },querySelectorAll:selector=>selector==='nav button,.tab'?[...buttons,...tabs]:selector==='nav button'?buttons:[],addEventListener:(name,fn)=>{if(!events.has(name))events.set(name,[]);events.get(name).push(fn);}};
 const local=new Map([['autosourcarr.preferenceOwner','local:Local user']]),session=new Map(saved?[['autosourcarr.navigation.v1',JSON.stringify({...saved,owner:saved.owner||'local:Local user'})]]:[]),preferences={};
 const context={document,console,Promise,Map,Set,URLSearchParams,AbortController,Number,JSON,performance:{now:()=>0},MutationObserver:class{observe(){}},setTimeout:(fn,delay)=>{const id=++timer;timers.set(id,{fn,delay});return id;},clearTimeout:id=>timers.delete(id),requestAnimationFrame:fn=>{const id=++timer;timers.set(id,{fn,delay:0});return id;},cancelAnimationFrame:id=>timers.delete(id),queueMicrotask,
  localStorage:{getItem:k=>local.get(k)||null,setItem:(k,v)=>local.set(k,v),removeItem:k=>local.delete(k)},sessionStorage:{getItem:k=>session.get(k)||null,setItem:(k,v)=>session.set(k,v),removeItem:k=>session.delete(k)},
  scrollX:0,scrollY:0,scrollTo({left,top}){this.scrollX=left;this.scrollY=top;},addEventListener:(name,fn)=>document.addEventListener(name,fn),
  browseState:{mode:'POPULAR',page:1,items:[]},importsState:{runId:'',filter:'all',review:'all',offset:0},workbenchState:{importKey:'',importId:null,duplicateLeft:null,duplicateRight:null,taskLane:'all',libraryScope:'all'},mixedEntriesState:{offset:0,selected:null},manualActionsSubtab:'matches',duplicateScope:'subbed',matrixData:null,subbedData:null,duplicateData:null,sourceRows:[],
  settingsState:{data:null,dirty:false,activeFile:'autosourcarr',editRevision:0,loadPromise:null,saving:false},uiPreferencesState:{loaded:true,pending:{},values:{}},
  readLocalUiPreference:k=>preferences[k],writeLocalUiPreference:(k,v)=>preferences[k]=v,
  setSourceSubtab:v=>preferences.sourcesSubtab=v,setSettingsSubtab:v=>preferences.settingsSubtab=v,setManualActionsSubtab:v=>context.manualActionsSubtab=v,setBrowseMode:v=>context.browseState.mode=v,
  syncDuplicateScopeButtons(){},syncBrowseConnectionControls(){},flushDeferredUiRenders(){},toast:message=>calls.push(['toast',message]),activeSurface:tab=>active()?.id===tab,
  api:async url=>{calls.push(['api',url]);return {files:[]};},settingsFile:()=>context.settingsState.data?.files?.find(f=>f.id===context.settingsState.activeFile),renderEnvSettings:()=>calls.push(['renderEnv']),loadDatabaseStorage:async()=>calls.push(['databaseStorage']),updateSettingsDirtyLabel(){},
 };
 context.window=context;context.$=selector=>document.querySelector(selector);
 for(const name of ['loadBrowse','loadSubbed','loadMatrix','loadDuplicates','loadMixedEntries','loadManualActions','health','loadApkDownload','loadImports','loadAttention','loadTasks','loadSourceBrowser','loadInternalServices','loadTagReview','loadSources','startTaskPolling','runLiveEvents','scheduleMetadataLivePoll','scheduleDuplicateHeartbeat','updateUiPreferenceOwner','rememberClientError','renderMatrix','renderDuplicates'])context[name]=async(...args)=>{calls.push([name,...args]);};
 context.loadUiPreferences=async()=>{};context.applyUiPreferences=()=>{};context.browsePageSizeNeedsReload=()=>false;
 vm.createContext(context);vm.runInContext(apiClient,context);vm.runInContext(nav,context);vm.runInContext(apiV1Source+navigation+settings+mark+bootstrap,context);
 return {context,nodes,calls,events,preferences,session,timers,run:code=>vm.runInContext(code,context),dispatch:(name,target,isTrusted=true)=>{for(const fn of events.get(name)||[])fn({target,isTrusted});},tick};
}
const tests=[];const test=(name,fn)=>tests.push({name,fn});
test('refresh Matrix loads Matrix only, preserves large source ID',async()=>{
 const h=harness({version:1,tab:'matrix',controls:{matrixSource:'9223372036854775806',matrixSearch:'remember me'}});
 await h.context.bootstrapAutoSourcarr();
 assert.equal(h.context.document.querySelector('main > .tab.active').id,'matrix');
 assert.equal(h.nodes.get('matrixSource').value,'9223372036854775806');
 assert.equal(h.nodes.get('matrixSearch').value,'remember me');
 assert.equal(h.calls.filter(call=>call[0]==='loadMatrix').length,1);
 assert.equal(h.calls.filter(call=>call[0]==='loadBrowse').length,0);
});
test('user navigation beats delayed initial filtered Browse startup',async()=>{
 const h=harness({version:1,tab:'browse',controls:{browseLanguage:'en'}}),inventory=deferred();
 h.context.loadSources=()=>inventory.promise;
 const boot=h.context.bootstrapAutoSourcarr();await tick();await h.context.switchTab('tasks');inventory.resolve();await boot;
 assert.equal(h.calls.filter(call=>call[0]==='loadBrowse').length,0);
 assert.equal(h.context.document.querySelector('main > .tab.active').id,'tasks');
});
test('late source inventory preserves new explicit filter selection',async()=>{
 const h=harness({version:1,tab:'matrix',controls:{matrixSource:'9223372036854775806'}});
 h.context.AutoSourcarrNavigation.initialize();const control=h.nodes.get('matrixSource');
 const option=new Node('','option');option._value='-123';control.append(option);control.value='-123';h.dispatch('change',control);
 h.context.AutoSourcarrNavigation.filtersReady();assert.equal(control.value,'-123');
});
test('read-only customization never requests env settings',async()=>{
 const h=harness(null);h.preferences.settingsSubtab='customization';await h.context.switchTab('settings');
 assert(!h.calls.some(call=>call[0]==='api'&&call[1]==='/api/v1/settings'));
});
test('dirty configuration survives leaving and reopening settings',async()=>{
 const h=harness(null);h.preferences.settingsSubtab='configuration';h.context.settingsState.dirty=true;h.context.settingsState.data={files:[{id:'autosourcarr',revision:'old',writable:true}]};
 await h.context.switchTab('settings');await h.context.switchTab('tasks');await h.context.switchTab('settings');
 assert.equal(h.context.settingsState.dirty,true);assert(!h.calls.some(call=>call[0]==='api'));
});
test('new edits made during explicit Reload survive its response',async()=>{
 const h=harness(null),get=deferred(),old={files:[{id:'autosourcarr',revision:'old',writable:true}]};h.context.settingsState.data=old;h.context.api=()=>get.promise;
 const loading=h.context.loadEnvSettings({force:true});h.context.markSettingsDirty();get.resolve({files:[{id:'autosourcarr',revision:'new'}]});await loading;
 assert.equal(h.context.settingsState.data,old);assert.equal(h.context.settingsState.dirty,true);assert(!h.calls.some(call=>call[0]==='renderEnv'));
});
test('new edits made during Save remain dirty with advanced revision',async()=>{
 const h=harness(null),put=deferred(),file={id:'autosourcarr',revision:'old',writable:true};h.context.settingsState.data={files:[file]};h.context.settingsState.dirty=true;h.context.api=()=>put.promise;
 const field=new Node('test');field.dataset.envKey='BROWSE_PAGE_SIZE';field.value='50';h.nodes.get('settingsGroups').children=[field];
 const saving=h.context.saveEnvSettings();field.value='100';h.context.markSettingsDirty();put.resolve({revision:'new',path:'/config/autosourcarr.env'});await saving;
 assert.equal(h.context.settingsState.dirty,true);assert.equal(field.value,'100');assert.equal(file.revision,'new');
});
test('manual action search roundtrip uses actual input ID',async()=>{
 const h=harness({version:1,tab:'manual-actions',controls:{manualActionSearch:'my alias'}});h.context.AutoSourcarrNavigation.initialize();
 assert.equal(h.nodes.get('manualActionSearch').value,'my alias');
});
test('late preferences do not replace restored source subtab or duplicate scope',async()=>{
 const h=harness({version:1,tab:'sources',sourceSubtab:'maintenance',duplicates:{scope:'unsubbed'}});h.context.AutoSourcarrNavigation.initialize();
 for(const name of ['normalizeHexColor','normalizeAccentStrength','normalizeCardFill','normalizeThemeStyle','normalizeThumbnailScale'])h.context[name]=value=>value;
 for(const name of ['syncCustomColorInputs','applyCustomizationSections','applyTheme','applyDensity','applyThumbnailSizes','applySourceSectionPreferences'])h.context[name]=()=>{};
 h.context.DEFAULT_CUSTOM_COLOR='#FFFFFF';vm.runInContext(prefs,h.context);
 h.context.applyUiPreferences({sourcesSubtab:'installed',duplicateScope:'subbed'});
 assert.equal(h.preferences.sourcesSubtab,'maintenance');assert.equal(h.context.duplicateScope,'unsubbed');
});
test('settings file tab ignores clicks during pending Save',async()=>{
 const h=harness(null),put=deferred(),file={id:'autosourcarr',revision:'a',writable:true},other={id:'source_host',revision:'b',writable:true};
 h.context.settingsState.data={files:[file,other]};h.context.settingsState.dirty=true;
 let requests=0;h.context.api=()=>++requests===1?put.promise:Promise.resolve({files:[file,other]});
 const line=app.split('\n').find(line=>line.includes("tabs.querySelectorAll('[data-settings-file]')"));
 h.context.b={dataset:{settingsFile:'source_host'}};
 const handler=h.run('('+line.slice(line.indexOf('b.onclick=')+10,line.lastIndexOf(');'))+')');
 const pending=h.context.saveEnvSettings();handler();
 assert.equal(h.context.settingsState.activeFile,'autosourcarr');
 put.resolve({revision:'a2',path:'/config/autosourcarr.env'});await pending;
});
test('refresh during first imports request retains restored selection',async()=>{
 const h=harness({version:1,tab:'imports',imports:{runId:'run-A',filter:'unresolved',selected:55}});
 h.context.AutoSourcarrNavigation.initialize();h.context.switchTab('imports',{load:false});h.context.AutoSourcarrNavigation.save();
 assert.equal(JSON.parse(h.session.get('autosourcarr.navigation.v1')).imports.selected,55);
});
test('loading placeholder cannot consume pending scroll restoration',async()=>{
 const h=harness({version:1,tab:'imports',scroll:{imports:{x:0,y:800}}});
 h.context.AutoSourcarrNavigation.initialize();h.context.switchTab('imports',{load:false});
 let contentReady=false;
 const query=h.context.document.querySelector;
 h.context.document.querySelector=selector=>selector.includes('.emptyTaskState')&&!contentReady?new Node('loading'):query(selector);
 h.context.scrollTo=function({left,top}){this.scrollX=left;this.scrollY=contentReady?top:0;};
 const frames=()=>{for(const [id,timer] of [...h.timers])if(timer.delay===0){h.timers.delete(id);timer.fn();}};
 frames();contentReady=true;h.context.AutoSourcarrNavigation.rendered('imports');frames();
 assert.equal(h.context.scrollY,800);
});
(async()=>{let failed=0;for(const {name,fn} of tests){try{await fn();console.log('PASS',name);}catch(error){failed++;console.log('FAIL',name,':',error.message);}}console.log(`${tests.length-failed} passed; ${failed} failed`);process.exitCode=failed?1:0;})();
