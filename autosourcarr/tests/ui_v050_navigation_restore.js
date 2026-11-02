/* UI-6: a browser refresh returns to the same tab, sub-tab, filters, scroll, library page and open entry. */
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const projectRoot=process.env.AUTOSOURCARR_PROJECT_ROOT||path.resolve(__dirname,'../..');
const staticDir=path.join(projectRoot,'autosourcarr/app/static');
const nav=fs.readFileSync(path.join(staticDir,'navigation-state.js'),'utf8');
const libraryController=fs.readFileSync(path.join(staticDir,'library-controller.js'),'utf8');
const KEY='autosourcarr.navigation.v1';
const tick=()=>new Promise(resolve=>setImmediate(resolve));

class Node{
  constructor(id,tag='div',classes=''){this.id=id;this.tagName=tag.toUpperCase();this.value='';this.options=[];this.children=[];this.dataset={};this.open=false;this.hidden=false;
    this.classSet=new Set(classes.split(/\s+/).filter(Boolean));const set=this.classSet;
    this.classList={contains:c=>set.has(c),add:c=>set.add(c),remove:c=>set.delete(c),toggle:(c,on)=>{if(on===undefined?!set.has(c):on)set.add(c);else set.delete(c);}};
    this.textContent='';this.disabled=false;this.innerHTML='';}
  append(child){this.children.push(child);if(child.tagName==='OPTION')this.options.push(child);}
  add(option){this.append(option);}
  replaceChildren(...nodes){this.children=[];this.options=[];for(const n of nodes)this.append(n);}
  querySelectorAll(){return [];}
  querySelector(){return null;}
  setAttribute(){}
}
function select(id,values){const node=new Node(id,'select');for(const value of values){const o=new Node('','option');o.value=value;node.append(o);}node.value=values[0]||'';return node;}

function harness(saved,{busy=false}={}){
  const nodes=new Map(),listeners=new Map(),frames=[],opened=[],libraryCalls=[];
  const add=node=>{nodes.set(node.id,node);return node;};
  for(const tab of ['browse','subbed','matrix','sources','settings','tasks'])add(new Node(tab,'section','tab'));
  add(new Node('browseSearch','input'));add(new Node('subbedSearch','input'));add(new Node('tagCatalogSearch','input'));
  add(select('sourceHealthFilter',['all','attention','working']));add(select('riLibraryPageSize',['30','60','120']));
  add(select('subbedSource',[]));
  let busyList=busy;
  const active=()=>[...nodes.values()].find(n=>n.classList.contains('tab')&&n.classList.contains('active'));
  const document={
    getElementById:id=>nodes.get(id)||null,createElement:tag=>new Node('',tag),
    querySelector:selector=>{
      if(selector==='main')return new Node('main');
      if(selector==='main > .tab.active')return active()||null;
      if(/\[aria-busy="true"\]$/.test(selector))return busyList?new Node('busy'):null;
      return null;
    },
    querySelectorAll:()=>[],
    addEventListener:(type,fn)=>{if(!listeners.has(type))listeners.set(type,[]);listeners.get(type).push(fn);},
  };
  const session=new Map(saved?[[KEY,JSON.stringify(saved)]]:[]);
  const context={document,console,JSON,Number,Object,Array,Math,Set,Map,Promise,
    MutationObserver:class{observe(){}},
    requestAnimationFrame:fn=>{frames.push(fn);return frames.length;},cancelAnimationFrame:()=>{},
    setTimeout:()=>0,clearTimeout:()=>{},queueMicrotask:fn=>fn(),
    localStorage:{getItem:key=>key==='autosourcarr.preferenceOwner'?'local:Local user':null},
    sessionStorage:{getItem:key=>session.get(key)||null,setItem:(key,value)=>session.set(key,value),removeItem:key=>session.delete(key)},
    scrollX:0,scrollY:0,scrollTo({left,top}){context.scrollX=left;context.scrollY=top;},addEventListener:(type,fn)=>document.addEventListener(type,fn),
    browseState:{mode:'POPULAR',page:1,items:[],selectedEntryId:null},importsState:{runId:'',filter:'all',review:'all',offset:0},
    workbenchState:{importKey:'',importId:null,duplicateLeft:null,duplicateRight:null,taskLane:'all',libraryScope:'all'},
    duplicateScope:'subbed',matrixData:null,subbedData:null,duplicateData:null,manualActionsSubtab:'matches',activeInspector:{matrix:null,subbed:null},browseOpen:false,
    browseInspectorIsOpen:()=>context.browseOpen,
    readLocalUiPreference:()=>undefined,setBrowseMode(){},syncDuplicateScopeButtons(){},setManualActionsSubtab(){},setSourceSubtab(){},setSettingsSubtab(){},syncBrowseConnectionControls(){},
    AutoSourcarrLibrary:{position:()=>context.libraryPosition||null,restorePosition:value=>libraryCalls.push(value)},
    openBrowseEntry:(id,event)=>opened.push(['browse',id,event]),openInlineEntry:(kind,id,event)=>opened.push([kind,id,event]),
  };
  context.window=context;context.globalThis=context;vm.createContext(context);vm.runInContext(nav,context);
  const navigation=context.AutoSourcarrNavigation;
  const show=tab=>{for(const node of nodes.values())if(node.classList.contains('tab'))node.classList.toggle('active',node.id===tab);};
  return {context,navigation,nodes,session,frames,opened,libraryCalls,show,
    setBusy:value=>{busyList=value;},
    runFrames:()=>{while(frames.length)frames.shift()();},
    fire:(type,event)=>{for(const fn of listeners.get(type)||[])fn(event);},
    stored:()=>JSON.parse(session.get(KEY)||'null')};
}
function boot(h){const restored=h.navigation.initialize();h.show(restored.tab);h.navigation.afterSwitch(restored.tab);return restored;}

