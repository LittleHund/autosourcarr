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
      const entry=await api('/api/v1/catalogue/entries/'+id,{signal:read.signal,suppressClientError:true});
      const controls=await api('/api/v1/library/'+id+'/controls',{signal:read.signal,suppressClientError:true});
      const saved=await api('/api/v1/profiles/'+id,{signal:read.signal,suppressClientError:true});
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
    try{const result=await api('/api/v1/downloads',{signal:read.signal,suppressClientError:true});if(own===generation){queue=result;renderQueue();}}
    catch(error){if(own===generation&&!read.signal.aborted)status(error.message);}
  }
  async function openHistory(){
    if(!show('Activity history'))return;
    // PERF-5: download events are a keyset page (downloads_before = next_before); older pages append below the rows
    // already shown, so nothing above moves (UI-12). Paging the task list keeps the download pages already loaded.
    let downloads=null;
    const downloadRow=row=>`<tr><td>${esc(row.event)}</td><td>Entry ${Number(row.entry_id)} · chapter ${Number(row.chapter_id)}${row.detail?'<br><span class="muted">'+esc(row.detail)+'</span>':''}</td><td>${esc(row.created_at?new Date(row.created_at).toLocaleString():'')}</td></tr>`;
    const downloadPage=page=>({items:[...(Array.isArray(page?.items)?page.items:[])],next:page?.has_more&&page.next_before!=null?String(page.next_before):null});
    function downloadPager(offset){
      const pager=byId('riDownloadPager'),rows=byId('riDownloadRows');if(!pager||!rows)return;
      pager.replaceChildren();if(!downloads.next)return;
      pager.append(action('Older downloads',()=>api('/api/v1/activity/history?offset='+offset+'&limit=50&downloads_before='+encodeURIComponent(downloads.next),{signal:read.signal,suppressClientError:true}),{after:page=>{
        const more=downloadPage(page.downloads);downloads.items.push(...more.items);downloads.next=more.next;
        if(more.items.length)rows.insertAdjacentHTML('beforeend',more.items.map(downloadRow).join(''));
        downloadPager(offset);
      }}));
    }
    async function load(offset=0){
      const own=generation,result=await api('/api/v1/activity/history?offset='+offset+'&limit=50',{signal:read.signal,suppressClientError:true});
      if(own!==generation)return;
      if(!Array.isArray(result.items))result.items=[];
      if(!downloads)downloads=downloadPage(result.downloads);
      body.innerHTML='<p class="muted">'+esc(result.note)+'</p><div class="riTableScroll"><table><thead><tr><th>Task</th><th>Outcome</th><th>Updated</th></tr></thead><tbody>'+result.items.map(row=>`<tr><td><strong>${esc(row.name)}</strong><br>${esc(row.message||row.error||'')}</td><td>${esc(row.interrupted?'Interrupted':row.cancel_requested?'Cancelled':row.state)}</td><td>${esc(new Date(row.updated_at).toLocaleString())}</td></tr>`).join('')+'</tbody></table></div><div id="riHistoryPager" class="riToolActions"></div>'
        +'<h3>Downloads</h3><div class="riTableScroll"><table><thead><tr><th>Event</th><th>Chapter</th><th>When</th></tr></thead><tbody id="riDownloadRows">'
        +(downloads.items.map(downloadRow).join('')||'<tr><td colspan="3">No download events retained.</td></tr>')+'</tbody></table></div><div id="riDownloadPager" class="riToolActions"></div>';
      downloadPager(offset);
      if(offset>0)byId('riHistoryPager').append(action('Previous history',async()=>load(Math.max(0,offset-50))));
      if(offset+result.items.length<result.total)byId('riHistoryPager').append(action('More history',async()=>load(offset+50)));
      status(result.total?`${offset+1}–${offset+result.items.length} of ${result.total} retained general tasks`:'No retained general tasks.');
    }
    await load();
  }
  function renderQueue(){
    body.innerHTML='<p class="muted">This is AutoSourcarr\'s own download queue: chapters are saved as CBZ files in the library folder'+(queue.library_root?' ('+esc(queue.library_root)+')':'')+'. Each source downloads one chapter at a time. Pause and resume affect the whole downloader; removing a queued chapter keeps any downloaded files.</p><div class="riToolActions" id="riQueueActions"></div><div class="riTableScroll"><table><thead><tr><th>Manga / chapter</th><th>State</th><th>Progress</th><th>Queue controls</th></tr></thead><tbody id="riQueueRows"></tbody></table></div>';
    const update=async result=>{queue=result;renderQueue();};
    byId('riQueueActions').append(action(queue.state==='STARTED'?'Pause all downloads':'Resume downloads',()=>api('/api/v1/downloads',{method:'POST',body:JSON.stringify({action:queue.state==='STARTED'?'stop':'start',revision:queue.revision}),timeoutMs:45000}),{after:update}));
    byId('riQueueActions').append(action('Refresh queue',()=>api('/api/v1/downloads'),{after:update}));
    for(const row of queue.queue){
      const tr=document.createElement('tr');tr.innerHTML=`<td><strong>${esc(row.manga?.title||'Unknown manga')}</strong><br>${esc(row.chapter?.name||'Chapter')}${row.source?.name?' · '+esc(row.source.name):''}</td><td>${esc(row.state)}${row.error?'<br><span class="muted">'+esc(row.error)+'</span>':''}</td><td>${Math.round(Number(row.progress||0)*100)}%</td><td></td>`;
      const controls=tr.lastElementChild;
      if(row.state==='ERROR')controls.append(action('Retry',()=>api('/api/v1/downloads',{method:'POST',body:JSON.stringify({action:'retry',revision:queue.revision,chapter_id:row.chapter.id}),timeoutMs:45000}),{after:update}));
      if(Number(row.position)>0)controls.append(action('Move to top',()=>api('/api/v1/downloads',{method:'POST',body:JSON.stringify({action:'move',revision:queue.revision,chapter_id:row.chapter.id,to:0}),timeoutMs:45000}),{after:update}));
      controls.append(action('Remove',()=>api('/api/v1/downloads',{method:'POST',body:JSON.stringify({action:'remove',revision:queue.revision,chapter_id:row.chapter.id}),timeoutMs:45000}),{after:update}));
      byId('riQueueRows').append(tr);
    }
    status(`${queue.queue.length} queued chapters · ${queue.state==='STARTED'?'Downloader running':'Downloader paused'}`);
  }
  // Phase 12 (SET-8/SET-9): backups live on /api/v1 through the generated client. The file upload (multipart, the
  // passphrase is a form field, never a header) uses api() directly; downloads are plain links (streamed by the server).
  const v1=()=>(window.AutoSourcarrApi||globalThis.AutoSourcarrApi).createClient({request:(path,options)=>api(path,options)});
  const when=value=>value?new Date(value).toLocaleString():'unknown time';
  const kindLabel=kind=>({manual:'Manual',automatic:'Automatic (before update)','pre-restore':'Before a restore'}[kind]||kind);
  const headerToken=()=>{try{return typeof token==='string'&&token?token:'';}catch(_){return '';}};
  function downloadLink(item,label='Download'){
    const url='/api/v1/backups/'+encodeURIComponent(item.id)+'/download';
    if(headerToken())return action(label,()=>api(url,{responseType:'blob'}),{after:blob=>{
      // An admin token sent as a header cannot ride on a plain link; only then the browser buffers the file.
      const href=URL.createObjectURL(blob),a=document.createElement('a');a.href=href;a.download='autosourcarr-'+item.id+'.ascbackup';a.click();setTimeout(()=>URL.revokeObjectURL(href),1000);
    }});
    const a=document.createElement('a');a.href=url;a.download='autosourcarr-'+item.id+'.ascbackup';a.textContent=label;a.className='riButtonLink';return a;
  }
  function multipart(file,passphrase){
    const boundary='----autosourcarr'+Math.random().toString(16).slice(2)+Date.now().toString(16);
    const parts=[];
    if(passphrase)parts.push('--'+boundary+'\r\nContent-Disposition: form-data; name="passphrase"\r\n\r\n',passphrase,'\r\n');
    parts.push('--'+boundary+'\r\nContent-Disposition: form-data; name="file"; filename="backup.ascbackup"\r\nContent-Type: application/octet-stream\r\n\r\n',file,'\r\n--'+boundary+'--\r\n');
    return {body:new Blob(parts),type:'multipart/form-data; boundary='+boundary};
  }
  async function openBackups(){
    if(!show('Backup & restore'))return;
    body.innerHTML='<div id="riBackupNotice"></div><p>A backup is one file with both databases (catalogue, manual decisions, chapter provenance), the settings without secrets, matching/serving/source profiles and the portable catalogue export. It can also be shared: secrets, accounts, keys, cookies and downloads are never included.</p><p class="muted">The passphrase is optional (12 to 1024 characters) and never saved; without it the file is not encrypted. An automatic backup is taken before every update or database migration. A restore is applied at the next start, before the databases open.</p><label>Backup passphrase (optional)<input id="riBackupPassword" type="password" autocomplete="new-password" minlength="12"/></label><div id="riBackupCreate" class="riToolActions"></div><h3>Saved backups</h3><div id="riBackupList"></div><h3>Restore from a file</h3><label>Backup file<input id="riRestoreFile" type="file" accept=".ascbackup"/></label><label>Its passphrase (if protected)<input id="riRestorePassword" type="password" autocomplete="off"/></label><div id="riRestoreActions" class="riToolActions"></div><div id="riRestorePreview"></div>';
    byId('riBackupCreate').append(action('Create backup',()=>{
      const passphrase=byId('riBackupPassword').value;byId('riBackupPassword').value='';
      return v1().createBackup(passphrase?{passphrase}:{}, {timeoutMs:30000});
    },{after:result=>{status('Backup queued in Tasks ('+result.task_id+'). Refresh this list when it is done.');return refreshBackups();}}));
    byId('riRestoreActions').append(action('Check backup & preview restore',async()=>{
      const file=byId('riRestoreFile').files[0],password=byId('riRestorePassword').value;byId('riRestorePassword').value='';
      if(!file)throw Error('Select a backup file first');
      const upload=multipart(file,password);
      return api('/api/v1/backups/restore/preview',{method:'POST',body:upload.body,headers:{'Content-Type':upload.type},timeoutMs:3600000});
    },{after:renderRestorePreview}));
    await refreshBackups();
  }
  function renderRestorePreview(result){
    staged=result;
    const rows=(result.databases||[]).flatMap(db=>db.tables.map(t=>`<tr><td>${esc(db.name)}</td><td>${esc(t.table)}</td><td>${t.current??'—'}</td><td>${t.backup??'—'}</td></tr>`)).join('');
    const list=(title,items)=>items&&items.length?`<p><strong>${esc(title)}</strong>: ${items.map(esc).join(', ')}</p>`:'';
    const settings=result.settings||{};
    byId('riRestorePreview').innerHTML=`<div class="notice"><strong>Backup checked</strong><p>From ${esc(when(result.backup?.created_at))} · AutoSourcarr ${esc(result.backup?.app_version)} · format ${Number(result.backup?.format_version)}</p><p>${esc(result.version_check?.message)}</p>${result.decisions_warning?'<p><strong>'+esc(result.decisions_warning)+'</strong></p>':''}<div class="riTableScroll"><table><thead><tr><th>Database</th><th>Table</th><th>Now</th><th>Backup</th></tr></thead><tbody>${rows}</tbody></table></div>${settings.included?`<p>${Number(settings.changed.length)} setting values change.</p>`:'<p>The backup has no settings; current settings stay.</p>'}${list('Secrets kept from this instance',settings.secrets_kept_from_this_instance)}${list('Secrets to enter again after the restore',settings.secrets_to_enter_again)}<p class="muted">${esc(result.effect)}</p><label>Type RESTORE THIS INSTANCE<input id="riRestoreConfirmation" autocomplete="off"/></label><div id="riRestoreApply"></div></div>`;
    byId('riRestoreApply').append(action('Restore at the next start',()=>{
      if(byId('riRestoreConfirmation').value!=='RESTORE THIS INSTANCE')throw Error('Type RESTORE THIS INSTANCE to confirm.');
      return v1().applyBackupRestore({stage_id:staged.stage_id,preview_token:staged.preview_token,confirm:true});
    },{danger:true,after:result=>{status(result.message);return refreshBackups();}}));
  }
  function previewSaved(item,label){
    return action(label||(item.protected?'Restore (needs passphrase)':'Restore'),()=>{
      const passphrase=item.protected?byId('riRestorePassword').value:'';if(item.protected)byId('riRestorePassword').value='';
      return v1().previewBackupRestore(passphrase?{backup_id:item.id,passphrase}:{backup_id:item.id},{timeoutMs:600000});
    },{after:renderRestorePreview});
  }
  function noticeBox(title,lines){
    const box=document.createElement('div');box.className='notice';
    box.innerHTML=`<strong>${esc(title)}</strong>`+lines.filter(Boolean).map(line=>'<p>'+esc(line)+'</p>').join('');
    const actions=document.createElement('div');actions.className='riToolActions';box.append(actions);return {box,actions};
  }
  function renderNotices(result){
    const root=byId('riBackupNotice');root.replaceChildren();
    const safe=result.safe_mode;
    if(safe){
      const {box,actions}=noticeBox(safe.status==='rolled-back'?'Update rolled back — safe mode':'Update paused — safe mode',[safe.message,safe.previous_version_hint,safe.error,safe.retry_pending?'A retry is armed: restart AutoSourcarr to run the update again.':null]);
      if(!safe.retry_pending&&(safe.actions||[]).includes('retry'))actions.append(action('Retry the update',()=>{
        if(!confirm('Take a fresh backup now and run the update again at the next start?'))return;
        return v1().retryBackupUpdate({confirm:true});
      },{after:result=>{if(result)status(result.message);return refreshBackups();}}));
      const named=result.items.find(item=>item.id===safe.backup_id);
      if((safe.actions||[]).includes('restore')&&named)actions.append(previewSaved(named,'Restore the backup from '+when(named.created_at)));
      if((safe.actions||[]).includes('migrate-without-backup')&&safe.preview_token)actions.append(action('Migrate without a backup',()=>{
        if(!confirm('Migrate at the next start WITHOUT a backup? If the migration fails, there is nothing to roll back to.'))return;
        return v1().migrateWithoutBackup({confirm:true,preview_token:safe.preview_token});
      },{danger:true,after:result=>{if(result)status(result.message);return refreshBackups();}}));
      root.append(box);
    }
    if(result.restore_pending){
      const {box,actions}=noticeBox('Restore armed',['The backup from '+when(result.restore_pending.backup_created_at)+' is restored at the next start. Until then changes are refused.']);
      actions.append(action('Cancel the restore',()=>v1().cancelBackupRestore(),{after:()=>refreshBackups()}));root.append(box);
    }
    if(result.restore){
      const {box,actions}=noticeBox(result.restore.status==='restored'?'Backup restored':'Restore not applied',[result.restore.message,result.restore.error]);
      actions.append(action('Keep current',()=>v1().dismissBackupUpdateNotice(),{after:()=>refreshBackups()}));root.append(box);
    }
    const update=result.update;
    if(update){
      const backup=result.items.find(item=>item.id===update.backup_id);
      const lines=[update.message];
      if(backup&&!backup.schema_matches_build)lines.push('This backup is at the database schema of '+(update.from_version||'the previous version')+'. To go back, run AutoSourcarr '+(update.from_version||'(the previous image)')+' again and restore this backup there; restoring it here would upgrade it again.');
      const {box,actions}=noticeBox('Updated to '+update.to_version,lines);
      if(backup&&backup.schema_matches_build)actions.append(previewSaved(backup,'Roll back to the backup from '+when(update.backup_created_at)));
      else if(backup)actions.append(downloadLink(backup,'Download the backup from '+when(update.backup_created_at)));
      actions.append(action('Keep current',()=>v1().dismissBackupUpdateNotice(),{after:()=>refreshBackups()}));
      root.append(box);
    }
  }
  async function refreshBackups(){
    const own=generation,result=await v1().listBackups({signal:read.signal,suppressClientError:true});if(own!==generation)return;
    renderNotices(result);
    const root=byId('riBackupList');root.replaceChildren();
    if(!result.items.length)root.textContent='No backups saved yet.';
    for(const item of result.items){
      const row=document.createElement('div');row.className='riBackupRow';row.innerHTML=`<span>${esc(when(item.created_at))} · ${esc(kindLabel(item.kind))} · ${(item.bytes/1048576).toFixed(1)} MiB · ${Number(item.entries)} entries${item.protected?' · password protected':''}</span>`;
      row.append(downloadLink(item));
      row.append(previewSaved(item));
      row.append(action('Delete backup',async()=>{
        if(!confirm('Permanently delete this backup? Download a copy first if you need to keep it.'))return;
        return v1().deleteBackup(item.id);
      },{danger:true,after:()=>refreshBackups()}));root.append(row);
    }
    status(`${result.items.length} backups · manual backups kept: up to ${Number(result.limits?.manual)} · automatic: newest ${Number(result.limits?.automatic_kept)}`);
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
