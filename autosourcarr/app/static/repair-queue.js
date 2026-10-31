/* Compact state groups and one stable inspector. Polling never rebuilds input forms. */
(()=>{
  'use strict';
  const panel=document.getElementById('tasksRepairsPanel');if(!panel)return;
  const get=id=>document.getElementById(id),groups=panel.querySelector('.taskSections'),raw=get('sourceRawInput'),evidence=get('sourceRepairDetails');
  const layout=document.createElement('div');layout.className='riRepairLayout';groups.before(layout);
  const queue=document.createElement('section');queue.className='riRepairQueue';layout.append(queue);queue.append(groups);groups.className='riRepairGroups';
  const needs=document.createElement('section');needs.className='taskSection';needs.innerHTML='<div class="taskSectionHead"><h3>Needs input</h3><span id="repairTasksInputCount">0</span></div><div class="tasksList" id="repairTasksInputList"></div>';groups.prepend(needs);
  const filter=document.createElement('div');filter.className='scopeChips riRepairStateFilters';filter.setAttribute('role','group');filter.setAttribute('aria-label','Repair state');queue.prepend(filter);
  const info=document.createElement('p');info.className='srOnly';filter.after(info);
  const aside=document.createElement('aside');aside.className='riRepairInspector';aside.hidden=true;aside.setAttribute('aria-label','Selected repair or source');layout.append(aside);
  const heading=document.createElement('h3'),meta=document.createElement('p'),message=document.createElement('p'),actions=document.createElement('div');meta.className='muted';message.className='riRepairMessage';actions.className='riToolActions';aside.append(heading,meta,message,actions,raw,evidence);
  const inputsHelp=get('repairInputsHelp');if(inputsHelp)aside.append(inputsHelp);
  const states=['all','input','running','completed','failed'],names={all:'All',input:'Needs input',running:'Running / queued',completed:'Completed / ready',failed:'Failed / blocked'};
  const lists={input:get('repairTasksInputList'),running:get('repairTasksRunningList'),completed:get('repairTasksCompletedList'),failed:get('repairTasksFailedList')};
  const counts={input:get('repairTasksInputCount'),running:get('repairTasksRunningCount'),completed:get('repairTasksCompletedCount'),failed:get('repairTasksFailedCount')};
  let rows=[],selected=null,selection=null,state='all',signature='',actionsSignature='';
  function classify(row){return row.kind==='source-raw-input'||row.repair_state==='awaiting-raws'?'input':row.state==='running'?'running':row.state==='completed'?'completed':'failed';}
  const pkg=row=>String(row.raw_package||row.context||'');
  function busy(){return window.AutoSourcarrRepairTasks?.isBusy?.()||window.AutoSourcarrSourceRaw?.isBusy?.();}
  function choose(id,{showRaw=false}={}){
    if(busy()){toast('Finish the current repair/code action before selecting another item.');return false;}
    if(selected!==id){window.AutoSourcarrRepairTasks?.selectionChanged?.();if(!showRaw)raw.hidden=true;actionsSignature='';selection=null;}
    selected=id;selection=rows.find(row=>row.id===id)||selection;aside.hidden=false;paintInspector();paintRows();return true;
  }
  function paintInspector(){
    const row=rows.find(item=>item.id===selected);
    if(!row){if(selected){heading.textContent=selection?pkg(selection):'Selected repair';message.textContent='This task is outside the current filter or retained snapshot. Select another item or refresh Tasks; existing input is retained.';actions.replaceChildren();actionsSignature='';}return;}
    selection=row;heading.textContent=pkg(row)||row.name||'Repair task';meta.textContent=[names[classify(row)],window.AutoSourcarrRepairTasks?.metadata(row)].filter(Boolean).join(' · ');message.textContent=row.message||row.error||'No additional message recorded.';
    const html=window.AutoSourcarrRepairTasks?.actions(row)||(row.cancellable?`<button data-cancel-queue-task="${esc(row.id)}">Cancel task</button>`:'');
    const next=JSON.stringify([html,!!busy()]);
    if(next!==actionsSignature){
      const focus=actions.contains(document.activeElement)?document.activeElement.dataset.repairAction:null;
      actions.innerHTML=html;actionsSignature=next;
      if(busy())for(const button of actions.querySelectorAll('button'))button.disabled=true;
      if(focus)actions.querySelector('[data-repair-action="'+focus+'"]')?.focus({preventScroll:true});
    }
  }
  function paintRows(){
    const active=document.activeElement?.dataset?.repairSelect;
    for(const key of states.filter(k=>k!=='all')){
      const items=rows.filter(row=>classify(row)===key);counts[key].textContent=String(items.length);lists[key].parentElement.hidden=state!=='all'&&state!==key;
      if(!items.length){lists[key].innerHTML='<p class="muted riQueueEmpty">No '+names[key].toLowerCase()+' items'+(state==='all'?' in this snapshot.':' for the selected origin. Use All repairs above to include both origins.')+'</p>';continue;}
      lists[key].innerHTML='<div class="riTableScroll"><table class="riRepairTable"><thead><tr><th>Source / task</th><th>State</th><th>Updated</th><th>Inspect</th></tr></thead><tbody>'+items.map(row=>{
        const source=pkg(row),name=source?source.split('.').at(-1):row.name||'Source repair';
        const label=key==='input'?'Code needed':String(row.repair_state||row.activity||row.state).replaceAll('-',' ');
        const stamp=row.updated_at||row.finished_at||row.created_at,date=stamp?(Number.isFinite(Number(stamp))?new Date(Number(stamp)*1000):new Date(stamp)):null;
        return `<tr class="${selected===row.id?'selected':''}"><td><strong title="${esc(source)}">${esc(name)}</strong><small>${esc(row.name||'Source repair')}</small></td><td>${esc(label)}</td><td>${date&&!Number.isNaN(date.getTime())?esc(date.toLocaleString()):'Not recorded'}</td><td><button type="button" data-repair-select="${esc(row.id)}" aria-pressed="${selected===row.id}">${selected===row.id?'Selected':'Inspect'}</button></td></tr>`;
      }).join('')+'</tbody></table></div>';
    }
    for(const button of filter.querySelectorAll('[data-repair-state]')){button.classList.toggle('active',button.dataset.repairState===state);button.setAttribute('aria-pressed',String(button.dataset.repairState===state));}
    info.textContent=rows.length?`${rows.filter(r=>classify(r)==='input').length} need input · ${rows.filter(r=>classify(r)==='running').length} running or queued · ${rows.filter(r=>classify(r)==='completed').length} completed or ready · ${rows.filter(r=>classify(r)==='failed').length} failed or blocked`:'No retained repair tasks for this origin. Start a repair or open Source code archive above.';
    if(active)queue.querySelector('[data-repair-select="'+CSS.escape(active)+'"]')?.focus({preventScroll:true});
  }
  for(const key of states){const button=document.createElement('button');button.type='button';button.dataset.repairState=key;button.textContent=names[key];button.onclick=()=>{state=key;paintRows();};filter.append(button);}
  queue.addEventListener('click',event=>{const button=event.target.closest('[data-repair-select]');if(button)choose(button.dataset.repairSelect);});
  aside.addEventListener('click',event=>{const button=event.target.closest('[data-cancel-queue-task]');if(button)window.cancelTask(button.dataset.cancelQueueTask,button);});
  document.addEventListener('autosourcarr:raw-selected',event=>{
    const row=event.detail.jobId?rows.find(r=>r.repair_id===event.detail.jobId):rows.find(r=>r.kind==='source-raw-input'&&pkg(r)===event.detail.package);
    if(row)choose(row.id,{showRaw:true});else{selected=null;selection=null;heading.textContent='Source code archive';meta.textContent=event.detail.package;message.textContent='Code and reconstruction actions below belong to this installed source.';actions.replaceChildren();aside.hidden=false;}
  });
  window.AutoSourcarrRepairQueue={
    render(items){rows=items;const next=JSON.stringify(items);if(next!==signature){signature=next;paintRows();}paintInspector();},
    selectRepair(id){const row=rows.find(r=>r.repair_id===id);choose(row?.id||'source-repair:'+id);},
    hasSelection:()=>!!selected,
    summary:()=>info.textContent,
    refresh(){actionsSignature='';paintInspector();},
    selection:()=>selected
  };
  paintRows();
  if(typeof renderTasks==='function')renderTasks();
})();