const tests=[];const test=(name,fn)=>tests.push({name,fn});

test('normalize keeps the open entry, library page and new filters, and rejects junk',()=>{
  const {navigation}=harness(null);
  const value=navigation.normalize({version:1,tab:'subbed',controls:{sourceHealthFilter:'working',tagCatalogSearch:'isekai',riLibraryPageSize:'60',evil:'x'},
    open:{subbed:42,browse:'7',matrix:-1,settings:3},library:{signature:'{"search":"a"}',cursor:'c2:abc',previous:['','c1:zz','bad cursor']}});
  assert.equal(value.controls.sourceHealthFilter,'working');assert.equal(value.controls.tagCatalogSearch,'isekai');assert.equal(value.controls.riLibraryPageSize,'60');
  assert.equal('evil' in value.controls,false);
  assert.deepEqual({...value.open},{browse:7,subbed:42},'Only entry tabs with positive ids are kept');
  assert.equal(value.library.cursor,'c2:abc');assert.deepEqual([...value.library.previous],['','c1:zz',''],'Cursors with spaces or control characters are dropped');
  assert.equal(navigation.normalize({version:1,library:{signature:'x',cursor:'c',previous:Array(51).fill('c')}}).library,null,'Over-long cursor chains are not restored');
  assert.equal(navigation.normalize({version:1,library:{signature:'x',cursor:'c\n',previous:[]}}).library.cursor,'');
});

test('save records the open entries, the library position and the health filter',()=>{
  const h=harness(null);boot(h);h.show('subbed');
  h.context.activeInspector.subbed=42;h.context.browseOpen=true;h.context.browseState.selectedEntryId=9;
  h.context.libraryPosition={signature:'sig',cursor:'c3',previous:['','c2']};
  h.nodes.get('sourceHealthFilter').value='attention';
  h.navigation.save();
  const saved=h.stored();
  assert.equal(saved.tab,'subbed');assert.deepEqual(saved.open,{browse:9,subbed:42});
  assert.deepEqual(saved.library,{signature:'sig',cursor:'c3',previous:['','c2']});assert.equal(saved.controls.sourceHealthFilter,'attention');
  h.context.activeInspector.subbed=null;h.context.browseOpen=false;h.navigation.save();
  assert.deepEqual(h.stored().open,{},'Closing an entry is remembered too');
});

test('refresh restores the library page before its first load and reopens the entry after the list rendered',()=>{
  const saved={version:1,owner:'local:Local user',tab:'subbed',controls:{subbedSearch:'one piece'},scroll:{subbed:{x:0,y:640}},
    open:{subbed:42},library:{signature:'sig',cursor:'c3',previous:['','c2']}};
  const h=harness(saved);const restored=boot(h);
  assert.equal(restored.tab,'subbed');assert.equal(h.nodes.get('subbedSearch').value,'one piece');
  assert.equal(h.libraryCalls.length,1);assert.equal(h.libraryCalls[0].cursor,'c3','The library resumes on the saved page');
  h.runFrames();assert.equal(h.opened.length,0,'Nothing opens before the list reports it rendered');
  h.navigation.rendered('subbed');h.runFrames();
  assert.equal(h.context.scrollY,640,'Scroll is restored first');
  assert.equal(h.opened.length,1);const [kind,id,token]=h.opened[0];
  assert.equal(kind,'subbed');assert.equal(id,42);
  assert.equal(h.navigation.isRestore(token),true,'The opener gets the private restore token');
  assert.equal(h.navigation.isRestore({isTrusted:false}),false,'A forged event is not a restore');assert.equal(h.navigation.isRestore(null),false);
  h.navigation.rendered('subbed');h.runFrames();assert.equal(h.opened.length,1,'Reopened once only; later updates never reopen it');
});

