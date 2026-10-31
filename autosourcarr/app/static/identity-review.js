/* Private, revision-bound portable-reference decisions; no automatic sharing. */
(()=>{
  'use strict';
  async function mount(ctx){
    const {body,signal,write,status,initialEntry=null,scoped=false}=ctx;
    const root=document.createElement('section');root.className='riIdentityWorkspace';body.append(root);
    root.innerHTML=`<p class="muted">Review public references on this instance. Work, edition and gallery scopes remain separate. These decisions do not merge entries or change source/chapter bindings.</p><div class="riIdentityColumns"><section class="riIdentityCatalogue" ${scoped?'hidden':''}><form data-identity-search><label>Find a saved entry<input type="search" maxlength="200" data-search autocomplete="off"></label><button type="submit">Search</button><button type="button" data-clear>Clear search</button></form><p data-list-status role="status"></p><div data-entry-list></div><div class="riToolActions"><button type="button" data-previous>Previous</button><button type="button" data-next>Next</button></div></section><section data-identity-detail><p>Select a saved entry to review its references.</p></section></div>`;
    const get=s=>root.querySelector(s),detail=get('[data-identity-detail]');let listGeneration=0,detailGeneration=0,listRead=null,detailRead=null,offset=0,search='',selected=null,snapshot=null,writing=false;
    const drafts=new Map();
    const alive=()=>root.isConnected&&!signal.aborted;
    signal.addEventListener('abort',()=>{listRead?.abort();detailRead?.abort();},{once:true});
    function remember(){if(selected&&snapshot)drafts.set(selected,{note:detail.querySelector('[data-note]')?.value||'',reference:detail.querySelector('[data-new-reference]')?.value||''});}
    async function list(){
      const own=++listGeneration;listRead?.abort();listRead=new AbortController();get('[data-list-status]').textContent='Loading saved entries…';
      try{
        const result=await api('/api/admin/identity-review?'+new URLSearchParams({search,offset,limit:20}),{signal:listRead.signal,suppressClientError:true});if(!alive()||own!==listGeneration)return;
        const host=get('[data-entry-list]');host.replaceChildren();
        for(const row of result.items){const button=document.createElement('button');button.type='button';button.className='riIdentityEntry';button.dataset.identityEntry=String(row.id);button.textContent=row.preferred_title+' · '+row.provider_count+' provider links · '+row.saved_decisions+' saved decisions';button.setAttribute('aria-pressed',String(row.id===selected));button.onclick=()=>choose(row.id);host.append(button);}
        get('[data-list-status]').textContent=result.total?`${offset+1}–${offset+result.items.length} of ${result.total} saved entries`:(search?'No saved entries match this search. Clear search to show the catalogue.':'No saved entries yet. Discover or import an entry first.');
        get('[data-previous]').disabled=!offset;get('[data-next]').disabled=offset+result.items.length>=result.total;
      }catch(error){if(alive()&&own===listGeneration)get('[data-list-status]').textContent='Entry list unavailable: '+error.message;}
    }
    async function choose(id){
      if(writing){status('Finish the current identity decision first.');return;}
      remember();snapshot=null;selected=Number(id);const own=++detailGeneration;detailRead?.abort();detailRead=new AbortController();detail.textContent='Loading identity evidence…';
      for(const button of root.querySelectorAll('[data-identity-entry]'))button.setAttribute('aria-pressed',String(Number(button.dataset.identityEntry)===selected));
      try{const result=await api('/api/admin/identity-review/'+selected,{signal:detailRead.signal,suppressClientError:true});if(alive()&&own===detailGeneration){snapshot=result;render();detail.querySelector('h3').focus({preventScroll:true});status('Identity evidence loaded. Reviews stay private to this instance.');}}
      catch(error){if(alive()&&own===detailGeneration)detail.textContent='Identity review unavailable: '+error.message;}
    }
    async function decide(reference,decision,restoreId=null){
      if(writing||!snapshot)return;remember();writing=true;const own=detailGeneration,current=snapshot;
      const button=detail.querySelector('[data-refresh]');detail.inert=true;
      try{await write(button,()=>api('/api/admin/identity-review/'+current.entry_id,{method:'POST',body:JSON.stringify({reference,decision,revision:current.revision,note:drafts.get(selected)?.note||'',restore_id:restoreId}),noMutationRefresh:true}),result=>{
        if(alive()&&own===detailGeneration){snapshot=result;render();status('Local identity review saved. Source matches and chapter bindings are unchanged.');}
      });}finally{writing=false;detail.inert=false;if(alive()&&own===detailGeneration&&document.activeElement===document.body)detail.querySelector('h3')?.focus({preventScroll:true});}
    }
    function render(){
      const saved=drafts.get(selected)||{};detail.innerHTML=`<div class="toolbar"><h3 class="grow"></h3><button type="button" data-refresh>Refresh evidence</button></div><p class="muted" data-review-note></p><label>Review / provenance note<textarea data-note rows="2" maxlength="1200" placeholder="Public evidence or reason for this local decision"></textarea></label><div data-references></div><form data-add><h4>Add a public reference for review</h4><label>ASC Media ID<input data-new-reference maxlength="6000" placeholder="asc1:work:anilist:123"></label><p class="muted">Add a work, edition or gallery reference with a short provenance note. It is not exported or used for lookup until you confirm it against the current evidence.</p><button type="submit">Add for review</button></form><div data-history></div>`;
      detail.querySelector('h3').textContent=snapshot.title;detail.querySelector('h3').tabIndex=-1;detail.querySelector('[data-review-note]').textContent=snapshot.note;
      detail.querySelector('[data-note]').value=saved.note||'';detail.querySelector('[data-new-reference]').value=saved.reference||'';
      detail.querySelector('[data-refresh]').onclick=()=>choose(selected);
      const host=detail.querySelector('[data-references]');
      if(!snapshot.references.length){const p=document.createElement('p');p.textContent='No exact public references recorded. Add a known catalogue/gallery reference for review, or use the existing metadata re-resolution controls.';host.append(p);}
      for(const ref of snapshot.references){
        const card=document.createElement('article');card.className='riIdentityReference';
        const heading=document.createElement('h4');heading.textContent=ref.scope+' · '+ref.provider;
        const field=document.createElement('input');field.readOnly=true;field.value=ref.id;field.setAttribute('aria-label',ref.provider+' portable reference');
        const state=document.createElement('p');state.textContent=ref.decision+(ref.current_evidence?'':' · evidence changed; confirmation needs review')+(ref.note?' · '+ref.note:'');
        card.append(heading,field,state);
        if(ref.shared_with.length){const warning=document.createElement('p');warning.className='notice';warning.textContent='Shared with '+ref.shared_with.map(r=>r.title+' (#'+r.id+')').join(', ')+'. A shared work reference can cover distinct editions. No merge is proposed.';card.append(warning);}
        const actions=document.createElement('div');actions.className='riToolActions';card.append(actions);
        for(const [verb,label] of [['confirmed','Confirm association'],['excluded','Exclude portable association'],['needs-review','Needs review'],['reset','Use inherited association']]){
          const button=document.createElement('button');button.type='button';button.textContent=label;button.disabled=verb==='reset'&&ref.decision==='inherited';button.onclick=()=>decide(ref.id,verb);actions.append(button);
        }
        const copy=document.createElement('button');copy.type='button';copy.textContent='Copy ID';copy.onclick=async()=>{try{await navigator.clipboard.writeText(ref.id);status('Portable ID copied.');}catch(_){field.focus();field.select();}};actions.append(copy);host.append(card);
      }
      detail.querySelector('[data-add]').onsubmit=event=>{event.preventDefault();decide(detail.querySelector('[data-new-reference]').value.trim(),'needs-review');};
      const history=detail.querySelector('[data-history]');
      if(snapshot.history.length){const heading=document.createElement('h4');heading.textContent='Recent local review history';history.append(heading);
        for(const row of snapshot.history.slice(0,12)){const line=document.createElement('p');line.textContent=row.reference+' · '+row.decision+' · '+row.reason;const restore=document.createElement('button');restore.type='button';restore.textContent='Restore for review';restore.onclick=()=>decide(row.reference,'restore',row.id);line.append(restore);history.append(line);}}
    }
    get('[data-identity-search]').onsubmit=event=>{event.preventDefault();search=get('[data-search]').value.trim();offset=0;list();};
    get('[data-clear]').onclick=()=>{get('[data-search]').value='';search='';offset=0;list();};
    get('[data-previous]').onclick=()=>{offset=Math.max(0,offset-20);list();};get('[data-next]').onclick=()=>{offset+=20;list();};
    if(!scoped)await list();if(initialEntry)await choose(initialEntry);else if(alive())status('Select a saved entry to review its public references.');
    return {choose};
  }
  window.AutoSourcarrIdentityReview={mount};
})();
