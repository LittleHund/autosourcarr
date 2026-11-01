/* Archived packages use their original provenance; installation is a user action. */
(() => {
  const byId=id=>document.getElementById(id),panel=byId('sourceOrphanedPanel'),installedPanel=byId('sourceInstalledOrphanPanel');
  if(!panel||!installedPanel)return;
  const state={data:null,generation:0,busy:false,timer:null};
  const visible=()=>!document.hidden&&byId('sources')?.classList.contains('active')&&((byId('sourceManagePanel')?.classList.contains('active')&&(panel.open||installedPanel.open))||panel.classList.contains('active')||installedPanel.classList.contains('active'));
  const button=(text,action,style='')=>{const el=document.createElement('button');el.type='button';el.textContent=text;el.className=style;el.onclick=()=>action(el);return el;};
  const states={'available':'Archived source','installed-unverified':'Installed · functionality unverified','repair-queued':'Repair queued',repaired:'Repaired',unavailable:'Archive unavailable'};
  const active=()=>['queued','running','installing','restarting'].includes(state.data?.operation?.state)||state.data?.refresh?.state==='running';
  function pending(){
    for(const id of ['refreshSourceOrphans','reloadSourceOrphans','reloadInstalledOrphans'])byId(id).disabled=state.busy;
    document.querySelectorAll('[data-orphan-mutation]').forEach(el=>el.disabled=state.busy||active()||el.dataset.orphanBlocked==='true');
  }
  async function mutate(pkg,verb){
    if(state.busy||active())return;
    if(verb==='install'&&!confirm('Install this original archived source into the integrated source engine? Archived code is unverified and may no longer work. The source engine may restart briefly.'))return;
    state.busy=true;++state.generation;pending();
    try{
      const result=await api(`/api/v1/source-orphans/${encodeURIComponent(pkg)}/${verb}`,{method:'POST',timeoutMs:verb==='repair'?45000:20000,noMutationRefresh:true});
      toast(result.message||(verb==='install'?'Archived installation queued':'Repair queued. Follow progress in Tasks → Repairs.'));
      if(verb==='repair')window.openSourceRepairTask?.();
      await load();
    }catch(error){toast('Unable to '+(verb==='install'?'install archived source':'start repair')+': '+error.message);}
    finally{state.busy=false;pending();}
  }
  function card(row){
    const el=document.createElement('article');el.className='sourceExtensionCard orphanSourceCard';
    const body=document.createElement('div');body.className='grow';
    const title=document.createElement('h4');title.className='sourceExtensionTitle';title.textContent=row.name||row.pkgName;
    const meta=document.createElement('p');meta.className='sourceExtensionMeta';meta.textContent=`${row.lang||'all'} · ${row.versionName||'Unknown version'} · ${row.origin||'Original repository'}`;
    const status=document.createElement('strong');status.className='sourceExtensionFlag';status.textContent=row.orphan_reason==='manual-upload'?'Manually added · orphaned':row.orphan_reason==='manual'?'Marked as orphaned':!row.installed&&row.archive_available===false?'Archived source · package unavailable':states[row.state]||String(row.state||'Archived');
    const message=document.createElement('p');message.className='muted';message.textContent=row.message||'Functionality has not been verified.';
    body.append(title,meta,status,message);
    const provenance=document.createElement('details');const summary=document.createElement('summary');summary.textContent='Original package & revision';
    const identity=document.createElement('p');identity.className='sourceExtensionMeta';identity.textContent=row.pkgName;
    const commit=document.createElement('p');commit.className='sourceExtensionMeta';commit.textContent=row.commit||'No historical revision recorded';
    provenance.append(summary,identity,commit);
    // Never render a repository-provided javascript: or data: URL as an action.
    try{const url=new URL(row.commit_url);if(url.protocol==='https:'){const link=document.createElement('a');link.href=url.href;link.target='_blank';link.rel='noopener noreferrer';link.textContent='View original revision';provenance.append(link);}}catch(_){}
    body.append(provenance);
    const actions=document.createElement('div');actions.className='sourceExtensionActions';
    if(row.installed){
      actions.append(button('Source settings',()=>window.openExtensionSourcePreferences(row.pkgName)));
      if(row.can_unmark_orphan)actions.append(button('Return to Installed',el=>window.setSourceOrphan(row.pkgName,false,el)));
      if(row.hasUpdate&&!row.isObsolete)actions.append(button('Update',el=>window.manageSourceExtension(row.pkgName,'update',el)));
      actions.append(button('Uninstall',el=>window.manageSourceExtension(row.pkgName,'uninstall',el)));
    }
    if(!row.installed&&row.archive_available!==false&&(row.can_install||row.archive_url)){const install=button('Install archived version',()=>mutate(row.pkgName,'install'),'primaryAction');install.dataset.orphanMutation='install';install.dataset.orphanBlocked=String(!row.can_install);install.title=row.can_install?'':'Source engine inventory must be available before installation.';actions.append(install);}
    // Repairs run in the app (domain move: new address, content check, automatic restore) on installed sources only.
    const repairReady=state.data.repair_available??state.data.worker_configured;
    const note=text=>{const p=document.createElement('p');p.className='muted';p.textContent=text;actions.append(p);};
    if(!repairReady)note('Source repair is not available in this mode (it needs the integrated source engine).');
    else if(!row.installed)note('Install this source first; repairs run on installed sources.');
    else if(row.can_repair){const repair=button('Check and repair',()=>mutate(row.pkgName,'repair'),'primaryAction');repair.dataset.orphanMutation='repair';repair.title='Finds the new web address, checks real content and restores the last working version when the check fails.';actions.append(repair);}
    if(row.repair_job_id||row.state==='repair-queued'||row.state==='repaired')actions.append(button('View repair evidence',()=>window.openSourceRepairTask?.(row.repair_job_id)));
    el.append(body,actions);return el;
  }
  function render(){
    if(!state.data)return;
    const data=state.data,refresh=data.refresh||{};
    const updated=refresh.checked_at?new Date(typeof refresh.checked_at==='number'?refresh.checked_at*1000:refresh.checked_at).toLocaleString():'';
    const inventoryMessage=data.inventory_refreshing?'Checking installed sources…':data.inventory_known===false?'Source engine inventory is unavailable. Cached installation status is shown; refresh when the engine is ready.':'';
    const status=[inventoryMessage,refresh.message||'Collect repository history to find removed sources.',updated&&`Last checked ${updated}`,refresh.coverage_days&&`History window: ${refresh.coverage_days} days`,data.operation?.message,data.notice].filter(Boolean).join(' · ');
    byId('sourceOrphanStatus').textContent=status;
    byId('sourceInstalledOrphanStatus').textContent=[inventoryMessage,data.operation?.message,(data.repair_available??data.worker_configured)?'Check and repair moves a source to its new web address, checks real content and restores the last working version when the check fails.':'Source repair is not available in this mode.',data.notice].filter(Boolean).join(' · ');
    const orphanCount=byId('sourceOrphanCount'),installedCount=byId('sourceInstalledOrphanCount');
    if(orphanCount)orphanCount.textContent=String((data.items||[]).filter(row=>!row.installed).length);
    if(installedCount)installedCount.textContent=String((data.items||[]).filter(row=>row.installed).length);
    for(const installed of [false,true]){
      const list=byId(installed?'sourceInstalledOrphanList':'sourceOrphanList');list.replaceChildren();
      const query=byId(installed?'sourceInstalledOrphanSearch':'sourceOrphanSearch').value.trim().toLocaleLowerCase();
      const rows=(data.items||[]).filter(row=>!!row.installed===installed&&(!query||[row.name,row.pkgName,row.lang].join(' ').toLocaleLowerCase().includes(query)));
      for(const row of rows)list.append(card(row));
      if(!rows.length){const message=document.createElement('p');message.className='emptyState muted';message.textContent=query?'No archived sources match this search.':installed?'No installed orphan sources. Installed archived packages will appear here.':'No uninstalled orphan sources in the saved archive. Collect repository history to check available snapshots.';list.append(message);}
    }
    pending();
  }
  async function load(){
    clearTimeout(state.timer);const generation=++state.generation;
    try{
      const data=await api('/api/v1/source-orphans',{timeoutMs:15000,suppressClientError:true});
      if(generation!==state.generation)return;
      state.data=data;render();
    }catch(error){if(generation===state.generation){byId('sourceOrphanStatus').textContent='Unable to load archived sources: '+error.message;byId('sourceInstalledOrphanStatus').textContent='Unable to load installed orphans: '+error.message;}}
    finally{if(generation===state.generation&&visible())state.timer=setTimeout(load,active()||state.data?.inventory_refreshing?4000:20000);}
  }
  byId('refreshSourceOrphans').onclick=async()=>{
    if(state.busy||state.data?.refresh?.state==='running')return;
    state.busy=true;++state.generation;pending();
    try{const result=await api('/api/v1/source-orphans/refresh',{method:'POST',timeoutMs:15000,noMutationRefresh:true});toast(result.message||'Repository history collection queued');await load();}
    catch(error){byId('sourceOrphanStatus').textContent='Unable to collect repository history: '+error.message;}
    finally{state.busy=false;pending();}
  };
  for(const id of ['reloadSourceOrphans','reloadInstalledOrphans'])byId(id).onclick=load;
  for(const id of ['sourceOrphanSearch','sourceInstalledOrphanSearch'])byId(id).oninput=render;
  byId('openOrphanRepairs').onclick=()=>window.openSourceRepairTask?.();
  window.loadSourceOrphans=load;
  const observer=new MutationObserver(()=>{if(!visible())clearTimeout(state.timer);});
  for(const el of [byId('sources'),byId('sourceManagePanel'),panel,installedPanel])if(el)observer.observe(el,{attributes:true,attributeFilter:['class','open']});
  for(const section of [panel,installedPanel])section.addEventListener?.('toggle',()=>{if(visible())load();else clearTimeout(state.timer);});
  document.addEventListener('visibilitychange',()=>{if(visible())load();else clearTimeout(state.timer);});
  if(visible())load();
})();
