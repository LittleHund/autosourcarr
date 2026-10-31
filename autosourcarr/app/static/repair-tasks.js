/* Repair jobs use the shared Tasks snapshot; evidence is loaded only on demand. */
(()=>{
  const panel=document.getElementById('tasks');if(!panel)return;
  const byId=id=>document.getElementById(id);
  const visible=()=>panel.classList.contains('active')&&byId('tasksRepairsPanel')?.classList.contains('active')&&!document.hidden;
  const valid=id=>/^[0-9a-f]{32}$/.test(String(id||''));
  let generation=0,openGeneration=0,read=null,mutation=false;
  function select(section,internal=false){
    const value=section==='repairs'?'repairs':'general';if(!internal)++openGeneration;
    for(const [id,kind] of [['tasksGeneralPanel','general'],['tasksRepairsPanel','repairs']])byId(id)?.classList.toggle('active',value===kind);
    for(const button of document.querySelectorAll?.('[data-task-section]')||[]){button.classList.toggle('active',button.dataset.taskSection===value);button.setAttribute('aria-selected',String(button.dataset.taskSection===value));}
    globalThis.writeLocalUiPreference?.('tasksSubtab',value);
    if(value==='repairs'&&visible())window.loadSourceRepairs?.();
    if(typeof renderTasks==='function')renderTasks();
  }
  const details=byId('sourceRepairDetails'),evidence=byId('sourceRepairEvidence');
  function invalidate(){++generation;read?.abort();read=null;}
  details.addEventListener('toggle',()=>{if(!details.open)invalidate();});
  async function detail(id){
    if(!valid(id))return;
    invalidate();const own=generation,controller=new AbortController();read=controller;
    details.hidden=false;details.open=true;evidence.textContent='Loading repair evidence…';
    details.scrollIntoView?.({block:'nearest'});
    const current=()=>own===generation&&details.open&&visible();
    try{
      const data=await api('/api/admin/source-maintenance/jobs/'+id,{signal:controller.signal,suppressClientError:true});
      if(current())evidence.textContent=JSON.stringify(data,null,2);
    }catch(error){if(current())evidence.textContent='Unable to load repair evidence: '+error.message;}
    finally{if(read===controller)read=null;}
  }
  async function open(id){
    invalidate();const own=++openGeneration;
    if(typeof setWorkbenchRepairLane==='function')setWorkbenchRepairLane('all',true);
    await switchTab('tasks');
    if(own!==openGeneration||!panel.classList.contains('active')||document.hidden)return;
    select('repairs',true);
    if(valid(id))window.AutoSourcarrRepairQueue?.selectRepair?.(id);
    if(valid(id))await detail(id);
    else{details.open=false;details.hidden=true;}
  }
  async function action(id,verb,button,pkg){
    if(mutation)return;
    if(verb==='raws'){await window.openSourceRawInput?.(pkg,valid(id)?id:null);return;}
    if(!valid(id))return;
    if(verb==='evidence'){await detail(id);return;}
    if(!['install','discard','cancel','inputs','code','original-code','fixed','retry','revert'].includes(verb))return;
    const own=generation;mutation=true;button.disabled=true;
    try{
      if(verb==='inputs'||verb==='code'||verb==='original-code'){
        const code=verb!=='inputs';
        const blob=await api('/api/admin/source-maintenance/jobs/'+id+'/'+(code?'code?candidate='+String(verb==='code'):'inputs'),{responseType:'blob',cache:'no-store',timeoutMs:60000,suppressClientError:true});
        if(!blob||blob.type!=='application/zip'||!blob.size||blob.size>32*1024*1024)throw new Error('No valid source input ZIP was returned. Refresh Tasks and retry.');
        downloadBlob(blob,'AutoSourcarr-'+(code?'source-code':'repair-inputs')+'-'+id+'.zip');
        toast(code?'Downloaded retained source code.':'Downloaded the saved APK/JAR inputs. Account, cookies and library files are excluded.');return;
      }
      if(['fixed','retry','revert'].includes(verb)){
        let feedback='';
        if(verb==='retry'){feedback=prompt('What still fails? A public title or error helps the next attempt. Omit passwords, tokens and cookies.','');if(feedback===null)return;}
        const revision=Number(button.dataset.repairRevision),hash=button.dataset.repairHash;
        if(!Number.isSafeInteger(revision)||!/^[0-9a-f]{64}$/.test(hash||''))throw new Error('Refresh this installed repair before acting.');
        await api('/api/admin/source-maintenance/jobs/'+id+'/feedback',{method:'POST',body:JSON.stringify({action:verb,revision,installed_sha256:hash,feedback}),timeoutMs:45000,noMutationRefresh:true});
        toast(verb==='fixed'?'Marked as the last working version. Revert remains available until a later fix starts.':verb==='revert'?'Revert queued; restoring prior code, binary and working confirmation.':'Remaining failure recorded; a new bounded repair was requested.');
        await loadTasks();return;
      }
      let review;
      if(verb==='install'){
        let message='Install this validated source? The source engine restarts briefly. Any existing installed version is retained for recovery.';
        if(Number(button.dataset?.repairAddressCount)>0){
          const job=await api('/api/admin/source-maintenance/jobs/'+id,{suppressClientError:true});
          if(job.id!==id||job.state!=='ready'||!Array.isArray(job.address_migrations)||!job.address_migrations.length||!/^[0-9a-f]{64}$/.test(job.candidate_sha256||''))throw new Error('Address review changed; refresh this repair before installing.');
          const pairs=[...new Set(job.address_migrations.map(row=>new URL(row.previous_url).origin+' → '+new URL(row.new_url).origin))];
          if(pairs.length>8)throw new Error('Too many website address changes for one review; review this package separately.');
          message+='\n\nWebsite addresses will change:\n'+pairs.join('\n')+'\n\nApprove these corresponding website addresses for this candidate?';
          review={approve_address_changes:true,reviewed_candidate_sha256:job.candidate_sha256};
          if(own!==generation||!visible())return;
        }
        if(!confirm(message))return;
      }
      if(verb==='discard'&&!confirm('Discard the validated repair for '+(pkg||'this source')+'? Its candidate, conversation and test evidence are retained. Start a new repair if you reconsider.'))return;
      await api('/api/admin/source-maintenance/jobs/'+id+'/'+verb,{method:'POST',...(review?{body:JSON.stringify(review)}:{}),timeoutMs:25000,noMutationRefresh:true});
      toast(verb==='cancel'?'Repair cancellation requested; waiting for cleanup.':verb==='install'?'Validated repair installation queued.':'Validated repair discarded.');
      await loadTasks();
    }catch(error){toast(error.message);}
    finally{mutation=false;button.disabled=false;window.AutoSourcarrRepairQueue?.refresh?.();}
  }
  panel.addEventListener('click',event=>{
    const button=event.target.closest('[data-repair-action]');
    if(button)action(button.dataset.repairId,button.dataset.repairAction,button,button.dataset.repairPackage).catch(error=>toast(error.message));
  });
  for(const button of document.querySelectorAll?.('[data-task-section]')||[])button.addEventListener('click',()=>select(button.dataset.taskSection));
  const visibilityChanged=()=>{if(!visible()){if(!panel.classList.contains('active')||document.hidden)++openGeneration;if(read)evidence.textContent='Evidence loading stopped. Select Evidence & session to refresh.';invalidate();}};
  document.addEventListener('visibilitychange',visibilityChanged);
  new MutationObserver(visibilityChanged).observe(panel,{attributes:true,attributeFilter:['class']});
  new MutationObserver(visibilityChanged).observe(byId('tasksRepairsPanel'),{attributes:true,attributeFilter:['class']});
  window.openSourceRepairTask=open;
  window.openTasksSection=async section=>{
    invalidate();const own=++openGeneration;await switchTab('tasks');
    if(own===openGeneration&&panel.classList.contains('active')&&!document.hidden)select(section,true);
  };
  select(globalThis.readLocalUiPreference?.('tasksSubtab')||'general');
  window.AutoSourcarrRepairTasks={
    isBusy:()=>mutation,
    selectionChanged(){invalidate();details.open=false;details.hidden=true;evidence.textContent='';},
    originChanged(){++openGeneration;if(!window.AutoSourcarrRepairQueue?.hasSelection?.()){invalidate();details.open=false;details.hidden=true;evidence.textContent='';}},
    actions(task){
      const button=(verb,label)=>`<button type="button" data-repair-id="${valid(task.repair_id)?task.repair_id:''}" data-repair-package="${esc(task.raw_package||task.context||'')}" data-repair-revision="${Number(task.repair_history_revision)||0}" data-repair-hash="${esc(task.installed_sha256||'')}" data-repair-address-count="${Number(task.address_migration_count)||0}" data-repair-action="${verb}">${label}</button>`;
      if(!valid(task.repair_id))return task.raw_package?button('raws','Provide code / Unavailable'):'';
      let result=button('evidence','Evidence & session');
      if(task.repair_state==='awaiting-raws'||task.raw_first&&['blocked','failed','interrupted'].includes(task.repair_state))result+=button('raws','Provide code / Unavailable');
      if(task.repair_raw_available&&!task.repair_is_current)result+=button('original-code','Download acquired code');
      if(task.repair_is_current){
        if(task.repair_can_feedback!==false){
          if(task.repair_state!=='confirmed')result+=button('fixed','Fixed');
          result+=button('retry','Doesn’t work / Retry');
        }
        if(task.repair_can_revert)result+=button('revert','Revert');
        result+=button('code','Download source code');
      }
      if(task.repair_inputs_available)result+=button('inputs','Download source inputs');
      if(!task.stale&&(task.repair_reviewable??(task.repair_state==='ready')))result+=button('install','Install validated repair')+button('discard','Discard validated repair');
      if(!task.stale&&task.cancellable)result+=button('cancel','Cancel repair');
      return result;
    },
    metadata(task){
      if(task.kind==='source-raw-input')return 'Installed source '+(task.source_version||'')+' · original raw code required';
      const when=stamp=>stamp?new Date(stamp*1000).toLocaleString():'';
      return [!task.repair_version||task.repair_version==='unknown'?'Retained repair · version not recorded':'Started in AutoSourcarr '+task.repair_version,
              task.repair_installed_at?'Installed '+when(task.repair_installed_at):'',task.repair_working_at?'Last confirmed working '+when(task.repair_working_at):'',
              task.address_migration_count?String(task.address_migration_count)+' website address changes require installation review':'',
              task.stale?'Repair status unavailable · showing last observed result':''].filter(Boolean).join(' · ');
    },
    renderStatus(snapshot){
      const status=byId('taskRepairStatus'),data=snapshot.source_repairs;
      if(!data){status.textContent='';return;}
      const all=(snapshot.items||[]).filter(row=>['source-repair','source-repair-preparation','source-raw-input','source-raw-acquisition'].includes(row.kind));
      const rows=typeof workbenchRepairTaskItems==='function'?workbenchRepairTaskItems(all):all;
      const counts=state=>rows.filter(row=>row.state===state).length;
      status.textContent=data.stale?'Repair status is unavailable. Retained jobs may be out of date; review Repair tools before retrying.':
        data.isolation?.available===false&&data.isolation.reason!=='not-checked'?data.isolation.message:
        globalThis.AutoSourcarrRepairQueue?'Select a repair to inspect its retained code, evidence and available actions.':
        `${counts('running')} ongoing · ${counts('completed')} completed or ready · ${counts('failed')} failed or blocked. Each result is shown in its section.`;
    }
  };
})();
