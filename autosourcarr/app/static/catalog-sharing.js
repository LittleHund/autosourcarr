/* Catalogue exchange has one file/preview owner; uncertainty is reconciled by stage ID. */
(()=>{
  'use strict';
  async function mount({body,signal,write,status,close}){
    const root=document.createElement('section');root.className='riCatalogSharing';body.append(root);
    root.innerHTML='<p>Export from each AutoSourcarr instance, then import the files here. Entries and saved decisions add to this catalogue. Local reader numbers stay local; shared record IDs and source references are translated.</p><p class="muted">Accounts, credentials, reading progress, downloads, source settings and extension files stay private. Imported source facts remain unverified until a matching local source record is available. This adds data; instance backup/restore remains separate.</p><p data-overview role="status"></p><div role="tablist" aria-label="Database sharing sections" class="riManageTabs"><button type="button" role="tab" data-share-tab="export" id="riShareTab-export" aria-controls="riSharePanel-export">Export</button><button type="button" role="tab" data-share-tab="import" id="riShareTab-import" aria-controls="riSharePanel-import">Import</button><button type="button" role="tab" data-share-tab="decisions" id="riShareTab-decisions" aria-controls="riSharePanel-decisions">Retained choices</button><button type="button" role="tab" data-share-tab="records" id="riShareTab-records" aria-controls="riSharePanel-records">Incoming versions</button></div><section role="tabpanel" id="riSharePanel-export" aria-labelledby="riShareTab-export"><h3>Export catalogue data</h3><label>Entries per file<input type="number" data-export-limit min="1" max="5000" step="1" value="2000"></label><label class="riCheck"><input type="checkbox" data-export-choices checked>Include saved manual decisions</label><p class="muted">Large catalogues use consecutive files. Each retains original IDs and versioned evidence, so you can import all parts in any order.</p><button type="button" data-export>Download catalogue file</button><button type="button" data-export-reset hidden>Start a new export</button><p data-export-status role="status"></p></section><section role="tabpanel" id="riSharePanel-import" aria-labelledby="riShareTab-import" hidden><h3>Add catalogue files</h3><label>Exported catalogue files<input type="file" data-files accept=".ascatalog,application/json" multiple></label><label class="riCheck"><input type="checkbox" data-import-choices checked>Apply unambiguous saved manual decisions</label><p class="muted">Conflicting local choices stay in force. Choices needing local source resolution or moving existing records wait for review. Gallery grouping follows your per-source rules.</p><div class="riToolActions"><button type="button" data-preview>Preview selected file</button><button type="button" data-apply disabled>Add the previewed data</button><button type="button" data-next hidden>Preview next file</button><button type="button" data-result disabled>Check last import result</button></div><p data-file-status role="status"></p><div data-preview-result></div></section><section role="tabpanel" id="riSharePanel-decisions" aria-labelledby="riShareTab-decisions" hidden><h3>Retained manual choices</h3><p class="muted">Refresh after a task finishes. Apply uses current local identities and respects source gallery rules. Keep local ignores this incoming choice on this instance.</p><button type="button" data-refresh-choices>Refresh choices</button><div data-choice-list></div><div data-choice-pages class="riToolActions"></div><button type="button" data-tasks>View tasks</button></section><section role="tabpanel" id="riSharePanel-records" aria-labelledby="riShareTab-records" hidden><h3>Incoming versions and identity conflicts</h3><p class="muted">Existing records are preserved. A conflicting version can be retained separately under a new portable ID. A verified local counterpart can be linked through a reviewed task.</p><button type="button" data-refresh-records>Refresh versions</button><div data-record-list></div><div data-record-pages class="riToolActions"></div></section>';
    const get=selector=>root.querySelector(selector),files=get('[data-files]');
    let writing=false,writeEpoch=0,uncertain=false,exportAfter=0,exportPart=1,queue=[],fileIndex=0,stage=null,previewRead=null,previewGeneration=0,historyRead=null,historyGeneration=0,choiceOffset=0,recordOffset=0;
    const alive=()=>root.isConnected&&!signal.aborted;
    signal.addEventListener('abort',()=>{previewRead?.abort();historyRead?.abort();},{once:true});
    const paragraph=(host,text,kind='muted')=>{const node=document.createElement('p');node.className=kind;node.textContent=text;host.append(node);return node;};
    const button=(label,handler)=>{const node=document.createElement('button');node.type='button';node.textContent=label;node.onclick=handler;return node;};
    function busy(on){if(on)writeEpoch++;writing=on;for(const control of root.querySelectorAll('input,select'))control.disabled=on;get('[data-apply]').disabled=on||uncertain||!stage||stage.state!=='preview'||stage.already_applied;}
    async function overview(){try{const row=await api('/api/admin/catalog-sharing/status',{signal,suppressClientError:true});if(alive())get('[data-overview]').textContent=`${row.entries} local entries · ${row.files_added} catalogue files added · ${row.pending_decisions} retained choices · ${row.retained_records} versions need review`;}catch(error){if(alive())get('[data-overview]').textContent='Sharing status unavailable: '+error.message;}}
    function select(key){
      for(const tab of root.querySelectorAll('[data-share-tab]')){const on=tab.dataset.shareTab===key;tab.setAttribute('aria-selected',String(on));tab.tabIndex=on?0:-1;get('#riSharePanel-'+tab.dataset.shareTab).hidden=!on;}
      if(key==='decisions')loadHistory('decisions');if(key==='records')loadHistory('records');
    }
    for(const tab of root.querySelectorAll('[data-share-tab]'))tab.onclick=()=>select(tab.dataset.shareTab);
    root.querySelector('[role=tablist]').addEventListener('keydown',event=>{const tabs=[...root.querySelectorAll('[data-share-tab]')],index=tabs.indexOf(event.target);if(index<0)return;let next=index;if(event.key==='ArrowRight')next=(index+1)%tabs.length;else if(event.key==='ArrowLeft')next=(index+tabs.length-1)%tabs.length;else if(event.key==='Home')next=0;else if(event.key==='End')next=tabs.length-1;else return;event.preventDefault();tabs[next].focus();});
    get('[data-export]').onclick=async event=>{
      if(writing)return;busy(true);
      try{await write(event.currentTarget,()=>api('/api/admin/catalog-sharing/export',{method:'POST',body:JSON.stringify({after:exportAfter,limit:Number(get('[data-export-limit]').value),include_decisions:get('[data-export-choices]').checked}),timeoutMs:120000,noMutationRefresh:true}),result=>{
        if(!alive())return;
        const blob=new Blob([JSON.stringify(result.catalogue)],{type:'application/json'});downloadBlob(blob,'AutoSourcarr-catalogue-'+new Date().toISOString().slice(0,10)+'-part-'+exportPart+'.ascatalog');
        get('[data-export-status]').textContent=`File ${exportPart}: ${result.entries} entry versions and ${result.decisions} saved choices. `+(result.next_cursor===null?'Export complete.':'More entries remain; download the next file.');
        exportAfter=result.next_cursor??0;exportPart++;get('[data-export]').textContent=result.next_cursor===null?'Download catalogue again':'Download next catalogue file';get('[data-export-reset]').hidden=result.next_cursor===null;
        if(result.next_cursor===null)exportPart=1;status('Catalogue file prepared. Private instance state was excluded.');
      });}finally{busy(false);}
    };
    get('[data-export-reset]').onclick=()=>{if(writing)return;exportAfter=0;exportPart=1;get('[data-export]').textContent='Download catalogue file';get('[data-export-reset]').hidden=true;get('[data-export-status]').textContent='New export starts at the first entry.';};
    files.onchange=()=>{if(writing)return;previewRead?.abort();previewGeneration++;queue=[...files.files];fileIndex=0;stage=null;uncertain=false;get('[data-preview-result]').replaceChildren();get('[data-next]').hidden=true;get('[data-result]').disabled=true;busy(false);get('[data-file-status]').textContent=queue.length?`${queue.length} file(s) selected. Preview each before adding it.`:'Select exported catalogue files.';};
    function renderPreview(result){
      const host=get('[data-preview-result]');host.replaceChildren();
      if(result.state==='applied'){renderResult(result.result);return;}
      paragraph(host,`${result.added} new entries · ${result.reused} mapped records · ${result.retained} versions retained for review · ${result.decision_total??result.decisions.length} saved choices in this file.`,'notice');
      if(result.already_applied)paragraph(host,'This catalogue file was already added. Existing results are retained.');
      if(result.state==='stale')paragraph(host,'Local data changed. Preview the file again before adding it.','notice');
      const wrap=document.createElement('div');wrap.className='riTableScroll';const table=document.createElement('table');table.className='riShareTable';table.innerHTML='<thead><tr><th>Incoming record</th><th>Action</th><th>Local entries</th></tr></thead><tbody></tbody>';wrap.append(table);host.append(wrap);
      for(const row of result.items){const tr=document.createElement('tr');for(const text of [row.title,row.action==='batch'?'Reuse within this file':row.action,row.target_ids.length?row.target_ids.map(id=>'#'+id).join(', '):row.message]){const cell=document.createElement('td');cell.textContent=text;tr.append(cell);}table.querySelector('tbody').append(tr);}
      const total=result.item_total??result.items.length;paragraph(host,`${total?result.offset+1:0}–${Math.min(total,result.offset+result.items.length)} of ${total} preview rows. All records are checked on the server.`);
      const pager=document.createElement('div');pager.className='riToolActions';host.append(pager);
      if(result.offset)pager.append(button('Previous preview rows',()=>pagePreview(Math.max(0,result.offset-result.limit))));
      if(result.offset+result.items.length<total)pager.append(button('More preview rows',()=>pagePreview(result.offset+result.limit)));
      busy(false);
    }
    async function pagePreview(offset){if(writing||!stage)return;const own=previewGeneration,epoch=writeEpoch,current=stage;try{const result=await api('/api/admin/catalog-sharing/stages/'+current.id+'?offset='+offset,{signal,suppressClientError:true});if(alive()&&own===previewGeneration&&epoch===writeEpoch&&!writing){stage={...result,id:current.id};renderPreview(stage);}}catch(error){if(alive()&&epoch===writeEpoch&&!writing)status(error.message);}}
    async function preview(){
      if(writing)return;const file=queue[fileIndex];if(!file){status('Choose exported catalogue files first.');return;}
      if(!file.size||file.size>64*1024*1024){status('Choose a nonempty catalogue file up to 64 MiB.');return;}
      const own=++previewGeneration,epoch=writeEpoch;previewRead?.abort();previewRead=new AbortController();stage=null;get('[data-apply]').disabled=true;get('[data-preview-result]').textContent='Checking file identities and current local choices…';
      get('[data-file-status]').textContent=`File ${fileIndex+1} of ${queue.length} · ${file.name}`;
      try{const result=await api('/api/admin/catalog-sharing/preview',{method:'POST',body:file,headers:{'Content-Type':'application/json'},signal:previewRead.signal,timeoutMs:120000,noMutationRefresh:true,suppressClientError:true});if(alive()&&own===previewGeneration&&epoch===writeEpoch&&!writing){uncertain=false;stage={...result,state:'preview'};get('[data-result]').disabled=false;renderPreview(stage);status('Review the additions. Existing local choices remain in force; structural choices wait for their own review.');}else if(alive()&&own===previewGeneration)get('[data-preview-result]').textContent='Preview was interrupted by another operation. Preview this file again before adding it.';}
      catch(error){if(alive()&&own===previewGeneration&&epoch===writeEpoch&&!writing){get('[data-preview-result]').textContent='Preview unavailable: '+error.message;status('No catalogue additions were confirmed.');}}
    }
    function renderResult(result){
      uncertain=false;
      get('[data-preview-result]').replaceChildren();paragraph(get('[data-preview-result]'),result.already_applied?'Already added; no duplicate entries were created.':`${result.added} entries added · ${result.reused} records reused · ${result.retained} versions retained · ${result.decisions_applied} choices applied · ${result.decisions_pending} choices await review.`,'notice');
      get('[data-next]').hidden=fileIndex+1>=queue.length;get('[data-apply]').disabled=true;overview();
    }
    async function reconcile(){
      if(!stage)return;const own=previewGeneration,epoch=writeEpoch,current=stage;
      try{const result=await api('/api/admin/catalog-sharing/stages/'+current.id,{signal,suppressClientError:true,timeoutMs:20000});if(alive()&&own===previewGeneration&&epoch===writeEpoch&&!writing){stage={...result,id:current.id};renderPreview(stage);if(result.state!=='applied')status('Import outcome is not confirmed. Check this result again or refresh the file preview before retrying.');}}
      catch(error){if(alive()&&own===previewGeneration&&epoch===writeEpoch&&!writing)status('Import outcome could not be confirmed: '+error.message+' Keep this file selected and check again.');}
    }
    get('[data-preview]').onclick=preview;get('[data-next]').onclick=()=>{if(writing)return;fileIndex++;stage=null;preview();};get('[data-result]').onclick=()=>{if(!writing)reconcile();};
    get('[data-apply]').onclick=async event=>{
      if(writing||uncertain||!stage||stage.state!=='preview'||stage.already_applied)return;const current=stage,own=previewGeneration;let confirmed=false;busy(true);
      try{await write(event.currentTarget,()=>api('/api/admin/catalog-sharing/apply',{method:'POST',body:JSON.stringify({id:current.id,revision:current.revision,include_decisions:get('[data-import-choices]').checked}),timeoutMs:120000,noMutationRefresh:true}),result=>{
        confirmed=true;if(alive()&&own===previewGeneration){stage={id:current.id,state:'applied',result};renderResult(result);status('Catalogue additions saved. Review retained choices or continue to the next file.');loadSubbed().catch(()=>{});}
      });}finally{if(!confirmed)uncertain=true;busy(false);if(stage?.state==='applied')get('[data-apply]').disabled=true;}
      if(!confirmed&&alive()&&own===previewGeneration){get('[data-apply]').disabled=true;await reconcile();}
    };
    function appendPages(host,total,offset,kind){host.replaceChildren();if(offset)host.append(button('Previous',()=>{if(kind==='decisions')choiceOffset=Math.max(0,offset-30);else recordOffset=Math.max(0,offset-30);loadHistory(kind);}));if(offset+30<total)host.append(button('More',()=>{if(kind==='decisions')choiceOffset=offset+30;else recordOffset=offset+30;loadHistory(kind);}));}
    async function actChoice(row,action,control){if(writing)return;busy(true);try{await write(control,()=>api('/api/admin/catalog-sharing/decisions/review',{method:'POST',body:JSON.stringify({uid:row.uid,digest:row.digest,revision:row.revision,action}),noMutationRefresh:true}),result=>status(result.queued?'Reviewed choice queued in Tasks. Refresh this list after it finishes.':result.already_running?'This choice is already running in Tasks.':result.already_reviewed?'This choice was already reviewed. Refresh the list to see its result.':result.error||'Review not queued.'));}finally{busy(false);}}
    async function actRecord(row,action,control,target=null){if(writing)return;busy(true);try{await write(control,()=>api('/api/admin/catalog-sharing/records/'+(target?'link':'review'),{method:'POST',body:JSON.stringify({uid:row.uid,digest:row.digest,revision:row.revision,...(target?{target}:{action})}),noMutationRefresh:true}),result=>{status(target?'Verified local link queued in Tasks.':result.state==='ignored'?'Local record retained.':`${result.added} separately identified entries added.`);});}finally{busy(false);loadHistory('records');overview();}}
    async function loadHistory(kind){
      if(writing||!alive())return;const own=++historyGeneration,epoch=writeEpoch;historyRead?.abort();historyRead=new AbortController();const offset=kind==='decisions'?choiceOffset:recordOffset,host=get(kind==='decisions'?'[data-choice-list]':'[data-record-list]');host.textContent='Reading retained catalogue data…';
      try{const result=await api('/api/admin/catalog-sharing/'+kind+'?offset='+offset+'&limit=30',{signal:historyRead.signal,suppressClientError:true});if(!alive()||own!==historyGeneration||epoch!==writeEpoch||writing)return;host.replaceChildren();
        if(!result.items.length)paragraph(host,kind==='decisions'?'No shared manual choices retained yet.':'No incoming versions retained yet.');
        for(const row of result.items){const card=document.createElement('article');card.className='riShareItem';const title=document.createElement('h4');title.textContent=kind==='decisions'?row.kind+' · '+row.state:row.record.title+' · '+row.state;card.append(title);paragraph(card,row.message);const actions=document.createElement('div');actions.className='riToolActions';
          if(kind==='decisions'){
            const value=row.decision.value;if(value.title)paragraph(card,'Saved title: '+value.title);
            if(value.group_title)paragraph(card,'Group: '+value.group_title);
            paragraph(card,'Shared references: '+row.decision.entries.slice(0,20).join(' · ')+(row.decision.entries.length>20?' · More references are retained in the exported file.':''));
            for(const local of row.local_records)paragraph(card,`${local.title} · ${local.source} · local entry #${local.entry_id}`);
            if(!['applied','ignored'].includes(row.state)){const apply=button('Review and apply locally',event=>actChoice(row,'apply',event.currentTarget));apply.disabled=!row.can_apply;actions.append(apply,button('Keep local choice',event=>actChoice(row,'ignore',event.currentTarget)));}
          }else{
            paragraph(card,'Portable ID: '+row.resolved_uid);paragraph(card,row.record.metadata.description||'No synopsis in this version.');
            paragraph(card,'Public references: '+row.record.references.slice(0,50).join(' · '));
            for(const fact of row.record.sources.slice(0,20)){const line=document.createElement('div');line.className='riToolActions';const text=document.createElement('span');text.textContent=fact.title+' · source '+fact.source_id;line.append(text);const gallery=button('Gallery rules',()=>{});gallery.dataset.galleryPolicySource=fact.source_id;line.append(gallery);card.append(line);}
            if(row.record.sources.length>20||row.record.references.length>50)paragraph(card,'Additional source facts and references are retained in catalogue exports.');
            if(row.state==='review')actions.append(button('Add as a separate record',event=>actRecord(row,'separate',event.currentTarget)),button('Keep local record',event=>actRecord(row,'ignore',event.currentTarget)));
            if(row.can_link)for(const candidate of row.local_candidates)if(!row.entry_ids.includes(candidate.id))actions.append(button('Use verified local record: '+candidate.title,event=>actRecord(row,'apply',event.currentTarget,candidate.id)));
          }
          for(const eid of row.entry_ids){const manage=button('Inspect local entry #'+eid,()=>{});manage.dataset.manageEntry=String(eid);actions.append(manage);}card.append(actions);host.append(card);
        }
        appendPages(get(kind==='decisions'?'[data-choice-pages]':'[data-record-pages]'),result.total,offset,kind);status(`${result.items.length} of ${result.total} retained ${kind} shown.`);
      }catch(error){if(alive()&&own===historyGeneration&&epoch===writeEpoch&&!writing)host.textContent='Retained data unavailable: '+error.message;}
    }
    get('[data-refresh-choices]').onclick=()=>loadHistory('decisions');get('[data-refresh-records]').onclick=()=>loadHistory('records');get('[data-tasks]').onclick=()=>{if(writing)return;close();window.openTasksSection?.('general');};
    select('export');await overview();if(alive())status('Use exported catalogue files for additive sharing. Local reader IDs and private instance state are preserved.');
  }
  window.AutoSourcarrCatalogSharing={mount};
})();
