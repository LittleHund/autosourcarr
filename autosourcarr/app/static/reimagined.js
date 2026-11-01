/* Sonarr v5-style workspace; existing controllers continue to own every mutation. */
(() => {
  'use strict';
  const core=window.AutoSourcarrReimaginedCore;
  const byId=id=>document.getElementById(id);
  const views=new Set(['posters','overview','table']);
  const local=(key,fallback)=>{try{return localStorage.getItem('autosourcarr.reimagined.'+key)||fallback;}catch(_){return fallback;}};
  const remember=(key,value)=>{try{localStorage.setItem('autosourcarr.reimagined.'+key,value);}catch(_){}};
  let libraryView=local('libraryView','posters'), browseView=local('browseView','posters');
  if(!views.has(libraryView))libraryView='posters';
  if(!views.has(browseView))browseView='posters';
  let navigating=false, searchGeneration=0,searchRequest=null,searchTimer=null,librarySignature='';
  let returnFocus=null,sidebarInert=null;
  const metric=value=>value===null||value===undefined?'—':Number(value).toLocaleString();
  const icon=name=>`<svg class="riIcon" aria-hidden="true"><use href="#ri-${name}"/></svg>`;
  const routePaths={subbed:'/series',browse:'/add',matrix:'/matching',duplicates:'/duplicates','mixed-entries':'/mixed','manual-actions':'/decisions',tasks:'/activity',imports:'/imports',sources:'/sources',attention:'/attention',settings:'/settings',about:'/system',statistics:'/statistics'};

  function activePath(tab) {
    if(tab==='subbed')return ({missing:'/wanted',missing_downloads:'/wanted',source_gaps:'/wanted/sources',verification:'/wanted/verification',unmatched:'/unmatched',monitored:'/series/monitored',catalog:'/catalog'}[workbenchState.libraryScope]||'/series');
    if(tab==='tasks'&&byId('tasksRepairsPanel').classList.contains('active'))return '/activity/repairs';
    return routePaths[tab]||'/series';
  }
  function syncNavigation(tab=document.querySelector('main > .tab.active')?.id||'subbed') {
    const path=activePath(tab);
    for(const button of document.querySelectorAll('.appNav [data-route]')) {
      const active=button.dataset.route===path||((path==='/unmatched'||path.startsWith('/wanted/'))&&button.dataset.route==='/wanted')||((path==='/catalog'||path.startsWith('/series/'))&&button.dataset.route==='/series');
      button.classList.toggle('active',active);
      if(active)button.setAttribute('aria-current','page');else button.removeAttribute('aria-current');
    }
    const libraryHeading=byId('riLibraryHeading');
    if(libraryHeading)libraryHeading.firstChild.textContent=['missing','missing_downloads','source_gaps','verification'].includes(workbenchState.libraryScope)?'Wanted ':({unmatched:'Needs a match ',catalog:'Saved catalog ',monitored:'Monitored '}[workbenchState.libraryScope]||'Manga ');
    const subtitle=document.querySelector('#subbed .pageHeading p');
    if(subtitle)subtitle.textContent=['missing','missing_downloads','source_gaps','verification'].includes(workbenchState.libraryScope)?'Checked missing downloads, source gaps and observations needing verification.':workbenchState.libraryScope==='unmatched'?'Subscribed manga waiting for a source match.':'Your library, connected across sources.';
    document.title=(tab==='subbed'?(libraryHeading?.firstChild.textContent||'Manga'):(document.querySelector('#'+tab+' .pageHeading h2')?.textContent||'AutoSourcarr')).trim()+' · AutoSourcarr';
    if(!navigating&&location.hash!=='#'+path)history.replaceState(null,'','#'+path);
  }
  async function navigate(path,{push=true,event=null}={}) {
    const route=core.parseRoute('#'+path);if(!route)return;
    if(searchRequest)searchRequest.abort();
    if(event?.isTrusted){
      if(['subbed','matrix'].includes(route.tab)&&activeInspector[route.tab]!==null)window.closeInlineEntry(route.tab,event);
      if(route.tab==='browse'&&browseInspectorIsOpen())window.closeBrowseEntry(event);
    }
    closeSidebar();
    if(push&&location.hash!=='#'+path)history.pushState(null,'','#'+path);
    navigating=true;
    try {
      if(route.tab==='subbed') {
        workbenchState.libraryScope=route.scope||'all';
        document.querySelectorAll('[data-library-scope]').forEach(button=>{const on=button.dataset.libraryScope===workbenchState.libraryScope;button.classList.toggle('active',on);button.setAttribute('aria-pressed',String(on));});
      }
      if(route.tab==='sources'&&route.section)setSourceSubtab(route.section);
      if(route.tab==='settings'&&route.section)setSettingsSubtab(route.section);
      const operation=route.tab==='tasks'&&route.section?window.openTasksSection(route.section):switchTab(route.tab);
      syncNavigation(route.tab);
      await operation;
    } finally {navigating=false;syncNavigation();}
  }

  function closeSidebar({restoreFocus=true}={}) {
    byId('riSidebar').inert=matchMedia('(max-width: 767px)').matches;
    if(!document.documentElement.classList.contains('riSidebarOpen'))return;
    document.documentElement.classList.remove('riSidebarOpen');
    byId('riMenuToggle').setAttribute('aria-expanded','false');
    byId('riSidebarScrim').hidden=true;
    if(sidebarInert)for(const [element,value] of sidebarInert)element.inert=value;
    sidebarInert=null;
    if(restoreFocus)byId('riMenuToggle').focus();
  }
  function openSidebar() {
    byId('riSidebar').inert=false;
    document.documentElement.classList.add('riSidebarOpen');
    byId('riMenuToggle').setAttribute('aria-expanded','true');byId('riSidebarScrim').hidden=false;
    sidebarInert=new Map([byId('mainContent'),document.querySelector('.activityStrip')].map(element=>[element,element.inert]));
    for(const element of sidebarInert.keys())element.inert=true;
    (document.querySelector('.appNav [aria-current="page"]')||document.querySelector('.appNav button')).focus();
  }

  function progressHtml(stats) {
    const p=core.chapterProgress(stats);
    const text=`${metric(p.downloaded)} / ${metric(p.total)} chapters`;
    return `<div class="riProgress ${p.complete?'complete':''} ${p.percent===null?'unknown':''}" role="img" aria-label="${esc(text)}"><span style="width:${p.percent??0}%"></span></div><span class="riChapterCount">${esc(text)}</span>`;
  }
  function libraryCard(entry,d,byEntry) {
    const id=Number(entry.id),stats=subscriptionStatsFor(d,id),matches=byEntry.get(id)||[];
    const sourceNames=new Map((d.sources||[]).map(source=>[String(source.source_id),source.name||source.display_name]));
    const names=[...new Set(matches.map(match=>sourceNames.get(String(match.source_id))).filter(Boolean))];
    const status=entry.status_label||'Unknown';
    const statusTone=String(entry.status||status).toLowerCase().includes('ongoing')?'ongoing':String(status).toLowerCase().includes('finish')?'finished':'other';
    const description=String(entry.description||'No synopsis is available yet.');
    const title=entry.preferred_title||'Untitled';
    return `<article class="riPoster" data-entry-id="${id}"><button type="button" class="riPosterButton" data-library-entry="${id}" aria-label="Open ${esc(title)}"><span class="riPosterArtwork"><span class="riPosterFallback" aria-hidden="true">${esc(title)}</span><img src="${entryCoverUrl(id)}" data-entry-cover="${id}" alt="" loading="lazy" decoding="async"><span class="riPosterStatus ${statusTone}">${esc(status)}</span><span class="riPosterOpen">Open manga ${icon('arrow')}</span></span><span class="riPosterInfo"><strong class="riPosterTitle">${esc(title)}</strong><span class="riPosterMeta">${Number(entry.match_count||0)} source${Number(entry.match_count||0)===1?'':'s'}<span> · </span>${entry.manual_preferred_title?'Custom title':'Automatic title'}</span><span class="riPosterDescription">${esc(shortText(description,420))}</span><span class="riPosterSources">${esc(names.slice(0,4).join(' · ')||'No source match yet')}</span><span class="riPosterCoverage">${progressHtml(stats)}</span></span></button><div class="riPosterActions"><button type="button" data-manage-entry="${id}">Manage${entry.monitored?' · monitored':''}</button></div></article>`;
  }
  function renderLibrary(entries,d,byEntry) {
    byId('riLibraryHeadingCount').textContent=' / '+Number(d.total??entries.length).toLocaleString();
    const grid=byId('riLibraryGrid'),table=byId('subbedTable').closest('.tablewrap');
    grid.hidden=libraryView==='table';table.hidden=libraryView!=='table';
    grid.dataset.view=libraryView;
    if(libraryView==='table')return false;
    const filterState=['Search','Source','MinMatches','EntryMatches','TitleState','Status','PublicationStatus'].map(suffix=>byId('subbed'+suffix).value);
    const signature=JSON.stringify([libraryView,workbenchState.libraryScope,filterState,entries,Array.from(byEntry),d.subscription?.stats_by_entry,d.subscription?.ok]);
    if(signature===librarySignature)return true;
    librarySignature=signature;
    const focused=grid.contains(document.activeElement)?document.activeElement?.dataset.libraryEntry:null;
    if(!entries.length) {
      const filtered=workbenchState.libraryScope!=='all'||byId('subbedSearch').value.trim()||byId('subbedSource').value||['MinMatches','EntryMatches'].some(suffix=>byId('subbed'+suffix).value)||['TitleState','Status','PublicationStatus'].some(suffix=>byId('subbed'+suffix).value!=='any');
      const available=d.subscription?.ok===true;
      const title=!available?'Library snapshot unavailable':filtered?'No manga match these filters':'Your library starts here';
      const note=!available?'Check the connection details above and refresh to try again. Your saved decisions stay in place.':filtered?'Try another title, source or publication status. Unknown chapter coverage stays separate from confirmed gaps.':'Use Discover to find a work, then open Library & serving profile to add it. You can also import a reader backup.';
      const action=!available?'<button type="button" data-refresh-library>Refresh library</button>':filtered?'<button type="button" data-clear-library-filters>Clear filters</button>':'<button type="button" data-route="/imports">Import a library</button>';
      grid.innerHTML=`<div class="riEmpty">${icon(filtered?'search':'book')}<h3>${title}</h3><p>${note}</p>${action}</div>`;
    } else grid.innerHTML=entries.map(entry=>libraryCard(entry,d,byEntry)).join('');
    if(focused)grid.querySelector(`[data-library-entry="${Number(focused)}"]`)?.focus({preventScroll:true});
    return true;
  }
  function setView(kind,view) {
    if(!views.has(view))return;
    if(kind==='library'){libraryView=view;remember('libraryView',view);librarySignature='';if(subbedData)renderMatrix('subbed');}
    else{browseView=view;remember('browseView',view);byId('browseGrid').dataset.view=view;}
    for(const button of document.querySelectorAll(`[data-view-kind="${kind}"]`))button.setAttribute('aria-pressed',String(button.dataset.view===view));
    document.querySelector(`[data-view-label="${kind}"]`).textContent=view[0].toUpperCase()+view.slice(1);
    document.querySelectorAll('.riViewMenu').forEach(menu=>menu.open=false);
  }
  function viewMenu(kind) {
    const menu=document.createElement('details');menu.className='riViewMenu toolbarMenu';
    menu.innerHTML=`<summary>${icon('grid')}<span data-view-label="${kind}">Posters</span></summary><div class="menuContent" role="group" aria-label="${kind==='library'?'Library':'Discover'} view">${['posters','overview','table'].map(view=>`<button type="button" data-view-kind="${kind}" data-view="${view}" aria-pressed="${view===(kind==='library'?libraryView:browseView)}">${icon(view==='posters'?'grid':'list')}${view[0].toUpperCase()+view.slice(1)}</button>`).join('')}</div>`;
    return menu;
  }
  function createToolbars() {
    for(const tab of document.querySelectorAll('main > .tab')) {
      const heading=tab.querySelector(':scope > .pageHeading');if(!heading)continue;
      const actions=heading.querySelector(':scope > .statusActions');if(!actions)continue;
      const toolbar=document.createElement('div');toolbar.className='riPageToolbar';toolbar.setAttribute('aria-label',(heading.querySelector('h2')?.textContent||'Page')+' actions');
      actions.classList.add('riPageActions');toolbar.append(actions);heading.before(toolbar);
      if(tab.id==='settings') {
        toolbar.prepend(byId('runSetup'));
        const intro=heading.querySelector(':scope > p');if(intro){intro.classList.add('riSettingsIntro');heading.after(intro);}
      }
      if(tab.id==='subbed') {
        const add=document.createElement('button');add.type='button';add.dataset.route='/add';add.className='riAddButton';add.innerHTML=icon('plus')+'Discover manga';actions.prepend(add);
        byId('loadSubbed').innerHTML=icon('refresh')+'Refresh & scan';
        const more=document.createElement('details');more.className='toolbarMenu riLibraryMaintenance';more.innerHTML='<summary>Search & repair</summary><div class="menuContent"></div>';
        more.querySelector('.menuContent').append(byId('searchAllSubbed'),byId('repairUnmatchedSubbed'));actions.append(more);
        const controls=document.createElement('div');controls.className='riToolbarRight';controls.append(viewMenu('library'));toolbar.append(controls);
      }
      if(tab.id==='browse') {const controls=document.createElement('div');controls.className='riToolbarRight';controls.append(viewMenu('browse'));toolbar.append(controls);}
    }
    for(const id of ['matrixSearch','subbedSearch']){byId(id).type='search';byId(id).placeholder='Filter manga…';}
    // Move existing filter controls; their values, listeners and safe-state IDs remain intact.
    for(const kind of ['matrix','subbed']) {
      const details=document.querySelector('#'+kind+' details.advancedFilters .menuContent');
      for(const suffix of ['MinMatches','EntryMatches'])details.append(byId(kind+suffix));
    }
    const size=document.querySelector('.browsePageSizeControl');
    size.classList.add('riInlinePageSize');
    byId('browseGrid').dataset.view=browseView;
  }

  function renderStatistics(health) {
    if(!health)return;
    const values=core.summarizeHealth(health),root=byId('riStatisticsMetrics');if(!root)return;
    const cards=[['Manga in library',values.subscribed,'Current main Suwayomi snapshot','book'],['Downloaded chapters',values.downloaded,'Reported by the main library','import'],['Catalog entries',values.entries,'Works discovered across sources','grid'],['Enabled sources',values.sources,'Source families available for discovery','source']];
    root.innerHTML=cards.map(([label,value,note,name])=>`<article class="riMetricCard">${icon(name)}<span>${esc(label)}</span><strong>${metric(value)}</strong><p>${esc(value===null?'Unavailable in the current snapshot':note)}</p></article>`).join('');
    byId('riStatisticsStatus').textContent=health.reimagined_preview?'Sample library statistics · local preview data':'Statistics from the current instance snapshot. Unavailable values remain unknown.';
    byId('riCoverageChart').innerHTML=`<div class="riCoverageNumber"><strong>${values.matchedPercent===null?'—':values.matchedPercent+'%'}</strong><span>of catalog entries matched</span></div><div class="riCoverageBar"><span style="width:${values.matchedPercent??0}%"></span></div><div class="riChartLegend"><span><i></i>Matched <strong>${metric(values.matched)}</strong></span><span>Unmatched <strong>${metric(values.unmatched)}</strong></span></div><button type="button" data-route="/matching">Review source coverage ${icon('arrow')}</button>`;
    const statuses=health.stats?.catalog?.publication_statuses||{};
    const total=Object.values(statuses).reduce((sum,n)=>sum+(Number(n)||0),0);
    byId('riPublicationChart').innerHTML=Object.entries(statuses).sort((a,b)=>b[1]-a[1]).map(([label,count])=>`<div class="riPublicationRow"><span>${esc(label)}</span><div><span style="width:${total?Number(count)/total*100:0}%"></span></div><strong>${metric(count)}</strong></div>`).join('')||'<p class="muted">No publication status observations yet.</p>';
    byId('riProvenanceMetrics').innerHTML=[['Visible chapters',values.chapters],['Source candidates',values.candidates],['Served history records',values.history]].map(([label,value])=>`<div><strong>${metric(value)}</strong><span>${esc(label)}</span></div>`).join('');
    if(health.reimagined_preview)byId('riPreviewBadge').hidden=false;
  }

  function closeSearch() {searchGeneration++;clearTimeout(searchTimer);searchRequest?.abort();byId('riSearchDialog').close();returnFocus?.focus?.({preventScroll:true});}
  function openSearch() {
    closeSidebar({restoreFocus:false});returnFocus=document.activeElement;
    const dialog=byId('riSearchDialog');if(dialog.open)return;
    dialog.showModal();byId('riCommandQuery').value='';byId('riCommandQuery').focus();
    showSearchResults((subbedData?.entries||browseState.items||[]).slice(0,8),'Your loaded titles');
  }
  function showSearchResults(items,status) {
    byId('riCommandResults').innerHTML=items.map(entry=>`<div role="listitem"><button type="button" data-search-entry="${Number(entry.id)}"><img src="${entryCoverUrl(Number(entry.id))}" alt="" loading="lazy"><span><strong>${esc(entry.preferred_title||'Untitled')}</strong><small>${Number(entry.match_count||0)} source matches · ${esc(entry.status_label||'Unknown')}</small></span>${icon('arrow')}</button></div>`).join('');
    byId('riCommandStatus').textContent=status||(items.length?items.length+' matching titles':'No saved titles match. Try another name or use Discover to search your sources.');
  }
  async function searchCatalog() {
    const generation=++searchGeneration;searchRequest?.abort();
    const query=byId('riCommandQuery').value.trim();
    if(!query){showSearchResults((subbedData?.entries||[]).slice(0,8),'Your loaded titles');return;}
    const controller=new AbortController();searchRequest=controller;byId('riCommandStatus').textContent='Searching saved titles…';
    const portable=query.startsWith('asc1:');
    const url=portable?'/api/v1/portable-identities/resolve':'/api/v1/catalogue/entries?search='+encodeURIComponent(query)+'&limit=12';
    const options=portable?{method:'POST',body:JSON.stringify({reference:query}),noMutationRefresh:true}:{};
    try {const data=await api(url,{...options,signal:controller.signal,timeoutMs:8000,suppressClientError:true});if(generation===searchGeneration&&byId('riSearchDialog').open)showSearchResults(data.items||[],portable?(data.ambiguous?'This reference belongs to multiple saved entries. Review their editions before choosing.':data.items?.length?'Saved entry for this media ID':'This media ID has no saved association on this instance.'):'');}
    catch(error){if(generation===searchGeneration&&!controller.signal.aborted){showSearchResults([]);byId('riCommandStatus').textContent='Search unavailable: '+error.message;}}
    finally{if(searchRequest===controller)searchRequest=null;}
  }
  function openSearchEntry(button,event) {
    const id=Number(button.dataset.searchEntry);closeSearch();
    if(!Number.isSafeInteger(id)||id<=0)return;
    switchTab('browse',{load:false});window.openBrowseEntry(id,event);
  }

  const originalPalette=applyAppearancePalette;
  applyAppearancePalette=function(style,theme,...rest) {
    originalPalette(style,theme,...rest);
    const light=style==='paper',root=document.documentElement;
    root.dataset.riLight=String(light);
    const colors=light?{bg:'oklch(97% .010 260)',panel:'oklch(94% .011 260)',panel2:'oklch(91% .012 260)',chrome:'oklch(88% .014 260)',control:'oklch(94% .011 260)','control-hover':'oklch(88% .014 260)',line:'oklch(84% .012 260)',text:'oklch(18% .014 260)',muted:'oklch(44% .012 260)',good:'oklch(46% .115 148)',warn:'oklch(48% .100 75)',bad:'oklch(50% .165 28)'}:
      {bg:'oklch(24% .014 260)',panel:'oklch(29% .015 260)',panel2:'oklch(27% .014 260)',chrome:'oklch(20% .015 260)',control:'oklch(29% .015 260)','control-hover':'oklch(33% .015 260)',line:'oklch(36% .016 260)',text:'oklch(90% .007 260)',muted:'oklch(68% .011 260)',good:'oklch(72% .115 148)',warn:'oklch(82% .150 80)',bad:'oklch(74% .150 28)'};
    if(['neutral','paper'].includes(style)) {
      for(const [key,value] of Object.entries(colors))root.style.setProperty('--'+key,value);
      if(theme==='blue')for(const [key,value] of Object.entries({'accent':light?'oklch(44% .130 215)':'oklch(82% .115 215)','accent-text':light?'oklch(44% .130 215)':'oklch(82% .115 215)','accent-border':light?'oklch(60% .130 215)':'oklch(70% .130 215)','accent-soft':light?'oklch(91% .025 215)':'oklch(30% .035 215)','on-accent':light?'#fff':'#10252b','progress':light?'oklch(50% .130 215)':'oklch(70% .115 215)'}))root.style.setProperty('--'+key,value);
    }
  };

  function initialize() {
    createToolbars();applyTheme(readLocalUiPreference('theme')||'blue',readLocalUiPreference('customColor'));
    for(const button of document.querySelectorAll('.appNav [data-route]'))button.onclick=event=>navigate(button.dataset.route,{event}).catch(error=>toast(error.message));
    document.addEventListener('click',event=>{
      if(event.target.closest('[data-clear-library-filters]')){
        for(const suffix of ['Search','Source','MinMatches','EntryMatches'])byId('subbed'+suffix).value='';
        for(const suffix of ['TitleState','Status','PublicationStatus'])byId('subbed'+suffix).value='any';
        window.AutoSourcarrLibrary.load({reset:true}).catch(error=>toast(error.message));byId('subbedSearch').focus();return;
      }
      if(event.target.closest('[data-refresh-library]')){loadSubbed().catch(error=>toast(error.message));return;}
      const view=event.target.closest('[data-view-kind]');if(view){setView(view.dataset.viewKind,view.dataset.view);return;}
      const route=event.target.closest('[data-route]');if(route&&!route.closest('.appNav')){navigate(route.dataset.route,{event}).catch(error=>toast(error.message));return;}
      const entry=event.target.closest('[data-library-entry]');if(entry){window.openInlineEntry('subbed',Number(entry.dataset.libraryEntry),event);return;}
      const result=event.target.closest('[data-search-entry]');if(result){openSearchEntry(result,event);return;}
      if(event.target.closest('[data-library-scope]')&&activeInspector.subbed!==null)window.closeInlineEntry('subbed',event);
      if(event.target.closest('[data-library-scope],[data-task-section]'))queueMicrotask(()=>syncNavigation());
    });
    byId('riMenuToggle').onclick=()=>document.documentElement.classList.contains('riSidebarOpen')?closeSidebar():openSidebar();
    byId('riSidebarScrim').onclick=()=>closeSidebar();
    byId('riThemeToggle').onclick=()=>applyThemeStyle(document.documentElement.dataset.themeStyle==='paper'?'neutral':'paper',true);
    byId('riRefreshStatistics').onclick=()=>health().catch(error=>toast(error.message));
    byId('riSearchOpen').onclick=openSearch;
    byId('riSearchDialog').addEventListener('close',()=>{searchGeneration++;clearTimeout(searchTimer);searchRequest?.abort();});
    byId('riSearchDialog').addEventListener('click',event=>{if(event.target===byId('riSearchDialog')){const box=event.target.getBoundingClientRect();if(event.clientX<box.left||event.clientX>box.right||event.clientY<box.top||event.clientY>box.bottom)closeSearch();}});
    byId('riCommandQuery').addEventListener('input',()=>{
      ++searchGeneration;searchRequest?.abort();clearTimeout(searchTimer);
      // Invalidate actionable results synchronously, before the debounce or fetch.
      byId('riCommandResults').replaceChildren();
      byId('riCommandStatus').textContent='Searching saved titles…';
      searchTimer=setTimeout(searchCatalog,180);
    });
    byId('riCommandQuery').addEventListener('keydown',event=>{if(event.key==='ArrowDown'){event.preventDefault();byId('riCommandResults').querySelector('button')?.focus();}else if(event.key==='Enter'){const first=byId('riCommandResults').querySelector('button');if(first){event.preventDefault();openSearchEntry(first,event);}}});
    byId('riCommandResults').addEventListener('keydown',event=>{if(!['ArrowDown','ArrowUp'].includes(event.key))return;event.preventDefault();const buttons=[...byId('riCommandResults').querySelectorAll('button')],index=buttons.indexOf(document.activeElement);if(event.key==='ArrowUp'&&index<=0)byId('riCommandQuery').focus();else buttons[Math.max(0,Math.min(buttons.length-1,index+(event.key==='ArrowDown'?1:-1)))]?.focus();});
    document.addEventListener('keydown',event=>{
      const typing=event.target.closest('input,textarea,select,[contenteditable="true"]');
      if(event.key==='/'&&!typing&&!event.altKey&&!event.ctrlKey&&!event.metaKey&&!document.querySelector('dialog[open],.modalBackdrop:not([hidden]),#errorPanel:not([hidden])')){event.preventDefault();openSearch();}
      if(document.documentElement.classList.contains('riSidebarOpen')){
        if(event.key==='Escape'){event.preventDefault();closeSidebar();}
        if(event.key==='Tab'){event.preventDefault();const buttons=[byId('riMenuToggle'),...document.querySelectorAll('.appNav button')],index=buttons.indexOf(document.activeElement);buttons[(index+(event.shiftKey?-1:1)+buttons.length)%buttons.length].focus();}
      }
      if((event.key==='Enter'||event.key===' ')&&event.target.matches('#browseGrid .browseCard')){event.preventDefault();window.openBrowseEntry(Number(event.target.dataset.entryId),event);}
    });
    const annotateBrowse=()=>byId('browseGrid').querySelectorAll('.browseCard').forEach(card=>{if(card.hasAttribute('tabindex'))return;card.tabIndex=0;card.setAttribute('role','group');card.setAttribute('aria-label',card.querySelector('h3')?.textContent||'Manga');});
    new MutationObserver(annotateBrowse).observe(byId('browseGrid'),{childList:true});
    document.addEventListener('error',event=>{if(event.target.matches?.('.riPosterArtwork img'))event.target.classList.add('riCoverMissing');},true);
    const media=matchMedia('(max-width: 767px)');byId('riSidebar').inert=media.matches;
    media.addEventListener('change',()=>{if(!media.matches)closeSidebar({restoreFocus:false});else if(!document.documentElement.classList.contains('riSidebarOpen'))byId('riSidebar').inert=true;});
    const navigation=window.AutoSourcarrNavigation,originalInitialize=navigation.initialize;
    navigation.initialize=function(){
      let hadSaved=false;try{hadSaved=!!sessionStorage.getItem('autosourcarr.navigation.v1');}catch(_){}
      const restored=originalInitialize(),route=core.parseRoute(location.hash);
      if(route){if(route.scope){workbenchState.libraryScope=route.scope;document.querySelectorAll('[data-library-scope]').forEach(button=>{const on=button.dataset.libraryScope===route.scope;button.classList.toggle('active',on);button.setAttribute('aria-pressed',String(on));});}if(route.tab==='sources'&&route.section)setSourceSubtab(route.section,false);if(route.tab==='settings'&&route.section)setSettingsSubtab(route.section,false);return {...restored,tab:route.tab};}
      return hadSaved?restored:{...restored,tab:'subbed',needsSourceInventory:false};
    };
    window.addEventListener('hashchange',event=>{const path=location.hash.replace(/^#/,'');if(core.parseRoute(location.hash))navigate(path,{push:false,event}).catch(error=>toast(error.message));});
    new MutationObserver(()=>{if(!navigating&&byId('tasks').classList.contains('active'))syncNavigation('tasks');}).observe(byId('tasksRepairsPanel'),{attributes:true,attributeFilter:['class']});
    const firstRoute=core.parseRoute(location.hash);
    if(firstRoute?.tab==='tasks'&&firstRoute.section)document.querySelector(`[data-task-section="${firstRoute.section}"]`)?.click();
    setView('library',libraryView);setView('browse',browseView);
  }
  window.AutoSourcarrReimagined={navigated:syncNavigation,renderLibrary,renderStatistics,navigate,closeNavigation:closeSidebar};
  initialize();
})();
