/* Reviewed controls share the instance dialog's write owner and revision checks. */
(() => {
  'use strict';
  const request=(path,method,body)=>api('/api/admin/'+path,{method,body:JSON.stringify(body),suppressClientError:true});
  const date=value=>value?new Date(value).toLocaleString():'Not recorded';
  const panel=(parent,title)=>{const box=document.createElement('details');box.className='riImprovementPanel';const summary=document.createElement('summary');summary.textContent=title;box.append(summary);parent.append(box);return box;};
  const message=(parent,text)=>{const p=document.createElement('p');p.className='muted';p.textContent=text;parent.append(p);};
  const get=(host,name)=>host.querySelector('[data-field="'+name+'"]');
  const select=(label,options,name)=>`<label>${esc(label)}<select data-field="${esc(name)}">${options.map(([key,text])=>`<option value="${esc(String(key))}">${esc(text)}</option>`).join('')}</select></label>`;
  const restoreSaveFocus=(owner,selector)=>{if(owner.isConnected&&document.activeElement===document.body&&!owner.closest('.riManagePane')?.hidden)owner.querySelector(selector)?.focus({preventScroll:true});};

  async function manage(ctx){
    const {body,entry,id,action,write,status,signal}=ctx;
    const owner=document.createElement('section');(ctx.metadataBody||body).append(owner);
    const upgradeOwner=document.createElement('section');(ctx.servingBody||owner).append(upgradeOwner);
    const metadata=await api('/api/admin/metadata-fields/'+id,{signal,suppressClientError:true});
    const upgrades=await api('/api/admin/serving-upgrades/'+id,{signal,suppressClientError:true});
    if(!owner.isConnected||signal.aborted)return;
    renderMetadata(metadata);renderUpgrades(upgrades);
    function renderMetadata(saved){
      owner.querySelector('[data-metadata]')?.remove();const box=panel(owner,'Display metadata ownership');box.open=!!ctx.metadataBody;box.dataset.metadata='true';
      message(box,'Ownership applies to display fields. Matching uses original source evidence. Empty remote fields keep the fallback; clearing a locked field is explicit.');
      const records=[...(entry.matches||[]),...(entry.source_variants||[])];
      const form=document.createElement('form');box.append(form);
      for(const [name,label] of [['description','Synopsis'],['artwork','Artwork'],['author','Author'],['artist','Artist']]){
        const row=document.createElement('fieldset');row.dataset.metadataField=name;
        const choice=saved.fields[name]||{mode:'inherited'};
        row.innerHTML=`<legend>${label}</legend>`+select('Owner',[['inherited','Automatic display'],['locked','Locked value'],['source','Preferred attached source']],'mode')+
          select('Source record',records.map((r,index)=>[index,(r.source_name||r.source_id)+' / '+r.source_manga_id]),'source')+
          (name==='artwork'?select('Pinned artwork',records.filter(r=>r.thumbnail_url).map(r=>[r.thumbnail_url,(r.source_name||r.source_id)+' / '+r.source_manga_id]),'value'):`<label>Locked ${label.toLowerCase()}<textarea data-field="value" rows="${name==='description'?4:2}" maxlength="12000"></textarea></label>`)+
          '<label class="riCheck"><input type="checkbox" data-field="clear">Explicitly clear this locked field</label>';
        get(row,'mode').value=choice.mode;get(row,'source').value=String(Math.max(0,records.findIndex(r=>r.source_id===choice.source_id&&r.source_manga_id===choice.source_manga_id)));
        get(row,'value').value=choice.value??entry[name==='artwork'?'thumbnail_url':name]??'';
        const visibility=()=>{const mode=get(row,'mode').value;get(row,'source').parentElement.hidden=mode!=='source';get(row,'value').parentElement.hidden=mode!=='locked';get(row,'clear').parentElement.hidden=mode!=='locked';};
        get(row,'mode').onchange=visibility;visibility();form.append(row);
      }
      const save=document.createElement('button');save.type='submit';save.textContent='Save display ownership';form.append(save);
      form.onsubmit=event=>{event.preventDefault();const fields={};for(const row of form.querySelectorAll('[data-metadata-field]')){
        const mode=get(row,'mode').value,name=row.dataset.metadataField;
        if(mode==='source'){
          const record=records[Number(get(row,'source').value)];if(!record){status('Choose an attached source record.');return;}
          fields[name]={mode,source_id:record.source_id,source_manga_id:record.source_manga_id};
        }else fields[name]=mode==='locked'?{mode,value:get(row,'value').value,clear:get(row,'clear').checked}:{mode};
      }
        form.inert=true;write(save,()=>request('metadata-fields/'+id,'PUT',{revision:saved.revision,fields}),result=>{
          if(owner.isConnected){renderMetadata(result);status('Display ownership saved. Reopen the work to refresh its displayed metadata.');refreshEntryCoverImages(id);}
        }).finally(()=>{form.inert=false;restoreSaveFocus(owner,'[data-metadata] button[type=submit]');});
      };
      if(saved.history.length){
        const history=document.createElement('div');history.innerHTML=select('Retained ownership snapshots',saved.history.map(r=>[r.id,date(r.created_at)+' · '+r.reason+' · work '+r.old_entry_id]),'history');box.append(history);
        history.append(action('Restore selected ownership snapshot',()=>request('metadata-fields/'+id,'PUT',{revision:saved.revision,fields:{},restore_id:Number(get(history,'history').value)}),{after:result=>{if(owner.isConnected)renderMetadata(result);}}));
      }
    }
    function renderUpgrades(saved){
      upgradeOwner.querySelector('[data-upgrades]')?.remove();const box=panel(upgradeOwner,'Reviewed serving upgrades');box.open=!!ctx.servingBody;box.dataset.upgrades='true';
      message(box,'Opt in, sample both candidates, then review a chapter choice. Existing downloaded files and reading progress are retained. Source locks, quality cutoffs, stale samples and cooldowns can block changes.');
      const form=document.createElement('form');form.innerHTML=`<label class="riCheck"><input type="checkbox" data-field="enabled" ${saved.policy.enabled?'checked':''}>Enable reviewed upgrades</label>`;
      for(const [name,label] of [['cutoff','Target quality cutoff (1–100)'],['minimum_improvement','Minimum gain (quality points)'],['cooldown_hours','Cooldown (hours)'],['sample_max_age_hours','Maximum sample age (hours)']])form.innerHTML+=`<label>${label}<input type="number" data-field="${name}" min="1" max="${name.includes('hours')?8760:100}" value="${Number(saved.policy[name])}" required></label>`;
      const save=document.createElement('button');save.type='submit';save.textContent='Save upgrade policy';form.append(save);box.append(form);
      if(saved.retained?.length){
        const history=document.createElement('div');history.innerHTML=select('Retained upgrade policies',saved.retained.map(r=>[r.id,date(r.created_at)+' · '+r.reason+' · work '+r.old_entry_id]),'retained');
        const use=document.createElement('button');use.type='button';use.textContent='Use retained policy as a draft';use.onclick=()=>{
          const row=saved.retained.find(r=>r.id===Number(get(history,'retained').value));if(!row)return;
          get(form,'enabled').checked=row.policy.enabled;for(const key of ['cutoff','minimum_improvement','cooldown_hours','sample_max_age_hours'])get(form,key).value=row.policy[key];
          clear();status('Retained upgrade policy selected. Save upgrade policy to apply it.');
        };history.append(use);box.append(history);
      }
      form.onsubmit=event=>{event.preventDefault();const policy={enabled:get(form,'enabled').checked};for(const key of ['cutoff','minimum_improvement','cooldown_hours','sample_max_age_hours'])policy[key]=Number(get(form,key).value);
        form.inert=true;write(save,()=>request('serving-upgrades/'+id,'PUT',{revision:saved.revision,policy}),result=>{if(owner.isConnected)renderUpgrades(result);}).finally(()=>{form.inert=false;restoreSaveFocus(upgradeOwner,'[data-upgrades] button[type=submit]');});};
      const chooser=document.createElement('div');box.append(chooser);const preview=document.createElement('div');box.append(preview);
      const previewOwner=window.AutoSourcarrReimaginedCore.previewController();
      const clear=()=>{previewOwner.invalidate();preview.replaceChildren();};form.addEventListener('input',clear);
      chooser.append(action('Load known chapters',()=>api('/api/v1/entries/'+id+'/chapters'),{after:result=>{
        if(!owner.isConnected)return;clear();chooser.replaceChildren();chooser.innerHTML=select('Chapter',result.items.map(r=>[r.id,r.display_name||r.chapter_key]),'chapter')+'<div data-candidates></div>';
        const load=async()=>{
          clear();const chapter=Number(get(chooser,'chapter').value),target=chooser.querySelector('[data-candidates]');target.replaceChildren();if(!chapter)return;
          const detail=await api('/api/admin/chapters/'+chapter,{signal,suppressClientError:true});if(!owner.isConnected||Number(get(chooser,'chapter').value)!==chapter)return;
          target.innerHTML=select('Candidate',detail.candidates.map(r=>[r.id,(r.source_name||r.source_id)+' · quality '+(r.quality_score??'not sampled')]),'candidate');get(target,'candidate').onchange=clear;
          target.append(action('Sample selected candidate quality',()=>request('candidates/'+Number(get(target,'candidate').value)+'/quality','POST',{sample_count:5}),{after:result=>{
            status(result.complete?'Complete quality sample: '+result.quality_score:'Quality attempt incomplete; previous score retained. '+(result.reason||''));clear();
          }}));
          target.append(action('Preview serving change',()=>previewOwner.preview(()=>request('serving-upgrades/'+id+'/preview','POST',{chapter_id:chapter,candidate_id:Number(get(target,'candidate').value)})),{after:result=>{
            if(!box.isConnected||!result||previewOwner.current()!==result)return;preview.replaceChildren();message(preview,'Quality '+result.quality_before+' → '+result.quality_after+'. '+result.effect);
            if(!result.eligible){message(preview,result.blockers.join(' · '));return;}
            preview.append(action('Apply reviewed serving choice',()=>{const snapshot=previewOwner.current();if(!snapshot)throw Error('Preview again after changing the selection.');return request('serving-upgrades/'+id+'/apply','POST',{chapter_id:snapshot.chapter_id,candidate_id:snapshot.candidate_id,revision:snapshot.revision});},{after:()=>{clear();status('Future serving choice saved. Downloaded chapters are retained.');}}));
          }}));
        };
        get(chooser,'chapter').onchange=()=>load().catch(error=>status(error.message));load().catch(error=>status(error.message));
      }}));
    }
  }
  async function review(ctx){
    const {body,action,status,signal}=ctx,box=panel(body,'Simulate a matching policy');
    message(box,'Run the actual matcher on up to 32 work pairs, with the 0.94 floor, edition checks, recorded separations and original-member proof. Applying saves a policy for future work.');
    const data=await api('/api/admin/policy-simulator/options',{signal,suppressClientError:true});if(!box.isConnected)return;
    const form=document.createElement('form');form.innerHTML=select('Scope',[['source','One source'],['group','Custom source group']],'scope')+'<div data-subject></div>'+select('Direction',[['external','Between sources'],['internal','Within one source']],'direction')+
      select('Starting rule',data.presets.map((p,i)=>[i,p.name]),'preset')+'<label class="riCheck"><input type="checkbox" data-field="enabled" checked>Allow matching when all checks pass</label><label class="riCheck"><input type="checkbox" data-field="inherit">Restore inherited rule</label>';
    for(const [key,label,min,value] of [['title','Title similarity',.94,.97],['summary','Summary similarity',.01,.9],['author','Creator similarity',.01,.9],['tags','Informative tag overlap',.01,.7]])form.innerHTML+=`<div class="riRuleCheck"><label><input type="checkbox" data-check="${key}" ${key==='title'?'checked':''}>${label}</label><input aria-label="${label} minimum" type="number" data-threshold="${key}" min="${min}" max="1" step=".01" value="${value}"></div>`;
    form.innerHTML+='<label>Work pairs, one pair per line<textarea data-field="pairs" rows="4" placeholder="12, 34" required></textarea></label><button type="submit">Preview policy</button>';
    box.append(form);const output=document.createElement('div');box.append(output);const previewOwner=window.AutoSourcarrReimaginedCore.previewController();
    const invalidate=()=>{previewOwner.invalidate();output.replaceChildren();};form.addEventListener('input',invalidate);form.addEventListener('change',invalidate);
    function subjects(){const values=get(form,'scope').value==='source'?data.sources.map(r=>[r.source_id,(r.name||r.source_id)+' · '+r.lang]):data.groups.map(r=>[r.id,r.name]);form.querySelector('[data-subject]').innerHTML=select('Policy subject',values,'subject');}
    get(form,'scope').onchange=subjects;subjects();
    get(form,'preset').onchange=()=>{const rule=data.presets[Number(get(form,'preset').value)].rule;for(const key of ['title','summary','author','tags']){form.querySelector('[data-check="'+key+'"]').checked=key in rule.checks;if(key in rule.checks)form.querySelector('[data-threshold="'+key+'"]').value=rule.checks[key];}};
    form.onsubmit=event=>{event.preventDefault();invalidate();const checks={};for(const el of form.querySelectorAll('[data-check]'))if(el.checked)checks[el.dataset.check]=Number(form.querySelector('[data-threshold="'+el.dataset.check+'"]').value);
      const draft={scope:get(form,'scope').value,subject:get(form,'subject').value,direction:get(form,'direction').value,rule:get(form,'inherit').checked?null:{enabled:get(form,'enabled').checked,checks}},pairs=get(form,'pairs').value.trim().split(/\n/).map(line=>line.split(',').map(v=>Number(v.trim())));
      ctx.write(event.submitter,()=>previewOwner.preview(()=>request('policy-simulator/preview','POST',{draft,pairs})),result=>{
        if(!box.isConnected||!result||previewOwner.current()!==result)return;output.innerHTML='<div class="riTableScroll"><table><thead><tr><th>Pair</th><th>Current</th><th>Draft</th></tr></thead><tbody>'+result.pairs.map(r=>`<tr><td>${r.pair.join(' / ')}</td><td>${r.before.eligible?'Eligible':'Blocked'}<br>${esc(r.before.reason)}</td><td>${r.after.eligible?'Eligible':'Blocked'}<br>${esc(r.after.reason)}</td></tr>`).join('')+'</tbody></table></div>';
        message(output,result.scope_note+' Engine: '+result.engine);output.append(action('Apply reviewed policy',()=>{const snapshot=previewOwner.current();if(!snapshot)throw Error('Preview the current draft first.');return request('policy-simulator/apply','POST',{draft:snapshot.draft,pairs:snapshot.pairs.map(r=>r.pair),revision:snapshot.revision});},{after:()=>{invalidate();status('Policy saved. No selected pair was merged.');}}));
      });
    };
  }
  async function statusView(ctx){
    const {body,action,signal,status}=ctx,host=document.createElement('section');body.append(host);
    async function render(){const data=await api('/api/admin/research-improvements/status',{signal,suppressClientError:true});if(!host.isConnected)return;
      host.innerHTML='<h3>Observation freshness</h3><p class="muted">Viewing these saved clocks and metrics makes no source requests.</p><div class="riToolActions" data-actions></div><p>'+Number(data.pending_feed_pages)+' durable feed pages pending.</p><div class="riTableScroll"><table><thead><tr><th>Work / phase</th><th>Last complete</th><th>Next due</th><th>Outcome / wait</th></tr></thead><tbody>'+data.due.map(r=>`<tr><td>${esc(r.kind)} / ${esc(r.work_key)}</td><td>${esc(date(r.last_complete))}</td><td>${esc(date(r.next_due))}</td><td>${esc(r.outcome)} · ${esc(r.reason)}</td></tr>`).join('')+'</tbody></table></div><h3>Measured phases</h3><div class="riTableScroll"><table><thead><tr><th>Phase</th><th>Count</th><th>p50 ms</th><th>p95 ms</th></tr></thead><tbody>'+data.metrics.phases.map(r=>`<tr><td>${esc(r.phase)}</td><td>${r.count}</td><td>${r.p50_ms??'—'}</td><td>${r.p95_ms??'—'}</td></tr>`).join('')+'</tbody></table></div><p class="muted">'+esc(data.metrics.limits.memory_measure)+'. Last 128 samples per phase; process restart clears metrics.</p><p>Active decoders: '+data.metrics.decode.active+' · Peak: '+data.metrics.decode.peak_active+' · Peak owned input bytes: '+data.metrics.decode.peak_encoded_bytes+'</p><h3>Quality attempts</h3><ul>'+data.quality_attempts.map(r=>`<li>Candidate ${r.candidate_id} · ${esc(r.outcome)} · ${esc(date(r.attempted_at))} · ${r.unobserved_pages} unobserved · ${esc(r.reason)}</li>`).join('')+'</ul><h3>Engine compatibility</h3><p>Current baseline '+esc(data.engine.baseline)+' · Candidate '+esc(data.engine.target)+'</p><p class="muted">Native qualification pending. Promotion requires a build, populated upgrade, restart and rollback test. Current serving uses authoritative polling.</p><div data-engine-actions class="riToolActions"></div><pre data-engine-result></pre>';
      host.querySelector('[data-actions]').append(action('Refresh saved status',async()=>render()));host.querySelector('[data-engine-result]').textContent=JSON.stringify(data.engine.last_check,null,2);
      for(const engine of ['source','main'])host.querySelector('[data-engine-actions]').append(action('Check '+engine+' engine API',()=>apiV1({timeoutMs:30000}).checkEngineCompatibility(engine),{after:result=>{if(host.isConnected)host.querySelector('[data-engine-result]').textContent=JSON.stringify(result,null,2);status('API capability check saved. Native upgrade qualification remains separate.');}}));
      status('Saved observations only. Automatic monitoring and feeds yield to browser activity.');
    }
    await render();
  }
  window.AutoSourcarrArrControls={manage,review,status:statusView};
})();
