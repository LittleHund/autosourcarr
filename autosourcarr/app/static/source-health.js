/* Source samples are explicit Tasks; this panel only reads recorded observations. */
(()=>{
  const byId=id=>document.getElementById(id),panel=byId('sourceHealthPanel');if(!panel)return;
  const visible=()=>!document.hidden&&panel.classList.contains('active')&&byId('sources').classList.contains('active');
  const labels={unchecked:'Never checked',working:'Working (sample)',failed:'Check failed',inconclusive:'Inconclusive',
    deferred:'Deferred · local capacity',verification:'Needs verification',rate_limited:'Rate limited',cancelled:'Cancelled'};
  let data=null,read=null,generation=0,busy=false,timer=null,selected=null,mutationError='';
  const date=value=>value?new Date(Number(value)*1000).toLocaleString():'Not recorded';
  const installed=row=>row.available===true||row.available===1||row.available==='1';
  const status=text=>{byId('sourceHealthStatus').textContent=text;};
  function invalidate(){++generation;read?.controller.abort();read=null;clearTimeout(timer);}
  function controls(){
    byId('sourceHealthDaily').disabled=busy;
    byId('checkEnabledSourceHealth').disabled=busy||!!data?.active?.manual;
    byId('refreshSourceHealth').disabled=busy;
  }
  function evidence(id,scroll=false){
    selected=String(id);const row=data?.items?.find(item=>installed(item)&&String(item.source_id)===selected);
    const target=byId('sourceHealthDetails');
    if(!row){selected=null;target.hidden=true;target.open=false;byId('sourceHealthEvidence').innerHTML='';return;}
    target.hidden=false;target.open=true;
    const checks=(row.checks||[]).map(check=>`<li>${esc(check.stage)}: ${esc(check.state)}</li>`).join('');
    const history=(row.history||[]).slice().reverse().map(item=>`<li>${esc(date(item.at))} · ${esc(labels[item.state]||item.state)} · ${esc(item.origin)} · ${esc(item.message)}</li>`).join('');
    byId('sourceHealthEvidence').innerHTML=`<h4>${esc(row.name)} · ${esc(row.lang)}</h4><p>${esc(row.message||'No recorded check')}</p><p>Version at last check: ${esc(row.checked_version||'Unknown')} · Package: ${esc(row.package_name||'Unknown')}</p><ul>${checks||'<li>No samples recorded</li>'}</ul><h4>Recent checks</h4><ul>${history||'<li>No check history</li>'}</ul>`;
    if(scroll)target.scrollIntoView?.({block:'nearest'});
  }
  function render(){
    if(!data)return;
    byId('sourceHealthDaily').checked=!!data.daily?.enabled;controls();
    const filter=byId('sourceHealthFilter').value,checking=new Set(data.checking||[]),manual=!!data.active?.manual;
    const rows=(data.items||[]).filter(installed).filter(row=>filter==='all'||(filter==='working'?row.state==='working'&&!row.stale&&!row.needs_recheck:row.state!=='working'||row.stale||row.needs_recheck));
    byId('sourceHealthTable').querySelector('tbody').innerHTML=rows.map(row=>{
      const id=esc(row.source_id),label=!row.available?'Not installed':checking.has(row.source_id)?'Checking…':row.needs_recheck?'Recheck after source change':labels[row.state]||'Unknown';
      const tone=row.available&&!row.needs_recheck&&!row.stale&&!checking.has(row.source_id)?row.state:'unchecked';
      const repair=/^[0-9a-f]{32}$/.test(row.repair_job_id||'')?`<button data-health-repair="${esc(row.repair_job_id)}" type="button">View last repair</button>`:'';
      return `<tr><td><strong>${esc(row.name)}</strong><br>${esc(row.lang)}<br><span class="muted">Checked version: ${esc(row.checked_version||'Unknown')}</span></td><td><span class="sourceHealthState ${esc(tone||'unchecked')}">${esc(label)}</span>${row.stale?'<br><span class="muted">Over 24 hours old</span>':''}<p>${esc(row.message||'')}</p></td><td>${esc(date(row.checked_at))}</td><td>${esc(date(row.last_success_at))}</td><td>${esc(date(row.last_repaired_at))}<br><span class="muted">Confirmed working: ${esc(date(row.last_confirmed_working_at))}</span><br><span class="muted">Current repair: ${esc(row.repair_confirmation||"unknown")}</span></td><td><div class="toolbar wrap"><button data-health-check="${id}" type="button" ${busy||manual||checking.has(row.source_id)||!row.available?'disabled':''}>Check now</button><button data-health-evidence="${id}" type="button">Evidence</button>${repair}${row.state==='verification'?'<button data-health-attention type="button">Needs attention</button>':''}</div></td></tr>`;
    }).join('')||'<tr><td colspan="6">'+(filter!=='all'?'No installed sources match this health filter. Clear view filters to show all installed sources.':'No installed sources in the loaded snapshot. Refresh results or sync installed sources if this inventory is outdated.')+'</td></tr>';
    const active=Object.keys(data.active||{}).length;
    status((mutationError?mutationError+' · ':'')+`${data.daily?.enabled?'Daily checks on':'Daily checks off'}${data.daily?.enabled&&data.daily.next_due_at!==null?' · Next due: '+(data.daily.next_due_at>Date.now()/1000?date(data.daily.next_due_at):'when idle'):''}${active?' · '+active+' check task(s) active':''}`);
    if(selected&&(byId('sourceHealthDetails').open||!(data.items||[]).some(row=>installed(row)&&String(row.source_id)===selected)))evidence(selected);
  }
  async function load(){
    if(!visible()||busy)return;
    if(read)return read.promise;
    clearTimeout(timer);const own=generation,request={controller:new AbortController(),promise:null};read=request;
    request.promise=(async()=>{
      try{
        const snapshot=await api('/api/admin/source-health',{signal:request.controller.signal,suppressClientError:true,timeoutMs:12000});
        if(read===request&&own===generation&&visible()&&!busy){data=snapshot;render();}
      }catch(error){if(read===request&&own===generation&&visible())status('Unable to refresh source health: '+error.message+' · last recorded results retained');}
      finally{if(read===request){read=null;if(visible())timer=setTimeout(load,10000);}}
    })();return request.promise;
  }
  async function mutate(path,body){
    if(busy)return;busy=true;mutationError='';invalidate();controls();status('Updating source checks…');
    try{
      await api('/api/admin/source-health/'+path,{method:path==='daily'?'PUT':'POST',body:JSON.stringify(body),timeoutMs:15000,noMutationRefresh:true});
      if(path==='check')toast('Source check queued. Follow its progress or cancel it in Tasks.');
      loadTasks().catch(()=>{});
    }catch(error){mutationError=error.message;status(mutationError);}
    finally{busy=false;controls();if(visible())load();}
  }
  byId('sourceHealthDaily').addEventListener('change',event=>mutate('daily',{enabled:event.target.checked}));
  byId('checkEnabledSourceHealth').addEventListener('click',()=>mutate('check',{all_enabled:true}));
  byId('refreshSourceHealth').addEventListener('click',()=>{mutationError='';invalidate();load();});
  byId('sourceHealthTasks').addEventListener('click',()=>window.openTasksSection?.('general'));
  byId('sourceHealthFilter').addEventListener('change',render);
  panel.addEventListener('click',event=>{
    const button=event.target.closest('button');if(!button||button.disabled)return;
    if(button.dataset.healthCheck)mutate('check',{source_ids:[button.dataset.healthCheck]});
    if(button.dataset.healthEvidence)evidence(button.dataset.healthEvidence,true);
    if(button.dataset.healthRepair)window.openSourceRepairTask?.(button.dataset.healthRepair);
    if('healthAttention' in button.dataset)switchTab('attention');
  });
  const changed=()=>{invalidate();if(visible())load();};
  document.addEventListener('visibilitychange',changed);
  for(const node of [panel,byId('sources')])new MutationObserver(changed).observe(node,{attributes:true,attributeFilter:['class']});
  window.loadSourceHealth=load;
  if(visible())load();
})();