test('a list that is still loading delays the reopen until it is rendered',()=>{
  const h=harness({version:1,owner:'local:Local user',tab:'browse',open:{browse:7}},{busy:true});boot(h);
  h.navigation.rendered('browse');h.runFrames();assert.equal(h.opened.length,0);
  h.setBusy(false);h.navigation.rendered('browse');h.runFrames();
  assert.deepEqual(h.opened.map(([tab,id])=>[tab,id]),[['browse',7]]);
});

test('the first real click or key press cancels a restore that is still waiting',()=>{
  const h=harness({version:1,owner:'local:Local user',tab:'matrix',open:{matrix:5}});boot(h);
  h.fire('pointerdown',{isTrusted:true});
  h.navigation.rendered('matrix');h.runFrames();assert.equal(h.opened.length,0,'The user acted first: nothing opens under them');
  h.navigation.save();assert.deepEqual(h.stored().open,{},'and the cancelled entry is not saved again');
  const synthetic=harness({version:1,owner:'local:Local user',tab:'matrix',open:{matrix:5}});boot(synthetic);
  synthetic.fire('pointerdown',{isTrusted:false});synthetic.navigation.rendered('matrix');synthetic.runFrames();
  assert.equal(synthetic.opened.length,1,'Synthetic events do not cancel it');
});

test('an entry waiting for its tab is kept when the page is saved again',()=>{
  const h=harness({version:1,owner:'local:Local user',tab:'subbed',open:{subbed:42}});boot(h);
  h.navigation.save();assert.deepEqual(h.stored().open,{subbed:42});
});

test('another account does not get the previous owner\'s position or entry',()=>{
  const h=harness({version:1,owner:'local:Local user',tab:'subbed',open:{subbed:42},library:{signature:'s',cursor:'c',previous:['']}});boot(h);
  h.navigation.identityChanged('forms:someone-else');
  assert.equal(h.libraryCalls.at(-1),null,'Library position cleared');
  h.navigation.rendered('subbed');h.runFrames();assert.equal(h.opened.length,0);
});

test('library controller resumes the saved cursor and starts again at page 1 when filters differ',async()=>{
  const nodes=new Map(),requests=[];
  const node=id=>{if(!nodes.has(id)){const n=new Node(id,'input');nodes.set(id,n);}return nodes.get(id);};
  for(const id of ['subbedSearch','subbedPublicationStatus','subbedTitleState','subbedStatus','subbedSort','subbedMinMatches','subbedEntryMatches'])node(id);
  nodes.set('subbedSource',select('subbedSource',['']));nodes.set('riLibraryPageSize',select('riLibraryPageSize',['30']));
  const context={console,JSON,Number,Object,Array,Map,Promise,URLSearchParams,AbortController,
    document:{documentElement:{dataset:{}},getElementById:node,addEventListener(){}},Option:class extends Node{constructor(text,value){super('','option');this.textContent=text;this.value=value;}},
    workbenchState:{libraryScope:'all'},subbedData:null,
    api:async url=>{requests.push(new URL('http://x'+url).searchParams);return {entries:[],sources:[],total:0,next_cursor:'',subscription:{ok:true}};},
    esc:String,apiV1:()=>({}),loadTasks:async()=>{},toast(){},queueMicrotask:fn=>fn()};
  context.window=context;vm.createContext(context);vm.runInContext(libraryController,context);
  const library=context.AutoSourcarrLibrary;
  const signature=JSON.stringify({search:'',source:'',publication:'',title_state:'',match_status:'',sort:'',source_count:'',record_count:'',scope:'all',limit:'30'});
  library.restorePosition({signature,cursor:'c3',previous:['','c2']});
  assert.deepEqual(JSON.parse(JSON.stringify(library.position())),{signature,cursor:'c3',previous:['','c2']});
  await library.load();assert.equal(requests.at(-1).get('cursor'),'c3','Same filters: the saved page loads');
  assert.match(node('riLibraryPage').textContent,/^Page 3 /);
  library.restorePosition({signature,cursor:'c3',previous:['','c2']});node('subbedSearch').value='naruto';await library.load();
  assert.equal(requests.at(-1).get('cursor'),'','Different filters start at page 1');assert.equal(library.position(),null);
  library.restorePosition(null);assert.equal(library.position(),null);
});

(async()=>{
  for(const {name,fn} of tests){await fn();await tick();console.log('PASS '+name);}
  console.log(`Navigation restore UI: ${tests.length} scenarios passed`);
})().catch(error=>{console.error(error);process.exitCode=1;});
