const UI_BUILD_VERSION=(()=>{const source=document.currentScript?.src||document.querySelector('script[src*="/static/app.js"]')?.src||'';return source.match(/[?&]v=(\d+\.\d+\.\d+)(?=&|$)/)?.[1]||'unknown';})();
const $ = s => document.querySelector(s);
let token = '';
let adminAuthDismissed = false;
try { token = localStorage.getItem('sc_admin_token') || ''; } catch (_) {}
let sourceRows = [];
let sourceGroups = [];
let sourceGroupDefinitions = [];
let sourceExtensionState = {loaded:false,loading:false,extensions:[],stores:[]};
let matrixData = null;
let subbedData = null;
let duplicateData = null;
let duplicateScope = 'subbed';
try { duplicateScope = localStorage.getItem('autosourcarr_duplicate_scope') === 'unsubbed' ? 'unsubbed' : 'subbed'; } catch (_) {}
let browseState = { mode: 'POPULAR', page: 1, hasNext: false, items: [], loading: false, renderNonce: 0, selectedEntryId: null, scrollY: 0 };
let activeInspector = { matrix: null, subbed: null };
function restoreCoverRevisions(key){
  try{return new Map(JSON.parse(sessionStorage.getItem(key)||'[]').filter(pair=>Array.isArray(pair)&&Number.isSafeInteger(pair[0])&&pair[0]>0&&Number.isFinite(pair[1])&&pair[1]>0).slice(-500));}catch(_){return new Map();}
}
function saveCoverRevisions(key,revisions){
  while(revisions.size>500)revisions.delete(revisions.keys().next().value);
  try{sessionStorage.setItem(key,JSON.stringify([...revisions]));}catch(_){}
}
let coverRenderNonce = Date.now();
try{const saved=Number(sessionStorage.getItem('autosourcarr.coverEpoch'));if(saved>0&&Number.isFinite(saved))coverRenderNonce=saved;else sessionStorage.setItem('autosourcarr.coverEpoch',String(coverRenderNonce));}catch(_){}
function renewCoverEpoch(){coverRenderNonce=Date.now();try{sessionStorage.setItem('autosourcarr.coverEpoch',String(coverRenderNonce));}catch(_){}return coverRenderNonce;}
const entryCoverRevisions = restoreCoverRevisions('autosourcarr.entryCoverRevisions');
const sourceCoverRevisions = restoreCoverRevisions('autosourcarr.sourceCoverRevisions');
const knownEntryThumbnails = new Map();
const knownEntryCoverReady = new Map();
let metadataLiveTimer = null;
let duplicateRetryTimer = null;
let duplicateRefreshTimer = null;
let duplicateHeartbeatTimer = null;
let matchBrowserState = null;
let matchBrowserGeneration = 0;
let matchMutationSequence = 0;
const matchEntryMutationRevisions = new Map();
const matchEntryMutationPending = new Map();
let backupUploadPending = null;
let matchSearchAbort = null;
let matchEnrichAbort = null;
const entryInspectorAbort = { matrix: null, subbed: null };
const entryInspectorGeneration = { matrix: 0, subbed: 0 };
const inspectorDetailRequests = new WeakMap();
let browseInspectorGeneration = 0;
let browseInspectorAbort = null;
const inspectorScrollPositions = {};
const deferredUiRenders = new Set();
let uiGestureUntil = 0;
let uiGestureFlushTimer = null;
let settingsState = { data: null, activeFile: 'autosourcarr', dirty: false, loadPromise:null, editRevision:0, saving:false };
let databaseStorageState = null;
let tasksState = { running: 0, items: [] };
let tasksLoadPromise = null;
let tasksLastSuccess = 0;
let tasksPollFailures = 0;
let tasksPollTimer = null;
let importsState = { runs: [], runId: '', filter: 'all', review: 'all', items: [], enrichmentPending: 0, offset: 0, limit: 50, total: 0, reviewCounts: null, loadedKey: '', loading: false, error: '', failures: 0, revision: 0 };
let importsListRequest = null;
const importItemRequests = new Map();
const importSearchRequests = new Map();
const matrixRequests = {matrix: new Map(), subbed: new Map()};
const matrixStatsRequests={matrix:null,subbed:null},matrixViewRetry={matrix:null,subbed:null};
let catalogViewRevision=0;
const matrixRecordReload = {matrix: null, subbed: null};
function activeSurface(name){return !!document.querySelector('#'+name)?.classList.contains('active');}
function importViewKey(){return [importsState.revision,importsState.runId,importsState.filter,importsState.review,importsState.offset].join('|');}

let manualGroupsState = { groups: [], individual: [], group_count: 0, manual_match_count: 0 };
let manualActionsState = { matches: manualGroupsState, titles: [], separate_entries: [], rejected_chapters: [], duplicate_decisions: [], counts: {} };
let manualActionsSubtab = 'matches';
let manualActionsGeneration = 0;
let manualActionsMutationPending = 0;
let manualActionsRequest = null;
let manualActionsReconcileTimer = null;
let manualActionsNeedsReconcile = false;
let importsPollTimer = null;
let importsPendingPollTimer = null;
const pendingImportActions = new Map();
let lastHealthSnapshot = null;
let clientHealthError = null;
let clientErrors = [];
let lastBackendErrors = {active_count:0,active_error_count:0,active_warning_count:0,items:[]};
let duplicateHydrateRetry = {matrix:null,subbed:null};
const duplicateSnapshotRequests=new Map();
const duplicatePollAttempts={matrix:0,subbed:0};
const entrySyncTimers = new Map();
let uiPreferencesState = {loaded:false,user:'',authelia:false,identityHeader:'',identityDiagnostics:{},values:{},saveTimer:null,pending:{}};
const UI_PREF_LOCAL_KEYS = {
  theme:'autosourcarr.theme',
  themeStyle:'autosourcarr.themeStyle',
  cardFill:'autosourcarr.cardFill',
  accentStrength:'autosourcarr.accentStrength',
  thumbnailScale:'autosourcarr.thumbnailScale',
  matchedThumbnailScale:'autosourcarr.matchedThumbnailScale',
  secondaryColor:'autosourcarr.secondaryColor',
  tertiaryColor:'autosourcarr.tertiaryColor',
  colorRolesVersion:'autosourcarr.colorRolesVersion',
  customizationSections:'autosourcarr.customizationSections',
  browsePageSize:'autosourcarr.browsePageSize',
  duplicateScope:'autosourcarr_duplicate_scope',
  sourcesSubtab:'autosourcarr_sources_subtab',
  sourceSections:'autosourcarr_source_sections',
  density:'autosourcarr_density',
  settingsSubtab:'autosourcarr_settings_subtab',
  customColor:'autosourcarr_custom_color',
};
const UI_THEMES = new Set(['blue','amber','emerald','violet','rose','cyan','custom']);
const DEFAULT_CUSTOM_COLOR='#ff2d78';
const CUSTOM_THEME_VARS=['--bg','--panel','--panel2','--chrome','--control','--control-hover','--surface-highlight','--line','--accent','--accent-soft','--accent-border','--accent-text','--accent-chip','--progress'];
function readLocalUiPreference(key){
  const storageKey=UI_PREF_LOCAL_KEYS[key];if(!storageKey)return undefined;
  try{const raw=localStorage.getItem(storageKey);if(raw===null)return undefined;if(['sourceSections','customizationSections'].includes(key)){try{return JSON.parse(raw);}catch(_){return undefined;}}return raw;}catch(_){return undefined;}
}
function writeLocalUiPreference(key,value){
  const storageKey=UI_PREF_LOCAL_KEYS[key];if(!storageKey)return;
  try{if(value===null||value===undefined||value==='')localStorage.removeItem(storageKey);else localStorage.setItem(storageKey,['sourceSections','customizationSections'].includes(key)?JSON.stringify(value):String(value));}catch(_){}
}
function normalizeHexColor(value){
  let raw=String(value||'').trim().replace(/^#/,'');
  if(/^[0-9a-fA-F]{3}$/.test(raw))raw=raw.split('').map(x=>x+x).join('');
  if(!/^[0-9a-fA-F]{6}$/.test(raw))return '';
  return `#${raw.toUpperCase()}`;
}
function hexToRgb(value){const hex=normalizeHexColor(value);if(!hex)return null;return {r:parseInt(hex.slice(1,3),16),g:parseInt(hex.slice(3,5),16),b:parseInt(hex.slice(5,7),16)};}
function rgbToHex(r,g,b){const clamp=v=>Math.max(0,Math.min(255,Number(v)||0));return '#'+[r,g,b].map(v=>Math.round(clamp(v)).toString(16).padStart(2,'0')).join('').toUpperCase();}
function mixRgb(rgb,target,amount){const a=Math.max(0,Math.min(1,Number(amount)||0));return {r:Math.round(rgb.r+(target.r-rgb.r)*a),g:Math.round(rgb.g+(target.g-rgb.g)*a),b:Math.round(rgb.b+(target.b-rgb.b)*a)};}
function rgbCss(rgb){return `rgb(${rgb.r} ${rgb.g} ${rgb.b})`;}
function syncCustomColorInputs(hex){const rgb=hexToRgb(hex)||hexToRgb(DEFAULT_CUSTOM_COLOR);const normalized=rgbToHex(rgb.r,rgb.g,rgb.b);if($('#customThemePicker'))$('#customThemePicker').value=normalized;if($('#customThemeHex'))$('#customThemeHex').value=normalized;if($('#customThemeR'))$('#customThemeR').value=rgb.r;if($('#customThemeG'))$('#customThemeG').value=rgb.g;if($('#customThemeB'))$('#customThemeB').value=rgb.b;if($('#customThemePreview'))$('#customThemePreview').style.background=normalized;return normalized;}
function applyCustomPalette(value){
  const hex=syncCustomColorInputs(normalizeHexColor(value)||DEFAULT_CUSTOM_COLOR);
  writeLocalUiPreference('customColor',hex);
  applyAppearancePalette(document.documentElement.dataset.themeStyle||readLocalUiPreference('themeStyle')||'neutral','custom',hex,readLocalUiPreference('secondaryColor'));
  return hex;
}
function clearCustomPalette(){for(const key of CUSTOM_THEME_VARS)document.documentElement.style.removeProperty(key);}
function applyTheme(theme,customColor){
  const name=UI_THEMES.has(String(theme||''))?String(theme):'blue';
  clearCustomPalette();
  document.documentElement.dataset.theme=name;writeLocalUiPreference('theme',name);
  const hex=customColor||readLocalUiPreference('customColor')||DEFAULT_CUSTOM_COLOR;
  syncCustomColorInputs(hex);
  applyAppearancePalette(readLocalUiPreference('themeStyle')||'neutral',name,hex,readLocalUiPreference('secondaryColor'));
  document.querySelectorAll('.themeChoice').forEach(b=>{const active=b.dataset.theme===name;b.classList.toggle('active',active);b.setAttribute('aria-checked',String(active));});
  const customPanel=$('#customThemeControls');if(customPanel)customPanel.hidden=false;
  const presetColors={blue:'#7DB0FF',amber:'#F1BD55',emerald:'#61D49A',violet:'#B69AFF',rose:'#F08CA5',cyan:'#62D4DF'};
  syncCustomColorInputs(presetColors[name]||hex);
  if($('#customThemePreview'))$('#customThemePreview').style.background=hex;
}
function applyThemeStyle(value,save=false){
  const name=normalizeThemeStyle(value);writeLocalUiPreference('themeStyle',name);
  applyTheme(readLocalUiPreference('theme')||'blue',readLocalUiPreference('customColor'));
  if(save)queueUiPreferenceSave('themeStyle',name);
}
applyTheme(readLocalUiPreference('theme')||'blue',readLocalUiPreference('customColor')||DEFAULT_CUSTOM_COLOR);
function applyDensity(value){const density=String(value||'')==='compact'?'compact':'comfortable';document.documentElement.dataset.density=density;writeLocalUiPreference('density',density);document.querySelectorAll('.densityChoice').forEach(b=>{const active=b.dataset.density===density;b.classList.toggle('active',active);b.setAttribute('aria-checked',active?'true':'false');});}
function setSettingsSubtab(value,save=true){
  const name=['configuration','browser','services'].includes(value)?value:'customization';
  document.querySelectorAll('.settingsSubtab').forEach(b=>{b.classList.toggle('active',b.dataset.settingsSubtab===name);b.setAttribute('aria-selected',String(b.dataset.settingsSubtab===name));});
  for(const [key,id] of [['customization','settingsCustomizationPane'],['configuration','settingsConfigurationPane'],['browser','settingsBrowserPane'],['services','settingsServicesPane']]){const pane=document.getElementById(id);if(pane){pane.hidden=name!==key;pane.classList.toggle('active',name===key);}}
  document.querySelectorAll('.settingsConfigAction').forEach(x=>x.hidden=name!=='configuration');
  writeLocalUiPreference('settingsSubtab',name);if(save&&uiPreferencesState.loaded)queueUiPreferenceSave('settingsSubtab',name);
  if(document.querySelector('#settings.active'))ensureSettingsPane().catch(e=>toast(e.message));
}
applyDensity(readLocalUiPreference('density')||'comfortable');
function updateUiPreferenceOwner(){
  const el=$('#uiPreferenceUser'),state=$('#uiIdentityState'),diag=$('#uiIdentityDiagnostics');
  if(!uiPreferencesState.loaded){if(el)el.textContent='Loading user preferences…';if(state)state.textContent='Checking Remote-User / Remote-Email / Remote-Groups…';return;}
  const d=uiPreferencesState.identityDiagnostics||{};
  if(uiPreferencesState.authelia){
    if(el)el.textContent=`Saved for ${uiPreferencesState.user} via ${uiPreferencesState.identityHeader||'Remote-User'}`;
    if(state)state.textContent='Authelia user detected';
    if(diag){const groups=Array.isArray(d.remote_groups)&&d.remote_groups.length?` Groups: ${d.remote_groups.join(', ')}.`:'';const email=d.remote_email?` Email: ${d.remote_email}.`:'';diag.textContent=`User-specific preferences are active using the existing NPM Authelia headers.${email}${groups}`;}
    return;
  }
  if(el)el.textContent='Shared local profile · Authelia username header not detected';
  if(d.direct_access){
    if(state)state.textContent='Direct backend access · Authelia bypassed';
    if(diag)diag.textContent=`This request reached AutoSourcarr directly at ${d.host||'the backend address'}. Your NGINX block only adds Remote-User, Remote-Email and Remote-Groups when you open AutoSourcarr through the Authelia/Nginx Proxy Manager hostname. Use that proxied URL for per-user preferences.`;
    return;
  }
  if(state)state.textContent=d.proxy_detected?'Proxy detected · Remote-User missing':'No forwarded identity headers detected';
  if(diag){const headers=Array.isArray(d.proxy_headers)&&d.proxy_headers.length?` Proxy-related headers received: ${d.proxy_headers.join(', ')}.`:'';const custom=d.custom_header?` Configured AUTHELIA_USER_HEADER=${d.custom_header}.`:'';diag.textContent=`AutoSourcarr expects the existing headers Remote-User, Remote-Email and Remote-Groups from your NPM auth_request block, but no authenticated username reached this request.${headers}${custom}`;}
}
function queueUiPreferenceSave(key,value){
  uiPreferencesState.revisions=uiPreferencesState.revisions||{};
  uiPreferencesState.revisions[key]=(uiPreferencesState.revisions[key]||0)+1;
  uiPreferencesState.values[key]=value;uiPreferencesState.pending[key]=value;writeLocalUiPreference(key,value);
  uiPreferencesState.saveFailures=0;
  if(typeof updatePreferenceSaveState==='function')updatePreferenceSaveState();
  if(uiPreferencesState.saveTimer)clearTimeout(uiPreferencesState.saveTimer);
  if(!uiPreferencesState.saving)uiPreferencesState.saveTimer=setTimeout(flushUiPreferenceSave,300);
}
async function flushUiPreferenceSave(){
  uiPreferencesState.saveTimer=null;
  if(uiPreferencesState.saving)return;
  const values={...uiPreferencesState.pending};
  if(!Object.keys(values).length)return;
  uiPreferencesState.pending={};uiPreferencesState.saving=true;
  try{
    const r=await api('/api/admin/ui-preferences',{method:'PUT',body:JSON.stringify({values}),timeoutMs:6000,noMutationRefresh:true,suppressClientError:true});
    uiPreferencesState.values={...(r.values||uiPreferencesState.values),...uiPreferencesState.pending};
    uiPreferencesState.user=String(r.user||uiPreferencesState.user);uiPreferencesState.authelia=!!r.authelia;
    uiPreferencesState.identityHeader=String(r.identity_header||uiPreferencesState.identityHeader);
    uiPreferencesState.identityDiagnostics=r.identity_diagnostics||uiPreferencesState.identityDiagnostics;
    uiPreferencesState.saveFailures=0;updateUiPreferenceOwner();
  }catch(e){
    // Changes made during the failed request always win over its older snapshot.
    uiPreferencesState.pending={...values,...uiPreferencesState.pending};
    uiPreferencesState.saveFailures=(uiPreferencesState.saveFailures||0)+1;
    rememberClientError('Unable to save user UI preferences',{component:'User preferences',path:'/api/admin/ui-preferences',method:'PUT',severity:'warning',detail:String(e?.message||e)});
  }finally{
    uiPreferencesState.saving=false;
    if(typeof updatePreferenceSaveState==='function')updatePreferenceSaveState();
    if(Object.keys(uiPreferencesState.pending).length&&(uiPreferencesState.saveFailures||0)<5){
      const delay=uiPreferencesState.saveFailures?Math.min(30000,1000*2**uiPreferencesState.saveFailures):300;
      uiPreferencesState.saveTimer=setTimeout(flushUiPreferenceSave,delay);
    }
  }
}
function applySourceSectionPreferences(){
  const prefs=uiPreferencesState.values.sourceSections||readLocalUiPreference('sourceSections')||{};
  document.querySelectorAll('.sourceExtensionSection[data-source-section]').forEach(section=>{const key=section.dataset.sourceSection;if(Object.prototype.hasOwnProperty.call(prefs,key))section.open=!!prefs[key];});
}
function currentSourceSectionPreferences(){
  const out={};document.querySelectorAll('.sourceExtensionSection[data-source-section]').forEach(section=>{out[section.dataset.sourceSection]=!!section.open;});return out;
}
function applyUiPreferences(values){
  const prefs=values||{};
  const customColor=normalizeHexColor(prefs.customColor||readLocalUiPreference('customColor')||DEFAULT_CUSTOM_COLOR)||DEFAULT_CUSTOM_COLOR;writeLocalUiPreference('customColor',customColor);syncCustomColorInputs(customColor);
  writeLocalUiPreference('accentStrength',normalizeAccentStrength(prefs.accentStrength??readLocalUiPreference('accentStrength')));
  writeLocalUiPreference('cardFill',normalizeCardFill(prefs.cardFill??readLocalUiPreference('cardFill')));
  writeLocalUiPreference('themeStyle',normalizeThemeStyle(prefs.themeStyle||readLocalUiPreference('themeStyle')));
  writeLocalUiPreference('secondaryColor',normalizeHexColor(prefs.secondaryColor||readLocalUiPreference('secondaryColor'))||'#EEB392');
  writeLocalUiPreference('tertiaryColor',normalizeHexColor(prefs.tertiaryColor||readLocalUiPreference('tertiaryColor'))||'#EEB392');
  if(prefs.colorRolesVersion!==undefined)writeLocalUiPreference('colorRolesVersion',prefs.colorRolesVersion);
  if(prefs.customizationSections!==undefined)writeLocalUiPreference('customizationSections',prefs.customizationSections);
  applyCustomizationSections();
  applyTheme(prefs.theme||readLocalUiPreference('theme')||'blue',customColor);
  applyDensity(prefs.density||readLocalUiPreference('density')||'comfortable');
  for(const key of ['thumbnailScale','matchedThumbnailScale'])writeLocalUiPreference(key,normalizeThumbnailScale(prefs[key]??readLocalUiPreference(key),key));
  applyThumbnailSizes();
  if(prefs.browsePageSize!==undefined)writeLocalUiPreference('browsePageSize',prefs.browsePageSize);
  if(!globalThis.AutoSourcarrNavigation?.started&&prefs.duplicateScope!==undefined){duplicateScope=prefs.duplicateScope==='unsubbed'?'unsubbed':'subbed';writeLocalUiPreference('duplicateScope',duplicateScope);}
  if(!globalThis.AutoSourcarrNavigation?.started&&prefs.sourcesSubtab!==undefined)writeLocalUiPreference('sourcesSubtab',['manage','repositories','maintenance','orphaned','installed-orphan'].includes(prefs.sourcesSubtab)?prefs.sourcesSubtab:'installed');
  if(prefs.sourceSections!==undefined)writeLocalUiPreference('sourceSections',prefs.sourceSections);
  applySourceSectionPreferences();
  syncDuplicateScopeButtons();
  const pageSize=readLocalUiPreference('browsePageSize');if(pageSize&&$('#browsePageSize')){$('#browsePageSize').value=pageSize;if($('#browsePageSizePreset')&&['25','50','100','200','500'].includes(pageSize))$('#browsePageSizePreset').value=pageSize;}
  if(!globalThis.AutoSourcarrNavigation?.started){
    setSourceSubtab(readLocalUiPreference('sourcesSubtab')||'installed',false);
    setSettingsSubtab(prefs.settingsSubtab||readLocalUiPreference('settingsSubtab')||'customization',false);
  }
}
async function loadUiPreferences(){
  const startedRevisions={...(uiPreferencesState.revisions||{})};
  const local={};for(const key of Object.keys(UI_PREF_LOCAL_KEYS)){const value=readLocalUiPreference(key);if(value!==undefined)local[key]=value;}
  const r=await api('/api/admin/ui-preferences',{timeoutMs:6000,noMutationRefresh:true,suppressClientError:true,cache:'no-store'});
  const server=(r.values&&typeof r.values==='object')?r.values:{};
  const owner=(r.authelia?'authelia:':'local:')+String(r.user||'');let previousOwner='';
  try{previousOwner=localStorage.getItem('autosourcarr.preferenceOwner')||'';}catch(_){}
  const switched=!!previousOwner&&previousOwner!==owner;
  if(switched)globalThis.AutoSourcarrNavigation?.identityChanged(owner);
  if(switched){for(const key of Object.keys(UI_PREF_LOCAL_KEYS)){writeLocalUiPreference(key,null);delete local[key];}}
  const merged={...(switched?{theme:'blue',themeStyle:'neutral',cardFill:'accent',accentStrength:25,thumbnailScale:100,matchedThumbnailScale:125,secondaryColor:'#EEB392',customColor:DEFAULT_CUSTOM_COLOR,density:'comfortable',duplicateScope:'subbed',sourcesSubtab:'installed',sourceSections:{},settingsSubtab:'customization',customizationSections:{},browsePageSize:'50'}:{}),...server};const migrate={};
  try{localStorage.setItem('autosourcarr.preferenceOwner',owner);}catch(_){}
  for(const [key,value] of Object.entries(local)){if(merged[key]===undefined){merged[key]=value;migrate[key]=value;}}
  if(Number(merged.colorRolesVersion)<2||merged.colorRolesVersion===undefined){const colors=migrateColorRoles(merged);for(const key of ['secondaryColor','tertiaryColor','colorRolesVersion']){merged[key]=colors[key];migrate[key]=colors[key];}}
  // A user may change and save a preference while this older GET is in flight.
  // Preserve that edit even after a successful PUT has cleared pending values.
  for(const [key,revision] of Object.entries(uiPreferencesState.revisions||{})){
    if(revision!==(startedRevisions[key]||0)){merged[key]=uiPreferencesState.values[key];delete migrate[key];}
  }
  uiPreferencesState={...uiPreferencesState,loaded:true,user:String(r.user||''),authelia:!!r.authelia,identityHeader:String(r.identity_header||''),identityDiagnostics:(r.identity_diagnostics||{}),values:{...merged,...uiPreferencesState.pending}};
  applyUiPreferences(uiPreferencesState.values);updateUiPreferenceOwner();
  if(typeof updatePreferenceSaveState==='function')updatePreferenceSaveState();
  if(Object.keys(migrate).length){uiPreferencesState.pending={...migrate,...uiPreferencesState.pending};if(!uiPreferencesState.saving){if(uiPreferencesState.saveTimer)clearTimeout(uiPreferencesState.saveTimer);uiPreferencesState.saveTimer=setTimeout(flushUiPreferenceSave,300);}}
  return uiPreferencesState;
}

function rememberClientError(message, meta = {}) {
  const item = {
    id: `client-${Date.now()}-${Math.random().toString(16).slice(2,8)}`,
    component: meta.component || 'Browser / UI',
    severity: meta.severity || 'error',
    message: String(message || 'Unknown browser error'),
    detail: String(meta.detail || ''),
    path: String(meta.path || ''),
    method: String(meta.method || ''),
    status_code: Number(meta.status_code || 0),
    first_seen: new Date().toISOString(),
    last_seen: new Date().toISOString(),
    count: 1,
    resolved: false,
    client_side: true,
    ui_version: UI_BUILD_VERSION,
    backend_version: String(meta.backend_version||lastHealthSnapshot?.version||'unknown'),
    error_type: String(meta.error_type||''), error_id: String(meta.error_id||''),
    elapsed_ms: meta.elapsed_ms??null, timeout_ms: meta.timeout_ms??null,
  };
  const signature = `${item.component}|${item.method}|${item.path}|${item.message}`;
  const existing = clientErrors.find(x => x.signature === signature && !x.resolved);
  if (existing) { existing.count += 1; existing.last_seen = item.last_seen; existing.detail = item.detail;for(const key of ['backend_version','error_type','error_id','elapsed_ms','timeout_ms'])existing[key]=item[key]; return existing; }
  item.signature = signature;
  clientErrors.unshift(item);
  clientErrors = clientErrors.slice(0, 30);
  return item;
}

const uiTimingSamples=[];
function recordUiTiming(kind,label,milliseconds,extra={}){
  const clean=String(label).split('?')[0].replace(/\/\d+(?=\/|$)/g,'/:id');
  uiTimingSamples.push({at:new Date().toISOString(),kind,label:clean,ms:Math.round(Math.max(0,milliseconds)),...extra});
  if(uiTimingSamples.length>120)uiTimingSamples.splice(0,uiTimingSamples.length-120);
}
function uiTimingSnapshot(){return uiTimingSamples.map(sample=>({...sample}));}
async function api(url, opts = {}) {
  const timingStart=performance.now();let timingHeaders=null,timingStatus=0,timingOk=false,responseVersion='';
  const requestMethod = String(opts.method || 'GET').toUpperCase();
  const timeoutMs = Number(opts.timeoutMs ?? 45000);
  const fetchOpts = { ...opts };
  delete fetchOpts.timeoutMs;
  delete fetchOpts.responseType;
  delete fetchOpts.suppressClientError;
  delete fetchOpts.noMutationRefresh;
  delete fetchOpts._adminAuthRetried;
  fetchOpts.headers = { 'Content-Type': 'application/json', ...(fetchOpts.headers || {}) };
  const requestToken = token;
  if (requestToken) fetchOpts.headers['X-Admin-Token'] = requestToken;
  let timer = null, ownedController = null, timedOut = false, abortForwarder = null;
  const callerSignal = fetchOpts.signal || null;
  if (callerSignal || timeoutMs > 0) {
    ownedController = new AbortController();
    if (callerSignal) {
      if (callerSignal.aborted) ownedController.abort();
      else { abortForwarder = () => ownedController.abort(); callerSignal.addEventListener('abort', abortForwarder, { once: true }); }
    }
    fetchOpts.signal = ownedController.signal;
    if (timeoutMs > 0) timer = setTimeout(() => { timedOut = true; ownedController.abort(); }, timeoutMs);
  }
  const manualMutation = typeof beginManualActionsMutation === 'function' && beginManualActionsMutation(url, requestMethod);
  let manualMutationFailed = false;
  try {
    const r = await fetch(url, fetchOpts);
    timingHeaders=performance.now()-timingStart;timingStatus=r.status;responseVersion=r.headers?.get?.('X-AutoSourcarr-Version')||'';
    // A parallel bootstrap request may have supplied the token after this one
    // was dispatched. Retry its old 401 once with the current credential.
    if (r.status === 401 && token && token !== requestToken && !opts._adminAuthRetried) return await api(url, {...opts,_adminAuthRetried:true});
    if (r.status === 401) {
      if(!opts._adminAuthRetried&&!adminAuthDismissed){
        const t = prompt(token?'The saved admin token was rejected. Enter the current admin token':'Admin token');
        if(t){token=t;try{localStorage.setItem('sc_admin_token',t);}catch(_){}return await api(url,{...opts,_adminAuthRetried:true});}
      }
      adminAuthDismissed=true;token='';try{localStorage.removeItem('sc_admin_token');}catch(_){}
      throw new Error('Admin token required or rejected. Reload the page to sign in again.');
    }
    if (!r.ok) {
      const detail=await r.text();let message=detail,needsAttention=false,errorId='';
      try { const body=JSON.parse(detail);errorId=String(body.error_id||'');needsAttention=body.needs_attention===true;message=typeof body.detail==='string'?body.detail:(typeof body.message==='string'?body.message:JSON.stringify(body.detail??body)); } catch (_) {}
      const failure=new Error(message||`HTTP ${r.status} ${r.statusText||'Request failed'}`);
      failure.error_id=errorId;failure.needsAttention=needsAttention;failure.status_code=r.status;failure.detail=detail;failure.retry_after=r.headers?.get?.('Retry-After')||null;throw failure;
    }
    const payload=opts.responseType==='blob'?await r.blob():await r.json();
    clientErrors.forEach(item=>{if(!item.resolved&&item.path===url&&item.method===requestMethod){item.resolved=true;item.resolved_at=new Date().toISOString();}});
    if(!opts.noMutationRefresh)scheduleMutationRefresh(url,requestMethod);
    timingOk=true;return payload;
  } catch (e) {
    manualMutationFailed = true;
    const method = requestMethod;
    const aborted = e?.name === 'AbortError';
    const callerCancelled = !!callerSignal?.aborted;
    const message = aborted ? (timedOut ? 'Request timed out' : 'Request cancelled') : String(e?.message || e || 'Request failed');
    // Navigation, tab changes and caller-initiated AbortController cancellation are
    // expected browser control flow, not AutoSourcarr failures. Only genuine request
    // failures are eligible for the global diagnostics list.
    if (!e?.needsAttention && !opts.suppressClientError && !(aborted && callerCancelled)) rememberClientError(message, { path: url, method, component: 'Browser request', severity: timedOut ? 'warning' : 'error', status_code:e?.status_code, detail:e?.detail||e?.stack, error_type:timedOut?'TimeoutError':e?.name, error_id:e?.error_id, backend_version:responseVersion, elapsed_ms:Math.round(performance.now()-timingStart),timeout_ms:timeoutMs });
    if(e?.needsAttention&&typeof loadAttention==='function')loadAttention(true).catch(()=>{});
    if (aborted) {
      const err = new Error(message);
      err.name = timedOut ? 'TimeoutError' : 'AbortError';
      err.expectedCancellation = !timedOut;
      throw err;
    }
    throw e;
  } finally {
    if (manualMutation) finishManualActionsMutation(manualMutationFailed);
    if (timer) clearTimeout(timer);
    if (callerSignal && abortForwarder) callerSignal.removeEventListener('abort', abortForwarder);
    if(typeof recordUiTiming==='function')recordUiTiming('request',url,performance.now()-timingStart,{method:requestMethod,status:timingStatus,ok:timingOk,headers_ms:timingHeaders===null?null:Math.round(timingHeaders)});
  }
}
// Typed client for /api/v1, generated from the server's OpenAPI document (scripts/generate-api-client.mjs).
// Requests still go through api() above: same admin token, timeouts and error handling.
function apiV1(defaults={}){return AutoSourcarrApi.createClient({request:(path,options)=>api(path,{...defaults,...options})});}
// UI-13 live updates: one server-sent event stream (/api/v1/events). It is read with fetch, not EventSource, so the
// admin token header can be sent. Events only say *what* changed; the page then reloads that part. Reconnects resume
// from the last event id; a "resync" event (the server no longer holds the gap) reloads everything.
const liveEvents={connected:false,lastId:null,controller:null,retryMs:3000,handlers:{},started:false};
function onLiveEvent(topic,handler){(liveEvents.handlers[topic]||=[]).push(handler);return()=>{const list=liveEvents.handlers[topic]||[];const at=list.indexOf(handler);if(at>=0)list.splice(at,1);};}
function dispatchLiveEvent(topic,data){
  for(const handler of [...(liveEvents.handlers[topic]||[]),...(liveEvents.handlers['*']||[])]){try{handler(data,topic);}catch(e){console.error('Live update handler failed',e);}}
}
function parseLiveBlock(block){
  let id=null,event='message';const data=[];
  for(const line of block.split('\n')){
    if(!line||line.startsWith(':'))continue;
    const colon=line.indexOf(':');const field=colon<0?line:line.slice(0,colon);let value=colon<0?'':line.slice(colon+1);if(value.startsWith(' '))value=value.slice(1);
    if(field==='id')id=value;else if(field==='event')event=value;else if(field==='data')data.push(value);else if(field==='retry'&&/^\d+$/.test(value))liveEvents.retryMs=Number(value);
  }
  return {id,event,data:data.join('\n')};
}
async function runLiveEvents(){
  if(liveEvents.started)return;liveEvents.started=true;
  for(;;){
    const controller=new AbortController();liveEvents.controller=controller;
    try{
      const headers={Accept:'text/event-stream'};
      if(token)headers['X-Admin-Token']=token;
      if(liveEvents.lastId)headers['Last-Event-ID']=liveEvents.lastId;
      const response=await fetch('/api/v1/events',{headers,cache:'no-store',signal:controller.signal,credentials:'same-origin'});
      if(!response.ok||!response.body)throw new Error('HTTP '+response.status);
      liveEvents.connected=true;
      const reader=response.body.getReader();const decoder=new TextDecoder();let buffer='';
      for(;;){
        const {value,done}=await reader.read();if(done)break;
        buffer+=decoder.decode(value,{stream:true}).replace(/\r\n?/g,'\n');
        let cut;
        while((cut=buffer.indexOf('\n\n'))>=0){
          const block=buffer.slice(0,cut);buffer=buffer.slice(cut+2);
          const item=parseLiveBlock(block);if(!item.data&&item.event==='message')continue;
          if(item.id)liveEvents.lastId=item.id;
          let payload=null;try{payload=item.data?JSON.parse(item.data):null;}catch(_){payload=null;}
          if(item.event==='resync')liveEvents.lastId=null;
          dispatchLiveEvent(item.event,payload);
        }
      }
    }catch(e){if(e?.name!=='AbortError')console.debug('Live updates disconnected',e?.message||e);}
    liveEvents.connected=false;
    await new Promise(resolve=>setTimeout(resolve,document.hidden?Math.max(15000,liveEvents.retryMs):liveEvents.retryMs));
  }
}
let liveTasksTimer=null;
function liveTasksChanged(){if(liveTasksTimer)return;liveTasksTimer=setTimeout(()=>{liveTasksTimer=null;loadTasks().catch(()=>{});},150);}
onLiveEvent('tasks',liveTasksChanged);
onLiveEvent('jobs',liveTasksChanged);
onLiveEvent('errors',()=>{const panel=document.getElementById('errorPanel');if(panel&&!panel.hidden&&typeof loadErrors==='function')loadErrors().catch(()=>{});});
onLiveEvent('resync',()=>{liveTasksChanged();});
// Waits until a task leaves the running list; resolves with its final row (state, message, error).
async function waitForTask(taskId,{timeoutMs=3600000}={}){
  const deadline=Date.now()+timeoutMs;
  for(;;){
    const snapshot=await apiV1({timeoutMs:12000,suppressClientError:true,cache:'no-store'}).getTasks();
    const row=(snapshot.items||[]).find(item=>item.id===taskId);
    if(!row)return {state:'failed',error:'The task is no longer listed; see the task history.'};
    if(row.state!=='running')return row;
    if(Date.now()>deadline)return {state:'failed',error:'Still running; follow it in the task list.'};
    await new Promise(resolve=>{let off=null;const timer=setTimeout(()=>{off?.();resolve();},liveEvents.connected?10000:2000);off=onLiveEvent('jobs',data=>{if(data?.id===taskId){clearTimeout(timer);off?.();resolve();}});});
  }
}
window.addEventListener('unhandledrejection', e => {
  const reason=e.reason;
  if(reason?.expectedCancellation || reason?.name==='AbortError') return;
  if(reason?.needsAttention){toast(reason.message);return;}
  console.error('Unhandled AutoSourcarr action failure', reason);
  const message = reason?.message || String(reason || 'Unknown error');
  rememberClientError(message, { component: 'Browser / UI', detail: String(reason?.stack||'Unhandled promise rejection'),error_type:reason?.name });
  toast('Action failed: ' + message);
});
function toast(x) { const t = $('#toast'); t.textContent = x; t.style.display = 'block'; setTimeout(() => t.style.display = 'none', 3500); }
function esc(x) { return String(x ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#039;'}[c])); }
function jsq(x) { return esc(String(x ?? '').replace(/\\/g, '\\\\').replace(/'/g, "\\'").replace(/\r/g, '\\r').replace(/\n/g, '\\n').replace(/\u2028/g, '\\u2028').replace(/\u2029/g, '\\u2029')); }
function fmtScore(x) { return x == null ? '—' : Number(x).toFixed(1); }
function shortText(x, n=300) { const s=String(x||'').replace(/\s+/g,' ').trim(); return s.length>n ? s.slice(0,n-1)+'…' : s; }

// Placeholder covers are deliberately not retried by the browser. The backend already
// fills the persistent cache asynchronously; retry storms competed with Matrix, Status
// and source-host work without adding useful information. The next render naturally
// uses the completed cache entry.

const LANG_NAMES={all:'All / multi-language',en:'English',ja:'Japanese',ko:'Korean',zh:'Chinese',ru:'Russian',vi:'Vietnamese',fr:'French',pl:'Polish',id:'Indonesian',tr:'Turkish',it:'Italian',es:'Spanish',uk:'Ukrainian',de:'German',th:'Thai',ro:'Romanian',ms:'Malay',sv:'Swedish',no:'Norwegian',nl:'Dutch',pt:'Portuguese','pt-br':'Portuguese (Brazil)',ar:'Arabic',hi:'Hindi',cs:'Czech',hu:'Hungarian',fi:'Finnish',da:'Danish'};
function langLabel(code){const c=String(code||'').toLowerCase();return LANG_NAMES[c]||c||'Unspecified';}
function familyKey(s){return `${String(s.name||s.display_name||s.source_id).trim().toLowerCase()}\u241f${String(s.base_url||'').trim().toLowerCase()}`;}
function sourceFamilies(items){
  const map=new Map();
  for(const s of items){
    const key=familyKey(s);
    if(!map.has(key)) map.set(key,{key,name:s.name||s.display_name||s.source_id,base_url:s.base_url||'',priority:Number(s.priority)||1000,variants:[]});
    const g=map.get(key); g.priority=Math.min(g.priority,Number(s.priority)||1000); g.variants.push(s);
  }
  const out=[...map.values()];
  for(const g of out){
    g.variants.sort((a,b)=>langLabel(a.lang).localeCompare(langLabel(b.lang)));
    const gids=[...new Set(g.variants.map(v=>v.group_id==null?'':String(v.group_id)))];
    g.group_id=gids.length===1&&gids[0]!==''?Number(gids[0]):null;
    g.group_name=g.group_id!=null?(g.variants.find(v=>Number(v.group_id)===g.group_id)?.group_name||''):'';
  }
  out.sort((a,b)=>a.priority-b.priority||a.name.localeCompare(b.name));
  return out;
}
function groupSources(items){
  const families=sourceFamilies(items), grouped=new Map(), out=[];
  for(const family of families){
    if(family.group_id==null){out.push(family);continue;}
    const key=`custom:${family.group_id}`;
    if(!grouped.has(key))grouped.set(key,{key,name:family.group_name||`Source group ${family.group_id}`,base_url:'',priority:family.priority,variants:[],families:[],custom_group_id:family.group_id});
    const g=grouped.get(key);g.priority=Math.min(g.priority,family.priority);g.variants.push(...family.variants);g.families.push(family);
  }
  out.push(...grouped.values());
  for(const g of out)g.variants.sort((a,b)=>(Number(a.priority)||1000)-(Number(b.priority)||1000)||String(a.name||'').localeCompare(String(b.name||''))||langLabel(a.lang).localeCompare(langLabel(b.lang)));
  out.sort((a,b)=>a.priority-b.priority||a.name.localeCompare(b.name));
  return out;
}
function selectedVariants(g){return g.variants.filter(v=>v.enabled_language);}
function groupSearch(g){const v=selectedVariants(g);return v.length>0&&v.every(x=>x.enabled_search);}
function groupChapters(g){const v=selectedVariants(g);return v.length>0&&v.every(x=>x.enabled_chapters);}
function langSummary(g){const picked=selectedVariants(g);if(!picked.length)return 'No languages';if(picked.length===g.variants.length)return g.variants.length===1?langLabel(picked[0].lang):`All ${picked.length} languages`;const labels=picked.map(v=>v.lang||'?');return labels.length<=4?labels.join(', '):`${labels.slice(0,3).join(', ')} +${labels.length-3}`;}
function groupSummary(g){if(g.custom_group_id!=null){const names=[...new Set((g.families||[]).map(x=>x.name).filter(Boolean))];const langs=[...new Set(selectedVariants(g).map(v=>langLabel(v.lang)))];return `${names.length} source${names.length===1?'':'s'}${langs.length?` · ${langs.join(', ')}`:''}`;}return langSummary(g);}
function groupForSource(sourceId){ const row=sourceRows.find(s=>String(s.source_id)===String(sourceId)); return row ? sourceGroups.find(g=>g.key===familyKey(row)) : null; }

function surfaceIsActive(surface){
  return !!document.querySelector(`#${surface}.active`);
}
function textSelectionIntersectsSurface(surface){
  try{
    const selection=window.getSelection?.();
    if(!selection||selection.isCollapsed||!String(selection.toString()||''))return false;
    const root=document.getElementById(surface);if(!root)return false;
    const nodeElement=node=>node?.nodeType===Node.ELEMENT_NODE?node:node?.parentElement;
    const anchor=nodeElement(selection.anchorNode),focus=nodeElement(selection.focusNode);
    return !!((anchor&&root.contains(anchor))||(focus&&root.contains(focus)));
  }catch(_){return false;}
}
function structuralRenderBlocked(surface){
  // Never replace large interactive DOM trees while a modal, inspector, click/gesture,
  // focused form control, or real browser text selection is being used.  v0.2.58
  // protected clicks/focus but a 1.5-4.5s poll could still replace the selected text
  // node and make the browser selection disappear.
  if(Date.now()<uiGestureUntil)return true;
  if(textSelectionIntersectsSurface(surface))return true;
  const active=document.activeElement;
  if(active&&/^(INPUT|TEXTAREA|SELECT)$/.test(active.tagName)&&active.closest?.(`#${surface}`))return true;
  if(document.body.classList.contains('modalOpen'))return true;
  if(surface==='matrix'&&activeInspector.matrix!=null)return true;
  if(surface==='subbed'&&activeInspector.subbed!=null)return true;
  if(surface==='browse'&&browseInspectorIsOpen())return true;
  if(surface==='duplicates'&&!$('#duplicateManagerModal')?.hidden)return true;
  return false;
}
function performSurfaceRender(surface){
  if(surface==='matrix'&&matrixData)rerenderMatrixPreservingScroll('matrix');
  else if(surface==='subbed'&&subbedData)rerenderMatrixPreservingScroll('subbed');
  else if(surface==='browse')renderBrowse();
  else if(surface==='imports')renderImportItems();
  else if(surface==='duplicates'&&duplicateData)renderDuplicates();
  else if(surface==='tasks')renderTasks();
}
function requestSurfaceRender(surface,{force=false}={}){
  if(!force&&!surfaceIsActive(surface))return;
  if(!force&&structuralRenderBlocked(surface)){deferredUiRenders.add(surface);return;}
  deferredUiRenders.delete(surface);performSurfaceRender(surface);
}
function flushDeferredUiRenders(){
  for(const surface of [...deferredUiRenders]){
    if(!surfaceIsActive(surface)){deferredUiRenders.delete(surface);continue;}
    if(structuralRenderBlocked(surface))continue;
    deferredUiRenders.delete(surface);performSurfaceRender(surface);
  }
}
new MutationObserver(()=>{if(!document.body.classList.contains('modalOpen'))requestAnimationFrame(flushDeferredUiRenders);}).observe(document.body,{attributes:true,attributeFilter:['class']});
function holdStructuralRenders(ms=700){
  uiGestureUntil=Math.max(uiGestureUntil,Date.now()+Math.max(100,ms));
  if(uiGestureFlushTimer)clearTimeout(uiGestureFlushTimer);
  uiGestureFlushTimer=setTimeout(()=>{uiGestureFlushTimer=null;flushDeferredUiRenders();},Math.max(120,ms)+30);
}
document.addEventListener('pointerdown',()=>holdStructuralRenders(700),{capture:true,passive:true});
document.addEventListener('focusout',()=>setTimeout(flushDeferredUiRenders,0),true);
document.addEventListener('selectionchange',()=>{const sel=window.getSelection?.();if(!sel||sel.isCollapsed)setTimeout(flushDeferredUiRenders,0);});

function entryCoverUrl(entryId,nonce=coverRenderNonce){
  const id=Number(entryId),rev=entryCoverRevisions.get(id)||0;
  return `/api/v1/covers/${id}?v=${Number(nonce)||coverRenderNonce}${rev?`&live=${rev}`:''}`;
}
function sourceCoverUrl(sourceMangaId,nonce=coverRenderNonce){
  const id=Number(sourceMangaId),rev=sourceCoverRevisions.get(id)||0;
  return `/api/v1/source-covers/${id}?v=${Number(nonce)||coverRenderNonce}${rev?`&live=${rev}`:''}`;
}
function refreshEntryCoverImages(entryId,skip=null){
  const id=Number(entryId);if(!id)return;
  const rev=Date.now();entryCoverRevisions.set(id,rev);saveCoverRevisions('autosourcarr.entryCoverRevisions',entryCoverRevisions);
  document.querySelectorAll(`img[data-entry-cover="${id}"]`).forEach(img=>{if(img!==skip)img.src=entryCoverUrl(id,rev);});
}
function noteEntryThumbnail(entry){
  if(!entry)return;
  const id=Number(entry.canonical_id||entry.id),thumb=String(entry.thumbnail_url||'').trim();
  if(!id||!thumb)return;
  const previous=knownEntryThumbnails.get(id);knownEntryThumbnails.set(id,thumb);
  if(previous!==undefined&&previous!==thumb)refreshEntryCoverImages(id);
}
function sparseEntryIds(){
  const ids=new Set();
  const add=e=>{if(!e)return;const id=Number(e.canonical_id||e.id);if(!id)return;const thumb=String(e.thumbnail_url||'').trim(),desc=String(e.description||'').trim();if(!thumb||desc.length<40||e.cover_cached!==true)ids.add(id);};
  if(activeSurface('browse'))(browseState.items||[]).forEach(add);
  for(const kind of ['matrix','subbed'])if(activeSurface(kind))(matrixDataset(kind)?.entries||[]).forEach(add);
  for(const item of (activeSurface('imports')?importsState.items||[]:[])){
    (item.suggestions||[]).forEach(add);add(item.matched_entry);
    if(item.reference_entry_id)ids.add(Number(item.reference_entry_id));
  }
  if(activeSurface('duplicates')&&!globalThis.AutoSourcarrMatchingReview?.historyActive)for(const group of (duplicateData?.groups||[]))(group.entries||[]).forEach(add);
  return [...ids].filter(Boolean).slice(0,500);
}
function patchCompactSummary(summary){
  if(!summary)return false;
  const id=Number(summary.canonical_id||summary.id);if(!id)return false;
  let structuralChanged=false;
  const merge=(obj)=>{
    if(!obj||Number(obj.canonical_id||obj.id)!==id)return;
    const before=[obj.preferred_title,obj.description,obj.status_label,obj.match_count,obj.thumbnail_url].map(x=>String(x??'')).join('\u241f');
    const score=obj.import_score;Object.assign(obj,summary,{id:Number(obj.id||id)});if(score!=null)obj.import_score=score;
    const after=[obj.preferred_title,obj.description,obj.status_label,obj.match_count,obj.thumbnail_url].map(x=>String(x??'')).join('\u241f');
    if(before!==after)structuralChanged=true;
  };
  for(const kind of ['matrix','subbed']){
    const d=matrixDataset(kind),e=(d?.entries||[]).find(x=>Number(x.id)===id);
    if(e){
      const recordsChanged=['match_count','entry_match_count'].some(k=>summary[k]!=null&&Number(summary[k])!==Number(e[k]));
      merge(e);
      if(recordsChanged&&activeSurface(kind)&&!matrixRecordReload[kind])matrixRecordReload[kind]=setTimeout(()=>{matrixRecordReload[kind]=null;loadMatrixView(kind).catch(()=>{});},150);
    }
  }
  const b=(browseState.items||[]).find(x=>Number(x.id)===id);if(b)merge(b);
  for(const item of (importsState.items||[])){
    (item.suggestions||[]).filter(x=>Number(x.canonical_id||x.id)===id).forEach(merge);
    // applied_match is a frozen import-time snapshot, not a live summary.
    if(item.matched_entry&&Number(item.matched_entry.canonical_id||item.matched_entry.id)===id)merge(item.matched_entry);
    if(Number(item.reference_entry_id||0)===id){item.reference_title=summary.preferred_title||item.reference_title;item.reference_description=summary.description||item.reference_description;item.reference_thumbnail_url=summary.cover_url||item.reference_thumbnail_url;}
  }
  for(const group of (duplicateData?.groups||[]))(group.entries||[]).filter(x=>Number(x.id)===id).forEach(merge);
  noteEntryThumbnail(summary);
  const coverReady=!!summary.cover_cached,wasReady=knownEntryCoverReady.get(id);
  knownEntryCoverReady.set(id,coverReady);
  // A cover request can return the lightweight placeholder while its loader keeps
  // running in the backend. As soon as the cache heartbeat says that loader has
  // completed, replace every visible placeholder for this entry immediately.
  if(coverReady&&wasReady!==true)refreshEntryCoverImages(id);
  patchOpenInspectorSnapshot(summary);
  const browseCard=document.querySelector(`#browseGrid .browseCard[data-entry-id="${id}"]`);
  if(browseCard){
    const browseEntry=(browseState.items||[]).find(x=>Number(x.id)===id)||summary;
    patchBrowseCardNode(browseCard,browseEntry);
  }
  return structuralChanged;
}
async function refreshSparseEntryMetadata(){
  if(refreshSparseEntryMetadata.pending)return refreshSparseEntryMetadata.pending;
  refreshSparseEntryMetadata.pending=refreshSparseEntryMetadataOnce().finally(()=>{refreshSparseEntryMetadata.pending=null;});
  return refreshSparseEntryMetadata.pending;
}
async function refreshSparseEntryMetadataOnce(){
  const ids=sparseEntryIds();if(!ids.length)return;
  try{
    const r=await api('/api/admin/entries/summaries',{method:'POST',body:JSON.stringify({ids}),timeoutMs:6000,suppressClientError:true,noMutationRefresh:true});
    // .some() stopped at the first changed entry, leaving the rest of this
    // successful response unapplied until many more HTTP polls had completed.
    let structuralChanged=false;
    for(const summary of (r.items||[]))structuralChanged=patchCompactSummary(summary)||structuralChanged;
    // Cover-ready changes update <img> elements directly. Rebuild a large list only
    // when text/structure actually changed, and never while the user is interacting
    // with an inspector/modal.
    if(structuralChanged){
      // Browse cards are keyed and patched in place by patchCompactSummary. Replacing
      // the whole Browse grid here restarted lazy images and changed row heights,
      // which made the viewport visibly jump every metadata heartbeat.
      for(const surface of ['matrix','subbed','imports','duplicates'])if(activeSurface(surface))requestSurfaceRender(surface);
    }
  }catch(_e){}
}
function scheduleMetadataLivePoll(delay=2200){
  if(metadataLiveTimer)return;
  metadataLiveTimer=setTimeout(async()=>{metadataLiveTimer=null;if(!document.hidden)await refreshSparseEntryMetadata();scheduleMetadataLivePoll(tasksState.running>0?3500:15000);},Math.max(500,delay));
}
function scheduleMetadataBurst(){
  for(const delay of [450,1200,2800,5500])setTimeout(()=>{if(!document.hidden)refreshSparseEntryMetadata().catch(()=>{});},delay);
}
function scheduleDuplicateLiveRefresh(delay=350){
  if(duplicateRefreshTimer)clearTimeout(duplicateRefreshTimer);
  duplicateRefreshTimer=setTimeout(()=>{duplicateRefreshTimer=null;if(duplicateCandidatesVisible())loadDuplicates(null,{render:true,quiet:true}).catch(()=>{});},Math.max(50,delay));
}
function duplicateCandidatesVisible(){return !document.hidden&&activeSurface('duplicates')&&!globalThis.AutoSourcarrMatchingReview?.historyActive;}
function scheduleDuplicateHeartbeat(delay=4500){
  if(duplicateHeartbeatTimer)return;
  duplicateHeartbeatTimer=setTimeout(async()=>{
    duplicateHeartbeatTimer=null;
    if(duplicateCandidatesVisible())await loadDuplicates(null,{render:true,quiet:true}).catch(()=>{});
    scheduleDuplicateHeartbeat(document.hidden?15000:4500);
  },Math.max(1000,delay));
}
function scheduleMutationRefresh(url,method){
  const path=String(url||'').split('?')[0];if(String(method||'GET').toUpperCase()==='GET')return;
  if(['/api/admin/entries/summaries','/api/admin/duplicate-candidates','/api/admin/matrix/chapter-stats'].includes(path))return;
  if(path!=='/api/admin/ui-preferences')catalogViewRevision++;
  if(path.includes('/entries/')||path.includes('/duplicates')||(path.includes('/manual-groups')||path.includes('/manual-actions'))||path.includes('/sources/'))scheduleDuplicateLiveRefresh();
  if(path.includes('/entries/')||path.includes('/imports/'))setTimeout(()=>refreshSparseEntryMetadata().catch(()=>{}),500);
}

// Navigation
let navigationGeneration=0;
function switchTab(tab,{load=true}={}){
  if(!document.querySelector(`nav button[data-tab="${tab}"]`))tab='browse';
  navigationGeneration++;
  globalThis.AutoSourcarrNavigation?.beforeSwitch(tab);
  flushDeferredUiRenders();
  document.querySelectorAll('nav button,.tab').forEach(x=>x.classList.remove('active'));
  document.querySelector(`nav button[data-tab="${tab}"]`)?.classList.add('active');
  $('#'+tab)?.classList.add('active');
  globalThis.AutoSourcarrNavigation?.afterSwitch(tab);
  globalThis.AutoSourcarrReimagined?.navigated(tab);
  return load?loadActiveTab(tab).catch(e=>toast(e.message)):Promise.resolve();
}
async function loadActiveTab(tab){
  if(tab==='statistics'){await health();globalThis.AutoSourcarrReimagined?.renderStatistics(lastHealthSnapshot);}
  if(tab==='browse'&&!browseState.items.length)await loadBrowse(browseState.page||1);
  if(tab==='subbed'){if(subbedData)renderMatrix('subbed');await loadSubbed();}
  if(tab==='matrix'){if(matrixData)renderMatrix('matrix');await loadMatrix();}
  if(tab==='duplicates'){
    if(globalThis.AutoSourcarrMatchingReview?.historyActive)await globalThis.AutoSourcarrMatchingReview.activateHistory();
    else{if(duplicateData)renderDuplicates();await loadDuplicates();}
  }
  if(tab==='mixed-entries')await loadMixedEntries();
  if(tab==='manual-actions'){await loadManualActions();if(manualActionsSubtab==='tags')window.loadTagReview?.();}
  if(tab==='about'){health().catch(()=>{});loadApkDownload();window.loadInternalServices?.();}
  if(tab==='imports')await loadImports();
  if(tab==='attention')await loadAttention(true);
  if(tab==='tasks')await loadTasks();
  if(tab==='settings')await ensureSettingsPane();
  if(tab==='sources'){
    setSourceSubtab(readLocalUiPreference('sourcesSubtab')||'installed',false);
    globalThis.AutoSourcarrMatchingReview?.activateRules();
    if(!sourceRows.length)await loadSources();
  }
  globalThis.AutoSourcarrNavigation?.rendered(tab);
}
function ensureSettingsPane(){
  const pane=readLocalUiPreference('settingsSubtab')||'customization';
  if(pane==='browser')return loadSourceBrowser();
  if(pane==='services')return window.loadInternalServices?.()||Promise.resolve();
  if(pane!=='configuration'||settingsState.dirty||settingsState.data)return Promise.resolve(settingsState.data);
  return loadEnvSettings();
}
document.querySelectorAll('nav button').forEach(b=>b.onclick=()=>switchTab(b.dataset.tab));


let attentionState={items:[]},attentionPromise=null,attentionLastLoad=0,attentionSignature='';
function renderAttention(){
  const items=attentionState.items||[],badge=$('#attentionNavBadge'),box=$('#attentionItems');
  if(badge){badge.hidden=!items.length;badge.textContent=String(items.length);}
  $('#attentionSummary').textContent=items.length?`${items.length} website${items.length===1?'':'s'} paused · one automatic retry per hour, including while this page is closed.`:'No sources need your help.';
  const signature=JSON.stringify(items);if(signature===attentionSignature)return;attentionSignature=signature;
  box.innerHTML=items.map(item=>`<article class="card assistanceCard"><div class="toolbar wrap"><h3 class="grow">${esc(item.name||'Source')}</h3><span class="publicationPill">${item.retrying?'Checking…':'Waiting for you'}</span></div><p><strong>${esc(item.reason)}</strong></p><p class="muted">${esc(item.website||item.base_url||'Source verification')} · ${Number(item.occurrences)||1} reported failure${Number(item.occurrences)===1?'':'s'}</p><p class="attentionNextRetry">Next automatic retry: ${esc(new Date(item.next_retry_at*1000).toLocaleString())}</p><div class="toolbar wrap"><button data-attention-webview="${esc(item.id)}">Open WebView</button><button class="primaryAction" data-attention-retry="${esc(item.id)}" ${item.retrying?'disabled':''}>I’ve completed verification — retry</button></div>${item.last_retry_error?`<p class="muted">Last retry: ${esc(shortText(item.last_retry_error,240))}</p>`:''}<details><summary>Technical details</summary><pre>${esc(item.detail||'')}</pre></details></article>`).join('');
}
async function loadAttention(force=false){
  if(attentionPromise)return attentionPromise;
  if(!force&&Date.now()-attentionLastLoad<10000)return;
  attentionLastLoad=Date.now();
  attentionPromise=(async()=>{attentionState=await api('/api/admin/attention',{timeoutMs:5000,suppressClientError:true,noMutationRefresh:true,cache:'no-store'});renderAttention();})();
  try{await attentionPromise;}catch(e){if(surfaceIsActive('attention'))$('#attentionSummary').textContent='Unable to refresh assistance: '+e.message;throw e;}finally{attentionPromise=null;}
}
$('#refreshAttention').onclick=()=>loadAttention(true).catch(e=>toast(e.message));
$('#attentionItems').addEventListener('click',async event=>{
  const button=event.target.closest('button');if(!button)return;
  const id=button.dataset.attentionWebview||button.dataset.attentionRetry,item=attentionState.items.find(x=>x.id===id);if(!item)return;
  if(button.dataset.attentionWebview){openSourceWebview(item.base_url,item.source_id);return;}
  button.disabled=true;
  try{await api(`/api/admin/attention/${encodeURIComponent(id)}/retry`,{method:'POST',body:'{}',noMutationRefresh:true,timeoutMs:5000,suppressClientError:true});toast('Verification check queued');await loadAttention(true);}
  catch(e){toast('Unable to retry verification: '+e.message);button.disabled=false;}
});

// Persistent Mihon/Tachiyomi import review.  The uploaded backup itself is deleted;
// this view reads only the compact per-item audit rows kept in AutoSourcarr's DB.
function cleanImportDisplayTitle(value){return String(value||'').replace(/\s*\((?:colored|colour(?:ed)?|full[ -]?color|full[ -]?colour)\)\s*$/i,'').trim()||String(value||'');}
function importStateLabel(state){
  return ({added:'Added',existing:'Existing',unresolved:'Unresolved',resolving:'Matching…',retrying:'Retrying…',failed:'Failed',skipped:'Skipped'})[String(state||'')]||String(state||'Unknown');
}
function importCandidateHtml(runId,item,c){
  const title=cleanImportDisplayTitle(c.preferred_title||`Entry ${c.id}`);
  const sources=(c.sources||[]).slice(0,4).join(', ');
  const desc=String(c.description||'').trim();
  const score=Number(c.import_score||0);
  return comparisonCard('B · Candidate in AutoSourcarr',title,entryCoverUrl(Number(c.id)),`Entry ${Number(c.id)}${score>0?` · ${Math.round(score*100)}% suggestion`:''}`,[['Aliases',reviewText((c.titles||[]).join(' · '),180)],['Sources',reviewText((c.sources||[]).join(' · '),180)],['Summary',reviewText(desc)],['Publication',esc(c.status_label||'Unknown')]],`<div class="reviewCardActions"><button class="goodAction" onclick="matchImportItem('${jsq(runId)}',${Number(item.id)},${Number(c.id)},this)">Match and add to main library</button></div>`);
}
function importAcceptedMatchHtml(runId,item){
  const c=item.applied_match||item.matched_entry||{};
  const entryId=Number(item.entry_id||c.id||0);
  if(!entryId)return '';
  const title=cleanImportDisplayTitle(c.preferred_title||`Entry ${entryId}`);
  const sources=(c.sources||[]).slice(0,6).join(', ');
  const desc=String(c.description||'').trim();
  const cover=String(c.cover_url||`/api/v1/covers/${entryId}`);
  return comparisonCard(item.historical_snapshot_missing?'B · Current match (historical snapshot unavailable)':'B · Imported match',title,entryCoverUrl(entryId),`AutoSourcarr entry ${entryId}`,[['Sources',reviewText(sources,180)],['Summary',reviewText(desc)]],`<div class="reviewCardActions"><button class="dangerAction" onclick="rejectImportMatch('${jsq(runId)}',${Number(item.id)},this)">Reject match</button></div>`);
}
function importReasonHtml(item){
  const reason=String(item.reason||'');
  const diagnostics=Array.isArray(item.source_diagnostics)?item.source_diagnostics:[];
  const messages=diagnostics.map(d=>String(d.message||'')).filter(m=>m&&!reason.includes(m));
  const summary=[reason,...messages.slice(0,2)].filter(Boolean).join(' ');
  const details=diagnostics.map(d=>`<div class="importSourceDiagnostic"><strong>${esc(d.source_name||d.source_id||'Source')}</strong>${d.query?`<div class="muted">Search: ${esc(d.query)}</div>`:''}<pre>${esc(d.detail||d.message||'')}</pre></div>`).join('');
  const legacy=item.reason_detail?`<pre>${esc(item.reason_detail)}</pre>`:'';
  const captcha=diagnostics.some(d=>d.kind==='captcha')||/captcha/i.test(String(item.reason_detail||''));
  const help=captcha?'<p class="muted">The challenge belongs to this source engine session. Open the affected source in Sources → WebView and complete its verification. Metadata confidence does not guarantee a chapter-source match.</p>':'';
  return `${summary?`<div class="importReason">${esc(summary)}</div>`:''}${details||legacy?`<details class="importDiagnostics"><summary>Source diagnostics</summary>${help}${details}${legacy}</details>`:''}`;
}
function importItemHtml(run,item){
  const state=String(item.state||'unresolved');
  const suggestions=item.suggestions||[];
  const source=[item.source_name,item.source_language].filter(Boolean).join(' · ');
  const originalTitle=cleanImportDisplayTitle(item.title||`Backup item ${item.position}`);
  const english=String(item.english_title||'').trim();
  const showEnglish=english&&english.toLocaleLowerCase()!==originalTitle.toLocaleLowerCase();
  const backupCover=String(item.backup_thumbnail_url||item.reference_thumbnail_url||'');
  const backupDesc=String(item.backup_description||'').trim();
  const backupAuthor=String(item.backup_author||'').trim();
  const metadataMatches=((item.metadata_identity||{}).matches||[]);
  const metadataLine=metadataMatches.length?`<div class="importMetadataIdentity" title="Confidence in the work identity, not a verified chapter-source match"><span>Metadata identity:</span> ${metadataMatches.map(x=>`${esc(x.provider_name||x.provider||'DB')} ${Math.round(Number(x.confidence||0)*100)}%`).join(' · ')}</div>`:'';
  const referenceId=Number(item.reference_entry_id||0);
  const referenceTitle=String(item.reference_title||'').trim();
  const referenceDesc=String(item.reference_description||'').trim();
  const referenceCover=String(item.reference_thumbnail_url||(referenceId?`/api/v1/covers/${referenceId}`:''));
  const actions=(state==='unresolved'||state==='failed')?`<div class="importResolveActions"><button onclick="retryImportItem('${jsq(run.id)}',${Number(item.id)},this)">Retry unresolved</button><button class="dangerAction" onclick="skipImportItem('${jsq(run.id)}',${Number(item.id)},this)">Skip</button></div><div class="importSearchRow"><input id="importSearch-${Number(item.id)}" placeholder="Search AutoSourcarr catalog by title"><button onclick="searchImportCandidates('${jsq(run.id)}',${Number(item.id)},this)">Search</button></div>`:'';
  const paused=String(item.suggestion_state||'')==='paused';
  const pausedNotice='<div class="muted importNoCandidates">Discovery paused. Partial candidates are kept; use Retry unresolved to resume.</div>';
  const candidateEmpty=paused?pausedNotice:(Number(item.suggestion_checked||0)>0
    ? '<div class="muted importNoCandidates">No reliable match found after checking the local catalog and enabled sources. You can still search manually.</div>'
    : '<div class="muted importNoCandidates importSearchingCandidates">Searching local catalog and enabled sources for possible matches…</div>');
  const candidates=(state==='unresolved'||state==='failed')?`<div class="importCandidates" id="importCandidates-${Number(item.id)}">${suggestions.length?(paused?pausedNotice:'')+importCandidateChoices(run.id,item):candidateEmpty}</div>`:'';
  const reference=(state==='unresolved'||state==='failed')&&referenceId?`<div class="importReference"><img src="${esc(entryCoverUrl(referenceId))}" data-entry-cover="${referenceId}" alt="" loading="lazy" decoding="async"><div class="grow"><small>Enabled backup-source reference</small><strong>${esc(referenceTitle||`Entry ${referenceId}`)}</strong>${referenceDesc?`<p>${esc(referenceDesc)}</p>`:''}</div><button class="goodAction" onclick="matchImportItem('${jsq(run.id)}',${Number(item.id)},${referenceId},this)">Match source reference</button></div>`:'';
  const accepted=(item.entry_id&&['added','existing','resolving'].includes(state))?importAcceptedMatchHtml(run.id,item):'';
  const backup=comparisonCard('A · Backup entry',originalTitle,backupCover,source||item.backup_source||'Original source unknown',[
    ['Original title',esc(originalTitle)],['English hint',showEnglish?esc(english):'<span class="muted">No separate hint</span>'],['Author',reviewText(backupAuthor,120)],['Summary',reviewText(backupDesc)]
  ],item.source_url?`<details class="reviewMore"><summary>Original source URL</summary><div>${esc(item.source_url)}</div></details>`:'');
  const progress=['resolving','retrying'].includes(state)?'<p class="muted">Matching is in progress. Track this item in Tasks.</p>':'';
  const outcome=['skipped','failed'].includes(state)?'<p class="muted">'+esc(importStateLabel(state))+'. Saved decisions are retained.</p>':'';
  return `<article class="importItem ${esc(state)}" data-import-item="${Number(item.id)}"><div class="compareHeader"><h3>Review import match</h3><span class="importStateBadge ${esc(state)}">${esc(importStateLabel(state))}</span></div><div class="comparePair importComparison">${backup}<div class="importCandidatePane">${accepted}${candidates}${progress}${outcome}</div></div>${reference?`<details class="importReferenceDetails"><summary>Original source reference available</summary>${reference}</details>`:''}<details class="importEvidenceDetails"><summary>Matching evidence and source diagnostics</summary>${metadataLine}${importReasonHtml(item)}</details><div class="reviewDecisionBar importDecisionBar">${actions||'<span class="muted">Saved to your import history.</span>'}</div></article>`;
}
function updateImportCounts(run){
  const counts={all:Number(run.total||0),unresolved:Number(run.unresolved||0),added:Number(run.added||0),existing:Number(run.existing||0),failed:Number(run.failed||0),skipped:Number(run.skipped||0)};
  for(const [key,value] of Object.entries(counts)){const el=$(`#importCount${key.charAt(0).toUpperCase()+key.slice(1)}`);if(el)el.textContent=String(value);}
}
function importHasEnglishHint(item){
  const original=String(item.title||'').trim().toLocaleLowerCase();
  const english=String(item.english_title||'').trim().toLocaleLowerCase();
  return !!english&&english!==original;
}
function importPassesReviewFilter(item){
  const mode=String(importsState.review||'all');
  const reviewable=['unresolved','failed'].includes(String(item.state||''));
  const hasCandidates=(item.suggestions||[]).length>0;
  const hasReference=Number(item.reference_entry_id||0)>0;
  const hasEnglish=importHasEnglishHint(item);
  const checked=Number(item.suggestion_checked||0)>0;
  if(mode==='has_candidates')return reviewable&&hasCandidates;
  if(mode==='no_candidates')return reviewable&&checked&&!hasCandidates;
  if(mode==='searching')return reviewable&&!checked&&String(item.suggestion_state||'')!=='paused';
  if(mode==='has_reference')return reviewable&&hasReference;
  if(mode==='no_reference')return reviewable&&!hasReference;
  if(mode==='has_english')return reviewable&&hasEnglish;
  if(mode==='no_english')return reviewable&&!hasEnglish;
  return true;
}
function updateImportReviewCounts(){
  const items=importsState.items||[];
  const counts=importsState.reviewCounts||{
    all:items.length,
    has_candidates:items.filter(x=>['unresolved','failed'].includes(String(x.state||''))&&(x.suggestions||[]).length>0).length,
    no_candidates:items.filter(x=>['unresolved','failed'].includes(String(x.state||''))&&Number(x.suggestion_checked||0)>0&&(x.suggestions||[]).length===0).length,
    searching:items.filter(x=>['unresolved','failed'].includes(String(x.state||''))&&Number(x.suggestion_checked||0)<=0&&String(x.suggestion_state||'')!=='paused').length,
    has_reference:items.filter(x=>['unresolved','failed'].includes(String(x.state||''))&&Number(x.reference_entry_id||0)>0).length,
    no_reference:items.filter(x=>['unresolved','failed'].includes(String(x.state||''))&&Number(x.reference_entry_id||0)<=0).length,
    has_english:items.filter(x=>['unresolved','failed'].includes(String(x.state||''))&&importHasEnglishHint(x)).length,
    no_english:items.filter(x=>['unresolved','failed'].includes(String(x.state||''))&&!importHasEnglishHint(x)).length,
  };
  for(const [key,value] of Object.entries(counts)){const el=$(`#importReviewCount-${key}`);if(el)el.textContent=String(value);}
}
function importPassesStateFilter(item){
  const wanted=String(importsState.filter||'all');
  return wanted==='all'||String(item.state||'')===wanted;
}
function patchImportItemDom(item){
  if(typeof patchImportWorkspace==='function'){patchImportWorkspace(item);return;}
  const card=document.querySelector(`[data-import-item="${Number(item.id)}"]`);
  const visible=importPassesStateFilter(item)&&importPassesReviewFilter(item);
  const run=importsState.runs.find(r=>String(r.id)===String(importsState.runId));
  if(card&&!visible)card.remove();
  else if(card&&visible&&run)card.outerHTML=importItemHtml(run,item);
  else if(visible&&!card){renderImportItems();return;}
  updateImportReviewCounts();
  const count=document.querySelectorAll('#importItems .importItem').length;
  const section=importsState.filter==='all'?'all items':`${importsState.filter} items`;
  const reviewLabel=importsState.review==='all'?'':` · ${String(importsState.review).replaceAll('_',' ')}`;
  const pending=pendingImportActions.size;
  $('#importItemsStatus').textContent=`Showing ${count} ${section}${reviewLabel}.${pending?` ${pending} background import action${pending===1?'':'s'} running…`:''}`;
}
function renderImportPagination(){
  const label=$('#importPageLabel'),previous=$('#importPrev'),next=$('#importNext');
  if(!label)return;
  const total=Number(importsState.total||0),start=Number(importsState.offset||0),n=(importsState.items||[]).length;
  label.textContent=total?`${start+1}–${Math.min(start+n,total)} of ${total} · 50 per page`:'0 items';
  previous.disabled=importsState.loading||start<=0;
  next.disabled=importsState.loading||start+n>=total;
}
function renderImportItems(){
  const key=importViewKey(),same=importsState.loadedKey===key;
  const run=importsState.runs.find(r=>String(r.id)===String(importsState.runId));
  if(!same){
    if(typeof rememberImportDraft==='function')rememberImportDraft();
    if($('#importQueue'))$('#importQueue').innerHTML='';
    $('#importItemsStatus').textContent=importsState.error?'Unable to load import items: '+importsState.error:'Loading import items…';
    $('#importItems').innerHTML=importsState.error?'<div class="emptyTaskState">This view could not be loaded. Refresh to retry.</div>':'<div class="emptyTaskState">Loading this import page…</div>';
    renderImportPagination();return;
  }
  const visible=(importsState.items||[]).filter(importPassesStateFilter).filter(importPassesReviewFilter);
  $('#importItemsStatus').textContent=(importsState.error?'Refresh failed: '+importsState.error+'. Showing the last loaded page. ':'')+`Showing ${visible.length} on this page · ${Number(importsState.total||0)} matching items.${importsState.enrichmentPending>0?` Discovering possible matches for ${importsState.enrichmentPending} unresolved items…`:''}`;
  if(typeof renderImportWorkspace==='function')renderImportWorkspace(run,visible);
  else $('#importItems').innerHTML=visible.length?visible.map(item=>importItemHtml(run,item)).join(''):'<div class="emptyTaskState">No items match these import filters.</div>';
  updateImportReviewCounts();renderImportPagination();
}
function loadImportItems(){
  const run=importsState.runs.find(r=>String(r.id)===String(importsState.runId));
  if(!run){if(typeof rememberImportDraft==='function')rememberImportDraft();if($('#importQueue'))$('#importQueue').innerHTML='';importsState.items=[];importsState.total=0;importsState.offset=0;importsState.enrichmentPending=0;importsState.loading=false;$('#importItems').innerHTML='<div class="emptyTaskState">No backup imports yet.</div>';$('#importItemsStatus').textContent='';renderImportPagination();return Promise.resolve();}
  const key=importViewKey();
  if(importItemRequests.has(key))return importItemRequests.get(key);
  updateImportCounts(run);
  $('#importRunSummary').textContent=`${run.filename||'backup'} · ${run.state||''} · ${Number(run.total||0)} items · added ${Number(run.added||0)} · existing ${Number(run.existing||0)} · unresolved ${Number(run.unresolved||0)} · failed ${Number(run.failed||0)}`;
  importsState.loading=true;importsState.error='';
  if(importsState.loadedKey!==key)renderImportItems();else{$('#importItemsStatus').textContent='Refreshing import page…';renderImportPagination();}
  const state=importsState.filter==='all'?'':`&state=${encodeURIComponent(importsState.filter)}`;
  const url=`/api/admin/imports/${encodeURIComponent(run.id)}/items?limit=50&offset=${importsState.offset}&review=${encodeURIComponent(importsState.review)}${state}`;
  const request=(async()=>{
    try{
      const data=await api(url,{timeoutMs:15000});
      if(importViewKey()!==key)return data;
      importsState.items=(data.items||[]).map(item=>{
        const action=pendingImportActions.get(importActionKey(run.id,item.id));
        const current=importsState.items.find(x=>Number(x.id)===Number(item.id));
        // Even a GET started after the click can precede POST acceptance.
        return current&&action&&(!action.accepted||Number(item.decision_revision||0)<action.decisionRevision)?current:item;
      });importsState.enrichmentPending=Number(data.enrichment_pending||0);
      importsState.total=Number(data.total??importsState.items.length);importsState.reviewCounts=data.review_counts||null;
      importsState.offset=Number(data.offset||0);importsState.loadedKey=importViewKey();importsState.error='';importsState.loading=false;
      Object.assign(run,data.run||{});updateImportCounts(run);requestSurfaceRender('imports');
      return data;
    }catch(error){
      if(importViewKey()===key){importsState.error=String(error?.message||error);importsState.loading=false;requestSurfaceRender('imports');}
      throw error;
    }finally{importItemRequests.delete(key);}
  })();
  importItemRequests.set(key,request);return request;
}
function loadImports(){
  if(importsListRequest){
    if(importsListRequest.revision===importsState.revision)return importsListRequest;
    return importsListRequest.catch(()=>{}).then(()=>loadImports());
  }
  const revision=importsState.revision;
  importsListRequest=(async()=>{
    try{
      const data=await api('/api/admin/imports?limit=50',{timeoutMs:12000});
      if(revision!==importsState.revision)return;
      importsState.runs=data.runs||[];
      if(!importsState.runId||!importsState.runs.some(r=>String(r.id)===String(importsState.runId))){importsState.runId=String(importsState.runs[0]?.id||'');importsState.offset=0;}
      const select=$('#importRunSelect');
      select.innerHTML=importsState.runs.length?importsState.runs.map(r=>`<option value="${esc(r.id)}" ${String(r.id)===importsState.runId?'selected':''}>${esc(r.filename||'backup')} · ${esc(r.created_at||'')} · ${Number(r.total||0)} items</option>`).join(''):'<option value="">No imports yet</option>';
      await loadImportItems();importsState.failures=0;
      const active=importsState.runs.find(r=>String(r.id)===importsState.runId);
      if((active&&String(active.state)==='running')||importsState.enrichmentPending>0)scheduleImportsPoll();
      if(pendingImportActions.size)schedulePendingImportPoll(250);
    }catch(error){
      if(revision===importsState.revision){importsState.failures++;importsState.error=String(error?.message||error);requestSurfaceRender('imports');scheduleImportsPoll();}
      throw error;
    }finally{importsListRequest=null;}
  })();
  importsListRequest.revision=revision;
  return importsListRequest;
}
function scheduleImportsPoll(){
  if(importsPollTimer)return;
  importsPollTimer=setTimeout(async()=>{
    importsPollTimer=null;
    if(!activeSurface('imports')||document.hidden)return;
    if(structuralRenderBlocked('imports')){scheduleImportsPoll();return;}
    await loadImports().catch(()=>{});
  },Math.min(30000,2000*Math.pow(2,Math.min(4,importsState.failures))));
}
function importActionKey(runId,itemId){return `${String(runId)}:${Number(itemId)}`;}
function invalidateImportRequests(){
  // Retain the visible page, but fence off every pre-decision GET (including the
  // request that a completion refresh would otherwise coalesce with).
  const loaded=importsState.loadedKey===importViewKey();
  importsState.revision++;
  if(loaded)importsState.loadedKey=importViewKey();
  importsState.loading=false;
}
function acceptImportAction(key,action,response){
  if(pendingImportActions.get(key)!==action)return false;
  action.accepted=true;
  action.decisionRevision=Number(response.decision_revision??action.decisionRevision);
  invalidateImportRequests();
  schedulePendingImportPoll(250);
  return true;
}
function failImportAction(key,action,error,item,previous){
  if(pendingImportActions.get(key)!==action)return;
  invalidateImportRequests();
  // A disconnected client cannot tell whether a POST was committed. Reconcile
  // with its next decision revision instead of presenting a false rollback.
  if(!error.status_code||Number(error.status_code)>=500){
    action.accepted=true;action.uncertainUntil=Date.now()+60000;
    if(item){item.reason='Checking whether the server accepted this action…';patchImportItemDom(item);}
    schedulePendingImportPoll(700);
    return;
  }
  pendingImportActions.delete(key);
  if(item&&previous){Object.assign(item,previous);patchImportItemDom(item);}
  loadImportItems().catch(()=>{});
}
function mergeImportItemSnapshot(runId,item){
  if(String(runId)!==String(importsState.runId))return;
  const idx=importsState.items.findIndex(x=>Number(x.id)===Number(item.id));
  if(idx>=0)importsState.items[idx]=item;
  // Off-page actions are reconciled through the next paginated GET, not appended.
  importsState.items.sort((a,b)=>Number(a.position||0)-Number(b.position||0));
}
function mergeImportRunSnapshot(run){
  if(!run)return;
  const current=importsState.runs.find(r=>String(r.id)===String(run.id));
  if(current)Object.assign(current,run);
  if(String(run.id)===String(importsState.runId))updateImportCounts(current||run);
}
async function refreshPendingImportActions(){
  if(!pendingImportActions.size)return;
  const pending=[...pendingImportActions.values()].filter(action=>action.accepted);
  const results=await Promise.all(pending.map(async action=>{
    try{
      const data=await api(`/api/admin/imports/${encodeURIComponent(action.runId)}/items/${action.itemId}`,{timeoutMs:5000,suppressClientError:true});
      return {action,data};
    }catch(error){return {action,error};}
  }));
  let changed=false,finished=false;
  for(const result of results){
    const {action}=result,key=importActionKey(action.runId,action.itemId);
    if(result.error||pendingImportActions.get(key)!==action)continue;
    const item=result.data?.item;
    if(!item)continue;
    if(Number(item.decision_revision||0)<action.decisionRevision){
      if(!action.uncertainUntil||Date.now()<action.uncertainUntil)continue;
      // No decision was observed after an ambiguous transport failure. Restore
      // the server's current state; never claim that a library addition succeeded.
      toast('The import action could not be confirmed. Check the item before retrying.');
    }
    mergeImportRunSnapshot(result.data?.run);
    mergeImportItemSnapshot(action.runId,item);
    changed=true;
    const actionDone=action.type==='reject'
      ? Number(item.suggestion_checked||0)>0
      : !['resolving','retrying'].includes(String(item.state||''));
    if(actionDone){pendingImportActions.delete(key);invalidateImportRequests();finished=true;}
  }
  if(changed&&String(importsState.runId))requestSurfaceRender('imports');
  if(finished){loadTasks().catch(()=>{});loadImportItems().catch(()=>{});}
  if(pendingImportActions.size)schedulePendingImportPoll(700);
}
function schedulePendingImportPoll(delay=700){
  if(importsPendingPollTimer||!pendingImportActions.size)return;
  importsPendingPollTimer=setTimeout(async()=>{
    importsPendingPollTimer=null;
    await refreshPendingImportActions().catch(()=>{});
  },Math.max(150,Number(delay)||700));
}
window.matchImportItem=(runId,itemId,entryId,button)=>{
  const item=importsState.items.find(x=>Number(x.id)===Number(itemId));
  const previous=item?{state:item.state,reason:item.reason,entry_id:item.entry_id,pending_match:item.pending_match}:null;
  const key=importActionKey(runId,itemId);
  if(button){button.disabled=true;button.textContent='Submitting…';}
  const action={runId:String(runId),itemId:Number(itemId),type:'match',previous,accepted:false,decisionRevision:Number(item?.decision_revision||0)+1};
  pendingImportActions.set(key,action);invalidateImportRequests();
  if(item){
    const chosen=(item.suggestions||[]).find(x=>Number(x.id)===Number(entryId))
      || (Number(item.reference_entry_id||0)===Number(entryId)?{id:Number(entryId),preferred_title:item.reference_title||`Entry ${entryId}`,description:item.reference_description||'',cover_url:item.reference_thumbnail_url||`/api/v1/covers/${entryId}`,sources:[item.source_name||''].filter(Boolean)}:null);
    item.state='resolving';
    item.reason='Submitting selected match…';
    item.entry_id=Number(entryId);
    item.applied_match=chosen||{id:Number(entryId),preferred_title:`Entry ${entryId}`,cover_url:`/api/v1/covers/${entryId}`,sources:[]};
    item.matched_entry=item.applied_match;
    item.pending_match=true;
    patchImportItemDom(item);
  }
  toast('Submitting match…');
  schedulePendingImportPoll(250);
  // Return control to the browser now so the card disappears/changes before even
  // the lightweight queue request is sent. The network work is never on the click path.
  setTimeout(async()=>{
    try{
      const r=await api(`/api/admin/imports/${encodeURIComponent(runId)}/items/${itemId}/match/${entryId}?background=true`,{method:'POST',body:'{}',timeoutMs:5000});
      if(r.error){const failure=new Error(r.error);failure.status_code=409;throw failure;}
      if(!acceptImportAction(key,action,r))return;
      if(item&&String(item.state)==='resolving')item.reason=r.already_running?'This import match is already running.':'Match accepted; adding to the library…';
      if(item&&importsState.filter==='all')patchImportItemDom(item);
      loadTasks().catch(()=>{});
      schedulePendingImportPoll(250);
    }catch(e){
      if(pendingImportActions.get(key)!==action)return;
      failImportAction(key,action,e,item,previous);
      toast('Unable to queue import match: '+e.message);
      if(button){button.disabled=false;button.textContent='Match';}
    }
  },0);
};
window.retryImportItem=(runId,itemId,button)=>{
  const item=importsState.items.find(x=>Number(x.id)===Number(itemId));
  const previous=item?{state:item.state,reason:item.reason,entry_id:item.entry_id}:null;
  const key=importActionKey(runId,itemId);
  if(button){button.disabled=true;button.textContent='Retrying';}
  const action={runId:String(runId),itemId:Number(itemId),type:'retry',previous,accepted:false,decisionRevision:Number(item?.decision_revision||0)+1};
  pendingImportActions.set(key,action);invalidateImportRequests();
  if(item){item.state='retrying';item.reason='Retry accepted; resolving in the background…';patchImportItemDom(item);}
  toast('Retry applied; background task started');
  schedulePendingImportPoll(250);
  setTimeout(async()=>{
    try{
      const r=await api(`/api/admin/imports/${encodeURIComponent(runId)}/items/${itemId}/retry?background=true`,{method:'POST',body:'{}',timeoutMs:5000});
      if(r.error){const failure=new Error(r.error);failure.status_code=409;throw failure;}
      if(!acceptImportAction(key,action,r))return;
      if(item&&String(item.state)==='retrying')item.reason=r.already_running?'This retry is already running.':'Retrying in the background…';
      if(item&&importsState.filter==='all')patchImportItemDom(item);
      loadTasks().catch(()=>{});
      schedulePendingImportPoll(250);
    }catch(e){
      if(pendingImportActions.get(key)!==action)return;
      failImportAction(key,action,e,item,previous);
      toast('Retry failed: '+e.message);
      if(button){button.disabled=false;button.textContent='Retry unresolved';}
    }
  },0);
};
window.rejectImportMatch=(runId,itemId,button)=>{
  if(!confirm('Reject this imported match? AutoSourcarr will remember the rejection across database resets. If this import added the work, it will be removed from the main Suwayomi library unless another accepted import still uses it.'))return;
  const item=importsState.items.find(x=>Number(x.id)===Number(itemId));
  if(!item)return;
  const previous=JSON.parse(JSON.stringify(item));
  const key=importActionKey(runId,itemId);
  if(button){button.disabled=true;button.textContent='Rejected';}
  const action={runId:String(runId),itemId:Number(itemId),type:'reject',previous,accepted:false,decisionRevision:Number(item?.decision_revision||0)+1};
  pendingImportActions.set(key,action);invalidateImportRequests();
  const oldTitle=String(item.matched_entry?.preferred_title||`Entry ${item.entry_id||''}`).trim();
  item.state='unresolved';
  item.reason=`Rejected previous match${oldTitle?`: ${oldTitle}`:''}. Looking for another match…`;
  item.entry_id=null;
  item.matched_entry=null;
  item.reference_entry_id=null;
  item.reference_title='';
  item.reference_description='';
  item.reference_thumbnail_url='';
  item.suggestions=[];
  item.suggestion_checked=0;
  patchImportItemDom(item);
  toast('Match rejected; alternative search is running in the background');
  schedulePendingImportPoll(250);
  setTimeout(async()=>{
    try{
      const r=await api(`/api/admin/imports/${encodeURIComponent(runId)}/items/${itemId}/reject`,{method:'POST',body:'{}',timeoutMs:5000});
      if(r.error){const failure=new Error(r.error);failure.status_code=409;throw failure;}
      if(!acceptImportAction(key,action,r))return;
      loadTasks().catch(()=>{});
      schedulePendingImportPoll(300);
    }catch(e){
      if(pendingImportActions.get(key)!==action)return;
      failImportAction(key,action,e,item,previous);
      toast('Unable to reject import match: '+e.message);
      if(button){button.disabled=false;button.textContent='Reject';}
    }
  },0);
};
window.skipImportItem=(runId,itemId,button)=>{
  if(!confirm('Skip this backup entry? It will remain visible in the Skipped section and nothing will be removed from Suwayomi.'))return;
  const item=importsState.items.find(x=>Number(x.id)===Number(itemId));
  const previous=item?JSON.parse(JSON.stringify(item)):null;
  const key=importActionKey(runId,itemId);
  const action={runId:String(runId),itemId:Number(itemId),type:'skip',previous,accepted:false,decisionRevision:Number(item?.decision_revision||0)+1};
  pendingImportActions.set(key,action);invalidateImportRequests();
  if(button){button.disabled=true;button.textContent='Skipped';}
  if(item){item.state='skipped';item.reason='Skipped by user';item.entry_id=null;patchImportItemDom(item);}
  toast('Import item skipped');
  setTimeout(async()=>{
    try{
      const r=await api(`/api/admin/imports/${encodeURIComponent(runId)}/items/${itemId}/skip`,{method:'POST',body:'{}',timeoutMs:5000});
      if(r.error){const failure=new Error(r.error);failure.status_code=409;throw failure;}
      if(!acceptImportAction(key,action,r))return;
      if(item){item.reason=r.reason||'Skipped by user';if(importsState.filter==='all')patchImportItemDom(item);}
    }catch(e){
      if(pendingImportActions.get(key)!==action)return;
      failImportAction(key,action,e,item,previous);
      toast('Unable to skip item: '+e.message);
      if(button){button.disabled=false;button.textContent='Skip';}
    }
  },0);
};
window.searchImportCandidates=async(runId,itemId,button)=>{
  const input=$(`#importSearch-${itemId}`),q=(input?.value||'').trim();
  if(!q){toast('Enter a title to search');return;}
  const item=importsState.items.find(x=>Number(x.id)===Number(itemId));
  const box=$(`#importCandidates-${itemId}`);
  if(!item||!box||String(runId)!==String(importsState.runId))return;
  const key=importActionKey(runId,itemId),viewKey=importViewKey();
  importSearchRequests.get(key)?.abort();
  const controller=new AbortController();importSearchRequests.set(key,controller);
  // Abort is best-effort: cached/in-flight responses can still finish. Validate
  // the query, rendered card and item before applying results or an auto-match.
  const current=()=>!controller.signal.aborted&&importSearchRequests.get(key)===controller
    &&importViewKey()===viewKey&&importsState.items.includes(item)
    &&$(`#importSearch-${itemId}`)===input&&$(`#importCandidates-${itemId}`)===box
    &&input.value.trim()===q&&['unresolved','failed'].includes(String(item.state));
  if(button){button.disabled=true;button.textContent='Searching…';}
  try{
    // Phase 1 is SQLite-only and should render almost immediately. Phase 2 then
    // checks the original/enabled live sources so a work does not need to have been
    // catalogued by some unrelated AutoSourcarr action before Imports can find it.
    const local=await api(`/api/admin/imports/${encodeURIComponent(runId)}/items/${itemId}/suggestions?q=${encodeURIComponent(q)}`,{timeoutMs:5000,signal:controller.signal});
    if(!current())return;
    item.suggestions=local.suggestions||[];
    box.innerHTML=item.suggestions.length?importCandidateChoices(runId,item):'<div class="muted">No local catalog match yet. Searching enabled sources…</div>';
    updateImportReviewCounts();
    if(button)button.textContent='Searching sources…';
    const live=await api(`/api/admin/imports/${encodeURIComponent(runId)}/items/${itemId}/suggestions/live?q=${encodeURIComponent(q)}`,{timeoutMs:25000,suppressClientError:true,signal:controller.signal});
    if(!current())return;
    item.suggestions=live.suggestions||item.suggestions||[];
    if(Number(live.reference_entry_id||0)>0)item.reference_entry_id=Number(live.reference_entry_id);
    box.innerHTML=item.suggestions.length?importCandidateChoices(runId,item):'<div class="muted">No matching entry found in the local catalog or enabled live sources.</div>';
    scheduleMetadataBurst();
    updateImportReviewCounts();
    const autoId=Number(live.auto_match_entry_id||0);
    if(autoId>0&&['unresolved','failed'].includes(String(item.state||'unresolved'))){
      toast(`Perfect import match accepted automatically${live.auto_match_reason?`: ${live.auto_match_reason}`:''}`);
      matchImportItem(runId,itemId,autoId,null);
      return;
    }
    if((live.live_sources||[]).length)toast(`Searched ${live.live_sources.length} enabled source${live.live_sources.length===1?'':'s'}`);
  }catch(e){
    if(!current())return;
    if(!(item.suggestions||[]).length)box.innerHTML='<div class="muted">No catalog candidates found for that search.</div>';
    toast('Search failed: '+e.message);
  }finally{
    if(importSearchRequests.get(key)===controller){
      importSearchRequests.delete(key);
      if(button){button.disabled=false;button.textContent='Search';}
    }
  }
};

function manualMemberCard(member, groupId='') {
  const title=member.source_title||`Source manga ${member.source_manga_id}`;
  const source=member.source_name||member.source_id;
  const owner=member.entry_id!=null?` · entry ${member.entry_id}`:'';
  const remove=groupId?`<button class="dangerAction compactButton" onclick="removeManualGroupMember('${jsq(groupId)}','${jsq(member.source_id)}',${Number(member.source_manga_id)},this)">Remove from group</button>`:'';
  return `<div class="manualMemberCard">
    <img src="${esc(member.cover_url||'')}" alt="" loading="lazy" decoding="async">
    <div class="manualMemberBody"><strong>${esc(title)}</strong><span>${esc(source)} · manga ${esc(member.source_manga_id)}${esc(owner)}</span>${remove}</div>
  </div>`;
}
function manualActionDate(value){const d=new Date(String(value||''));return Number.isNaN(d.getTime())?'':d.toLocaleString();}
function manualActionEntryButton(entryId,title=''){return Number(entryId)>0?`<button class="miniButton" onclick="switchTab('matrix');document.querySelector('#matrixSearch').value='${jsq(title||String(entryId))}';loadMatrix()">Open in matrix</button>`:'';}
function setManualActionsSubtab(name){
  const wanted=['matches','titles','separate','chapters','duplicates','tags'].includes(String(name))?String(name):'matches';manualActionsSubtab=wanted;
  document.querySelectorAll('.manualActionSubtab').forEach(b=>{const on=b.dataset.manualActionSubtab===wanted;b.classList.toggle('active',on);b.setAttribute('aria-selected',on?'true':'false');});
  document.querySelectorAll('.manualActionPanel').forEach(p=>p.classList.toggle('active',p.dataset.manualActionPanel===wanted));
  if($('#manualLedgerSearch'))$('#manualLedgerSearch').hidden=wanted==='tags';
  if(wanted==='tags'&&activeSurface('manual-actions'))window.loadTagReview?.();
  else if(typeof filterManualLedger==='function')filterManualLedger();
  const status=$('#manualActionsStatus');if(status)status.hidden=wanted==='tags'||!status.textContent;
  if(wanted!=='tags'&&manualActionsNeedsReconcile)scheduleManualActionsReconcile();
}
function renderManualGroups(){
  const groups=manualGroupsState.groups||[], singles=manualGroupsState.individual||[];
  $('#manualGroupsSummary').textContent=`${groups.length} persistent group${groups.length===1?'':'s'} · ${Number(manualGroupsState.manual_match_count||0)} persistent manual match${Number(manualGroupsState.manual_match_count||0)===1?'':'es'}`;
  $('#manualGroupsList').innerHTML=groups.length?groups.map(g=>`<article class="manualGroupCard" data-group-id="${esc(g.id)}">
    <div class="manualGroupHead"><div><span class="eyebrow">Persistent group</span><h3>${esc(g.title||'Manual group')}</h3><span class="muted">${Number(g.member_count||0)} source records${g.entry_id!=null?` · current entry ${esc(g.entry_id)}`:''}</span></div></div>
    <div class="manualGroupMembers">${(g.members||[]).map(m=>manualMemberCard(m,g.id)).join('')}</div>
    <div class="manualGroupAdd"><input id="manualGroupSearch-${esc(g.id)}" placeholder="Search catalog/source title to add a record"><button onclick="searchManualGroupCandidates('${jsq(g.id)}',this)">Search</button></div>
    <div id="manualGroupResults-${esc(g.id)}" class="manualGroupResults"></div>
  </article>`).join(''):'<div class="emptyDuplicateState"><strong>No manual groups yet.</strong><span>After you manually match two or more source records to the same AutoSourcarr entry, the group appears here and survives normal database resets.</span></div>';
  $('#manualSinglesList').innerHTML=singles.length?singles.map(m=>manualMemberCard(m,'')).join(''):'<div class="muted">No individual persistent manual matches.</div>';
}
function renderManualActions(){
  const counts=manualActionsState.counts||{};
  if($('#manualActionMatchesCount'))$('#manualActionMatchesCount').textContent=Number(counts.matches||0);
  if($('#manualActionTitlesCount'))$('#manualActionTitlesCount').textContent=Number(counts.titles||0);
  if($('#manualActionSeparateCount'))$('#manualActionSeparateCount').textContent=Number(counts.separate_entries||0);
  if($('#manualActionChapterCount'))$('#manualActionChapterCount').textContent=Number(counts.rejected_chapters||0);
  if($('#manualActionDuplicateCount'))$('#manualActionDuplicateCount').textContent=Number(counts.duplicate_decisions||0);
  renderManualGroups();

  const titles=manualActionsState.titles||[];
  $('#manualTitlesSummary').textContent=`${titles.length} entr${titles.length===1?'y':'ies'} with a manual title action · main-title overrides never propagate to future or different entries.`;
  $('#manualTitlesList').innerHTML=titles.length?titles.map(x=>{
    const scope=x.scope||[],aliases=x.aliases||[];
    return `<article class="manualActionCard"><div class="manualActionCardHead"><div><span class="eyebrow">Entry ${Number(x.entry_id)}</span><h3>${esc(x.entry_title||x.title||`Entry ${x.entry_id}`)}</h3>${x.title?`<div class="manualActionPrimary">Manual main title: <strong>${esc(x.title)}</strong></div>`:''}<div class="muted">${manualActionDate(x.updated_at)||'Manual alias only'}</div></div><div class="manualActionButtons">${manualActionEntryButton(x.entry_id,x.entry_title||x.title)}<button class="dangerAction" onclick="removeManualTitleAction(${Number(x.entry_id)},this)">Remove manual titles</button></div></div>
      ${scope.length?`<div class="manualScopeBox"><strong>Frozen match scope when title was set (${scope.length})</strong><div class="chipList">${scope.map(r=>`<span class="titleChip">${esc(r.source_name)} · ${Number(r.source_manga_id)}</span>`).join('')}</div><span class="muted">New matches are not added to this scope and cannot inherit this title.</span></div>`:'<div class="muted">No source records were attached when this manual title action was captured.</div>'}
      ${aliases.length?`<div class="manualScopeBox"><strong>Manual alternate titles</strong><div class="chipList">${aliases.map(a=>`<span class="titleChip">${esc(a.title)}${a.language?` <small>${esc(a.language)}</small>`:''}</span>`).join('')}</div></div>`:''}</article>`;
  }).join(''):'<div class="emptyDuplicateState"><strong>No manual titles.</strong><span>Main titles are currently automatic and no manual aliases are stored.</span></div>';

  const sep=manualActionsState.separate_entries||[];
  $('#manualSeparateSummary').textContent=`${sep.length} persistent separate-entry decision${sep.length===1?'':'s'}`;
  $('#manualSeparateList').innerHTML=sep.length?sep.map(x=>`<article class="manualActionCard"><div class="manualActionCardHead"><div><span class="eyebrow">Separate entry</span><h3>${esc(x.source_title||`Source manga ${x.source_manga_id}`)}</h3><div>${esc(x.source_name||x.source_id)} · manga ${Number(x.source_manga_id)}</div><div class="muted">Kept separate from ${esc(x.target_title||`entry ${x.target_entry_id}`)}${x.separate_title?` · own entry ${esc(x.separate_title)}`:''} · ${manualActionDate(x.updated_at)}</div></div><div class="manualActionButtons">${manualActionEntryButton(x.target_entry_id,x.target_title)}<button onclick="allowManualSeparateAction(${Number(x.target_entry_id)},'${jsq(x.source_id)}',${Number(x.source_manga_id)},this)">Allow match</button></div></div></article>`).join(''):'<div class="muted">No source records are manually forced to stay separate.</div>';

  const chapters=manualActionsState.rejected_chapters||[];
  $('#manualChapterSummary').textContent=`${chapters.length} rejected chapter candidate${chapters.length===1?'':'s'}`;
  $('#manualChapterList').innerHTML=chapters.length?chapters.map(x=>`<article class="manualActionCard"><div class="manualActionCardHead"><div><span class="eyebrow">Rejected chapter</span><h3>${esc(x.name||x.virtual_name||`Chapter ${x.source_chapter_id}`)}</h3><div>${esc(x.source_name||x.source_id)}${x.entry_title?` · ${esc(x.entry_title)}`:''}</div><div class="muted">${manualActionDate(x.rejected_at)}</div></div><div class="manualActionButtons">${manualActionEntryButton(x.entry_id,x.entry_title)}${Number(x.candidate_id)>0?`<button onclick="restoreManualRejectedChapter(${Number(x.candidate_id)},this)">Restore candidate</button>`:'<span class="muted">Candidate not currently cached</span>'}</div></div></article>`).join(''):'<div class="muted">No chapter candidates are manually rejected.</div>';

  const dup=manualActionsState.duplicate_decisions||[];
  $('#manualDuplicateSummary').textContent=`${dup.length} current manual duplicate decision${dup.length===1?'':'s'}`;
  $('#manualDuplicateList').innerHTML=dup.length?dup.map(x=>`<article class="manualActionCard"><div class="manualActionCardHead"><div><span class="eyebrow">${x.decision==='not_duplicate'?'Different works':'Manual duplicate'}</span><h3>${esc(x.left_title||`Entry ${x.left_entry_id}`)} ↔ ${esc(x.right_title||`Entry ${x.right_entry_id}`)}</h3><div class="muted">${manualActionDate(x.reviewed_at)}</div></div><div class="manualActionButtons"><button onclick="clearManualDuplicateDecision(${Number(x.left_entry_id)},${Number(x.right_entry_id)},this)">Clear decision</button></div></div></article>`).join(''):'<div class="muted">No current manual duplicate decisions.</div>';
  setManualActionsSubtab(manualActionsSubtab);
  if(typeof filterManualLedger==='function')filterManualLedger();
}
function setManualActionsStatus(message=''){
  const status=$('#manualActionsStatus');
  if(status){status.textContent=message;status.hidden=manualActionsSubtab==='tags'||!message;}
}
function manualActionsMutationRoute(url,method){
  const path=String(url||'').split('?')[0],verb=String(method||'GET').toUpperCase();
  if(!['POST','PUT','PATCH','DELETE'].includes(verb))return false;
  if(verb==='POST'&&/^\/api\/admin\/maintenance\/(reset-manual-groups|reset-manual-matches|reset-all-manual-actions|reset-database|full-reset)$/.test(path))return true;
  if(verb==='POST'&&path==='/api/admin/duplicates/review')return true;
  if(verb==='POST'&&/^\/api\/admin\/candidates\/\d+\/(reject|restore)$/.test(path))return true;
  if(verb==='POST'&&/^\/api\/admin\/manual-groups\/[^/]+\/members$/.test(path))return true;
  if(verb==='DELETE'&&/^\/api\/admin\/manual-groups\/[^/]+\/members\/[^/]+\/\d+$/.test(path))return true;
  // Import Match persists its source anchor before slow library reconciliation.
  if(verb==='POST'&&/^\/api\/admin\/imports\/[^/]+\/items\/\d+\/match\/\d+$/.test(path))return true;
  if(verb==='POST')return /^\/api\/admin\/entries\/\d+\/(preferred-title|aliases|manual-match|separate-source|unmatch-duplicates|unbind-auto-matches|redo-automation)$/.test(path)
    ||/^\/api\/admin\/entries\/\d+\/variants\/[^/]+\/\d+\/promote$/.test(path);
  if(verb==='DELETE')return /^\/api\/admin\/entries\/\d+(\/manual-titles|\/matches\/[^/]+|\/(variants|separate-source)\/[^/]+\/\d+)?$/.test(path);
  return false;
}
function scheduleManualActionsReconcile(){
  if(manualActionsMutationPending||manualActionsReconcileTimer||!manualActionsNeedsReconcile||manualActionsSubtab==='tags'||!activeSurface('manual-actions'))return;
  manualActionsReconcileTimer=setTimeout(()=>{
    manualActionsReconcileTimer=null;
    if(manualActionsMutationPending||!manualActionsNeedsReconcile||manualActionsSubtab==='tags'||!activeSurface('manual-actions'))return;
    if(manualActionsRequest?.generation===manualActionsGeneration)return;
    // One bounded read also reconciles a request whose acknowledgement was lost.
    loadManualActions().catch(()=>{});
  },50);
}
function beginManualActionsMutation(url,method){
  if(!manualActionsMutationRoute(url,method))return false;
  manualActionsGeneration++;manualActionsMutationPending++;manualActionsNeedsReconcile=true;manualActionsRequest=null;
  if(manualActionsReconcileTimer){clearTimeout(manualActionsReconcileTimer);manualActionsReconcileTimer=null;}
  setManualActionsStatus('Updating manual decisions… Saved decisions will refresh when the request finishes.');
  return true;
}
function finishManualActionsMutation(failed=false){
  // Completing any write invalidates reads made before its outcome was known.
  manualActionsGeneration++;manualActionsMutationPending=Math.max(0,manualActionsMutationPending-1);
  manualActionsNeedsReconcile=true;manualActionsRequest=null;
  if(manualActionsMutationPending)setManualActionsStatus('Updating manual decisions… Saved decisions will refresh when the requests finish.');
  else if(failed)setManualActionsStatus(activeSurface('manual-actions')&&manualActionsSubtab!=='tags'
    ?'The request failed; checking saved manual decisions…'
    :'The request failed. Refresh saved decisions to check the current state.');
  else setManualActionsStatus('Saved manual decisions need to be refreshed.');
  scheduleManualActionsReconcile();
}
async function loadManualActions(){
  if(manualActionsSubtab==='tags')return window.loadTagReview?.();
  if(manualActionsMutationPending){setManualActionsStatus('Updating manual decisions… Saved decisions will refresh when the requests finish.');return manualActionsState;}
  if(manualActionsReconcileTimer){clearTimeout(manualActionsReconcileTimer);manualActionsReconcileTimer=null;}
  const request={generation:++manualActionsGeneration};manualActionsRequest=request;manualActionsNeedsReconcile=true;
  const current=()=>manualActionsRequest===request&&request.generation===manualActionsGeneration&&!manualActionsMutationPending&&manualActionsSubtab!=='tags';
  setManualActionsStatus('Refreshing saved manual decisions…');
  try{
    const data=await api('/api/admin/manual-actions',{timeoutMs:15000,cache:'no-store'});
    if(!current())return manualActionsState;
    manualActionsState=data;
    manualGroupsState=data.matches||{groups:[],individual:[],group_count:0,manual_match_count:0};
    manualActionsNeedsReconcile=false;setManualActionsStatus();renderManualActions();return manualActionsState;
  }catch(error){
    if(!current())return manualActionsState;
    manualActionsNeedsReconcile=true;
    setManualActionsStatus('Unable to refresh saved decisions: '+error.message+'. Previous results may be out of date; use Refresh to check the current state.');
    throw error;
  }finally{if(manualActionsRequest===request)manualActionsRequest=null;}
}
async function loadManualGroups(){return loadManualActions();}
window.removeManualTitleAction=async(entryId,button)=>{if(!confirm('Remove the manual main title and manual alternate titles from this entry?'))return;if(button)button.disabled=true;try{await api(`/api/admin/entries/${Number(entryId)}/manual-titles`,{method:'DELETE'});toast('Manual titles removed');await Promise.allSettled([loadManualActions(),refreshBothMatrices()]);}catch(e){toast('Unable to remove manual titles: '+e.message);}finally{if(button)button.disabled=false;}};
window.allowManualSeparateAction=async(entryId,sourceId,mangaId,button)=>{if(button)button.disabled=true;try{await api(`/api/admin/entries/${Number(entryId)}/separate-source/${encodeURIComponent(sourceId)}/${Number(mangaId)}`,{method:'DELETE'});toast('Separate-entry decision removed');await Promise.allSettled([loadManualActions(),refreshBothMatrices()]);}catch(e){toast('Unable to remove separate-entry decision: '+e.message);}finally{if(button)button.disabled=false;}};
window.restoreManualRejectedChapter=async(candidateId,button)=>{if(button)button.disabled=true;try{await api(`/api/admin/candidates/${Number(candidateId)}/restore`,{method:'POST'});toast('Chapter candidate restored');await loadManualActions();}catch(e){toast('Unable to restore chapter: '+e.message);}finally{if(button)button.disabled=false;}};
window.clearManualDuplicateDecision=async(left,right,button)=>{if(button)button.disabled=true;try{await api('/api/admin/duplicates/review',{method:'POST',body:JSON.stringify({left_entry_id:Number(left),right_entry_id:Number(right),decision:'clear'})});duplicateConfirmedReviews.delete([Math.min(left,right),Math.max(left,right)].join(':'));duplicateViewRevision++;toast('Duplicate decision cleared');await Promise.allSettled([loadManualActions(),loadDuplicates(null,{render:$('#duplicates')?.classList.contains('active'),quiet:true})]);}catch(e){toast('Unable to clear duplicate decision: '+e.message);}finally{if(button)button.disabled=false;}};

window.searchManualGroupCandidates=async(groupId,button)=>{
  const input=document.getElementById(`manualGroupSearch-${groupId}`),out=document.getElementById(`manualGroupResults-${groupId}`);
  const q=String(input?.value||'').trim(); if(!q){if(out)out.innerHTML='<span class="muted">Enter a title first.</span>';return;}
  if(button)button.disabled=true;if(out)out.innerHTML='<span class="muted">Searching catalog…</span>';
  try{
    const data=await api(`/api/admin/manual-groups/${encodeURIComponent(groupId)}/candidates?q=${encodeURIComponent(q)}&limit=60`,{timeoutMs:20000});
    const items=data.items||[];
    if(out)out.innerHTML=items.length?items.map(x=>`<div class="manualCandidateCard"><img src="${esc(x.cover_url||'')}" alt="" loading="lazy" decoding="async"><div><strong>${esc(x.source_title||x.preferred_title)}</strong><span>${esc(x.source_name)} · ${esc(x.preferred_title||'')}</span></div><button class="goodAction" onclick="addManualGroupMember('${jsq(groupId)}','${jsq(x.source_id)}',${Number(x.source_manga_id)},this)">Add</button></div>`).join(''):'<span class="muted">No matching source records found.</span>';
  }catch(e){if(out)out.innerHTML=`<span class="badText">${esc(e.message)}</span>`;}finally{if(button)button.disabled=false;}
};
window.addManualGroupMember=async(groupId,sourceId,mangaId,button)=>{
  if(button)button.disabled=true;
  try{await api(`/api/admin/manual-groups/${encodeURIComponent(groupId)}/members`,{method:'POST',body:JSON.stringify({source_id:String(sourceId),source_manga_id:Number(mangaId)}),timeoutMs:10000});toast('Added to persistent manual group');await loadManualGroups();refreshBothMatrices().catch(()=>{});}
  catch(e){toast('Unable to add group member: '+e.message);if(button)button.disabled=false;}
};
window.removeManualGroupMember=async(groupId,sourceId,mangaId,button)=>{
  if(!confirm('Remove this source record from the persistent group? The current source match will stay attached, but it will no longer be forced to stay in this group after future rebuilds.'))return;
  if(button)button.disabled=true;
  try{await api(`/api/admin/manual-groups/${encodeURIComponent(groupId)}/members/${encodeURIComponent(sourceId)}/${Number(mangaId)}`,{method:'DELETE'});toast('Removed from persistent group');await loadManualGroups();}
  catch(e){toast('Unable to remove group member: '+e.message);if(button)button.disabled=false;}
};

// User-visible background task monitor. Keep the small navigation badge fresh even
// when the Tasks tab is not open, without forcing any expensive catalog refresh.
function taskProgressText(task){
  const current=Number(task.current||0),total=Number(task.total||0);
  return total>0?`${current}/${total}`:(task.state==='running'&&!['queued','waiting'].includes(task.activity)?'Working…':'');
}
function taskDurationText(seconds){
  const value=Math.max(0,Math.round(Number(seconds||0)));
  if(value<60)return `${value}s`;
  const hours=Math.floor(value/3600),minutes=Math.floor((value%3600)/60),secs=value%60;
  if(hours>0)return `${hours}h ${minutes}m`;
  return minutes<10&&secs?`${minutes}m ${secs}s`:`${minutes}m`;
}
function taskTimingText(task){
  if(String(task.state||'')!=='running')return '';
  if(['queued','waiting'].includes(task.activity))return `Waiting ${taskDurationText(task.waiting_seconds||0)}`;
  const elapsed=Math.max(0,Number(task.elapsed_seconds||0));
  const eta=task.eta_seconds===null||task.eta_seconds===undefined?null:Math.max(0,Number(task.eta_seconds));
  const total=Number(task.total||0),current=Number(task.current||0);
  const parts=[`Elapsed ${taskDurationText(elapsed)}`];
  if(eta!==null&&Number.isFinite(eta))parts.push(`ETA ~${taskDurationText(eta)}`);
  else if(total>0&&current<=0)parts.push('ETA after first item');
  return parts.join(' · ');
}
function taskUpdateText(task){
  if(String(task.state||'')!=='running')return '';
  const updated=Date.parse(task.updated_at||'');
  if(!Number.isFinite(updated))return '';
  const age=Math.max(0,(Date.now()-updated)/1000);
  return age>=60?`Last task update ${taskDurationText(age)} ago`:'';
}
function taskFinishedText(task){
  if(String(task.state||'')==='running')return '';
  const raw=task.finished_at||task.updated_at||'';
  if(!raw)return '';
  const d=new Date(raw);if(Number.isNaN(d.getTime()))return '';
  return `Finished ${d.toLocaleString([], {year:'numeric',month:'short',day:'2-digit',hour:'2-digit',minute:'2-digit'})}`;
}
function taskNewestFirst(a,b){
  const ta=Date.parse(a.finished_at||a.updated_at||a.created_at||'')||0;
  const tb=Date.parse(b.finished_at||b.updated_at||b.created_at||'')||0;
  return tb-ta;
}
function taskCardHtml(t){
  const current=Number(t.current||0),total=Number(t.total||0);
  const pct=total>0?Math.max(0,Math.min(100,Math.round(current/total*100))):0;
  const state=String(t.state||'running');
  const activity=state==='running'&&['queued','waiting'].includes(t.activity)?t.activity:state;
  const active=state==='running'&&activity==='running';
  const progress=active?(total>0?`<div class="taskProgress"><div class="taskProgressBar" style="width:${pct}%"></div></div>`:`<div class="taskProgress indeterminate"><div class="taskProgressBar"></div></div>`):'';
  const message=(t.message||t.error)?(typeof diagnosticCause==='function'?diagnosticCause({message:t.message||t.error}).title:(t.message||t.error)):'';
  const repair=['source-repair','source-raw-input'].includes(t.kind),repairUi=globalThis.AutoSourcarrRepairTasks;
  const meta=[t.context||'',repair?repairUi?.metadata(t):'',taskProgressText(t),repair?t.message:message,repair?'':taskTimingText(t),taskUpdateText(t),taskFinishedText(t)].filter(Boolean).map(esc).join(' · ');
  const cancel=repair?(repairUi?.actions(t)||''):(state==='running'&&t.cancellable)?`<button class="dangerAction taskCancel" onclick="cancelTask('${jsq(t.id)}',this)">Cancel</button>`:'';
  const label=t.kind==='source-raw-input'?'code needed':repair?String(t.repair_state||state).replaceAll('-',' '):t.interrupted?'interrupted':activity;
  const retry=t.interrupted&&!t.cancel_requested&&t.kind==='library-monitor'?`<button data-safe-task-retry="${esc(t.id)}">Retry observation</button>`:'';
  return `<article class="taskCard ${esc(activity)}${repair?' repairTaskCard':''}" data-origin="${esc(t.origin||'unknown')}"><div class="taskCardHead"><div><strong>${esc(t.name||'Background task')}</strong><div class="muted">${meta||esc(state)}</div></div><div class="taskCardActions"><span class="taskState ${esc(activity)}">${esc(label)}</span>${cancel}${retry}</div></div>${t.origin?`<span class="taskOrigin">${t.origin==='manual'?'Your action':'Automatic'}</span>`:''}${!repair&&(t.error||String(t.message||'').length>280)?`<details><summary>Technical details</summary><pre>${esc([t.message,t.error].filter(Boolean).join('\n\n'))}</pre></details>`:''}${progress}${active&&total>0?`<div class="taskNumbers"><span>${current.toLocaleString()} / ${total.toLocaleString()}</span><span>${pct}%</span></div>`:''}</article>`;
}
function renderTaskSection(listId,countId,items,emptyText){
  const list=$(listId),count=$(countId);if(count)count.textContent=String(items.length);
  if(!list)return;
  list.innerHTML=items.length?items.map(taskCardHtml).join(''):`<div class="emptyTaskState"><span>${esc(emptyText)}</span></div>`;
}
function renderTasks(){
  globalThis.AutoSourcarrRepairTasks?.renderStatus(tasksState);
  const badge=$('#taskNavBadge');
  const running=Number(tasksState.running||0);
  if(badge){badge.textContent=String(running);badge.hidden=running<=0;}
  const summary=$('#tasksSummary');
  if(typeof updateActivityStrip==='function')updateActivityStrip();
  const repair=t=>['source-repair','source-repair-preparation','source-raw-input','source-raw-acquisition'].includes(t.kind);
  const all=tasksState.items||[],general=all.filter(t=>!repair(t)),repairs=all.filter(repair);
  const generalItems=typeof workbenchTaskItems==='function'?workbenchTaskItems(general):general;
  const repairItems=typeof workbenchRepairTaskItems==='function'?workbenchRepairTaskItems(repairs):repairs;
  const items=[...generalItems,...repairItems];
  const ongoing=items.filter(t=>String(t.state||'')==='running');
  const completed=items.filter(t=>String(t.state||'')==='completed').sort(taskNewestFirst);
  const failed=items.filter(t=>String(t.state||'')==='failed').sort(taskNewestFirst);
  renderTaskSection('#tasksRunningList','#tasksRunningCount',ongoing.filter(t=>!repair(t)),'No ongoing tasks.');
  renderTaskSection('#tasksCompletedList','#tasksCompletedCount',completed.filter(t=>!repair(t)),'No recently completed tasks.');
  renderTaskSection('#tasksFailedList','#tasksFailedCount',failed.filter(t=>!repair(t)),'No recent failed or cancelled tasks.');
  if(globalThis.AutoSourcarrRepairQueue)globalThis.AutoSourcarrRepairQueue.render(repairItems);
  else{
    renderTaskSection('#repairTasksRunningList','#repairTasksRunningCount',ongoing.filter(repair),'No ongoing repairs.');
    renderTaskSection('#repairTasksCompletedList','#repairTasksCompletedCount',completed.filter(repair),'No completed or ready repairs.');
    renderTaskSection('#repairTasksFailedList','#repairTasksFailedCount',failed.filter(repair),'No failed or blocked repairs.');
  }
  const repairCount=$('#repairTaskSectionCount');if(repairCount)repairCount.textContent=String(items.filter(repair).length);
  if(summary){
    const selected=$('#tasksRepairsPanel')?.classList.contains('active')?repairItems:generalItems;
    const current=selected.filter(t=>t.state==='running'),queued=current.filter(t=>t.activity==='queued').length,waiting=current.filter(t=>t.activity==='waiting').length;
    summary.textContent=`${current.length-queued-waiting} active · ${queued} queued · ${waiting} waiting · ${selected.filter(t=>t.state==='failed').length} failed or blocked`;
    if($('#tasksRepairsPanel')?.classList.contains('active')&&globalThis.AutoSourcarrRepairQueue)summary.textContent=globalThis.AutoSourcarrRepairQueue.summary();
    if(tasksState.history_error)summary.textContent+=' · '+tasksState.history_error;
  }
}
function taskUiDiagnosticContext(){
  const present=id=>!!document.getElementById(id),active=id=>!!document.getElementById(id)?.classList.contains('active');
  const rows=Array.isArray(tasksState.items)?tasksState.items:[],repairs=rows.filter(row=>row.kind==='source-repair');
  return {tasks_visible:active('tasks'),repairs_selected:active('tasksRepairsPanel'),
    lane:typeof workbenchState==='undefined'?'unknown':workbenchState.taskLane,
    repair_lane:typeof workbenchState==='undefined'?'unknown':workbenchState.repairLane,
    all_filter_present:!!document.querySelector('[data-task-lane="all"]'),
    repair_all_filter_present:!!document.querySelector('[data-repair-lane="all"]'),
    repair_lists_present:['repairTasksRunningList','repairTasksCompletedList','repairTasksFailedList'].every(present),
    observed_worker_jobs:repairs.length,observed_blocked_jobs:repairs.filter(row=>row.repair_state==='blocked').length};
}
window.cancelTask=async(taskId,button)=>{
  if(button)button.disabled=true;
  try{await apiV1().cancelTask(taskId);toast('Task cancelled');await loadTasks();}
  catch(e){toast('Unable to cancel task: '+e.message);if(button)button.disabled=false;}
};

async function loadTasks(){
  // Tab changes, refresh clicks and the background timer share one request. Never
  // let a delayed older response overwrite a newer task snapshot.
  if(tasksLoadPromise)return tasksLoadPromise;
  tasksLoadPromise=(async()=>{
    try{
      tasksState=await apiV1({timeoutMs:12000,suppressClientError:true,cache:'no-store'}).getTasks();
      tasksLastSuccess=Date.now();tasksPollFailures=0;
      if(typeof updateActivityStrip==='function')updateActivityStrip();
      if(surfaceIsActive('tasks'))requestSurfaceRender('tasks');else renderTasks();
      return true;
    }catch(e){
      tasksPollFailures++;
      if($('#tasksSummary')&&document.querySelector('#tasks.active')){
        const last=tasksLastSuccess?` · showing the last snapshot from ${new Date(tasksLastSuccess).toLocaleTimeString()}`:'';
        $('#tasksSummary').textContent='Task status unavailable: '+e.message+last+' · retrying automatically';
      }
      return false;
    }
  })();
  try{return await tasksLoadPromise;}finally{tasksLoadPromise=null;}
}
function startTaskPolling(){
  if(tasksPollTimer)return;
  const poll=async()=>{
    tasksPollTimer='running';
    loadAttention().catch(()=>{});
    await loadTasks();
    const running=Number(tasksState.running||0)>0;
    const tasksOpen=!!document.querySelector('#tasks.active');
    const importsOpen=!!document.querySelector('#imports.active');
    const delay=document.hidden?60000:(tasksPollFailures?Math.min(30000,3000*2**Math.min(tasksPollFailures,4)):liveEvents.connected?30000:(running||tasksOpen||importsOpen?3000:30000));
    tasksPollTimer=setTimeout(poll,delay);
  };
  poll();
}
window.addEventListener('visibilitychange',()=>{
  if(!document.hidden && tasksPollTimer && tasksPollTimer!=='running'){
    clearTimeout(tasksPollTimer);tasksPollTimer=null;startTaskPolling();
  }
});

// Settings editor: config.json through /api/v1/settings (see settingsFromApi below). Changes apply after a restart.
function settingsFile(){
  return settingsState.data?.files?.find(f=>f.id===settingsState.activeFile)||null;
}
function settingsInput(item){
  const managed=item.key==='DATABASE_DIR'||item.editable===false;
  const common=`data-env-key="${esc(item.key)}" data-env-kind="${esc(item.kind||'text')}"${managed?' disabled title="Set this in Compose"':''}`;
  if(item.kind==='boolean'){
    const val=String(item.value||'').toLowerCase();
    return `<select ${common}><option value="true" ${val==='true'?'selected':''}>true</option><option value="false" ${val==='false'?'selected':''}>false</option></select>`;
  }
  if(item.kind==='textarea') return `<textarea ${common} rows="3" spellcheck="false">${esc(item.value||'')}</textarea>`;
  const type=item.kind==='password'?'password':(item.kind==='url'?'url':(item.kind==='integer'||item.kind==='number'?'number':'text'));
  const step=item.kind==='number'?' step="any"':'';
  return `<input ${common} type="${type}" value="${esc(item.value||'')}"${step} spellcheck="false">`;
}
function renderEnvSettings(){
  const data=settingsState.data;
  const tabs=$('#settingsFileTabs'), groups=$('#settingsGroups'), status=$('#settingsFileStatus');
  if(!data){tabs.innerHTML='';groups.innerHTML='';status.textContent='Loading settings…';return;}
  tabs.innerHTML=(data.files||[]).map(f=>`<button class="settingsFileTab ${f.id===settingsState.activeFile?'active':''}" data-settings-file="${esc(f.id)}">${esc(f.title)}</button>`).join('');
  tabs.querySelectorAll('[data-settings-file]').forEach(b=>b.onclick=()=>{if(settingsState.saving)return;if(settingsState.dirty&&!confirm('Discard unsaved settings changes?'))return;settingsState.editRevision++;settingsState.activeFile=b.dataset.settingsFile;settingsState.dirty=false;renderEnvSettings();});
  const file=settingsFile();
  if(!file){groups.innerHTML='';status.textContent='No settings file selected.';return;}
  const stateBits=[file.path,file.exists?'file found':'file missing',file.writable?'writable':'read-only'];
  status.textContent=stateBits.join(' · ');
  if(file.errors?.length){groups.innerHTML=`<div class="notice settingsError"><strong>Cannot edit this file.</strong><br>${file.errors.map(esc).join('<br>')}</div>`;return;}
  groups.innerHTML=(file.groups||[]).map((g,index)=>`<details class="settingsGroup" ${index===0?'open':''}><summary class="settingsGroupHeader"><span class="settingsGroupTitle"><span class="settingsChevron" aria-hidden="true">›</span><h3>${esc(g.name)}</h3></span><span class="muted">${g.items.length} setting${g.items.length===1?'':'s'}</span></summary><div class="settingsFieldGrid">${g.items.map(item=>`<label class="settingsField"><span class="settingsFieldTitle"><strong>${esc(item.label)}</strong><code>${esc(item.key)}</code></span>${item.description?`<small>${esc(item.description)}</small>`:''}${settingsInput(item)}${item.resettable?`<button type="button" class="tinyButton" data-env-reset="${esc(item.key)}">${settingsState.resets?.has(item.key)?'Undo':item.secret?'Remove saved value':'Use default'}</button>`:''}</label>`).join('')}</div></details>`).join('')||'<div class="emptyBrowserResult">No settings found.</div>';
  const notice=settingsState.notice;
  if(notice?.items?.length)groups.insertAdjacentHTML?.('afterbegin',`<div class="notice settingsUpdateNotice"><strong>Updated from ${esc(notice.from_version)} to ${esc(notice.to_version)}</strong><ul>${notice.items.map(item=>`<li><code>${esc(item.key)}</code> ${esc(item.message)}</li>`).join('')}</ul><button type="button" onclick="dismissSettingsUpdateNotice()">Dismiss</button></div>`);
  groups.querySelectorAll('[data-env-reset]').forEach(button=>button.onclick=event=>{event.preventDefault();const key=button.dataset.envReset;settingsState.resets??=new Set();if(settingsState.resets.has(key))settingsState.resets.delete(key);else settingsState.resets.add(key);markSettingsDirty();renderEnvSettings();});
  groups.querySelectorAll('[data-env-key]').forEach(input=>input.addEventListener('input',markSettingsDirty));
  groups.querySelectorAll('[data-env-key]').forEach(input=>input.addEventListener('change',markSettingsDirty));
  $('#saveSettings').disabled=!file.writable;
  $('#saveSettingsBottom').disabled=!file.writable;
  updateSettingsDirtyLabel();
  if(typeof filterSettings==='function')filterSettings();
}
function markSettingsDirty(){settingsState.editRevision++;settingsState.dirty=true;updateSettingsDirtyLabel();}
function updateSettingsDirtyLabel(){const n=$('#settingsDirtyState');if(n)n.textContent=settingsState.dirty?'Unsaved changes':'No unsaved changes';}
async function loadDatabaseStorage(){
  const state=$('#databaseStorageState'),detail=$('#databaseStorageDetail'),input=$('#databaseStoragePath'),button=$('#moveDatabaseStorage');
  if(state)state.textContent='Loading…';
  try{
    const d=await apiV1().getDatabaseStorage();databaseStorageState=d;
    if(input&&!input.matches(':focus'))input.value=d.active_dir||'';
    if(button)button.disabled=!d.move_available;
    const pending=d.pending_relocation;
    if(state)state.textContent=`${fmtBytes(d.bytes||0)} · ${d.active_dir||'unknown'}`;
    if(detail){
      if(!d.move_available)detail.textContent=d.move_note||'Moving the databases is not available in this version.';
      else if(pending&&pending.target_dir&&pending.activated_in_process)detail.textContent=`Using ${pending.target_dir} now. The old DB files are kept as a safety copy until the next redeploy verifies this location again.`;
      else if(pending&&pending.target_dir)detail.textContent=`Move staging to ${pending.target_dir}. The current DB remains authoritative until the final verified cut-over finishes.`;
      else detail.textContent='Enter an absolute writable path mounted into this container. AutoSourcarr copies, verifies and switches both DBs live without creating a fresh database.';
    }
  }catch(e){if(state)state.textContent='Unavailable';if(detail)detail.textContent=e.message;}
}
async function moveDatabaseStorage(){
  if(!databaseStorageState?.move_available){toast(databaseStorageState?.move_note||'Moving the databases is not available in this version.');return;}
  const input=$('#databaseStoragePath'),button=$('#moveDatabaseStorage');const path=String(input?.value||'').trim();
  if(!path){toast('Enter a database storage path');return;}
  if(!path.startsWith('/')){toast('Database storage must be an absolute container path');return;}
  if(!confirm(`Move both AutoSourcarr databases to ${path}?\n\nAutoSourcarr pauses database work, copies and checks both databases, then switches to the new folder. The old files stay where they are as a safety copy.`))return;
  if(button)button.disabled=true;
  try{
    const r=await apiV1({timeoutMs:30000}).moveDatabaseStorage({path});
    toast(r.message||'Databases moved');
    // The move runs as a job: wait for it in the task list (live events wake this up early).
    if(r.task_id){
      const result=await waitForTask(r.task_id,{timeoutMs:6*60*60*1000});
      toast(result.state==='completed'?(result.message||'Databases moved'):'Database move failed: '+(result.error||result.message||'see the task list'));
    }
    await Promise.allSettled([loadDatabaseStorage(),loadEnvSettings({force:true})]);
  }catch(e){toast('Database move failed: '+e.message);}
  finally{if(button)button.disabled=!databaseStorageState?.move_available;}
}
// The Settings page reads and saves config.json through /api/v1/settings. The response is shown in
// the page's existing file/group layout: one "file" (config.json) with the schema's groups.
function settingsFromApi(view){
  const kind=item=>item.secret?'password':item.type==='boolean'?'boolean':item.type==='integer'||item.type==='number'?item.type:item.type==='url'?'url':'text';
  const note=item=>[item.description,item.editable===false?'Set in Compose only.':'',item.overridden_by_environment?'Currently decided by a Compose variable; saving a value here makes this app decide instead.':'',item.saved_in_app&&!item.secret?'Saved in this app.':'',item.secret?(item.is_set?'A value is saved; leave empty to keep it.':'Not set.'):'',item.problem?'Problem: '+item.problem:''].filter(Boolean).join(' ');
  return {files:[{id:'config',title:'config.json',path:'config.json',exists:!!view.config_file_exists,writable:true,revision:view.revision,
    groups:(view.groups||[]).map(group=>({name:group.name,items:group.items.map(item=>({key:item.key,label:item.label,description:note(item),kind:kind(item),value:item.secret?'':(item.value??''),secret:item.secret,overridden:item.overridden_by_environment,editable:item.editable!==false,resettable:item.editable!==false&&(item.secret?item.is_set:item.saved_in_app||item.is_set)}))}))}]};
}
function loadEnvSettings({force=false}={}){
  if(settingsState.loadPromise)return settingsState.loadPromise;
  if(settingsState.dirty&&!force)return Promise.resolve(settingsState.data);
  const revision=settingsState.editRevision;
  const loading=(async()=>{
    const [view,notice]=await Promise.all([apiV1().getSettings(),apiV1().getUpdateNotice().catch(()=>null)]);
    if(settingsState.editRevision!==revision)return settingsState.data;
    const data=settingsFromApi(view);
    settingsState.data=data;settingsState.notice=notice?.present?notice.notice:null;settingsState.resets=new Set();
    if(!data.files?.some(f=>f.id===settingsState.activeFile))settingsState.activeFile=data.files?.[0]?.id||'autosourcarr';
    settingsState.dirty=false;renderEnvSettings();loadDatabaseStorage().catch(()=>{});return data;
  })();
  settingsState.loadPromise=loading;
  return loading.finally(()=>{if(settingsState.loadPromise===loading)settingsState.loadPromise=null;});
}
async function saveEnvSettings(){
  if(settingsState.saving)return;
  const revision=settingsState.editRevision;
  const file=settingsFile(); if(!file)return;
  if(!file.writable){toast('Settings file is not writable inside the AutoSourcarr container');return;}
  // Only changed values are sent; an empty secret field keeps the saved secret.
  const saved=new Map((file.groups||[]).flatMap(group=>group.items).map(item=>[item.key,item]));
  const values={};
  // "Use default" sends null (Compose or the default decides again); "Remove saved value" sends an empty secret.
  for(const key of settingsState.resets||[])values[key]=saved.get(key)?.secret?'':null;
  $('#settingsGroups').querySelectorAll('[data-env-key]').forEach(input=>{
    const key=input.dataset.envKey,item=saved.get(key),value=String(input.value??'');
    if(key in values||item?.editable===false)return;
    if(item?.secret&&value==='')return;
    if(item&&!item.secret&&String(item.value??'')===value)return;
    values[key]=value;
  });
  settingsState.saving=true;
  const buttons=[$('#saveSettings'),$('#saveSettingsBottom')];buttons.forEach(b=>{if(b)b.disabled=true;});
  try{
    const r=await apiV1().saveSettings({revision:file.revision,values});
    if(settingsState.editRevision!==revision){file.revision=r.revision;toast('Saved. Newer edits remain unsaved.');return;}
    settingsState.dirty=false;
    toast(r.message||'Settings saved. Restart AutoSourcarr to apply them.');
    await loadEnvSettings();
    const status=$('#settingsFileStatus');if(status)status.textContent=`config.json · saved${r.restart_required?' · restart required':''}`;
  }catch(e){toast('Unable to save settings: '+e.message);}
  finally{settingsState.saving=false;const f=settingsFile();buttons.forEach(b=>{if(b)b.disabled=!(f?.writable);});updateSettingsDirtyLabel();}
}
async function dismissSettingsUpdateNotice(){
  try{await apiV1().dismissUpdateNotice();settingsState.notice=null;renderEnvSettings();}catch(e){toast(e.message);}
}
window.goDuplicates=async(entryId=null)=>{globalThis.AutoSourcarrMatchingReview?.showCandidates();switchTab('duplicates',{load:false});await loadDuplicates(entryId);};
window.goUnavailableSources=()=>{switchTab('sources');if($('#sourceAvailability')){$('#sourceAvailability').value='unavailable';renderSources();}};
window.goUnmatched=()=>{switchTab('matrix');$('#matrixMinMatches').value='0';renderMatrix('matrix');};

function fmtBytes(n){const v=Number(n||0);if(v<1024)return `${v} B`;if(v<1024**2)return `${(v/1024).toFixed(1)} KiB`;if(v<1024**3)return `${(v/1024**2).toFixed(1)} MiB`;return `${(v/1024**3).toFixed(2)} GiB`;}
function fmtWhen(x){if(!x)return 'Never';const d=new Date(x);return Number.isNaN(d.getTime())?String(x):d.toLocaleString();}
// ok===null: this server did not check the connection (shown neutral, never as Ready or Problem).
function connectionRow(title,ok,detail,extra=''){const tone=ok===null?'warn':ok?'good':'bad';return `<div class="connectionRow"><span class="connectionIcon ${tone}">${ok===null?'?':ok?'✓':'!'}</span><div class="grow"><strong>${esc(title)}</strong><div class="muted">${esc(detail||'')}</div>${extra?`<div class="tinyText">${esc(extra)}</div>`:''}</div><span class="connectionState ${tone}">${ok===null?'Not checked':ok?'Ready':'Problem'}</span></div>`;}
function simpleMetric(label,value,note='',tone=''){return `<div class="simpleMetric ${tone}"><div><strong>${esc(value)}</strong><span>${esc(label)}</span></div>${note?`<small>${esc(note)}</small>`:''}</div>`;}
function simpleMetricAction(label,value,note,action,tone=''){return `<button class="simpleMetric metricAction ${tone}" onclick="${action}"><div><strong>${esc(value)}</strong><span>${esc(label)}</span></div>${note?`<small>${esc(note)}</small>`:''}</button>`;}
function metricCard(label,value,sub=''){return `<div class="metricCard"><span class="metricLabel">${esc(label)}</span><strong>${esc(value)}</strong>${sub?`<span class="muted">${esc(sub)}</span>`:''}</div>`;}
function renderStatus(h){
  const st=h.stats||{},cat=st.catalog||{},ch=st.chapters||{},src=st.sources||{},cache=st.cache||{},perf=st.performance||{},storage=st.storage||{},lib=h.library||{};
  const unchecked=part=>part?.checked===false;
  const sourceOk=unchecked(h.source_host)||!!h.source_host?.ok, mainConfigured=!!h.main_suwayomi?.configured, mainOk=unchecked(h.main_suwayomi)||!!h.main_suwayomi?.ok;
  const transConfigured=!!h.translation?.configured, transOk=unchecked(h.translation)||!!h.translation?.ok;
  const duplicateCount=Number(cat.possible_duplicates||0), duplicateGroups=Number(cat.duplicate_groups||0);
  const duplicateReady=cat.duplicate_snapshot_ready!==false;
  const unmatched=Number(cat.unmatched_entries||0), entries=Number(cat.entries||0);
  const matchedPct=entries?Math.round((Number(cat.matched_entries||0)/entries)*100):0;
  const attention=[];
  if(!sourceOk)attention.push({tone:'bad',title:'Source engine is unavailable',body:'Browsing and chapter refreshes cannot query Mihon extensions until it reconnects.',action:'goUnavailableSources()',label:'Review sources'});
  if(mainConfigured&&!mainOk)attention.push({tone:'warn',title:'Main Suwayomi is unavailable',body:'The Subbed tab cannot reliably tell which AutoSourcarr entries are in your library.',action:"switchTab('subbed')",label:'Open Subbed'});
  if(duplicateReady&&duplicateCount>0)attention.push({tone:'warn',title:`${duplicateCount} entries need duplicate review`,body:`${duplicateGroups||'Multiple'} candidate group(s) remain after automatic merging. Open the review page to see exactly which entries are being compared.`,action:'goDuplicates()',label:'Review duplicates'});
  if(Number(src.unavailable_variants||0)>0)attention.push({tone:'warn',title:`${src.unavailable_variants} source variant(s) unavailable`,body:'Their history is kept, but AutoSourcarr will not query them until they return.',action:'goUnavailableSources()',label:'Show unavailable sources'});
  if(transConfigured&&!transOk)attention.push({tone:'warn',title:'Title translator is configured but unavailable',body:'Normal matching still works, but origin-language/literal-title enrichment is temporarily skipped.'});
  if(unmatched>0)attention.push({tone:'warn',title:`${unmatched} unmatched catalog entries`,body:'These entries have no active source match and may need a manual search.',action:'goUnmatched()',label:'Show unmatched'});
  if(!attention.length)attention.push({tone:'good',title:'No action needed',body:'Connections, matching and cached browsing are currently in a usable state.'});
  $('#statusAttention').innerHTML=attention.map(x=>`<div class="attentionCard ${x.tone} ${x.action?'actionable':''}" ${x.action?`onclick="${x.action}" role="button" tabindex="0"`:''}><strong>${esc(x.title)}</strong><span>${esc(x.body)}</span>${x.action?`<b class="attentionActionLabel">${esc(x.label||'Open')} →</b>`:''}</div>`).join('');
  const ready=sourceOk&&(!mainConfigured||mainOk);
  $('#statusHeadline').textContent=ready?'AutoSourcarr is ready':'AutoSourcarr needs attention';
  if($('#statusVersion'))$('#statusVersion').textContent='v'+String(h.version||'unknown');
  $('#statusHeadlineSub').textContent=ready?`${entries} entries · ${matchedPct}% matched · ${cache.persistent_browse_feeds??0} saved browse feeds`:'Check the highlighted items below first.';
  $('#statusOverview').innerHTML=[
    connectionRow('AutoSourcarr',true,`v${h.version}`,'Backend and databases loaded'),
    connectionRow('Source engine',unchecked(h.source_host)?null:sourceOk,h.source_host?.url||'',h.source_host?.warning||h.source_host?.error||(h.source_host?.degraded?'Web server reachable · GraphQL busy':'Mihon extension host reachable')),
    connectionRow('Main Suwayomi',mainConfigured?(unchecked(h.main_suwayomi)?null:mainOk):true,mainConfigured?(h.main_suwayomi?.url||''):'Not configured',mainConfigured?(h.main_suwayomi?.warning||h.main_suwayomi?.error||'Library connection reachable'):'Only needed for the Subbed tab'),
    connectionRow('Title translation',!transConfigured?true:unchecked(h.translation)?null:transOk,transConfigured?(h.translation?.url||'Configured'):'Optional / disabled',transConfigured?(h.translation?.warning||h.translation?.error||`${h.translation?.languages||0} languages reported`):'Enable LibreTranslate for origin-language and literal-title searches'),
  ].join('');
  const downloadSize=lib.download_size||{};
  const downloadSizeValue=downloadSize.available?fmtBytes(downloadSize.bytes||0):(downloadSize.configured?'Unavailable':'Not mounted');
  const downloadSizeNote=downloadSize.available?`${Number(downloadSize.files||0).toLocaleString()} downloaded files counted`:String(downloadSize.error||'Set MAIN_SUWAYOMI_DOWNLOADS_PATH to show real disk usage');
  $('#statusCatalog').innerHTML=[
    simpleMetric('Catalogued entries',entries,'All works currently stored in AutoSourcarr'),
    simpleMetric('Subbed entries',lib.ok?Number(lib.subbed_entries||0).toLocaleString():'Unavailable',lib.ok?'AutoSourcarr titles in the main Suwayomi library':(lib.error||'Main Suwayomi library unavailable')),
    simpleMetric('Downloaded chapters',lib.ok?Number(lib.downloaded_chapters||0).toLocaleString():'Unavailable','Count reported by the main Suwayomi library'),
    !duplicateReady?simpleMetric('Duplicate review','Preparing…','The duplicate index is building in the background'):duplicateCount?simpleMetricAction('Duplicate candidates',duplicateCount,`${duplicateGroups} group(s) waiting for review`,'goDuplicates()','warn'):simpleMetric('Duplicate candidates',0,'Nothing waiting for review','good'),
    unmatched?simpleMetricAction('Unmatched',unmatched,'Open the matrix filtered to exactly 0 matches','goUnmatched()','warn'):simpleMetric('Unmatched',0,'Every entry has at least one source','good'),
    simpleMetricAction('Multi-record works',cat.entries_with_additional_matches??0,`${cat.alternate_source_records??0} additional source records contribute coverage`,"switchTab('matrix');$('#matrixStatus').value='multiple_records';renderMatrix('matrix')"),
  ].join('');
  $('#statusStorage').innerHTML=[
    simpleMetric('AutoSourcarr size',fmtBytes(storage.total_bytes||0),`${Number(storage.total_files||0).toLocaleString()} files · app/config/data/deps visible to this container · manga downloads excluded`),
    simpleMetric('Database size',fmtBytes(storage.database_bytes||0),`${Number(storage.database_files||0).toLocaleString()} SQLite file(s) including WAL/SHM`),
    simpleMetric('Downloaded size',downloadSizeValue,downloadSizeNote),
  ].join('');
  const latest=cache.latest_browse_refresh?fmtWhen(cache.latest_browse_refresh):'Not yet';
  const crawler=perf.continuous_catalog_cursor||{};
  const crawlerValue=perf.continuous_catalog_discovery?(crawler.last_source_name||'Starting…'):'Disabled';
  const crawlerNote=perf.continuous_catalog_discovery?(crawler.updated_at?`${crawler.last_mode||''} page ${crawler.last_page||1} · ${crawler.last_items||0} seen / ${crawler.last_new_items||0} new · ${fmtWhen(crawler.updated_at)}`:'Low-priority source walk starts after startup'):'Enable CONTINUOUS_CATALOG_DISCOVERY to keep expanding the catalog';
  $('#statusCache').innerHTML=[
    simpleMetric('Saved feeds',cache.persistent_browse_feeds??0,'Popular/Latest/search results can open from SQLite'),
    simpleMetric('Last feed refresh',latest,cache.active_background_refreshes?`${cache.active_background_refreshes} background refresh job(s) running`:'No feed refresh currently running'),
    simpleMetric('Cached covers',cache.cover_files??0,fmtBytes(cache.cover_bytes??0)),
    simpleMetric('Translation cache',cache.translation_cache_rows??0,cache.translation_jobs?`${cache.translation_jobs} title-enrichment job(s) running`:'No title-enrichment job running'),
    simpleMetric('Catalog crawler',crawlerValue,crawlerNote),
  ].join('');
  const pubs=cat.publication_statuses||{};
  $('#statusPublication').innerHTML=Object.entries(pubs).sort((a,b)=>b[1]-a[1]).map(([name,count])=>`<div class="breakdownRow"><span>${esc(name)}</span><strong>${esc(count)}</strong></div>`).join('')||'<span class="muted">No publication status data yet.</span>';
  $('#statusSources').innerHTML=[
    metricCard('Enabled source families',src.enabled_families??0,`${src.families??0} known`),metricCard('Enabled language variants',src.enabled_variants??0,`${src.variants??0} available`),
    metricCard('Unavailable variants',src.unavailable_variants??0),metricCard('Search variants',src.search_variants??0),metricCard('Chapter variants',src.chapter_variants??0),
    metricCard('Visible chapters',ch.virtual_chapters??0),metricCard('Chapter candidates',ch.chapter_candidates??0,'Primary + additional records'),metricCard('Served history',ch.served_history??0),
  ].join('');
  $('#statusPerformance').innerHTML=[
    metricCard('Interactive source limit',`${perf.interactive_source_timeout??'?'}s`),metricCard('Interactive page limit',`${perf.interactive_overall_timeout??'?'}s`),
    metricCard('Background source limit',`${perf.federated_source_timeout??0}s`),metricCard('Background page limit',`${perf.federated_overall_timeout??0}s`),
    metricCard('Source concurrency',perf.max_source_concurrency??0),metricCard('Source-host workers',perf.source_host_work_concurrency??0),metricCard('Background source slots',perf.background_source_concurrency??0),metricCard('Background task slots',perf.background_task_concurrency??0),metricCard('Import resolve slots',perf.import_resolve_concurrency??0),metricCard('Prefetched pages',perf.browse_prefetch_pages??1),metricCard('Items/source',perf.items_per_source??0),
    metricCard('Manual title queries',perf.manual_search_alias_limit??0),metricCard('Translation',perf.translation_enabled?'Enabled':'Disabled',perf.translation_target_languages||''),metricCard('Translation concurrency',perf.translation_concurrency??0),metricCard('Cover concurrency',perf.cover_fetch_concurrency??0),
  ].join('');
  $('#statusJson').textContent=JSON.stringify(h,null,2);
}
async function health(){
  const badge=$('#health');
  try{
    const h=await api('/api/v1/health',{cache:'no-store',timeoutMs:20000,suppressClientError:true,priority:'high'});
    lastHealthSnapshot=h;
    globalThis.AutoSourcarrReimagined?.renderStatistics(h);
    clientHealthError=null;
    const activeErrors=Number(h.errors?.active_errors||0);
    const activeWarnings=Number(h.errors?.active_warnings||0);
    // /health carries a small active-error snapshot. Preserve it so the error
    // drawer has useful content synchronously when the badge is clicked.
    if(Array.isArray(h.errors?.items)){
      lastBackendErrors={
        active_count:Number(h.errors?.active||0),
        active_error_count:activeErrors,
        active_warning_count:activeWarnings,
        items:h.errors.items,
        groups:h.errors.groups,
      };
    }
    if(!h.source_host?.ok&&h.source_host?.checked!==false){
      badge.textContent=h.source_engine?.state==='setup-required'?'integrated image required':'source engine unavailable';
      badge.className='pill healthButton fail';
    }else if(activeErrors>0){
      badge.textContent=`${activeErrors} active error${activeErrors===1?'':'s'}`;
      badge.className='pill healthButton fail';
    }else if(h.source_host?.degraded||activeWarnings>0){
      badge.textContent=h.source_host?.degraded?'source engine busy':`${activeWarnings} warning${activeWarnings===1?'':'s'}`;
      badge.className='pill healthButton warn';
    }else{
      badge.textContent='source engine online';
      badge.className='pill healthButton ok';
    }
    renderStatus(h);
    return h;
  }catch(e){
    const message=String(e?.message||e||'Health request failed');
    // A status probe timing out is not proof that the app is broken; the browser
    // already loaded this UI from AutoSourcarr. Keep it as connection context rather
    // than creating a self-reinforcing global browser error.
    clientHealthError={message,component:'AutoSourcarr health check',path:'/api/v1/health',method:'GET',last_seen:new Date().toISOString()};
    badge.textContent=lastHealthSnapshot?'status delayed':'status unavailable';
    badge.className='pill healthButton warn';
    if(lastHealthSnapshot) renderStatus(lastHealthSnapshot);
    else if($('#statusOverview'))$('#statusOverview').innerHTML=connectionRow('AutoSourcarr',true,'UI/backend reachable','Status details are temporarily unavailable: '+message);
    return null;
  }
}

function diagnosticConnectionRow(title,state,detail){
  const tone=state==='good'?'good':state==='warn'?'warn':'bad';
  const label=state==='good'?'Ready':state==='warn'?'Degraded':'Problem';
  return `<div class="diagnosticConnection ${tone}"><div><strong>${esc(title)}</strong><span>${esc(detail||'')}</span></div><b>${label}</b></div>`;
}
function errorItemHtml(item){
  if(typeof workbenchErrorHtml==='function')return workbenchErrorHtml(item);
  const resolved=!!item.resolved;
  const severity=String(item.severity||'error');
  const when=fmtWhen(item.last_seen||item.first_seen);
  const rawPath=String(item.path||'');const shownPath=rawPath.length>180?rawPath.slice(0,177)+'…':rawPath;
  const meta=[item.method&&shownPath?`${item.method} ${shownPath}`:shownPath,item.status_code?`HTTP ${item.status_code}`:'',Number(item.count||1)>1?`${Number(item.count)} occurrences`:'',when].filter(Boolean).join(' · ');
  return `<article class="diagnosticError ${severity} ${resolved?'resolved':''}"><div class="diagnosticErrorHead"><div><span class="errorStateBadge ${severity}">${resolved?'Resolved':severity==='warning'?'Warning':'Error'}</span><strong>${esc(item.component||'AutoSourcarr')}</strong></div><span class="muted">${esc(meta)}</span></div><div class="diagnosticMessage">${esc(item.message||'Unknown error')}</div>${item.detail?`<details><summary>Technical details</summary><pre>${esc(item.detail)}</pre></details>`:''}</article>`;
}
// Presentation-only browser grouping. Raw clientErrors retain request-specific
// recovery state and all detailed export fields. Do not replace that collection.
function groupBrowserDiagnostics(items){
  const route=path=>{
    try{
      const parsed=new URL(String(path||''),'https://autosourcarr.invalid');
      if(!parsed.pathname.startsWith('/api/'))return String(path||'');
      return parsed.pathname; // UI list query changes share an operation.
    }catch(_){return String(path||'');}
  };
  const groups=new Map();
  for(const item of items||[]){
    const message=String(item.message||'Unknown error').replace(/\s+/g,' ').replace(/\s*\(error [a-f0-9]{12}\)/gi,'').trim();
    const context=item.source_context||{},source=String(context.source_id||context.source_name||'');
    // Unknown per-source requests retain their full path: the browser cannot
    // prove that two unknown chapter failures came from the same source.
    let path=route(item.path);
    if(source)path=path.replace(/(\/api\/(?:admin|v1)\/(?:chapters|entries)\/)\d+(?=\/|$)/g,'$1{id}');
    const identity=JSON.stringify([!!item.client_side,item.component||'Browser / UI',item.severity||'error',Number(item.status_code||0),item.error_type||'',!!item.resolved,!!item.pinned,source,String(item.method||'').toUpperCase(),path,message]);
    let group=groups.get(identity);
    if(!group){
      group={...item,source_context:{...context},count:0,record_count:0,member_ids:[],paths:[],group_key:identity};
      groups.set(identity,group);
    }else if(String(item.last_seen||item.first_seen||'')>String(group.last_seen||group.first_seen||'')){
      for(const field of ['id','signature','message','detail','path','last_seen','resolved_at'])if(field in item)group[field]=item[field];
      group.source_context={...context};
    }
    group.count+=Math.max(1,Number(item.count)||1);group.record_count++;
    if(!group.member_ids.includes(item.id))group.member_ids.push(item.id);
    if(item.path&&!group.paths.includes(item.path))group.paths.push(item.path);
    if(item.first_seen&&(!group.first_seen||item.first_seen<group.first_seen))group.first_seen=item.first_seen;
  }
  return [...groups.values()].sort((a,b)=>Number(!!b.pinned&&!b.resolved)-Number(!!a.pinned&&!a.resolved)||String(b.last_seen||b.first_seen||'').localeCompare(String(a.last_seen||a.first_seen||'')));
}

function renderErrorsSnapshot(backend=lastBackendErrors,h=lastHealthSnapshot,backendLoadError=''){
  const summary=$('#errorPanelSummary'),connections=$('#errorConnectionSummary'),list=$('#errorList');
  const connectionRows=[];
  if(h){
    const sh=h.source_host||{};
    connectionRows.push(diagnosticConnectionRow('Source engine',sh.checked===false?'warn':sh.ok?(sh.degraded?'warn':'good'):'bad',sh.warning||sh.error||sh.url||''));
    const main=h.main_suwayomi||{};
    if(main.configured)connectionRows.push(diagnosticConnectionRow('Main Suwayomi',main.ok?'good':'bad',main.error||main.url||''));
    const tr=h.translation||{};
    if(tr.configured)connectionRows.push(diagnosticConnectionRow('Title translation',tr.ok?'good':'warn',tr.error||tr.url||''));
    const lib=h.library||{};
    if(h.main_suwayomi?.configured)connectionRows.push(diagnosticConnectionRow('Main library query',lib.ok===true?'good':lib.refreshing?'warn':'bad',lib.refreshing?'Refreshing cached library metrics…':(lib.error||`${Number(lib.subbed_entries||0)} subscribed entries`)));
  }else if(clientHealthError){
    connectionRows.push(diagnosticConnectionRow('Status details','warn',clientHealthError.message||'Status details are temporarily unavailable.'));
  }else{
    connectionRows.push(diagnosticConnectionRow('AutoSourcarr','good','The UI/backend is reachable. Current dependency status is still loading.'));
  }
  if(connections)connections.innerHTML=connectionRows.join('');

  const browserGroups=groupBrowserDiagnostics(clientErrors);
  const clientItems=browserGroups.filter(x=>!x.resolved);
  const backendItems=[...(backend?.groups||backend?.items||[])];
  const allItems=[...browserGroups,...backendItems];
  const items=typeof filterWorkbenchErrors==='function'?filterWorkbenchErrors(allItems):[...clientItems,...backendItems];
  if(list)list.innerHTML=items.length?items.map(errorItemHtml).join(''):'<div class="emptyTaskState"><strong>No issues in this view.</strong><span>Use Active issues or Resolved history to change the view.</span></div>';
  const active=Number(backend?.grouped_active_count??backend?.active_count??0)+clientItems.length;
  if(summary){const base=active?`${active} active browser/backend issue${active===1?'':'s'}. Service reachability and source website failures are shown separately.`:'No active errors. Resolved history is available in its own view.';summary.textContent=backendLoadError?`${base} Using cached diagnostics because refresh timed out.`:base;}
}

async function loadErrors(){
  const summary=$('#errorPanelSummary');
  // Paint what we already know immediately. v0.2.33/34 waited for the slow health
  // request before showing an error list that was already in memory.
  renderErrorsSnapshot();
  if(summary&&!lastBackendErrors.items?.length&&!clientErrors.length)summary.textContent='Loading recent error history…';

  const backendPromise=apiV1({cache:'no-store',timeoutMs:6000,suppressClientError:true,priority:'high'}).getErrors();
  // Attach both outcomes now: health may reject while history is still pending.
  const healthPromise=api('/api/v1/health',{cache:'no-store',timeoutMs:20000,suppressClientError:true,priority:'high'})
    .then(value=>({ok:true,value}),error=>({ok:false,error}));

  let backendLoadError='';
  try{
    lastBackendErrors=await backendPromise;
  }catch(e){
    backendLoadError=String(e?.message||e||'Unable to refresh error history');
  }
  // Error history is displayed as soon as its cheap endpoint returns; dependency
  // health can finish later and must never hold this panel hostage.
  renderErrorsSnapshot(lastBackendErrors,lastHealthSnapshot,backendLoadError);

  try{
    const outcome=await healthPromise;
    if(!outcome.ok)throw outcome.error;
    const h=outcome.value;
    lastHealthSnapshot=h;
    globalThis.AutoSourcarrReimagined?.renderStatistics(h);
    clientHealthError=null;
    renderStatus(h);
  }catch(e){
    clientHealthError={message:String(e?.message||e||'Health request failed'),component:'AutoSourcarr health check',path:'/api/v1/health',method:'GET',last_seen:new Date().toISOString()};
  }
  renderErrorsSnapshot(lastBackendErrors,lastHealthSnapshot,backendLoadError);
  return {health:lastHealthSnapshot,backend:lastBackendErrors,active:Number(lastBackendErrors.grouped_active_count??lastBackendErrors.active_count??0)+groupBrowserDiagnostics(clientErrors).filter(x=>!x.resolved).length};
}
function openErrors(){
  const panel=$('#errorPanel'),badge=$('#health');
  if(!panel)return;
  panel.hidden=false;
  if(badge)badge.setAttribute('aria-expanded','true');
  if(typeof workbenchState!=='undefined')workbenchState.diagnosticFocus=document.activeElement;
  if(typeof setDiagnosticsModal==='function')setDiagnosticsModal(true);
  // Render synchronously before scrolling so clicking the badge feels immediate.
  renderErrorsSnapshot();
  $('#closeErrors')?.focus();
  loadErrors().catch(e=>{if($('#errorPanelSummary'))$('#errorPanelSummary').textContent='Unable to load diagnostics: '+e.message;});
}

function closeErrors(){
  const panel=$('#errorPanel'),badge=$('#health');
  if(panel)panel.hidden=true;
  if(badge)badge.setAttribute('aria-expanded','false');
  if(typeof setDiagnosticsModal==='function')setDiagnosticsModal(false);
  if(typeof workbenchState!=='undefined')workbenchState.diagnosticFocus?.focus?.();
}

// Sources
async function putSource(id,body){return api('/api/admin/sources/'+encodeURIComponent(id),{method:'PUT',body:JSON.stringify(body)});}
function renderSourceGroupManager(){
  const root=$('#sourceGroupManager');if(!root)return;
  root.innerHTML=sourceGroupDefinitions.length?sourceGroupDefinitions.map(g=>`<div class="sourceGroupEditor" data-source-group-id="${Number(g.id)}"><input data-role="name" value="${esc(g.name)}" aria-label="Source group name"><label class="sourceGroupPriorityLabel">Priority <input data-role="priority" type="number" min="1" max="100000" value="${Number(g.priority)||1000}" aria-label="Source group priority"></label><span class="muted">${Number(g.member_variants||0)} variant(s)</span><button onclick="renameSourceGroup(${Number(g.id)},this)">Save</button><button onclick="openMatchingPolicy('group','${Number(g.id)}')">Matching rules</button><button class="danger ghost" onclick="deleteSourceGroup(${Number(g.id)})">Delete</button></div>`).join(''):'<span class="muted">No custom source groups yet.</span>';
}
function sourceGroupOptions(selected){
  return `<option value="">Individual source</option>`+sourceGroupDefinitions.map(g=>`<option value="${Number(g.id)}" ${Number(selected)===Number(g.id)?'selected':''}>${esc(g.name)}</option>`).join('');
}
function sourceFamilyVisual(g,i,{showMove=true}={}){
  const unavailable=g.variants.filter(v=>v.available===false||Number(v.available)===0).length;
  const iconVariant=g.variants.find(v=>v.icon_url)||g.variants[0]||{},rawSourceIcon=String(iconVariant.icon_url||'').trim();
  const sourceIconSrc=rawSourceIcon.startsWith('https://')?rawSourceIcon:(rawSourceIcon?`/api/v1/source-icon/${encodeURIComponent(iconVariant.source_id)}`:'');
  const initials=String(g.name||'?').split(/\s+/).filter(Boolean).slice(0,2).map(x=>x[0]).join('').toUpperCase();
  return `<div class="sourceNameWithIcon"><span class="sourceIconWrap"><span class="sourceIconFallback">${esc(initials)}</span>${sourceIconSrc?`<img class="sourceTableIcon" src="${esc(sourceIconSrc)}" alt="" loading="lazy" onerror="this.remove()">`:''}</span><span><strong>${esc(g.name)}</strong><br><span class="muted">${g.variants.length} language variant${g.variants.length===1?'':'s'}${unavailable?` · ${unavailable} unavailable`:''}</span>${showMove?`<br><label class="sourceMoveGroup">Group <select onchange="assignSourceFamilyGroup(${i},this.value)">${sourceGroupOptions(g.group_id)}</select></label>`:''}</span></div>`;
}
function sourceFamilyCells(g,i){
  return `<td>${sourceFamilyVisual(g,i)}</td><td><details class="langpicker"><summary>${esc(langSummary(g))}</summary><div class="langmenu"><div class="langactions"><button type="button" onclick="event.preventDefault();event.stopPropagation();setAllSourceLanguages(${i},true)">Select all</button><button type="button" onclick="event.preventDefault();event.stopPropagation();setAllSourceLanguages(${i},false)">Clear</button></div>${g.variants.map(v=>`<label class="${v.available===false||Number(v.available)===0?'unavailableVariant':''}"><input type="checkbox" ${v.enabled_language?'checked':''} onchange="setSourceLanguage(${i},'${jsq(v.source_id)}',this.checked)"><span>${esc(langLabel(v.lang))}${v.available===false||Number(v.available)===0?' (unavailable)':''}</span><span class="langcode">${esc(v.lang||'—')}</span></label>`).join('')}</div></details></td><td><input type="checkbox" ${groupSearch(g)?'checked':''} ${selectedVariants(g).length?'':'disabled'} onchange="updateSourceGroup(${i},'enabled_search',this.checked)"></td><td><input type="checkbox" ${groupChapters(g)?'checked':''} ${selectedVariants(g).length?'':'disabled'} onchange="updateSourceGroup(${i},'enabled_chapters',this.checked)"></td><td>${esc(g.base_url)}<br><div class="toolbar wrap"><button class="miniButton sourceWebviewButton" onclick="openSourceFamilyWebview(${i})">Open WebView</button><button class="miniButton" onclick="openSourcePreferences(${i})">Source settings</button><button class="miniButton" onclick="openMatchingPolicy('source','${jsq(g.variants[0]?.source_id||'')}')">Matching rules</button><button type="button" class="miniButton" data-gallery-policy="${i}">Gallery matching</button></div></td>`;
}
function renderSources(){
  const t=$('#sourcesTable');
  const availability=$('#sourceAvailability')?.value||'all';
  const shown=sourceGroups.map((g,i)=>({g,i})).filter(({g})=>{const any=g.variants.some(v=>v.available!==false&&Number(v.available)!==0),anyUnavailable=g.variants.some(v=>v.available===false||Number(v.available)===0);return availability==='available'?any:availability==='unavailable'?anyUnavailable:true;});
  const grouped=new Map(),individual=[];
  for(const item of shown){
    if(item.g.group_id==null){individual.push(item);continue;}
    const gid=Number(item.g.group_id);if(!grouped.has(gid))grouped.set(gid,[]);grouped.get(gid).push(item);
  }
  const blocks=[];
  for(const def of sourceGroupDefinitions){const members=grouped.get(Number(def.id))||[];if(members.length)blocks.push({kind:'group',priority:Number(def.priority)||1000,name:def.name,def,members});}
  for(const item of individual)blocks.push({kind:'individual',priority:Number(item.g.priority)||1000,name:item.g.name,item});
  blocks.sort((a,b)=>a.priority-b.priority||String(a.name||'').localeCompare(String(b.name||'')));
  const rows=[];
  for(const block of blocks){
    if(block.kind==='individual'){
      const {g,i}=block.item,unavailable=g.variants.some(v=>v.available===false||Number(v.available)===0);
      rows.push(`<tr class="${unavailable?'sourceHasUnavailable':''}"><td class="sourceGroupColumn"><select onchange="assignSourceFamilyGroup(${i},this.value)">${sourceGroupOptions(null)}</select></td><td><input type="number" min="1" max="100000" value="${Number(g.priority)||1000}" style="width:80px" onchange="updateSourceGroup(${i},'priority',this.value)"></td>${sourceFamilyCells(g,i)}</tr>`);
      continue;
    }
    const {def,members}=block;
    members.forEach(({g,i},memberIndex)=>{
      const unavailable=g.variants.some(v=>v.available===false||Number(v.available)===0);
      rows.push(`<tr class="sourceGroupedRow ${unavailable?'sourceHasUnavailable':''}">${memberIndex===0?`<td class="sourceGroupColumn sourceGroupSharedCell" rowspan="${members.length}"><strong>${esc(def.name)}</strong><span class="muted">${members.length} source${members.length===1?'':'s'}</span></td><td class="sourceGroupSharedCell" rowspan="${members.length}"><input type="number" min="1" max="100000" value="${Number(def.priority)||1000}" style="width:80px" onchange="updateCustomSourceGroupPriority(${Number(def.id)},this.value)"><span class="muted sourceSharedPriorityHint">shared</span></td>`:''}${sourceFamilyCells(g,i)}</tr>`);
    });
  }
  t.innerHTML='<tr><th>Group</th><th>Priority</th><th>Source</th><th>Languages</th><th>Search</th><th>Chapters</th><th>Base URL</th></tr>'+rows.join('');
  renderSourceGroupManager();
}
async function loadSources(){const d=await api('/api/admin/sources');sourceRows=d.items||[];sourceGroupDefinitions=d.groups||[];sourceGroups=sourceFamilies(sourceRows);renderSources();populateBrowseFilters();populateConnectedSourceFilters();globalThis.AutoSourcarrNavigation?.filtersReady();}
function syncSourceRowsToMatrices(){
  for(const data of [matrixData,subbedData])if(data)data.sources=sourceRows.map(row=>({...row}));
}
function applySourcePatch(sourceId,patch){
  const id=String(sourceId);
  const row=sourceRows.find(x=>String(x.source_id)===id);if(row)Object.assign(row,patch);
  for(const d of [matrixData,subbedData]){const r=(d?.sources||[]).find(x=>String(x.source_id)===id);if(r)Object.assign(r,patch);}
}
function rerenderSourceDrivenUi(){
  sourceGroups=sourceFamilies(sourceRows);renderSources();populateBrowseFilters();
  if($('#matrix')?.classList.contains('active')&&matrixData)rerenderMatrixPreservingScroll('matrix');
  if($('#subbed')?.classList.contains('active')&&subbedData)rerenderMatrixPreservingScroll('subbed');
}
window.updateSourceGroup=async(i,key,value)=>{const g=sourceGroups[i];if(!g)return;const patches=[];
  if(key==='priority'){const priority=Number(value);for(const v of g.variants)patches.push([v.source_id,{priority}]);}
  else{const enabled=!!value;for(const v of g.variants)patches.push([v.source_id,{[key]:enabled&&!!v.enabled_language}]);}
  const previous=patches.map(([id,p])=>[id,Object.fromEntries(Object.keys(p).map(k=>[k,sourceRows.find(x=>String(x.source_id)===String(id))?.[k]]))]);
  patches.forEach(([id,p])=>applySourcePatch(id,p));rerenderSourceDrivenUi();toast('Source updated');
  try{await Promise.all(patches.map(([id,p])=>putSource(id,p)));await loadSources();syncSourceRowsToMatrices();rerenderSourceDrivenUi();}
  catch(e){previous.forEach(([id,p])=>applySourcePatch(id,p));rerenderSourceDrivenUi();toast('Source update failed: '+e.message);}
};
window.updateCustomSourceGroupPriority=async(groupId,value)=>{const gid=Number(groupId),priority=Number(value);const def=sourceGroupDefinitions.find(x=>Number(x.id)===gid);if(!def)return;const previousPriority=Number(def.priority)||1000,previousRows=sourceRows.filter(x=>Number(x.group_id)===gid).map(x=>[x.source_id,x.priority]);def.priority=priority;for(const [id] of previousRows)applySourcePatch(id,{priority,group_priority:priority});rerenderSourceDrivenUi();toast(`${def.name} priority updated`);try{await api(`/api/admin/source-groups/${gid}`,{method:'PUT',body:JSON.stringify({priority})});await loadSources();syncSourceRowsToMatrices();rerenderSourceDrivenUi();}catch(e){def.priority=previousPriority;previousRows.forEach(([id,p])=>applySourcePatch(id,{priority:p,group_priority:previousPriority}));rerenderSourceDrivenUi();toast('Source group priority update failed: '+e.message);}};
window.assignSourceFamilyGroup=async(i,value)=>{const g=sourceGroups[i];if(!g)return;const groupId=value?Number(value):null;const previous=g.variants.map(v=>[v.source_id,v.group_id,v.group_name,v.priority,v.group_priority]);const def=sourceGroupDefinitions.find(x=>Number(x.id)===groupId);for(const v of g.variants)applySourcePatch(v.source_id,{group_id:groupId,group_name:def?.name||'',...(def?{priority:Number(def.priority)||1000,group_priority:Number(def.priority)||1000}:{group_priority:null})});rerenderSourceDrivenUi();try{await Promise.all(g.variants.map(v=>putSource(v.source_id,{group_id:groupId})));await loadSources();syncSourceRowsToMatrices();rerenderSourceDrivenUi();toast(groupId?`Grouped under ${def?.name||'source group'} · shared priority ${Number(def?.priority)||1000}`:'Source shown individually');}catch(e){previous.forEach(([id,gid,name,priority,groupPriority])=>applySourcePatch(id,{group_id:gid,group_name:name,priority,group_priority:groupPriority}));rerenderSourceDrivenUi();toast('Source group update failed: '+e.message);}};
window.createSourceGroup=async()=>{const input=$('#newSourceGroupName');const name=String(input?.value||'').trim();if(!name)return;try{await api('/api/admin/source-groups',{method:'POST',body:JSON.stringify({name,priority:1000})});if(input)input.value='';await loadSources();toast('Source group created');}catch(e){toast('Unable to create source group: '+e.message);}};
window.renameSourceGroup=async(groupId,button)=>{const root=button?.closest?.('[data-source-group-id]');const name=String(root?.querySelector('[data-role="name"]')?.value||'').trim();const priority=Number(root?.querySelector('[data-role="priority"]')?.value||1000);if(!name)return;try{await api(`/api/admin/source-groups/${Number(groupId)}`,{method:'PUT',body:JSON.stringify({name,priority})});await loadSources();syncSourceRowsToMatrices();rerenderSourceDrivenUi();toast('Source group updated');}catch(e){toast('Unable to update source group: '+e.message);}};
window.deleteSourceGroup=async(groupId)=>{if(!confirm('Delete this source group? Sources remain installed and will be shown individually.'))return;try{await api(`/api/admin/source-groups/${Number(groupId)}`,{method:'DELETE'});await loadSources();syncSourceRowsToMatrices();rerenderSourceDrivenUi();toast('Source group deleted');}catch(e){toast('Unable to delete source group: '+e.message);}};
window.setSourceLanguage=async(i,sourceId,checked)=>{const g=sourceGroups[i];if(!g)return;const variant=g.variants.find(v=>String(v.source_id)===String(sourceId));if(!variant)return;const hadSelected=selectedVariants(g).length>0;const search=hadSelected?groupSearch(g):true,chapters=hadSelected?groupChapters(g):true;const patch={enabled_language:!!checked,enabled_search:!!checked&&search,enabled_chapters:!!checked&&chapters};const previous={enabled_language:variant.enabled_language,enabled_search:variant.enabled_search,enabled_chapters:variant.enabled_chapters};applySourcePatch(sourceId,patch);rerenderSourceDrivenUi();toast(`${langLabel(variant.lang)} ${checked?'enabled':'disabled'}`);try{await putSource(sourceId,patch);loadSources().catch(()=>{});}catch(e){applySourcePatch(sourceId,previous);rerenderSourceDrivenUi();toast('Source update failed: '+e.message);}};
window.setAllSourceLanguages=async(i,checked)=>{const g=sourceGroups[i];if(!g)return;const hadSelected=selectedVariants(g).length>0;const search=hadSelected?groupSearch(g):true,chapters=hadSelected?groupChapters(g):true;const patches=g.variants.map(v=>[v.source_id,{enabled_language:!!checked,enabled_search:!!checked&&search,enabled_chapters:!!checked&&chapters}]);const previous=patches.map(([id,p])=>[id,Object.fromEntries(Object.keys(p).map(k=>[k,sourceRows.find(x=>String(x.source_id)===String(id))?.[k]]))]);patches.forEach(([id,p])=>applySourcePatch(id,p));rerenderSourceDrivenUi();toast(checked?'All languages selected':'Languages cleared');try{await Promise.all(patches.map(([id,p])=>putSource(id,p)));loadSources().catch(()=>{});}catch(e){previous.forEach(([id,p])=>applySourcePatch(id,p));rerenderSourceDrivenUi();toast('Source update failed: '+e.message);}};
if($('#sourceAvailability'))$('#sourceAvailability').onchange=renderSources;
$('#syncSources').onclick=async()=>{toast('Syncing sources…');try{await api('/api/admin/sources/sync',{method:'POST',timeoutMs:15000});await loadSources();syncSourceRowsToMatrices();rerenderSourceDrivenUi();toast('Sources synced');}catch(e){toast('Source sync failed: '+e.message);}};


function setSourceSubtab(name,persist=true){
  const oldOrphan=['orphaned','installed-orphan'].includes(name);
  const selected=oldOrphan?'manage':['manage','repositories','maintenance','health'].includes(name)?name:'installed',manage=selected==='manage';
  if(oldOrphan){const section=$(name==='orphaned'?'#sourceOrphanedPanel':'#sourceInstalledOrphanPanel');if(section)section.open=true;}
  document.querySelectorAll('.sourceSubtab').forEach(b=>b.classList.toggle('active',b.dataset.sourceSubtab===selected));
  $('#sourceInstalledPanel')?.classList.toggle('active',selected==='installed');
  $('#sourceHealthPanel')?.classList.toggle('active',selected==='health');
  if(selected==='health'&&activeSurface('sources'))window.loadSourceHealth?.();
  $('#sourceRepositoriesPanel')?.classList.toggle('active',selected==='repositories');
  $('#sourceMaintenancePanel')?.classList.toggle('active',selected==='maintenance');
  if(selected==='maintenance'&&activeSurface('sources'))window.loadSourceRepairs?.();
  if(activeSurface('sources')&&selected==='repositories'&&typeof loadRepositories==='function'&&!repositoryState.loaded)loadRepositories();
  $('#sourceManagePanel')?.classList.toggle('active',manage);
  if(persist)queueUiPreferenceSave('sourcesSubtab',selected);else writeLocalUiPreference('sourcesSubtab',selected);
  if(manage&&activeSurface('sources'))window.loadSourceOrphans?.();
  if(manage&&activeSurface('sources')&&!sourceExtensionState.loaded)loadSourceExtensions(false).catch(e=>toast('Extension catalog failed: '+e.message));
}
function extensionLangLabel(lang){return langLabel(lang||'all');}
function extensionActionButton(ext){
  const pkg=jsq(ext.pkgName||'');
  if(ext.isInstalled&&ext.hasUpdate&&!ext.isObsolete)return `<button class="primaryAction" onclick="manageSourceExtension('${pkg}','update',this)">Update</button><button class="uninstallExtension" onclick="manageSourceExtension('${pkg}','uninstall',this)">Uninstall</button>`;
  if(ext.isInstalled)return `<button class="uninstallExtension" onclick="manageSourceExtension('${pkg}','uninstall',this)">Uninstall</button>`;
  return `<button class="primaryAction" onclick="manageSourceExtension('${pkg}','install',this)">Install</button>`;
}
function extensionSettingsButton(ext){
  if(!ext.isInstalled)return '';
  const variants=ext.source?.nodes;
  if(Array.isArray(variants)&&variants.length&&variants.every(row=>row.isConfigurable===false))return '<button type="button" disabled title="This extension does not expose configurable source preferences.">No source settings</button>';
  return `<button type="button" onclick="openExtensionSourcePreferences('${jsq(ext.pkgName||'')}')">Source settings</button>`;
}
function extensionOrphanButton(ext){
  return ext.isInstalled&&ext.can_mark_orphan?`<button type="button" onclick="setSourceOrphan('${jsq(ext.pkgName||'')}',true,this)">Mark as orphaned</button>`:'';
}
function sourceExtensionCard(x){
  const store=(sourceExtensionState.stores||[]).find(st=>st.indexUrl&&st.indexUrl===x.storeIndexUrl),warning=String(x.contentWarning||'').toUpperCase();
  const name=x.name||x.pkgName||'Unknown extension',initials=String(name).split(/\s+/).filter(Boolean).slice(0,2).map(y=>y[0]).join('').toUpperCase();
  const rawIcon=String(x.iconUrl||'').trim(),iconSrc=rawIcon.startsWith('https://')?rawIcon:(rawIcon?`/api/v1/source-extension-icon/${encodeURIComponent(x.pkgName||'')}`:'');
  const icon=iconSrc?`<img class="sourceExtensionIcon" src="${esc(iconSrc)}" alt="" loading="lazy" onerror="this.remove()">`:'';
  return `<article class="sourceExtensionCard ${x.hasUpdate?'updateAvailable':''} ${x.isObsolete?'obsolete':''}"><span class="sourceExtensionIconWrap"><span class="sourceExtensionIconFallback">${esc(initials)}</span>${icon}</span><div><div class="sourceExtensionTitle">${esc(name)}</div><div class="sourceExtensionMeta">${esc(extensionLangLabel(x.lang))} · ${esc(x.versionName||'unknown version')} · ${esc(store?.name||'extension store')}</div><div class="sourceExtensionFlags"><span class="sourceExtensionFlag">${x.isInstalled?'Installed':'Available'}</span>${x.hasUpdate?'<span class="sourceExtensionFlag">Update available</span>':''}${x.isObsolete?'<span class="sourceExtensionFlag">Obsolete</span>':''}${warning&&warning!=='SAFE'?`<span class="sourceExtensionFlag">${esc(warning)}</span>`:''}</div><div class="sourceExtensionMeta">${esc(x.pkgName||'')}</div></div><div class="sourceExtensionActions">${extensionSettingsButton(x)}${extensionOrphanButton(x)}${extensionActionButton(x)}</div></article>`;
}
function renderSourceExtensionGroup(listId,items,emptyText){const list=$(listId);if(!list)return;list.innerHTML=items.length?items.map(sourceExtensionCard).join(''):`<div class="emptyState muted">${esc(emptyText)}</div>`;}
function renderSourceExtensions(){
  const summary=$('#sourceExtensionSummary');
  const q=($('#sourceExtensionSearch')?.value||'').trim().toLocaleLowerCase(),lang=$('#sourceExtensionLanguage')?.value||'all';
  const all=sourceExtensionState.extensions||[],updatesTotal=all.filter(x=>x.isInstalled&&x.hasUpdate&&!x.isObsolete).length;
  const badge=$('#sourceUpdateBadge');if(badge){badge.hidden=!updatesTotal;badge.textContent=String(updatesTotal);}
  const visible=all.filter(x=>(!q||`${x.name||''} ${x.pkgName||''} ${x.lang||''}`.toLocaleLowerCase().includes(q))&&(lang==='all'||String(x.lang||'')===lang));
  const sort=(a,b)=>Number(b.hasUpdate)-Number(a.hasUpdate)||String(a.name||'').localeCompare(String(b.name||''));
  const updates=visible.filter(x=>x.isInstalled&&x.hasUpdate&&!x.isObsolete&&!x.is_orphaned).sort(sort);
  const installed=visible.filter(x=>x.isInstalled&&!x.is_orphaned&&!(x.hasUpdate&&!x.isObsolete)).sort(sort);
  const available=visible.filter(x=>!x.isInstalled&&!x.is_orphaned).sort(sort);
  const updatesSection=$('#sourceExtensionUpdatesSection');if(updatesSection){updatesSection.hidden=!all.some(x=>x.isInstalled&&x.hasUpdate&&!x.isObsolete&&!x.is_orphaned);if(!updatesSection.hidden&&uiPreferencesState.values.sourceSections?.updates===undefined)updatesSection.open=true;}
  if($('#sourceExtensionUpdatesCount'))$('#sourceExtensionUpdatesCount').textContent=String(updates.length);
  if($('#sourceExtensionInstalledCount'))$('#sourceExtensionInstalledCount').textContent=String(installed.length);
  if($('#sourceExtensionAvailableCount'))$('#sourceExtensionAvailableCount').textContent=String(available.length);
  const updateAll=$('#updateAllSourceExtensions');if(updateAll){updateAll.disabled=!updatesTotal;updateAll.textContent=updatesTotal?`Update all (${updatesTotal})`:'Update all';}
  renderSourceExtensionGroup('#sourceExtensionUpdatesList',updates,'No matching source updates.');
  renderSourceExtensionGroup('#sourceExtensionInstalledList',installed,'No installed source extensions match these filters.');
  renderSourceExtensionGroup('#sourceExtensionAvailableList',available,'No not-installed source extensions match these filters.');
  if(summary)summary.textContent=`${all.filter(x=>x.isInstalled).length} installed · ${all.filter(x=>!x.isInstalled).length} not installed · ${updatesTotal} update${updatesTotal===1?'':'s'} · ${visible.length} matching filters`;
  applySourceSectionPreferences();
}
function renderSourceExtensionStores(){const el=$('#sourceExtensionStores');if(!el)return;const stores=sourceExtensionState.stores||[];el.innerHTML=stores.length?stores.map(x=>`<span class="sourceStoreChip"><strong>${esc(x.name||'Extension store')}</strong>${x.isLegacy?' · legacy':''}</span>`).join(''):'<span class="muted">Using the source host’s local extension catalog. Refresh catalog to fetch configured store information and newly available versions.</span>';}
function populateSourceExtensionLanguages(){const sel=$('#sourceExtensionLanguage');if(!sel)return;const old=sel.value||'all';const langs=[...new Set((sourceExtensionState.extensions||[]).map(x=>String(x.lang||'')).filter(Boolean))].sort();sel.innerHTML='<option value="all">All languages</option>'+langs.map(x=>`<option value="${esc(x)}">${esc(extensionLangLabel(x))}</option>`).join('');sel.value=langs.includes(old)?old:'all';}
let sourceExtensionReadRevision=0;
async function loadSourceExtensions(refresh=false){
  if(sourceExtensionState.loading)return;
  const revision=sourceExtensionReadRevision;sourceExtensionState.loading=true;
  if($('#sourceExtensionSummary'))$('#sourceExtensionSummary').textContent=refresh?'Refreshing extension stores…':'Loading source extensions…';
  try{
    const d=await api('/api/admin/source-extensions'+(refresh?'?refresh=true':''),{timeoutMs:refresh?90000:45000});
    if(revision!==sourceExtensionReadRevision){sourceExtensionState.loading=false;return loadSourceExtensions(false);}
    sourceExtensionState={loaded:true,loading:false,extensions:d.extensions||[],stores:d.stores||[]};
    populateSourceExtensionLanguages();renderSourceExtensionStores();renderSourceExtensions();
    window.loadSourceOrphans?.();
  }catch(e){sourceExtensionState.loading=false;throw e;}
}
window.setSourceOrphan=async(pkg,orphaned,button)=>{
  if(button)button.disabled=true;
  ++sourceExtensionReadRevision;
  try{
    const result=await api(`/api/admin/source-orphans/${encodeURIComponent(pkg)}/${orphaned?'mark':'unmark'}`,{method:'POST',timeoutMs:15000,noMutationRefresh:true});
    ++sourceExtensionReadRevision;
    const index=(sourceExtensionState.extensions||[]).findIndex(x=>String(x.pkgName)===String(pkg));
    if(index>=0&&result.extension)sourceExtensionState.extensions[index]={...sourceExtensionState.extensions[index],...result.extension};
    renderSourceExtensions();
    const section=$(orphaned?'#sourceInstalledOrphanPanel':'#sourceExtensionInstalledSection');if(section)section.open=true;
    await window.loadSourceOrphans?.();
    toast(result.message||'Source classification updated');
    loadSourceExtensions(false).catch(()=>{});
  }catch(error){toast('Unable to change orphan label: '+error.message);refreshManagedSourcesAfterMutation();}
  finally{if(button)button.disabled=false;}
};

function refreshManagedSourcesAfterMutation(){
  window.loadSourceOrphans?.();
  setTimeout(()=>loadSourceExtensions(false).catch(e=>rememberClientError('Unable to refresh extension inventory',{component:'Source extensions',severity:'warning',detail:String(e?.message||e)})),180);
  for(const delay of [700,1800,4000])setTimeout(()=>loadSources().then(()=>{rerenderSourceDrivenUi();}).catch(()=>{}),delay);
}
window.manageSourceExtension=async(pkg,action,button)=>{const verb=action==='install'?'Installing':action==='update'?'Updating':'Uninstalling';if(action==='uninstall'&&!confirm('Uninstall this source extension from the source engine? Existing AutoSourcarr source records are preserved as unavailable history, but the source will no longer be queried.'))return;if(button)button.disabled=true;toast(`${verb} source extension…`);try{const d=await api('/api/admin/source-extensions/'+encodeURIComponent(pkg)+'/action',{method:'POST',body:JSON.stringify({action}),timeoutMs:90000});const idx=(sourceExtensionState.extensions||[]).findIndex(x=>String(x.pkgName||'')===String(pkg));if(idx>=0&&d.extension)sourceExtensionState.extensions[idx]={...sourceExtensionState.extensions[idx],...d.extension};renderSourceExtensions();toast(`Source extension ${action==='uninstall'?'uninstalled':action==='update'?'updated':'installed'} · source list syncing in background`);refreshManagedSourcesAfterMutation();}catch(e){toast(`Source extension ${action} failed: `+e.message);if(action==='install')loadSourceExtensions(false).catch(()=>{});}finally{if(button)button.disabled=false;}};
window.updateAllManagedSourceExtensions=async()=>{const b=$('#updateAllSourceExtensions');if(!b||b.disabled)return;b.disabled=true;toast('Updating all source extensions…');try{const d=await api('/api/admin/source-extensions/update-all',{method:'POST',timeoutMs:180000});toast(`Updated ${Number(d.updated||0)} source extension${Number(d.updated||0)===1?'':'s'} · source list syncing in background`);refreshManagedSourcesAfterMutation();}catch(e){toast('Update all failed: '+e.message);}finally{b.disabled=false;}};
document.querySelectorAll('.sourceSubtab').forEach(b=>b.onclick=()=>setSourceSubtab(b.dataset.sourceSubtab));
if($('#sourceExtensionSearch'))$('#sourceExtensionSearch').oninput=renderSourceExtensions;if($('#sourceExtensionLanguage'))$('#sourceExtensionLanguage').onchange=renderSourceExtensions;
if($('#refreshSourceExtensions'))$('#refreshSourceExtensions').onclick=async()=>{const b=$('#refreshSourceExtensions');b.disabled=true;try{await loadSourceExtensions(true);toast('Extension catalog refreshed');}catch(e){toast('Extension refresh failed: '+e.message);}finally{b.disabled=false;}};
document.querySelectorAll('.sourceExtensionSection[data-source-section]').forEach(section=>section.addEventListener('toggle',()=>{if(!uiPreferencesState.loaded)return;queueUiPreferenceSave('sourceSections',currentSourceSectionPreferences());}));

// Extension-style companion browser
function browseEnabledRows(){
  return sourceRows.filter(s=>Number(s.available)!==0&&s.available!==false&&s.enabled_language&&s.enabled_search);
}
function populateBrowseFilters(){
  const source=$('#browseSource'),language=$('#browseLanguage'),matches=$('#browseMatchCount');
  if(!source||!language||!matches)return;
  const oldSource=source.value,oldLang=language.value,oldMatches=matches.value;
  const enabledRows=browseEnabledRows(),enabled=new Set(enabledRows.map(s=>String(s.source_id))),displayGroups=groupSources(sourceRows);
  source.innerHTML='<option value="">All enabled sources/groups</option>'+displayGroups.filter(g=>g.variants.some(v=>enabled.has(String(v.source_id)))).map(g=>`<option value="${esc(g.key)}">${esc(g.name)}</option>`).join('');
  const langs=[...new Set(enabledRows.map(s=>String(s.lang||'').toLowerCase()).filter(Boolean))].sort((a,b)=>langLabel(a).localeCompare(langLabel(b)));
  language.innerHTML='<option value="">All enabled languages</option>'+langs.map(l=>`<option value="${esc(l)}">${esc(langLabel(l))}</option>`).join('');
  matches.innerHTML='<option value="">Any match count</option>'+Array.from({length:Math.max(1,displayGroups.length)+1},(_,i)=>`<option value="${i}">${i} matched</option>`).join('');
  if([...source.options].some(o=>o.value===oldSource))source.value=oldSource;
  if([...language.options].some(o=>o.value===oldLang))language.value=oldLang;
  if([...matches.options].some(o=>o.value===oldMatches))matches.value=oldMatches;
}
function populateConnectedSourceFilters(){
  const rows=[...sourceRows].sort((a,b)=>String(a.name||a.display_name||a.source_id).localeCompare(String(b.name||b.display_name||b.source_id))||String(a.lang||'').localeCompare(String(b.lang||'')));
  for(const kind of ['matrix','subbed','browseConnected']){
    const control=$('#'+kind+'Source');if(!control)continue;
    const previous=control.value||'',previousLabel=String(control.selectedOptions?.[0]?.textContent||previous).replace(/ · retained filter$/,'');
    control.innerHTML='<option value="">'+(kind==='browseConnected'?'Any connected source':'All connected sources')+'</option>'+rows.map(row=>`<option value="${esc(String(row.source_id))}">${esc(row.name||row.display_name||row.source_id)} · ${esc(langLabel(row.lang||''))}${row.available===false||Number(row.available)===0?' · unavailable':''}</option>`).join('');
    if(previous&&!rows.some(row=>String(row.source_id)===previous))control.insertAdjacentHTML('beforeend',`<option value="${esc(previous)}">${esc(previousLabel)} · retained filter</option>`);
    if(previous)control.value=previous;
  }
  syncBrowseConnectionControls();
}
function syncBrowseConnectionControls(){
  const local=!!$('#browseConnectedSource')?.value;
  for(const id of ['browseSource','browseLanguage']){const control=$('#'+id);if(control)control.disabled=local;}
  document.querySelectorAll('.browseMode').forEach(button=>{button.disabled=local;button.classList.toggle('active',!local&&button.dataset.mode===browseState.mode);});
  const refresh=$('#refreshBrowseLive');if(refresh)refresh.disabled=local||browseState.loading;
}
function setBrowseMode(mode){
  browseState.mode=mode;
  document.querySelectorAll('.browseMode').forEach(b=>b.classList.toggle('active',b.dataset.mode===mode));
  $('#browseSearch').classList.toggle('searchActive',mode==='SEARCH');
  syncBrowseConnectionControls();
}
function browseSelectedSourceIds(){
  const selected=$('#browseSource')?.value||'',lang=($('#browseLanguage')?.value||'').toLowerCase();
  const selectedIds=selected?new Set((groupSources(sourceRows).find(g=>g.key===selected)?.variants||[]).map(v=>String(v.source_id))):null;
  return browseEnabledRows().filter(s=>(!selectedIds||selectedIds.has(String(s.source_id)))&&(!lang||String(s.lang||'').toLowerCase()===lang)).map(s=>String(s.source_id));
}
function browseQueryUrl(page,forceLive=false){
  const params=new URLSearchParams(),connected=$('#browseConnectedSource')?.value||'';
  params.set('mode',connected?'SEARCH':browseState.mode); params.set('page',String(page)); params.set('min_matches','0'); params.set('sort',$('#browseSort').value||'relevance');
  const customSize=Number.parseInt($('#browsePageSize')?.value||'',10);
  if(Number.isFinite(customSize)&&customSize>=1)params.set('page_size',String(Math.min(500,customSize)));
  params.set('force_live',!connected&&forceLive?'true':'false');
  if(connected||browseState.mode==='SEARCH')params.set('q',$('#browseSearch').value.trim());
  const exact=$('#browseMatchCount').value;if(exact!=='')params.set('match_count',exact);
  if(connected)params.set('source_id',connected);
  // An unfiltered feed is a broad search. Explicit IDs tell the backend to
  // bypass source cooldown and produce different persistent-cache keys.
  if(!connected&&($('#browseSource')?.value||$('#browseLanguage')?.value)){
    for(const id of browseSelectedSourceIds())params.append('sources',id);
  }
  return '/api/admin/browse?'+params.toString();
}
function browseCardHtml(e){
  const id=Number(e.id);
  return `<article class="browseCard ${Number(browseState.selectedEntryId)===id?'selectedBrowseCard':''}" data-entry-id="${id}" onclick="openBrowseEntry(${id},event)">
    <div class="browseCoverWrap"><img loading="lazy" fetchpriority="low" decoding="async" src="${entryCoverUrl(id,browseState.renderNonce)}" data-entry-cover="${id}" class="browseCover"><span class="browseMatchBadge" data-browse-match-count>${Number(e.match_count||0)} matched</span><span class="browseStatusBadge" data-browse-status>${esc(e.status_label||'Unknown')}</span></div>
    <div class="browseCardBody"><h3 data-browse-title>${esc(e.preferred_title||'Untitled')}</h3><div class="browseSources" data-browse-sources>${(e.sources||[]).slice(0,4).map(x=>`<span class="titleChip">${esc(x)}</span>`).join('')||'<span class="muted">No source label</span>'}</div><p data-browse-description>${esc(shortText(e.description||'Metadata is being acquired…',360))}</p><div class="cardActions"><button onclick="event.stopPropagation();refreshBrowseEntryMetadata(${id},this)">Refresh metadata/data</button><button onclick="event.stopPropagation();openBrowseEntry(${id},event)">Details</button></div></div>
  </article>`;
}
function patchBrowseCardNode(card,e){
  if(!card||!e)return false;
  const id=Number(e.canonical_id||e.id);if(!id||Number(card.dataset.entryId)!==id)return false;
  card.classList.toggle('selectedBrowseCard',Number(browseState.selectedEntryId)===id);
  const title=card.querySelector('[data-browse-title]');if(title)title.textContent=e.preferred_title||'Untitled';
  const status=card.querySelector('[data-browse-status]');if(status)status.textContent=e.status_label||'Unknown';
  const count=card.querySelector('[data-browse-match-count]');if(count)count.textContent=`${Number(e.match_count||0)} matched`;
  const desc=card.querySelector('[data-browse-description]');if(desc)desc.textContent=shortText(e.description||'Metadata is being acquired…',360);
  if(Array.isArray(e.sources)){
    const sources=card.querySelector('[data-browse-sources]');
    if(sources)sources.innerHTML=e.sources.slice(0,4).map(x=>`<span class="titleChip">${esc(x)}</span>`).join('')||'<span class="muted">No source label</span>';
  }
  return true;
}
function captureBrowseViewportAnchor(grid){
  if(!grid)return null;
  const cards=[...grid.querySelectorAll('.browseCard[data-entry-id]')];
  const anchor=cards.find(card=>card.getBoundingClientRect().bottom>0)||cards[0];
  if(!anchor)return {scrollY:window.scrollY};
  const rect=anchor.getBoundingClientRect();
  return {id:Number(anchor.dataset.entryId),top:rect.top,scrollY:window.scrollY};
}
function restoreBrowseViewportAnchor(anchor){
  if(!anchor)return;
  requestAnimationFrame(()=>{
    const card=anchor.id?document.querySelector(`#browseGrid .browseCard[data-entry-id="${anchor.id}"]`):null;
    if(card){const delta=card.getBoundingClientRect().top-anchor.top;if(Math.abs(delta)>.5)window.scrollBy(0,delta);}
    else if(Number.isFinite(anchor.scrollY))window.scrollTo(0,anchor.scrollY);
  });
}
function renderBrowse(){
  const renderStarted=Date.now();try{
  const grid=$('#browseGrid'); if(!grid)return;
  const items=browseState.items||[];
  const anchor=captureBrowseViewportAnchor(grid);
  if(!items.length){if(!grid.querySelector('.browseEmpty'))grid.innerHTML='<div class="emptyBrowserResult browseEmpty"><strong>No entries found.</strong><span>Try another source/language filter, a different search, or refresh this page from the live sources.</span></div>';restoreBrowseViewportAnchor(anchor);return;}
  const current=[...grid.querySelectorAll(':scope > .browseCard[data-entry-id]')];
  const sameOrder=current.length===items.length&&current.every((card,index)=>Number(card.dataset.entryId)===Number(items[index]?.id));
  if(sameOrder){items.forEach((e,index)=>patchBrowseCardNode(current[index],e));return;}
  grid.innerHTML=items.map(browseCardHtml).join('');
  restoreBrowseViewportAnchor(anchor);

  }finally{if(typeof recordUiTiming==='function')recordUiTiming('render','browse',Date.now()-renderStarted,{items:browseState.items?.length||0});}
}
async function loadBrowse(page=1,forceLive=false){
  if(!$('#browseConnectedSource')?.value&&($('#browseSource')?.value||$('#browseLanguage')?.value)&&!browseSelectedSourceIds().length){$('#browseStatus').textContent='Saved source filter is unavailable. Choose an available source or clear the filter.';return;}

  const key=browseQueryUrl(page,forceLive),previous=browseState.activeRequest;
  if(previous?.key===key)return previous.promise;
  const controller=typeof AbortController==='function'?new AbortController():null;
  const requestInfo={key,controller,promise:null};
  browseState.activeRequest=requestInfo;
  previous?.controller?.abort();
  const current=()=>browseState.activeRequest===requestInfo;
  const run=async()=>{
  browseState.loading=true; $('#browseStatus').textContent=forceLive?'Refreshing directly from enabled sources…':'Loading browse feed…';
  $('#runBrowse').disabled=true; $('#refreshBrowseLive').disabled=true;
  try{
    const requestedSize=Number.parseInt($('#browsePageSize')?.value||'',10);
    const d=await api(key,controller?{signal:controller.signal}:{});
    if(!current())return d;
    browseState.page=page; browseState.hasNext=!!d.has_next_page; browseState.items=d.items||[];
    browseState.loadedPageSize=Number(d.page_size)||(requestedSize>0?Math.min(500,requestedSize):0);
    // Browse is also catalog discovery. Invalidate already-rendered Matrix/Subbed
    // snapshots so switching tabs always reflects records just persisted by Browse.
    if(Number(d.cataloged_entries||0)>0){matrixData=null;subbedData=null;duplicateData=null;}
    const sizeInput=$('#browsePageSize');
    if(sizeInput&&!(sizeInput.value||'').trim())sizeInput.placeholder=`Default (${Number(d.page_size||50)})`;
    $('#browsePage').textContent=`Page ${page}`; $('#browsePrev').disabled=page<=1; $('#browseNext').disabled=!browseState.hasNext;
    const errors=(d.source_errors||[]).filter(e=>!e.needs_attention).length;
    const verificationPaused=(d.source_errors||[]).some(e=>e.needs_attention);
    const origin=d.source_filter?'saved entries from selected source':d.persistent_cache?'saved cache':d.cache_hit?'memory cache':'live/federated';
    $('#browseStatus').textContent=`${browseState.items.length} entries · ${origin}${Number(d.cataloged_entries||0)?` · ${Number(d.cataloged_entries)} cataloged`:''}${d.refreshing?' · background refresh running':''}${errors?` · ${errors} source error${errors===1?'':'s'}`:''}${verificationPaused?' · verification paused — see Needs attention':''}`;
    renderBrowse();
  }catch(error){
    if(!current())return; // superseded/cancelled work does not replace the active result or toast
    $('#browseStatus').textContent='Browse refresh failed: '+String(error?.message||error)+(browseState.items.length?' · showing previous results':' · use Browse to retry');
    throw error;
  }finally{
    if(current()){
    browseState.activeRequest=null;
    browseState.loading=false;$('#runBrowse').disabled=false;$('#refreshBrowseLive').disabled=!!$('#browseConnectedSource')?.value;
    if(browseState.pendingPreferenceReload){
      browseState.pendingPreferenceReload=false;
      if(browsePageSizeNeedsReload())setTimeout(()=>loadBrowse(1).catch(e=>toast(e.message)),0);
    }
    }
  }
  };
  requestInfo.promise=run();
  return requestInfo.promise;
}

if($('#browseConnectedSource'))$('#browseConnectedSource').onchange=()=>{syncBrowseConnectionControls();loadBrowse(1).catch(e=>toast(e.message));};

function browseInspectorIsOpen(){return !$('#browseInspectorBackdrop').hidden;}
function setBrowseSelectedCard(id){
  document.querySelectorAll('#browseGrid .browseCard').forEach(card=>card.classList.toggle('selectedBrowseCard',Number(card.dataset.entryId)===Number(id)));
}
function restoreBrowseScroll(){
  const y=Number(browseState.scrollY||0);
  requestAnimationFrame(()=>window.scrollTo(0,y));
}
function closeBrowseEntry(userEvent=null){
  if(!userEvent?.isTrusted)return;
  browseInspectorGeneration+=1;
  browseInspectorAbort?.abort();browseInspectorAbort=null;
  browseState.selectedEntryId=null;
  $('#browseInspectorBackdrop').hidden=true;
  $('#browseInspectorBody').innerHTML='';
  setBrowseSelectedCard(null);
  restoreBrowseScroll();
  requestAnimationFrame(flushDeferredUiRenders);
}
window.closeBrowseEntry=closeBrowseEntry;
window.openBrowseEntry=async(id,userEvent=null)=>{
  if(!userEvent?.isTrusted)return;
  browseInspectorAbort?.abort();const controller=new AbortController();browseInspectorAbort=controller;
  const generation=++browseInspectorGeneration;
  if(!browseInspectorIsOpen())browseState.scrollY=window.scrollY;
  browseState.selectedEntryId=Number(id);
  setBrowseSelectedCard(id);
  const backdrop=$('#browseInspectorBackdrop'),body=$('#browseInspectorBody');
  backdrop.hidden=false;
  restoreBrowseScroll();
  body.innerHTML='<div class="muted">Loading entry details…</div>';
  revealEntryInspector(backdrop);
  try{
    const e=await api('/api/admin/entries/'+id,{signal:controller.signal,timeoutMs:15000});
    if(generation!==browseInspectorGeneration||Number(browseState.selectedEntryId)!==Number(id))return;
    applyEntrySnapshotEverywhere(e,{render:false});noteEntryThumbnail(e);
    const matches=[...(e.matches||[]),...(e.source_variants||[])];
    $('#browseInspectorTitle').textContent=e.preferred_title||'Entry details';
    $('#browseInspectorSubtitle').textContent=`ID ${id} · ${e.match_count||0} source family(s) · ${matches.length} matched record(s)`;
    body.innerHTML=`<div class="inspectorTop browseModalInspectorTop"><img src="${entryCoverUrl(id,browseState.renderNonce)}" data-entry-cover="${id}" class="inspectorCover"><div class="inspectorInfo"><div class="toolbar wrap"><h2 class="grow">${esc(e.preferred_title)}</h2><span class="publicationPill" data-inspector-status>${esc(e.status_label||'Unknown')}</span>${entryWebviewButton(e)}</div><div class="entryDescription" data-inspector-description>${esc(e.description||'No description available yet.').replace(/\n/g,'<br>')}</div><div class="toolbar wrap"><button onclick="refreshBrowseEntryAll(${id},this)">Refresh</button><button type="button" data-manage-entry="${id}">Library &amp; serving profile</button><input id="browseMainTitle" value="${esc(e.preferred_title)}" placeholder="Main title"><button onclick="setBrowseMainTitle(${id})">Set main title</button><button class="danger ghost" onclick="clearBrowseManualTitles(${id},this)" ${e.manual_preferred_title||e.titles?.some?.(t=>t.title_type==='manual')?'':'disabled'}>Remove manual titles</button><button onclick="closeBrowseEntry(event);switchTab('matrix');document.querySelector('#matrixSearch').value='${jsq(e.preferred_title)}';loadMatrix()">Open in matrix</button></div></div></div>${entryDetailSection('titles','Alternate titles',entryAlternateTitlesHtml(e))}${entryDetailSection('tags','Tags',`<div class="entryTagMetadata" data-entry-tags="${Number(id)}"><p class="muted">Loading saved tags…</p></div>`)}${entryDetailSection('metadata','Metadata',`<div class="toolbar"><h3 class="grow">Metadata identities</h3><button class="miniButton" onclick="refreshBrowseIdentity(${id},this)">Re-resolve</button></div><div data-browse-identity-list>${metadataIdentityHtml(e)}</div><div data-gallery-metadata></div>`)}${entryDetailSection('matches','Source matches',`<div class="browseMatchList">${matches.map(m=>`<div class="sourceMatchLine"><img src="${sourceCoverUrl(Number(m.source_manga_id),browseState.renderNonce)}" data-source-cover="${Number(m.source_manga_id)}" class="sourceLineCover"><div class="grow"><strong>${esc(m.source_title||'Untitled')}</strong><div class="muted">${esc(m.source_name||m.source_id)} · ${esc(langLabel(m.source_lang||''))}${m.is_variant?' · additional record':''}</div><div class="muted">${esc(shortText(m.description||'No source summary cached.',220))}</div></div><button class="miniButton sourceWebviewButton" onclick="openEntrySourceWebview('${jsq(m.real_url||'')}','${jsq(m.source_id)}')">Open in WebView</button></div>`).join('')||'<span class="muted">No active source records.</span>'}</div>`)}${entryChapterSections('browse')}`;
    bindEntryDetailSections(body,'browse',id,controller.signal);
    window.loadEntryTags?.(body.querySelector('[data-entry-tags]'),id,controller.signal,body.querySelector('[data-gallery-metadata]'));

  }catch(e){if(generation===browseInspectorGeneration&&Number(browseState.selectedEntryId)===Number(id))body.innerHTML=`<div class="emptyBrowserResult"><strong>Unable to load entry.</strong><span>${esc(e.message)}</span></div>`;}
};
$('#closeBrowseInspector').onclick=e=>closeBrowseEntry(e);
$('#browseInspectorBackdrop').addEventListener('click',e=>{if(e.target===$('#browseInspectorBackdrop'))closeBrowseEntry(e);});
document.addEventListener('keydown',e=>{if(e.key==='Escape'&&browseInspectorIsOpen())closeBrowseEntry(e);});
function identityResolutionMessage(result){
  const matches=Number(result.matched||0), errors=Object.keys(result.provider_errors||{}).length;
  const ambiguous=(result.ambiguous_bootstrap_providers||[]).length;
  if(matches>0)return `Metadata resolved: ${matches} provider match${matches===1?'':'es'}${errors?' · other providers temporarily unavailable':''}`;
  if(ambiguous)return 'Still unresolved: multiple exact metadata candidates; no identity was guessed';
  if(errors)return 'Still unresolved: metadata providers failed; expand diagnostics for details';
  return 'Still unresolved: no safe metadata identity found';
}
function identityResolutionDiagnostics(result){
  const failures=Object.entries(result.provider_errors||{}).map(([provider,error])=>`${provider}: ${String(error)}`);
  const ambiguous=result.ambiguous_bootstrap_providers||[];
  if(ambiguous.length)failures.push(`Ambiguous exact titles: ${ambiguous.join(', ')}`);
  return failures.length?`<details class="spaced"><summary>Metadata diagnostics</summary><pre class="muted">${esc(failures.join('\n'))}</pre></details>`:'';
}
window.refreshBrowseIdentity=async(id,button)=>{
  if(button){button.disabled=true;button.textContent='Resolving…';}
  try{
    const r=await api(`/api/admin/entries/${id}/identity/refresh?force=true`,{method:'POST',timeoutMs:120000});
    const entry=r.entry||await api(`/api/admin/entries/${id}`,{timeoutMs:10000,suppressClientError:true});
    applyEntrySnapshotEverywhere(entry,{render:false});
    const list=$('#browseInspectorBody')?.querySelector('[data-browse-identity-list]');
    if(list&&browseInspectorIsOpen()&&Number(browseState.selectedEntryId)===Number(id))list.innerHTML=metadataIdentityHtml(entry)+identityResolutionDiagnostics(r);
    patchOpenInspectorSnapshot(entry);
    toast(identityResolutionMessage(r));
  }catch(e){toast('Metadata identity refresh failed: '+e.message);}
  finally{if(button){button.disabled=false;button.textContent='Re-resolve';}}
};
window.refreshBrowseEntryMetadata=async(id,button)=>{
  if(button){button.disabled=true;button.textContent='Queued…';}
  toast('Metadata refresh queued');
  try{
    const r=await api(`/api/admin/entries/${id}/refresh-metadata?background=true`,{method:'POST',timeoutMs:8000});
    scheduleEntrySnapshotSync(id,[1200,4000,9000],true);
    loadTasks().catch(()=>{});
    toast(r.already_running?'Metadata refresh is already running':'Metadata refresh is running in the background');
  }catch(e){toast('Metadata refresh failed: '+e.message);}
  finally{if(button){button.disabled=false;button.textContent='Refresh metadata/data';}}
};
window.setBrowseMainTitle=async(id)=>{const input=$('#browseMainTitle');const v=input?.value.trim();if(!v)return;try{
  // Update the visible title before the round-trip; the endpoint itself is local DB work.
  patchOpenInspectorTitle(id,v);
  const entry=await api('/api/admin/entries/'+id+'/preferred-title',{method:'POST',body:JSON.stringify({title:v}),timeoutMs:8000});
  applyEntrySnapshotEverywhere(entry);patchOpenInspectorTitle(id,entry.preferred_title||v);toast('Main title updated');
}catch(e){toast('Unable to change main title: '+e.message);syncEntrySnapshot(id,{refreshInspector:false});}};
window.clearBrowseManualTitles=async(id,button)=>{if(!confirm('Remove the manual main-title override and all manually added alternate titles for this entry? The main title will return to the most-used title among its bound source records.'))return;const oldText=button?.textContent;if(button){button.disabled=true;button.textContent='Removing…';}try{const entry=await api('/api/admin/entries/'+id+'/manual-titles',{method:'DELETE',timeoutMs:8000});applyEntrySnapshotEverywhere(entry);toast('Manual titles removed');if(browseInspectorIsOpen()&&Number(browseState.selectedEntryId)===Number(id))patchOpenInspectorSnapshot(entry);}catch(e){toast('Unable to remove manual titles: '+e.message);}finally{if(button&&button.isConnected){button.disabled=false;button.textContent=oldText||'Remove manual titles';}}};
window.refreshBrowseEntryAll=async(id,button)=>{
  if(button){button.disabled=true;button.textContent='Queued…';}
  toast('Entry refresh queued');
  try{
    const r=await api(`/api/admin/entries/${id}/refresh-all`,{method:'POST',timeoutMs:10000});
    browseState.renderNonce=Date.now();
    scheduleEntrySnapshotSync(id,[1500,5000,12000],true);loadTasks().catch(()=>{});
    toast(r.already_running?'Entry refresh is already running':'Refreshing the complete entry in the background');
  }catch(e){toast('Entry refresh failed: '+e.message);}
  finally{if(button){button.disabled=false;button.textContent='Refresh';}}
};
async function repairMissingMetadata(button){
  if(button)button.disabled=true;
  try{const r=await api('/api/admin/maintenance/refresh-metadata?limit=5000',{method:'POST'});toast(`Metadata repair queued: ${r.queued||0} of ${r.candidates||0} incomplete entries`);loadTasks().catch(()=>{});}
  catch(e){toast('Metadata repair failed: '+e.message);}finally{if(button)button.disabled=false;}
}
document.querySelectorAll('.browseMode').forEach(b=>b.onclick=()=>{setBrowseMode(b.dataset.mode);loadBrowse(1).catch(e=>toast(e.message));});
$('#runBrowse').onclick=()=>loadBrowse(1).catch(e=>toast(e.message));
$('#browseSearch').addEventListener('keydown',e=>{if(e.key==='Enter'){setBrowseMode('SEARCH');loadBrowse(1).catch(x=>toast(x.message));}});
$('#browsePrev').onclick=()=>{if(browseState.page>1)loadBrowse(browseState.page-1).catch(e=>toast(e.message));};
$('#browseNext').onclick=()=>{if(browseState.hasNext)loadBrowse(browseState.page+1).catch(e=>toast(e.message));};
$('#refreshBrowseLive').onclick=()=>loadBrowse(browseState.page,true).catch(e=>toast(e.message));
$('#refreshBrowseMetadata').onclick=e=>repairMissingMetadata(e.currentTarget);
function applyBrowsePageSize(rawValue=null){
  const input=$('#browsePageSize'),preset=$('#browsePageSizePreset');
  const raw=String(rawValue??input?.value??'').trim();
  if(!raw){
    if(input)input.value='';
    if(preset)preset.value='';
    queueUiPreferenceSave('browsePageSize',null);
    return loadBrowse(1).catch(e=>toast(e.message));
  }
  const parsed=Number.parseInt(raw,10);
  if(!Number.isFinite(parsed)||parsed<1){toast('Entries per page must be between 1 and 500.');return;}
  const size=Math.max(1,Math.min(500,parsed));
  if(input)input.value=String(size);
  if(preset)preset.value=['25','50','100','200','500'].includes(String(size))?String(size):'';
  queueUiPreferenceSave('browsePageSize',String(size));
  loadBrowse(1).catch(e=>toast(e.message));
}
const savedBrowsePageSize=readLocalUiPreference('browsePageSize');
if(savedBrowsePageSize&&$('#browsePageSize')){
  $('#browsePageSize').value=savedBrowsePageSize;
  if($('#browsePageSizePreset')&&['25','50','100','200','500'].includes(savedBrowsePageSize))$('#browsePageSizePreset').value=savedBrowsePageSize;
}
$('#browsePageSizePreset').addEventListener('change',e=>{
  const value=e.currentTarget.value;
  if(value&&$('#browsePageSize'))$('#browsePageSize').value=value;
  applyBrowsePageSize(value);
});
$('#applyBrowsePageSize').onclick=()=>applyBrowsePageSize();
$('#browsePageSize').addEventListener('keydown',e=>{if(e.key==='Enter')applyBrowsePageSize();});

// Matrix rendering
function matrixControls(kind){
  return {
    search: $('#'+kind+'Search'), source: $('#'+kind+'Source'), min: $('#'+kind+'MinMatches'), entryMatches: $('#'+kind+'EntryMatches'), titleState: $('#'+kind+'TitleState'), status: $('#'+kind+'Status'), publication: $('#'+kind+'PublicationStatus'), sort: $('#'+kind+'Sort'),
    table: $('#'+kind+'Table'), count: $('#'+kind+'Count'), inspector: $('#'+kind+'Inspector')
  };
}
function matrixDataset(kind){ return kind==='subbed' ? subbedData : matrixData; }
function setMatrixDataset(kind,d){ if(kind==='subbed')subbedData=d; else matrixData=d; }
function publicationMatches(value,status){
  const n=Number(status||0);
  if(value==='ongoing')return n===1;
  if(value==='finished')return n===2||n===4;
  if(value==='licensed')return n===3;
  if(value==='cancelled')return n===5;
  if(value==='hiatus')return n===6;
  if(value==='unknown')return n===0;
  return true;
}
function ensureExactMatchOptions(select,maxCount){
  const previous=select.value;
  const max=Math.max(0,Number(maxCount)||0);
  select.innerHTML='<option value="">Any source match count</option>'+Array.from({length:max+1},(_,i)=>`<option value="${i}">${i} source${i===1?'':'s'} matched</option>`).join('');
  if([...select.options].some(o=>o.value===previous))select.value=previous;
}
function ensureAvailableEntryMatchOptions(select,entries){
  if(!select)return;
  const previous=select.value;
  const counts=[...new Set((entries||[]).map(e=>Number(e.entry_match_count||0)))].sort((a,b)=>a-b);
  select.innerHTML='<option value="">Any entry match count</option>'+counts.map(n=>`<option value="${n}">${n} matched entr${n===1?'y':'ies'}</option>`).join('');
  if([...select.options].some(o=>o.value===previous))select.value=previous;
}
function subscriptionStatsFor(d,entryId){
  const sub=d?.subscription;
  if(!sub?.ok)return null;
  const stats=sub.stats_by_entry||{};
  const found=stats[String(entryId)]||null;
  if(found)return found;
  return (sub.entry_ids||[]).map(Number).includes(Number(entryId))
    ? {entry_id:Number(entryId),chapter_count:null,downloaded_chapter_count:null}
    : null;
}
function entryChapterStatsHtml(entry,subscriptionStats){
  const latest=entry?.served_latest_chapter;
  const checked=!!entry?.chapter_records_checked;
  let served='';
  if(latest!=null)served=`Latest ch. ${fmtChapterNumber(latest)}`;
  else served=entry?.chapter_stats_pending?(entry.chapter_stats_error?'Coverage unavailable; refresh to retry':'Loading chapter coverage…'):checked?'No chapters served':'No chapters cached';
  let downloaded='';
  if(subscriptionStats){
    const n=subscriptionStats.downloaded_chapter_count;
    downloaded=n==null?'? downloaded':`${Number(n).toLocaleString()} downloaded`;
  }
  return `<div class="subbedChapterStats"><span>${served}</span>${downloaded?`<span>${downloaded}</span>`:''}</div>`;
}
function fmtChapterNumber(value){
  const n=Number(value);
  if(!Number.isFinite(n)||n<0)return '?';
  return Number.isInteger(n)?String(n):String(Math.round(n*1000)/1000);
}
function sourceCoverageHtml(match,kind,entryId,sourceId){
  const latest=match.latest_chapter;
  const count=Number(match.chapter_count||0);
  const missing=Number(match.missing_chapter_count||0);
  const checked=!!match.chapters_checked;
  const failed=String(match.chapter_check_error||'').trim();
  let stats='';
  if(match.chapter_stats_pending)stats=`<span class="mutedCoverage">${match.chapter_stats_error?'Coverage unavailable; refresh to retry':'Loading chapter coverage…'}</span>`;
  else if(latest==null&&count===0){
    if(failed)stats='<span class="missingCoverage">Chapter check failed</span>';
    else if(checked)stats='<span>No chapters found</span>';
    else stats='<span class="mutedCoverage">No chapters cached</span>';
  }else{
    stats=`<span>Latest ch. ${fmtChapterNumber(latest)}</span><span>${count} cached</span>${missing>0?`<span class="missingCoverage">${missing} missing</span>`:''}`;
  }
  return `<div class="sourceCoverage">${stats}<button class="chapterCheckButton" onclick="event.stopPropagation();checkSourceChapters(${Number(entryId)},'${jsq(sourceId)}',${Number(match.source_manga_id)},this)">Check chapters</button></div>`;
}
window.checkSourceChapters=async(entryId,sourceId,sourceMangaId,button)=>{
  if(button){button.disabled=true;button.textContent='Checking…';}
  try{
    const r=await api(`/api/admin/entries/${entryId}/source-records/${encodeURIComponent(sourceId)}/${sourceMangaId}/check-chapters`,{method:'POST',timeoutMs:30000});
    const stats=r.stats||{};
    for(const d of [subbedData,matrixData]){
      if(!d)continue;
      for(const collection of [d.matches||[],d.variants||[]]){
        const row=collection.find(x=>Number(x.entry_id)===Number(entryId)&&String(x.source_id)===String(sourceId)&&Number(x.source_manga_id)===Number(sourceMangaId));
        if(row)Object.assign(row,stats,{chapter_stats_pending:false,chapter_stats_error:''});
      }
      const entry=(d.entries||[]).find(x=>Number(x.id)===Number(entryId));
      if(entry&&r.entry_stats)Object.assign(entry,r.entry_stats);
    }
    requestSurfaceRender('subbed');
    requestSurfaceRender('matrix');
    for(const openKind of ['subbed','matrix']){
      if(activeInspector[openKind]===Number(entryId))refreshInlineEntryStable(openKind,Number(entryId)).catch(()=>{});
    }
    const count=Number(stats.chapter_count||0);
    toast(count?`Chapter check complete: ${count} chapter${count===1?'':'s'} found`:'Chapter check complete: no chapters found');
    loadTasks().catch(()=>{});
  }catch(e){toast('Chapter check failed: '+e.message);if(button){button.disabled=false;button.textContent='Check chapters';}}
};

// Initial entries appear in bounded batches. Updates retain the old table until
// their replacement is complete, so document height and open controls survive.
const tableRenderJobs=new WeakMap();
function tableDetailKey(detail){
  const row=detail.closest('tr[data-entry-id]'),cell=detail.closest('td,th');
  if(!row||!cell)return '';
  const table=row.closest('table'),header=table?.tHead?.rows?.[0]?.cells?.[cell.cellIndex];
  const group=cell.dataset.sourceGroup||header?.dataset.sourceGroup||String(header?.textContent||cell.cellIndex).trim();
  const position=[...cell.querySelectorAll('details')].indexOf(detail);
  return JSON.stringify([row.dataset.entryId,group,detail.dataset.detailsKey||position]);
}
function captureTableViewport(table){
  const open=new Set([...table.querySelectorAll('details[open]')].map(tableDetailKey).filter(Boolean));
  const rows=[...table.querySelectorAll('tbody tr[data-entry-id]')];
  const row=rows.find(r=>r.getBoundingClientRect().bottom>0);
  const wrap=table.closest('.tablewrap');
  return {open,id:row?.dataset.entryId,top:row?.getBoundingClientRect().top,x:window.scrollX||0,y:window.scrollY||0,left:wrap?.scrollLeft||0};
}
function restoreTableViewport(table,state){
  const row=state.id?[...table.querySelectorAll('tbody tr[data-entry-id]')].find(r=>r.dataset.entryId===state.id):null;
  if(row&&Number.isFinite(state.top)){
    const delta=row.getBoundingClientRect().top-state.top;
    if(Math.abs(delta)>.5)window.scrollBy(0,delta);
  }else if(Number.isFinite(state.y))window.scrollTo(state.x,state.y);
  const wrap=table.closest('.tablewrap');if(wrap)wrap.scrollLeft=state.left;
}
function renderTableRows(table,header,rows){
  const previous=tableRenderJobs.get(table);if(previous?.timer)clearTimeout(previous.timer);
  const populated=!!table.querySelector('tbody tr[data-entry-id]');
  const target=populated?document.createElement('table'):table;
  const surface=table.closest('.tab')?.id;
  const job={next:0,batch:20,timer:null,start:performance.now(),blockedInitially:!!(surface&&structuralRenderBlocked(surface))};tableRenderJobs.set(table,job);
  target.innerHTML=header.replace(/<tbody><\/tbody>$/,'')+'<tbody></tbody>';
  const body=target.tBodies[target.tBodies.length-1];
  table.setAttribute('aria-busy',rows.length?'true':'false');
  const finish=()=>{
    if(tableRenderJobs.get(table)!==job)return;
    if(populated){
      // A user may open a row/inspector or start typing while batches are built.
      if(!job.blockedInitially&&surface&&structuralRenderBlocked(surface)){
        job.timer=setTimeout(finish,100);return;
      }
      const state=captureTableViewport(table);
      for(const detail of target.querySelectorAll('details'))detail.open=state.open.has(tableDetailKey(detail));
      table.replaceChildren(...target.childNodes);
      restoreTableViewport(table,state);
    }
    table.setAttribute('aria-busy','false');job.timer=null;
    recordUiTiming('render',table.id||'entry-table',performance.now()-job.start,{rows:rows.length,phase:'complete',retained_previous:populated});
  };
  const append=()=>{
    if(tableRenderJobs.get(table)!==job)return;
    const start=performance.now(),batch=rows.slice(job.next,job.next+job.batch);
    body.insertAdjacentHTML('beforeend',batch.join(''));job.next+=batch.length;
    if(job.next===batch.length&&!populated)recordUiTiming('render',table.id||'entry-table',performance.now()-job.start,{rows:batch.length,phase:'first entries'});
    if(job.next>=rows.length){finish();return;}
    const elapsed=performance.now()-start;
    job.batch=Math.max(4,Math.min(40,Math.floor(job.batch*(elapsed>20?.7:elapsed<8?1.3:1))));
    job.timer=setTimeout(append,0);
  };
  append();
}

function renderMatrix(kind='matrix'){
  if(kind==='subbed'&&globalThis.AutoSourcarrLibrary?.hasData())return globalThis.AutoSourcarrLibrary.render();
  const d=matrixDataset(kind); if(!d)return;
  const c=matrixControls(kind);
  const enabledVariants=d.sources.filter(s=>s.enabled_language&&(s.enabled_search||s.enabled_chapters));
  const enabled=groupSources(enabledVariants);
  const records=new Map(), byEntry=new Map();
  const outcomes=new Map((d.discovery_outcomes||[]).map(x=>[x.entry_id+'|'+x.source_id,x]));
  const addRecord=(m,isVariant=false)=>{
    const key=m.entry_id+'|'+m.source_id;
    if(!records.has(key))records.set(key,[]);
    records.get(key).push({...m,is_variant:isVariant});
    if(!byEntry.has(m.entry_id))byEntry.set(m.entry_id,[]);
    byEntry.get(m.entry_id).push({...m,is_variant:isVariant});
  };
  (d.matches||[]).forEach(m=>addRecord(m,false));
  (d.variants||[]).forEach(m=>addRecord(m,true));
  ensureExactMatchOptions(c.min,Math.max(enabled.length,...d.entries.map(e=>Number(e.match_count)||0)));
  ensureAvailableEntryMatchOptions(c.entryMatches,d.entries);
  const exact=c.min.value,entryExact=c.entryMatches?.value||'',titleState=c.titleState?.value||'any',status=c.status.value,pub=c.publication?.value||'any',sort=c.sort.value;
  const duplicateMap=d.duplicates||{};
  let entries=d.entries.filter(e=>(exact===''||Number(e.match_count)===Number(exact))&&(entryExact===''||Number(e.entry_match_count||0)===Number(entryExact))&&(titleState==='any'||(titleState==='manual'&&!!e.manual_preferred_title)||(titleState==='automatic'&&!e.manual_preferred_title)));
  const groupHasMatch=(entryId,g)=>g.variants.some(v=>(records.get(entryId+'|'+v.source_id)||[]).length>0);
  entries=entries.filter(e=>{
    const ms=byEntry.get(e.id)||[];
    if(!publicationMatches(pub,e.status))return false;
    if(status==='manual')return ms.some(m=>m.locked);
    if(status==='gaps')return enabled.some(g=>!groupHasMatch(e.id,g));
    if(status==='full')return enabled.length>0&&enabled.every(g=>groupHasMatch(e.id,g));
    if(status==='duplicates')return !!duplicateMap[String(e.id)];
    if(status==='multiple_records'){const counts=new Map();for(const m of ms)counts.set(String(m.source_id),(counts.get(String(m.source_id))||0)+1);return [...counts.values()].some(n=>n>1);}
    return true;
  });
  if(sort==='matches')entries.sort((a,b)=>b.match_count-a.match_count||a.preferred_title.localeCompare(b.preferred_title));
  if(sort==='title')entries.sort((a,b)=>a.preferred_title.localeCompare(b.preferred_title));
  // Fixed table layout: every enabled source gets the exact same column width.
  // Auto table layout + content-visibility used to recalculate widths as rows
  // entered the scroll viewport, making columns appear to resize while scrolling.
  const matrixEntryWidth=240, matrixSourceWidth=205;
  c.table.style.minWidth=`${matrixEntryWidth + enabled.length*matrixSourceWidth}px`;
  let h='<colgroup><col class="matrixEntryColWidth">'+enabled.map(()=>'<col class="matrixSourceColWidth">').join('')+'</colgroup>';
  h+='<thead><tr><th class="entryCol">Entry</th>'+enabled.map(g=>`<th data-source-group="${esc(g.key)}">${esc(g.name)}<br><span class="muted">#${g.priority} · ${esc(groupSummary(g))}</span></th>`).join('')+'</tr></thead>';
  const rowHtml=[];
  for(const e of (kind==='subbed'&&typeof renderSubbedRows==='function'?[]:entries)){
    let row='';
    const dup=duplicateMap[String(e.id)];
    const subStats=subscriptionStatsFor(d,e.id);
    const dupCandidates=dup?.candidates||[];const dupNames=dupCandidates.slice(0,2).map(x=>x.other_title||`Entry ${x.entry_id}`);const dupLabel=dupCandidates.length?`${dupCandidates.length} possible duplicate${dupCandidates.length===1?'':'s'}`:'';
    row+=`<tr data-entry-id="${e.id}" class="matrixRow ${activeInspector[kind]===e.id?'selectedRow':''}"><td class="entryCol"><div class="entryCellWrap"><button class="entrySummary libraryMatrixButton" onclick="openInlineEntry('${kind}',${e.id},event)"><span class="matrixLibraryCard entryLibraryCard"><img loading="lazy" fetchpriority="low" decoding="async" src="${entryCoverUrl(e.id)}" data-entry-cover="${e.id}" class="matrixLibraryCover"><span class="matrixCardGradient"></span><span class="matrixCardTitle">${esc(e.preferred_title)}</span><span class="matrixCardBadge">${Number(e.entry_match_count||0)} match${Number(e.entry_match_count||0)===1?'':'es'} · ${e.match_count} source${e.match_count===1?'':'s'}</span>${e.manual_preferred_title?'<span class="manualTitleBadge">MANUAL TITLE</span>':''}${dup?`<span class="duplicateBadge">${esc(dupLabel)}</span>`:''}<span class="publicationBadge">${esc(e.status_label||'Unknown')}</span></span>${entryChapterStatsHtml(e,subStats)}</button><button class="deleteEntryButton" title="Delete entry" onclick="event.stopPropagation();deleteEntry(${e.id},'${kind}')">×</button>${dup?`<button class="duplicateCounterpartLink" onclick="event.stopPropagation();goDuplicates(${e.id})"><b>Possible same work:</b> ${esc(dupNames.join(' · '))}${dupCandidates.length>2?` +${dupCandidates.length-2}`:''}</button>`:''}</div></td>`;
    for(const g of enabled){
      const gm=[];
      for(const v of g.variants){
        for(const m of (records.get(e.id+'|'+v.source_id)||[]))gm.push({v,m});
      }
      if(!gm.length){
        const searchable=(selectedVariants(g).find(v=>v.enabled_search)||g.variants.find(v=>v.enabled_search)||g.variants[0]);
        const reports=g.variants.map(v=>outcomes.get(e.id+'|'+v.source_id)).filter(Boolean);
        const report=reports.find(x=>x.state==='owned')||reports.find(x=>x.state==='verification')||reports[0];
        const labels={owned:'Candidate needs review',verification:'Needs verification',unavailable:'Source unavailable',deferred:'Search deferred',no_results:'No search results',separate:'Manually separated'};
        row+=`<td class="match none" data-source-group="${esc(g.key)}"><div>${esc(labels[report?.state]||'No match')}</div>${report?`<small class="discoveryOutcome" title="${esc(report.message||'')}">${esc(shortText(report.message||'',120))}</small>`:''}${report?.state==='owned'&&Number(report.candidate_entry_id)>0?`<button class="miniButton" onclick="goDuplicates(${Number(e.id)})">Review entry ${Number(report.candidate_entry_id)}</button>`:''}${searchable?`<button class="miniButton" onclick="openMatchBrowser(${e.id},'${jsq(searchable.source_id)}')">Search all names</button>`:''}</td>`;
      }else{
        const renderedRecords=gm.map(({v,m})=>{const cls=m.confidence>=.94?'good':m.confidence>=.78?'warn':'bad';const coverage=sourceCoverageHtml(m,kind,e.id,v.source_id);const actions=m.is_variant?`<button class="miniButton primaryAction" onclick="promoteVariantPrimary(${e.id},'${jsq(v.source_id)}',${Number(m.source_manga_id)},this)">Use as primary</button><button class="miniButton danger ghost" onclick="clearVariant(${e.id},'${jsq(v.source_id)}',${Number(m.source_manga_id)},'${kind}')">Remove additional</button>`:`<button class="miniButton" onclick="openMatchBrowser(${e.id},'${jsq(v.source_id)}')">Edit matches</button>`;return `<div class="matrixvariant libraryVariant ${m.is_variant?'additionalRecord':''}"><div class="matrixLibraryCard sourceLibraryCard"><img loading="lazy" fetchpriority="low" decoding="async" src="${sourceCoverUrl(m.source_manga_id)}" data-source-cover="${m.source_manga_id}" class="matrixLibraryCover"><span class="matrixCardGradient"></span><span class="matrixCardTitle">${esc(m.source_title)}</span><span class="matrixLangBadge">${esc(v.lang||'—')}</span></div><div class="matrixMatchText libraryMatchMeta"><div><span class="confidence ${cls}">${Math.round(m.confidence*100)}%</span> <span class="tag ${m.locked?'manual':''}">${m.locked?'MANUAL':(m.is_variant?'ADDITIONAL':'AUTO')}</span></div>${coverage}<div class="muted compactMeta"><b>${esc(v.name||v.display_name||v.source_id)}</b>${m.description?` · ${esc(shortText(m.description||'',100))}`:''}</div><div class="compactMatchActions">${actions}</div></div></div>`;});row+=`<td class="match" data-source-group="${esc(g.key)}">`+renderedRecords[0]+(renderedRecords.length>1?`<details class="sourceRecordDetails"><summary>${renderedRecords.length-1} more source record${renderedRecords.length===2?'':'s'}</summary>${renderedRecords.slice(1).join('')}</details>`:'')+'</td>';
      }
    }
    row+='</tr>';rowHtml.push(row);
  }
  if(kind==='subbed'&&typeof renderSubbedRows==='function'){
    entries=filterLibraryScope(entries,byEntry);h=renderSubbedRows([],d,byEntry);rowHtml.push(...renderSubbedRows(entries,d,byEntry,true));c.table.classList.add('subbedTable');
  }
  const presented=kind==='subbed'&&globalThis.AutoSourcarrReimagined?.renderLibrary(entries,d,byEntry);
  renderTableRows(c.table,h,presented?[]:rowHtml);
  const sub = d.subscription || null;
  c.count.textContent=`${entries.length} shown · ${enabled.length} source groups${kind==='subbed'&&sub?.ok?(sub.refreshing?' · saved library snapshot · refreshing':' · synced from main library'):''}`;
  updateMatrixCoverageLabel(kind,d);
  if(kind==='subbed'){
    const n=$('#subbedNotice');
    if(sub?.ok==null){n.hidden=false;n.textContent='Loading the saved main-library membership… Entries appear as soon as it is available.';}else if(sub && !sub.ok){n.hidden=false;n.textContent='Subbed view unavailable: '+(sub.error||'main Suwayomi could not be queried')+'. Set MAIN_SUWAYOMI_URL to your main Suwayomi service.';} else n.hidden=true;
  }
}
async function hydrateMatrixDuplicates(kind,d,{render=true}={}){
  const ids=(d.entries||[]).map(e=>Number(e.id)).filter(Boolean);
  if(!ids.length){d.duplicates_pending=false;return;}
  if(duplicateHydrateRetry[kind]){clearTimeout(duplicateHydrateRetry[kind]);duplicateHydrateRetry[kind]=null;}
  try{
    // POST avoids the multi-kilobyte ?ids= URL that v0.2.32+ generated for a 500-row
    // Matrix. The backend serves only the completed duplicate snapshot, so this
    // request is cheap and never starts the full scorer itself.
    const key=ids.slice().sort((a,b)=>a-b).join(',');
    let request=duplicateSnapshotRequests.get(key);
    if(!request){request=api('/api/admin/duplicate-candidates',{method:'POST',body:JSON.stringify({ids}),timeoutMs:6000,suppressClientError:true,noMutationRefresh:true}).finally(()=>{if(duplicateSnapshotRequests.get(key)===request)duplicateSnapshotRequests.delete(key);});duplicateSnapshotRequests.set(key,request);}
    const r=await request;
    if(matrixDataset(kind)!==d)return;
    const changed=JSON.stringify(d.duplicates||{})!==JSON.stringify(r.duplicates||{});
    d.duplicates=r.duplicates||{};d.duplicates_pending=!!r.pending;
    if(changed&&(render||document.querySelector(`#${kind}.active`)))requestSurfaceRender(kind);
    if(r.pending){
      const delay=Math.min(30000,5000*Math.pow(2,Math.min(3,duplicatePollAttempts[kind]++)));
      if(!document.hidden&&activeSurface(kind))duplicateHydrateRetry[kind]=setTimeout(()=>{duplicateHydrateRetry[kind]=null;if(!document.hidden&&activeSurface(kind)&&matrixDataset(kind)===d)hydrateMatrixDuplicates(kind,d,{render:true}).catch(()=>{});},delay);
    }else duplicatePollAttempts[kind]=0;
  }catch(e){
    if(matrixDataset(kind)===d){d.duplicates_pending=false;console.warn('Duplicate candidate snapshot unavailable',e);}
  }
}

function updateMatrixCoverageLabel(kind,data){
  const count=matrixControls(kind).count;
  const base=String(count.textContent||'').split(' · chapter coverage')[0];
  count.textContent=base+(data.chapter_stats_error?' · chapter coverage unavailable; Refresh to retry':data.chapter_stats_pending?' · chapter coverage refreshing':'');
}
const ENTRY_CHAPTER_FIELDS=['served_chapter_count','served_latest_chapter','chapter_records_checked'];
const RECORD_CHAPTER_FIELDS=['chapter_count','latest_chapter','missing_chapter_count','chapters_checked','chapter_checked_at','chapter_check_error'];
function matrixRecordKey(row){return [row.entry_id,row.source_id,row.source_manga_id].join('|');}
function preserveMatrixChapterStats(data,previous){
  if(!data.chapter_stats_pending||!previous)return;
  const entries=new Map((previous.entries||[]).map(e=>[Number(e.id),e]));
  const records=new Map([...(previous.matches||[]),...(previous.variants||[])].map(r=>[matrixRecordKey(r),r]));
  for(const row of data.entries||[]){const old=entries.get(Number(row.id));if(old&&!old.chapter_stats_pending&&old.chapter_records_checked!==undefined){for(const key of ENTRY_CHAPTER_FIELDS)row[key]=old[key];row.chapter_stats_pending=false;row.chapter_stats_error='';}}
  for(const row of [...(data.matches||[]),...(data.variants||[])]){const old=records.get(matrixRecordKey(row));if(old&&!old.chapter_stats_pending&&old.chapters_checked!==undefined){for(const key of RECORD_CHAPTER_FIELDS)row[key]=old[key];row.chapter_stats_pending=false;row.chapter_stats_error='';}}
}
function mergeMatrixChapterStats(data,payload){
  let changed=false;
  const assign=(row,values)=>{for(const [key,value] of Object.entries(values)){if(row[key]!==value){row[key]=value;changed=true;}}};
  for(const row of data.entries||[]){const s=payload.entry_stats?.[String(row.id)]||{};assign(row,{served_chapter_count:Number(s.served_chapter_count||0),served_latest_chapter:s.served_latest_chapter??null,chapter_records_checked:!!s.chapter_records_checked,chapter_stats_pending:false,chapter_stats_error:''});}
  const records=new Map((payload.record_stats||[]).map(r=>[matrixRecordKey(r),r]));
  for(const row of [...(data.matches||[]),...(data.variants||[])]){const s=records.get(matrixRecordKey(row))||{};assign(row,{chapter_count:Number(s.chapter_count||0),latest_chapter:s.latest_chapter??null,missing_chapter_count:Number(s.missing_chapter_count||0),chapters_checked:!!s.chapters_checked,chapter_checked_at:s.chapter_checked_at??null,chapter_check_error:String(s.chapter_check_error||''),chapter_stats_pending:false,chapter_stats_error:''});}
  data.chapter_stats_pending=false;data.chapter_stats_error='';return changed;
}
async function hydrateMatrixChapters(kind,data){
  matrixStatsRequests[kind]?.abort();
  const controller=new AbortController(),revision=catalogViewRevision;matrixStatsRequests[kind]=controller;
  updateMatrixCoverageLabel(kind,data);
  try{
    const payload=await api('/api/admin/matrix/chapter-stats',{method:'POST',body:JSON.stringify({ids:(data.entries||[]).map(e=>Number(e.id))}),timeoutMs:15000,signal:controller.signal,noMutationRefresh:true});
    if(matrixDataset(kind)!==data||controller.signal.aborted)return;
    // A completed manual check/merge/reset is newer than this request's snapshot.
    if(catalogViewRevision!==revision){hydrateMatrixChapters(kind,data);return;}
    const changed=mergeMatrixChapterStats(data,payload);
    updateMatrixCoverageLabel(kind,data);
    if(changed&&activeSurface(kind))requestSurfaceRender(kind);
  }catch(error){
    if(controller.signal.aborted||matrixDataset(kind)!==data)return;
    data.chapter_stats_error=String(error?.message||error);
    for(const row of [...(data.entries||[]),...(data.matches||[]),...(data.variants||[])])if(row.chapter_stats_pending)row.chapter_stats_error=data.chapter_stats_error;
    if(activeSurface(kind))requestSurfaceRender(kind);
    updateMatrixCoverageLabel(kind,data);
  }finally{if(matrixStatsRequests[kind]===controller)matrixStatsRequests[kind]=null;}
}

function loadMatrixView(kind,{render=true}={}){
  if(kind==='subbed'&&globalThis.AutoSourcarrLibrary)return globalThis.AutoSourcarrLibrary.load();
  const c=matrixControls(kind),query=String(c.search.value||''),sourceId=String(c.source?.value||'');
  const key=JSON.stringify([query,sourceId]),requests=matrixRequests[kind];
  const current=()=>String(c.search.value||'')===query&&String(c.source?.value||'')===sourceId;
  if(requests.has(key))return requests.get(key);
  const url='/api/admin/matrix?'+(kind==='subbed'?'subbed=true&':'')+'search='+encodeURIComponent(query)+'&limit=500&include_chapter_stats=false'+(sourceId?'&source_id='+encodeURIComponent(sourceId):'');
  const request=(async()=>{
    try{
      const data=await api(url,{timeoutMs:15000});
      if(!current())return data;
      const previous=matrixDataset(kind);
      preserveMatrixChapterStats(data,previous);
      const visible=d=>JSON.stringify({entries:d.entries,sources:d.sources,matches:d.matches,variants:d.variants,discovery_outcomes:d.discovery_outcomes,subscription:d.subscription?{entry_ids:d.subscription.entry_ids,stats_by_entry:d.subscription.stats_by_entry,ok:d.subscription.ok,error:d.subscription.error,refreshing:!!d.subscription.refreshing}:null});
      const unchanged=previous&&visible(previous)===visible(data);
      if(unchanged){data.duplicates=previous.duplicates||{};data.duplicates_pending=previous.duplicates_pending;}
      setMatrixDataset(kind,data);
      if((!unchanged||String(c.count.textContent||'').startsWith('Refresh failed:'))&&(render||activeSurface(kind)))renderMatrix(kind);
      hydrateMatrixDuplicates(kind,data,{render:render||activeSurface(kind)});
      if(data.chapter_stats_pending)hydrateMatrixChapters(kind,data);
      if(matrixViewRetry[kind]){clearTimeout(matrixViewRetry[kind]);matrixViewRetry[kind]=null;}
      if(kind==='subbed'&&data.subscription?.refreshing){
        matrixViewRetry[kind]=setTimeout(()=>{matrixViewRetry[kind]=null;if(activeSurface(kind)&&matrixDataset(kind)===data)loadSubbed().catch(()=>{});},2000);
      }
      return data;
    }catch(error){
      if(current())c.count.textContent='Refresh failed: '+String(error?.message||error)+(matrixDataset(kind)?' · showing previous results':'');
      throw error;
    }finally{requests.delete(key);}
  })();requests.set(key,request);return request;
}
function loadMatrix(options={}){return loadMatrixView('matrix',options);}
function loadSubbed(options={}){return loadMatrixView('subbed',options);}
async function refreshBothMatrices(){await Promise.allSettled([loadMatrix(),loadSubbed()]);}
$('#loadMatrix').onclick=()=>loadMatrix().catch(e=>toast(e.message)); $('#loadSubbed').onclick=()=>loadSubbed().catch(e=>toast(e.message));
if($('#repairUnmatchedSubbed'))$('#repairUnmatchedSubbed').onclick=async()=>{
  const b=$('#repairUnmatchedSubbed');b.disabled=true;
  try{const r=await api('/api/admin/subbed/repair-unmatched',{method:'POST',timeoutMs:12000});toast(r.already_running?'A Subbed discovery task is already running':`Repair queued for ${Number(r.entries||0)} source-less subscribed entries`);loadTasks().catch(()=>{});}
  catch(e){toast('Unable to queue repair: '+e.message);}finally{b.disabled=false;}
};
if($('#searchAllSubbed'))$('#searchAllSubbed').onclick=async()=>{
  const b=$('#searchAllSubbed');b.disabled=true;
  try{const r=await api('/api/admin/subbed/search-all',{method:'POST',timeoutMs:12000});toast(r.already_running?'A Subbed discovery task is already running; this request was not queued again':`Search all queued for ${Number(r.entries||0).toLocaleString()} subscribed entr${Number(r.entries||0)===1?'y':'ies'}`);loadTasks().catch(()=>{});}
  catch(e){toast('Unable to search all Subbed entries: '+e.message);}
  finally{b.disabled=false;}
};
for(const kind of ['matrix','subbed']){
  ['MinMatches','EntryMatches','TitleState','Status','PublicationStatus','Sort'].forEach(s=>{const el=$('#'+kind+s);if(el)el.onchange=()=>renderMatrix(kind);});
  const sourceSelect=$('#'+kind+'Source');if(sourceSelect)sourceSelect.onchange=()=>loadMatrixView(kind).catch(error=>toast(error.message));
  $('#'+kind+'Search').addEventListener('keydown',e=>{if(e.key==='Enter')(kind==='matrix'?loadMatrix:loadSubbed)().catch(error=>toast(error.message));});
}

function entryDetailSection(key,title,content,{open=true}={}){
  return `<details class="card spaced entryDetailSection" data-detail-section="${key}"${open?' open':''}><summary>${title}</summary><div class="entryDetailContent">${content}</div></details>`;
}
function entryAlternateTitlesHtml(e){
  return `<div class="chipList">${(e.titles||[]).map(t=>`<span class="titleChip">${esc(t.title)} <small>${esc(t.title_type)}</small></span>`).join('')||'<span class="muted">No alternate titles.</span>'}</div>`;
}
function captureEntryDetailState(root){
  const open=new Set([...root.querySelectorAll('[data-detail-section][open]')].map(section=>section.dataset.detailSection));
  const drafts=[];
  for(const input of root.querySelectorAll('input[id],textarea[id]'))if(input.value!==input.defaultValue)drafts.push({id:input.id,value:input.value});
  const focused=document.activeElement;
  const focus=focused?.id&&root.contains?.(focused)?{id:focused.id,start:focused.selectionStart,end:focused.selectionEnd}:null;
  return {open,drafts,focus};
}
function restoreEntryDetailState(root,state){
  if(!state)return;
  for(const draft of state.drafts){const input=root.querySelector('#'+draft.id);if(input)input.value=draft.value;}
  if(state.focus){const input=root.querySelector('#'+state.focus.id);input?.focus?.({preventScroll:true});if(input&&Number.isInteger(state.focus.start))try{input.setSelectionRange(state.focus.start,state.focus.end);}catch(_){}}
}
function inspectorRoot(kind){return kind==='browse'?$('#browseInspectorBody'):matrixControls(kind).inspector;}
function inspectorEntryId(kind){return kind==='browse'?browseState.selectedEntryId:activeInspector[kind];}
function inspectorGeneration(kind){return kind==='browse'?browseInspectorGeneration:entryInspectorGeneration[kind];}
function revealEntryInspector(target){
  // Explicit row selection must reveal the details even after scrolling far down.
  requestAnimationFrame(()=>{if(target.hidden)return;const rect=target.getBoundingClientRect();if(rect.top<0||rect.top>window.innerHeight*.65)target.scrollIntoView({block:'start',behavior:'instant'});target.focus?.({preventScroll:true});});
}
function chapterListHtml(ch,id,kind){
  return ch.slice(0,200).map(c=>`<button class="chapterrow chapterServingRow" onclick="openChapter(${c.id},this,'${kind}')"><span class="chapterServingName">${esc(c.display_name)}</span><span class="chapterServingSource"><b>Source:</b> ${esc(c.serving_source_name||'Unknown')}${c.serving_official?' <span class="tag officialTag">OFFICIAL</span>':''}</span><span class="chapterServingCandidates">${Number(c.candidate_count||0)} candidate${Number(c.candidate_count||0)===1?'':'s'}</span><span class="chapterServingEntry"><b>Entry:</b> ${esc(c.serving_source_entry_title||'')}</span></button>`).join('')||'<span class="muted">No chapters cached yet. Refresh this entry.</span>';
}
function entryChapterSections(kind){
  return entryDetailSection('chapters','Chapters',`<p class="muted">Primary and additional source records contribute their chapter coverage.</p><div class="chapterlist" data-entry-chapters>Open this section to load chapters.</div><div id="chapterDetail-${kind}"></div>`,{open:false})+entryDetailSection('history','Previously served',`<button type="button" data-load-history>Load history</button><pre id="historyBox-${kind}" class="muted"></pre>`,{open:false});
}
function bindEntryDetailSections(root,kind,id,signal=null,savedOpen=null){
  if(!savedOpen)globalThis.AutoSourcarrNavigation?.entrySectionsRendered(kind);
  const generation=inspectorGeneration(kind);
  const current=()=>!signal?.aborted&&root===inspectorRoot(kind)&&Number(inspectorEntryId(kind))===Number(id)&&inspectorGeneration(kind)===generation;
  if(savedOpen)for(const section of root.querySelectorAll('[data-detail-section]'))section.open=savedOpen.has(section.dataset.detailSection);
  const chapterSection=root.querySelector('[data-detail-section="chapters"]'),list=root.querySelector('[data-entry-chapters]');
  let loaded=false,pending=false;
  const loadChapters=async()=>{
    if(loaded||pending||!current()||!list)return;pending=true;list.textContent='Loading chapter sources…';
    try{const payload=await api('/api/v1/entries/'+id+'/chapters',{signal,timeoutMs:15000,suppressClientError:true});if(!current()||!list.isConnected)return;list.innerHTML=chapterListHtml(payload.items||[],id,kind);loaded=true;}
    catch(error){if(!current()||!list.isConnected)return;list.textContent='Chapters could not be loaded: '+error.message+' ';const retry=document.createElement('button');retry.type='button';retry.textContent='Retry chapters';retry.onclick=()=>loadChapters();list.append(retry);}
    finally{pending=false;}
  };
  if(chapterSection){chapterSection.ontoggle=()=>{if(chapterSection.open)loadChapters();};if(chapterSection.open)loadChapters();}
  const history=root.querySelector('[data-detail-section="history"]'),historyButton=root.querySelector('[data-load-history]');
  if(historyButton)historyButton.onclick=()=>loadHistory(id,kind);
  const loadVisibleHistory=()=>{if(history?.open&&current()&&!$('#historyBox-'+kind)?.textContent)historyButton?.click();};
  if(history){history.ontoggle=loadVisibleHistory;loadVisibleHistory();}
}

// Inline entry inspector (the old Entry details functionality lives here now).
function selectMatrixRow(kind,id){
  const table=matrixControls(kind).table;
  table?.querySelectorAll('.matrixRow.selectedRow').forEach(row=>row.classList.remove('selectedRow'));
  if(id!=null)table?.querySelector(`.matrixRow[data-entry-id="${id}"]`)?.classList.add('selectedRow');
}
window.openInlineEntry=async(kind,id,userEvent=null)=>{
  // Entry visibility is user-owned state. Background Tasks are allowed to refresh
  // data, but they cannot synthesize an inspector open/close transition.
  if(!userEvent?.isTrusted)return;
  if(entryInspectorAbort[kind])entryInspectorAbort[kind].abort();
  const controller=new AbortController();entryInspectorAbort[kind]=controller;
  const generation=++entryInspectorGeneration[kind];
  if(activeInspector[kind]==null)inspectorScrollPositions[kind]={x:window.scrollX||0,y:window.scrollY||0};
  activeInspector[kind]=Number(id); selectMatrixRow(kind,id);
  const target=matrixControls(kind).inspector;
  target.hidden=false;
  revealEntryInspector(target);
  try{await renderInlineEntry(kind,id,controller.signal,{generation,showLoading:true,scroll:false});}
  catch(e){
    if(controller.signal.aborted)return;
    const target=matrixControls(kind).inspector;
    target.hidden=false;target.innerHTML=`<button class="closeInline" onclick="closeInlineEntry('${kind}',event)">Back to results</button><div class="card errorCard"><strong>Unable to open entry.</strong><div class="muted">${esc(e.message)}</div><button onclick="openInlineEntry('${kind}',${Number(id)},event)">Retry</button></div>`;
    toast('Entry details failed: '+e.message);
  }
};
function metadataIdentityHtml(e){
  const identities=(e?.identities||[]).filter(x=>x&&x.external_id);
  if(!identities.length)return '<span class="muted">No external identity resolved yet.</span>';
  return identities.map(x=>{
    const name=esc(x.provider_name||x.provider||'Metadata');
    const id=esc(x.external_id);
    const pct=Math.round(Number(x.confidence||0)*100);
    const scope=esc(x.identity_scope||'work');
    const rawHref=String(x.external_url||'').trim();
    const href=/^https?:\/\//i.test(rawHref)?rawHref:'';
    const label=href?`<a href="${esc(href)}" target="_blank" rel="noopener noreferrer"><strong>${name}</strong></a>`:`<strong>${name}</strong>`;
    return `<div class="sourceMatchLine metadataIdentity"><div class="grow">${label}<div class="muted">${id} · ${scope} identity · ${pct}% confidence</div></div></div>`;
  }).join('');
}

function inspectorChapterInfo(match,entryId,sourceId,kind){
  const count=Number(match.chapter_count||0),latest=match.latest_chapter,missing=Number(match.missing_chapter_count||0);
  let text='';
  if(latest==null&&count===0){
    if(match.chapter_check_error)text='Chapter check failed';
    else if(match.chapters_checked)text='No chapters found';
    else text='No chapters cached';
  }else{text=`Latest ch. ${fmtChapterNumber(latest)} · ${count} cached${missing>0?` · ${missing} missing`:''}`;}
  return `<div class="muted">${esc(text)}</div><button class="miniButton chapterInspectorCheck" onclick="event.stopPropagation();checkSourceChapters(${Number(entryId)},'${jsq(sourceId)}',${Number(match.source_manga_id)},this)">Check chapters</button>`;
}

// 0.4.5: "Open in WebView" on entries. The primary (locked, else first) source record opens at its
// cached source page; a missing or unsafe address falls back to the source's base URL.
function primaryEntrySource(e){const rows=e?.matches||[];return rows.find(m=>m.locked)||rows[0]||null;}
function entryWebviewButton(e){const m=primaryEntrySource(e);return m?`<button class="sourceWebviewButton" onclick="openEntrySourceWebview('${jsq(m.real_url||'')}','${jsq(m.source_id)}')" title="Open this entry's primary source page in the shared WebView">Open in WebView</button>`:'';}
window.openEntrySourceWebview=(url,sourceId)=>openSourceWebview(url||'',sourceId||'');
async function renderInlineEntry(kind,id,signal=null,options={}){
  const generation=Number(options.generation??entryInspectorGeneration[kind]);
  const target=matrixControls(kind).inspector;
  // Never change target.hidden here. This function is also used by asynchronous
  // refresh work; visibility belongs exclusively to openInlineEntry/closeInlineEntry.
  if(target.hidden||Number(activeInspector[kind])!==Number(id)||generation!==entryInspectorGeneration[kind])return;
  if(options.showLoading!==false)target.innerHTML=`<button class="closeInline" onclick="closeInlineEntry('${kind}',event)">Back to results</button><div class="card">Loading entry…</div>`;
  if(options.scroll!==false&&!document.documentElement.dataset.ui)target.scrollIntoView({behavior:'smooth',block:'nearest'});
  const coverNonce=coverRenderNonce;
  const e=await api('/api/admin/entries/'+id,{signal,timeoutMs:15000});
  if(signal?.aborted||generation!==entryInspectorGeneration[kind]||Number(activeInspector[kind])!==Number(id))return;
  applyEntrySnapshotEverywhere(e,{render:false});noteEntryThumbnail(e);

  const linked=[...(e.matches||[]),...(e.source_variants||[])],linkedIds=new Set(linked.map(row=>String(row.source_id)));
  const inspectorSources=sourceRows.filter(row=>row.enabled_language||linkedIds.has(String(row.source_id)));
  for(const record of linked)if(!inspectorSources.some(row=>String(row.source_id)===String(record.source_id)))inspectorSources.push({source_id:String(record.source_id),name:record.source_name||String(record.source_id),lang:record.source_lang||'',enabled_language:true});
  const allGroups=groupSources(inspectorSources);
  const recordsBySource=new Map();
  const push=(m,isVariant=false)=>{const key=String(m.source_id);if(!recordsBySource.has(key))recordsBySource.set(key,[]);recordsBySource.get(key).push({...m,is_variant:isVariant});};
  (e.matches||[]).forEach(m=>push(m,false)); (e.source_variants||[]).forEach(m=>push(m,true));
  const sourceCards=allGroups.map(g=>{
    const gm=[];
    for(const v of g.variants){for(const m of (recordsBySource.get(String(v.source_id))||[]))gm.push({v,m});}
    if(gm.length){return `<div class="inspectorSource"><div class="sourceHeading"><strong>${esc(g.name)}</strong><span class="muted">${esc(groupSummary(g))}</span></div>${gm.map(({v,m})=>`<div class="sourceMatchLine"><img src="${sourceCoverUrl(m.source_manga_id,coverNonce)}" data-source-cover="${m.source_manga_id}" class="sourceLineCover"><div class="grow"><span class="langcode">${esc(v.lang||'—')}</span> <strong>${esc(m.source_title)}</strong><div class="muted">${esc(v.name||v.display_name||v.source_id)} · ${Math.round(m.confidence*100)}% · ${m.locked?'manual locked':(m.is_variant?'additional match':'primary automatic')}</div>${inspectorChapterInfo(m,id,v.source_id,kind)}</div><button class="miniButton sourceWebviewButton" onclick="openEntrySourceWebview('${jsq(m.real_url||'')}','${jsq(v.source_id)}')">Open in WebView</button>${m.is_variant?`<button class="miniButton primaryAction" onclick="promoteVariantPrimary(${id},'${jsq(v.source_id)}',${Number(m.source_manga_id)},this)">Use as primary</button><button class="danger ghost" onclick="clearVariant(${id},'${jsq(v.source_id)}',${Number(m.source_manga_id)},'${kind}')">Remove</button>`:`<button onclick="openMatchBrowser(${id},'${jsq(v.source_id)}')">Edit matches</button><button class="danger ghost" onclick="clearMatch(${id},'${jsq(v.source_id)}','${kind}')">Clear primary</button>`}</div>`).join('')}</div>`;}
    const v=selectedVariants(g).find(x=>x.enabled_search)||g.variants.find(x=>x.enabled_search);
    return v?`<div class="inspectorSource missingSource"><div class="grow"><strong>${esc(g.name)}</strong><div class="muted">No match · ${esc(langSummary(g))}</div></div><button onclick="openMatchBrowser(${id},'${jsq(v.source_id)}')">Search all names</button></div>`:'';
  }).join('');
  const interaction=options.showLoading===false?captureEntryDetailState(target):null;
  target.innerHTML=`<div class="inspectorTop"><button class="closeInline" onclick="closeInlineEntry('${kind}',event)">Back to results</button><img src="${entryCoverUrl(id,coverNonce)}" data-entry-cover="${id}" class="inspectorCover"><div class="inspectorInfo"><div class="toolbar wrap"><h2 class="grow">${esc(e.preferred_title)}</h2><span class="publicationPill" data-inspector-status>${esc(e.status_label||'Unknown')}</span>${entryWebviewButton(e)}<button class="danger" onclick="deleteEntry(${id},'${kind}')">Delete entry</button></div><div class="muted" data-inspector-meta>ID ${id} · ${e.match_count} source family(s) · ${(e.source_variants||[]).length} additional match(es)</div><div class="entryDescription" data-inspector-description>${esc(e.description||'No description available.').replace(/\n/g,'<br>')}</div><div class="toolbar wrap"><button onclick="refreshEntry(${id},'${kind}',this)">Refresh</button><button type="button" data-manage-entry="${id}">Library &amp; serving profile</button><button onclick="discoverAdditional(${id},'${kind}')">Find missing + extra source records</button><button class="dangerAction" onclick="redoEntryAutomation(${id},'${kind}',this)">Redo automation</button><input id="mainTitle-${kind}" value="${esc(e.preferred_title)}" placeholder="Main title"><button onclick="setMainTitle(${id},'${kind}')">Set main title</button><button class="danger ghost" onclick="clearManualTitles(${id},'${kind}',this)" ${e.manual_preferred_title||(e.titles||[]).some(t=>t.title_type==='manual')?'':'disabled'}>Remove manual titles</button><input id="newAlias-${kind}" placeholder="Add alternate title"><button onclick="addAlias(${id},'${kind}')">Add title</button></div></div></div>${entryDetailSection('titles','Alternate titles',entryAlternateTitlesHtml(e))}${entryDetailSection('tags','Tags',`<div class="entryTagMetadata" data-entry-tags="${Number(id)}"><p class="muted">Loading saved tags…</p></div>`)}${entryDetailSection('metadata','Metadata',`<div class="toolbar"><h3 class="grow">Metadata identities</h3><button class="miniButton" onclick="refreshIdentity(${id},'${kind}',this)">Re-resolve</button></div><div class="sourceInspectorList" data-identity-list>${metadataIdentityHtml(e)}</div><div data-gallery-metadata></div>`)}${entryDetailSection('matches','Source matches',`<div class="sourceInspectorList">${sourceCards}</div>`)}${entryChapterSections(kind)}`;
  restoreEntryDetailState(target,interaction);
  bindEntryDetailSections(target,kind,id,signal,interaction?.open);
  window.loadEntryTags?.(target.querySelector('[data-entry-tags]'),id,signal,target.querySelector('[data-gallery-metadata]'));

}
window.closeInlineEntry=(kind,userEvent=null)=>{if(!userEvent?.isTrusted)return;entryInspectorGeneration[kind]+=1;if(entryInspectorAbort[kind]){entryInspectorAbort[kind].abort();entryInspectorAbort[kind]=null;}activeInspector[kind]=null;matrixControls(kind).inspector.hidden=true;matrixControls(kind).inspector.innerHTML='';selectMatrixRow(kind,null);requestAnimationFrame(()=>{const pos=inspectorScrollPositions[kind];if(pos)window.scrollTo(pos.x,pos.y);flushDeferredUiRenders();});};
async function refreshInlineEntryStable(kind,id){
  if(Number(activeInspector[kind])!==Number(id))return;
  const generation=++entryInspectorGeneration[kind];
  return renderInlineEntry(kind,id,null,{generation,showLoading:false,scroll:false});
}
window.refreshIdentity=async(id,kind,button)=>{
  if(button){button.disabled=true;button.textContent='Resolving…';}
  try{
    const r=await api(`/api/admin/entries/${id}/identity/refresh?force=true`,{method:'POST',timeoutMs:120000});
    const entry=r.entry||await api(`/api/admin/entries/${id}`,{timeoutMs:10000,suppressClientError:true});
    applyEntrySnapshotEverywhere(entry,{render:false});
    const root=matrixControls(kind).inspector;
    const list=root?.querySelector('[data-identity-list]');if(list&&Number(activeInspector[kind])===Number(id))list.innerHTML=metadataIdentityHtml(entry)+identityResolutionDiagnostics(r);
    patchOpenInspectorSnapshot(entry);
    toast(identityResolutionMessage(r));
  }catch(e){toast('Metadata identity refresh failed: '+e.message);}
  finally{if(button){button.disabled=false;button.textContent='Re-resolve';}}
};
window.refreshEntry=async(id,kind,button=null)=>{
  if(button){button.disabled=true;button.textContent='Queued…';}
  toast('Entry refresh queued');
  try{
    const r=await api(`/api/admin/entries/${id}/refresh-all`,{method:'POST',timeoutMs:10000});
    renewCoverEpoch();
    scheduleEntrySnapshotSync(id,[1500,5000,12000],true);loadTasks().catch(()=>{});
    toast(r.already_running?'Entry refresh is already running':'Refreshing the complete entry in the background');
  }catch(e){toast('Refresh failed: '+e.message);}
  finally{if(button){button.disabled=false;button.textContent='Refresh';}}
};
window.refreshEntryCover=async(id,kind)=>{toast('Refreshing cover…');try{await api(`/api/admin/entries/${id}/refresh-covers`,{method:'POST',timeoutMs:15000});renewCoverEpoch();await refreshInlineEntryStable(kind,id);toast('Cover refresh queued');}catch(e){toast('Cover refresh failed: '+e.message);}};
window.discoverAdditional=async(id,kind)=>{
  toast('Source scan queued');
  try{
    const r=await api(`/api/admin/entries/${id}/discover-additional?background=true`,{method:'POST',timeoutMs:8000});
    scheduleEntrySnapshotSync(id,[1800,6000,14000],true);loadTasks().catch(()=>{});
    toast(r.already_running?'Source scan is already running':'Finding missing + extra source records in the background');
  }catch(e){toast('Source scan failed: '+e.message);}
};
window.redoEntryAutomation=async(id,kind,button=null)=>{
  if(!confirm('Redo automatic matching for this entry and every automation-owned attachment? Manual/locked matches are preserved. Automatic source and duplicate relationships are rebuilt from clean evidence using the current rules.'))return;
  if(button){button.disabled=true;button.textContent='Queued…';}
  try{
    const r=await api(`/api/admin/entries/${id}/redo-automation`,{method:'POST',timeoutMs:8000});
    toast(r.already_running?'Automation redo is already running':'Automation redo queued');
    scheduleEntrySnapshotSync(id,[2500,7000,15000,30000],true);
    loadTasks().catch(()=>{});
  }catch(e){toast('Unable to redo automation: '+e.message);}
  finally{if(button){button.disabled=false;button.textContent='Redo automation';}}
};
window.addAlias=async(id,kind)=>{const input=$('#newAlias-'+kind);const v=input?.value.trim();if(!v)return;try{
  if(input){input.value='';input.placeholder='Saving…';}
  const entry=await api('/api/admin/entries/'+id+'/aliases',{method:'POST',body:JSON.stringify({title:v}),timeoutMs:8000});
  applyEntrySnapshotEverywhere(entry);
  const root=matrixControls(kind).inspector;const chips=root?.querySelector('[data-detail-section="titles"] .chipList');
  if(chips)chips.insertAdjacentHTML('beforeend',`<span class="titleChip">${esc(v)} <small>manual</small></span>`);
  toast('Alternate title added');
}catch(e){toast('Unable to add title: '+e.message);}finally{if(input)input.placeholder='Add alternate title';}};
window.setMainTitle=async(id,kind)=>{const input=$('#mainTitle-'+kind);const v=input?.value.trim();if(!v)return;try{
  patchOpenInspectorTitle(id,v);
  const entry=await api('/api/admin/entries/'+id+'/preferred-title',{method:'POST',body:JSON.stringify({title:v}),timeoutMs:8000});
  applyEntrySnapshotEverywhere(entry);patchOpenInspectorTitle(id,entry.preferred_title||v);toast('Main title updated');
}catch(e){toast('Unable to change main title: '+e.message);syncEntrySnapshot(id,{refreshInspector:false});}};
window.clearManualTitles=async(id,kind,button)=>{if(!confirm('Remove the manual main-title override and all manually added alternate titles for this entry? The main title will return to the most-used title among its bound source records.'))return;const oldText=button?.textContent;if(button){button.disabled=true;button.textContent='Removing…';}try{const entry=await api('/api/admin/entries/'+id+'/manual-titles',{method:'DELETE',timeoutMs:8000});applyEntrySnapshotEverywhere(entry);toast('Manual titles removed');if(activeInspector[kind]===Number(id))await refreshInlineEntryStable(kind,id);}catch(e){toast('Unable to remove manual titles: '+e.message);}finally{if(button&&button.isConnected){button.disabled=false;button.textContent=oldText||'Remove manual titles';}}};
window.loadHistory=async(id,kind)=>{
  const target=$('#historyBox-'+kind),generation=inspectorGeneration(kind);
  if(!target||Number(inspectorEntryId(kind))!==Number(id))return;
  const request={};inspectorDetailRequests.set(target,request);
  const current=()=>inspectorDetailRequests.get(target)===request&&generation===inspectorGeneration(kind)
    &&Number(inspectorEntryId(kind))===Number(id)&&$('#historyBox-'+kind)===target;
  target.textContent='Loading history…';
  try{
    const h=await api('/api/admin/entries/'+id+'/history');
    if(!current())return;
    target.textContent=h.history.length?h.history.map(x=>`${x.served_at}  ${x.chapter?.display_name||x.virtual_chapter_id}  ← ${x.source_name} (${x.reason})`).join('\n'):'No chapter has been served yet.';
  }catch(error){if(current())target.textContent='History could not be loaded: '+error.message;}
};
window.openChapter=async(chapterId,button,kind)=>{
  const target=$('#chapterDetail-'+kind),generation=inspectorGeneration(kind),entryId=inspectorEntryId(kind);
  if(!target||entryId==null)return;
  const request={};inspectorDetailRequests.set(target,request);
  inspectorRoot(kind).querySelectorAll('.chapterrow').forEach(x=>x.classList.remove('selected'));
  if(button)button.classList.add('selected');
  const d=await api('/api/admin/chapters/'+chapterId);
  if(inspectorDetailRequests.get(target)!==request||generation!==inspectorGeneration(kind)
    ||inspectorEntryId(kind)!==entryId||$('#chapterDetail-'+kind)!==target)return;
  const served=d.served?.candidate_id,wouldServe=Number(d.would_serve_candidate_id||0);const candidateHtml=(d.candidates||[]).map(c=>`<div class="candidate ${wouldServe===Number(c.id)?'selectedCandidate':''}"><div><strong>${esc(c.source_name)}</strong>${c.official?' <span class="tag officialTag">OFFICIAL</span>':''}${wouldServe===Number(c.id)?' <span class="tag manual">WILL SERVE</span>':''}${served===c.id?' <span class="tag">LAST SERVED</span>':''}</div><div class="muted"><b>Entry:</b> ${esc(c.source_entry_title||'Unknown source entry')}<br>${esc(c.name)} · ${c.page_count||'?'} pages</div><div>Resolver ${fmtScore(c.resolver_score)} · Quality ${fmtScore(c.quality_score)}</div><div class="compactMatchActions"><button onclick="qualityCandidate(${c.id},${chapterId},'${kind}')">Analyze quality</button><button class="danger ghost" onclick="rejectChapterCandidate(${c.id},${chapterId},'${kind}')">Reject episode</button></div></div>`).join('')||'<span class="muted">No active candidates.</span>';const rejectedHtml=(d.rejected_candidates||[]).length?`<details class="rejectedCandidates"><summary>${d.rejected_candidates.length} rejected candidate${d.rejected_candidates.length===1?'':'s'}</summary><div class="candidategrid">${d.rejected_candidates.map(c=>`<div class="candidate rejectedCandidate"><div><strong>${esc(c.source_name)}</strong>${c.official?' <span class="tag officialTag">OFFICIAL</span>':''}</div><div class="muted"><b>Entry:</b> ${esc(c.source_entry_title||'Unknown source entry')}<br>${esc(c.name)}</div><button onclick="restoreChapterCandidate(${c.id},${chapterId},'${kind}')">Restore candidate</button></div>`).join('')}</div></details>`:'';target.innerHTML=`<div class="candidatebox"><h4>${esc(d.chapter.display_name)}</h4><div class="servingDecision">${wouldServe?`<strong>Serving decision:</strong> ${esc((d.candidates||[]).find(c=>Number(c.id)===wouldServe)?.source_name||'Unknown')} · ${esc((d.candidates||[]).find(c=>Number(c.id)===wouldServe)?.source_entry_title||'')}`:'<strong>No candidate can currently serve this chapter.</strong>'}</div><div class="candidategrid">${candidateHtml}</div>${rejectedHtml}</div>`;};
window.qualityCandidate=async(candidateId,chapterId,kind)=>{toast('Sampling chapter images…');try{const r=await api('/api/admin/candidates/'+candidateId+'/quality',{method:'POST',body:JSON.stringify({sample_count:5})});toast(`Quality ${fmtScore(r.quality_score)} · ${r.median_width||'?'}×${r.median_height||'?'} · broken ${r.broken_pages||0}`);await openChapter(chapterId,null,kind);}catch(e){toast('Quality analysis failed: '+e.message);}};
window.rejectChapterCandidate=async(candidateId,chapterId,kind)=>{if(!confirm('Reject this episode candidate? It will stay rejected after source refreshes and will not be served unless you restore it.'))return;try{await api(`/api/admin/candidates/${Number(candidateId)}/reject`,{method:'POST'});toast('Episode candidate rejected');await openChapter(chapterId,null,kind);if(activeInspector[kind]!=null)refreshInlineEntryStable(kind,activeInspector[kind]).catch(()=>{});}catch(e){toast('Unable to reject episode: '+e.message);}};
window.restoreChapterCandidate=async(candidateId,chapterId,kind)=>{try{await api(`/api/admin/candidates/${Number(candidateId)}/restore`,{method:'POST'});toast('Episode candidate restored');await openChapter(chapterId,null,kind);if(activeInspector[kind]!=null)refreshInlineEntryStable(kind,activeInspector[kind]).catch(()=>{});}catch(e){toast('Unable to restore episode: '+e.message);}};
window.clearMatch=async(id,sourceId,kind)=>{if(!confirm('Clear this source match? Existing served-history records are kept.'))return;try{
  toast('Clearing match…');
  const entry=await api(`/api/admin/entries/${id}/matches/${encodeURIComponent(sourceId)}`,{method:'DELETE',timeoutMs:8000});
  applyEntrySnapshotEverywhere(entry);
  if(activeInspector[kind]===Number(id))refreshInlineEntryStable(kind,id).catch(()=>{});
  toast('Match cleared');
}catch(e){toast('Unable to clear match: '+e.message);}};

function applyEntrySnapshotToDataset(d,entry){
  if(!d||!entry)return;
  const id=Number(entry.canonical_id||entry.id);
  d.matches=(d.matches||[]).filter(x=>Number(x.entry_id)!==id).concat((entry.matches||[]).map(x=>({...x,entry_id:id})));
  d.variants=(d.variants||[]).filter(x=>Number(x.entry_id)!==id).concat((entry.source_variants||[]).map(x=>({...x,entry_id:id})));
  const idx=(d.entries||[]).findIndex(x=>Number(x.id)===id);
  if(idx>=0)d.entries[idx]={...d.entries[idx],preferred_title:entry.preferred_title,manual_preferred_title:entry.manual_preferred_title,status:entry.status,status_label:entry.status_label,match_count:entry.match_count,entry_match_count:entry.entry_match_count??d.entries[idx].entry_match_count,thumbnail_url:entry.thumbnail_url};
}
function rerenderMatrixPreservingScroll(kind){
  const c=matrixControls(kind),wrap=c.table?.closest('.tablewrap');
  const top=wrap?.scrollTop||0,left=wrap?.scrollLeft||0;
  renderMatrix(kind);
  if(wrap){wrap.scrollTop=top;wrap.scrollLeft=left;}
}
function patchOpenInspectorSnapshot(entry){
  if(!entry)return;
  const id=Number(entry.canonical_id||entry.id);if(!id)return;
  const patchTitleInput=input=>{
    if(!input||!entry.preferred_title)return;
    // Focus alone is not a draft marker: the user can click away before saving.
    // defaultValue tracks the last accepted server title without discarding edits.
    if(input.value===entry.preferred_title){input.defaultValue=entry.preferred_title;return;}
    if(document.activeElement!==input&&input.value===input.defaultValue){
      input.value=entry.preferred_title;input.defaultValue=entry.preferred_title;
    }
  };
  const patchRoot=(root,kind=null)=>{
    if(!root)return;
    const h=root.querySelector('.inspectorTop h2');if(h&&entry.preferred_title)h.textContent=entry.preferred_title;
    const status=root.querySelector('[data-inspector-status]');if(status&&entry.status_label)status.textContent=entry.status_label;
    const desc=root.querySelector('[data-inspector-description]');if(desc&&entry.description!=null)desc.innerHTML=esc(entry.description||'No description available.').replace(/\n/g,'<br>');
    if(kind)patchTitleInput(root.querySelector(`#mainTitle-${kind}`));
  };
  for(const kind of ['matrix','subbed'])if(activeInspector[kind]===id)patchRoot(matrixControls(kind).inspector,kind);
  if(browseInspectorIsOpen()&&Number(browseState.selectedEntryId)===id){
    const root=$('#browseInspectorBody');patchRoot(root,null);
    patchTitleInput($('#browseMainTitle'));
    if(entry.preferred_title&&$('#browseInspectorTitle'))$('#browseInspectorTitle').textContent=entry.preferred_title;
  }
}

function applyEntrySnapshotEverywhere(entry,{render=true,matchBrowser=true}={}){
  if(!entry)return;
  const id=Number(entry.canonical_id||entry.id);
  noteEntryThumbnail(entry);
  applyEntrySnapshotToDataset(subbedData,entry);
  applyEntrySnapshotToDataset(matrixData,entry);
  const bidx=(browseState.items||[]).findIndex(x=>Number(x.id)===id);
  if(bidx>=0){browseState.items[bidx]={...browseState.items[bidx],...entry,id};}
  if(matchBrowser&&matchBrowserState&&Number(matchBrowserState.entryId)===id&&!matchEntryMutationPending.get(id)){matchBrowserState.entry=entry;matchBrowserState.entryId=id;}
  patchOpenInspectorSnapshot(entry);
  if(!render)return;
  requestSurfaceRender('subbed');
  requestSurfaceRender('matrix');
  if(bidx>=0){
    const card=document.querySelector(`#browseGrid .browseCard[data-entry-id="${id}"]`);
    if(card)patchBrowseCardNode(card,browseState.items[bidx]);
    else requestSurfaceRender('browse');
  }
}
function removeEntryEverywhere(entryId){
  const id=Number(entryId);
  for(const d of [subbedData,matrixData]){
    if(!d)continue;
    d.entries=(d.entries||[]).filter(x=>Number(x.id)!==id);
    d.matches=(d.matches||[]).filter(x=>Number(x.entry_id)!==id);
    d.variants=(d.variants||[]).filter(x=>Number(x.entry_id)!==id);
    if(d.duplicates)delete d.duplicates[String(id)];
  }
  browseState.items=(browseState.items||[]).filter(x=>Number(x.id)!==id);
  requestSurfaceRender('subbed');
  requestSurfaceRender('matrix');
  requestSurfaceRender('browse');
}
async function syncEntrySnapshot(entryId,{refreshInspector=false}={}){
  const id=Number(entryId),revision=matchEntryMutationRevisions.get(id)||0,startedSequence=matchMutationSequence;
  if(matchEntryMutationPending.get(id))return null;
  try{
    const entry=await api(`/api/admin/entries/${id}`,{timeoutMs:8000,suppressClientError:true});
    // A reconciliation read begun before a manual choice cannot undo its optimism.
    const canonicalId=Number(entry.canonical_id||entry.id||id);
    if(matchEntryMutationPending.get(id)||matchEntryMutationPending.get(canonicalId)||
       (matchEntryMutationRevisions.get(id)||0)!==revision||(matchEntryMutationRevisions.get(canonicalId)||0)>startedSequence)return null;
    applyEntrySnapshotEverywhere(entry);
    if(matchBrowserState&&Number(matchBrowserState.entryId)===canonicalId)renderMatchBrowserTarget();
    // Background reconciliation updates inspector fields in place. Never rebuild an
    // open inspector just because a Task completed; that used to reset focus/scroll
    // and race with close/reopen clicks.
    if(refreshInspector)patchOpenInspectorSnapshot(entry);
    return entry;
  }catch(_e){return null;}
}
function scheduleEntrySnapshotSync(entryId,delays=[1200,5000],refreshInspector=true){
  const id=Number(entryId);
  const old=entrySyncTimers.get(id)||[];old.forEach(clearTimeout);
  const timers=delays.map((delay,index)=>setTimeout(()=>{
    syncEntrySnapshot(id,{refreshInspector:refreshInspector&&index===delays.length-1}).catch(()=>{});
    if(index===delays.length-1)entrySyncTimers.delete(id);
  },delay));
  entrySyncTimers.set(id,timers);
}
function patchOpenInspectorTitle(id,title){
  for(const kind of ['subbed','matrix']){
    if(activeInspector[kind]!==Number(id))continue;
    const root=matrixControls(kind).inspector;
    const h=root?.querySelector('.inspectorTop h2');if(h)h.textContent=title;
    const input=root?.querySelector(`#mainTitle-${kind}`);if(input)input.value=title;
  }
  if(browseInspectorIsOpen()&&Number(browseState.selectedEntryId)===Number(id)){
    const h=$('#browseInspectorBody')?.querySelector('.inspectorTop h2');if(h)h.textContent=title;
    const input=$('#browseMainTitle');if(input)input.value=title;
    if($('#browseInspectorTitle'))$('#browseInspectorTitle').textContent=title;
  }
}
window.promoteVariantPrimary=async(id,sourceId,sourceMangaId,button)=>{
  if(button)button.disabled=true;
  try{
    const entry=await api(`/api/admin/entries/${id}/variants/${encodeURIComponent(sourceId)}/${sourceMangaId}/promote`,{method:'POST'});
    applyEntrySnapshotEverywhere(entry);
    for(const openKind of ['subbed','matrix']){
      if(activeInspector[openKind]===Number(id))refreshInlineEntryStable(openKind,Number(id)).catch(()=>{});
    }
    loadTasks().catch(()=>{});
    toast('Primary source record updated');
  }catch(e){toast('Unable to change primary: '+e.message);if(button)button.disabled=false;}
};

window.clearVariant=async(id,sourceId,sourceMangaId,kind)=>{if(!confirm('Remove this additional source match? Chapter history is kept.'))return;try{
  toast('Removing additional match…');
  const entry=await api(`/api/admin/entries/${id}/variants/${encodeURIComponent(sourceId)}/${sourceMangaId}`,{method:'DELETE',timeoutMs:8000});
  applyEntrySnapshotEverywhere(entry);
  if(activeInspector[kind]===Number(id))refreshInlineEntryStable(kind,id).catch(()=>{});
  toast('Additional match removed');
}catch(e){toast('Unable to remove additional match: '+e.message);}};
window.deleteEntry=async(id,kind)=>{
  if(!confirm('Delete this AutoSourcarr entry from the catalog? Its current source records will be suppressed so it does not immediately reappear.'))return;
  try{
    await api(`/api/admin/entries/${id}`,{method:'DELETE'});
  }catch(e){
    if(String(e.message).includes('main Suwayomi library')){
      if(!confirm('This entry is added to your main Suwayomi library. Deleting it can break that library item. Delete it anyway?'))return;
      await api(`/api/admin/entries/${id}?force=true`,{method:'DELETE'});
    }else{throw e;}
  }
  if(activeInspector[kind]===id){activeInspector[kind]=null;matrixControls(kind).inspector.hidden=true;}
  removeEntryEverywhere(id);toast('Entry deleted and source records suppressed');health().catch(()=>{});
};

// Dedicated duplicate browser + per-entry management dialog.
let duplicateManagerEntryId=null;
function duplicateEntryCard(e,selectedId=null){
  const aliases=(e.titles||[]).filter(x=>x&&x!==e.preferred_title).slice(0,6);
  const sources=(e.sources||[]).map(x=>typeof x==='string'?x:(x.name||x.source_name||x.source_id)).filter(Boolean);
  const candidate=Number(selectedId)>0&&Number(selectedId)!==Number(e.id);
  const actions=candidate
    ? `<button class="goodAction" ${duplicateReviewButtonAttributes(selectedId,e.id)} onclick="reviewDuplicate(${Number(selectedId)},${Number(e.id)},'duplicate')">Match to selected</button><button ${duplicateReviewButtonAttributes(selectedId,e.id)} onclick="reviewDuplicate(${Number(selectedId)},${Number(e.id)},'not_duplicate')">Different work</button><button class="miniButton" onclick="closeDuplicateManager();switchTab('matrix');document.querySelector('#matrixSearch').value='${jsq(e.preferred_title)}';loadMatrix()">Open in matrix</button>`
    : `<button onclick="closeDuplicateManager();switchTab('matrix');document.querySelector('#matrixSearch').value='${jsq(e.preferred_title)}';loadMatrix()">Open in matrix</button>`;
  return `<article class="duplicateEntryCard" data-entry-id="${e.id}">
    <div class="duplicateEntryTop"><img loading="lazy" fetchpriority="low" decoding="async" src="${entryCoverUrl(e.id)}" data-entry-cover="${e.id}" class="duplicateCover"><div class="grow"><span class="entryIdPill">ID ${e.id}</span><h3>${esc(e.preferred_title)}</h3><div class="muted">${e.match_count||0} source family(s) · ${e.additional_records||0} additional record(s) · ${esc(e.status_label||'Unknown')}</div></div></div>
    <p class="duplicateDescription">${esc(shortText(e.description||'No usable summary available.',420))}</p>
    <div class="duplicateField"><b>Also known as</b><div class="chipList">${aliases.length?aliases.map(x=>`<span class="titleChip">${esc(x)}</span>`).join(''):'<span class="muted">No other stored titles</span>'}</div></div>
    <div class="duplicateField"><b>Sources</b><div class="muted">${esc(sources.join(' · ')||'No sources')}</div></div>
    <div class="cardActions duplicateDirectActions">${actions}</div>
  </article>`;
}
function pairEvidence(pair,entries){
  const a=entries.find(x=>Number(x.id)===Number(pair.left_id)),b=entries.find(x=>Number(x.id)===Number(pair.right_id));
  const titlePct=Math.round(Number(pair.title_score||0)*100),summaryPct=Math.round(Number(pair.summary_similarity||0)*100);
  const exact=Number(pair.exact_title_count||0),eligible=!!pair.auto_merge_eligible;
  const badge=eligible?'<span class="duplicateRuleBadge eligible">Auto-merge eligible</span>':'<span class="duplicateRuleBadge review">Review only</span>';
  const exactNames=(pair.exact_titles||[]).slice(0,4).map(x=>`<span class="titleChip">${esc(x)}</span>`).join('');
  const closest=(pair.matched_title&&pair.other_title)?`<div class="muted">Closest real-title comparison: “${esc(pair.matched_title)}” ↔ “${esc(pair.other_title)}”</div>`:'';
  return `<div class="duplicatePair ${eligible?'autoEligible':''}"><div class="duplicatePairText"><div class="duplicatePairNames"><strong>${esc(a?.preferred_title||`Entry ${pair.left_id}`)}</strong><span>↔</span><strong>${esc(b?.preferred_title||`Entry ${pair.right_id}`)}</strong>${badge}</div><div class="duplicateEvidenceGrid"><span><b>${exact}</b> exact real-title match${exact===1?'':'es'}</span><span><b>${summaryPct}%</b> cleaned-summary match</span><span><b>${titlePct}%</b> best fuzzy title</span></div><div class="pairReason">Automatic rule: ${esc(pair.auto_merge_reason||'manual review required')}.</div>${exactNames?`<div class="chipList">${exactNames}</div>`:''}${closest}</div><div class="duplicatePairActions"><button class="goodAction" ${duplicateReviewButtonAttributes(pair.left_id,pair.right_id)} onclick="reviewDuplicate(${pair.left_id},${pair.right_id},'duplicate')">Same work</button><button ${duplicateReviewButtonAttributes(pair.left_id,pair.right_id)} onclick="reviewDuplicate(${pair.left_id},${pair.right_id},'not_duplicate')">Different works</button></div></div>`;
}
function duplicateBrowserIndex(){
  const map=new Map();
  for(const group of (duplicateData?.groups||[])){
    const entries=new Map((group.entries||[]).map(e=>[Number(e.id),e]));
    for(const e of (group.entries||[])){
      const id=Number(e.id);
      if(!map.has(id))map.set(id,{entry:e,candidateIds:new Set(),pairs:[],entries:new Map()});
      const rec=map.get(id); for(const [eid,item] of entries)rec.entries.set(eid,item);
    }
    for(const pair of (group.pairs||[])){
      const left=Number(pair.left_id),right=Number(pair.right_id);
      for(const [id,other] of [[left,right],[right,left]]){
        if(!map.has(id))continue;
        const rec=map.get(id);rec.candidateIds.add(other);rec.pairs.push(pair);
      }
    }
  }
  return map;
}
function duplicateBrowserCard(rec){
  const e=rec.entry,count=rec.candidateIds.size;
  return `<article class="duplicateBrowserCard" data-entry-id="${e.id}" onclick="openDuplicateManager(${e.id})"><div class="duplicateBrowserCoverWrap"><img loading="lazy" fetchpriority="low" decoding="async" src="${entryCoverUrl(e.id)}" data-entry-cover="${e.id}" class="duplicateBrowserCover"><span class="duplicateCandidateBadge">${count} possible duplicate${count===1?'':'s'}</span></div><div class="duplicateBrowserBody"><span class="entryIdPill">ID ${e.id}</span><h3>${esc(e.preferred_title||'Untitled')}</h3><div class="muted">${e.match_count||0} source family(s) · ${esc(e.status_label||'Unknown')}</div><p>${esc(shortText(e.description||'No usable summary available.',240))}</p><div class="cardActions"><button type="button">Manage duplicates</button></div></div></article>`;
}
function duplicateSubbedSet(){
  return new Set((duplicateData?.subbed_entry_ids||[]).map(Number).filter(Number.isFinite));
}
function syncDuplicateScopeButtons(){
  document.querySelectorAll('.duplicateScopeFilter').forEach(button=>{
    const active=String(button.dataset.duplicateScope||'')===duplicateScope;
    button.classList.toggle('active',active);
    button.setAttribute('aria-selected',active?'true':'false');
  });
  writeLocalUiPreference('duplicateScope',duplicateScope);
}
function renderDuplicateAutomationStatus(){
  const automatic=duplicateData?.automatic_matching||{};
  const autoStatus=$('#duplicateAutomationStatus');
  const autoButton=$('#runAutomaticDuplicates');
  if(autoButton){
    autoButton.disabled=!!automatic.paused;
    autoButton.textContent=automatic.paused?'Paused by rebuild':'Run automatic sweep';
  }
  if(autoStatus){
    const eligiblePairs=(duplicateData?.groups||[]).reduce((n,g)=>n+(g.pairs||[]).filter(p=>p.auto_merge_eligible).length,0);
    if(!automatic.enabled)autoStatus.innerHTML='<strong>Automatic duplicate matching is disabled.</strong> Enable AUTOMATIC_DUPLICATE_MATCHING to run safe background merges.';
    else if(automatic.paused)autoStatus.innerHTML=`<strong>Automatic duplicate matching is paused.</strong> Redo/reset rebuild work has exclusive ownership of entry relationships${(automatic.pause_reasons||[]).length?` · ${esc((automatic.pause_reasons||[]).join(', '))}`:''}. One fresh sweep is queued after the rebuild finishes.`;
    else if(automatic.running)autoStatus.innerHTML=`<strong>Automatic duplicate matching is running.</strong> ${eligiblePairs} pair${eligiblePairs===1?'':'s'} currently meet the safe evidence gate. Progress is visible in Tasks.`;
    else if(automatic.last_run){
      const err=automatic.last_error?` · last error: ${esc(automatic.last_error)}`:'';
      autoStatus.innerHTML=`<strong>Automatic matching enabled.</strong> Last sweep ${esc(fmtWhen(automatic.last_run))}: checked ${Number(automatic.last_checked||0).toLocaleString()} entries, merged ${Number(automatic.last_merged||0).toLocaleString()}. ${eligiblePairs} safe pair${eligiblePairs===1?' remains':'s remain'}${err}.`;
    }else autoStatus.innerHTML=`<strong>Automatic matching enabled.</strong> No completed sweep recorded yet. ${eligiblePairs} pair${eligiblePairs===1?'':'s'} currently meet the safe evidence gate.`;
  }
}
function renderDuplicates(focusId=null){
  const renderStarted=Date.now();try{
  syncDuplicateScopeButtons();renderDuplicateAutomationStatus();
  if(duplicateData?.pending&&!(duplicateData?.groups||[]).length){
    $('#duplicateCount').textContent='Preparing duplicate review…';
    $('#duplicateGroups').innerHTML='<div class="emptyDuplicateState"><strong>Preparing duplicate review…</strong><span>AutoSourcarr is rebuilding the review index in the background. Browsing, matching and Status remain usable while this runs.</span></div>';
    return;
  }
  if(duplicateData?.subscription_pending){
    $('#duplicateScopeSubbedCount').textContent='…';
    $('#duplicateScopeUnsubbedCount').textContent='…';
    $('#duplicateCount').textContent='Preparing library filter…';
    $('#duplicateGroups').innerHTML='<div class="emptyDuplicateState"><strong>Checking main Suwayomi library…</strong><span>The duplicate index is ready; AutoSourcarr is only waiting for the Subbed/Unsubbed library snapshot.</span></div>';
    return;
  }
  const index=duplicateBrowserIndex();
  const subbedIds=duplicateSubbedSet();
  const all=[...index.values()];
  const subbedCount=all.filter(rec=>subbedIds.has(Number(rec.entry.id))).length;
  const unsubbedCount=all.length-subbedCount;
  $('#duplicateScopeSubbedCount').textContent=String(subbedCount);
  $('#duplicateScopeUnsubbedCount').textContent=String(unsubbedCount);
  if(focusId&&index.has(Number(focusId))){
    duplicateScope=subbedIds.has(Number(focusId))?'subbed':'unsubbed';
    syncDuplicateScopeButtons();
  }
  const q=($('#duplicateSearch')?.value||'').trim().toLowerCase();
  const countFilter=$('#duplicateSize')?.value||'';
  let shown=all.filter(rec=>{
    const e=rec.entry,n=rec.candidateIds.size,isSubbed=subbedIds.has(Number(e.id));
    if(duplicateScope==='subbed'&&!isSubbed)return false;
    if(duplicateScope==='unsubbed'&&isSubbed)return false;
    if(countFilter==='1'&&n!==1)return false;
    if(countFilter==='2'&&n!==2)return false;
    if(countFilter==='3'&&n<3)return false;
    if(!q)return true;
    return String(e.id)===q||String(e.preferred_title||'').toLowerCase().includes(q)||(e.titles||[]).some(t=>String(t).toLowerCase().includes(q));
  });
  shown.sort((a,b)=>b.candidateIds.size-a.candidateIds.size||String(a.entry.preferred_title||'').localeCompare(String(b.entry.preferred_title||'')));
  const scopeLabel=duplicateScope==='subbed'?'subbed':'unsubbed';
  $('#duplicateCount').textContent=`${shown.length} ${scopeLabel} entr${shown.length===1?'y':'ies'} with possible duplicates`;
  $('#duplicateGroups').innerHTML=shown.length?(typeof renderCompareDesk==='function'?renderCompareDesk(shown,focusId):`<div class="duplicateBrowserGrid">${shown.map(duplicateBrowserCard).join('')}</div>`):`<div class="emptyDuplicateState"><strong>No unresolved ${scopeLabel} duplicate candidates.</strong><span>Pairs marked as different works stay dismissed. Placeholder aliases and generated translations do not create duplicate candidates.</span></div>`;
  if(focusId&&index.has(Number(focusId))&&typeof renderCompareDesk!=='function')openDuplicateManager(Number(focusId));

  }finally{if(typeof recordUiTiming==='function')recordUiTiming('render','duplicates',Date.now()-renderStarted,{items:duplicateData?.groups?.length||0});}
}
function closeDuplicateManager(){duplicateManagerEntryId=null;$('#duplicateManagerModal').hidden=true;$('#duplicateManagerBody').innerHTML='';if(typeof syncModalScrollLock==='function')syncModalScrollLock();else document.body.classList.remove('modalOpen');requestAnimationFrame(flushDeferredUiRenders);}
window.closeDuplicateManager=closeDuplicateManager;
window.openDuplicateManager=id=>{
  const index=duplicateBrowserIndex(),rec=index.get(Number(id));if(!rec){closeDuplicateManager();return;}
  duplicateManagerEntryId=Number(id);const e=rec.entry;
  $('#duplicateManagerTitle').textContent=e.preferred_title||`Entry ${id}`;
  $('#duplicateManagerSubtitle').textContent=`ID ${id} · ${rec.candidateIds.size} possible duplicate${rec.candidateIds.size===1?'':'s'} · compare cleaned summaries and real titles`;
  const counterparts=[...rec.candidateIds].map(x=>rec.entries.get(x)).filter(Boolean);
  const allEntries=[e,...counterparts.filter(x=>Number(x.id)!==Number(e.id))];
  $('#duplicateManagerBody').innerHTML=`<div class="duplicateManagerSelected"><h3>Selected entry</h3>${duplicateEntryCard(e)}</div><div class="duplicateManagerCandidates"><h3>Possible duplicates</h3><div class="muted duplicateMatchHint">Use <b>Match to selected</b> to merge a candidate here. Manual duplicate matches are saved under Manual actions → Matches / groups.</div><div class="duplicateManagerCandidateGrid">${counterparts.map(x=>duplicateEntryCard(x,e.id)).join('')}</div></div><div class="duplicateEvidence"><h3>Pair evidence</h3>${rec.pairs.map(p=>pairEvidence(p,allEntries)).join('')||'<div class="muted">No unresolved pair evidence.</div>'}</div>`;
  $('#duplicateManagerModal').hidden=false;document.body.classList.add('modalOpen');
};
async function loadDuplicates(focusId=null,{render=true,quiet=false}={}){
  const serial=++duplicateLoadSerial,revision=duplicateViewRevision;
  if(render&&!duplicateData?.groups?.length)$('#duplicateGroups').innerHTML='<div class="muted">Loading duplicate candidates…</div>';
  let incoming=await api('/api/admin/duplicates',{timeoutMs:8000,suppressClientError:true});
  if(serial!==duplicateLoadSerial||revision!==duplicateViewRevision)return duplicateData;
  if(incoming.pending||incoming.stale){
    let groups=incoming.groups||[];
    for(const review of duplicateConfirmedReviews.values())groups=pruneDuplicateReviewGroups(groups,review.left,review.right,review.decision);
    incoming={...incoming,groups};
  }else duplicateConfirmedReviews.clear();
  const previous=duplicateData;
  // Keep the last usable snapshot visible while the backend recomputes a stale one.
  // A catalog mutation should not turn Duplicates into a blank loading screen.
  if(incoming?.groups?.length||!duplicateData||(!incoming.pending&&!incoming.stale)){duplicateData=incoming;}
  else duplicateData={...duplicateData,...incoming,groups:duplicateData.groups||[]};
  if(render){
    renderDuplicateAutomationStatus();
    const changed=!previous||JSON.stringify([previous.groups,previous.subbed_entry_ids,previous.subscription_pending,previous.pending])!==JSON.stringify([duplicateData.groups,duplicateData.subbed_entry_ids,duplicateData.subscription_pending,duplicateData.pending]);
    if(focusId!=null&&!structuralRenderBlocked('duplicates'))renderDuplicates(focusId);
    else if(changed)requestSurfaceRender('duplicates');
  }
  if(duplicateData?.pending||duplicateData?.stale||duplicateData?.subscription_pending){
    if(duplicateRetryTimer)clearTimeout(duplicateRetryTimer);
    duplicateRetryTimer=setTimeout(()=>{duplicateRetryTimer=null;if(duplicateCandidatesVisible())loadDuplicates(focusId,{render:true,quiet:true}).catch(()=>{});},1800);
  }else if(!quiet&&duplicateRetryTimer){clearTimeout(duplicateRetryTimer);duplicateRetryTimer=null;}
  return duplicateData;
}
const duplicateReviewBusy=new Set();
const duplicateConfirmedReviews=new Map();
let duplicateViewRevision=0,duplicateLoadSerial=0;
function duplicatePairBusy(left,right){
  const ids=new Set([Number(left),Number(right)]);
  return [...duplicateReviewBusy].some(key=>key.split(':').some(id=>ids.has(Number(id))));
}
function duplicateReviewButtonAttributes(left,right){
  const busy=duplicatePairBusy(left,right);
  return `data-review-left="${Number(left)}" data-review-right="${Number(right)}" aria-busy="${busy}" ${busy?'disabled':''}`;
}
function updateDuplicateReviewButtons(){
  document.querySelectorAll('[data-review-left]').forEach(button=>{
    const busy=duplicatePairBusy(button.dataset.reviewLeft,button.dataset.reviewRight);
    button.disabled=busy;button.setAttribute('aria-busy',String(busy));
  });
}
function pruneDuplicateReviewGroups(groups,left,right,decision){
  const affected=new Set([Number(left),Number(right)]);
  return (groups||[]).flatMap(group=>{
    const pairs=(group.pairs||[]).filter(pair=>{
      const a=Number(pair.left_id),b=Number(pair.right_id);
      return decision==='duplicate'?!affected.has(a)&&!affected.has(b):!(affected.has(a)&&affected.has(b));
    });
    if(!pairs.length)return [];
    const ids=new Set(pairs.flatMap(pair=>[Number(pair.left_id),Number(pair.right_id)]));
    return [{...group,pairs,entry_ids:[...ids],entries:(group.entries||[]).filter(e=>ids.has(Number(e.id)))}];
  });
}
window.reviewDuplicate=async(left,right,decision)=>{
  if(duplicatePairBusy(left,right))return;
  if(decision==='duplicate'&&!confirm('Merge these entries as the same work? All real source records, aliases and chapter candidates will be retained under one canonical entry.'))return;
  const key=[Math.min(left,right),Math.max(left,right)].join(':');duplicateReviewBusy.add(key);duplicateViewRevision++;
  updateDuplicateReviewButtons();
  const selected=duplicateManagerEntryId;
  try{
    const r=await api('/api/admin/duplicates/review',{method:'POST',body:JSON.stringify({left_entry_id:left,right_entry_id:right,decision})});
    duplicateViewRevision++;
    if(r.decision!=='stale'){
      duplicateConfirmedReviews.set(key,{left,right,decision:r.decision});
      const groups=duplicateData?pruneDuplicateReviewGroups(duplicateData.groups,left,right,r.decision):(r.groups||[]);
      duplicateData={...(duplicateData||{}),groups,pending:!!r.pending,stale:!!r.pending};
      requestSurfaceRender('duplicates');
    }
    if(selected&&duplicateManagerEntryId===selected){const index=duplicateBrowserIndex();if(index.has(Number(selected)))openDuplicateManager(selected);else closeDuplicateManager();}
    if(r.decision==='stale')toast('That candidate changed in the background; the review list was refreshed.');else toast(decision==='duplicate'?`Matched into entry ${r.canonical_entry_id}${r.manual_group_id?' · saved in Manual actions → Matches / groups':''}`:'Marked as different works');
    if(decision==='duplicate')loadManualGroups().catch(()=>{});
    Promise.allSettled([refreshBothMatrices(),health()]);
  }catch(e){toast('Duplicate review failed: '+e.message);try{await loadDuplicates(selected);}catch(_){}}
  finally{duplicateReviewBusy.delete(key);duplicateViewRevision++;updateDuplicateReviewButtons();scheduleDuplicateLiveRefresh();}
};
$('#closeDuplicateManager').onclick=closeDuplicateManager;
$('#refreshDuplicates').onclick=()=>{const loading=globalThis.AutoSourcarrMatchingReview?.refresh()||loadDuplicates();loading.catch(e=>toast(e.message));};
$('#runAutomaticDuplicates').onclick=async()=>{
  const button=$('#runAutomaticDuplicates');
  if(duplicateData?.automatic_matching?.paused){toast('Automatic duplicate matching stays paused until Redo/reset rebuild work finishes');return;}
  button.disabled=true;button.textContent='Queued…';
  try{
    const r=await api('/api/admin/duplicates/automatic',{method:'POST',timeoutMs:8000});
    if(r.status?.paused)toast('Automatic duplicate matching is paused until the current rebuild finishes');
    else toast(r.already_running?'Automatic duplicate matching is already running':'Automatic duplicate sweep queued');
    loadTasks().catch(()=>{});setTimeout(()=>loadDuplicates(null,{render:true,quiet:true}).catch(()=>{}),700);
  }catch(e){toast('Unable to queue automatic duplicate matching: '+e.message);}
  finally{button.disabled=false;button.textContent='Run automatic sweep';}
};
function closeAutoMergePreview(){const modal=$('#autoMergePreviewModal');if(modal)modal.hidden=true;if(typeof syncModalScrollLock==='function')syncModalScrollLock();}
function autoMergeEntryName(group,id){const e=(group.entries||[]).find(x=>Number(x.id)===Number(id));return e?.preferred_title||`Entry ${id}`;}
async function openAutoMergePreview(){
  const modal=$('#autoMergePreviewModal'),body=$('#autoMergePreviewBody'),confirmButton=$('#confirmAutoMergePreview');
  modal.hidden=false;document.body.classList.add('modalOpen');body.innerHTML='<div class="muted">Calculating current auto-merge candidates…</div>';confirmButton.disabled=true;
  try{
    const d=await api('/api/admin/maintenance/consolidate-preview',{timeoutMs:30000});
    window.autoMergePreviewData=d;
    const groups=d.groups||[];
    if(!groups.length){body.innerHTML='<div class="emptyDuplicateState"><strong>Nothing qualifies for automatic merge.</strong><span>Possible duplicates that do not meet the exact-title/summary rules remain available for manual review.</span></div>';return;}
    body.innerHTML=`<div class="notice"><strong>${Number(d.entry_count||0)} entries across ${Number(d.pair_count||0)} eligible pair(s)</strong><br>Review the works and evidence below. Nothing is merged until you press Confirm and merge.</div>`+groups.map((g,gi)=>`<section class="autoMergePreviewGroup"><h3>Merge group ${gi+1}</h3>${(g.pairs||[]).map(p=>`<div class="autoMergePreviewPair"><div><strong>${esc(autoMergeEntryName(g,p.left_id))}</strong><span> ↔ </span><strong>${esc(autoMergeEntryName(g,p.right_id))}</strong></div><div class="muted">${Number(p.exact_title_count||0)} exact title match${Number(p.exact_title_count||0)===1?'':'es'} · cleaned summary ${Math.round(Number(p.summary_similarity||0)*100)}% · ${esc(p.auto_merge_reason||'eligible')}</div>${(p.exact_titles||[]).length?`<div class="tinyText">Exact: ${(p.exact_titles||[]).map(esc).join(' · ')}</div>`:''}</div>`).join('')}</section>`).join('');
    confirmButton.disabled=false;
  }catch(e){body.innerHTML=`<div class="emptyDuplicateState"><strong>Unable to build merge preview.</strong><span>${esc(e.message)}</span></div>`;}
}
async function confirmAutoMergePreview(){
  const d=window.autoMergePreviewData||{};if(!(d.groups||[]).length)return;
  const button=$('#confirmAutoMergePreview');button.disabled=true;button.textContent='Merging…';
  try{const r=await api('/api/admin/maintenance/consolidate',{method:'POST',timeoutMs:8000});closeAutoMergePreview();toast(r.queued?'Auto-merge queued; it will yield to interactive work':'Auto-merge started');loadTasks().catch(()=>{});}
  catch(e){toast('Auto-merge failed: '+e.message);}
  finally{button.disabled=false;button.textContent='Confirm and merge';}
}
$('#autoMergeDuplicates').onclick=openAutoMergePreview;
$('#closeAutoMergePreview').onclick=closeAutoMergePreview;
$('#cancelAutoMergePreview').onclick=closeAutoMergePreview;
$('#confirmAutoMergePreview').onclick=confirmAutoMergePreview;
document.querySelectorAll('.duplicateScopeFilter').forEach(button=>button.onclick=()=>{duplicateScope=String(button.dataset.duplicateScope||'subbed')==='unsubbed'?'unsubbed':'subbed';syncDuplicateScopeButtons();queueUiPreferenceSave('duplicateScope',duplicateScope);renderDuplicates();});
$('#duplicateSearch').addEventListener('input',()=>renderDuplicates());
$('#duplicateSize').onchange=()=>renderDuplicates();

// Suwayomi-like visual manual match browser.
function matchBrowserEnabledSources(){
  return sourceRows
    .filter(v=>Number(v.available)!==0&&v.available!==false&&v.enabled_language&&v.enabled_search)
    .slice()
    .sort((a,b)=>(Number(a.priority)||1000)-(Number(b.priority)||1000)||String(a.name||a.display_name||a.source_id).localeCompare(String(b.name||b.display_name||b.source_id))||langLabel(a.lang).localeCompare(langLabel(b.lang)));
}
function matchSourceName(row){
  if(!row)return 'Source';
  return groupForSource(row.source_id)?.name||row.name||row.display_name||row.source_id;
}
function populateMatchSourceSelector(preferredId){
  const rows=matchBrowserEnabledSources();
  const select=$('#matchSource');
  select.innerHTML=rows.map(v=>`<option value="${esc(v.source_id)}" ${String(v.source_id)===String(preferredId)?'selected':''}>${esc(matchSourceName(v))} · ${esc(langLabel(v.lang))}</option>`).join('');
  return rows;
}
function updateMatchBrowserSourceHeading(){
  if(!matchBrowserState)return;
  const row=sourceRows.find(v=>String(v.source_id)===String(matchBrowserState.sourceId));
  $('#matchBrowserTitle').textContent='Edit matches · '+matchSourceName(row);
}
window.openMatchBrowser=async(entryId,sourceId)=>{
  const generation=++matchBrowserGeneration;
  const ownsOpening=()=>generation===matchBrowserGeneration&&!$('#matchBrowser').hidden;
  if(matchSearchAbort){matchSearchAbort.abort();matchSearchAbort=null;}
  if(matchEnrichAbort){matchEnrichAbort.abort();matchEnrichAbort=null;}
  matchBrowserState=null;
  // Open immediately so a slow API/source request can never look like a dead click.
  $('#matchBrowser').hidden=false; document.body.classList.add('modalOpen');
  $('#matchBrowserTitle').textContent='Edit matches';
  $('#matchBrowserSubtitle').textContent='Loading entry and enabled sources…';
  $('#matchTarget').innerHTML='<div class="muted">Loading entry details…</div>';
  $('#matchResults').innerHTML=''; $('#matchSearchStatus').textContent='';
  try{
    if(!sourceRows.length)await loadSources();
    if(!ownsOpening())return;
    const entry=await api('/api/admin/entries/'+entryId,{timeoutMs:15000});
    if(!ownsOpening())return;
    const enabled=populateMatchSourceSelector(sourceId);
    if(!enabled.length){throw new Error('No source/language is enabled for search. Enable one in Sources first.');}
    const preferred=enabled.find(v=>String(v.source_id)===String(sourceId))||enabled[0];
    $('#matchSource').value=String(preferred.source_id);
    matchBrowserState={entryId:Number(entry.canonical_id||entry.id||entryId),entry,sourceId:String(preferred.source_id),page:1,hasNext:false,results:[],dirty:false};
    updateMatchBrowserSourceHeading();
    $('#matchBrowserSubtitle').textContent='Switch between any enabled source/language, search all known names, and keep multiple chapter-contributing records attached.';
    renderMatchBrowserTarget();
    $('#matchSearch').value=''; $('#matchSearch').placeholder='Optional extra search text (all known names are always searched)';
    const current=(entry.matches||[]).find(m=>String(m.source_id)===String(preferred.source_id));
    $('#clearCurrentMatch').hidden=!current;
    await runMatchSearch(1);
  }catch(e){
    if(!ownsOpening())return;
    matchBrowserState=null;
    $('#matchTarget').innerHTML=`<div class="emptyBrowserResult"><strong>Unable to open match editor.</strong><span>${esc(e.message)}</span></div>`;
    $('#matchSearchStatus').textContent='';
  }
};
function renderMatchBrowserTarget(){
  if(!matchBrowserState)return;
  const entry=matchBrowserState.entry||{}, entryId=Number(entry.id||entry.canonical_id||matchBrowserState.entryId);
  $('#matchTarget').innerHTML=`<img src="${entryCoverUrl(entryId)}" data-entry-cover="${entryId}" class="targetCover"><div><h3>${esc(entry.preferred_title||'Untitled')}</h3><div class="muted">${(entry.titles||[]).length} known title(s) searched automatically · ${(entry.matches||[]).filter(m=>m.active!==0).length} primary source match(es) · ${(entry.source_variants||[]).filter(m=>m.active!==0).length} additional record(s)</div><p>${esc(shortText(entry.description||'No description available.',700))}</p><div class="chipList">${(entry.titles||[]).slice(0,12).map(t=>`<span class="titleChip">${esc(t.title)}</span>`).join('')}</div></div>`;
  const current=(entry.matches||[]).find(m=>String(m.source_id)===String(matchBrowserState.sourceId)&&m.active!==0);
  $('#clearCurrentMatch').hidden=!current;
}
function applyMatchBrowserEntry(entry){
  if(!matchBrowserState||!entry)return;
  matchBrowserState.entry=entry;
  matchBrowserState.entryId=Number(entry.canonical_id||entry.id||matchBrowserState.entryId);
  matchBrowserState.dirty=true;
  renderMatchBrowserTarget();
  renderMatchResults(matchBrowserState.results||[]);
}
function closeMatchBrowser(){
  ++matchBrowserGeneration;
  const dirty=!!matchBrowserState?.dirty,entryId=Number(matchBrowserState?.entryId||0);
  if(matchSearchAbort){matchSearchAbort.abort();matchSearchAbort=null;}
  if(matchEnrichAbort){matchEnrichAbort.abort();matchEnrichAbort=null;}
  $('#matchBrowser').hidden=true; if(typeof syncModalScrollLock==='function')syncModalScrollLock();else document.body.classList.remove('modalOpen'); matchBrowserState=null;
  requestAnimationFrame(flushDeferredUiRenders);
  // Manual selection already patches the local Matrix/Subbed snapshots. Only reconcile
  // this one entry later, after background source hydration has had time to finish.
  if(dirty&&entryId)scheduleEntrySnapshotSync(entryId,[1200,5000],true);
}
$('#closeMatchBrowser').onclick=closeMatchBrowser;
$('#matchSource').onchange=async()=>{if(!matchBrowserState)return;++matchBrowserGeneration;matchBrowserState.sourceId=$('#matchSource').value;matchBrowserState.page=1;updateMatchBrowserSourceHeading();const current=(matchBrowserState.entry.matches||[]).find(m=>String(m.source_id)===String(matchBrowserState.sourceId));$('#clearCurrentMatch').hidden=!current;await runMatchSearch(1);};
$('#runMatchSearch').onclick=()=>runMatchSearch(1);
$('#matchSearch').addEventListener('keydown',e=>{if(e.key==='Enter')runMatchSearch(1);});
$('#matchPrev').onclick=()=>{if(matchBrowserState&&matchBrowserState.page>1)runMatchSearch(matchBrowserState.page-1);};
$('#matchNext').onclick=()=>{if(matchBrowserState?.hasNext)runMatchSearch(matchBrowserState.page+1);};
// Capture the editor and submitted identity before any await. A new open/source or
// newer mutation owns its own response; stale work may only update its actual entry.
function beginMatchBrowserMutation(){
  if(!matchBrowserState)return null;
  const owner={state:matchBrowserState,generation:matchBrowserGeneration,
    entryId:Number(matchBrowserState.entryId),sourceId:String(matchBrowserState.sourceId),revision:++matchMutationSequence};
  matchEntryMutationRevisions.set(owner.entryId,owner.revision);
  matchEntryMutationPending.set(owner.entryId,(matchEntryMutationPending.get(owner.entryId)||0)+1);
  return owner;
}
function latestMatchBrowserMutation(owner,entry=null){
  const returnedId=Number(entry?.canonical_id||entry?.id||owner.entryId);
  return matchEntryMutationRevisions.get(owner.entryId)===owner.revision&&
    (matchEntryMutationRevisions.get(returnedId)||0)<=owner.revision;
}
function ownsMatchBrowserMutation(owner){
  return latestMatchBrowserMutation(owner)&&owner.generation===matchBrowserGeneration&&matchBrowserState===owner.state&&
    Number(matchBrowserState.entryId)===owner.entryId&&String(matchBrowserState.sourceId)===owner.sourceId&&!$('#matchBrowser').hidden;
}
function finishMatchBrowserMutation(owner){
  const pending=(matchEntryMutationPending.get(owner.entryId)||1)-1;
  if(pending)matchEntryMutationPending.set(owner.entryId,pending);else matchEntryMutationPending.delete(owner.entryId);
  if(!pending)scheduleEntrySnapshotSync(owner.entryId,[1500,5000,10000],true);
}
function applyMatchBrowserMutationEntry(owner,entry){
  if(!entry||!latestMatchBrowserMutation(owner,entry))return;
  const owns=ownsMatchBrowserMutation(owner),returnedId=Number(entry.canonical_id||entry.id||owner.entryId);
  // Responses can follow a merge alias. Carry ownership onto that returned ID so
  // an older request/rollback cannot supersede a newer choice on the survivor.
  matchEntryMutationRevisions.set(returnedId,owner.revision);
  applyEntrySnapshotEverywhere(entry,{matchBrowser:false});
  if(owns)applyMatchBrowserEntry(entry);
}
$('#clearCurrentMatch').onclick=async()=>{
  if(!matchBrowserState)return;
  if(!confirm('Clear all matches for this source/language? Existing served-history records are kept.'))return;
  const owner=beginMatchBrowserMutation();
  try{
    toast('Clearing source matches…');
    const entry=await api(`/api/admin/entries/${owner.entryId}/matches/${encodeURIComponent(owner.sourceId)}`,{method:'DELETE',timeoutMs:8000});
    applyMatchBrowserMutationEntry(owner,entry);toast('Source matches cleared');
  }catch(e){toast('Unable to clear source matches: '+e.message);}
  finally{finishMatchBrowserMutation(owner);}
};

async function runMatchSearch(page=1){
  if(!matchBrowserState)return;
  if(matchSearchAbort)matchSearchAbort.abort();
  if(matchEnrichAbort)matchEnrichAbort.abort();
  matchSearchAbort=new AbortController();
  const signal=matchSearchAbort.signal;
  const q=$('#matchSearch').value.trim();
  const requestedSource=String(matchBrowserState.sourceId),requestedEntry=Number(matchBrowserState.entryId),generation=matchBrowserGeneration;
  matchBrowserState.page=page; $('#matchSearchStatus').textContent='Searching all known names…'; $('#matchResults').innerHTML='<div class="muted">Searching aliases and ranking candidates…</div>';
  try{
    const d=await api(`/api/admin/entries/${requestedEntry}/manual-search?source_id=${encodeURIComponent(requestedSource)}&q=${encodeURIComponent(q)}&page=${page}&all_names=true`,{signal});
    if(signal.aborted||generation!==matchBrowserGeneration||!matchBrowserState||String(matchBrowserState.sourceId)!==requestedSource||Number(matchBrowserState.entryId)!==requestedEntry)return;
    const results=d.mangas||[]; matchBrowserState.results=results; matchBrowserState.hasNext=!!d.hasNextPage; $('#matchPage').textContent=`Page ${page}`; $('#matchPrev').disabled=page<=1; $('#matchNext').disabled=!matchBrowserState.hasNext;
    if(d.error){$('#matchResults').innerHTML=`<div class="emptyBrowserResult"><strong>Source search unavailable.</strong><span>${esc(d.error)}</span></div>`;$('#matchSearchStatus').textContent='Source error';return;}
    const deferred=Number(d.deferred_count||0),liveQueries=(d.live_queries||[]).length;
    if(!results.length){
      $('#matchResults').innerHTML=deferred?'<div class="emptyBrowserResult"><strong>Search deferred.</strong><span>The source host is busy or the live-search time budget was reached. Retry this search; this is not a confirmed miss.</span></div>':'<div class="emptyBrowserResult"><strong>No matches found for the attempted queries.</strong><span>Add an alternate title or enter another search phrase and try again.</span></div>';
      $('#matchSearchStatus').textContent=deferred?`${deferred} deferred · ${liveQueries} live queries attempted`:`0 results · ${liveQueries} live queries attempted`;return;
    }
    const qn=(d.queries||[]).length, generated=(d.query_meta||[]).filter(x=>x.kind!=='known'&&x.kind!=='manual').length;const warnings=(d.warnings||[]).length;const cached=Number(d.cached_results||0); $('#matchSearchStatus').textContent=`${results.length} ranked result(s) · ${qn} names for ranking · ${liveQueries} live queries attempted${cached?` · ${cached} cached`:''}${generated?` · ${generated} translated/romanized`:''}${warnings?` · partial live search (${warnings} timeout/error${warnings===1?'':'s'})`:''}${deferred?` · ${deferred} deferred; retry available`:''}`;
    renderMatchResults(results);
    enrichMatchResults(results).catch(()=>{});
  }catch(e){if(signal.aborted||generation!==matchBrowserGeneration)return;$('#matchResults').innerHTML=`<div class="emptyBrowserResult"><strong>Search failed.</strong><span>${esc(e.message)}</span></div>`;$('#matchSearchStatus').textContent='';}
  finally{if(matchSearchAbort?.signal===signal)matchSearchAbort=null;}
}
function renderMatchResults(results){
  const primary=matchBrowserState.entry.matches.find(m=>String(m.source_id)===String(matchBrowserState.sourceId));
  const additions=(matchBrowserState.entry.source_variants||[]).filter(m=>String(m.source_id)===String(matchBrowserState.sourceId));
  const additionalIds=new Set(additions.map(m=>Number(m.source_manga_id)));
  $('#matchResults').innerHTML=results.map(m=>{
    const isPrimary=primary&&Number(primary.source_manga_id)===Number(m.id), isAdditional=additionalIds.has(Number(m.id));
    const pct=Math.round(Number(m.autosourcarr_match_score||0)*100);
    const reason=[m.match_reason,m.auto_match_eligible?`auto: ${m.auto_match_reason||'eligible'}`:null,m.matched_query?`via “${m.matched_query}”`:null].filter(Boolean).join(' · ');
    const translated=m.translated_candidate_title?`<div class="translatedHint">Literal translation: ${esc(m.translated_candidate_title)}</div>`:'';
    const romanized=m.candidate_transliteration?`<div class="translatedHint">Romanized: ${esc(m.candidate_transliteration)}</div>`:'';
    const separate=!!m.manual_separate;
    const actions=separate
      ? `<span class="tag bad">SEPARATE ENTRY</span><button class="miniButton" onclick="allowManualCandidate(${Number(m.id)})">Allow match</button>`
      : `${m.auto_match_eligible&&!isPrimary&&!isAdditional?'<span class="tag autoEligibleTag">AUTO ELIGIBLE</span>':''}${isPrimary?'<span class="tag manual">PRIMARY</span>':isAdditional?'<span class="tag manual">ADDITIONAL</span>':''}${!isPrimary?`<button onclick="useManualMatch(${Number(m.id)},'replace')">Use as primary</button>`:''}${!isPrimary&&!isAdditional?`<button onclick="useManualMatch(${Number(m.id)},'add')">Add match</button>`:''}<button class="miniButton danger ghost" onclick="separateManualCandidate(${Number(m.id)})">Separate entry</button>`;
    return `<article class="mangaCard ${isPrimary||isAdditional?'currentMatch':''} ${separate?'separateCandidate':''}" id="candidate-${m.id}"><div class="cardCoverWrap"><img loading="lazy" fetchpriority="low" decoding="async" src="${sourceCoverUrl(m.id)}" data-source-cover="${m.id}" class="browserCover"><span class="langBadge">${esc(langLabel(sourceRows.find(s=>String(s.source_id)===String(matchBrowserState.sourceId))?.lang||''))}</span><span class="candidateScore">${pct}%</span></div><div class="mangaCardBody"><h3>${esc(m.title||'Untitled')}</h3><div class="candidateMeta">${esc([m.author,m.artist].filter(Boolean).join(' · '))}</div><div class="matchReason">${esc(reason)}</div>${translated}${romanized}<p class="candidateDescription">${esc(shortText(m.description||'Loading description…',420))}</p><div class="cardActions">${actions}</div></div></article>`;
  }).join('');
}
async function enrichMatchResults(results){
  if(matchEnrichAbort)matchEnrichAbort.abort();
  const controller=new AbortController();matchEnrichAbort=controller;
  const sourceId=String(matchBrowserState?.sourceId||'');
  // Search results already carry the normal manga fields. Only hydrate sparse cards;
  // fetching details for every one of 60-100 ranked results was needless source-host
  // pressure and could make unrelated UI actions feel stalled.
  const pending=(results||[]).filter(m=>!m.description||!m.thumbnailUrl||(!m.author&&!m.artist)).slice(0,24);
  let idx=0; const workers=Array.from({length:Math.min(2,pending.length)},async()=>{
    while(idx<pending.length&&!controller.signal.aborted){
      const m=pending[idx++];
      try{
        const d=await api('/api/admin/source-manga/'+m.id,{signal:controller.signal});
        if(controller.signal.aborted||String(matchBrowserState?.sourceId||'')!==sourceId)return;
        Object.assign(m,d||{});const card=$('#candidate-'+m.id);if(!card)continue;
        const desc=card.querySelector('.candidateDescription');const meta=card.querySelector('.candidateMeta');
        if(desc)desc.textContent=shortText(d.description||m.description||'No description available.',700);
        if(meta)meta.textContent=[d.author,d.artist].filter(Boolean).join(' · ');
        const h=card.querySelector('h3');if(h&&d.title)h.textContent=d.title;
      }catch(e){if(controller.signal.aborted)return;/* keep usable search result */}
    }
  });
  await Promise.allSettled(workers);
  if(matchEnrichAbort===controller)matchEnrichAbort=null;
}
function manualCandidateRequest(m){
  if(!m)return null;
  return {id:Number(m.id),title:m.title||'',url:m.url||'',thumbnailUrl:m.thumbnailUrl||m.thumbnail_url||'',description:m.description||'',author:m.author||'',artist:m.artist||'',genre:m.genre||[],status:m.status||0,initialized:m.initialized!==false,realUrl:m.realUrl||m.real_url||''};
}
function optimisticManualMatchEntry(entry,sourceId,candidate,mode){
  const out=typeof structuredClone==='function'?structuredClone(entry):JSON.parse(JSON.stringify(entry));
  out.matches=out.matches||[];out.source_variants=out.source_variants||[];
  const row=sourceRows.find(x=>String(x.source_id)===String(sourceId));
  const record={source_id:String(sourceId),source_name:matchSourceName(row),source_manga_id:Number(candidate.id),source_manga_url:candidate.url||'',source_title:candidate.title||'Untitled',thumbnail_url:candidate.thumbnailUrl||candidate.thumbnail_url||'',description:candidate.description||'',author:candidate.author||'',artist:candidate.artist||'',genre:Array.isArray(candidate.genre)?candidate.genre.join(', '):(candidate.genre||''),status:candidate.status||0,confidence:1,match_type:'manual',locked:1,active:1};
  const primary=out.matches.find(x=>String(x.source_id)===String(sourceId)&&x.active!==0);
  if(mode==='add'&&primary){
    out.source_variants=out.source_variants.filter(x=>!(String(x.source_id)===String(sourceId)&&Number(x.source_manga_id)===Number(candidate.id)));
    out.source_variants.push({...record,is_variant:true});
  }else{
    const had=!!primary;
    out.matches=out.matches.filter(x=>String(x.source_id)!==String(sourceId));out.matches.push(record);
    out.source_variants=out.source_variants.filter(x=>String(x.source_id)!==String(sourceId));
    if(!had)out.match_count=Number(out.match_count||0)+1;
  }
  return out;
}
window.separateManualCandidate=async(sourceMangaId)=>{
  if(!matchBrowserState)return;
  const candidate=(matchBrowserState.results||[]).find(x=>Number(x.id)===Number(sourceMangaId));
  if(!candidate)return;
  if(!confirm(`Keep “${candidate.title||'this source entry'}” as a separate work from ${matchBrowserState.entry?.preferred_title||'the selected entry'}? AutoSourcarr will not attach this exact source record here automatically.`))return;
  const owner=beginMatchBrowserMutation(),submitted=manualCandidateRequest(candidate);
  const card=$('#candidate-'+sourceMangaId);if(card)card.classList.add('savingMatch');
  try{
    const result=await api(`/api/admin/entries/${owner.entryId}/separate-source`,{method:'POST',timeoutMs:10000,body:JSON.stringify({source_id:owner.sourceId,source_manga_id:Number(sourceMangaId),candidate:submitted,defer_hydration:true})});
    const owns=ownsMatchBrowserMutation(owner);
    if(owns){candidate.manual_separate=true;owner.state.dirty=true;}
    if(result.entry)applyMatchBrowserMutationEntry(owner,result.entry);
    else if(owns)renderMatchResults(owner.state.results||[]);
    toast(`Kept as separate entry${result.separate_entry_id?` ${result.separate_entry_id}`:''}`);
  }catch(e){toast('Unable to mark separate entry: '+e.message);}
  finally{finishMatchBrowserMutation(owner);if(card)card.classList.remove('savingMatch');}
};
window.allowManualCandidate=async(sourceMangaId)=>{
  if(!matchBrowserState)return;
  const owner=beginMatchBrowserMutation(),candidate=(owner.state.results||[]).find(x=>Number(x.id)===Number(sourceMangaId));
  try{
    await api(`/api/admin/entries/${owner.entryId}/separate-source/${encodeURIComponent(owner.sourceId)}/${Number(sourceMangaId)}`,{method:'DELETE',timeoutMs:8000});
    if(ownsMatchBrowserMutation(owner)){
      if(candidate)candidate.manual_separate=false;
      owner.state.dirty=true;renderMatchResults(owner.state.results||[]);
    }
    toast('Source record can be matched again');
  }catch(e){toast('Unable to clear separate-entry decision: '+e.message);}
  finally{finishMatchBrowserMutation(owner);}
};

window.useManualMatch=async(sourceMangaId,mode='replace')=>{
  if(!matchBrowserState)return;
  const card=$('#candidate-'+sourceMangaId),candidate=(matchBrowserState.results||[]).find(x=>Number(x.id)===Number(sourceMangaId));
  if(!candidate){toast('Selected source record is no longer in the result list');return;}
  const owner=beginMatchBrowserMutation(),before=owner.state.entry,submitted=manualCandidateRequest(candidate);
  if(card)card.classList.add('savingMatch');
  const optimistic=optimisticManualMatchEntry(before,owner.sourceId,candidate,mode);
  applyMatchBrowserMutationEntry(owner,optimistic);
  toast(mode==='add'?'Source record added; fetching chapters in background…':'Primary match saved; fetching chapters in background…');
  try{
    const entry=await api(`/api/admin/entries/${owner.entryId}/manual-match`,{method:'POST',timeoutMs:8000,body:JSON.stringify({source_id:owner.sourceId,source_manga_id:Number(sourceMangaId),mode,candidate:submitted,defer_hydration:true})});
    if(ownsMatchBrowserMutation(owner))candidate.manual_separate=false;
    applyMatchBrowserMutationEntry(owner,entry);
    if(entry.background_hydration)scheduleEntrySnapshotSync(entry.canonical_id||entry.id,[1500,5000,10000],true);
    const forced=Array.isArray(entry.forced_from_entry_ids)?entry.forced_from_entry_ids.length:0;
    toast(forced?`Match moved from ${forced} other entr${forced===1?'y':'ies'} and locked`:(mode==='add'?'Additional match saved':'Primary match locked'));
  }catch(e){
    applyMatchBrowserMutationEntry(owner,before);toast('Unable to save match: '+e.message);
  }finally{finishMatchBrowserMutation(owner);if(card)card.classList.remove('savingMatch');}
};

let duplicateSplitData=[];
function closeUnmatchDuplicates(){ $('#unmatchDuplicatesModal').hidden=true; if(typeof syncModalScrollLock==='function')syncModalScrollLock();else document.body.classList.remove('modalOpen'); }
function renderUnmatchDuplicates(){
  const q=($('#unmatchDuplicateSearch')?.value||'').trim().toLowerCase();
  const items=(duplicateSplitData||[]).filter(x=>!q||String(x.entry_id)===q||String(x.preferred_title||'').toLowerCase().includes(q)||(x.records||[]).some(r=>String(r.title||'').toLowerCase().includes(q)||String(r.source_name||'').toLowerCase().includes(q)));
  $('#unmatchDuplicateList').innerHTML=items.length?items.map(x=>`<section class="duplicateSplitEntry" data-split-entry="${x.entry_id}"><div class="duplicateSplitEntryHead"><img src="${esc(x.cover_url)}" class="duplicateCover"><div><span class="entryIdPill">ID ${x.entry_id}</span><h3>${esc(x.preferred_title)}</h3><div class="muted">${x.merge_count} recorded merge${x.merge_count===1?'':'s'} · select the record(s) that belong to the other work</div></div></div><div class="duplicateSplitRecords">${(x.records||[]).map((r,i)=>`<label class="duplicateSplitRecord"><input type="checkbox" data-split-source="${esc(r.source_id)}" data-split-manga="${Number(r.source_manga_id)}"><span><strong>${esc(r.title||`Manga ${r.source_manga_id}`)}</strong><small>${esc(r.source_name||r.source_id)} · ${r.kind==='additional'?'additional record':'primary record'} · source manga ${r.source_manga_id}</small></span></label>`).join('')}</div><div class="duplicateSplitActions"><button class="dangerAction" onclick="splitDuplicateRecords(${x.entry_id})">Split selected into a new entry</button></div></section>`).join(''):'<div class="emptyDuplicateState"><strong>No recorded merged entries found.</strong><span>Only entries with merge history appear here.</span></div>';
}
async function loadUnmatchDuplicates(){ $('#unmatchDuplicateList').innerHTML='<div class="muted">Loading merged entries…</div>';const d=await api('/api/admin/maintenance/unmatch-duplicates');duplicateSplitData=d.items||[];renderUnmatchDuplicates(); }
window.openUnmatchDuplicatesForEntry=async(entryId)=>{
  $('#unmatchDuplicatesModal').hidden=false;document.body.classList.add('modalOpen');
  $('#unmatchDuplicateSearch').value='';
  try{
    await loadUnmatchDuplicates();
    const item=duplicateSplitData.find(x=>Number(x.entry_id)===Number(entryId));
    if(!item){toast('This entry has no recorded duplicate-merge history. Use Redo automation from Match Matrix/Subbed to rebuild automatic attachments.');return;}
    duplicateSplitData=[item];renderUnmatchDuplicates();
  }catch(e){toast('Unable to load duplicate records: '+e.message);}
};
window.splitDuplicateRecords=async(entryId)=>{const box=document.querySelector(`[data-split-entry="${entryId}"]`);const records=[...box.querySelectorAll('input[type=checkbox]:checked')].map(i=>({source_id:i.dataset.splitSource,source_manga_id:Number(i.dataset.splitManga)}));if(!records.length){toast('Select at least one source record to split');return;}if(!confirm(`Split ${records.length} selected source record(s) out of this entry? Associated source-provided titles will be removed from the original entry.`))return;try{const r=await api(`/api/admin/entries/${entryId}/unmatch-duplicates`,{method:'POST',body:JSON.stringify({records})});toast(`Created separate entry ${r.new_entry_id}`);await loadUnmatchDuplicates();await Promise.allSettled([refreshBothMatrices(),loadDuplicates(),health()]);}catch(e){toast('Unable to split duplicate: '+e.message);}};
$('#unmatchDuplicates').onclick=async()=>{$('#unmatchDuplicatesModal').hidden=false;document.body.classList.add('modalOpen');await loadUnmatchDuplicates().catch(e=>toast(e.message));};
$('#closeUnmatchDuplicates').onclick=closeUnmatchDuplicates;
$('#reloadUnmatchDuplicates').onclick=()=>loadUnmatchDuplicates().catch(e=>toast(e.message));
$('#unmatchDuplicateSearch').addEventListener('input',renderUnmatchDuplicates);
$('#resetCovers').onclick=async()=>{if(!confirm('Reset every cached cover? Covers will be downloaded again from matched sources as they are viewed.'))return;const b=$('#resetCovers');b.disabled=true;try{const r=await api('/api/admin/maintenance/reset-covers',{method:'POST'});renewCoverEpoch();browseState.renderNonce++;await Promise.allSettled([loadBrowse(browseState.page,false),refreshBothMatrices()]);toast(`Cover cache reset · ${r.removed_files||0} cached file(s) removed`);}catch(e){toast('Cover reset failed: '+e.message);}finally{b.disabled=false;}};
$('#resetDatabase').onclick=async()=>{const first=confirm('Reset the AutoSourcarr database? This deletes rebuildable catalog matches, duplicate decisions, browse/translation caches, cached covers and chapter-origin data. Your env settings, main-Suwayomi library items, persistent manual matches, and manual entry groups are kept. Before deletion AutoSourcarr records the old source IDs only as UNTRUSTED rebuild hints. Subbed entries are restored first and those exact source records are re-fetched and re-checked with the current matcher; nothing is rebound merely because it matched before. The remaining previous catalog is then replayed by exact source ID under the current rules, avoiding most expensive source-wide searches, while low-priority source browsing continues afterward. The reset is aborted if the main-Suwayomi subscription snapshot cannot be read.');if(!first)return;const typed=prompt('Type RESET DATABASE to continue');if(typed!=='RESET DATABASE'){toast('Database reset cancelled');return;}const b=$('#resetDatabase');b.disabled=true;try{const r=await api('/api/admin/maintenance/reset-database',{method:'POST'});duplicateData=null;matrixData=null;subbedData=null;await Promise.allSettled([loadSources(),loadBrowse(1),loadMatrix(),health(),loadTasks(),loadManualGroups()]);const n=Number(r.subbed_restored||0),m=Number(r.manual_matches?.restored||0),h=Number(r.rebuild_hint_records||0);toast(`Database reset · ${n} subscribed entr${n===1?'y':'ies'} restored · ${m} persistent manual match${m===1?'':'es'} restored · ${h.toLocaleString()} old source IDs queued for fresh validation${r.subbed_rebrowse_queued?' · Subbed rebuild running':''}`);}catch(e){toast('Database reset failed: '+e.message);}finally{b.disabled=false;}};
if($('#fullReset'))$('#fullReset').onclick=async()=>{
  const first=confirm('Full reset AutoSourcarr data? This clears the catalog, matches, duplicate decisions, manual matches/groups, manual titles/separate-entry decisions, import history/decisions, chapter rejections, caches and chapter-origin history. Configuration is kept: persistent env/API/signing settings, UI preferences, source groups/priorities/language/search settings, database location, and your main-Suwayomi library are not reset. Subbed entries are recreated from the main library and searched again from scratch.');if(!first)return;
  const typed=prompt('Type FULL RESET to continue');if(typed!=='FULL RESET'){toast('Full reset cancelled');return;}
  const b=$('#fullReset');b.disabled=true;
  try{const r=await api('/api/admin/maintenance/full-reset',{method:'POST',timeoutMs:30000});duplicateData=null;matrixData=null;subbedData=null;await Promise.allSettled([loadSources(),loadBrowse(1),loadMatrix(),loadSubbed(),health(),loadTasks(),loadManualGroups()]);const n=Number(r.subbed_restored||0);toast(`Full reset complete · configuration kept · ${n} subscribed entr${n===1?'y':'ies'} restored${r.subbed_rebrowse_queued?' · fresh Subbed search running':''}`);}
  catch(e){toast('Full reset failed: '+e.message);}
  finally{b.disabled=false;}
};


if($('#moveDatabaseStorage'))$('#moveDatabaseStorage').onclick=()=>moveDatabaseStorage();
$('#refreshStatus').onclick=()=>{health().catch(e=>toast(e.message));loadApkDownload();loadSourceEngine();};
$('#refreshFeeds').onclick=async()=>{const b=$('#refreshFeeds');b.disabled=true;try{const r=await api('/api/admin/maintenance/refresh-feeds',{method:'POST'});toast(`Browse refresh scheduled (${r.scheduled||0} feed jobs)`);setTimeout(()=>health().catch(()=>{}),1000);}catch(e){toast('Unable to schedule feed refresh: '+e.message);}finally{b.disabled=false;}};
$('#scanAdditionalRecords').onclick=async()=>{const b=$('#scanAdditionalRecords');b.disabled=true;try{const r=await api('/api/admin/maintenance/discover-additional?limit=5000',{method:'POST'});toast(r.already_running?'Additional-record scan is already running':`Background scan queued for ${r.entries||0} entries`);loadTasks().catch(()=>{});setTimeout(()=>health().catch(()=>{}),1000);}catch(e){toast('Unable to start additional-record scan: '+e.message);}finally{b.disabled=false;}};

document.querySelectorAll('.themeChoice').forEach(b=>b.onclick=()=>{const theme=b.dataset.theme||'blue';applyTheme(theme,readLocalUiPreference('customColor')||DEFAULT_CUSTOM_COLOR);queueUiPreferenceSave('theme',theme);});
function commitCustomThemeColor(hex){const normalized=normalizeHexColor(hex);if(!normalized){toast('Enter a valid HEX color such as #FF2D78');return false;}applyCustomPalette(normalized);applyTheme('custom',normalized);queueUiPreferenceSave('customColor',normalized);queueUiPreferenceSave('theme','custom');return true;}
if($('#applyCustomTheme'))$('#applyCustomTheme').onclick=()=>commitCustomThemeColor($('#customThemeHex')?.value||'');
if($('#customThemeHex'))$('#customThemeHex').addEventListener('change',()=>commitCustomThemeColor($('#customThemeHex').value));
for(const id of ['customThemeR','customThemeG','customThemeB'])if($(`#${id}`))$(`#${id}`).addEventListener('change',()=>{const hex=rgbToHex($('#customThemeR').value,$('#customThemeG').value,$('#customThemeB').value);syncCustomColorInputs(hex);commitCustomThemeColor(hex);});
document.querySelectorAll('.densityChoice').forEach(b=>b.onclick=()=>{const density=b.dataset.density||'comfortable';applyDensity(density);queueUiPreferenceSave('density',density);});
document.querySelectorAll('.settingsSubtab').forEach(b=>b.onclick=()=>setSettingsSubtab(b.dataset.settingsSubtab||'customization'));
$('#reloadSettings').onclick=()=>{if(settingsState.dirty&&!confirm('Discard unsaved settings changes?'))return;loadEnvSettings({force:true}).catch(e=>toast(e.message));};
$('#saveSettings').onclick=saveEnvSettings;
$('#saveSettingsBottom').onclick=saveEnvSettings;
if($('#refreshManualActions'))$('#refreshManualActions').onclick=()=>loadManualActions().catch(e=>toast(e.message));
document.querySelectorAll('.manualActionSubtab').forEach(b=>b.onclick=()=>setManualActionsSubtab(b.dataset.manualActionSubtab));
if($('#resetManualGroups'))$('#resetManualGroups').onclick=async()=>{
  if(!confirm('Reset all persistent manual entry groups? Individual manual matches will remain persistent, but their grouping will be forgotten.'))return;
  const typed=prompt('Type RESET GROUPS to continue');if(typed!=='RESET GROUPS'){toast('Group reset cancelled');return;}
  const b=$('#resetManualGroups');b.disabled=true;try{const r=await api('/api/admin/maintenance/reset-manual-groups',{method:'POST'});toast(`Reset ${Number(r.groups_removed||0)} manual group${Number(r.groups_removed||0)===1?'':'s'}`);await loadManualGroups();}catch(e){toast('Manual group reset failed: '+e.message);}finally{b.disabled=false;}
};
if($('#resetManualMatches'))$('#resetManualMatches').onclick=async()=>{
  if(!confirm('Reset every persistent manual match? Current matches stay in the catalog but become ordinary unlocked automatic matches. They will NOT survive a later database reset unless manually matched again.'))return;
  const typed=prompt('Type RESET MANUAL MATCHES to continue');if(typed!=='RESET MANUAL MATCHES'){toast('Manual-match reset cancelled');return;}
  const b=$('#resetManualMatches');b.disabled=true;try{const r=await api('/api/admin/maintenance/reset-manual-matches',{method:'POST'});toast(`Reset ${Number(r.manual_matches_removed||0)} persistent manual match${Number(r.manual_matches_removed||0)===1?'':'es'}`);await Promise.allSettled([loadManualGroups(),refreshBothMatrices()]);}catch(e){toast('Manual-match reset failed: '+e.message);}finally{b.disabled=false;}
};
if($('#refreshTasks'))$('#refreshTasks').onclick=()=>loadTasks().catch(e=>toast(e.message));
if($('#clearTaskHistory'))$('#clearTaskHistory').onclick=async()=>{
  if(!confirm('Clear completed, failed and cancelled task history? Running tasks and repairs awaiting review will be kept. Repair evidence stays on disk; finished repair rows are hidden until this app restarts.'))return;
  const b=$('#clearTaskHistory');b.disabled=true;
  try{const r=await apiV1().clearTaskHistory();await loadTasks();const n=Number(r.cleared||0);toast(`Cleared ${n} task history entr${n===1?'y':'ies'}`);}
  catch(e){toast('Unable to clear task history: '+e.message);}
  finally{b.disabled=false;}
};
if($('#refreshImports'))$('#refreshImports').onclick=()=>loadImports().catch(e=>toast(e.message));
if($('#clearImportHistory'))$('#clearImportHistory').onclick=async()=>{
  if(!confirm('Clear finished import history? Active imports will be kept. This removes only AutoSourcarr import audit/history rows; it does not remove anything from the Suwayomi library or reset manual matches.'))return;
  const b=$('#clearImportHistory');b.disabled=true;
  try{const r=await api('/api/admin/imports/clear-history',{method:'POST',body:'{}'});importsState.revision++;importsState.offset=0;importsState.loadedKey='';importsState.runId='';importsState.runs=[];importsState.items=[];await loadImports();toast(`Cleared ${Number(r.runs_cleared||0)} import run${Number(r.runs_cleared||0)===1?'':'s'} (${Number(r.items_cleared||0)} item${Number(r.items_cleared||0)===1?'':'s'})`);}
  catch(e){toast('Unable to clear import history: '+e.message);}
  finally{b.disabled=false;}
};
if($('#resetImportManualAutoresolve'))$('#resetImportManualAutoresolve').onclick=async()=>{
  if(!confirm('Reset all learned manual import autoresolve mappings? Future backup imports will no longer automatically reuse your previous manual import Match decisions. Ordinary manual source matches/groups are kept.'))return;
  const b=$('#resetImportManualAutoresolve');b.disabled=true;
  try{
    const r=await api('/api/admin/imports/reset-manual-autoresolve',{method:'POST',body:'{}'});
    toast(`Reset ${Number(r.autoresolve_mappings_removed||0)} manual import autoresolve mapping${Number(r.autoresolve_mappings_removed||0)===1?'':'s'}`);
  }catch(e){toast('Unable to reset import manual autoresolve: '+e.message);}
  finally{b.disabled=false;}
};
if($('#resetImportRejections'))$('#resetImportRejections').onclick=async()=>{
  if(!confirm('Reset all semi-permanent rejected import matches? Previously rejected candidates may become suggestions again. Import history and Suwayomi library entries are not deleted.'))return;
  const b=$('#resetImportRejections');b.disabled=true;
  try{
    const r=await api('/api/admin/imports/reset-rejections',{method:'POST',body:'{}'});
    await loadImports();
    toast(`Reset ${Number(r.rejections_cleared||0)} rejected import match${Number(r.rejections_cleared||0)===1?'':'es'}`);
  }catch(e){toast('Unable to reset rejected import matches: '+e.message);}
  finally{b.disabled=false;}
};
if($('#importPrev'))$('#importPrev').onclick=()=>{importsState.offset=Math.max(0,importsState.offset-50);loadImportItems().catch(e=>toast(e.message));};
if($('#importNext'))$('#importNext').onclick=()=>{importsState.offset+=50;loadImportItems().catch(e=>toast(e.message));};
if($('#importRunSelect'))$('#importRunSelect').onchange=e=>{importsState.runId=String(e.target.value||'');importsState.offset=0;loadImportItems().catch(x=>toast(x.message));};
document.querySelectorAll('.importStateFilter').forEach(b=>b.onclick=()=>{importsState.filter=String(b.dataset.state||'all');importsState.offset=0;document.querySelectorAll('.importStateFilter').forEach(x=>x.classList.toggle('active',x===b));loadImportItems().catch(e=>toast(e.message));});
document.querySelectorAll('.importReviewFilter').forEach(b=>b.onclick=()=>{importsState.review=String(b.dataset.review||'all');importsState.offset=0;document.querySelectorAll('.importReviewFilter').forEach(x=>x.classList.toggle('active',x===b));loadImportItems().catch(e=>toast(e.message));});
function newBackupImportId(){
  return Array.from(crypto.getRandomValues(new Uint8Array(16)),value=>value.toString(16).padStart(2,'0')).join('');
}
async function confirmBackupImport(result,input,status){
  backupUploadPending=null;
  if(input)input.value='';
  if(status)status.textContent=`Import accepted${result.task_id?` as task ${result.task_id}`:` as ${result.import_id}`}. Entries already present in the main Suwayomi library will be skipped; the temporary backup is deleted when processing finishes.`;
  importsState.runId=String(result.import_id||'');
  toast('Backup import queued for the main Suwayomi library');
  switchTab('imports');
  await loadTasks().catch(()=>{});
}
async function uploadMihonBackup(){
  const input=$('#backupImportFile'),file=input?.files?.[0];
  if(!file){toast('Choose a Mihon/Tachiyomi backup first');return;}
  const button=$('#importBackup'),status=$('#backupImportStatus');
  if(button?.disabled)return;
  if(button)button.disabled=true;
  if(status)status.textContent=`Uploading and decoding ${file.name}…`;
  let requestStarted=false,knownRejected=false,importId='';
  try{
    // New explicit UTF-8 header leaves the legacy X-Filename contract unchanged.
    const encodedName=encodeURIComponent(file.name);
    if(!backupUploadPending||backupUploadPending.file!==file)backupUploadPending={file,importId:newBackupImportId()};
    importId=backupUploadPending.importId;
    const headers={'Content-Type':'application/octet-stream','X-Filename-UTF8':encodedName,'X-Import-ID':importId};
    if(token)headers['X-Admin-Token']=token;
    const controller=new AbortController(),timer=setTimeout(()=>controller.abort(),120000);
    let result;
    try{
      requestStarted=true;
      const response=await fetch('/api/admin/import/mihon-backup',{method:'POST',headers,body:file,signal:controller.signal});
      if(response.status===401){knownRejected=true;throw new Error('Admin token required. Configure/open AutoSourcarr with the admin token and retry.');}
      if(!response.ok){
        knownRejected=response.status>=400&&response.status<500&&response.status!==409;
        const text=await response.text();throw new Error(text||`HTTP ${response.status}`);
      }
      result=await response.json();
      if(result.queued!==true||String(result.import_id)!==importId)throw new Error('The upload acknowledgment did not identify the accepted import');
    }finally{clearTimeout(timer);}
    await confirmBackupImport(result,input,status);
  }catch(e){
    const message=e?.name==='AbortError'?'Upload acknowledgment timed out':String(e?.message||e);
    if(requestStarted&&!knownRejected&&importId){
      // An abort or lost body does not prove the server rejected an upload. Query
      // its stable identity once; never automatically replay the POST.
      if(status)status.textContent='Checking whether the server accepted this import…';
      try{
        const receipt=await api(`/api/admin/imports/${encodeURIComponent(importId)}/receipt`,{timeoutMs:10000,suppressClientError:true});
        if(receipt.queued===true&&String(receipt.import_id)===importId){await confirmBackupImport(receipt,input,status);return;}
      }catch(_error){}
      if(status)status.textContent=`Upload acknowledgment unavailable: ${message}. Check Tasks and Imports for ${importId} before retrying. Retrying this selected file uses the same import identity.`;
      toast('Backup upload acknowledgment unavailable; check the import before retrying');
    }else{
      if(status)status.textContent=`Import could not be queued: ${message}`;
      toast('Backup import failed: '+message);
    }
  }finally{if(button)button.disabled=false;}
}

if($('#importBackup'))$('#importBackup').onclick=uploadMihonBackup;
if($('#health'))$('#health').onclick=openErrors;
if($('#closeErrors'))$('#closeErrors').onclick=closeErrors;
if($('#refreshErrors'))$('#refreshErrors').onclick=()=>loadErrors().catch(e=>toast('Unable to refresh diagnostics: '+e.message));
if($('#clearResolvedErrors'))$('#clearResolvedErrors').onclick=async()=>{
  const b=$('#clearResolvedErrors');if(b)b.disabled=true;
  try{
    await apiV1({timeoutMs:8000,suppressClientError:true}).clearErrors({resolved_only:true});
    clientErrors=clientErrors.filter(item=>!item.resolved);
    await loadErrors();
    toast('Resolved error history cleared');
  }catch(e){toast('Unable to clear resolved errors: '+e.message);}
  finally{if(b)b.disabled=false;}
};
function browsePageSizeNeedsReload(){
  const selected=Number.parseInt($('#browsePageSize')?.value||'',10);
  const loaded=Number(browseState.loadedPageSize||0);
  return selected>0&&loaded>0&&Math.min(500,selected)!==loaded;
}
async function bootstrapAutoSourcarr(){
  // Apply this browser's preferences immediately; remote settings and source
  // inventory must not hold cached Browse or the task monitor behind them.
  applyUiPreferences({});
  const setupCheck=globalThis.AutoSourcarrSetup?.initialize();
  if(setupCheck)await setupCheck;
  const restored=globalThis.AutoSourcarrNavigation?.initialize()||{tab:'browse'};
  switchTab(restored.tab,{load:false});
  const initialGeneration=navigationGeneration;
  const preferences=loadUiPreferences().catch(e=>{
    uiPreferencesState.loaded=true;uiPreferencesState.user='Local user';uiPreferencesState.authelia=false;
    updateUiPreferenceOwner();
    rememberClientError('Unable to load server user preferences',{component:'User preferences',path:'/api/admin/ui-preferences',method:'GET',severity:'warning',detail:String(e?.message||e)});
  });
  startTaskPolling();
  runLiveEvents();
  health();
  scheduleMetadataLivePoll();
  scheduleDuplicateHeartbeat();
  // Browse first; heavy catalogue views load only when visited.
  const sources=loadSources().catch(e=>toast('Unable to load source filters: '+e.message));
  const browse=(async()=>{if(restored.needsSourceInventory)await sources;if(navigationGeneration!==initialGeneration)return;return loadActiveTab(restored.tab);})().catch(e=>toast(e.message));
  const reconcile=Promise.allSettled([preferences,browse]).then(()=>{
    if(!activeSurface('browse')||!browseState.items.length||!browsePageSizeNeedsReload())return;
    // Server preferences may arrive after a cached page. Reconcile pagination
    // using current filters; a slow source-list request does not delay this.
    if(browseState.loading){browseState.pendingPreferenceReload=true;return;}
    return loadBrowse(1);
  }).catch(e=>toast(e.message));
  await Promise.allSettled([reconcile,sources]);
}
if(typeof initializeWorkbench==='function')initializeWorkbench();
if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',bootstrapAutoSourcarr,{once:true});else queueMicrotask(bootstrapAutoSourcarr);


// Preview before confirmation. A changed decision revision requires a new preview.
if($('#resetAllManualActions'))$('#resetAllManualActions').onclick=async()=>{
  const button=$('#resetAllManualActions');button.disabled=true;
  try{
    const preview=await api('/api/admin/maintenance/reset-all-manual-actions',{cache:'no-store',suppressClientError:true,timeoutMs:20000});
    if(typeof globalThis.AutoSourcarrConfirm?.request!=='function')throw new Error('Confirmation controls did not load. Refresh this page before trying again.');
    const typed=await globalThis.AutoSourcarrConfirm.request({title:'Reset all manual actions',rows:preview.counts.map(row=>`${row.label}: ${row.count}`),details:[preview.retained,preview.effect],phrase:'RESET ALL MANUAL ACTIONS',acceptLabel:'Reset manual actions'});
    if(typed!=='RESET ALL MANUAL ACTIONS')return;
    await api('/api/admin/maintenance/reset-all-manual-actions',{method:'POST',timeoutMs:120000,noMutationRefresh:true,suppressClientError:true,body:JSON.stringify({revision:preview.revision,confirmation:typed})});
    toast('Manual decisions reset. Library and history retained.');
    // Reopen the remembered tab with fresh snapshots; old in-flight responses
    // belong to the previous document and cannot repaint reset decisions.
    catalogViewRevision++;location.reload();
  }catch(e){toast('Manual reset: '+e.message+' Refresh the preview to check the current state.');}
  finally{button.disabled=false;button.focus?.();}
};
