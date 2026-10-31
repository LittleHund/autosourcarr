/* Private repair jobs never block catalog tabs; poll only the visible panel. */
(()=>{
  const panel=document.getElementById('sourceMaintenancePanel');
  if(!panel)return;
  const byId=id=>document.getElementById(id);
  let timer=null,loading=false,generation=0,installedLoaded=false;
  let knownIsolation=null,repairSubmitting=false,storageError='';
  const repairBlocked=()=>knownIsolation?.available===false&&!!knownIsolation.reason&&knownIsolation.reason!=='not-checked';
  const aiVisible=()=>document.getElementById('sources')?.classList.contains('active')&&panel.classList.contains('active')&&!document.hidden;
  const workspaceVisible=()=>byId('tasks')?.classList.contains('active')&&byId('tasksRepairsPanel')?.classList.contains('active')&&!document.hidden;
  const visible=()=>aiVisible()||workspaceVisible();
  async function load(){
    if(loading||!visible())return;
    loading=true;clearTimeout(timer);const own=++generation,setupOwn=setupRevision,accountAtStart=controlPaintRevision;
    try{
      const data=await api('/api/v1/source-maintenance',{timeoutMs:25000,suppressClientError:true});
      if(own!==generation||setupOwn!==setupRevision)return;
      const status=byId('sourceRepairStatus');
      storageError=data.storage_error||'';
      byId('repairAIStatus').textContent=!data.configured||data.reachable===false?readinessMessage(data):
        `${data.ai_provider?.provider&&data.ai_provider.provider!=='chatgpt'?data.ai_provider.provider+' · '+data.ai_provider.model:data.auth_saved?'Saved Codex login found':'Sign in through AI setup and account'}`;
      status.textContent=storageError||(!data.configured||data.reachable===false?readinessMessage(data):`${data.ai_provider?.provider&&data.ai_provider.provider!=='chatgpt'?data.ai_provider.provider+' · '+data.ai_provider.model:data.auth_saved?'Saved Codex login found':'Sign in through Setup and account'} · Repair process reachable`);
      byId('sourceRepairFormSection').hidden=!data.configured||data.reachable===false;
      if(!data.configured||data.reachable===false)byId('repairSetupPanel').open=true;
      byId('sourceRepairWatch').checked=!!data.watch?.enabled;byId('sourceRepairWatch').disabled=!data.configured||data.reachable===false;
      await loadSetup(data,accountAtStart);
      byId('rollbackSourceRepair').hidden=data.operation?.state!=='installed'||!!data.operation?.raw_first;
      byId('sourceRepairWatchStatus').textContent=data.watch?.message||'At most one new source per day and one attempt per source each week. New validated repairs install automatically; uncertain website moves wait for review.';
      knownIsolation=knownIsolation||data.isolation;
      byId('sourceRepairForm').querySelector('button[type=submit]').disabled=repairSubmitting||repairBlocked()||!!storageError;
      if(!storageError&&repairBlocked())status.textContent=knownIsolation.message+' Expand Source repair readiness below.';
      if(workspaceVisible()&&data.configured&&data.reachable!==false&&!installedLoaded){
        const inventory=await api('/api/v1/source-maintenance/installed',{timeoutMs:25000,suppressClientError:true});
        const select=byId('sourceRepairPackage'),previous=select.value;select.replaceChildren();
        for(const row of inventory.items||[]){const option=document.createElement('option');option.value=row.pkgName;if(option.dataset)option.dataset.originalVersion=String(row.versionName||'');option.textContent=`${row.name} · ${row.versionName}${row.isObsolete?' · missing from catalog':''}`;select.append(option);}
        if([...select.options].some(o=>o.value===previous))select.value=previous;
        installedLoaded=true;if(!byId('sourceRepairModule').value)select.dispatchEvent(new Event('change'));
      }
    }catch(e){if(own===generation&&setupOwn===setupRevision){if(accountAtStart===controlPaintRevision)unavailableControls(e.message);byId('sourceRepairStatus').textContent=e.message;}}
    finally{loading=false;if(visible())timer=setTimeout(load,controlBusy?2000:10000);}
  }

  let aiProvider='chatgpt';
  let controlBusy=false,controlRequest=false,setupRevision=0,controlOperation=null,loginAttempt=null,loginOpened=null,controlError='';
  let controlTimer=null,controlRead=null,controlPaintRevision=0,controlMutation=0,controlActionType='';
  const controlTarget=()=>byId(controlActionType==='prepare'?'repairToolStatus':'repairControlStatus');
  const secretApi=(path,options={})=>api('/api/admin/source-maintenance'+path,{suppressClientError:true,timeoutMs:25000,...options});
  function clearCode(){byId('repairDeviceLogin').hidden=true;byId('repairDeviceCode').textContent='';byId('repairDeviceExpiry').textContent='';}
  function invalidateControlRead(){clearTimeout(controlTimer);controlRead?.controller.abort();controlRead=null;}
  function scheduleControl(delay=2000){clearTimeout(controlTimer);if(visible()&&!controlRequest)controlTimer=setTimeout(loadControl,delay);}
  function loadControl(){
    if(!visible()||controlRequest)return Promise.resolve();
    if(controlRead)return controlRead.promise;
    const read={controller:new AbortController(),promise:null},own=setupRevision;
    controlRead=read;
    const current=()=>controlRead===read&&own===setupRevision&&visible();
    read.promise=(async()=>{
      try{const state=await secretApi('/control',{signal:read.controller.signal});if(current())renderControl(state);}
      catch(error){if(current())unavailableControls(error.message);}
      finally{if(controlRead===read){controlRead=null;if(visible()&&(controlBusy||loginAttempt))scheduleControl();}}
    })();
    return read.promise;
  }
  function renderControl(data){
    ++controlPaintRevision;
    controlActionType=data.action||controlActionType;
    if(data.isolation){knownIsolation=data.isolation;byId('sourceRepairForm').querySelector('button[type=submit]').disabled=repairSubmitting||repairBlocked()||!!storageError;}
    controlBusy=!!data.busy;
    controlOperation=data.busy&&data.action==='login'?data.operation_id:null;
    if(!data.busy)loginAttempt=null;
    for(const id of ['repairLogin','repairCheckLogin','repairLogout'])byId(id).hidden=aiProvider!=='chatgpt';
    byId('repairTestAI').hidden=aiProvider==='chatgpt';byId('repairTestAI').disabled=!!(controlRequest||controlBusy||data.repair_busy||data.blocked);
    controlTarget().textContent=controlError||data.message||'Choose a repair AI provider, then connect its account.';
    if(data.action!=='prepare')AutoSourcarrRepairLogin.renderDiagnostics(byId('repairAccountDiagnostics'),controlError||aiProvider!=='chatgpt'?null:data);
    if(globalThis.renderRepairIsolation)renderRepairIsolation(byId('repairIsolation'),data.isolation);
    const busy=controlRequest||controlBusy||data.repair_busy||data.blocked;
    byId('repairCleanup').hidden=!data.blocked;byId('repairCleanup').disabled=!!(controlRequest||controlBusy||data.repair_busy);
    for(const id of ['repairPrepare','repairLogin','repairCheckLogin','repairLogout'])byId(id).disabled=!!busy;
    byId('repairCancelLogin').hidden=!AutoSourcarrRepairLogin.running(data);
    byId('repairCancelLogin').disabled=controlRequest||!controlOperation;
    clearCode();
    if(!aiVisible())return;
    if(AutoSourcarrRepairLogin.valid(data)){
      byId('repairDeviceCode').textContent=data.device_code;byId('repairDeviceLogin').hidden=false;
      byId('repairDeviceExpiry').textContent=AutoSourcarrRepairLogin.expiry(data)+' Finish in the OpenAI page, then return here.';
    }else if(AutoSourcarrRepairLogin.running(data)&&!data.device_code&&!data.verification_url){
      byId('repairDeviceLogin').hidden=false;
      byId('repairDeviceExpiry').textContent=AutoSourcarrRepairLogin.hint(data.operation_id===loginAttempt&&loginOpened);
    }
  }
  byId('repairTestAI').addEventListener('click',event=>testAI(event.target));
  async function testAI(b){
    if(controlRequest||controlBusy)return;
    controlRequest=true;b.disabled=true;
    AutoSourcarrRepairLogin.renderDiagnostics(byId('repairAccountDiagnostics'),null);
    try{const result=await secretApi('/provider/test',{method:'POST',timeoutMs:45000});byId('repairControlStatus').textContent=result.message;}
    catch(e){byId('repairControlStatus').textContent=e.message;}
    finally{controlRequest=false;b.disabled=controlBusy;}
  }
  function readinessMessage(data){
    if(data.mode!=='integrated')return 'This installation is not using the bundled repair tools. Rebuild and restart the current AutoSourcarr image; it detects its tools automatically. No separate repair service is needed.';
    const state=data.service||{};
    if(['unavailable','failed','stopped'].includes(state.state))return state.message||'Built-in repair is unavailable. Check Settings → Services for this instance.';
    if(state.state==='starting')return state.message||'Starting this instance’s built-in repair process…';
    return data.message||'The built-in repair process is not ready. Check Settings → Services for this instance.';
  }
  function unavailableControls(message){
    controlBusy=false;clearCode();
    byId('sourceRepairFormSection').hidden=true;
    byId('sourceRepairWatch').disabled=true;
    byId('repairControlStatus').textContent=message;
    byId('repairIsolation').replaceChildren();
    AutoSourcarrRepairLogin.renderDiagnostics(byId('repairAccountDiagnostics'),null);
    byId('repairTestAI').hidden=aiProvider==='chatgpt';
    for(const id of ['repairTestAI','repairPrepare','repairLogin','repairCheckLogin','repairLogout','repairCancelLogin','repairCleanup'])byId(id).disabled=true;
    byId('repairCancelLogin').hidden=true;byId('repairCleanup').hidden=true;
  }
  async function loadSetup(data,accountAtStart){
    if(controlRequest)return;
    aiProvider=data.ai_provider?.provider||'chatgpt';
    if(data.configured&&data.reachable!==false&&data.web_control){
      await loadControl();
    }else if(accountAtStart===controlPaintRevision){
      unavailableControls(data.configured&&data.reachable!==false?'The running repair process needs this release’s bundled tools. Rebuild and restart this AutoSourcarr image.':readinessMessage(data));
    }
  }
  async function accountAction(action){
    if(controlRequest)return;
    if(action==='logout'&&!confirm('Sign out of this instance’s repair account? Installed sources and backups stay intact.'))return;
    const operation=action==='login'?AutoSourcarrRepairLogin.operationId():action==='cancel-login'?controlOperation:null;
    if(action==='cancel-login'&&!operation)return;
    // A popup can hide this page while its POST is pending. Read visibility
    // revisions must not discard the result or cancellation of that mutation.
    controlRequest=true;invalidateControlRead();++setupRevision;const own=++controlMutation,current=()=>own===controlMutation;clearCode();controlError='';controlActionType=action;
    if(action!=='prepare')AutoSourcarrRepairLogin.renderDiagnostics(byId('repairAccountDiagnostics'),null);
    if(action==='login'){loginAttempt=operation;loginOpened=AutoSourcarrRepairLogin.open();byId('repairDeviceLogin').hidden=false;byId('repairDeviceExpiry').textContent=AutoSourcarrRepairLogin.hint(loginOpened);}
    controlTarget().textContent=action==='login'?'Starting ChatGPT sign-in…':action==='cancel-login'?'Cancelling sign-in…':'Starting '+action.replaceAll('-',' ')+'…';
    for(const id of ['repairPrepare','repairLogin','repairCheckLogin','repairLogout','repairCancelLogin','repairCleanup'])byId(id).disabled=true;
    try{const state=await secretApi('/control/'+action,{method:'POST',...(operation?{body:JSON.stringify({operation_id:operation})}:{})});if(current()){controlRequest=false;if(visible())renderControl(state);}}
    catch(e){
      if(current()){
        controlError=e.message;if(visible()){clearCode();controlTarget().textContent=controlError;}
        if(action==='login'&&!(e.status_code>=400&&e.status_code<500)){try{await secretApi('/control/cancel-login',{method:'POST',body:JSON.stringify({operation_id:operation})});}catch(_){} }
        if(current()&&visible()){
          const readRevision=setupRevision;
          try{const state=await secretApi('/control');if(current()&&readRevision===setupRevision&&visible()){controlRequest=false;renderControl(state);}}catch(_){if(current()&&readRevision===setupRevision&&visible())unavailableControls(controlError);}
        }
      }
    }
    finally{if(current()){controlRequest=false;clearTimeout(timer);if(visible()){scheduleControl(0);timer=setTimeout(load,500);}}}
  }
  for(const [id,action] of [['repairPrepare','prepare'],['repairLogin','login'],['repairCheckLogin','login-status'],['repairLogout','logout'],['repairCancelLogin','cancel-login'],['repairCleanup','retry-cleanup']])byId(id).addEventListener('click',()=>accountAction(action));
  byId('openRepairSetup').addEventListener('click',()=>{
    byId('repairSetupPanel').open=true;
    byId('configureRepairAI').focus();
    byId('repairSetupPanel').scrollIntoView({block:'nearest'});
  });
  byId('openRepairAI').addEventListener('click',async()=>{
    await switchTab('sources');setSourceSubtab('maintenance');byId('repairSetupPanel').open=true;
  });
  byId('configureRepairAI').addEventListener('click',()=>{
    if(window.AutoSourcarrSetup?.openRepairAI)window.AutoSourcarrSetup.openRepairAI();
    else toast('Setup is still loading. Refresh this page if it stays unavailable.');
  });
  byId('sourceRepairPackage').addEventListener('change',()=>{
    const parts=byId('sourceRepairPackage').value.replace(/^eu\.kanade\.tachiyomi\.extension\./,'').split('.');
    byId('sourceRepairModule').value=parts.length===2?`src/${parts[0]}/${parts[1]}`:'';
    resetOriginalConfirmation();
  });
  byId('refreshSourceRepairs').addEventListener('click',()=>{installedLoaded=false;resetOriginalConfirmation();if(!controlRequest)controlError='';loadControl();load();});
  byId('rollbackSourceRepair').addEventListener('click',async event=>{
    if(!confirm('Restore the binary that was installed before the last repair? The source engine restarts briefly.'))return;
    event.target.disabled=true;
    try{await api('/api/v1/source-maintenance/rollback',{method:'POST'});await load();}
    catch(e){toast(e.message);}finally{event.target.disabled=false;}
  });
  byId('sourceRepairWatch').addEventListener('change',async event=>{
    const enabled=event.target.checked;event.target.disabled=true;
    try{await api('/api/v1/source-maintenance/watch',{method:'PUT',body:JSON.stringify({enabled})});await load();}
    catch(e){event.target.checked=!enabled;toast(e.message);}finally{event.target.disabled=false;}
  });
  function resetOriginalConfirmation(){
    const field=byId('sourceRepairOriginalConfirmed');if(field)field.checked=false;
  }
  async function attachOriginalApk(){
    const input=byId('sourceRepairOriginalApk'),button=byId('attachSourceRepairOriginalApk'),status=byId('sourceRepairOriginalStatus');
    if(!input||!button||!status||button.disabled)return;
    const select=byId('sourceRepairPackage'),file=input.files?.[0],pkg=select.value,version=select.selectedOptions?.[0]?.dataset?.originalVersion||'';
    if(!pkg){status.textContent='Select the installed source first.';return;}
    if(!file||!/\.apk$/i.test(file.name)||file.size<=0||file.size>32*1024*1024){status.textContent='Choose its original APK, up to 32 MiB.';return;}
    if(!byId('sourceRepairOriginalConfirmed')?.checked){status.textContent='Confirm this is the original APK used for the selected installed version.';return;}
    if(!version){status.textContent='Refresh installed sources before confirming the original APK.';return;}
    button.disabled=true;status.textContent='Retaining original APK for '+pkg+'…';
    try{
      const result=await api('/api/v1/source-maintenance/original-apk/'+encodeURIComponent(pkg),{
        method:'POST',body:file,headers:{'Content-Type':'application/vnd.android.package-archive','X-AutoSourcarr-Original-Apk-Confirmed':'true','X-AutoSourcarr-Original-Apk-Version':version},timeoutMs:45000,noMutationRefresh:true});
      status.textContent=pkg+': '+(result.message||'Original APK retained. Start a new repair to verify the match.');
      if(input.files?.[0]===file){input.value='';resetOriginalConfirmation();}
    }catch(error){status.textContent='Original APK attachment was not confirmed for '+pkg+': '+error.message;}
    finally{button.disabled=false;}
  }
  byId('sourceRepairOriginalApk')?.addEventListener('change',resetOriginalConfirmation);
  byId('attachSourceRepairOriginalApk')?.addEventListener('click',attachOriginalApk);
  byId('sourceRepairForm').addEventListener('submit',async event=>{
    event.preventDefault();if(repairSubmitting||repairBlocked()||storageError)return;
    const submit=event.target.querySelector('button[type=submit]');repairSubmitting=true;submit.disabled=true;
    try{
      const job=await api('/api/admin/source-maintenance/jobs',{method:'POST',timeoutMs:45000,body:JSON.stringify({pkg_name:byId('sourceRepairPackage').value,module:byId('sourceRepairModule').value.trim(),revision:byId('sourceRepairRevision').value.trim(),issue:byId('sourceRepairIssue').value.trim()})});
      await window.openSourceRepairTask(job.id);
    }catch(e){toast(e.message);}finally{repairSubmitting=false;submit.disabled=repairBlocked()||!!storageError;}
  });
  window.loadSourceRepairs=load;
  byId('openSourceRepairTasks').addEventListener('click',()=>window.openSourceRepairTask());
  const visibilityChanged=()=>{
    if(!aiVisible())clearCode();
    if(visible()){
      // Recovery may still own the POST lock. Show its known failure without
      // enabling another mutation or waiting for the cancellation response.
      if(controlError)controlTarget().textContent=controlError;
      loadControl();load();
    }else{++setupRevision;invalidateControlRead();clearTimeout(timer);clearCode();}
  };
  document.addEventListener('visibilitychange',visibilityChanged);
  for(const node of [panel,byId('sources'),byId('tasks'),byId('tasksRepairsPanel')])new MutationObserver(visibilityChanged).observe(node,{attributes:true,attributeFilter:['class']});
  if(visible())load();
})();
