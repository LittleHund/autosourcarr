/* Remember only safe view state: safe view state, not form/session/credential persistence.
 * Load after all other static scripts. Credentials and edit drafts are excluded.
 * Public contract: initialize/beforeSwitch/afterSwitch/filtersReady/rendered/save.
 */
(() => {
  'use strict';
  const KEY = 'autosourcarr.navigation.v1';
  const VERSION = 1;
  const MAX_BYTES = 32768;
  const TABS = new Set(['browse','subbed','matrix','duplicates','mixed-entries','manual-actions','sources','about','imports','attention','tasks','settings','statistics']);
  const CONTROL_IDS = [
    'browseSearch','browseSource','browseConnectedSource','browseLanguage','browseMatchCount','browseSort',
    ...['matrix','subbed'].flatMap(kind => ['Search','Source','MinMatches','EntryMatches','TitleState','Status','PublicationStatus','Sort'].map(suffix => kind+suffix)),
    'duplicateSearch','duplicateSize','mixedSearch','mixedFilter','manualActionSearch',
    'sourceAvailability','sourceExtensionSearch','sourceExtensionLanguage','sourceOrphanSearch','sourceInstalledOrphanSearch',
    'settingsSearch',
  ];
  // Only these selects legitimately have no matching <option> until data loads.
  const DEFERRED_OPTIONS = new Set(['browseSource','browseConnectedSource','browseLanguage','browseMatchCount','matrixSource','subbedSource','matrixMinMatches','subbedMinMatches','matrixEntryMatches','subbedEntryMatches','sourceExtensionLanguage']);
  const STATIC_DETAILS = {
    matrixFilters:'#matrix details.advancedFilters',
    subbedFilters:'#subbed details.advancedFilters',
    browseFilters:'#browse details.advancedFilters',
    importFilters:'#imports details.importAdvancedFilters',
  };
  const DETAIL_SECTIONS = new Set(['titles','tags','metadata','matches','chapters','history','served']);
  const own = (object,key) => Object.prototype.hasOwnProperty.call(object,key);
  const plain = value => value && typeof value === 'object' && !Array.isArray(value);
  const text = (value,max=300) => typeof value === 'string' ? value.slice(0,max) : '';
  const integer = (value,min,max,fallback=min) => Number.isSafeInteger(Number(value)) && Number(value)>=min && Number(value)<=max ? Number(value) : fallback;
  const id = value => integer(value,1,Number.MAX_SAFE_INTEGER,0) || null;
  const choice = (value,allowed,fallback) => allowed.includes(value) ? value : fallback;
  const element = id => document.getElementById(id);
  const currentTab = () => document.querySelector('main > .tab.active')?.id || 'browse';

  // Pure normalization supports deterministic tests and rejects unknown keys.
  function normalize(raw) {
    if(!plain(raw) || raw.version!==VERSION) return null;
    const result = {version:VERSION,owner:text(raw.owner,512),tab:TABS.has(raw.tab)?raw.tab:'browse',controls:{},scroll:{},details:{},entrySections:{}};
    for(const key of CONTROL_IDS) if(plain(raw.controls) && own(raw.controls,key) && typeof raw.controls[key]==='string') result.controls[key]=text(raw.controls[key],300);
    for(const tab of TABS) if(plain(raw.scroll?.[tab])) result.scroll[tab]={x:integer(raw.scroll[tab].x,0,16000000),y:integer(raw.scroll[tab].y,0,16000000)};
    for(const key of Object.keys(STATIC_DETAILS)) if(typeof raw.details?.[key]==='boolean') result.details[key]=raw.details[key];
    for(const kind of ['browse','matrix','subbed']) {
      const values={}; for(const key of DETAIL_SECTIONS) if(typeof raw.entrySections?.[kind]?.[key]==='boolean') values[key]=raw.entrySections[kind][key];
      result.entrySections[kind]=values;
    }
    result.browse={mode:choice(raw.browse?.mode,['POPULAR','LATEST','SEARCH'],'POPULAR'),page:integer(raw.browse?.page,1,100000,1)};
    result.imports={runId:text(raw.imports?.runId,128),filter:choice(raw.imports?.filter,['all','unresolved','added','existing','failed','skipped'],'all'),review:choice(raw.imports?.review,['all','has_candidates','no_candidates','searching','has_reference','no_reference','has_english','no_english'],'all'),offset:Math.floor(integer(raw.imports?.offset,0,5000000)/50)*50,selected:id(raw.imports?.selected)};
    result.duplicates={scope:choice(raw.duplicates?.scope,['subbed','unsubbed'],'subbed'),left:id(raw.duplicates?.left),right:id(raw.duplicates?.right)};
    const matchingTarget=text(raw.matching?.target,150);
    result.matching={view:choice(raw.matching?.view,['possible','history'],'possible'),query:text(raw.matching?.query,200),offset:Math.floor(integer(raw.matching?.offset,0,1000000)/50)*50,target:/^(?:source|group):[^:\s<>]{1,128}$/.test(matchingTarget)?matchingTarget:'',open:raw.matching?.open===true};
    result.mixed={offset:Math.floor(integer(raw.mixed?.offset,0,5000000)/50)*50,selected:id(raw.mixed?.selected)};
    result.manualSubtab=choice(raw.manualSubtab,['matches','titles','separate','chapters','duplicates','tags'],'matches');
    result.sourceSubtab=choice(raw.sourceSubtab,['installed','manage','repositories','maintenance','health'],'installed');
    result.settingsSubtab=choice(raw.settingsSubtab,['customization','configuration','browser','services'],'customization');
    result.taskLane=choice(raw.taskLane,['all','manual','automatic'],'all');
    result.libraryScope=choice(raw.libraryScope,['all','unmatched','missing','missing_downloads','source_gaps','verification','monitored','catalog'],'all');
    return result;
  }
  function owner() {try{return localStorage.getItem('autosourcarr.preferenceOwner')||'';}catch(_){return '';}}
  function read() {
    try {const raw=sessionStorage.getItem(KEY);return raw && raw.length<=MAX_BYTES ? normalize(JSON.parse(raw)) : null;} catch(_){return null;}
  }
  let saved=null, booted=false, started=false, applying=false, lastTab='browse';
  let pendingScroll=null, scheduled=null, saveTimer=null, sourceRestorePending=false;
  let revision=0;
  let initialControls={};const touchedControls=new Set();
  function cancelPositionRestore(){revision++;pendingScroll=null;if(scheduled!==null){cancelAnimationFrame(scheduled);scheduled=null;}}
  function scheduleSave(){if(!started || applying)return;clearTimeout(saveTimer);saveTimer=setTimeout(save,200);}

  function capture() {
    const result=saved || normalize({version:VERSION});
    result.owner=owner();result.tab=currentTab();
    for(const key of CONTROL_IDS){const control=element(key);if(control)result.controls[key]=text(control.value,300);}
    // Preserve pending desired position while the initial render is incomplete.
    if(!pendingScroll || pendingScroll.tab!==result.tab)result.scroll[result.tab]={x:Math.max(0,Math.round(window.scrollX||0)),y:Math.max(0,Math.round(window.scrollY||0))};
    for(const [key,selector] of Object.entries(STATIC_DETAILS)){const detail=document.querySelector(selector);if(detail)result.details[key]=!!detail.open;}
    for(const kind of ['browse','matrix','subbed']) {
      const root=element(kind==='browse'?'browseInspectorBody':kind+'Inspector');
      for(const detail of root?.querySelectorAll('[data-detail-section]')||[]){const key=detail.dataset.detailSection;if(DETAIL_SECTIONS.has(key))result.entrySections[kind][key]=!!detail.open;}
    }
    result.browse={mode:browseState.mode,page:browseState.page};
    result.imports={runId:importsState.runId,filter:importsState.filter,review:importsState.review,offset:importsState.offset,selected:workbenchState.importId||id(String(workbenchState.importKey||'').split('|').pop())};
    result.duplicates={scope:duplicateScope,left:workbenchState.duplicateLeft,right:workbenchState.duplicateRight};
    result.matching=globalThis.AutoSourcarrMatchingReview?.getViewState()||result.matching;
    if(typeof mixedEntriesState!=='undefined')result.mixed={offset:mixedEntriesState.offset,selected:mixedEntriesState.selected};
    result.manualSubtab=manualActionsSubtab;
    result.sourceSubtab=readLocalUiPreference('sourcesSubtab')||'installed';
    result.settingsSubtab=readLocalUiPreference('settingsSubtab')||'customization';
    result.taskLane=workbenchState.taskLane;result.libraryScope=workbenchState.libraryScope;
    return normalize(result);
  }
  function save(){if(!started || applying)return;saved=capture();try{sessionStorage.setItem(KEY,JSON.stringify(saved));}catch(_){}}

  function restoreControl(key,value,{placeholder=false}={}) {
    const control=element(key);if(!control)return;
    if(control.tagName==='SELECT' && !Array.from(control.options).some(option=>option.value===value)) {
      if(!placeholder || !DEFERRED_OPTIONS.has(key))return;
      const option=document.createElement('option');option.value=value;option.textContent=value+' · saved filter';option.dataset.navigationPlaceholder='true';control.append(option);
    }
    control.value=value;
  }
  function applyChips(selector,datasetKey,value){document.querySelectorAll(selector).forEach(button=>{const active=button.dataset[datasetKey]===value;button.classList.toggle('active',active);button.setAttribute('aria-pressed',String(active));});}
  function applyReadOnlyDetails(){
    if(!saved)return;
    for(const [key,selector] of Object.entries(STATIC_DETAILS)){const detail=document.querySelector(selector);if(detail && own(saved.details,key))detail.open=saved.details[key];}
  }
  function initialize() {
    if(started)return {tab:lastTab,needsSourceInventory:false};
    saved=read();
    if(saved?.owner && owner() && saved.owner!==owner())saved=null;
    started=true;applying=true;initialControls={...(saved?.controls||{})};
    if(saved) {
      for(const [key,value] of Object.entries(saved.controls))restoreControl(key,value,{placeholder:true});
      Object.assign(browseState,saved.browse);setBrowseMode(saved.browse.mode);
      Object.assign(importsState,{runId:saved.imports.runId,filter:saved.imports.filter,review:saved.imports.review,offset:saved.imports.offset});
      workbenchState.importKey=saved.imports.selected?saved.imports.runId+'|'+saved.imports.selected:'';
      duplicateScope=saved.duplicates.scope;syncDuplicateScopeButtons();
      workbenchState.duplicateLeft=saved.duplicates.left;workbenchState.duplicateRight=saved.duplicates.right;
      globalThis.AutoSourcarrMatchingReview?.restoreViewState(saved.matching);
      if(typeof mixedEntriesState!=='undefined')Object.assign(mixedEntriesState,saved.mixed);
      workbenchState.taskLane=saved.taskLane;workbenchState.libraryScope=saved.libraryScope;
      applyChips('[data-task-lane]','taskLane',saved.taskLane);applyChips('[data-library-scope]','libraryScope',saved.libraryScope);
      document.querySelectorAll('.importStateFilter').forEach(button=>button.classList.toggle('active',button.dataset.state===saved.imports.filter));
      document.querySelectorAll('.importReviewFilter').forEach(button=>button.classList.toggle('active',button.dataset.review===saved.imports.review));
      // These setters must be side-effect-free when their parent tab is inactive.
      setManualActionsSubtab(saved.manualSubtab);setSourceSubtab(saved.sourceSubtab,false);setSettingsSubtab(saved.settingsSubtab,false);
      applyReadOnlyDetails();
      lastTab=saved.tab;
      if(saved.scroll[lastTab])pendingScroll={tab:lastTab,position:saved.scroll[lastTab],revision};
      sourceRestorePending=true;
    } else saved=normalize({version:VERSION,owner:owner()});
    applying=false;
    // Trusted interaction cancels late scroll restoration, never a background poll.
    for(const type of ['pointerdown','keydown','wheel','touchstart'])document.addEventListener(type,event=>{if(event.isTrusted)cancelPositionRestore();},{capture:true,passive:true});
    // Capture only allowlisted controls; never serialize arbitrary form contents.
    for(const type of ['input','change'])document.addEventListener(type,event=>{if(CONTROL_IDS.includes(event.target?.id)){revision++;touchedControls.add(event.target.id);scheduleSave();}},{capture:true});
    document.addEventListener('click',()=>queueMicrotask(scheduleSave));
    document.addEventListener('toggle',event=>{if(event.target?.matches?.('details'))scheduleSave();},{capture:true});
    window.addEventListener('scroll',scheduleSave,{passive:true});
    window.addEventListener('pagehide',save);
    new MutationObserver(()=>{if(pendingScroll)rendered();}).observe(document.querySelector('main'),{subtree:true,childList:true,attributes:true,attributeFilter:['aria-busy']});
    return {tab:lastTab,needsSourceInventory:lastTab==='browse' && !element('browseConnectedSource')?.value && !!(element('browseSource')?.value || element('browseLanguage')?.value)};
  }

  function beforeSwitch(tab) {
    if(!started || !TABS.has(tab))return;
    if(booted){save();cancelPositionRestore();}
    // First initialization has already provided the desired saved position.
  }
  function afterSwitch(tab) {
    if(!started || !TABS.has(tab))return;
    lastTab=tab;booted=true;saved.tab=tab;
    if(!pendingScroll && saved.scroll[tab])pendingScroll={tab,position:saved.scroll[tab],revision};
    if(!pendingScroll && !saved.scroll[tab])pendingScroll={tab,position:{x:0,y:0},revision};
    rendered(tab);scheduleSave();
  }
  function filtersReady() {
    if(!sourceRestorePending || !saved)return;
    applying=true;
    for(const key of DEFERRED_OPTIONS)if(own(initialControls,key) && !touchedControls.has(key))restoreControl(key,initialControls[key],{placeholder:true});
    syncBrowseConnectionControls();applying=false;sourceRestorePending=false;
  }
  function rendered(tab=currentTab()) {
    // Call after asynchronous lists/tables finish, not only after their request.
    if(!pendingScroll || pendingScroll.tab!==tab || currentTab()!==tab || pendingScroll.revision!==revision)return;
    if(document.querySelector('#'+tab+' [aria-busy="true"]'))return;
    if(scheduled!==null)cancelAnimationFrame(scheduled);
    scheduled=requestAnimationFrame(()=>{
      scheduled=null;if(!pendingScroll || currentTab()!==tab || pendingScroll.revision!==revision)return;
      const {x,y}=pendingScroll.position;
      window.scrollTo({left:x,top:y,behavior:'instant'});
      // Retain the request when height is still growing; a later render retries.
      if(Math.abs((window.scrollY||0)-y)<=2)pendingScroll=null;
    });
  }
  function entrySectionsRendered(kind) {
    // Restores section expansion only; deliberately does not reopen dialogs or
    // forge the isTrusted event required by existing inspector-open controls.
    const values=saved?.entrySections?.[kind];if(!values)return;
    const root=element(kind==='browse'?'browseInspectorBody':kind+'Inspector');
    applying=true;for(const detail of root?.querySelectorAll('[data-detail-section]')||[]){const key=detail.dataset.detailSection;if(own(values,key))detail.open=values[key];}applying=false;
  }
  function identityChanged(newOwner) {
    if(!saved || !saved.owner || saved.owner===newOwner){if(saved)saved.owner=text(newOwner,512);return;}
    cancelPositionRestore();sourceRestorePending=false;
    try{sessionStorage.removeItem(KEY);}catch(_){}
    // Root should also clear restored search controls if identity differs before
    // results are presented. This method must not issue requests or wipe drafts.
    for(const key of CONTROL_IDS){const el=element(key);if(el)el.value=el.tagName==='SELECT'?(Array.from(el.options).find(o=>o.defaultSelected)||el.options[0])?.value||'':'';}
    browseState.items=[];browseState.page=1;browseState.mode='POPULAR';matrixData=null;subbedData=null;duplicateData=null;
    Object.assign(importsState,{runId:'',filter:'all',review:'all',offset:0});
    workbenchState.importKey='';workbenchState.importId=null;workbenchState.duplicateLeft=null;workbenchState.duplicateRight=null;
    globalThis.AutoSourcarrMatchingReview?.restoreViewState(null);
    if(typeof mixedEntriesState!=='undefined')Object.assign(mixedEntriesState,{offset:0,selected:null});
    saved=normalize({version:VERSION,owner:newOwner,tab:currentTab()});
  }
  window.AutoSourcarrNavigation={get started(){return started;},initialize,beforeSwitch,afterSwitch,filtersReady,rendered,entrySectionsRendered,identityChanged,save,normalize};
})();
