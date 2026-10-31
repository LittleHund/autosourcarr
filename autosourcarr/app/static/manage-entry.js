/* One selected work, independent live forms and explicit write ownership. */
(()=>{
  'use strict';
  function render(ctx){
    const {body,entry,id,action,write,status,signal,isCurrent}=ctx;let controls=ctx.controls,profile=ctx.profile,enhanced=false,enhancing=false;
    const byId=name=>document.getElementById(name),line=(label,value)=>`<div class="riFact"><span>${esc(label)}</span><strong>${esc(value)}</strong></div>`;
    body.innerHTML=`<h3>${esc(entry.preferred_title)}</h3><section id="riManageOverview"><div class="riFacts" data-overview></div><div class="riFacts" data-freshness></div><div class="riToolActions" id="riMembershipActions"></div><p class="muted">Main Suwayomi owns downloads. Removing membership keeps downloaded chapters. Monitoring refreshes known chapters while idle and admits up to100missing downloads per check.</p></section>
      <form id="riProfileForm"><h3>Serving preferences</h3><p class="muted">Empty lists inherit global defaults. Identity, edition and rejection safeguards remain.</p><label>Preferred languages, in order<input id="riProfileLanguages" placeholder="en, ja"></label><label>Preferred source IDs, in order<input id="riProfileSources" placeholder="Leave empty for global priority"></label><p class="muted" data-connected></p><label>Quality preference<select id="riProfileQuality"><option value="default">Global resolver</option><option value="quality">Prefer measured quality</option><option value="complete">Prefer page completeness</option></select></label><button type="submit">Save serving profile</button></form><div id="riRetainedProfiles"></div>`;
    byId('riProfileLanguages').value=profile.profile.languages.join(', ');byId('riProfileSources').value=profile.profile.sources.join(', ');byId('riProfileQuality').value=profile.profile.quality;
    body.querySelector('[data-connected]').textContent='Connected source IDs: '+((entry.matches||[]).map(s=>`${s.source_name||s.source_id}: ${s.source_id}`).join(' · ')||'No attached sources');
    const identities=document.createElement('section');identities.className='riMediaIdentity';body.append(identities);
    const title=document.createElement('h3');title.textContent='Portable media IDs';identities.append(title);
    const note=document.createElement('p');note.className='muted';note.textContent=entry.portable_identity?.note||'No exact public identity recorded yet; local reader links are unchanged.';identities.append(note);
    for(const ref of entry.portable_identity?.references||[]){
      const row=document.createElement('div');row.className='riMediaReference';const label=document.createElement('span');label.textContent=ref.scope+' · '+ref.provider+' · '+ref.association;
      const input=document.createElement('input');input.readOnly=true;input.value=ref.id;input.setAttribute('aria-label',ref.provider+' '+ref.scope+' media ID');
      const button=document.createElement('button');button.type='button';button.textContent='Copy ID';button.onclick=async()=>{try{await navigator.clipboard.writeText(ref.id);status('Media ID copied. Paste it in Search on another instance.');}catch(_){input.focus();input.select();}};
      row.append(label,input,button);identities.append(row);
    }
    const reviewButton=document.createElement('button');reviewButton.type='button';reviewButton.textContent='Review identity associations';identities.append(reviewButton);
    const inlineReview=document.createElement('section');inlineReview.hidden=true;
    const tabs=window.AutoSourcarrManageTabs.mount(body,{onSelect:key=>{if(['serving','metadata'].includes(key))loadEnhancements();}});
    tabs.panels.ids.append(inlineReview);
    reviewButton.onclick=async()=>{if(enhancing||!isCurrent())return;identities.hidden=true;inlineReview.hidden=false;await window.AutoSourcarrIdentityReview.mount({body:inlineReview,signal,write,status,initialEntry:id,scoped:true});};
    const metadataHost=document.createElement('section'),servingHost=document.createElement('section');tabs.panels.metadata.append(metadataHost);tabs.panels.serving.append(servingHost);
    async function loadEnhancements(){
      if(enhanced||enhancing||!isCurrent())return;enhancing=true;metadataHost.textContent='Loading metadata ownership…';servingHost.textContent='Loading reviewed serving controls…';
      try{
        metadataHost.replaceChildren();servingHost.replaceChildren();
        await window.AutoSourcarrArrControls.manage({body,metadataBody:metadataHost,servingBody:servingHost,entry,id,action,write,status,signal});
        if(isCurrent())enhanced=true;
      }catch(error){if(isCurrent()){metadataHost.textContent='Metadata controls unavailable: '+error.message;const retry=document.createElement('button');retry.type='button';retry.textContent='Retry loading controls';retry.onclick=()=>loadEnhancements();metadataHost.append(retry);servingHost.textContent='Reviewed controls could not be loaded. Retry from Metadata; your serving profile draft is retained.';}}
      finally{enhancing=false;}
    }
    for(const retained of profile.retained||[]){
      const row=document.createElement('p');row.textContent='Retained from merged work '+retained.title+(retained.monitored===null?'':retained.monitored?' · monitoring was on':' · monitoring was off');
      if(retained.profile){const choose=document.createElement('button');choose.type='button';choose.textContent='Use these preferences';choose.onclick=()=>{byId('riProfileLanguages').value=retained.profile.languages.join(', ');byId('riProfileSources').value=retained.profile.sources.join(', ');byId('riProfileQuality').value=retained.profile.quality;status('Retained preferences selected. Save serving profile to apply them.');};row.append(choose);}
      byId('riRetainedProfiles').append(row);
    }
    const reload=async()=>{const result=await api('/api/admin/library/'+id+'/controls',{signal,suppressClientError:true});if(isCurrent()){controls=result;paintOverview();loadSubbed().catch(()=>{});}};
    function paintOverview(){
      const o=controls.observation||{};byId('riManageOverview').querySelector('[data-overview]').innerHTML=line('Main library',controls.in_library===true?'Added':controls.in_library===false?'Not added':'Snapshot unavailable')+line('Monitoring',controls.monitored?'Enabled':'Off')+line('Downloads',o.download_state==='checked'?`${o.downloaded} of ${o.chapter_count} checked chapters`:'Not checked')+line('Source coverage',o.coverage_state==='checked'?`${o.source_gaps||0} confirmed gaps`:'Verification needed');
      byId('riManageOverview').querySelector('[data-freshness]').innerHTML=line('Last complete observation',controls.last_complete_observation?.observed_at?new Date(controls.last_complete_observation.observed_at).toLocaleString():'No complete observation')+line('Latest attempt',controls.due?.last_attempt?new Date(controls.due.last_attempt).toLocaleString():'Not attempted')+line('Next automatic check',controls.monitored?(controls.due?.next_due?new Date(controls.due.next_due).toLocaleString():'Due while idle'):'Monitoring off')+line('Wait / incomplete reason',controls.due?.reason||o.incomplete_reason||'None');
      const actions=byId('riMembershipActions');actions.replaceChildren();
      if(controls.in_library!==true)actions.append(action('Add to library',()=>api('/api/admin/library/'+id+'/membership',{method:'PUT',body:JSON.stringify({revision:controls.revision,in_library:true,monitored:false})}),{after:reload}));
      if(controls.in_library!==false)actions.append(action('Remove from library',async()=>{if(!confirm('Remove this work from the main library? Downloaded chapters are kept.'))return;return api('/api/admin/library/'+id+'/membership',{method:'PUT',body:JSON.stringify({revision:controls.revision,in_library:false,monitored:false})});},{danger:true,after:reload}));
      if(controls.in_library===true){
        actions.append(action(controls.monitored?'Stop monitoring':'Monitor downloads',()=>api('/api/admin/library/'+id+'/monitoring',{method:'PUT',body:JSON.stringify({revision:controls.revision,monitored:!controls.monitored})}),{after:reload}));
        actions.append(action('Refresh & check chapters',()=>api('/api/admin/library/'+id+'/observe?refresh=true',{method:'POST',body:'{}',timeoutMs:120000}),{after:reload}));
        actions.append(action('Queue missing downloads',()=>api('/api/admin/library/'+id+'/download',{method:'POST',body:'{}',timeoutMs:60000}),{after:r=>status(`${r.queued} chapter downloads confirmed. ${r.remaining||0} remain for a later batch.`)}));
      }
    }
    byId('riProfileForm').onsubmit=event=>{
      event.preventDefault();const form=event.currentTarget,button=event.submitter||form.querySelector('[type=submit]');form.inert=true;
      write(button,()=>api('/api/admin/profiles/'+id,{method:'PUT',body:JSON.stringify({revision:profile.revision,profile:{languages:byId('riProfileLanguages').value.split(',').map(s=>s.trim()).filter(Boolean),sources:byId('riProfileSources').value.split(',').map(s=>s.trim()).filter(Boolean),quality:byId('riProfileQuality').value}})}),result=>{if(isCurrent())profile=result;}).finally(()=>{form.inert=false;if(isCurrent()&&!tabs.panels.serving.hidden&&document.activeElement===document.body)button.focus({preventScroll:true});});
    };
    paintOverview();status('Changes apply to this work only. Each section has its own Save action.');return tabs;
  }
  window.AutoSourcarrManageEntry={render};
})();
