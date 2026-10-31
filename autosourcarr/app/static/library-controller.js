/* The library owns its paged projection. Entry inspectors retain their controllers. */
(() => {
  'use strict';
  const byId=id=>document.getElementById(id);
  let payload=null,request=null,generation=0,signature='',cursor='',previous=[],pageNumber=1;
  if(document.documentElement.dataset.preview==='sample'){
    byId('riLibraryPageSize').add(new Option('6 (sample)','6'));byId('riLibraryPageSize').value='6';
  }
  const scope=()=>({missing:'missing_downloads'}[workbenchState.libraryScope]||workbenchState.libraryScope||'all');
  function query(){return {search:byId('subbedSearch').value,source:byId('subbedSource').value,
    publication:byId('subbedPublicationStatus').value,title_state:byId('subbedTitleState').value,
    match_status:byId('subbedStatus').value,sort:byId('subbedSort').value,source_count:byId('subbedMinMatches').value,
    record_count:byId('subbedEntryMatches').value,scope:scope(),limit:byId('riLibraryPageSize').value};}
  function fillOptions(select,values,label){
    const current=select.value;select.replaceChildren(new Option(label,''));
    for(const [key,name] of values)select.add(new Option(name,key));
    if([...select.options].some(o=>o.value===current))select.value=current;
  }
  async function load({reset=false}={}){
    const values=query(),nextSignature=JSON.stringify(values);
    if(reset||nextSignature!==signature){cursor='';previous=[];pageNumber=1;signature=nextSignature;}
    const own=++generation;request?.abort();const controller=new AbortController();request=controller;
    byId('subbedCount').textContent='Loading library…';byId('riLibraryNext').disabled=true;
    try{
      const result=await api('/api/admin/library?'+new URLSearchParams({...values,cursor}),{signal:controller.signal,timeoutMs:15000,suppressClientError:true});
      if(own!==generation||controller.signal.aborted)return;
      payload=result;
      const stats=Object.fromEntries(result.entries.map(row=>[String(row.id),row.library_stats||{}]));
      subbedData={...result,subscription:{...result.subscription,entry_ids:result.entries.map(row=>row.id),stats_by_entry:stats},discovery_outcomes:[]};
      fillOptions(byId('subbedSource'),result.sources.map(s=>[s.source_id,`${s.name} (${s.lang||'?'})`]),'All connected sources');
      render();
    }catch(error){if(own===generation&&!controller.signal.aborted){byId('subbedCount').textContent='Library unavailable: '+error.message;byId('subbedNotice').hidden=false;byId('subbedNotice').textContent='Your current page and saved decisions are retained. Refresh to try again.';}}
    finally{if(request===controller)request=null;}
    return payload;
  }
  function render(){
    if(!payload||!subbedData)return false;
    const byEntry=new Map();for(const row of subbedData.matches||[]){if(!byEntry.has(row.entry_id))byEntry.set(row.entry_id,[]);byEntry.get(row.entry_id).push(row);}
    const cards=window.AutoSourcarrReimagined?.renderLibrary(subbedData.entries,subbedData,byEntry);
    if(cards===false){
      byId('subbedTable').innerHTML='<thead><tr><th>Manga</th><th>Sources</th><th>Downloads</th><th>Coverage</th><th>Controls</th></tr></thead><tbody>'+subbedData.entries.map(e=>{
        const o=e.observation||{},download=o.download_state==='checked'?`${o.downloaded} / ${o.chapter_count}`:'Not checked';
        return `<tr><td><button data-library-entry="${e.id}">${esc(e.preferred_title)}</button></td><td>${Number(e.source_count||0)}</td><td>${esc(download)}</td><td>${o.coverage_state==='checked'?Number(o.source_gaps||0)+' source gaps':'Verification needed'}</td><td><button data-manage-entry="${e.id}">Manage</button></td></tr>`;
      }).join('')+'</tbody>';
    }
    byId('riLibraryHeadingCount').textContent=' / '+Number(payload.total).toLocaleString();
    byId('subbedCount').textContent=`${payload.entries.length} shown · ${Number(payload.total).toLocaleString()} matching works`;
    byId('riLibraryPage').textContent=`Page ${pageNumber} · ${payload.total} works`;
    byId('riLibraryPrevious').disabled=!previous.length;byId('riLibraryNext').disabled=!payload.next_cursor;
    const notice=byId('subbedNotice');notice.hidden=payload.subscription?.ok===true&&!payload.subscription?.refreshing;
    if(!notice.hidden)notice.textContent=payload.subscription?.error||'Using the last library snapshot while its membership refreshes.';
    byId('riWantedExplanation').hidden=!['missing_downloads','source_gaps','verification'].includes(scope());
    return true;
  }
  async function next(){if(!payload?.next_cursor)return;previous.push(cursor);cursor=payload.next_cursor;pageNumber++;await load();}
  async function back(){if(!previous.length)return;cursor=previous.pop();pageNumber--;await load();}
  for(const suffix of ['Source','MinMatches','EntryMatches','TitleState','Status','PublicationStatus','Sort'])byId('subbed'+suffix).onchange=()=>load({reset:true});
  byId('riLibraryPageSize').onchange=()=>load({reset:true});
  byId('riLibraryPrevious').onclick=()=>back();byId('riLibraryNext').onclick=()=>next();
  document.addEventListener('click',event=>{
    if(event.target.closest('[data-library-scope]'))queueMicrotask(()=>load({reset:true}));
    if(event.target.closest('[data-safe-task-retry]')){
      const button=event.target.closest('[data-safe-task-retry]');button.disabled=true;
      api('/api/admin/tasks/'+encodeURIComponent(button.dataset.safeTaskRetry)+'/retry-safe',{method:'POST',body:'{}'}).then(()=>loadTasks()).catch(e=>toast(e.message)).finally(()=>{button.disabled=false;});
    }
  });
  window.AutoSourcarrLibrary={load,render,hasData:()=>!!payload};
})();
