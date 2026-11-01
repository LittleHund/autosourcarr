/* Local metadata and tag decisions. Fetching tags never blocks opening an entry. */
(() => {
  const byId=id=>document.getElementById(id), panel=byId('tagReviewPanel');
  if(!panel)return;
  const state={generation:0,catalogGeneration:0,offset:0,total:0,busy:false,
    pickerGeneration:{Left:0,Right:0},pickers:{Left:[],Right:[]},selected:{Left:null,Right:null}};
  const label=tag=>`${tag.namespace||'general'}: ${tag.name||'Unnamed tag'}`;
  const button=(text,action,className='')=>{const el=document.createElement('button');el.type='button';el.textContent=text;el.className=className;el.onclick=()=>action(el);return el;};
  const note=(node,message)=>{node.replaceChildren();const p=document.createElement('p');p.className='muted';p.textContent=message;node.append(p);};
  function selectionChanged(side){
    const id=Number(byId(`tag${side}Select`).value);
    state.selected[side]=state.pickers[side].find(tag=>Number(tag.id)===id)||null;
    byId(`tag${side}Selection`).textContent=state.selected[side]?`Selected ${label(state.selected[side])}`:'No tag selected.';
    updateButtons();
  }
  function updateButtons(){
    const left=state.selected.Left,right=state.selected.Right,disabled=state.busy||!left||!right||Number(left.id)===Number(right.id);
    for(const id of ['tagSame','tagDifferent','tagClear'])byId(id).disabled=disabled;
    const differentRoles=!!left&&!!right&&left.namespace!==right.namespace;
    byId('tagSame').disabled=disabled||differentRoles;
    byId('tagSame').title=differentRoles?'Different metadata roles cannot be combined. Choose two tags in the same namespace.':'';
    panel.querySelectorAll('[data-tag-decision]').forEach(el=>el.disabled=state.busy);
  }
  async function searchPicker(side){
    const generation=++state.pickerGeneration[side],select=byId(`tag${side}Select`);
    // A new search invalidates the old selection immediately; a slow response cannot restore it.
    state.selected[side]=null;state.pickers[side]=[];select.replaceChildren();
    byId(`tag${side}Selection`).textContent='Searching saved tags…';updateButtons();
    try{
      const result=await api(`/api/v1/tags?search=${encodeURIComponent(byId(`tag${side}Search`).value.trim())}&limit=100&offset=0`,{timeoutMs:15000});
      if(generation!==state.pickerGeneration[side])return;
      state.pickers[side]=result.items||[];
      for(const tag of state.pickers[side]){const option=document.createElement('option');option.value=String(tag.id);option.textContent=label(tag);select.append(option);}
      select.selectedIndex=-1;
      byId(`tag${side}Selection`).textContent=state.pickers[side].length?'Choose a tag above. Narrow the search if it is not listed.':'No saved tags match. Read tags from saved metadata if needed.';
    }catch(error){if(generation===state.pickerGeneration[side])byId(`tag${side}Selection`).textContent='Unable to search: '+error.message;}
  }
  async function decide(left,right,decision){
    if(state.busy||!left||!right||Number(left.id)===Number(right.id))return;
    state.busy=true;++state.generation;updateButtons();
    try{
      await api('/api/v1/tags/review',{method:'POST',body:JSON.stringify({left_id:Number(left.id),right_id:Number(right.id),decision}),noMutationRefresh:true});
      toast(decision==='same'?'Tag meanings matched':decision==='different'?'Tags will stay separate':'Tag decision cleared');
      await load();await loadCatalog();
    }catch(error){toast('Unable to save tag decision: '+error.message);}
    finally{state.busy=false;updateButtons();}
  }
  function decisionRow(row,reviewed){
    const card=document.createElement('article');card.className='card tagDecisionCard';
    const heading=document.createElement('strong');heading.textContent=`${label(row.left)} ↔ ${label(row.right)}`;
    const text=document.createElement('p');text.className='muted';
    text.textContent=reviewed?(row.decision==='same'?'Same meaning':row.decision==='different'?'Kept separate':String(row.decision||'Saved decision')):
      `${row.reason||'Similar tag spelling'}${Number.isFinite(Number(row.score))?` · ${Math.round(Number(row.score)*100)}% similarity`:''}`;
    const actions=document.createElement('div');actions.className='toolbar wrap';
    const add=(title,decision,style)=>{const el=button(title,()=>decide(row.left,row.right,decision),style);el.dataset.tagDecision=decision;actions.append(el);};
    if(reviewed)add('Clear decision','clear','');
    else{add('Same meaning','same','primaryAction');add('Keep separate','different','');}
    card.append(heading,text,actions);return card;
  }
  async function load(){
    const generation=++state.generation;
    byId('tagReviewStatus').textContent='Loading saved tag decisions…';
    let remaining=2,failures=0;
    const loadList=async(path,id,reviewed)=>{
      try{
        const result=await api(path,{timeoutMs:15000});
        if(generation!==state.generation)return;
        const node=byId(id);node.replaceChildren();
        for(const row of result.items||[])if(row.left&&row.right)node.append(decisionRow(row,reviewed));
        if(!node.children.length)note(node,reviewed?'No manual tag decisions yet.':'No suggested synonyms to review. Existing spelling groups appear in the tag catalog.');
      }catch(error){
        if(generation!==state.generation)return;
        failures++;note(byId(id),'Unable to load: '+error.message);
      }finally{
        if(generation===state.generation){
          remaining--;updateButtons();
          byId('tagReviewStatus').textContent=remaining?'Loading remaining tag list…':failures?'Some tag lists could not be loaded. Refresh to retry.':'Source spelling and original tags are retained. Manual decisions take precedence over automatic synonyms.';
        }
      }
    };
    // Paint durable manual decisions as soon as they arrive, independently of
    // the more expensive suggestions request. Both retain the same generation.
    await Promise.all([
      loadList('/api/v1/tags/reviews','tagDecisions',true),
      loadList('/api/v1/tags/suggestions','tagSuggestions',false)
    ]);
  }
  async function loadCatalog(reset=false){
    if(reset)state.offset=0;
    const generation=++state.catalogGeneration,query=byId('tagCatalogSearch').value.trim(),offset=state.offset;
    byId('tagCatalogStatus').textContent='Loading tags…';
    try{
      const result=await api(`/api/v1/tags?search=${encodeURIComponent(query)}&limit=50&offset=${offset}`,{timeoutMs:15000});
      if(generation!==state.catalogGeneration)return;
      const rows=result.items||[];state.total=Number(result.total||0);
      if(state.total>0&&offset>=state.total){state.offset=Math.floor((state.total-1)/50)*50;return loadCatalog();}
      const catalog=byId('tagCatalog');catalog.replaceChildren();
      for(const tag of rows){
        const card=document.createElement('article');card.className='card tagCatalogItem';
        const title=document.createElement('strong');title.textContent=label(tag);
        const count=document.createElement('p');count.className='muted';count.textContent=`${Number(tag.entry_count||0)} entries · ${Number(tag.source_count||0)} sources`;
        const canonical=document.createElement('p');canonical.className='muted';canonical.textContent=`Grouped as ${tag.canonical_name||tag.name}`;
        card.append(title,count,canonical);
        if(tag.aliases?.length){const aliases=document.createElement('div');aliases.className='chipList';for(const item of tag.aliases){const chip=document.createElement('span');chip.className='titleChip';chip.textContent=label(item);aliases.append(chip);}card.append(aliases);}
        catalog.append(card);
      }
      if(!rows.length)note(catalog,'No tags found. Use “Read tags from saved metadata” to index existing source records.');
      byId('tagCatalogStatus').textContent=`${state.total} saved tags`;
      byId('tagPageLabel').textContent=rows.length?`${offset+1}–${offset+rows.length} of ${state.total}`:'0 tags';
      byId('tagPrevious').disabled=offset===0;byId('tagNext').disabled=offset+rows.length>=state.total;
    }catch(error){if(generation===state.catalogGeneration)byId('tagCatalogStatus').textContent='Unable to load tags: '+error.message;}
  }
  window.loadTagReview=()=>Promise.allSettled([load(),loadCatalog()]);
  byId('refreshTags').onclick=window.loadTagReview;
  byId('tagCatalogForm').onsubmit=event=>{event.preventDefault();loadCatalog(true);};
  byId('tagPrevious').onclick=()=>{state.offset=Math.max(0,state.offset-50);loadCatalog();};
  byId('tagNext').onclick=()=>{state.offset+=50;loadCatalog();};
  for(const side of ['Left','Right']){
    byId(`tag${side}Form`).onsubmit=event=>{event.preventDefault();searchPicker(side);};
    byId(`tag${side}Select`).onchange=()=>selectionChanged(side);
  }
  for(const [id,decision] of [['tagSame','same'],['tagDifferent','different'],['tagClear','clear']])byId(id).onclick=()=>decide(state.selected.Left,state.selected.Right,decision);
  byId('reindexTags').onclick=async event=>{
    const el=event.currentTarget;el.disabled=true;
    try{const result=await api('/api/v1/tags/reindex',{method:'POST',noMutationRefresh:true,timeoutMs:15000});toast(result.already_running?'Tag indexing is already running':'Tag indexing queued. Progress is available in Tasks.');window.loadTasks?.().catch(()=>{});}
    catch(error){toast('Unable to queue tag indexing: '+error.message);}finally{el.disabled=false;}
  };

  const entryLoads=new WeakMap();
  const values=value=>Array.isArray(value)?value.map(values).join(', '):value&&typeof value==='object'?Object.entries(value).map(([key,item])=>`${key}: ${values(item)}`).join(' · '):String(value??'');
  window.loadEntryTags=async(target,id,signal=null,metadataTarget=null)=>{
    if(!target)return;
    const request={id:Number(id)};entryLoads.set(target,request);
    const current=()=>!signal?.aborted&&target.isConnected&&entryLoads.get(target)===request&&Number(target.dataset.entryTags)===Number(id);
    try{
      const result=await api(`/api/v1/entries/${Number(id)}/tags`,{signal,timeoutMs:10000,suppressClientError:true});
      if(!current())return;
      const title=document.createElement('h3');title.textContent=metadataTarget?'Tags':'Tags & gallery metadata';target.replaceChildren(title);
      const basis=document.createElement('p');basis.className='muted';basis.textContent=result.metadata_basis==='source'?'Metadata from saved source records and parsed gallery titles. An external database match is not required.':'Saved tags and gallery details from matched source records.';target.append(basis);
      const chips=document.createElement('div');chips.className='chipList';
      for(const tag of result.tags||[]){const chip=document.createElement('span');chip.className='titleChip';chip.textContent=label(tag);chips.append(chip);}
      if(chips.children.length)target.append(chips);else{const p=document.createElement('p');p.className='muted';p.textContent='No tags saved for this entry yet.';target.append(p);}
      if(metadataTarget)metadataTarget.replaceChildren();
      const galleryRoot=metadataTarget||target;
      for(const record of result.gallery||[]){
        const details=document.createElement('details');details.className='galleryMetadata comparisonEvidence';
        const summary=document.createElement('summary');
        const kind=record.kind==='collection'?'Collection / anthology':'Gallery / single work';
        summary.textContent=`${kind} · ${record.source_name||record.source_id||'Source'} · ${record.title||'Untitled'}`;
        const dl=document.createElement('dl');
        for(const [name,value] of [['Original source title',record.raw_title],['Parsed title',record.title],['Work title',record.work_title],['Format',record.animated===true?'Animated':record.animated===false?'Non-animated':''],['Chapter / part',record.chapter_number],['Artist',record.artist],['Circle',record.circle],['Type',record.category],['Language',record.language],['Parody / series',record.parody],['Metadata origin',record.provenance]]){
          const text=values(value);if(!text)continue;const dt=document.createElement('dt'),dd=document.createElement('dd');dt.textContent=name;dd.textContent=text;dl.append(dt,dd);
        }
        details.append(summary,dl);galleryRoot.append(details);
      }
      target.append(button('Review tag meanings',()=>{setManualActionsSubtab('tags');switchTab('manual-actions');}));
    }catch(error){
      if(!current())return;
      note(target,'Saved tags could not be loaded: '+error.message);
      target.append(button('Retry tags',()=>window.loadEntryTags(target,id,signal)));
    }
  };
})();
