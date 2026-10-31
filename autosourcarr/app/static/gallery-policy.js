/* Gallery choices belong to a stable source ID, with explicit saves and owned reads. */
(()=>{
  'use strict';
  async function mount({body,variants,signal,write,status,close}){
    const root=document.createElement('section');root.className='riGalleryPolicy';body.append(root);
    root.innerHTML='<p>Choose gallery handling separately for each source language. Saving applies the rules to future matches and queues a cached layout update in Tasks.</p><label>Source language<select data-source></select></label><div class="riToolActions"><button type="button" data-reload>Refresh saved state</button><button type="button" data-apply>Apply saved rules again</button><button type="button" data-tasks>View tasks</button></div><p data-layout role="status"></p><form data-policy></form>';
    const get=selector=>root.querySelector(selector),picker=get('[data-source]'),form=get('[data-policy]'),drafts=new Map();let selected='',snapshot=null,read=null,generation=0,writing=false;
    const alive=()=>root.isConnected&&!signal.aborted;
    signal.addEventListener('abort',()=>read?.abort(),{once:true});
    for(const row of variants){const option=document.createElement('option');option.value=String(row.source_id);option.textContent=(row.name||row.source_id)+' · '+(row.lang||'all');picker.append(option);}
    function value(){return {profile:get('[data-profile]').value,enabled:get('[data-enabled]').checked,
      criteria:[...form.querySelectorAll('[data-criterion]:checked')].map(node=>node.dataset.criterion).sort(),
      title_minimum:Number(get('[data-title]').value)/100,tag_similarity:Number(get('[data-tags]').value)/100,tag_overlap:Number(get('[data-overlap]').value)};}
    function remember(){if(snapshot&&get('[data-profile]')){const draft=value();if(JSON.stringify(draft)===JSON.stringify(snapshot.policy))drafts.delete(selected);else drafts.set(selected,draft);}}
    function render(){
      const current=drafts.get(selected)||snapshot.policy;
      form.innerHTML='<label>Gallery metadata profile<select data-profile><option value="auto">Detect known gallery sources</option><option value="gallery">This source contains gallery uploads</option></select></label><label class="riCheck"><input data-enabled type="checkbox">Combine compatible galleries as chapters</label><p class="muted">Off keeps distinct gallery records separate, including cached groups. On uses their chapter/part numbers and preserves independent slots for unnumbered uploads. Cached pages and historical reader IDs are retained.</p><label>Minimum native work-title similarity (%)<input data-title type="number" min="94" max="100" step="1" required></label><fieldset><legend>Additional required matching criteria</legend><div data-criteria></div></fieldset><p class="muted">All selected checks require matching evidence on both records. The native title floor, creator/edition conflicts, animated versus still distinction and saved Separate decisions always remain enforced. These percentages are similarity thresholds.</p><label>Informative tag similarity (% overlap / union)<input data-tags type="number" min="1" max="100" step="1" required></label><label>Minimum shared informative tags<input data-overlap type="number" min="1" max="50" step="1" required></label><button type="submit">Save gallery rules</button>';
      get('[data-profile]').value=current.profile;get('[data-enabled]').checked=current.enabled;get('[data-title]').value=Math.round(current.title_minimum*100);get('[data-tags]').value=Math.round(current.tag_similarity*100);get('[data-overlap]').value=current.tag_overlap;
      for(const [key,label] of [['artist','Artist / author'],['circle','Circle / group'],['parody','Parody / franchise'],['language','Language'],['category','Gallery category'],['tags','Informative tags']]){
        const row=document.createElement('label');row.className='riCheck';const input=document.createElement('input');input.type='checkbox';input.dataset.criterion=key;input.checked=current.criteria.includes(key);row.append(input,document.createTextNode(label));get('[data-criteria]').append(row);
      }
      get('[data-layout]').textContent=(snapshot.detected?'Known gallery source. ':'Use the gallery profile above if this provider is not detected automatically. ')+(snapshot.layout_pending?'Saved layout update is pending. Check Tasks or apply the saved rules again.':snapshot.layout_result?.checked!=null?`${snapshot.layout_result.checked} cached entries checked · ${snapshot.layout_result.splits} records separated · ${snapshot.layout_result.merged} entries combined.`:'No saved layout update yet.');
      form.onsubmit=event=>{event.preventDefault();save(event.submitter||form.querySelector('[type=submit]'));};
    }
    async function load(id){
      if(writing)return;remember();snapshot=null;selected=String(id);const own=++generation;read?.abort();read=new AbortController();form.textContent='Loading gallery rules…';status('Reading saved gallery choices…');
      try{const result=await api('/api/admin/gallery-policies/'+encodeURIComponent(selected),{signal:read.signal,suppressClientError:true});if(alive()&&own===generation){snapshot=result;render();status('Each source language has its own gallery choices. Unsaved drafts stay in this dialog.');}}
      catch(error){if(alive()&&own===generation){form.textContent='Gallery rules unavailable: '+error.message;status('Refresh saved state to try again.');}}
    }
    async function save(button){
      if(writing||!snapshot)return;remember();const current=snapshot,own=generation,policy=drafts.get(selected)||value();writing=true;picker.disabled=true;form.inert=true;
      try{await write(button,()=>api('/api/admin/gallery-policies/'+encodeURIComponent(selected),{method:'PUT',body:JSON.stringify({revision:current.revision,policy}),noMutationRefresh:true}),result=>{
        if(alive()&&own===generation){snapshot=result;drafts.delete(selected);render();status(result.task?.queued?'Gallery rules saved. Their cached layout update is queued in Tasks.':result.task?.already_running?'Gallery rules saved. A layout task is already running; the newest rules remain pending until applied.':'Gallery rules saved. '+(result.task?.error||'Use Apply saved rules again to update cached groups.'));}
      });}finally{writing=false;picker.disabled=false;form.inert=false;if(alive()&&document.activeElement===document.body)form.querySelector('[type=submit]')?.focus({preventScroll:true});}
    }
    picker.onchange=()=>load(picker.value);get('[data-reload]').onclick=()=>load(picker.value);
    get('[data-apply]').onclick=async event=>{if(!snapshot||writing)return;const id=selected,own=generation;writing=true;picker.disabled=true;form.inert=true;try{await write(event.currentTarget,()=>api('/api/admin/gallery-policies/'+encodeURIComponent(id)+'/apply',{method:'POST',body:'{}',noMutationRefresh:true}),result=>{if(alive()&&own===generation)status(result.queued?'Saved gallery layout queued in Tasks.':result.already_running?'A gallery layout task is already running.':result.error||'Layout not queued.');});}finally{writing=false;picker.disabled=false;form.inert=false;}};
    get('[data-tasks]').onclick=()=>{if(writing)return;close();window.openTasksSection?.('general');};
    if(picker.value)await load(picker.value);else status('No source languages are available. Refresh Sources first.');
  }
  window.AutoSourcarrGalleryPolicy={mount};
})();
