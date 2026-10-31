/* Built-in tools are asynchronous; poll only while their controls are visible. */
(()=>{
  const byId=id=>document.getElementById(id);
  const labels={repair:'Source repair',builder:'Reader extension builder',translation:'Title translation',solver:'Challenge handling'};
  let loading=null,timer=null,mode=null,generation=0,readerReady=false,builderBusy=false,builderActionPending=false,builderButtons=[],builderPreflights=[];
  const visible=()=>!document.hidden&&(byId('about')?.classList.contains('active')||(byId('settings')?.classList.contains('active')&&!byId('settingsServicesPane')?.hidden));
  function readerBuildStatus(target,value){
    const valid=value&&typeof value.ready==='boolean'&&Number.isInteger(value.exit_code)&&Array.isArray(value.errors)&&Array.isArray(value.warnings)
      &&![...value.errors,...value.warnings].some(item=>!item||typeof item.key!=='string'||!Number.isInteger(item.code)||typeof item.message!=='string')
      &&!value.errors.some(item=>item.code<=0)&&value.ready===(value.errors.length===0)&&value.exit_code===(value.errors[0]?.code??0);
    const ready=!!(valid&&value.ready&&value.errors.length===0);
    if(!target)return ready;
    target.replaceChildren();
    const status=document.createElement('p');status.setAttribute('role','status');
    status.textContent=!valid?'Reader build check unavailable. Refresh services to check the running settings.':ready?'Running reader settings pass the build check.':'Reader build needs attention. Review reader settings before building.';
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
  function updateBuilderControls(){
    const disabled=mode!=='integrated'||!readerReady||builderBusy||builderActionPending;
    byId('buildReaderExtensions').disabled=disabled;
    for(const button of builderButtons)button.disabled=disabled;
    byId('cancelReaderBuild').hidden=!builderBusy;
    byId('cancelReaderBuild').disabled=builderActionPending;
  }
  function render(data){
    mode=data.mode;const integrated=mode==='integrated';
    const box=byId('internalServices');box.replaceChildren();builderButtons=[];builderPreflights=[];
    readerReady=readerBuildStatus(byId('readerBuildPreflight'),data.reader_build);
    if(!integrated){const p=document.createElement('p');p.textContent='External compatibility mode. Rebuild and redeploy the integrated image to use built-in tools.';box.append(p);}
    for(const [name,label] of Object.entries(labels)){
      const state=data.services?.[name]||{},card=document.createElement('article');card.className='statusPanel';
      const title=document.createElement('h3');title.textContent=label;
      const message=document.createElement('p');message.textContent=state.message||'Starts when needed';
      const badge=document.createElement('span');badge.className='publicationPill';badge.textContent=state.state||'idle';
      card.append(title,badge,message);
      const button=document.createElement('button');button.type='button';
      button.textContent=name==='builder'?'Build extensions':name==='repair'?'Open repair tools & account':name==='translation'?'Prepare translation':'Start / check handler';
      button.disabled=!integrated||(name!=='repair'&&['running','queued','starting','ready'].includes(state.state));
      button.onclick=()=>{if(name==='repair'){switchTab('sources');setSourceSubtab('maintenance');byId('repairSetupPanel').open=true;}else action(name,'start');};
      if(name==='builder'){
        builderButtons.push(button);
        const preflight=document.createElement('div');builderPreflights.push(preflight);readerBuildStatus(preflight,data.reader_build);card.append(preflight);
        const configure=document.createElement('button');configure.type='button';configure.textContent='Review reader settings';configure.onclick=readerSettings;card.append(configure);
      }
      card.append(button);box.append(card);
    }
    const builder=data.services?.builder||{};builderBusy=['queued','running','starting'].includes(builder.state);
    byId('readerBuildStatus').textContent=builder.message||'';
    updateBuilderControls();
    byId('openSolverSettings').hidden=integrated;
  }
  async function action(name,verb){
    if(name==='builder'){
      if(builderActionPending||verb==='start'&&(mode!=='integrated'||!readerReady||builderBusy))return;
      builderActionPending=true;updateBuilderControls();
    }
    const own=++generation;clearTimeout(timer);
    try{
      const data=await api('/api/admin/services/'+name+'/'+verb,{method:'POST',suppressClientError:true,noMutationRefresh:true});
      if(own===generation)render(data);
    }catch(error){
      if(own===generation){
        if(name==='builder')readerCheckUnavailable();
        toast(name==='builder'?'Reader build request could not be completed. Refresh Status to check the configuration and current build.':error.message);
      }
    }
    finally{
      if(name==='builder'){builderActionPending=false;updateBuilderControls();}
      await load({force:true});
    }
  }
  async function load({force=false}={}){
    if(loading&&!force)return loading;
    clearTimeout(timer);
    const own=++generation;
    const request=(async()=>{
      const data=await api('/api/admin/services',{timeoutMs:5000,suppressClientError:true,noMutationRefresh:true});
      if(own!==generation)return;
      render(data);
      if(byId('about')?.classList.contains('active')){
        const jar=await api('/api/admin/extension-jar',{timeoutMs:5000,suppressClientError:true,noMutationRefresh:true});
        if(own!==generation)return;
        byId('downloadJar').disabled=!jar.available;byId('downloadJar').dataset.version=jar.version;
        loadApkDownload();
      }
    })();
    loading=request;
    try{await request;}catch(_error){
      if(own!==generation)return;
      readerCheckUnavailable();updateBuilderControls();
      byId('readerBuildStatus').textContent='Service status could not be refreshed. Use Refresh services to check again.';
    }
    finally{if(loading===request){loading=null;if(visible())timer=setTimeout(load,5000);}}
  }
  byId('refreshInternalServices').onclick=load;
  byId('buildReaderExtensions').onclick=()=>action('builder','start');
  byId('cancelReaderBuild').onclick=()=>action('builder','cancel');
  byId('configureReaderBuild').onclick=readerSettings;
  byId('downloadJar').onclick=async()=>{
    const button=byId('downloadJar');if(button.disabled)return;button.disabled=true;
    const version=button.dataset.version;
    try{
      const blob=await api('/api/admin/extension-jar/download?version='+encodeURIComponent(version),{responseType:'blob',cache:'no-store',timeoutMs:60000,noMutationRefresh:true});
      if(blob.type!=='application/java-archive'||!blob.size)throw new Error('No valid JAR returned. Refresh Status and retry.');
      downloadBlob(blob,`AutoSourcarr-v${version}-Suwayomi.jar`);
    }catch(error){byId('readerBuildStatus').textContent=error.message;}
    finally{button.disabled=false;}
  };
  document.addEventListener('visibilitychange',()=>{if(visible())load();else clearTimeout(timer);});
  window.loadInternalServices=load;
  window.integratedServiceMode=()=>mode;
})();
