/* Local rules/history are fetched only when opened, never during catalogue boot. */
(()=>{
  const el=id=>document.getElementById(id),escape=esc;
  if(!el('matchingPoliciesPanel'))return;
  const labels={title:'Native title similarity',summary:'Summary similarity',author:'Author / artist similarity',tags:'Informative tag overlap'};
  const defaults={title:97,summary:90,author:90,tags:70};
  let config=null,configGeneration=0,saving=false,focusTarget='',historyGeneration=0,offset=0,loadingRules=false;
  let historyActive=false,historyLoaded=false,historyPromise=null,historyQuery='';
  let presetId='',presetSaving=false,presetGeneration=0,presetDirty=false;
  const active=tab=>typeof activeSurface!=='function'||activeSurface(tab);
  const saveView=()=>globalThis.AutoSourcarrNavigation?.save();
  const candidatesVisible=()=>!document.hidden&&active('duplicates')&&!historyActive;
  const configured=(scope,subject,direction)=>config.rules.find(r=>r.scope===scope&&r.subject===subject&&r.direction===direction)?.rule;
  function renderRules(){
    const [scope,subject]=el('matchingPolicyTarget').value.split(':');
    const source=config.sources.find(s=>String(s.id)===subject),group=config.groups.find(g=>String(g.id)===String(source?.group_id));
    el('matchingPolicyForms').innerHTML=['internal','external'].map(direction=>{
      const own=configured(scope,subject,direction),inherited=scope==='source'&&group?configured('group',String(group.id),direction):null;
      const value=own||inherited,mode=!own?'inherit':own.enabled?'custom':'off';
      const origin=scope==='source'&&group?`group “${group.name}”`:'built-in defaults';
      return `<form class="matchingPolicyCard" data-direction="${direction}"><h3>${direction==='internal'?'Same source':'Different sources'}</h3>
        <label>Automatic matching <select name="mode"><option value="inherit" ${mode==='inherit'?'selected':''}>Inherit ${escape(origin)}</option><option value="custom" ${mode==='custom'?'selected':''}>Require selected checks</option><option value="off" ${mode==='off'?'selected':''}>Disabled</option></select></label>
        <p class="muted ruleOrigin">${mode==='inherit'?(inherited?(inherited.enabled?'Inherited required checks are shown below.':'The group disables this direction.'):'Using the built-in safe matching rules.'):'All checked requirements must pass.'}</p>
        <label>Matching preset <select name="preset"><option value="">Choose a preset…</option>${(config.presets||[]).map(p=>`<option value="${escape(p.id)}">${escape(p.name)}</option>`).join('')}</select></label>
        <p class="muted rulePresetHelp">Presets fill this direction’s draft. Review the checks, then Save to apply. Required metadata must be available; built-in identity and edition checks still apply.</p><button class="rulePresetSave" type="button">Save draft as a preset</button>
        <fieldset ${mode==='custom'?'':'disabled'}><legend>Additional required evidence</legend>${Object.entries(labels).map(([key,label])=>`<label class="matchingPolicyCheck"><input type="checkbox" name="${key}" ${value?.checks?.[key]!=null?'checked':''}><span>${label}</span><input aria-label="${label} minimum percent" name="${key}Percent" type="number" min="${key==='title'?94:1}" max="100" step="1" value="${Math.round((value?.checks?.[key]??defaults[key]/100)*100)}">%</label>`).join('')}</fieldset>
        <p class="muted">Similarity percentages are comparison scores, not probabilities of correctness. Title requirements cannot be below 94%. Broad/shared tags or templated summaries alone never authorize a match.</p><button type="submit">Save ${direction==='internal'?'same-source':'cross-source'} rules</button><span class="ruleSaveStatus" role="status"></span></form>`;
    }).join('');
    el('matchingPolicyForms').querySelectorAll('form').forEach(form=>{
      const modeHint=()=>{
        const inherited=scope==='source'&&group?configured('group',String(group.id),form.dataset.direction):null;
        form.querySelector('.ruleOrigin').textContent=form.elements.mode.value==='inherit'?(inherited?(inherited.enabled?'Inherited required checks are shown below.':'The group disables this direction.'):'Using the built-in safe matching rules.'):form.elements.mode.value==='off'?'Automatic matching will be disabled after Save.':'All checked requirements must pass after Save.';
      };
      const customDraft=()=>{form.elements.preset.value='';form.querySelector('.rulePresetHelp').textContent='Custom draft. Save this direction to apply your changes.';form.querySelector('.ruleSaveStatus').textContent='Unsaved draft.';modeHint();};
      form.elements.mode.addEventListener('change',()=>{form.querySelector('fieldset').disabled=form.elements.mode.value!=='custom';customDraft();});
      form.elements.preset.addEventListener('change',()=>{
        const preset=(config.presets||[]).find(p=>p.id===form.elements.preset.value);if(!preset)return;
        form.elements.mode.value=preset.rule.enabled?'custom':'off';form.querySelector('fieldset').disabled=!preset.rule.enabled;
        for(const key of Object.keys(labels)){
          const threshold=preset.rule.checks[key];form.elements[key].checked=threshold!=null;
          form.elements[key+'Percent'].value=Math.round((threshold??defaults[key]/100)*100);
        }
        form.querySelector('.rulePresetHelp').textContent=preset.description;
        modeHint();
        form.querySelector('.ruleSaveStatus').textContent='Preset loaded into draft. Save this direction to apply.';
      });
      form.querySelectorAll('input').forEach(input=>input.addEventListener('input',customDraft));
      form.querySelector('.rulePresetSave')?.addEventListener('click',()=>{
        if(presetSaving)return;
        const rule=draftRule(form);if(!rule){form.querySelector('.ruleSaveStatus').textContent='Choose required checks or Disabled before saving a preset.';return;}
        renderPresetEditor('',{name:'',description:'',rule});presetDirty=true;
        el('matchingPresetPanel').open=true;el('matchingPresetName').focus();el('matchingPresetPanel').scrollIntoView({block:'nearest'});
      });
      form.addEventListener('submit',event=>{event.preventDefault();saveRule(form,scope,subject);});
    });
  }
  async function loadRules(target=''){
    if(saving||presetSaving)return;
    loadingRules=true;
    const generation=++configGeneration,presetsAtStart=presetGeneration;el('matchingPolicyStatus').textContent='Loading saved matching rules…';
    try{
      const data=await api('/api/v1/matching-policies');if(generation!==configGeneration)return;
      config=presetsAtStart!==presetGeneration&&config?{...data,presets:config.presets,preset_revision:config.preset_revision}:data;
      const select=el('matchingPolicyTarget'),previous=target||select.value;
      select.innerHTML='<optgroup label="Source groups">'+data.groups.map(g=>`<option value="group:${Number(g.id)}">${escape(g.name)}</option>`).join('')+'</optgroup><optgroup label="Sources and language variants">'+data.sources.map(s=>`<option value="source:${escape(s.id)}">${escape(s.name)} (${escape(s.lang||'all')}) · ${escape(s.id)}</option>`).join('')+'</optgroup>';
      if([...select.options].some(o=>o.value===previous))select.value=previous;
      focusTarget=select.value;saveView();
      el('matchingPolicyStatus').textContent=select.value?'Changes apply to new automatic decisions. Save each direction separately.':'Sync/install a source to configure its matching rules.';
      if(select.value)renderRules();else el('matchingPolicyForms').replaceChildren();
      updatePresetChoices();if(!presetDirty)renderPresetEditor(presetId);
    }catch(error){if(generation===configGeneration)el('matchingPolicyStatus').textContent=error.message;}
    finally{if(generation===configGeneration)loadingRules=false;}
  }
  async function saveRule(form,scope,subject){
    if(saving)return;const mode=form.elements.mode.value,checks={};
    if(mode==='custom')for(const key of Object.keys(labels))if(form.elements[key].checked)checks[key]=Number(form.elements[key+'Percent'].value)/100;
    if(mode==='custom'&&!Object.keys(checks).length){form.querySelector('.ruleSaveStatus').textContent='Select at least one check, or choose Inherit.';return;}
    const generation=configGeneration;
    saving=true;el('matchingPolicyTarget').disabled=true;
    // Freeze the submitted snapshot; preserve the other direction's draft.
    const controls=[...form.querySelectorAll('input, select, fieldset')].map(control=>[control,control.disabled]);
    controls.forEach(([control])=>control.disabled=true);
    const buttons=[...el('matchingPolicyForms').querySelectorAll('button')];buttons.forEach(b=>b.disabled=true);
    const status=form.querySelector('.ruleSaveStatus');status.textContent='Saving…';
    try{
      const data=await api(`/api/v1/matching-policies/${scope}/${encodeURIComponent(subject)}/${form.dataset.direction}`,{method:'PUT',body:JSON.stringify({revision:config.revision,rule:mode==='inherit'?null:{enabled:mode!=='off',checks:mode==='custom'?checks:{}}})});
      if(generation!==configGeneration)return;
      // A rule response may predate an independent preset edit.
      config={...data,presets:config.presets,preset_revision:config.preset_revision};
      status.textContent='Saved. Existing entries unchanged.';
      // Keep the other direction's unsaved controls intact.
    }catch(error){if(generation===configGeneration)status.textContent=error.message;}
    finally{if(generation===configGeneration){saving=false;el('matchingPolicyTarget').disabled=false;buttons.forEach(b=>b.disabled=false);controls.forEach(([control,disabled])=>control.disabled=disabled);}}
  }
  function draftRule(form){
    const mode=form.elements.mode.value;if(mode==='inherit')return null;
    const checks={};if(mode==='custom')for(const key of Object.keys(labels))if(form.elements[key].checked)checks[key]=Number(form.elements[key+'Percent'].value)/100;
    return {enabled:mode==='custom',checks};
  }
  function updatePresetChoices(){
    if(!config||!el('matchingPresetTarget'))return;
    const options=(config.presets||[]).map(p=>`<option value="${escape(p.id)}">${escape(p.name)}${p.builtin?' · built-in':''}</option>`).join('');
    el('matchingPresetTarget').innerHTML='<option value="">New preset</option>'+options;el('matchingPresetTarget').value=presetId;
    el('matchingPolicyForms').querySelectorAll('form').forEach(form=>{
      const select=form.elements.preset,previous=select.value;
      select.innerHTML='<option value="">Choose a preset…</option>'+options;
      select.value=(config.presets||[]).some(p=>p.id===previous)?previous:'';
    });
  }
  function renderPresetEditor(id='',draft=null){
    const form=el('matchingPresetForm');if(!form||!config)return;
    const selected=(config.presets||[]).find(p=>p.id===id);
    presetId=selected?.id||'';presetDirty=false;el('matchingPresetTarget').value=presetId;
    const value=draft||selected||{name:'',description:'',rule:{enabled:true,checks:{title:.97}}};
    el('matchingPresetName').value=selected?.builtin?value.name+' copy':value.name;
    el('matchingPresetDescription').value=value.description;
    form.elements.mode.value=value.rule.enabled?'custom':'off';el('matchingPresetChecks').disabled=!value.rule.enabled;
    for(const key of Object.keys(labels)){form.elements[key].checked=value.rule.checks[key]!=null;form.elements[key+'Percent'].value=Math.round((value.rule.checks[key]??defaults[key]/100)*100);}
    el('matchingPresetSave').textContent=selected?.builtin?'Save as new':presetId?'Save preset':'Create preset';
    el('matchingPresetClone').hidden=!selected||selected.builtin;el('matchingPresetDelete').hidden=!selected||selected.builtin;
    el('matchingPresetStatus').textContent=selected?.builtin?'Built-in template. Save your changes as a new preset.':'';
  }
  function freezePresetEditor(value){
    el('matchingPresetForm')?.querySelectorAll('input, select, textarea, button, fieldset').forEach(node=>node.disabled=value);
    if(!value&&el('matchingPresetChecks'))el('matchingPresetChecks').disabled=el('matchingPresetMode').value!=='custom';
    for(const id of ['matchingPresetTarget','matchingPresetReload','matchingPolicyReload'])if(el(id))el(id).disabled=value;
  }
  async function savePreset({remove=false,clone=false}={}){
    if(!config||presetSaving)return;
    const form=el('matchingPresetForm'),selected=(config.presets||[]).find(p=>p.id===presetId),updating=!!selected&&!selected.builtin&&!clone;
    if(remove&&!updating)return;
    if(remove&&!confirm('Delete this saved preset? Applied matching rules stay unchanged.'))return;
    const rule=draftRule(form);
    if(!remove&&(!el('matchingPresetName').value.trim()||rule.enabled&&!Object.keys(rule.checks).length)){el('matchingPresetStatus').textContent='Enter a name and choose at least one check, or select Disabled.';return;}
    const own=++presetGeneration,id=updating?presetId:'',before=new Set(config.presets.map(p=>p.id));
    const body=remove?{revision:config.preset_revision}:{revision:config.preset_revision,name:clone?el('matchingPresetName').value.slice(0,75)+' copy':el('matchingPresetName').value,description:el('matchingPresetDescription').value,rule};
    presetSaving=true;freezePresetEditor(true);el('matchingPresetStatus').textContent=remove?'Deleting…':'Saving preset…';
    try{
      const data=await api('/api/v1/matching-presets'+(id?'/'+encodeURIComponent(id):''),{method:remove?'DELETE':id?'PUT':'POST',body:JSON.stringify(body)});
      if(own!==presetGeneration||!config)return;
      config={...config,...data};presetId=remove?'':id||data.presets.find(p=>!before.has(p.id))?.id||'';
      updatePresetChoices();renderPresetEditor(presetId);el('matchingPresetStatus').textContent=remove?'Preset deleted. Applied rules unchanged.':'Preset saved. Select it in a direction and Save to apply.';
    }catch(error){if(own===presetGeneration&&config)el('matchingPresetStatus').textContent=error.message;}
    finally{if(own===presetGeneration&&config){presetSaving=false;freezePresetEditor(false);}}
  }
  async function reloadPresets(){
    if(!config||presetSaving)return;
    const own=++presetGeneration;
    if(presetDirty&&!confirm('Discard this preset draft and reload saved presets?'))return;
    presetSaving=true;freezePresetEditor(true);
    try{const data=await api('/api/v1/matching-presets');if(own!==presetGeneration||!config)return;config={...config,...data};updatePresetChoices();renderPresetEditor(presetId);}
    catch(error){if(own===presetGeneration&&config)el('matchingPresetStatus').textContent=error.message;}
    finally{if(own===presetGeneration&&config){presetSaving=false;freezePresetEditor(false);}}
  }
  if(el('matchingPresetForm')){
    el('matchingPresetTarget').addEventListener('change',()=>{if(!presetSaving)renderPresetEditor(el('matchingPresetTarget').value);});
    el('matchingPresetForm').addEventListener('submit',event=>{event.preventDefault();savePreset();});
    el('matchingPresetMode').addEventListener('change',()=>{presetDirty=true;el('matchingPresetChecks').disabled=el('matchingPresetMode').value!=='custom';});
    el('matchingPresetForm').querySelectorAll('input, textarea').forEach(node=>node.addEventListener('input',()=>{presetDirty=true;el('matchingPresetStatus').textContent='Unsaved preset draft.';}));
    el('matchingPresetClone').addEventListener('click',()=>savePreset({clone:true}));el('matchingPresetDelete').addEventListener('click',()=>savePreset({remove:true}));el('matchingPresetReload').addEventListener('click',reloadPresets);
  }
  window.openMatchingPolicy=(scope,subject)=>{
    focusTarget=scope+':'+subject;el('matchingPoliciesPanel').open=true;
    loadRules(focusTarget);el('matchingPoliciesPanel').scrollIntoView({block:'start'});el('matchingPolicyTarget').focus();
  };
  function activateRules(){if(el('matchingPoliciesPanel').open&&!config&&!loadingRules)return loadRules(focusTarget);}
  el('matchingPoliciesPanel').addEventListener('toggle',()=>{if(active('sources'))activateRules();saveView();});
  el('matchingPolicyReload').addEventListener('click',()=>loadRules());
  el('matchingPolicyTarget').addEventListener('change',()=>{if(!saving&&config){focusTarget=el('matchingPolicyTarget').value;renderRules();saveView();}});

  function historyView(show,{load=true,persist=true}={}){
    historyActive=!!show;
    el('duplicateCandidatesPanel').hidden=show;el('duplicateHistoryPanel').hidden=!show;
    el('duplicatePossibleView').setAttribute('aria-pressed',String(!show));el('duplicateHistoryView').setAttribute('aria-pressed',String(show));
    if(show){if(load)loadHistory();}else{++historyGeneration;historyPromise=null;}
    if(persist)saveView();
  }
  function loadHistory(){
    const promise=fetchHistory();historyPromise=promise;
    const clear=()=>{if(historyPromise===promise)historyPromise=null;};
    promise.then(clear,clear);
    return promise;
  }
  async function fetchHistory(){
    const generation=++historyGeneration;el('mergeHistoryCount').textContent='Loading recorded merges…';
    try{
      const data=await api(`/api/v1/duplicates/history?offset=${offset}&limit=50&q=${encodeURIComponent(historyQuery)}`);
      if(generation!==historyGeneration)return;
      if(offset&&offset>=data.total){offset=data.total?Math.floor((data.total-1)/50)*50:0;saveView();return loadHistory();}
      historyLoaded=true;
      el('mergeHistoryCount').textContent=data.total?`${offset+1}–${offset+data.items.length} of ${data.total}`:'No automatic merges recorded.';
      el('mergeHistoryPrevious').disabled=offset===0;el('mergeHistoryNext').disabled=offset+data.items.length>=data.total;
      el('mergeHistoryList').innerHTML=data.items.map(row=>`<article class="mergeHistoryCard"><div class="toolbar wrap"><strong>${escape(row.left_title||'Original entry')} + ${escape(row.right_title||'Original entry')}</strong><span class="muted">${escape(new Date(row.merged_at).toLocaleString())} · ${escape(row.state)}</span></div><p>${escape(row.reason)}</p><p class="muted">Survivor at merge: ${Number(row.target_entry_id)} · absorbed entry: ${Number(row.duplicate_entry_id)}</p>${currentActions(row.current_entry_id)}<details data-event="${Number(row.id)}"><summary>Inspect before-merge entries and evidence</summary><div class="mergeHistoryDetail"></div></details></article>`).join('');
      el('mergeHistoryList').querySelectorAll('details').forEach(d=>d.addEventListener('toggle',()=>{if(d.open&&!d.dataset.loaded)loadDetail(d,generation);}));
      wireActions(el('mergeHistoryList'));
      globalThis.AutoSourcarrNavigation?.rendered('duplicates');
    }catch(error){if(generation===historyGeneration)el('mergeHistoryCount').textContent=error.message;}
  }
  function currentActions(id){return id?`<div class="toolbar wrap"><button type="button" data-inspect="${Number(id)}">Inspect current entry</button><button type="button" data-split="${Number(id)}">Review / split source records</button></div>`:'<p class="muted">The current entry is no longer in the catalogue.</p>';}
  function wireActions(root){
    root.querySelectorAll('[data-inspect]').forEach(b=>b.addEventListener('click',event=>openBrowseEntry(Number(b.dataset.inspect),event)));
    root.querySelectorAll('[data-split]').forEach(b=>b.addEventListener('click',()=>openUnmatchDuplicatesForEntry(Number(b.dataset.split))));
  }
  async function loadDetail(node,generation){
    node.dataset.loaded='loading';const target=node.querySelector('.mergeHistoryDetail');target.textContent='Loading saved snapshot…';
    try{
      const data=await api('/api/v1/duplicates/history/'+node.dataset.event);if(generation!==historyGeneration)return;
      target.innerHTML='<div class="mergeHistoryBefore">'+data.before.map((snapshot,index)=>`<section><h4>${escape(snapshot.role||'Original entry')} before merge</h4>${snapshot.available?`<strong>${escape(snapshot.entry.preferred_title||'Untitled')} · ID ${Number(snapshot.entry.id)}</strong><p>${escape(snapshot.entry.description||'No saved description')}</p><details><summary>Original aliases</summary><p>${snapshot.titles.map(escape).join(' · ')||'None saved'}</p></details><h5>Source records</h5>${snapshot.records.map(r=>`<p><strong>${escape(r.source_title||'Untitled')}</strong><br><span class="muted">Source ${escape(r.source_id)} · record ${Number(r.source_manga_id)}</span></p>`).join('')}`:'<p class="muted">Original details were not saved by that release.</p>'}</section>`).join('')+'</div><details><summary>Recorded decision details</summary><pre>'+escape(Object.keys(data.evidence).length?JSON.stringify(data.evidence,null,2):'Only the recorded reason is available for this older merge. Current settings are not used to reconstruct an old decision.')+'</pre></details>';
      node.dataset.loaded='yes';
    }catch(error){if(generation===historyGeneration){target.textContent=error.message;delete node.dataset.loaded;}}
  }
  el('duplicateHistoryView').addEventListener('click',()=>historyView(true));
  el('duplicatePossibleView').addEventListener('click',()=>{historyView(false);if(active('duplicates')&&typeof loadDuplicates==='function')loadDuplicates().catch(()=>{});});
  function searchHistory(){historyQuery=el('mergeHistoryQuery').value.trim().slice(0,200);offset=0;saveView();loadHistory();}
  el('mergeHistorySearch').addEventListener('click',searchHistory);
  el('mergeHistoryQuery').addEventListener('keydown',e=>{if(e.key==='Enter'){e.preventDefault();searchHistory();}});
  el('mergeHistoryPrevious').addEventListener('click',()=>{offset=Math.max(0,offset-50);saveView();loadHistory();});
  el('mergeHistoryNext').addEventListener('click',()=>{offset=Math.min(1000000,offset+50);saveView();loadHistory();});
  window.AutoSourcarrMatchingReview={
    candidatesVisible,
    get historyActive(){return historyActive;},
    activateRules,
    activateHistory(){if(historyActive)return historyPromise||(historyLoaded?Promise.resolve():loadHistory());},
    refresh(){return historyActive?loadHistory():loadDuplicates();},
    showCandidates(){historyView(false,{load:false});},
    getViewState(){return {view:historyActive?'history':'possible',query:historyQuery,offset,target:focusTarget,open:!!el('matchingPoliciesPanel').open};},
    restoreViewState(state){
      ++configGeneration;++historyGeneration;config=null;saving=false;loadingRules=false;historyLoaded=false;historyPromise=null;
      ++presetGeneration;presetSaving=false;presetDirty=false;presetId='';freezePresetEditor(false);
      if(el('matchingPresetName')){el('matchingPresetName').value='';el('matchingPresetDescription').value='';el('matchingPresetTarget').value='';el('matchingPresetStatus').textContent='';el('matchingPresetPanel').open=false;}
      offset=state?.offset||0;historyQuery=state?.query||'';focusTarget=state?.target||'';
      el('matchingPolicyTarget').value='';el('matchingPolicyTarget').disabled=false;
      el('mergeHistoryQuery').value=historyQuery;el('mergeHistoryList').replaceChildren();el('matchingPolicyForms').replaceChildren();
      el('matchingPoliciesPanel').open=!!state?.open;
      historyView(state?.view==='history',{load:false,persist:false});
    },
  };
})();
