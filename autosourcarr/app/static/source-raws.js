/* Explicit source-code input actions; no acquisition is started by task polling. */
(()=>{
  const panel=document.getElementById('sourceRawInput');if(!panel)return;
  const get=id=>document.getElementById(id),status=get('sourceRawStatus');
  let selected=null,jobId=null,generation=0,busy=false,read=null;
  const path=pkg=>'/api/v1/source-maintenance/raws/'+encodeURIComponent(pkg);
  const buttons=['provideSourceRawProject','fetchSourceRawProject','sourceRawsUnavailable','replaceSourceRawProject','downloadSourceRawProject','discoverSourceRawProject'];
  function paint(row){
    selected=row;status.textContent=row.package+' · '+row.version+' · '+row.state+' · '+row.message+(row.visibility?' · '+row.visibility+' code':'');
    if(get('sourceRawInstalledPackage'))get('sourceRawInstalledPackage').value=row.package;
    get('sourceRawModule').value=row.origin?.module||'.';
    get('sourceRawRepository').value=row.origin?.repository||'';
    get('sourceRawRevision').value=row.origin?.revision||'';
    get('replaceSourceRawProject').hidden=row.state!=='available';get('downloadSourceRawProject').hidden=row.state!=='available';
    for(const id of buttons)get(id).disabled=busy;
    get('provideSourceRawProject').disabled=busy||row.state==='available';
    get('discoverSourceRawProject').disabled=busy||row.state!=='needs-input';
  }
  async function open(pkg,id=null){
    if(busy){toast('Finish the current code submission before opening another source.');return;}
    const own=++generation;jobId=id;selected=null;read?.abort();read=new AbortController();
    get('sourceRawProject').value='';
    get('sourceRawKnownProject').value='';
    panel.hidden=false;panel.open=true;status.textContent='Reading installed source and retained-code request…';
    if(typeof CustomEvent==='function'&&typeof document.dispatchEvent==='function')document.dispatchEvent(new CustomEvent('autosourcarr:raw-selected',{detail:{package:pkg,jobId:id}}));
    for(const name of buttons)get(name).disabled=true;
    panel.scrollIntoView?.({block:'nearest'});
    try{const row=await api(path(pkg),{signal:read.signal,suppressClientError:true});if(own===generation)paint(row);}
    catch(error){if(own===generation)status.textContent=error.message;}
  }
  async function mutate(verb){
    if(busy||!selected)return;
    const row=selected,own=generation,pkg=row.package,file=get('sourceRawProject').files?.[0];
    let options={method:'POST',timeoutMs:45000,noMutationRefresh:true};
    if(verb==='project'){
      if(!file||!/\.zip$/i.test(file.name)||!file.size||file.size>32*1024*1024){toast('Choose a source project ZIP up to32MiB.');return;}
      options={...options,body:file,headers:{'Content-Type':'application/zip','X-Raw-Revision':String(row.revision),'X-Source-Version':row.version,'X-Source-Module':get('sourceRawModule').value.trim()||'.'}};
    }else{
      const body={revision:row.revision};
      if(verb==='origin')body.origin={repository:get('sourceRawRepository').value.trim(),module:get('sourceRawModule').value.trim()||'.',revision:get('sourceRawRevision').value.trim(),ecosystem:row.origin?.ecosystem||'tachiyomi'};
      if(verb==='unavailable'&&jobId)body.job_id=jobId;
      options.body=JSON.stringify(body);
    }
    busy=true;for(const id of buttons)get(id).disabled=true;
    if(get('sourceRawInstalledPackage'))get('sourceRawInstalledPackage').disabled=true;
    status.textContent=verb==='unavailable'?'Requesting isolated reconstruction of the saved installed source…':'Retaining the selected source-code input…';
    try{
      const result=await api(path(pkg)+'/'+verb,options);
      if(own===generation){
        if(result.package)paint(result);
        else{status.textContent=result.message||'Code choice accepted; progress appears in Ongoing repairs.';if(result.id)await window.openSourceRepairTask?.(result.id);}
        if(verb==='project'&&get('sourceRawProject').files?.[0]===file)get('sourceRawProject').value='';
      }
      await loadTasks();
    }catch(error){if(own===generation)status.textContent='Code submission was not confirmed: '+error.message;}
    finally{busy=false;if(get('sourceRawInstalledPackage'))get('sourceRawInstalledPackage').disabled=false;if(own===generation&&selected){for(const id of buttons)get(id).disabled=false;get('provideSourceRawProject').disabled=selected.state==='available';get('discoverSourceRawProject').disabled=selected.state!=='needs-input';}window.AutoSourcarrRepairQueue?.refresh?.();}
  }
  for(const [id,verb] of [['provideSourceRawProject','project'],['fetchSourceRawProject','origin'],['sourceRawsUnavailable','unavailable'],['replaceSourceRawProject','request-input'],['discoverSourceRawProject','discover']])get(id).addEventListener('click',()=>mutate(verb));
  get('sourceRawKnownProject').addEventListener('change',event=>{if(event.target.value){get('sourceRawRepository').value=event.target.value;status.textContent='Keiyoushi project selected as candidate code. Fetch upstream code to archive it; this does not prove the original bytes of a local installation.';}});
  get('downloadSourceRawProject').addEventListener('click',async()=>{
    if(busy||!selected)return;const pkg=selected.package;
    try{const blob=await api(path(pkg)+'/code',{responseType:'blob',timeoutMs:60000,suppressClientError:true});if(!blob.size||blob.size>32*1024*1024)throw new Error('Invalid source-code ZIP');downloadBlob(blob,'AutoSourcarr-source-code-'+pkg+'.zip');}catch(error){toast(error.message);}
  });
  get('openSelectedSourceRaw').addEventListener('click',()=>{const pkg=get('sourceRepairPackage').value;if(pkg)open(pkg);});
  get('openSourceRawArchive')?.addEventListener('click',async()=>{
    if(busy)return;const own=++generation;read?.abort();read=new AbortController();
    panel.hidden=false;panel.open=true;selected=null;status.textContent='Reading installed sources…';
    if(typeof CustomEvent==='function'&&typeof document.dispatchEvent==='function')document.dispatchEvent(new CustomEvent('autosourcarr:raw-selected',{detail:{package:'Installed sources',jobId:null}}));
    for(const id of buttons)get(id).disabled=true;
    try{
      const result=await api('/api/v1/source-maintenance/installed',{signal:read.signal,suppressClientError:true});
      if(own!==generation)return;
      const select=get('sourceRawInstalledPackage');select.replaceChildren();
      for(const row of result.items||[]){const option=document.createElement('option');option.value=row.pkgName;option.textContent=(row.name||row.pkgName)+' · '+row.versionName;select.append(option);}
      if(select.value)await open(select.value);
      else status.textContent='Install a source first; its code archive will be retained here.';
    }catch(error){if(own===generation)status.textContent=error.message;}
  });
  get('sourceRawInstalledPackage')?.addEventListener('change',event=>{if(event.target.value)open(event.target.value);});
  get('refreshSourceRawProject').addEventListener('click',()=>{if(selected)open(selected.package,jobId);});
  window.openSourceRawInput=open;
  window.AutoSourcarrSourceRaw={isBusy:()=>busy};
})();
