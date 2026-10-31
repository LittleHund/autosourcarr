/* Report a real preflight failure separately from a check that has not run. */
(()=>{
  globalThis.renderRepairIsolation=(target,data)=>{
    target.replaceChildren();if(!data)return;
    if(data.available){
      if(!data.resources)return;
      const detail=document.createElement('details'),title=document.createElement('summary');
      title.textContent='Repair resource budgets';detail.append(title);
      const note=document.createElement('p');note.textContent='Repair processes have inherited limits, with memory, process and disk monitoring. Aggregate limits are sampled and can briefly overshoot; they are not a reserved memory allocation.';detail.append(note);
      const observed=document.createElement('pre');observed.textContent=JSON.stringify(data.resources,null,2);detail.append(observed);target.append(detail);return;
    }
    if(data.reason==='not-checked'||(!data.reason&&!data.details&&!data.checks)){
      const note=document.createElement('p');note.className='muted';note.textContent=data.message||'Use Check repair tools to test readiness for this instance.';target.append(note);return;
    }
    const detail=document.createElement('details'),title=document.createElement('summary');
    title.textContent='Repair readiness diagnostics';detail.append(title);
    const message=document.createElement('p');message.textContent=data.message||'The repair readiness check failed.';detail.append(message);
    const restricted=['container-policy','host-userns','namespace-denied','mount-denied'].includes(data.reason);
    detail.open=restricted;
    const note=document.createElement('p');note.textContent=restricted?'ChatGPT sign-in, saved-account checks and API connection tests work without this repair check or Compose changes. Running edited source code still requires isolation. This host blocks that execution; the app cannot grant itself host permissions. Normal library features remain available.':'Check the reported tool or runtime failure, then use Check repair tools again. ChatGPT account setup is independent of source-repair readiness. Normal library features remain available.';detail.append(note);
    const advanced=document.createElement('details'),advancedTitle=document.createElement('summary');advancedTitle.textContent='Host permissions for running source repairs';advanced.append(advancedTitle);
    if(Array.isArray(data.steps)){
      const steps=document.createElement('ol');for(const text of data.steps.slice(0,6)){const item=document.createElement('li');item.textContent=String(text);steps.append(item);}advanced.append(steps);
    }
    if(data.host_setup){
      const button=document.createElement('button');button.type='button';button.textContent='Download host policies and instructions';
      const status=document.createElement('p');status.setAttribute('role','status');
      button.addEventListener('click',async()=>{
        button.disabled=true;status.textContent='Preparing host policy download…';
        try{
          const result=await api('/api/admin/source-maintenance/host-setup',{suppressClientError:true,responseType:'blob'});
          const url=URL.createObjectURL(result),link=document.createElement('a');
          link.href=url;link.download='autosourcarr-repair-host-setup.zip';document.body.append(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);
          status.textContent='Open README.md in the download. Apply its host steps, Save/Redeploy, then check repair tools again.';
        }catch(error){status.textContent=error.message||'Host policy download failed.';}
        finally{button.disabled=false;}
      });advanced.append(button,status);
    }
    if(data.host_setup||Array.isArray(data.steps)&&data.steps.length)detail.append(advanced);
    const observed=document.createElement('pre');observed.textContent=JSON.stringify({reason:data.reason,exit_code:data.exit_code,checks:data.checks,details:data.details},null,2);detail.append(observed);target.append(detail);
  };
})();
