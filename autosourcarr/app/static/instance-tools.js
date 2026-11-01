/* Instance operations use one native modal, explicit revisions and owned writes. */
(() => {
  'use strict';
  const byId=id=>document.getElementById(id),dialog=byId('riInstanceDialog'),body=byId('riInstanceBody');
  let generation=0,read=null,busy=false,previousFocus=null,managed=null,profile=null,queue=null,review=null,staged=null;
  const status=message=>{byId('riInstanceStatus').textContent=message;};
  function show(title){
    if(busy)return false;
    read?.abort();read=new AbortController();generation++;managed=profile=queue=review=staged=null;
    if(!dialog.open){previousFocus=document.activeElement;dialog.showModal();}
    byId('riInstanceTitle').textContent=title;body.replaceChildren();status('Loading…');byId('riInstanceClose').focus({preventScroll:true});return true;
  }
  function close(){if(!busy)dialog.close();}
  dialog.addEventListener('cancel',event=>{if(busy)event.preventDefault();});
  dialog.addEventListener('close',()=>{generation++;read?.abort();body.replaceChildren();status('');managed=profile=queue=review=staged=null;previousFocus?.focus?.({preventScroll:true});});
  byId('riInstanceClose').onclick=close;
  async function write(button,operation,after){
    if(busy)return;busy=true;button.disabled=true;byId('riInstanceClose').disabled=true;status('Working…');
    const own=generation;
    try{const result=await operation();if(own===generation&&dialog.open){status('Saved.');if(after)await after(result);}}
    catch(error){if(own===generation&&dialog.open)status(error.message+' Refresh the current state before retrying.');}
    finally{busy=false;button.disabled=false;byId('riInstanceClose').disabled=false;}
  }
  function action(label,fn,options={}){
    const button=document.createElement('button');button.type='button';button.textContent=label;
    if(options.danger)button.className='dangerAction';button.onclick=()=>write(button,fn,options.after);return button;
  }
  const line=(label,value)=>`<div class="riFact"><span>${esc(label)}</span><strong>${esc(value)}</strong></div>`;
  async function manage(id){
    if(!show('Library & serving profile'))return;
    const own=generation;
    try{
      const entry=await api('/api/admin/entries/'+id,{signal:read.signal,suppressClientError:true});
      const controls=await api('/api/admin/library/'+id+'/controls',{signal:read.signal,suppressClientError:true});
      const saved=await api('/api/admin/profiles/'+id,{signal:read.signal,suppressClientError:true});
      if(own!==generation||!dialog.open)return;managed={...controls,id};profile=saved;
      renderManage(entry);
    }catch(error){if(own===generation&&!read.signal.aborted)status(error.message);}
  }
  function renderManage(entry){
    const own=generation,signal=read.signal;
    window.AutoSourcarrManageEntry.render({body,entry,id:managed.id,controls:managed,profile,action,write,status,signal,
      isCurrent:()=>own===generation&&dialog.open&&!signal.aborted});
  }
  async function openIdentities(){
    if(show('Identity review'))await window.AutoSourcarrIdentityReview.mount({body,signal:read.signal,write,status});
  }
  async function openGalleryPolicy(index){
    const group=sourceGroups[Number(index)];if(!group||!show('Gallery matching · '+group.name))return;
    const own=generation,signal=read.signal;
    try{await window.AutoSourcarrGalleryPolicy.mount({body,variants:group.variants,signal,write,status,close});}
    catch(error){if(own===generation&&dialog.open&&!signal.aborted)status(error.message);}
  }
  async function openGallerySource(sid){
    if(!/^-?[0-9]{1,20}$/.test(String(sid))||!show('Gallery matching · Source '+sid))return;
    const own=generation,signal=read.signal;
    try{await window.AutoSourcarrGalleryPolicy.mount({body,variants:[{source_id:String(sid),name:'Source '+sid,lang:'catalogue'}],signal,write,status,close});}
    catch(error){if(own===generation&&dialog.open&&!signal.aborted)status(error.message);}
  }
  async function openSharing(){
    if(show('Database sharing'))await window.AutoSourcarrCatalogSharing.mount({body,signal:read.signal,write,status,close});
  }
  async function openQueue(){
    if(!show('Download queue'))return;
    const own=generation;
    try{const result=await api('/api/admin/downloads',{signal:read.signal,suppressClientError:true});if(own===generation){queue=result;renderQueue();}}
    catch(error){if(own===generation&&!read.signal.aborted)status(error.message);}
  }
  async function openHistory(){
    if(!show('Activity history'))return;
    async function load(offset=0){
      const own=generation,result=await api('/api/admin/activity/history?offset='+offset+'&limit=50',{signal:read.signal,suppressClientError:true});
      if(own!==generation)return;
      body.innerHTML='<p class="muted">'+esc(result.note)+'</p><div class="riTableScroll"><table><thead><tr><th>Task</th><th>Outcome</th><th>Updated</th></tr></thead><tbody>'+result.items.map(row=>`<tr><td><strong>${esc(row.name)}</strong><br>${esc(row.message||row.error||'')}</td><td>${esc(row.interrupted?'Interrupted':row.cancel_requested?'Cancelled':row.state)}</td><td>${esc(new Date(row.updated_at).toLocaleString())}</td></tr>`).join('')+'</tbody></table></div><div id="riHistoryPager" class="riToolActions"></div>';
      if(offset>0)byId('riHistoryPager').append(action('Previous history',async()=>load(Math.max(0,offset-50))));
      if(offset+result.items.length<result.total)byId('riHistoryPager').append(action('More history',async()=>load(offset+50)));
      status(result.total?`${offset+1}–${offset+result.items.length} of ${result.total} retained general tasks`:'No retained general tasks.');
    }
    await load();
  }
  function renderQueue(){
    body.innerHTML='<p class="muted">This is the main Suwayomi download queue. Pause and resume affect its whole downloader. Removing a queued chapter keeps any downloaded files.</p><div class="riToolActions" id="riQueueActions"></div><div class="riTableScroll"><table><thead><tr><th>Manga / chapter</th><th>State</th><th>Progress</th><th>Queue controls</th></tr></thead><tbody id="riQueueRows"></tbody></table></div>';
    const update=async result=>{queue=result;renderQueue();};
    byId('riQueueActions').append(action(queue.state==='STARTED'?'Pause all downloads':'Resume downloads',()=>api('/api/admin/downloads',{method:'POST',body:JSON.stringify({action:queue.state==='STARTED'?'stop':'start',revision:queue.revision}),timeoutMs:45000}),{after:update}));
    byId('riQueueActions').append(action('Refresh queue',()=>api('/api/admin/downloads'),{after:update}));
    for(const row of queue.queue){
      const tr=document.createElement('tr');tr.innerHTML=`<td><strong>${esc(row.manga?.title||'Unknown manga')}</strong><br>${esc(row.chapter?.name||'Chapter')}</td><td>${esc(row.state)}</td><td>${Math.round(Number(row.progress||0)*100)}%</td><td></td>`;
      const controls=tr.lastElementChild;
      if(Number(row.position)>0)controls.append(action('Move to top',()=>api('/api/admin/downloads',{method:'POST',body:JSON.stringify({action:'move',revision:queue.revision,chapter_id:row.chapter.id,to:0}),timeoutMs:45000}),{after:update}));
      controls.append(action('Remove',()=>api('/api/admin/downloads',{method:'POST',body:JSON.stringify({action:'remove',revision:queue.revision,chapter_id:row.chapter.id}),timeoutMs:45000}),{after:update}));
      byId('riQueueRows').append(tr);
    }
    status(`${queue.queue.length} queued chapters · ${queue.state==='STARTED'?'Downloader running':'Downloader paused'}`);
  }
  async function openBackups(){
    if(!show('Instance backup & restore'))return;
    body.innerHTML='<p>Back up both app databases, saved decisions, settings, signing identity and retained instance state. Downloads, logs and replaceable runtime/cache directories are excluded.</p><p class="muted">Creating a consistent snapshot briefly pauses and drains automatic work. Finish manual tasks first. Encrypted exports require this passphrase to restore; the app does not save it. Limits: 128 MiB encrypted archive, 512 MiB selected state.</p><label>Backup passphrase<input id="riBackupPassword" type="password" autocomplete="new-password" minlength="12"/></label><div id="riBackupCreate" class="riToolActions"></div><h3>Saved backups</h3><div id="riBackupList"></div><h3>Verify a restore</h3><label>Encrypted backup<input id="riRestoreFile" type="file" accept=".ascbackup"/></label><label>Restore passphrase<input id="riRestorePassword" type="password" autocomplete="off"/></label><div id="riRestoreActions" class="riToolActions"></div><div id="riRestorePreview"></div>';
    byId('riBackupCreate').append(action('Create encrypted backup',()=>{
      const passphrase=byId('riBackupPassword').value;byId('riBackupPassword').value='';
      return api('/api/admin/instance/backups',{method:'POST',body:JSON.stringify({passphrase}),timeoutMs:150000});
    },{after:()=>refreshBackups()}));
    byId('riRestoreActions').append(action('Check integrity & preview',async()=>{
      const file=byId('riRestoreFile').files[0],password=byId('riRestorePassword').value;byId('riRestorePassword').value='';
      if(!file)throw Error('Select an encrypted backup first');if(file.size>128*1024*1024+64)throw Error('Backup exceeds 128 MiB');
      const encoded=btoa(String.fromCharCode(...new TextEncoder().encode(password)));
      return api('/api/admin/instance/restore/preview',{method:'POST',body:file,headers:{'Content-Type':'application/octet-stream','X-Backup-Passphrase':encoded},timeoutMs:120000});
    },{after:result=>{
      staged=result;byId('riRestorePreview').innerHTML=`<div class="notice"><strong>Integrity verified</strong><p>${Number(result.files)} files · ${Number(result.databases)} databases · version ${esc(result.version)}</p><p>${esc(result.includes.join(' · '))}</p><p>The restore replaces this instance’s selected state and restarts it. A private rollback copy is retained. Your normal container paths must match.</p><label>Type RESTORE THIS INSTANCE<input id="riRestoreConfirmation" autocomplete="off"/></label><div id="riRestoreApply"></div></div>`;
      byId('riRestoreApply').append(action('Restore & restart instance',()=>api('/api/admin/instance/restore/apply',{method:'POST',body:JSON.stringify({id:staged.id,revision:staged.revision,confirmation:byId('riRestoreConfirmation').value})}),{danger:true,after:()=>status('Verified restore staged. The instance is restarting; reload after it returns.')}));
    }}));
    await refreshBackups();
  }
  async function refreshBackups(){
    const own=generation,result=await api('/api/admin/instance/backups',{signal:read.signal,suppressClientError:true});if(own!==generation)return;
    const root=byId('riBackupList');root.replaceChildren();
    if(!result.items.length)root.textContent='No instance backups saved yet.';
    for(const item of result.items){
      const row=document.createElement('div');row.className='riBackupRow';row.innerHTML=`<span>${esc(new Date(item.created_at).toLocaleString())} · ${(item.bytes/1048576).toFixed(1)} MiB · ${Number(item.files)} files</span>`;
      row.append(action('Download encrypted backup',()=>api('/api/admin/instance/backups/'+item.id+'/download',{responseType:'blob'}),{after:blob=>{
        const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download='autosourcarr-'+item.id+'.ascbackup';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
      }}));
      row.append(action('Delete backup',async()=>{
        if(!confirm('Permanently delete this encrypted backup? Download a copy first if you need to retain it.'))return;
        return api('/api/admin/instance/backups/'+item.id,{method:'DELETE'});
      },{danger:true,after:()=>refreshBackups()}));root.append(row);
    }
    status(result.pending_restore?'A verified restore is pending restart.':'Backups are private to this instance.');
  }
  async function openReview(){
    if(!show('Matching evidence & reviewed decisions'))return;
    body.innerHTML='<p class="muted">Review recorded matching, separate-work decisions and automatic merge evidence in one place. The existing match matrix and merge-history inspectors retain full evidence. Bulk actions here remember separate works and preserve all library data.</p><label>Work ID for the audit trail<input id="riAuditEntry" type="number" min="1" placeholder="Leave empty for recent history"/></label><div class="riToolActions" id="riAuditActions"></div><div id="riAuditResults"></div><details><summary>Review several separate-work decisions</summary><label>One exact pair per line<textarea id="riReviewPairs" rows="4" placeholder="12, 34&#10;56, 78"></textarea></label><div class="riToolActions" id="riReviewActions"></div><div id="riReviewPreview"></div></details>';
    byId('riAuditActions').append(action('Load evidence',()=>api('/api/v1/review-workbench'+(byId('riAuditEntry').value?'/entries/'+encodeURIComponent(byId('riAuditEntry').value):'')),{after:renderAudit}));
    byId('riReviewActions').append(action('Preview separate-work decisions',()=>{
      const pairs=byId('riReviewPairs').value.trim().split(/\n/).map(line=>line.split(',').map(v=>Number(v.trim())));
      return api('/api/v1/review-workbench/preview',{method:'POST',body:JSON.stringify({pairs})});
    },{after:result=>{
      review=result;byId('riReviewPreview').innerHTML='<ul>'+result.items.map(item=>`<li>${item.entries.map(e=>esc(e.preferred_title)+' (#'+e.id+')').join(' / ')} — ${esc(item.effect)}</li>`).join('')+'</ul><p class="muted">'+esc(result.undo)+'</p><div id="riReviewApply"></div>';
      byId('riReviewApply').append(action('Confirm reviewed separations',()=>api('/api/v1/review-workbench/apply',{method:'POST',body:JSON.stringify({pairs:review.items.map(item=>item.pair),revision:review.revision})}),{after:async result=>{
        byId('riReviewPreview').textContent=`${result.applied} reviewed decisions saved.`+(result.failure?' '+result.failure:'');renderAudit(await api('/api/v1/review-workbench'));
      }}));
    }}));
    const own=generation;const result=await api('/api/v1/review-workbench',{signal:read.signal,suppressClientError:true});if(own===generation)renderAudit(result);
    if(own===generation)await window.AutoSourcarrArrControls?.review({body,action,write,status,signal:read.signal});
  }
  function renderAudit(result){
    // Phase 8E: one decision log; every row names who made the decision (account, key or automatic).
    const who=actor=>actor?(actor.kind==='automatic'?'Automatic':actor.kind==='key'?'Key '+actor.name:actor.kind==='upgrade'?'Upgrade '+actor.name:actor.name):'Unknown';
    const rows=result.decisions.map(r=>({at:r.at,kind:r.kind,entry:r.entries[0],actor:who(r.actor),detail:r.reason+(r.records.length?' · '+r.records.join(', '):'')+(r.undo_of?' · undoes #'+r.undo_of:'')}));
    byId('riAuditResults').innerHTML='<p class="muted">'+esc(result.note)+'</p><div class="riTableScroll"><table><thead><tr><th>Date</th><th>Decision / evidence</th><th>Who</th><th>Work</th></tr></thead><tbody>'+rows.map(r=>`<tr><td>${esc(new Date(r.at).toLocaleString())}</td><td><strong>${esc(r.kind)}</strong><br>${esc(r.detail)}</td><td>${esc(r.actor)}</td><td>${r.entry?Number(r.entry):'Historical'}</td></tr>`).join('')+'</tbody></table></div><ul class="riReviewBatches">'+result.bulk_actions.map(b=>`<li>Reviewed batch #${Number(b.id)} · ${Number(b.items)} item(s) · ${esc(who(b.actor))} · ${esc(new Date(b.created_at).toLocaleString())}${b.undone_at?' · undone by '+esc(who(b.undone_by))+' '+esc(new Date(b.undone_at).toLocaleString()):''}</li>`).join('')+'</ul><div id="riReviewUndoActions" class="riToolActions"></div>';
    if(result.entry){
      const evidence=document.createElement('details');evidence.open=true;
      evidence.innerHTML=`<summary>Current work evidence: ${esc(result.entry.preferred_title)}</summary><p class="muted">${result.entry.title_manual?'Manual title.':'Automatic title.'} The 0.94 automatic title floor and original-member/edition gates remain enforced.</p><div class="riTableScroll"><table><thead><tr><th>Source record (source / manga)</th></tr></thead><tbody>${(result.entry.records||[]).map(r=>`<tr><td>${esc(String(r).replace(':',' / '))}</td></tr>`).join('')}</tbody></table></div>`;
      byId('riAuditResults').prepend(evidence);
    }
    const merges=document.createElement('div');merges.className='riToolActions';
    for(const event of result.automatic_merges.items)merges.append(action('Inspect merge '+event.id,()=>api('/api/v1/duplicates/history/'+event.id),{after:detail=>{
      const box=document.createElement('details');box.open=true;const label=document.createElement('summary');label.textContent='Recorded merge evidence '+detail.id;const pre=document.createElement('pre');pre.textContent=JSON.stringify(detail,null,2);box.append(label,pre);byId('riAuditResults').append(box);
    }}));
    byId('riAuditResults').append(merges);
    for(const item of result.bulk_actions.filter(a=>!a.undone_at))byId('riReviewUndoActions').append(action('Undo reviewed batch #'+Number(item.id),()=>api('/api/v1/review-workbench/'+encodeURIComponent(item.id)+'/undo',{method:'POST',body:'{}'}),{after:async()=>renderAudit(await api('/api/v1/review-workbench'))}));
    status(`${rows.length} retained audit events shown.`);
  }
  function openTool(kind){
    window.AutoSourcarrReimagined?.closeNavigation?.();
    const operation={queue:openQueue,history:openHistory,backups:openBackups,review:openReview,identities:openIdentities,sharing:openSharing,research:async()=>{
      if(show('Freshness, performance & engine compatibility'))await window.AutoSourcarrArrControls.status({body,action,write,status,signal:read.signal});
    }}[kind];
    if(operation){const pending=operation(),own=generation,signal=read.signal;pending.catch(error=>{if(own===generation&&dialog.open&&!signal.aborted)status(error.message);});}
  }
  // Legacy navigation assigns every nav button a Browse fallback. Tool buttons
  // have their own owner and must not trigger source discovery as a side effect.
  for(const button of document.querySelectorAll('.appNav [data-instance-tool]'))button.onclick=event=>{
    event.preventDefault();event.stopPropagation();openTool(button.dataset.instanceTool);
  };
  document.addEventListener('click',event=>{
    const sharedGallery=event.target.closest('[data-gallery-policy-source]');if(sharedGallery){openGallerySource(sharedGallery.dataset.galleryPolicySource);return;}
    const gallery=event.target.closest('[data-gallery-policy]');if(gallery){openGalleryPolicy(gallery.dataset.galleryPolicy);return;}
    const manga=event.target.closest('[data-manage-entry]');if(manga){manage(Number(manga.dataset.manageEntry));return;}
    const tool=event.target.closest('[data-instance-tool]');if(!tool)return;
    openTool(tool.dataset.instanceTool);
  });
  window.AutoSourcarrInstanceTools={manage};
})();
