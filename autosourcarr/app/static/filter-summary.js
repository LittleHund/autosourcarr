/* View-only recovery: never changes source enablement, import or matching decisions. */
(()=>{
  'use strict';
  const get=id=>document.getElementById(id),summaries=[];
  function summary(after,label,values,clear,extra=()=>null){
    if(!after)return;
    const root=document.createElement('section');root.className='riFilterSummary';root.setAttribute('aria-label',label+' view filters');
    const text=document.createElement('span'),button=document.createElement('button'),note=document.createElement('p');text.setAttribute('role','status');button.type='button';button.textContent='Clear view filters';note.className='muted';root.append(text,button,note);after.after(root);button.onclick=clear;
    let previous='';summaries.push(()=>{const selected=values(),message=extra()||'',signature=JSON.stringify([selected,message]);if(previous===signature)return;previous=signature;text.textContent=selected.length?'Active: '+selected.join(' · '):'No extra view filters';button.hidden=!selected.length;note.hidden=!message;note.textContent=message;});
  }
  function selected(id,defaultValue=''){
    const element=get(id);if(!element||element.value===defaultValue)return null;
    const text=element.tagName==='SELECT'?element.selectedOptions?.[0]?.textContent:element.value;
    return String(text||'').trim()||null;
  }
  const sourceSearch=get('sourceExtensionSearch'),language=get('sourceExtensionLanguage');
  summary(sourceSearch?.closest('.toolbar'),'Source catalogue',()=>[selected('sourceExtensionSearch'),selected('sourceExtensionLanguage','all')].filter(Boolean),()=>{
    sourceSearch.value='';language.value='all';renderSourceExtensions();update();sourceSearch.focus();
  },()=>{
    if(typeof sourceExtensionState==='undefined'||!sourceExtensionState.loaded)return 'Source inventory is not loaded. Refresh the catalogue; an unknown inventory is not evidence that sources are missing.';
    const q=sourceSearch.value.trim().toLocaleLowerCase(),lang=language.value||'all';
    const matches=sourceExtensionState.extensions.some(r=>(!q||`${r.name||''} ${r.pkgName||''} ${r.lang||''}`.toLocaleLowerCase().includes(q))&&(lang==='all'||r.lang===lang));
    return matches?'':'No extensions shown in the loaded snapshot for these filters. Clear view filters or use Refresh catalogue to check again.';
  });
  for(const [id,all,label] of [['sourceAvailability','all','Source availability'],['sourceHealthFilter','all','Installed-source health']]){
    const element=get(id);summary(element?.closest('.toolbar')||element?.closest('label'),label,()=>[selected(id,all)].filter(Boolean),()=>{element.value=all;element.dispatchEvent(new Event('change',{bubbles:true}));update();element.focus();},()=>id==='sourceHealthFilter'?'Health always remains limited to installed sources. Unknown and incomplete samples remain distinct from failures.':'');
  }
  for(const id of ['sourceOrphanSearch','sourceInstalledOrphanSearch']){
    const element=get(id);summary(element,'Archived source search',()=>[selected(id)].filter(Boolean),()=>{element.value='';element.dispatchEvent(new Event('input',{bubbles:true}));update();element.focus();});
  }
  summary(get('importStateFilters'),'Import review',()=>typeof importsState==='undefined'?[]:[importsState.filter!=='all'?'State: '+importsState.filter:null,importsState.review!=='all'?'Review: '+importsState.review.replaceAll('_',' '):null].filter(Boolean),()=>{
    importsState.filter='all';importsState.review='all';importsState.offset=0;
    for(const button of document.querySelectorAll('.importStateFilter'))button.classList.toggle('active',button.dataset.state==='all');
    for(const button of document.querySelectorAll('.importReviewFilter'))button.classList.toggle('active',button.dataset.review==='all');
    loadImportItems().catch(error=>toast(error.message));update();document.querySelector('.importStateFilter[data-state=all]')?.focus();
  },()=>{
    if(typeof importsState==='undefined')return '';
    if(importsState.error)return 'The latest refresh failed; saved decisions and the last loaded page are retained. Refresh before interpreting missing results.';
    if(!importsState.runId)return 'No import selected. Choose an existing import, or select a backup above. Clearing filters does not reset import decisions.';
    if(importsState.loading)return 'Loading the selected import view…';
    return !importsState.items.length?'No items shown for the selected import view. Clear view filters or refresh. Existing added, skipped and rejected decisions are retained.':'';
  });
  const libraryFilters=[['subbedSearch',''],['subbedSource',''],['subbedMinMatches',''],['subbedEntryMatches',''],['subbedTitleState','any'],['subbedStatus','any'],['subbedPublicationStatus','any']];
  summary(get('subbedCount')?.closest('.toolbar'),'Library / Wanted',()=>libraryFilters.map(([id,value])=>selected(id,value)).filter(Boolean),()=>{
    for(const [id,value] of libraryFilters)if(get(id))get(id).value=value;
    window.AutoSourcarrLibrary.load({reset:true}).catch(error=>toast(error.message));update();get('subbedSearch')?.focus();
  },()=>{
    if(typeof subbedData==='undefined'||!subbedData)return '';
    if(subbedData.subscription?.ok!==true)return 'Library membership is unavailable or refreshing. This does not prove that downloads or source matches are missing.';
    const scope=workbenchState.libraryScope;
    if(!subbedData.entries?.length&&['missing','missing_downloads','source_gaps','verification'].includes(scope))return 'No entries shown in this Wanted view. Unchecked, stale or deferred observations stay separate from confirmed gaps. Clear extra filters, refresh the snapshot, or choose Verification needed.';
    return ['missing','missing_downloads','source_gaps','verification'].includes(scope)?'Clearing extra filters keeps this Wanted category. Local timeouts and unobserved chapters do not prove missing content.':'';
  });
  function wantedRecovery(){
    const root=get('riLibraryGrid');if(!root||typeof workbenchState==='undefined'||typeof subbedData==='undefined')return;
    const empty=!subbedData?.entries?.length,wanted=['missing','missing_downloads','source_gaps'].includes(workbenchState.libraryScope);
    const existing=get('riWantedRecovery');
    if(!empty||!wanted){existing?.remove();return;}if(existing)return;
    const actions=document.createElement('div');actions.id='riWantedRecovery';actions.className='riToolActions';
    const verify=document.createElement('button');verify.type='button';verify.dataset.route='/wanted/verification';verify.textContent='View observations needing verification';
    const refresh=document.createElement('button');refresh.type='button';refresh.textContent='Refresh saved library view';refresh.onclick=()=>window.AutoSourcarrLibrary.load({reset:true}).catch(error=>toast(error.message));actions.append(verify,refresh);root.append(actions);
  }
  function update(){for(const render of summaries)render();wantedRecovery();}
  let pending=false;function schedule(){if(pending)return;pending=true;queueMicrotask(()=>{pending=false;update();});}
  document.addEventListener('input',schedule);document.addEventListener('change',schedule);document.addEventListener('click',schedule);
  for(const id of ['sources','imports','subbed'])if(get(id))new MutationObserver(schedule).observe(get(id),{subtree:true,childList:true});
  window.AutoSourcarrFilterSummary={update};update();
})();
