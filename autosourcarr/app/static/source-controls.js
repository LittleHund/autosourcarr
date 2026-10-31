/* Source-engine preferences. Drafts stay in memory, never in browser storage. */
(()=>{
'use strict';
const byId=id=>document.getElementById(id),modal=byId('sourcePreferencesModal');
if(!modal)return;
const state={generation:0,controller:null,id:'',drafts:new Map(),snapshots:new Map(),saving:new Set(),returnFocus:null};
const types=new Set(['switch','checkbox','text','list','multiselect']);
const equal=(a,b)=>JSON.stringify(a)===JSON.stringify(b);
const identity=p=>JSON.stringify([p.index,p.key,p.type]);
const draftsFor=id=>{if(!state.drafts.has(id))state.drafts.set(id,new Map());return state.drafts.get(id);};
const active=(id,generation)=>state.id===id&&state.generation===generation&&!modal.hidden;
const element=(tag,text,className)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(className)n.className=className;return n;};
function status(text){byId('sourcePreferencesStatus').textContent=text;}
function supported(p){return types.has(p.type)&&p.key!=null&&p.enabled!==false&&p.visible!==false;}
function controls(){
 const loading=!!state.controller,busy=state.saving.has(state.id),drafts=draftsFor(state.id);
 byId('sourcePreferencesSave').disabled=loading||busy||!drafts.size;
 byId('sourcePreferencesDiscard').disabled=busy||!drafts.size;
 byId('sourcePreferencesReload').disabled=loading||busy;
 byId('sourcePreferencesVariant').disabled=busy;
 for(const n of byId('sourcePreferencesFields').querySelectorAll('input,select,textarea'))n.disabled=loading||busy||n.dataset.readonly==='true';
}
function setDraft(p,value){const drafts=draftsFor(state.id);if(equal(value,p.value))drafts.delete(identity(p));else drafts.set(identity(p),value);status(drafts.size?'Unsaved changes. Close or switch source to keep these edits until this page is refreshed.':'No unsaved changes.');controls();}
function render(snapshot){
 const root=byId('sourcePreferencesFields');root.replaceChildren();
 const drafts=draftsFor(state.id),rows=(snapshot.preferences||[]).filter(p=>p.visible!==false);
 if(!rows.length)root.append(element('p',snapshot.configurable===false?'This extension does not expose source settings.':'This source has no configurable settings.','muted'));
 for(const p of rows){
  const field=element('div',undefined,'sourcePreferenceField'),label=element(p.type==='multiselect'?'fieldset':'label');
  const title=String(p.title||p.key||'Source preference'),value=drafts.has(identity(p))?drafts.get(identity(p)):p.value;
  const readOnly=!supported(p),secret=p.secret||/password|passphrase|secret|token|api.?key|authorization|cookie/i.test(String(p.key)+' '+title),controlId='sourcePreference-'+String(p.index),summaryId=controlId+'-summary';
  if(p.type==='multiselect'){
   label.append(element('legend',title));const options=element('div',undefined,'sourcePreferenceOptions');
   const selected=new Set(Array.isArray(value)?value.map(String):[]);
   (p.entry_values||[]).forEach((v,i)=>{const option=element('label'),input=element('input');input.type='checkbox';input.value=String(v);input.checked=selected.has(String(v));input.dataset.readonly=String(readOnly);input.disabled=readOnly;input.onchange=()=>{if(input.checked)selected.add(String(v));else selected.delete(String(v));setDraft(p,[...selected]);};option.append(input,element('span',String((p.entries||[])[i]??v)));options.append(option);});
   label.append(options);
  }else if(p.type==='switch'||p.type==='checkbox'){
   const line=element('span'),input=element('input');input.id=controlId;input.type='checkbox';input.checked=value===true;input.dataset.readonly=String(readOnly);input.disabled=readOnly;input.onchange=()=>setDraft(p,input.checked);line.append(input,element('span',title));label.append(line);
  }else if(p.type==='list'){
   label.append(element('span',title));const input=element('select');input.id=controlId;input.dataset.readonly=String(readOnly);input.disabled=readOnly;
   const values=(p.entry_values||[]).map(String);if(!values.includes(String(value??''))){const current=element('option',value==null?'Choose a value':`Current: ${value}`);current.value=String(value??'');current.disabled=true;input.append(current);}
   values.forEach((v,i)=>{const option=element('option',String((p.entries||[])[i]??v));option.value=v;input.append(option);});input.value=String(value??'');input.onchange=()=>setDraft(p,input.value);label.append(input);
  }else if(p.type==='text'){
   label.append(element('span',title));const input=element('input');input.id=controlId;input.type=secret?'password':'text';input.maxLength=8192;input.autocomplete='off';input.spellcheck=false;input.value=String(value??'');input.dataset.readonly=String(readOnly);input.disabled=readOnly;input.oninput=()=>setDraft(p,input.value);label.append(input);
  }else{
   label.append(element('strong',title),element('p','This setting type is not supported here.','muted'));
  }
  field.append(label);
  const message=[secret?'Sensitive setting. Its value is stored by the source engine.':p.summary,secret?'':p.dialog_message,readOnly&&types.has(p.type)?'This preference is read-only.':''].filter(Boolean).join('\n');
  if(message){const description=element('small',message);description.id=summaryId;field.append(description);for(const control of label.querySelectorAll('input,select'))control.setAttribute('aria-describedby',summaryId);}
  root.append(field);
 }
 controls();
}
async function load(id){
 state.controller?.abort();state.controller=null;state.id=String(id);const generation=++state.generation;
 if(state.saving.has(state.id)){render(state.snapshots.get(state.id));status('Saving source settings…');return;}
 const controller=new AbortController();state.controller=controller;
 byId('sourcePreferencesFields').replaceChildren();status('Loading source settings…');controls();
 try{
  const snapshot=await api('/api/v1/sources/'+encodeURIComponent(state.id)+'/preferences',{signal:controller.signal,timeoutMs:40000,noMutationRefresh:true,suppressClientError:true,cache:'no-store'});
  if(!active(String(id),generation))return;
  state.snapshots.set(String(id),snapshot);state.controller=null;render(snapshot);status(draftsFor(String(id)).size?'Current settings loaded. Unsaved edits were retained; review them before saving.':'Changes apply to this source language in the integrated engine.');
 }catch(error){if(active(String(id),generation)&&!controller.signal.aborted){state.controller=null;status('Unable to load source settings: '+error.message);controls();}}
 finally{if(state.controller===controller)state.controller=null;}
}
function beginPreferences(name){
 state.returnFocus=modal.hidden?document.activeElement:state.returnFocus;
 state.controller?.abort();state.controller=null;++state.generation;state.id='';modal.hidden=false;
 byId('sourcePreferencesTitle').textContent=(name||'Source')+' settings';
 byId('sourcePreferencesVariant').replaceChildren();byId('sourcePreferencesFields').replaceChildren();
 syncModalScrollLock();byId('sourcePreferencesClose').focus();controls();return state.generation;
}
async function selectPreferenceSources(name,variants){
 const select=byId('sourcePreferencesVariant');select.replaceChildren();
 const rows=variants.filter(v=>v.source_id!=null&&v.available!==false&&Number(v.available)!==0);
 for(const row of rows){const option=element('option',`${row.name||name} · ${langLabel(row.lang)} (${row.lang||'all'})`);option.value=String(row.source_id);select.append(option);}
 if(!rows.length){status('No installed source variants are available for this extension. Refresh the extension list after installation finishes.');controls();return;}
 select.value=String(rows[0].source_id);await load(select.value);
}
window.openSourcePreferences=async(index)=>{
 const group=sourceGroups[index];if(!group)return;beginPreferences(group.name);
 await selectPreferenceSources(group.name,group.variants);
};
window.openExtensionSourcePreferences=async(pkg)=>{
 // Package inventory carries the exact engine source IDs. Discovery must also work
 // before the separate source-family screen has ever been loaded or synchronized.
 pkg=String(pkg);let extension=(sourceExtensionState.extensions||[]).find(x=>String(x.pkgName)===pkg);
 const generation=beginPreferences(extension?.name||pkg);status('Loading installed source variants…');
 if(!extension||!Array.isArray(extension.source?.nodes)){
  const controller=new AbortController();state.controller=controller;controls();
  try{
   const inventory=await api('/api/v1/source-extensions',{signal:controller.signal,timeoutMs:45000,noMutationRefresh:true,suppressClientError:true,cache:'no-store'});
   if(generation!==state.generation||modal.hidden)return;
   extension=(inventory.extensions||[]).find(x=>String(x.pkgName)===pkg);
  }catch(error){if(generation===state.generation&&!modal.hidden&&!controller.signal.aborted)status('Unable to load installed sources: '+error.message);return;}
  finally{if(state.controller===controller){state.controller=null;controls();}}
 }
 if(generation!==state.generation||modal.hidden)return;
 if(!extension?.isInstalled){status('This extension is not currently installed. Install it before changing source settings.');controls();return;}
 byId('sourcePreferencesTitle').textContent=(extension.name||pkg)+' settings';
 await selectPreferenceSources(extension.name,(extension.source?.nodes||[]).filter(row=>row.id!=null).map(row=>({...row,source_id:String(row.id),available:true})));
};
window.closeSourcePreferences=()=>{state.controller?.abort();state.controller=null;++state.generation;modal.hidden=true;syncModalScrollLock();state.returnFocus?.focus?.();};
byId('sourcePreferencesVariant').onchange=event=>load(event.target.value);
byId('sourcePreferencesClose').onclick=window.closeSourcePreferences;
byId('sourcePreferencesReload').onclick=()=>{if(state.id)load(state.id);};
byId('sourcePreferencesDiscard').onclick=()=>{draftsFor(state.id).clear();const snapshot=state.snapshots.get(state.id);if(snapshot)render(snapshot);status('Unsaved changes discarded.');};
byId('sourcePreferencesForm').onsubmit=async event=>{
 event.preventDefault();const id=state.id;
 if(state.saving.has(id)||state.controller)return;
 let snapshot=state.snapshots.get(id);if(!snapshot)return;
 const drafts=draftsFor(id);if(!drafts.size)return;state.saving.add(id);controls();status('Saving source settings…');
 let saved=0,message='Source settings saved.';
 try{
  for(const [key,value] of [...drafts]){
   const p=(snapshot.preferences||[]).find(pref=>identity(pref)===key);
   if(!p||!supported(p))throw new Error('A preference changed or became unavailable. Reload settings and review your remaining edits.');
   snapshot=await api('/api/v1/sources/'+encodeURIComponent(id)+'/preferences',{method:'PUT',body:JSON.stringify({index:p.index,key:p.key,type:p.type,value,revision:snapshot.revision}),timeoutMs:65000,noMutationRefresh:true,suppressClientError:true});
   state.snapshots.set(id,snapshot);if(equal(drafts.get(key),value))drafts.delete(key);saved++;
  }
  if(state.id===id&&!modal.hidden)status(message);
 }catch(error){message=(saved?`${saved} setting(s) saved. `:'')+'Unable to save remaining settings: '+error.message+' Your unsaved edits are retained.';}
 finally{state.saving.delete(id);if(state.id===id&&!modal.hidden&&!state.controller){render(state.snapshots.get(id));status(message);controls();}}
};
modal.addEventListener('keydown',event=>{
 if(event.key!=='Tab')return;const focusable=[...modal.querySelectorAll('button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex="0"]')].filter(n=>!n.hidden);
 if(!focusable.length)return;const first=focusable[0],last=focusable.at(-1);
 if(event.shiftKey&&document.activeElement===first){event.preventDefault();last.focus();}else if(!event.shiftKey&&document.activeElement===last){event.preventDefault();first.focus();}
});

/* A single queue prevents multiple installs competing for the engine. */
const upload={queue:[],running:false,rows:[]};
function uploadStatus(){byId('sourceApkStatus').textContent=upload.running?`Installing extensions · ${upload.queue.length} waiting`:(upload.rows.length?'Installation queue complete. Review the results below.':'');byId('sourceApkClear').hidden=!upload.rows.length;byId('sourceApkClear').disabled=upload.running;}
function uploadRow(file){const row=element('li');row.dataset.state='queued';row.textContent=file.name+' · Waiting';byId('sourceApkResults').append(row);upload.rows.push(row);return row;}
async function drainUploads(){
 if(upload.running)return;upload.running=true;let changed=false;uploadStatus();
 try{while(upload.queue.length){
  const {file,row}=upload.queue.shift();row.dataset.state='installing';row.textContent=file.name+' · Installing…';uploadStatus();
  try{const result=await api('/api/v1/source-extensions/upload',{method:'POST',body:file,headers:{'Content-Type':'application/vnd.android.package-archive','X-Filename':encodeURIComponent(file.name)},timeoutMs:240000,noMutationRefresh:true,suppressClientError:true});row.dataset.state='installed';row.textContent=file.name+' · '+(result.message||'Installed');changed=true;}
  catch(error){const uncertain=error.name==='TimeoutError'||error.name==='TypeError'||Number(error.status_code)>=500||/not confirm|timed? ?out|timeout|network|failed to fetch/i.test(error.message);row.dataset.state=uncertain?'unconfirmed':'failed';row.textContent=file.name+' · '+(uncertain?'Installation not confirmed: ':'Installation failed: ')+error.message+(uncertain?' Refresh Sources to check whether it installed before retrying.':'');if(uncertain)changed=true;}
 }}finally{upload.running=false;uploadStatus();if(changed)refreshManagedSourcesAfterMutation();}
}
function enqueue(files){
 for(const file of Array.from(files||[])){
  const row=uploadRow(file);const error=!/\.apk$/i.test(file.name)?'Choose an .apk source extension':file.size<=0?'The file is empty':file.size>32*1024*1024?'The APK exceeds the 32 MiB limit':null;
  if(error){row.dataset.state='failed';row.textContent=file.name+' · '+error;continue;}upload.queue.push({file,row});
 }
 if(upload.queue.length)drainUploads();else uploadStatus();
}
const drop=byId('sourceApkDropZone'),files=byId('sourceApkFiles');
byId('sourceApkBrowse').onclick=()=>files.click();files.onchange=()=>{enqueue(files.files);files.value='';};
for(const name of ['dragenter','dragover'])drop.addEventListener(name,event=>{event.preventDefault();drop.classList.add('dragOver');if(event.dataTransfer)event.dataTransfer.dropEffect='copy';});
drop.addEventListener('dragleave',event=>{if(!drop.contains(event.relatedTarget))drop.classList.remove('dragOver');});
drop.addEventListener('drop',event=>{event.preventDefault();drop.classList.remove('dragOver');enqueue(event.dataTransfer?.files);});
byId('sourceApkClear').onclick=()=>{if(upload.running)return;upload.rows=[];byId('sourceApkResults').replaceChildren();uploadStatus();};
})();
