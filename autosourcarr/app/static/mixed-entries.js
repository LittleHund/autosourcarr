/* Advisory review of records inside a single catalog entry. No live source scans. */
const mixedEntriesState={generation:0,detailGeneration:0,items:[],selected:null,detail:null,offset:0,total:0,busy:false};
async function loadMixedEntries(reset=false){
  const state=mixedEntriesState,generation=++state.generation;
  if(reset)state.offset=0;
  const query=document.querySelector('#mixedSearch').value.trim(),filter=document.querySelector('#mixedFilter').value;
  document.querySelector('#mixedSummary').textContent='Checking saved source records…';
  try{
    const data=await api(`/api/v1/mixed-entries?state=${encodeURIComponent(filter)}&q=${encodeURIComponent(query)}&offset=${state.offset}&limit=50`,{timeoutMs:15000});
    if(generation!==state.generation)return;
    state.items=data.items||[];state.offset=Number(data.offset||0);state.total=Number(data.total||0);
    const check=data.check;let progress='';
    // Findings come from the saved catalogue-wide check (a resumable job); start the first one automatically.
    if(check&&!check.run_id){progress=' · first check starting';startMixedCheck(false);}
    else if(check&&!check.completed_at)progress=` · check running (${Number(check.checked||0)} entries so far)`;
    document.querySelector('#mixedSummary').textContent=`${Number(data.counts?.needs_review||0)} need review · ${Number(data.counts?.reviewed||0)} reviewed · ${Number(data.counts?.all||0)} entries with multiple records${progress}`;
    if(!state.items.some(item=>item.entry_id===state.selected))state.selected=state.items[0]?.entry_id??null;
    renderMixedQueue();
    if(state.selected!==null)await selectMixedEntry(state.selected);
    else{state.detailGeneration++;state.detail=null;document.querySelector('#mixedDetail').innerHTML='<div class="emptyTaskState">No entries match this view. Choose All multi-record entries to inspect other combinations.</div>';}
  }catch(error){
    if(generation===state.generation)document.querySelector('#mixedSummary').textContent='Unable to refresh: '+error.message;
  }
}
function renderMixedQueue(){
  const state=mixedEntriesState;
  document.querySelector('#mixedQueue').innerHTML=state.items.map(item=>`<button type="button" class="reviewQueueItem ${item.entry_id===state.selected?'active':''}" aria-pressed="${item.entry_id===state.selected}" data-mixed-entry="${Number(item.entry_id)}"><img class="queueCover" src="${esc(entryCoverUrl(item.entry_id))}" alt="" loading="lazy" decoding="async"><span class="queueText"><strong>${esc(item.preferred_title)}</strong><small>${Number(item.record_count)} source records · ${item.state==='reviewed'?'Reviewed':item.state==='needs_review'?'Needs review':'No strong conflict'}</small></span></button>`).join('');
  document.querySelector('#mixedQueue').querySelectorAll('[data-mixed-entry]').forEach(button=>{button.onclick=()=>selectMixedEntry(Number(button.dataset.mixedEntry));});
  document.querySelector('#mixedPageLabel').textContent=state.total?`${state.offset+1}–${Math.min(state.offset+state.items.length,state.total)} of ${state.total}`:'0 entries';
  document.querySelector('#mixedPrevious').disabled=state.offset<=0;
  document.querySelector('#mixedNext').disabled=state.offset+state.items.length>=state.total;
}
async function selectMixedEntry(id){
  const state=mixedEntriesState,generation=++state.detailGeneration;
  state.selected=id;state.detail=null;renderMixedQueue();
  const target=document.querySelector('#mixedDetail');target.innerHTML='<div class="emptyTaskState">Loading source evidence…</div>';
  try{
    const data=await api(`/api/v1/mixed-entries/${Number(id)}`,{timeoutMs:10000});
    if(generation!==state.detailGeneration||id!==state.selected)return;
    state.detail=data;renderMixedDetail(data);
  }catch(error){if(generation!==state.detailGeneration)return;target.innerHTML=`<div class="notice">${esc(error.message)} <button type="button" id="mixedRetryDetail">Retry</button></div>`;const retry=document.querySelector('#mixedRetryDetail');if(retry)retry.onclick=()=>selectMixedEntry(id);}
}
function renderMixedDetail(data){
  const records=new Map(data.records.map(record=>[record.key,record]));
  const label=key=>{const record=records.get(key);return record?`${record.source_name}: ${record.title}`:key;};
  const evidence=(data.evidence||[]).map(pair=>`<li><strong>${esc(label(pair.left))}</strong> ↔ <strong>${esc(label(pair.right))}</strong><p>${esc(pair.reasons.join(' · '))}</p></li>`).join('');
  document.querySelector('#mixedDetail').innerHTML=`<div class="reviewHeading"><h3>${esc(data.preferred_title)}</h3><span class="muted">Entry ${Number(data.entry_id)}</span></div><section class="comparisonEvidence"><h3>Why review this entry?</h3>${evidence?`<ul>${evidence}</ul>`:'<p>No strong conflict found in the available metadata. This does not confirm that every record is the same work.</p>'}${data.partial?'<p class="muted">Showing a bounded evidence sample for this large entry; inspect the remaining records too.</p>':''}<p class="muted">These are suggestions from cached source metadata. Covers and translations can differ for the same work.</p></section><div class="reviewDecisionBar"><p class="muted">${data.reviewed_at?'You marked this combination as correct. New source evidence will require another review.':'Inspect the records below. Open Match Matrix to separate a wrong source using its existing source controls.'}</p><div class="statusActions"><button type="button" id="mixedOpenMatrix">Open in Match Matrix</button><button type="button" id="mixedKeep" class="goodAction">${data.reviewed_at?'Undo review':'Keep together'}</button></div></div><div class="mixedRecordGrid">${data.records.map(record=>comparisonCard(record.source_name+(record.lang?` (${record.lang})`:''),record.title,sourceCoverUrl(record.source_manga_id),`${record.locked?'Locked · ':''}${record.match_type||'Source record'} · ID ${Number(record.source_manga_id)}`,[['Author',reviewText(record.author,180)],['Format',esc(record.media_mode)],['Color edition',esc(record.color_mode)],['Summary',reviewText(record.description,360)]])).join('')}</div>`;
  document.querySelector('#mixedKeep').onclick=()=>reviewMixedEntry(data);
  document.querySelector('#mixedOpenMatrix').onclick=event=>{switchTab('matrix');openInlineEntry('matrix',Number(data.entry_id),event);};
}
async function reviewMixedEntry(data){
  const state=mixedEntriesState;if(state.busy)return;
  const button=document.querySelector('#mixedKeep');state.busy=true;if(button)button.disabled=true;
  try{
    await api(`/api/v1/mixed-entries/${Number(data.entry_id)}/review`,{method:'POST',body:JSON.stringify({fingerprint:data.fingerprint,decision:data.reviewed_at?'clear':'keep'}),noMutationRefresh:true});
    toast(data.reviewed_at?'Review cleared':'Combination marked as correct');
    await loadMixedEntries();
  }catch(error){toast('Unable to save review: '+error.message);if(state.selected===data.entry_id)await selectMixedEntry(data.entry_id);}
  finally{state.busy=false;if(button?.isConnected)button.disabled=false;}
}
async function startMixedCheck(restart){
  try{await api('/api/v1/mixed-entries/check',{method:'POST',body:JSON.stringify({restart:Boolean(restart)}),noMutationRefresh:true});}
  catch(error){toast('Unable to start the mixed-entry check: '+error.message);}
}
document.querySelector('#mixedRefresh').onclick=async()=>{await startMixedCheck(true);await loadMixedEntries();};
document.querySelector('#mixedApply').onclick=()=>loadMixedEntries(true);
document.querySelector('#mixedFilter').onchange=()=>loadMixedEntries(true);
document.querySelector('#mixedSearch').onkeydown=event=>{if(event.key==='Enter')loadMixedEntries(true);};
document.querySelector('#mixedPrevious').onclick=()=>{mixedEntriesState.offset=Math.max(0,mixedEntriesState.offset-50);loadMixedEntries();};
document.querySelector('#mixedNext').onclick=()=>{mixedEntriesState.offset+=50;loadMixedEntries();};
