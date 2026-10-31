/* Workspace presentation. Existing app.js owns requests, decision safety and data. */
const workbenchState={duplicateLeft:null,duplicateRight:null,importKey:'',importId:null,importDrafts:new Map(),importCandidates:new Map(),taskLane:'all',repairLane:'all',libraryScope:'all',diagnosticFilter:'active',diagnosticFocus:null};
let repositoryState={loaded:false,loading:false,revision:'',repositories:[],preview:null};
function updatePreferenceSaveState(){
  const el=document.querySelector('#preferenceSaveState');if(!el)return;
  const s=uiPreferencesState;
  const retry=document.querySelector('#retryPreferenceSave');if(retry)retry.hidden=!s.saveFailures;
  el.textContent=s.saveFailures?'Server save failed · changes are kept on this browser':s.saving||Object.keys(s.pending||{}).length?'Saving your appearance…':s.loaded?'Saved for '+(s.user||'local profile'):'Loading saved appearance…';
}
// Color placement and accent are independent, persisted user preferences.
function normalizeThemeStyle(value){return ['neutral','tonal','dual','paper','graphite','aurora'].includes(value)?value:'neutral';}
function colorLuminance(c){const f=v=>{v/=255;return v<=.04045?v/12.92:((v+.055)/1.055)**2.4;};return .2126*f(c.r)+.7152*f(c.g)+.0722*f(c.b);}
function colorContrast(a,b){const x=colorLuminance(a),y=colorLuminance(b);return (Math.max(x,y)+.05)/(Math.min(x,y)+.05);}
function readableColor(color,surfaces,light=false){
  let safe={...color};const target=light?{r:0,g:0,b:0}:{r:255,g:255,b:255};
  for(let i=0;i<100&&surfaces.some(bg=>colorContrast(safe,bg)<4.5);i++)safe=mixRgb(safe,target,.09);
  return safe;
}
function normalizeAccentStrength(value){const n=Number(value);return Number.isFinite(n)&&n>=10&&n<=50?n:25;}
function normalizeCardFill(value){return value==='neutral'?'neutral':'accent';}
function appearancePalette(style,accent,secondary,strength=25,cardFill='accent',tertiary=secondary){
  const intensity=normalizeAccentStrength(strength)/100;
  style=normalizeThemeStyle(style);const light=style==='paper';
  const p=light?{bg:'#F2F0EA',panel:'#FFFEFA',panel2:'#E8E5DC',chrome:'#EAE7DF',control:'#E3E0D7','control-hover':'#D8D4CA',line:'#CAC5B9',text:'#242C30',muted:'#565E60',good:'#216441',warn:'#775208',bad:'#A12F3D'}
    :{bg:'#101217',panel:'#171A20',panel2:'#1D2129',chrome:'#13161C',control:'#222731','control-hover':'#2B3240',line:'#303540',text:'#EEF1F5',muted:'#A5AFBD',good:'#70DBA0',warn:'#F1C65C',bad:'#FF9199'};
  if(style==='graphite')Object.assign(p,{bg:'#08090B',panel:'#101216',panel2:'#171A1F',chrome:'#0B0D10',control:'#202329','control-hover':'#292D35',line:'#333740'});
  const tint=(base,color,n)=>rgbToHex(...Object.values(mixRgb(hexToRgb(base),color,n)));
  // Cards have their own color role in every style. Tint from the neutral base,
  // not an already tinted panel, so changing style does not compound the color.
  p.card=normalizeCardFill(cardFill)==='accent'?tint(p.panel,secondary,intensity):p.panel;
  p['card-secondary']=p.card;
  p['evidence-card']=style==='dual'&&normalizeCardFill(cardFill)==='accent'?tint(p.panel,tertiary,intensity):p.card;
  if(style==='tonal'||style==='aurora'){
    p.panel=tint(p.panel,secondary,style==='tonal'?intensity:intensity*.5);
    p.panel2=tint(p.panel2,secondary,style==='tonal'?Math.min(.55,intensity+.05):intensity*.65);
    p.chrome=tint(p.chrome,accent,.08);
  }
  p['secondary-soft']=tint(p.panel,secondary,style==='dual'?intensity*.65:(light?.06:.10));
  p['accent-soft']=tint(p.panel,accent,light?intensity*.35:intensity*.65);
  const surfaceKeys=['bg','panel','panel2','chrome','control','control-hover','accent-soft','secondary-soft','card','card-secondary','evidence-card'];
  // Strong white/black tints must not make a dark/light panel unreadable.
  const surfaceTarget=hexToRgb(p.bg),baseText=hexToRgb(p.text);
  for(const key of surfaceKeys){let shade=hexToRgb(p[key]);for(let i=0;i<100&&colorContrast(baseText,shade)<4.7;i++)shade=mixRgb(shade,surfaceTarget,.08);p[key]=rgbToHex(shade.r,shade.g,shade.b);}
  const surfaces=surfaceKeys.map(k=>hexToRgb(p[k]));
  const safe=readableColor(accent,surfaces,light),second=readableColor(secondary,surfaces,light);
  p.accent=rgbCss(safe);p['accent-text']=p.accent;p.secondary=rgbCss(second);p.tertiary=rgbCss(readableColor(tertiary,surfaces,light));
  p['accent-fill']=rgbCss(accent);p['on-accent-fill']=colorContrast(accent,{r:0,g:0,b:0})>=colorContrast(accent,{r:255,g:255,b:255})?'#000000':'#FFFFFF';
  p['on-accent']=colorContrast(safe,{r:16,g:18,b:23})>=4.5?'#101217':'#FFFFFF';
  // Primary is the exact chosen border/action hue, independent of card tint.
  p['accent-border']=rgbCss(accent);
  p['accent-chip']=p['accent-soft'];p['surface-highlight']=p['accent-soft'];p.progress=p.accent;
  for(const k of ['text','muted','good','warn','bad'])p[k]=rgbCss(readableColor(hexToRgb(p[k]),[...surfaces,hexToRgb(p['accent-soft']),hexToRgb(p['secondary-soft'])],light));
  return p;
}
function applyAppearancePalette(style,theme,custom,secondary){
  const presets={blue:'#7DB0FF',amber:'#F1BD55',emerald:'#61D49A',violet:'#B69AFF',rose:'#F08CA5',cyan:'#62D4DF'};
  style=normalizeThemeStyle(style);const root=document.documentElement;
  const primary=hexToRgb(theme==='custom'?custom:presets[theme])||hexToRgb('#7DB0FF');
  const migrated=Number(readLocalUiPreference('colorRolesVersion'))>=2;
  const second=migrated?(normalizeHexColor(secondary)||rgbToHex(primary.r,primary.g,primary.b)):rgbToHex(primary.r,primary.g,primary.b);
  const third=normalizeHexColor(migrated?readLocalUiPreference('tertiaryColor'):secondary)||'#EEB392';
  const strength=normalizeAccentStrength(readLocalUiPreference('accentStrength'));
  const cardFill=normalizeCardFill(readLocalUiPreference('cardFill'));
  const palette=appearancePalette(style,primary,hexToRgb(second),strength,cardFill,hexToRgb(third));
  root.dataset.cardFill=cardFill;
  const fillControl=document.querySelector('#cardFill');if(fillControl)fillControl.value=cardFill;
  const slider=document.querySelector('#accentStrength'),label=document.querySelector('#accentStrengthValue');if(slider)slider.value=String(strength);if(label)label.textContent=strength+'%';
  for(const [key,value] of Object.entries(palette))root.style.setProperty('--'+key,value);
  root.style.setProperty('--danger','var(--bad)');root.style.setProperty('--border','var(--line)');
  root.style.colorScheme=style==='paper'?'light':'dark';root.dataset.themeStyle=style;
  document.querySelectorAll('.styleChoice[data-theme-style]').forEach(b=>{const on=b.dataset.themeStyle===style;b.classList.toggle('active',on);b.setAttribute('aria-checked',String(on));});
  const panel=document.querySelector('#secondaryThemeControls');if(panel)panel.hidden=false;
  const thirdPanel=document.querySelector('#tertiaryThemeControls');if(thirdPanel)thirdPanel.hidden=style!=='dual';
  for(const id of ['#tertiaryThemePicker','#tertiaryThemeHex']){const el=document.querySelector(id);if(el)el.value=third;}
  for(const id of ['#secondaryThemePicker','#secondaryThemeHex']){const el=document.querySelector(id);if(el)el.value=second;}
}
function commitSecondaryColor(value){
  const hex=normalizeHexColor(value);if(!hex){toast('Enter a valid secondary HEX color.');return;}
  ensureColorRoles();writeLocalUiPreference('secondaryColor',hex);applyTheme(readLocalUiPreference('theme'),readLocalUiPreference('customColor'));queueUiPreferenceSave('secondaryColor',hex);
}
function ensureColorRoles(){
  if(Number(readLocalUiPreference('colorRolesVersion'))>=2)return;
  const colors=migrateColorRoles({theme:readLocalUiPreference('theme'),customColor:readLocalUiPreference('customColor'),secondaryColor:readLocalUiPreference('secondaryColor')});
  for(const key of ['secondaryColor','tertiaryColor','colorRolesVersion']){writeLocalUiPreference(key,colors[key]);queueUiPreferenceSave(key,colors[key]);}
}
function migrateColorRoles(values){
  if(Number(values.colorRolesVersion)>=2)return values;
  const presets={blue:'#7DB0FF',amber:'#F1BD55',emerald:'#61D49A',violet:'#B69AFF',rose:'#F08CA5',cyan:'#62D4DF'};
  const primary=values.theme==='custom'?(normalizeHexColor(values.customColor)||'#FF2D78'):(presets[values.theme]||presets.blue);
  return {...values,secondaryColor:primary,tertiaryColor:normalizeHexColor(values.secondaryColor)||'#EEB392',colorRolesVersion:2};
}
function commitTertiaryColor(value){
  const hex=normalizeHexColor(value);if(!hex){toast('Enter a valid tertiary HEX color.');return;}
  ensureColorRoles();writeLocalUiPreference('tertiaryColor',hex);applyTheme(readLocalUiPreference('theme'),readLocalUiPreference('customColor'));queueUiPreferenceSave('tertiaryColor',hex);
}
function applyCustomizationSections(){
  const values=readLocalUiPreference('customizationSections')||{};
  document.querySelectorAll('[data-customization-section]').forEach(el=>{el.open=Object.hasOwn(values,el.dataset.customizationSection)?!!values[el.dataset.customizationSection]:el.dataset.defaultOpen==='true';});
}
// Cover size is independent of row spacing and color placement.
function normalizeThumbnailScale(value,key='thumbnailScale'){
  const fallback=key==='matchedThumbnailScale'?125:100;
  const n=Number(value);return Number.isFinite(n)&&n>=50&&n<=200?Math.round(n/5)*5:fallback;
}
function applyThumbnailSizes(){
  const root=document.documentElement,main=normalizeThumbnailScale(readLocalUiPreference('thumbnailScale')),matched=normalizeThumbnailScale(readLocalUiPreference('matchedThumbnailScale'),'matchedThumbnailScale');
  const px=(name,value)=>root.style.setProperty('--'+name,Math.round(value)+'px');
  for(const [name,base] of Object.entries({'entry-thumb-width':72,'entry-thumb-height':105,'queue-thumb-width':60,'queue-thumb-height':88,'comparison-thumb-width':128,'comparison-thumb-height':187,'inspector-thumb-width':120,'inspector-thumb-height':175,'browse-card-width':190}))px(name,base*main/100);
  px('matched-thumb-width',48*matched/100);px('matched-thumb-height',70*matched/100);
  px('entry-column-width',Math.max(240,72*main/100+160));px('matched-column-width',Math.max(205,48*matched/100+135));px('queue-column-width',Math.max(270,60*main/100+200));
  for(const [key,n] of [['thumbnailScale',main],['matchedThumbnailScale',matched]]){const input=document.getElementById(key),output=document.getElementById(key+'Value');if(input)input.value=String(n);if(output)output.textContent=n+'%';}
}
function importQueueCover(item){
  const raw=String(item.backup_thumbnail_url||item.reference_thumbnail_url||'').trim();
  const cover=/^https?:\/\//i.test(raw)||/^\/(?!\/)/.test(raw)?raw:((item.entry_id||item.reference_entry_id)?entryCoverUrl(Number(item.entry_id||item.reference_entry_id)):'');
  return cover?`<img class="queueCover" src="${esc(cover)}" alt="" loading="lazy" decoding="async">`:'<span class="queueCover coverPlaceholder" aria-label="No cover available">No cover</span>';
}
function downloadBlob(blob,filename){
  const url=URL.createObjectURL(blob),link=document.createElement('a');link.href=url;link.download=filename;document.body.append(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),30000);
}
function diagnosticPrivateKey(key){
  const normalized=String(key).replace(/([a-z0-9])([A-Z])/g,'$1_$2');
  return /^(?:authorization|proxy[-_]authorization|(?:set[-_])?cookie|(?:.*[-_])?(?:password|passwd|passphrase|secret|token|api[-_]?key|access[-_]?key|credential|session(?:[-_]?id)?|csrf(?:[-_]?token)?|device[-_]?code|user[-_]?code|user[-_]?name|email|account[-_]?id|workspace[-_]?id|private[-_]?key))$/i.test(normalized)||/^(?:user|host(?:name)?|ip|client_ip|remote_addr|title|preferred_title|backup_title|source_title|query|search_query)$/i.test(normalized);
}
function scrubDiagnosticExport(value,depth=0){
  if(depth>12)return '[depth limited]';
  if(Array.isArray(value))return value.map(item=>scrubDiagnosticExport(item,depth+1));
  if(value&&typeof value==='object')return Object.fromEntries(Object.entries(value).map(([key,item])=>[key,diagnosticPrivateKey(key)?'[redacted]':scrubDiagnosticExport(item,depth+1)]));
  if(typeof value!=='string')return value;
  return value.replace(/-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?(?:-----END [^-]*PRIVATE KEY-----|$)/g,'[private key omitted]')
    .replace(/\b(?:https?|wss?):\/\/[^\s<>"']+/gi,url=>{
      const match=url.match(/^(\w+:\/\/)([^/?#]+)([^?#]*)/);if(!match)return '[URL omitted]';
      const authority=/^\[host-[0-9a-f]{10}\]$/.test(match[2])?match[2]:'[host]';
      const path=/^\/(?:api|static)(?:\/|$)/.test(match[3])||/^\/(?:graphql|status|health)$/.test(match[3])?match[3]:(match[3]?'/[path]':'');
      return match[1]+authority+path+(url.includes('?')?'?'+url.split('?')[1].split('#')[0]:'');
    })
    .replace(/([?&][^\s&=]+)=[^\s&#"']*/g,'$1=[redacted]')
    .replace(/([A-Za-z_][A-Za-z0-9_-]*)(["']?\s*[:=]\s*)(?:"[^"\r\n]*"|'[^'\r\n]*'|[^\s,;&{}\[\]]+)/g,(all,key,separator)=>diagnosticPrivateKey(key)?key+separator+'[redacted]':all)
    .replace(/((?:authorization|proxy-authorization|cookie|set-cookie)["']?\s*[:=]\s*)[^\r\n]+/gi,'$1[redacted]')
    .replace(/((?:[\w-]*(?:token|password|secret|api[_-]?key)|passphrase|credential|access[_-]?key|user(?:name)?|email|account[_-]?id|workspace[_-]?id)["']?\s*[:=]\s*)(?:"[^"\r\n]*"|'[^'\r\n]*'|[^\s,;&}]+)/gi,'$1[redacted]')
    .replace(/\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+/gi,'$1 [redacted]')
    .replace(/\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/g,'[redacted]')
    .replace(/\bsk-(?:proj-|ant-)?[A-Za-z0-9_-]{12,}/g,'[redacted]')
    .replace(/((?:--?(?:storepass|keypass|ks-pass|key-pass|password|passwd|token|api-key|api_key|client-secret|credential))(?:=|\s+))(?:"[^"\r\n]*"|'[^'\r\n]*'|[^\s]+)/gi,'$1[redacted]')
    .replace(/\bpass:[^\s,"']+/gi,'pass:[redacted]')
    .replace(/\b[A-Z0-9._%+-]{1,64}@[A-Z0-9.-]{1,253}\.[A-Z]{2,63}\b/gi,'[email]')
    .replace(/(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])/g,ip=>ip.split('.').every(n=>Number(n)<=255)?'[address]':ip)
    .replace(/(?<![\w:])(?:[0-9a-f]{0,4}:){2,}[0-9a-f:.]*(?![\w:])/gi,'[address]')
    .replace(/[A-Za-z]:\\[^\r\n"<>|]*/g,path=>'[path]/'+path.split('\\').at(-1))
    .replace(/(?<![\w:/])\/(?:home|Users|mnt|config|data|tmp)\/[^\s"']+/g,path=>'[path]/'+path.split('/').at(-1));
}
function diagnosticExportText(backend,browserErrors,health,refreshError='',support={}){
  const backendVersion=support.runtime?.app_version||backend?.app_version||health?.version||'unknown';
  const uiVersion=typeof UI_BUILD_VERSION!=='undefined'?UI_BUILD_VERSION:'unknown';
  const lines=['AutoSourcarr diagnostic logs','Diagnostic format: 3','Exported (browser UTC): '+new Date().toISOString(),
    'Version: '+String(backendVersion),'UI asset version: '+uiVersion,
    'Server snapshot UTC: '+String(support.runtime?.server_time||backend?.captured_at||'unavailable'),
    'Browser received snapshot UTC: '+String(support.browser_received_at||'unavailable'),
    'Dependency checks captured UTC: '+String(health?.captured_at||'unavailable (cached status)'),
    'Clock note: server and browser timestamps use separate clocks; compare capture times before inferring event order.',
    'Scope: retained app/source-engine/helper logs, backend and current-browser errors, service/task/import context and cached health.',
    'Retention: process logs rotate on disk; backend/browser registries reset on restart. Missing or older rotated records cannot be recovered.',
    'Privacy: configured credentials, account identifiers, endpoint hosts, query values and private paths are masked. Raw settings, databases, cookies and request bodies are excluded.'];
  if(uiVersion!=='unknown'&&backendVersion!=='unknown'&&uiVersion!==backendVersion)lines.push('VERSION MISMATCH: browser assets and backend differ; refresh the browser after redeploying.');
  if(support.runtime)lines.push('Runtime: '+JSON.stringify(support.runtime,null,2));
  if(typeof navigator!=='undefined')lines.push('Browser: '+JSON.stringify({user_agent:navigator.userAgent,language:navigator.language,platform:navigator.platform},null,2));
  if(typeof uiTimingSnapshot==='function')lines.push('\nRecent browser timings (ms; requests include server/network wait and body decoding; no query values):',JSON.stringify(uiTimingSnapshot(),null,2));
  if(typeof attentionState!=='undefined')lines.push('\nSources needing human assistance:',JSON.stringify(attentionState,null,2));
  if(refreshError)lines.push('PARTIAL EXPORT: '+refreshError+' Cached records are marked by their original capture times.');
  if(support.partial_error)lines.push('PARTIAL EXPORT: '+JSON.stringify(support.partial_error));
  if(support.source_engine||health?.source_engine)lines.push('\nSource engine: '+JSON.stringify(support.source_engine||health.source_engine,null,2));
  lines.push('Backend records retained: '+Number(backend?.history_count??backend?.items?.length??0),
    'Backend records omitted from snapshot: '+Number(backend?.omitted_records||0));
  for(const key of ['source_host','main_suwayomi','translation','library']){
    const h=health?.[key];if(!h)continue;
    lines.push('\nConnection '+key+': '+JSON.stringify(Object.fromEntries(['configured','ok','degraded','deferred','url','error','warning','refreshing','subbed_entries','checked_at','probes','exception_chain'].filter(k=>h[k]!==undefined).map(k=>[k,h[k]])),null,2));
  }
  for(const key of ['limits','internal_services','task_counts','tasks','tasks_omitted','task_ui','imports','logs'])if(support[key]!==undefined)lines.push('\n'+key+': '+JSON.stringify(support[key],null,2));
  if(!support.logs)lines.push('PARTIAL EXPORT: retained process logs were unavailable; only the listed error snapshots were collected.');
  for(const [origin,items] of [['Browser',browserErrors||[]],['Backend',backend?.items||[]]]){
    lines.push('\n'+origin+' errors ('+items.length+')');
    items.forEach((item,i)=>{lines.push('\n'+'='.repeat(64),'Record '+(i+1));for(const [k,v] of Object.entries(scrubDiagnosticExport(item)))lines.push(k+': '+(typeof v==='object'?JSON.stringify(v,null,2):String(v)));});
  }
  return scrubDiagnosticExport(lines.join('\n'))+'\n';
}
async function exportErrors(){
  const button=document.querySelector('#exportErrors'),shortcut=document.querySelector('#downloadDiagnostics'),status=document.querySelector('#errorExportStatus');
  if(button.disabled)return;
  button.disabled=true;if(shortcut)shortcut.disabled=true;status.textContent='Preparing diagnostics…';let failure='',support={};
  let backend=lastBackendErrors,exportHealth=lastHealthSnapshot;
  try{
    try{
      support=await api('/api/admin/diagnostics',{cache:'no-store',timeoutMs:8000,suppressClientError:true,noMutationRefresh:true});
      support.browser_received_at=new Date().toISOString();backend=support.errors||backend;
      exportHealth=support.health||exportHealth;
      if(support.partial_error)failure='Some local diagnostics could not be collected.';
    }catch(e){
      failure='Detailed snapshot unavailable: '+e.message;
      try{backend=await api('/api/admin/errors?limit=100&include_resolved=true',{cache:'no-store',timeoutMs:6000,suppressClientError:true,noMutationRefresh:true});}
      catch(error){failure+='; error-history refresh failed: '+error.message;}
    }
    try{if(typeof loadAttention==='function')await loadAttention(true);}catch(e){failure+='; assistance refresh failed: '+e.message;}
    if(typeof taskUiDiagnosticContext==='function')support.task_ui=taskUiDiagnosticContext();
    const content=diagnosticExportText(backend,clientErrors,exportHealth,failure,support);
    downloadBlob(new Blob([content],{type:'text/plain;charset=utf-8'}),'AutoSourcarr-diagnostics-'+new Date().toISOString().replace(/[:.]/g,'-')+'.txt');
    status.textContent=failure?'Downloaded available logs; missing sections are noted in the file.':'Downloaded diagnostic logs with sensitive data masked.';
  }catch(e){status.textContent='Export failed: '+e.message;}finally{button.disabled=false;if(shortcut)shortcut.disabled=false;}
}

function workbenchTaskItems(items){return workbenchState.taskLane==='all'?items:items.filter(t=>t.origin===workbenchState.taskLane);}
function workbenchRepairTaskItems(items){return workbenchState.repairLane==='all'?items:items.filter(t=>t.origin===workbenchState.repairLane);}
function updateActivityStrip(){
  for(const lane of ['manual','automatic']){
    const el=document.querySelector(lane==='manual'?'#activityManualText':'#activityAutomaticText');if(!el)continue;
    const rows=(tasksState.items||[]).filter(t=>t.state==='running'&&t.origin===lane);
    const needsInput=t=>t.kind==='source-raw-input'||t.repair_state==='awaiting-raws';
    const input=rows.filter(needsInput).length;
    const active=rows.filter(t=>!needsInput(t)&&!['queued','waiting'].includes(t.activity)).length;
    const waiting=rows.length-active-input;
    el.textContent=rows.length?`${active} active${waiting?` · ${waiting} waiting`:''}${input?` · ${input} need input`:''}`:'Idle';
  }
}
function diagnosticCause(item){
  const raw=String(item.message||'Unknown error'),all=raw+' '+String(item.detail||'');
  if(item.key==='source-host-health')return {title:'The source engine did not pass its API health check',next:'The engine may be busy or unavailable. Cached entries remain usable. Check Status; if this persists, export diagnostics and check the container log.'};
  if(item.component==='Import discovery'&&/timed out|timeout/i.test(all))return {title:'Automatic import search ran out of time',next:'Partial candidates were kept. Review this item in Imports, choose a match, or retry when sources are available.'};
  if(item.component==='Import source'&&/timed out|timeout/i.test(all))return {title:'A source lookup took too long',next:'Other sources can still be searched. This does not mean the title is missing or that a CAPTCHA was detected.'};
  if(/captcha|human verification|shape.selecting|cloudflare.*challenge|failed to bypass cloudflare/i.test(all))return {title:'The source website needs verification',next:'Open this source in AutoSourcarr’s WebView, complete its verification, then retry the affected chapter.'};
  if(/(?:HTTP(?: error)?|status(?: code)?)\s*:?\s*429\b|rate.limit/i.test(all))return {title:'The source is limiting requests',next:'AutoSourcarr waits before retrying. Keep the source’s request limits and use another candidate while it recovers.'};
  if(/(?:HTTP(?: error)?|status(?: code)?)\s*:?\s*403\b/i.test(all))return {title:'The source website denied the chapter request (HTTP 403)',next:'Open the affected source in WebView to inspect its response, or use another candidate. A reachable engine does not mean the website allows this request.'};
  if(/(?:HTTP(?: error)?|status(?: code)?)\s*:?\s*521\b/i.test(all))return {title:'The source website returned HTTP 521',next:'The website’s origin may be unavailable. Retry later or choose another chapter candidate; the source engine can still be reachable.'};
  if(/Name or service not known|getaddrinfo|nodename nor servname/i.test(all)&&/source.host/i.test(all+' '+String(item.component||'')))return {title:'The configured source engine cannot be found',next:'Open Status and check the source engine mode and saved connection. Integrated mode requires the bundled AutoSourcarr image and retained source data.'};
  if(/All connection attempts failed/i.test(all)&&/translat/i.test(all+' '+String(item.component||'')))return {title:'The title translation service is unavailable',next:'Open Settings → Services and check Title translation. Integrated mode starts it inside AutoSourcarr; an external connection uses its saved URL.'};
  if(/timed?\s*out|timeout/i.test(all))return {title:'The request took too long',next:'Check Tasks for an accepted or waiting action before retrying. A browser timeout does not confirm that the action failed.'};
  if(/failed to fetch/i.test(all))return {title:'The browser could not reach AutoSourcarr',next:'Check the app connection and refresh. Your existing library and saved decisions are retained.'};
  return {title:raw.split(/\s+at\s+(?:[\w$]+\.)|\n\s*at |Caused by:/)[0].slice(0,280),next:'Review the affected request below, then retry when the dependency is available.'};
}
function workbenchErrorHtml(item){
  const cause=diagnosticCause(item),resolved=!!item.resolved,severity=item.severity==='warning'?'warning':'error';
  const meta=[Number(item.count||1)+' occurrence'+(Number(item.count||1)===1?'':'s'),Number(item.record_count||1)>1?`${item.record_count} affected requests`:'',fmtWhen(item.last_seen||item.first_seen)].filter(Boolean).join(' · ');
  const technical=[item.app_version?'AutoSourcarr '+item.app_version:'',item.context&&Object.keys(item.context).length?JSON.stringify(item.context,null,2):'',item.exception_chain?.length?JSON.stringify(item.exception_chain,null,2):'',item.detail_truncated?'Detail shortened in retained history; export includes available stack locations.':'',item.source_context&&Object.keys(item.source_context).length?JSON.stringify(item.source_context,null,2):'',item.method&&item.path?item.method+' '+item.path:item.path,item.status_code?'Recorded status code: '+item.status_code:'',item.message,item.detail,item.paths?.length>1?'Affected requests:\n'+item.paths.join('\n'):''].filter(Boolean).join('\n\n');
  return `<article class="diagnosticError ${severity} ${resolved?'resolved':''}"><div class="diagnosticErrorHead"><div><span class="errorStateBadge ${severity}">${resolved?'Resolved':severity==='warning'?'Warning':'Error'}</span><strong>${esc(item.source_context?.source_name||item.component||'AutoSourcarr')}</strong></div><span class="muted">${esc(meta)}</span></div><div class="diagnosticMessage">${esc(cause.title)}</div><p class="diagnosticNext">${esc(cause.next)}</p>${item.source_context?.source_id?`<button class="miniButton" data-error-webview="${esc(item.id)}">Open affected source in WebView</button>`:""}<details><summary>Technical details</summary><pre>${esc(technical)}</pre></details></article>`;
}
function filterWorkbenchErrors(items){
  const active=items.filter(x=>!x.resolved).length,resolved=items.length-active;
  for(const [id,n] of [['#diagnosticActiveCount',active],['#diagnosticResolvedCount',resolved]]){const el=document.querySelector(id);if(el)el.textContent=String(n);}
  const clear=document.querySelector('#clearResolvedErrors');if(clear)clear.hidden=workbenchState.diagnosticFilter!=='resolved'||resolved===0;
  return items.filter(x=>workbenchState.diagnosticFilter==='resolved'?x.resolved:!x.resolved).sort((a,b)=>Number(!!b.pinned&&!b.resolved)-Number(!!a.pinned&&!a.resolved));
}
function setWorkbenchTaskLane(lane){
  workbenchState.taskLane=['manual','automatic'].includes(lane)?lane:'all';
  document.querySelectorAll('[data-task-lane]').forEach(b=>{const on=b.dataset.taskLane===workbenchState.taskLane;b.classList.toggle('active',on);b.setAttribute('aria-pressed',String(on));});
  renderTasks();
}
function setWorkbenchRepairLane(lane,keepSelection=false){
  if(!keepSelection)globalThis.AutoSourcarrRepairTasks?.originChanged();
  workbenchState.repairLane=['manual','automatic'].includes(lane)?lane:'all';
  document.querySelectorAll('[data-repair-lane]').forEach(b=>{const on=b.dataset.repairLane===workbenchState.repairLane;b.classList.toggle('active',on);b.setAttribute('aria-pressed',String(on));});
  renderTasks();
}
function reviewText(value,limit=260){
  const text=String(value||'').trim();if(!text)return '<span class="muted">Not available</span>';
  if(text.length<=limit)return esc(text);
  return `<span>${esc(text.slice(0,limit))}…</span><details class="reviewMore"><summary>Read full text</summary><div>${esc(text)}</div></details>`;
}
function reviewFields(rows){return '<dl class="reviewFields">'+rows.map(([label,value])=>`<div><dt>${esc(label)}</dt><dd>${value}</dd></div>`).join('')+'</dl>';}
function comparisonCard(label,title,cover,meta,rows,extra=''){
  return `<article class="comparisonCard"><div class="comparisonLabel">${esc(label)}</div><div class="comparisonHero">${cover?`<img src="${esc(cover)}" alt="" class="comparisonCover" loading="lazy" decoding="async">`:'<div class="comparisonCover coverPlaceholder" aria-label="No cover">No cover</div>'}<div><h3>${esc(title)}</h3><p class="muted">${esc(meta)}</p></div></div>${reviewFields(rows)}${extra}</article>`;
}
function duplicateComparisonCard(e,label){
  const aliases=(e.titles||[]).filter(x=>x&&x!==e.preferred_title),sources=(e.sources||[]).map(x=>typeof x==='string'?x:(x.name||x.source_name||x.source_id)).filter(Boolean);
  return comparisonCard(label,e.preferred_title||'Untitled',entryCoverUrl(e.id),`${duplicateSubbedSet().has(Number(e.id))?'Subbed':'Unsubbed'} · ${Number(e.match_count||0)} source groups · ID ${Number(e.id)}`,[['Aliases',reviewText(aliases.join(' · '),180)],['Sources',reviewText(sources.join(' · '),180)],['Summary',reviewText(e.description)],['Publication',esc(e.status_label||'Unknown')]],`<div class="reviewCardActions"><button class="miniButton" onclick="openDuplicateInMatrix(${Number(e.id)})">Open in matrix</button></div>`);
}
function openDuplicateInMatrix(id){const entry=duplicateBrowserIndex().get(Number(id))?.entry;if(!entry)return;document.querySelector('#matrixSearch').value=entry.preferred_title||'';switchTab('matrix');}
function comparisonEvidence(pair){
  return `<section class="comparisonEvidence"><h3>Similarity evidence</h3><div class="evidenceMetrics"><div><strong>${Number(pair.exact_title_count||0)} shared titles</strong><span>${reviewText((pair.exact_titles||[]).join(' · '),130)}</span></div><div><strong>${Math.round(Number(pair.summary_similarity||0)*100)}% summary match</strong><span class="muted">Cleaned descriptions</span></div><div><strong>${Math.round(Number(pair.title_score||0)*100)}% title similarity</strong><span class="muted">A suggestion, not a decision</span></div></div><details class="reviewMore"><summary>Automatic matching details</summary><p>${esc(pair.auto_merge_reason||'Manual review required')}</p>${pair.matched_title&&pair.other_title?`<p>${esc(pair.matched_title)} ↔ ${esc(pair.other_title)}</p>`:''}</details></section><div class="reviewDecisionBar"><p class="muted">Merging keeps source records and chapters. You will confirm the merge.</p><div class="duplicatePairActions"><button class="goodAction" ${duplicateReviewButtonAttributes(pair.left_id,pair.right_id)} onclick="reviewDuplicate(${Number(pair.left_id)},${Number(pair.right_id)},'duplicate')">Same work</button><button class="dangerAction" ${duplicateReviewButtonAttributes(pair.left_id,pair.right_id)} onclick="reviewDuplicate(${Number(pair.left_id)},${Number(pair.right_id)},'not_duplicate')">Different works</button></div></div>`;
}
function importCandidateChoices(runId,item){
  const choices=item.suggestions||[];if(!choices.length)return '<p class="muted">No candidates yet.</p>';
  const key=String(runId)+'|'+item.id,chosen=workbenchState.importCandidates.get(key);
  const selected=choices.find(c=>Number(c.id)===Number(chosen))||choices[0];
  workbenchState.importCandidates.set(key,Number(selected.id));
  if(workbenchState.importCandidates.size>200)workbenchState.importCandidates.delete(workbenchState.importCandidates.keys().next().value);
  const selector=choices.length>1?`<label class="candidateSelector">Candidate <select aria-label="Import candidate" onchange="selectImportCandidate(${Number(item.id)},this.value)">${choices.map(c=>`<option value="${Number(c.id)}" ${Number(c.id)===Number(selected.id)?'selected':''}>${esc(c.preferred_title||'Entry '+c.id)}</option>`).join('')}</select><span class="muted">${choices.length} suggestions</span></label>`:'';
  return selector+importCandidateHtml(runId,item,selected);
}
function selectImportCandidate(itemId,candidateId){
  const item=(importsState.items||[]).find(i=>Number(i.id)===Number(itemId));if(!item||!['unresolved','failed'].includes(item.state))return;
  workbenchState.importCandidates.set(String(importsState.runId)+'|'+itemId,Number(candidateId));const box=document.querySelector('#importCandidates-'+itemId);if(box)box.innerHTML=importCandidateChoices(importsState.runId,item);
}

function renderCompareDesk(shown,focusId){
  if(focusId&&shown.some(x=>Number(x.entry.id)===Number(focusId))){workbenchState.duplicateLeft=Number(focusId);workbenchState.duplicateRight=null;}
  let rec=shown.find(x=>Number(x.entry.id)===workbenchState.duplicateLeft)||shown[0];
  if(!rec)return '';
  workbenchState.duplicateLeft=Number(rec.entry.id);
  const candidates=[...rec.candidateIds].map(id=>rec.entries.get(id)).filter(Boolean);
  const counterpart=candidates.find(e=>Number(e.id)===workbenchState.duplicateRight)||candidates[0];
  if(!counterpart)return '<div class="emptyTaskState">This review changed. Refresh candidates to continue.</div>';
  workbenchState.duplicateRight=Number(counterpart.id);
  const pair=rec.pairs.find(p=>[Number(p.left_id),Number(p.right_id)].includes(Number(counterpart.id)));
  return `<div class="compareDesk"><aside class="reviewQueue" aria-label="Duplicate review queue">${shown.map(x=>`<button class="${Number(x.entry.id)===workbenchState.duplicateLeft?'active':''}" aria-pressed="${Number(x.entry.id)===workbenchState.duplicateLeft}" onclick="selectDuplicateDesk(${Number(x.entry.id)})"><img class="queueCover" src="${entryCoverUrl(x.entry.id)}" loading="lazy" alt=""><span class="queueText"><strong>${esc(x.entry.preferred_title)}</strong><small>${x.candidateIds.size} candidate${x.candidateIds.size===1?'':'s'} · ID ${Number(x.entry.id)}</small></span></button>`).join('')}</aside><div class="compareContent"><div class="compareHeader"><h3>Review possible duplicate</h3><select aria-label="Compare candidate" onchange="selectDuplicateCounterpart(this.value)">${candidates.map(e=>`<option value="${Number(e.id)}" ${Number(e.id)===Number(counterpart.id)?'selected':''}>${esc(e.preferred_title)} · ID ${Number(e.id)}</option>`).join('')}</select></div><div class="comparePair">${duplicateComparisonCard(rec.entry,'A · Selected entry')}${duplicateComparisonCard(counterpart,'B · Matching candidate')}</div><div class="compareEvidence">${pair?comparisonEvidence(pair):'<p>No pair evidence is available.</p>'}</div></div></div>`;
}
function selectDuplicateDesk(id){workbenchState.duplicateLeft=Number(id);workbenchState.duplicateRight=null;renderDuplicates();}
function selectDuplicateCounterpart(id){workbenchState.duplicateRight=Number(id);renderDuplicates();}
function rememberImportDraft(){
  if(!workbenchState.importKey)return;
  const input=document.querySelector('#importSearch-'+workbenchState.importId);
  if(input)workbenchState.importDrafts.set(workbenchState.importKey,input.value);
  if(workbenchState.importDrafts.size>200)workbenchState.importDrafts.delete(workbenchState.importDrafts.keys().next().value);
}
function renderImportWorkspace(run,visible,{preserveDetail=false}={}){
  const queue=document.querySelector('#importQueue');if(!queue)return false;
  rememberImportDraft();
  const item=visible.find(x=>String(run?.id)+'|'+x.id===workbenchState.importKey)||visible[0];
  const nextKey=item?String(run?.id)+'|'+item.id:'';
  const unchanged=nextKey===workbenchState.importKey;
  workbenchState.importKey=nextKey;workbenchState.importId=item?.id??null;
  queue.innerHTML=visible.map(x=>`<button class="${Number(x.id)===Number(item?.id)?'active':''}" aria-pressed="${Number(x.id)===Number(item?.id)}" onclick="selectImportDesk(${Number(x.id)})">${importQueueCover(x)}<span class="queueText"><strong>${esc(x.title||x.original_title||'Untitled import')}</strong><small>${esc(x.source_name||'Original source unknown')} · ${esc(x.state||'unresolved')}</small></span></button>`).join('')||'<p class="muted">No items in this view.</p>';
  if(!preserveDetail||!unchanged){
    document.querySelector('#importItems').innerHTML=item?importItemHtml(run,item):'<div class="emptyTaskState">No items match these import filters.</div>';
    const input=item?document.querySelector('#importSearch-'+item.id):null;
    if(input&&workbenchState.importDrafts.has(nextKey))input.value=workbenchState.importDrafts.get(nextKey);
  }
  return true;
}
function selectImportDesk(id){rememberImportDraft();workbenchState.importKey=String(importsState.runId)+'|'+Number(id);workbenchState.importId=null;renderImportItems();}
function patchImportWorkspace(item){
  const run=importsState.runs.find(r=>String(r.id)===String(importsState.runId));
  const visible=(importsState.items||[]).filter(importPassesStateFilter).filter(importPassesReviewFilter);
  renderImportWorkspace(run,visible,{preserveDetail:Number(item.id)!==Number(workbenchState.importId)});
  updateImportReviewCounts();renderImportPagination();
}
function subbedHasGaps(e,byEntry){return (byEntry.get(e.id)||[]).some(m=>m.chapters_checked&&Number(m.missing_chapter_count)>0);}
function filterLibraryScope(entries,byEntry){
  if(workbenchState.libraryScope==='unmatched')return entries.filter(e=>Number(e.match_count||0)===0);
  if(workbenchState.libraryScope==='missing')return entries.filter(e=>subbedHasGaps(e,byEntry));
  return entries;
}
function renderSubbedRows(entries,d,byEntry,rowsOnly=false){
  const metric=v=>v==null?'Unknown':Number(v).toLocaleString();
  const rows=entries.map(e=>{
    const stats=subscriptionStatsFor(d,e.id),gaps=(byEntry.get(e.id)||[]).filter(m=>m.chapters_checked&&Number(m.missing_chapter_count)>0).length;
    return `<tr class="matrixRow ${activeInspector.subbed===e.id?'selectedRow':''}" data-entry-id="${Number(e.id)}"><td class="entryCol"><button class="entrySummary" onclick="openInlineEntry('subbed',${Number(e.id)},event)"><span class="matrixLibraryCard entryLibraryCard"><img class="matrixLibraryCover" src="${entryCoverUrl(e.id)}" data-entry-cover="${Number(e.id)}" alt="" loading="lazy"><strong class="matrixCardTitle">${esc(e.preferred_title)}</strong><span class="publicationBadge">${esc(e.status_label||'Unknown')}</span>${e.manual_preferred_title?'<span class="manualTitleBadge">Manual title</span>':''}</span></button></td><td><span class="coverageMetric">${e.served_latest_chapter==null?'—':esc(fmtChapterNumber(e.served_latest_chapter))}</span><span class="coverageCaption">${e.chapter_stats_pending?(e.chapter_stats_error?'Coverage unavailable; refresh to retry':'Loading chapter coverage…'):e.chapter_records_checked?'Checked chapters':'Cached chapters'}</span></td><td><span class="coverageMetric">${metric(stats?.downloaded_chapter_count)}</span><span class="coverageCaption">Main library</span></td><td><span class="coverageMetric">${metric(stats?.chapter_count)}</span><span class="coverageCaption">Known in library</span></td><td><span class="${!Number(e.match_count)?'warn':gaps?'warn':'muted'}">${Number(e.match_count||0)} source groups</span><span class="coverageCaption">${!Number(e.match_count)?'Needs a match':gaps?gaps+' source records with gaps':e.chapter_stats_pending?(e.chapter_stats_error?'Coverage unavailable; refresh to retry':'Loading chapter coverage…'):'Open to inspect coverage'}</span>${gaps?`<details><summary>Missing by source</summary>${(byEntry.get(e.id)||[]).filter(m=>m.chapters_checked&&Number(m.missing_chapter_count)>0).map(m=>`<div>${esc(m.source_title||m.source_id)}: ${Number(m.missing_chapter_count)} missing</div>`).join('')}</details>`:''}<button class="miniButton" onclick="openInlineEntry('subbed',${Number(e.id)},event)">Review</button></td></tr>`;
  });
  return rowsOnly?rows:'<thead><tr><th>Entry</th><th>Latest served</th><th>Downloaded</th><th>Library chapters</th><th>Source coverage</th></tr></thead><tbody>'+rows.join('')+'</tbody>';
}
function filterManualLedger(){
  document.querySelectorAll('.manualScopeBox').forEach(box=>{
    if(box.tagName==='DETAILS')return;
    const label=box.querySelector('strong');if(!label)return;
    const details=document.createElement('details'),summary=document.createElement('summary');
    details.className=box.className;summary.textContent=label.textContent;details.append(summary);label.remove();
    while(box.firstChild)details.append(box.firstChild);
    box.replaceWith(details);
  });
  const q=(document.querySelector('#manualActionSearch')?.value||'').trim().toLocaleLowerCase();let count=0,total=0;
  document.querySelectorAll('.manualActionPanel.active .manualActionCard,.manualActionPanel.active .manualGroupCard,#manualSinglesList>.manualMemberCard').forEach(card=>{if(!card.closest('.manualActionPanel.active'))return;total++;const visible=!q||card.textContent.toLocaleLowerCase().includes(q);card.hidden=!visible;if(visible)count++;});
  const label=document.querySelector('#manualSearchCount');if(label)label.textContent=q?`${count} of ${total} decisions`:'';
}
function filterSettings(){
  const q=(document.querySelector('#settingsSearch')?.value||'').trim().toLowerCase();
  document.querySelectorAll('#settingsGroups .settingsGroup').forEach(group=>{let shown=0;group.querySelectorAll('.settingsField').forEach(field=>{field.hidden=!!q&&!field.textContent.toLowerCase().includes(q);if(!field.hidden)shown++;});group.hidden=shown===0;if(q)group.open=shown>0;});
}
function repositoryMessage(text){const el=document.querySelector('#repositoryStatus');if(el)el.textContent=text;}
function renderRepositories(){
  const list=document.querySelector('#repositoryList');if(!list)return;
  const restart=document.querySelector('#restartRepositorySources');if(restart)restart.disabled=!repositoryState.can_restart;
  list.innerHTML=repositoryState.repositories.map((r,i)=>`<article class="repositoryCard"><div><h3>${esc(r.name||'Extension repository')}</h3><p>${esc(r.url)}</p><span class="repositoryState">${esc(r.state||'Saved configuration')}</span></div><button class="danger ghost" onclick="removeSourceRepository(${i},this)">Remove repository</button></article>`).join('')||'<div class="emptyTaskState">No extension repositories configured. Add Keiyoushi or another compatible repository.</div>';
}
function repositoryApplyMessage(){return repositoryState.can_restart?'Choose Apply saved repositories to restart this instance’s source process. Installed extensions, sign-ins and library data are retained.':'Restart the configured external source engine to apply saved repositories.';}
async function applySavedRepositories(){
  const button=document.querySelector('#restartRepositorySources');if(!repositoryState.can_restart||button.disabled)return;button.disabled=true;
  try{await api('/api/admin/source-browser/restart',{method:'POST',timeoutMs:6000,noMutationRefresh:true});repositoryMessage('Source restart queued. Check Status for readiness, then refresh Discover. Installed extensions and source sign-ins are retained.');}
  catch(error){repositoryMessage('Source restart was not queued: '+error.message);}
  finally{button.disabled=!repositoryState.can_restart;}
}
async function loadRepositories(){
  if(repositoryState.loading)return;repositoryState.loading=true;repositoryMessage('Loading saved repositories…');
  try{const data=await api('/api/admin/source-repositories',{timeoutMs:6000,noMutationRefresh:true});repositoryState={...repositoryState,...data,loaded:true};renderRepositories();repositoryMessage(repositoryApplyMessage());}
  catch(e){repositoryMessage('Unable to load repositories: '+e.message);}
  finally{repositoryState.loading=false;}
}
async function previewSourceRepository(event){
  event.preventDefault();const button=document.querySelector('#previewRepository'),url=document.querySelector('#repositoryUrl').value.trim();button.disabled=true;repositoryState.preview=null;const panel=document.querySelector('#repositoryPreview');panel.hidden=true;
  try{const data=await api('/api/admin/source-repositories/preview',{method:'POST',body:JSON.stringify({url}),timeoutMs:12000,noMutationRefresh:true});if(document.querySelector('#repositoryUrl').value.trim()!==url)return;repositoryState.preview=data;panel.innerHTML=`<h3>${esc(data.name||'Extension repository')}</h3><p>${esc(data.format)} · ${data.extension_count==null?'Extension count checked by the source host':Number(data.extension_count)+' extensions'}</p><p class="muted">Recognized manifest. Source compatibility is checked during installation.</p><p class="muted">Source code: ${esc(data.source_repository||'No unambiguous declaration found')}. ${esc(data.source_code_status||'Provide the original project when required.')}</p><button class="primaryAction" id="confirmAddRepository">Add repository</button>`;panel.hidden=false;document.querySelector('#confirmAddRepository').onclick=addSourceRepository;}
  catch(e){repositoryMessage('Repository preview failed: '+e.message);}
  finally{button.disabled=false;}
}
async function addSourceRepository(){
  const preview=repositoryState.preview;if(!preview)return;const b=document.querySelector('#confirmAddRepository');b.disabled=true;
  try{const data=await api('/api/admin/source-repositories',{method:'POST',body:JSON.stringify({url:preview.url,revision:repositoryState.revision}),timeoutMs:8000,noMutationRefresh:true});repositoryState={...repositoryState,...data,preview:null};renderRepositories();document.querySelector('#repositoryForm').hidden=true;repositoryMessage('Repository saved. '+repositoryApplyMessage());}
  catch(e){repositoryMessage('Repository was not saved: '+e.message+' Refresh the list before retrying.');b.disabled=false;}
}
async function removeSourceRepository(index,button){
  const repo=repositoryState.repositories[index];if(!repo||!confirm('Remove this repository from the source engine configuration? Installed extensions are retained.'))return;button.disabled=true;
  try{const data=await api('/api/admin/source-repositories',{method:'DELETE',body:JSON.stringify({url:repo.url,revision:repositoryState.revision}),timeoutMs:8000,noMutationRefresh:true});repositoryState={...repositoryState,...data};renderRepositories();repositoryMessage('Repository removed from configuration. '+repositoryApplyMessage());}
  catch(e){repositoryMessage('Repository was not removed: '+e.message);button.disabled=false;}
}
const diagnosticInertState=new Map();
function setDiagnosticsModal(open){
  const backdrop=document.querySelector('#diagnosticsBackdrop');if(backdrop)backdrop.hidden=!open;
  document.body.classList.toggle('diagnosticsOpen',open);
  if(open){
    const main=document.querySelector('main'),panel=document.querySelector('#errorPanel');
    for(const el of [...document.body.children,...(main?.children||[])]){
      if(el===main||el===panel||el===panel?.parentElement||['SCRIPT','STYLE'].includes(el.tagName))continue;
      if(!diagnosticInertState.has(el))diagnosticInertState.set(el,el.inert);
      el.inert=true;
    }
  }else{for(const [el,inert] of diagnosticInertState)el.inert=inert;diagnosticInertState.clear();}
}
let apkDownloadState={loading:false,downloading:false,version:'',available:false};
async function loadApkDownload(){
  if(apkDownloadState.loading||apkDownloadState.downloading)return;
  apkDownloadState.loading=true;
  const button=document.querySelector('#downloadApk'),message=document.querySelector('#apkDownloadStatus');
  button.disabled=true;
  try{
    const data=await api('/api/admin/extension-apk',{cache:'no-store',timeoutMs:6000,noMutationRefresh:true});
    apkDownloadState={...apkDownloadState,...data};
    button.textContent='Download APK · v'+data.version;button.disabled=!data.available;
    message.textContent=data.available?`Ready · Android extension ${data.extension_version} · ${Math.ceil(data.size/1024)} KB`:(data.message||'APK not built for this version.');
    document.querySelector('#apkBuildHelp').hidden=!!data.available;
  }catch(e){apkDownloadState.available=false;message.textContent='Unable to check the APK: '+e.message;document.querySelector('#apkBuildHelp').hidden=false;}
  finally{apkDownloadState.loading=false;}
}
async function downloadCurrentApk(){
  if(!apkDownloadState.available||apkDownloadState.downloading)return;
  const button=document.querySelector('#downloadApk'),message=document.querySelector('#apkDownloadStatus'),version=apkDownloadState.version;
  apkDownloadState.downloading=true;button.disabled=true;message.textContent='Preparing current APK…';
  try{
    const blob=await api('/api/admin/extension-apk/download?version='+encodeURIComponent(version),{responseType:'blob',cache:'no-store',timeoutMs:60000,noMutationRefresh:true});
    if(blob.type!=='application/vnd.android.package-archive'||!blob.size)throw new Error('The server returned no valid APK. Check your sign-in, then refresh Status.');
    downloadBlob(blob,`AutoSourcarr-v${version}-Tachiyomi-Mihon.apk`);message.textContent='APK download started · v'+version;
  }catch(e){message.textContent='APK download failed: '+e.message;}
  finally{apkDownloadState.downloading=false;button.disabled=!apkDownloadState.available;}
}

function syncModalScrollLock(){document.body.classList.toggle('modalOpen',!!document.querySelector('.modalBackdrop:not([hidden])'));}
// One dismissal owner prevents Escape closing a dialog underneath diagnostics.
function initializeOverlayDismissal(){
  const panel=document.querySelector('#errorPanel');
  const backdrop=document.createElement('div');backdrop.id='diagnosticsBackdrop';backdrop.hidden=true;
  panel.before(backdrop);backdrop.append(panel);
  const closers={diagnosticsBackdrop:closeErrors,duplicateManagerModal:closeDuplicateManager,
    autoMergePreviewModal:closeAutoMergePreview,matchBrowser:closeMatchBrowser,unmatchDuplicatesModal:closeUnmatchDuplicates};
  const top=()=>!backdrop.hidden?backdrop:[...document.querySelectorAll('.modalBackdrop:not([hidden])')].at(-1);
  let pressed=null;
  document.addEventListener('pointerdown',event=>{pressed=event.target;},true);
  document.addEventListener('pointercancel',()=>{pressed=null;},true);
  const dismiss=element=>{if(element.id==='setupModal')window.AutoSourcarrSetup?.cancel();else if(element.id==='sourcePreferencesModal')window.closeSourcePreferences?.();else closers[element.id]?.();syncModalScrollLock();};
  document.addEventListener('click',event=>{
    const overlay=top(),origin=pressed;pressed=null;
    if(overlay){if(event.target===overlay&&(!origin||origin===overlay)){event.preventDefault();event.stopImmediatePropagation();dismiss(overlay);}return;}
    document.querySelectorAll('details.toolbarMenu[open],details.langpicker[open]').forEach(menu=>{if(!menu.contains(event.target))menu.open=false;});
  },true);
  document.addEventListener('keydown',event=>{
    if(event.key!=='Escape')return;
    const overlay=top();if(overlay){event.preventDefault();event.stopImmediatePropagation();dismiss(overlay);return;}
    const menus=[...document.querySelectorAll('details.toolbarMenu[open],details.langpicker[open]')];
    if(menus.length){event.preventDefault();event.stopImmediatePropagation();menus.forEach(menu=>menu.open=false);menus.at(-1).querySelector('summary')?.focus();}
  },true);
}

function initializeWorkbench(){
  initializeOverlayDismissal();
  for(const id of ['#tertiaryThemePicker','#tertiaryThemeHex'])document.querySelector(id)?.addEventListener(id.endsWith('Picker')?'input':'change',event=>commitTertiaryColor(event.target.value));
  document.querySelectorAll('[data-customization-section]').forEach(el=>el.addEventListener('toggle',()=>{if(!uiPreferencesState.loaded)return;const values=readLocalUiPreference('customizationSections')||{},key=el.dataset.customizationSection;if(values[key]===el.open)return;queueUiPreferenceSave('customizationSections',{...values,[key]:el.open});}));
  applyThumbnailSizes();
  for(const key of ['thumbnailScale','matchedThumbnailScale'])document.getElementById(key)?.addEventListener('input',event=>{const n=normalizeThumbnailScale(event.target.value,key);writeLocalUiPreference(key,n);applyThumbnailSizes();queueUiPreferenceSave(key,n);});
  document.querySelector('#resetThumbnailSizes')?.addEventListener('click',()=>{for(const [key,n] of [['thumbnailScale',100],['matchedThumbnailScale',125]]){writeLocalUiPreference(key,n);queueUiPreferenceSave(key,n);}applyThumbnailSizes();});
  document.querySelector('#accentStrength')?.addEventListener('input',event=>{const n=normalizeAccentStrength(event.target.value);writeLocalUiPreference('accentStrength',n);applyTheme(readLocalUiPreference('theme'),readLocalUiPreference('customColor'));queueUiPreferenceSave('accentStrength',n);});
  document.querySelector('#cardFill')?.addEventListener('change',event=>{const value=normalizeCardFill(event.target.value);writeLocalUiPreference('cardFill',value);applyTheme(readLocalUiPreference('theme'),readLocalUiPreference('customColor'));queueUiPreferenceSave('cardFill',value);});
  document.querySelector('#openSourceWebview')?.addEventListener('click',()=>openSourceWebview());
  document.querySelector('#errorList')?.addEventListener('click',event=>{const b=event.target.closest('[data-error-webview]');if(b){const e=[...(lastBackendErrors.groups||[]),...(lastBackendErrors.items||[])].find(x=>x.id===b.dataset.errorWebview);if(e)openDiagnosticWebview(e);}});
  document.querySelector('[data-tab=about]')?.addEventListener('click',loadSourceEngine);
  document.querySelector('#downloadApk')?.addEventListener('click',downloadCurrentApk);
  document.querySelector('#exportErrors')?.addEventListener('click',exportErrors);
  document.querySelector('#downloadDiagnostics')?.addEventListener('click',()=>{openErrors();exportErrors();});
  document.querySelectorAll('.styleChoice[data-theme-style]').forEach(b=>b.addEventListener('click',()=>applyThemeStyle(b.dataset.themeStyle,true)));
  document.querySelector('#customThemePicker')?.addEventListener('input',event=>commitCustomThemeColor(event.target.value));
  document.querySelector('#secondaryThemePicker')?.addEventListener('input',event=>commitSecondaryColor(event.target.value));
  document.querySelector('#secondaryThemeHex')?.addEventListener('change',event=>commitSecondaryColor(event.target.value));
  document.querySelector('#retryPreferenceSave')?.addEventListener('click',()=>{uiPreferencesState.saveFailures=0;flushUiPreferenceSave();});
  document.querySelectorAll('.themePicker,.themeStylePicker,[aria-label="Interface density"]').forEach(group=>group.addEventListener('keydown',event=>{
    if(!['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(event.key))return;
    const choices=[...group.querySelectorAll('button[role="radio"]')];const i=choices.indexOf(document.activeElement);if(i<0)return;
    event.preventDefault();const step=['ArrowLeft','ArrowUp'].includes(event.key)?-1:1;const next=choices[(i+step+choices.length)%choices.length];next.focus();next.click();
  }));
  document.querySelectorAll('[data-task-lane]').forEach(b=>b.onclick=()=>setWorkbenchTaskLane(b.dataset.taskLane));
  document.querySelectorAll('[data-repair-lane]').forEach(b=>b.onclick=()=>setWorkbenchRepairLane(b.dataset.repairLane));
  for(const [id,lane] of [['#activityManual','manual'],['#activityAutomatic','automatic']])document.querySelector(id)?.addEventListener('click',()=>{setWorkbenchTaskLane(lane);if(window.openTasksSection)window.openTasksSection('general');else switchTab('tasks');});
  document.querySelectorAll('[data-library-scope]').forEach(b=>b.onclick=()=>{workbenchState.libraryScope=b.dataset.libraryScope;document.querySelectorAll('[data-library-scope]').forEach(x=>{x.classList.toggle('active',x===b);x.setAttribute('aria-pressed',String(x===b));});renderMatrix('subbed');});
  document.querySelector('#manualActionSearch')?.addEventListener('input',filterManualLedger);
  document.querySelector('#settingsSearch')?.addEventListener('input',filterSettings);
  document.querySelectorAll('[data-diagnostic-filter]').forEach(b=>b.onclick=()=>{workbenchState.diagnosticFilter=b.dataset.diagnosticFilter;document.querySelectorAll('[data-diagnostic-filter]').forEach(x=>x.classList.toggle('active',x===b));renderErrorsSnapshot();});
  document.querySelector('#showImportUpload')?.addEventListener('click',()=>{const el=document.querySelector('.importUploadPanel');if(el){el.scrollIntoView({block:'nearest'});document.querySelector('#backupImportFile')?.focus({preventScroll:true});}});
  document.querySelector('#refreshRepositories')?.addEventListener('click',loadRepositories);
  document.querySelector('#restartRepositorySources')?.addEventListener('click',applySavedRepositories);
  document.querySelector('#showRepositoryForm')?.addEventListener('click',()=>{document.querySelector('#repositoryForm').hidden=false;document.querySelector('#repositoryUrl').focus();});
  document.querySelector('#repositoryForm')?.addEventListener('submit',previewSourceRepository);
  document.querySelector('#repositoryUrl')?.addEventListener('input',()=>{repositoryState.preview=null;document.querySelector('#repositoryPreview').hidden=true;});
  document.querySelector('#useKeiyoushi')?.addEventListener('click',()=>{document.querySelector('#repositoryUrl').value='https://github.com/keiyoushi/extensions/raw/repo/index.pb';repositoryState.preview=null;document.querySelector('#repositoryPreview').hidden=true;});
  document.addEventListener('keydown',e=>{const panel=document.querySelector('#errorPanel');if(!panel||panel.hidden)return;if(e.key==='Escape'){e.preventDefault();closeErrors();return;}if(e.key!=='Tab')return;const controls=[...panel.querySelectorAll('button,a[href],input,select,summary,[tabindex="0"]')].filter(x=>!x.disabled&&x.getClientRects().length);const first=controls[0],last=controls.at(-1);if(e.shiftKey&&(document.activeElement===first||!panel.contains(document.activeElement))){e.preventDefault();last?.focus();}else if(!e.shiftKey&&(document.activeElement===last||!panel.contains(document.activeElement))){e.preventDefault();first?.focus();}});
  initializeSourceBrowser();
  updateActivityStrip();updatePreferenceSaveState();
}

function safeSourceWebviewUrl(value){try{const u=new URL(String(value||''));return ['http:','https:'].includes(u.protocol)&&!u.username&&!u.password?u.href:'';}catch(_){return '';}}
async function openSourceWebview(url='',sourceId=''){
  let target=safeSourceWebviewUrl(url);
  // Open synchronously with the user's click so popup blockers do not discard
  // the authenticated result. The token stays in the API header/HttpOnly cookie.
  const view=window.open('about:blank','_blank');if(!view){toast('Allow a new tab to open the source WebView.');return;}view.opener=null;
  try{if(!target&&sourceId){const data=await api('/api/admin/sources',{noMutationRefresh:true,timeoutMs:5000});const base=(data.items||[]).find(x=>String(x.source_id)===String(sourceId))?.base_url;try{target=safeSourceWebviewUrl(new URL(url||base,base).href);}catch(_){target=safeSourceWebviewUrl(base);}}
  const result=await api('/api/admin/source-webview/session',{method:'POST',noMutationRefresh:true,timeoutMs:6000});if(result.url!=='/api/admin/source-webview')throw new Error('Invalid WebView address');view.location.href=result.url+(target?'#'+encodeURIComponent(target):'');}
  catch(e){view.close();toast('Unable to open source WebView: '+e.message);}
}
function openSourceFamilyWebview(index){const group=sourceGroups[index];if(group)openSourceWebview(group.base_url);}
function openDiagnosticWebview(item){
  const ctx=item.source_context||{},source=sourceRows.find(x=>String(x.source_id)===String(ctx.source_id));
  let target=ctx.source_chapter_url||source?.base_url||'';if(source?.base_url){try{target=new URL(target,source.base_url).href;}catch(_){target=source.base_url;}}openSourceWebview(target,ctx.source_id);
}
function challengeStatusText(c={}){
  const solver=c.cloudflare||{},messages=[c.message,solver.message];
  if(solver.endpoint)messages.push('Solver: '+solver.endpoint+'.');
  if(solver.detail)messages.push(solver.detail);
  if(solver.action)messages.push(solver.action);
  if(solver.next_probe_at){const next=new Date(solver.next_probe_at*1000);messages.push('Next automatic health check: '+next.toLocaleString()+'. Check automatic handlers retries now.');}
  const groups=new Map();
  for(const row of c.sources||[]){const key=row.source_name+': '+(row.message||row.state);groups.set(key,(groups.get(key)||0)+1);}
  for(const [message,count] of groups)messages.push(message+(count>1?' ('+count+' source variants)':''));
  return messages.filter(Boolean).join(' ')||'Automatic handlers: '+(c.state||'waiting for source engine')+'.';
}
async function loadSourceEngine(){
  const el=document.querySelector('#sourceEngineStatus');if(!el)return;
  try{const s=await api('/api/admin/source-engine',{timeoutMs:5000,noMutationRefresh:true});el.textContent=(s.mode==='integrated'?'Integrated process · no separate source container':'External source engine')+' · '+s.state+(s.error?' · '+s.error:'');const details=document.querySelector('#sourceChallengeStatus');if(details)details.textContent=challengeStatusText(s.automatic_challenges);}
  catch(e){el.textContent='Unable to read source engine status: '+e.message;}
}

async function retrySourceChallenges(){try{const r=await api('/api/admin/source-engine/challenges',{method:'POST',noMutationRefresh:true,timeoutMs:5000});await loadSourceEngine();toast(r.queued?'Verification setup queued. Refresh engine status shortly.':'Check not queued: automatic handlers are disabled, the engine is starting or another check is running.');}catch(e){toast(e.message);}}

// Shared engine controls; refresh status without replacing an unsaved UA draft.
const sourceBrowserUi={snapshot:null,loading:null,timer:null,dirty:false,revision:0,submitting:false};
function sourceBrowserVisible(){return !!document.querySelector('#settings.active #settingsBrowserPane.active');}
function sourceBrowserDraft(preset){
  const select=document.querySelector('#sourceUserAgentPreset'),input=document.querySelector('#sourceUserAgentValue');
  const known=sourceBrowserUi.snapshot?.presets?.find(p=>p.id===preset);
  select.value=known?.id||'default';
  input.readOnly=select.value!=='custom';
  if(select.value==='browser')input.value=navigator.userAgent;
  else if(select.value==='custom'){if(!sourceBrowserUi.dirty)input.value=sourceBrowserUi.snapshot?.preferences?.user_agent||'';}
  else input.value=known?.value||sourceBrowserUi.snapshot?.default_user_agent||'';
}
function renderSourceBrowser(){
  const s=sourceBrowserUi.snapshot;if(!s)return;
  const select=document.querySelector('#sourceUserAgentPreset');
  const signature=JSON.stringify(s.presets||[]);
  if(select.dataset.presets!==signature){const previous=select.value;select.innerHTML=(s.presets||[]).map(p=>`<option value="${esc(p.id)}">${esc(p.label)}</option>`).join('');select.dataset.presets=signature;select.value=previous;}
  if(!sourceBrowserUi.dirty){sourceBrowserDraft(s.preferences.preset);if(['custom','browser'].includes(s.preferences.preset))document.querySelector('#sourceUserAgentValue').value=s.preferences.user_agent;}
  select.disabled=!s.integrated;
  const busy=s.busy||sourceBrowserUi.submitting;
  for(const id of ['applySourceUserAgent','resetSourceUserAgent','clearSourceBrowserData'])document.getElementById(id).disabled=!s.integrated||busy;
  document.querySelector('#openSettingsWebview').disabled=busy||s.engine_state!=='ready';
  document.querySelector('#checkBrowserChallenges').disabled=!s.integrated||busy||s.engine_state!=='ready';
  const warnings=s.result?.warnings||[];
  document.querySelector('#sourceBrowserOperation').textContent=(!s.integrated?'Browser controls require integrated mode. ':s.error?'Change failed: '+s.error:busy?'Source browser change '+s.operation+'…':s.operation==='completed'?'Browser change complete · source engine '+s.engine_state:'Source engine '+s.engine_state)+(warnings.length?' '+warnings.join(' '):'');
  const hooks=s.runtime||{};
  const uaStatus=!s.preferences.user_agent?'Engine defaults selected (network default and native WebView identity).':
    'Saved identity: '+((s.presets||[]).find(p=>p.id===s.preferences.preset)?.label||'Custom')+'. Network: '+(hooks.network||'awaiting runtime confirmation')+'. WebView: '+(hooks.webview||'awaiting first WebView use')+'.';
  document.querySelector('#sourceUserAgentRuntime').textContent=uaStatus+(sourceBrowserUi.dirty?' Unsaved browser identity.':'');
  const c=s.automatic_challenges||{};
  document.querySelector('#browserChallengeStatus').textContent=challengeStatusText(c);
}
async function loadSourceBrowser(){
  if(sourceBrowserUi.loading)return sourceBrowserUi.loading;
  clearTimeout(sourceBrowserUi.timer);
  sourceBrowserUi.loading=(async()=>{
    try{sourceBrowserUi.snapshot=await api('/api/admin/source-browser',{timeoutMs:6000,noMutationRefresh:true,suppressClientError:true,cache:'no-store'});renderSourceBrowser();}
    catch(e){document.querySelector('#sourceBrowserOperation').textContent='Unable to refresh browser status: '+e.message;}
    finally{sourceBrowserUi.loading=null;if(sourceBrowserVisible())sourceBrowserUi.timer=setTimeout(loadSourceBrowser,sourceBrowserUi.snapshot?.busy?1500:5000);}
  })();return sourceBrowserUi.loading;
}
async function changeSourceBrowser(action,reset=false){
  if(sourceBrowserUi.submitting||sourceBrowserUi.snapshot?.busy)return;
  if(action==='clear-data'&&!window.confirm('Clear all source website cookies, sign-ins and WebView data? The configured solver session will also be reset if reachable. Sources briefly restart; library databases, extensions and downloads are retained.'))return;
  const revision=sourceBrowserUi.revision;
  const body=action==='user-agent'?{preset:reset?'default':document.querySelector('#sourceUserAgentPreset').value,user_agent:reset?'':document.querySelector('#sourceUserAgentValue').value}:null;
  sourceBrowserUi.submitting=true;renderSourceBrowser();
  try{
    await api('/api/admin/source-browser/'+action,{method:action==='user-agent'?'PUT':'POST',...(body?{body:JSON.stringify(body)}:{}),timeoutMs:6000,noMutationRefresh:true});
    if(action==='user-agent'&&sourceBrowserUi.revision===revision){sourceBrowserUi.dirty=false;}
    // The currently pending read may precede this accepted operation. Drain it,
    // then fetch the operation status instead of treating that old read as current.
    if(sourceBrowserUi.loading)await sourceBrowserUi.loading;
    await loadSourceBrowser();
  }catch(e){document.querySelector('#sourceBrowserOperation').textContent='Browser change was not confirmed: '+e.message;}
  finally{sourceBrowserUi.submitting=false;for(const id of ['applySourceUserAgent','resetSourceUserAgent','clearSourceBrowserData'])document.getElementById(id).disabled=!sourceBrowserUi.snapshot?.integrated||!!sourceBrowserUi.snapshot?.busy;}
}
async function checkBrowserChallenges(){
  const button=document.querySelector('#checkBrowserChallenges');button.disabled=true;
  try{const r=await api('/api/admin/source-engine/challenges',{method:'POST',timeoutMs:6000,noMutationRefresh:true});await loadSourceBrowser();if(!r.queued)toast(r.automatic_challenges?.state==='disabled'?'Automatic handlers are disabled. Enable SOURCE_AUTO_CHALLENGES in Application settings and redeploy.':'The engine is starting or a handler check is already running.');}
  catch(e){toast(e.message);}finally{button.disabled=!!sourceBrowserUi.snapshot?.busy||sourceBrowserUi.snapshot?.engine_state!=='ready';}
}
async function openSolverSettings(){
  setSettingsSubtab('configuration');
  // Do not replace an application-settings draft to follow a diagnostic shortcut.
  if(settingsState.dirty){toast('Save or discard your current settings draft before switching to solver settings.');return;}
  try{
    if(!settingsState.data)await loadEnvSettings();
    settingsState.activeFile='source_host';renderEnvSettings();
    const search=document.querySelector('#settingsSearch');search.value='FLARESOLVERR';filterSettings();search.focus();
  }catch(e){toast(e.message);}
}
function initializeSourceBrowser(){
  document.querySelector('#sourceUserAgentPreset')?.addEventListener('change',event=>{sourceBrowserUi.dirty=true;sourceBrowserUi.revision++;sourceBrowserDraft(event.target.value);renderSourceBrowser();});
  document.querySelector('#sourceUserAgentValue')?.addEventListener('input',()=>{sourceBrowserUi.dirty=true;sourceBrowserUi.revision++;});
  document.querySelector('#applySourceUserAgent')?.addEventListener('click',()=>changeSourceBrowser('user-agent'));
  document.querySelector('#resetSourceUserAgent')?.addEventListener('click',()=>changeSourceBrowser('user-agent',true));
  document.querySelector('#clearSourceBrowserData')?.addEventListener('click',()=>changeSourceBrowser('clear-data'));
  document.querySelector('#refreshSourceBrowser')?.addEventListener('click',loadSourceBrowser);
  document.querySelector('#openSettingsWebview')?.addEventListener('click',()=>openSourceWebview());
  document.querySelector('#checkBrowserChallenges')?.addEventListener('click',checkBrowserChallenges);
  document.querySelector('#openSolverSettings')?.addEventListener('click',openSolverSettings);
  document.querySelector('#browserNeedsAttention')?.addEventListener('click',()=>switchTab('attention'));
}
