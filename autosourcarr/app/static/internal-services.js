/* Built-in tools are asynchronous; their status follows live task/job/connection events while the controls are visible. */
(()=>{
  const byId=id=>document.getElementById(id);
  const labels={repair:'Source repair',builder:'Reader extension builder',translation:'Title translation',solver:'Challenge handling'};
  let loading=null,timer=null,mode=null,generation=0,readerReady=false,builderPreflights=[];
  const visible=()=>!document.hidden&&(byId('about')?.classList.contains('active')||(byId('settings')?.classList.contains('active')&&!byId('settingsServicesPane')?.hidden));
  function readerBuildStatus(target,value){
    const valid=value&&typeof value.ready==='boolean'&&Number.isInteger(value.exit_code)&&Array.isArray(value.errors)&&Array.isArray(value.warnings)
      &&![...value.errors,...value.warnings].some(item=>!item||typeof item.key!=='string'||!Number.isInteger(item.code)||typeof item.message!=='string')
      &&!value.errors.some(item=>item.code<=0)&&value.ready===(value.errors.length===0)&&value.exit_code===(value.errors[0]?.code??0);
    const ready=!!(valid&&value.ready&&value.errors.length===0);
    if(!target)return ready;
    target.replaceChildren();
    const status=document.createElement('p');status.setAttribute('role','status');
    status.textContent=!valid?'Reader access check unavailable. Refresh services to check the running settings.':ready?'Reader access is set up.':'Reader access needs attention.';
    target.append(status);
    if(valid){
      const issues=document.createElement('ul');
      for(const [items,warning] of [[value.errors,false],[value.warnings,true]])for(const item of items){
        const row=document.createElement('li');row.textContent=(warning?'Warning · ':'')+(item.key?item.key+': ':'')+item.message;issues.append(row);
      }
      if(issues.children.length)target.append(issues);
    }
    return ready;
  }
  function readerSettings(){
    if(window.AutoSourcarrSetup?.openReaders)return window.AutoSourcarrSetup.openReaders();
    toast('Reader setup is still loading. Refresh this page if it stays unavailable.');
  }
  function readerCheckUnavailable(){
    readerReady=false;readerBuildStatus(byId('readerBuildPreflight'),null);
    for(const panel of builderPreflights)readerBuildStatus(panel,null);
  }
  // 0.5.0 has no in-app reader build (the prebuilt extension is served from /api/reader/repo/) and no service to start
  // from this panel: Build and Cancel stay hidden and nothing is ever posted (the 0.4.x /api/admin/services actions are gone).
  function updateBuilderControls(){
    for(const id of ['buildReaderExtensions','cancelReaderBuild']){byId(id).disabled=true;byId(id).hidden=true;}
  }
  function render(data){
    mode=data.mode;const integrated=mode==='integrated';
    const box=byId('internalServices');box.replaceChildren();builderPreflights=[];
    readerReady=readerBuildStatus(byId('readerBuildPreflight'),data.reader_build);
    if(!integrated){const p=document.createElement('p');p.textContent='External compatibility mode. Rebuild and redeploy the integrated image to use built-in tools.';box.append(p);}
    const names=data.services&&Object.keys(data.services).length?Object.keys(data.services):Object.keys(labels);
    for(const name of names){
      const state=data.services?.[name]||{},label=state.label||labels[name]||name,card=document.createElement('article');card.className='statusPanel';
      const title=document.createElement('h3');title.textContent=label;
      const message=document.createElement('p');message.textContent=state.message||'Starts when needed';
      const badge=document.createElement('span');badge.className='publicationPill';badge.textContent=state.state||'idle';
      card.append(title,badge,message);
      if(name==='repair'){
        // Navigation only: repair jobs live under Tasks → Repairs.
        const button=document.createElement('button');button.type='button';button.textContent='Open repair tools & account';button.disabled=!integrated;
        button.onclick=()=>{switchTab('sources');setSourceSubtab('maintenance');byId('repairSetupPanel').open=true;};card.append(button);
      }
      if(name==='builder'){
        const preflight=document.createElement('div');builderPreflights.push(preflight);readerBuildStatus(preflight,data.reader_build);card.append(preflight);
        const configure=document.createElement('button');configure.type='button';configure.textContent='Review reader settings';configure.onclick=readerSettings;card.append(configure);
      }
      box.append(card);
    }
    byId('readerBuildStatus').textContent=data.services?.builder?.message||'';
    updateBuilderControls();
    byId('openSolverSettings').hidden=integrated;
  }
  async function load({force=false}={}){
    if(loading&&!force)return loading;
    clearTimeout(timer);
    const own=++generation;
    const request=(async()=>{
      const data=await api('/api/v1/internal-services',{timeoutMs:5000,suppressClientError:true,noMutationRefresh:true});
      if(own!==generation)return;
      render(data);
      if(byId('about')?.classList.contains('active')){
        // Phase 10: the prebuilt reader extension is served from AutoSourcarr's own repository (/api/reader/repo/).
        const setup=await api('/api/v1/reader-extension/setup',{timeoutMs:5000,suppressClientError:true,noMutationRefresh:true});
        if(own!==generation)return;
        const extension=setup.extension||{};
        byId('downloadJar').disabled=!extension.jar;byId('downloadJar').dataset.version=extension.version_name||'';byId('downloadJar').dataset.path=extension.jar||'';
        loadApkDownload();
      }
    })();
    loading=request;
    try{await request;}catch(_error){
      if(own!==generation)return;
      readerCheckUnavailable();updateBuilderControls();
      byId('readerBuildStatus').textContent='Service status could not be refreshed. Use Refresh services to check again.';
    }
    finally{if(loading===request)loading=null;}
  }
  byId('refreshInternalServices').onclick=load;
  updateBuilderControls();
  byId('configureReaderBuild').onclick=readerSettings;
  byId('downloadJar').onclick=async()=>{
    const button=byId('downloadJar');if(button.disabled)return;button.disabled=true;
    const version=button.dataset.version;
    try{
      const blob=await api(button.dataset.path,{responseType:'blob',cache:'no-store',timeoutMs:60000,noMutationRefresh:true});
      if(blob.type!=='application/java-archive'||!blob.size)throw new Error('No valid JAR returned. Refresh Status and retry.');
      downloadBlob(blob,`AutoSourcarr-v${version}-Suwayomi.jar`);
    }catch(error){byId('readerBuildStatus').textContent=error.message;}
    finally{button.disabled=false;}
  };
  document.addEventListener('visibilitychange',()=>{if(visible())load();else clearTimeout(timer);});
  window.loadInternalServices=load;
  // UI-13: no timer; a build or service change arrives as an event and refreshes this panel once per burst.
  // Engine and connection state have their own topics; the reader build is a task (no more precise topic).
  globalThis.AutoSourcarrLive?.subscribe({name:'internal-services',topics:['sources','connections','tasks'],delayMs:1000,when:visible,refresh:()=>load()});
  window.integratedServiceMode=()=>mode;
})();
