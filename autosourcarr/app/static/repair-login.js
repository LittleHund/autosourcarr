/* Account pages open only from an explicit sign-in click, before any HTTP wait. */
(()=>{
  const url='https://auth.openai.com/codex/device';
  globalThis.AutoSourcarrRepairLogin={
    url,
    operationId(){return globalThis.crypto?.randomUUID?.()||'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g,c=>{const r=Math.random()*16|0;return(c==='x'?r:(r&3|8)).toString(16);});},
    open(){
      let popup;
      try{
        popup=globalThis.open?.('about:blank','_blank');
        if(!popup)return false;
        popup.opener=null;popup.location.replace(url);return true;
      }catch(_){try{popup?.close();}catch(_){}return false;}
    },
    running(state){return state.busy&&state.action==='login'&&(state.state===undefined||state.state==='running');},
    valid(state){
      const fresh=Object.hasOwn(state,'expires_in')?Number.isFinite(state.expires_in)&&state.expires_in>0:
        Number.isFinite(state.expires_at)&&state.expires_at*1000>Date.now();
      return !!(this.running(state)&&state.verification_url===url&&
        /^[A-Z0-9]{4,8}-[A-Z0-9]{4,8}$/.test(state.device_code||'')&&
        fresh);
    },
    expiry(state){
      if(Number.isFinite(state.expires_in)){const minutes=Math.max(1,Math.ceil(state.expires_in/60));return 'Code expires in about '+minutes+' minute'+(minutes===1?'':'s')+'.';}
      return 'Code expires at '+new Date(state.expires_at*1000).toLocaleTimeString()+'.';
    },
    hint(opened){
      return opened?'The OpenAI sign-in page opened in a new tab. Waiting for the one-time code… Return here to copy it.':
        'Your browser did not open a tab. Use Open sign-in page below. Waiting for the one-time code…';
    },
    resourceDetails(value){
      if(!value||typeof value!=='object'||Array.isArray(value))return null;
      const limits=['nproc_soft','nproc_hard','address_space_soft_bytes','address_space_hard_bytes',
        'open_files_soft','open_files_hard','cgroup_pids_max','cgroup_memory_max_bytes'];
      const safe={};
      for(const key of [...limits,'cgroup_pids_current','cgroup_memory_current_bytes']){
        const item=Object.hasOwn(value,key)?value[key]:undefined;
        if(typeof item!=='string')continue;
        const numeric=/^(?:0|[1-9][0-9]{0,19})$/.test(item)&&(item.length<20||item<='18446744073709551615');
        if(numeric||item==='unknown'||limits.includes(key)&&item==='unlimited')safe[key]=item;
      }
      if(Object.hasOwn(value,'cgroup_scope')&&['unknown','visible-v2-only'].includes(value.cgroup_scope))safe.cgroup_scope=value.cgroup_scope;
      if(Object.hasOwn(value,'ancestor_limits')&&value.ancestor_limits==='unknown')safe.ancestor_limits='unknown';
      return Object.keys(safe).length?safe:null;
    },
    diagnostics(state){
      if(state?.state!=='failed'||!['login','login-status','logout'].includes(state.action))return null;
      const data=state.diagnostics;
      const reasons=['unclassified','device-login-disabled','device-endpoint-unavailable','rate-limited','request-denied','service-unavailable','http-error','tls-error','dns-error','network-error','invalid-response','tool-startup','resource-limit','configuration-error','timed-out','process-signal','no-code-timeout','runtime-error','credential-save'];
      const phases=['starting','requesting-code','authorizing','checking-account','signing-out','saving-account'];
      if(!data||!reasons.includes(data.reason)||!phases.includes(data.phase)||typeof data.code_received!=='boolean')return null;
      const safe={reason:data.reason,phase:data.phase,
        exit_code:Number.isInteger(data.exit_code)&&data.exit_code>=-128&&data.exit_code<=255?data.exit_code:null,
        http_status:Number.isInteger(data.http_status)&&data.http_status>=400&&data.http_status<=599?data.http_status:null,
        code_received:data.code_received};
      if(['exec','parent'].includes(data.launcher_stage))safe.launcher_stage=data.launcher_stage;
      if(Number.isInteger(data.os_error)&&data.os_error>=0&&data.os_error<=4095)safe.os_error=data.os_error;
      const resources=this.resourceDetails(data.resources);
      if(resources)safe.resources=resources;
      return safe;
    },
    renderDiagnostics(container,state){
      if(!container)return;
      container.replaceChildren();
      const data=this.diagnostics(state);
      if(!data)return;
      const details=document.createElement('details');details.open=true;
      const summary=document.createElement('summary');summary.textContent='Account error details';
      const evidence=document.createElement('pre'),text=JSON.stringify(data,null,2);evidence.textContent=text;
      const copy=document.createElement('button');copy.type='button';copy.textContent='Copy error details';
      copy.addEventListener('click',async()=>{
        try{await globalThis.navigator.clipboard.writeText(text);copy.textContent='Copied';}
        catch(_){copy.textContent='Select the details above and copy them';}
      });
      details.append(summary,evidence,copy);container.append(details);
    },
  };
})();
