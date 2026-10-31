// Executed production workspace behavior; no browser or new runtime dependency.
const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict');
const root=__dirname+'/../app/static/',source=fs.readFileSync(root+'app.js','utf8'),workbench=fs.readFileSync(root+'workbench.js','utf8');
const section=(a,b)=>{const start=source.indexOf(a),end=source.indexOf(b,start);assert(start>=0&&end>start);return source.slice(start,end);};
function context(extra={}){const ctx={document:{querySelector:()=>null},esc:s=>String(s).replaceAll('<','&lt;').replaceAll('>','&gt;'),fmtWhen:String,...extra};vm.createContext(ctx);vm.runInContext(workbench,ctx);vm.runInContext(section('function normalizeHexColor(', 'function syncCustomColorInputs('),ctx);return ctx;}
const tests=[];function test(name,fn){tests.push({name,fn});}
const luminosity=c=>{const f=v=>{v/=255;return v<=.04045?v/12.92:((v+.055)/1.055)**2.4;};return .2126*f(c.r)+.7152*f(c.g)+.0722*f(c.b);};

test('Custom accents retain at least 4.5:1 contrast, including black and saturated blue',()=>{
 const ctx=context({mixRgb:(c,t,a)=>({r:Math.round(c.r+(t.r-c.r)*a),g:Math.round(c.g+(t.g-c.g)*a),b:Math.round(c.b+(t.b-c.b)*a)})});
 for(const r of [0,64,128,255])for(const g of [0,64,128,255])for(const b of [0,64,128,255]){const surfaces=[{r:34,g:39,b:49},{r:16,g:18,b:23}],safe=ctx.readableColor({r,g,b},surfaces);for(const surface of surfaces)assert(ctx.colorContrast(safe,surface)>=4.5);}
 const css=fs.readFileSync(root+'workbench.css','utf8');for(const theme of ['blue','amber','emerald','violet','rose','cyan']){const hex=css.match(new RegExp(`\\[data-theme=${theme}\\]\\{--accent:#([a-f0-9]{6})`))[1];const c={r:parseInt(hex.slice(0,2),16),g:parseInt(hex.slice(2,4),16),b:parseInt(hex.slice(4),16)};assert((luminosity(c)+.05)/(luminosity({r:34,g:39,b:49})+.05)>=4.5,theme);}
});

test('Diagnostics extracts an upstream 521 without hiding complete stack evidence',()=>{
 const ctx=context(),item={message:'Private source host failed: HTTP error 521 at eu.source.Extension(Source.kt:12)',detail:'raw details',status_code:503};
 assert.match(ctx.diagnosticCause(item).title,/521/);assert(!ctx.diagnosticCause(item).title.includes('503'));
 const html=ctx.workbenchErrorHtml(item);assert(html.includes('Source.kt:12'));assert.match(html,/<details><summary>Technical details/);assert(!html.split('class="diagnosticMessage">')[1].split('</div>')[0].includes('Source.kt'));
});

test('Active shared root cause stays above derivative browser failures',()=>{
 const ctx=context(),items=[{id:'browser',resolved:false},{id:'old',resolved:true,pinned:true},{id:'engine',resolved:false,pinned:true}];
 assert.equal(ctx.filterWorkbenchErrors(items)[0].id,'engine');assert.equal(items[0].id,'browser','Input history order remains unchanged');
 vm.runInContext("workbenchState.diagnosticFilter='resolved'",ctx);assert.equal(ctx.filterWorkbenchErrors(items)[0].id,'old');
});

test('CAPTCHA, browser connection loss and timeout retain distinct recovery actions',()=>{
 const ctx=context();assert.match(ctx.diagnosticCause({message:'Shape-selecting captcha detected'}).next,/WebView/);assert.match(ctx.diagnosticCause({message:'Failed to fetch'}).title,/browser/);assert.match(ctx.diagnosticCause({message:'Request timed out'}).next,/does not confirm/);
});

test('Task lane selection uses explicit origin and reconciles activity counts',()=>{
 const nodes={'#activityManualText':{},'#activityAutomaticText':{}};
 const ctx=context({document:{querySelector:q=>nodes[q]},tasksState:{items:[{name:'Same name',state:'running',origin:'manual',activity:'running'},{name:'Same name',state:'running',origin:'automatic',activity:'waiting'},{state:'completed',origin:'manual'}]}});
 vm.runInContext("workbenchState.taskLane='manual'",ctx);assert.equal(ctx.workbenchTaskItems(ctx.tasksState.items).length,2);ctx.updateActivityStrip();assert.equal(nodes['#activityManualText'].textContent,'1 active');assert.equal(nodes['#activityAutomaticText'].textContent,'0 active · 1 waiting');
});

test('Import search drafts survive run/filter/page loading and only one detail is rendered',()=>{
 const nodes={};let renders=0;
 const detail={_html:'',set innerHTML(v){this._html=v;renders++;for(const k of Object.keys(nodes))if(k.startsWith('#importSearch-'))delete nodes[k];const m=v.match(/data-input="(\d+)"/);if(m)nodes['#importSearch-'+m[1]]={value:''};},get innerHTML(){return this._html;}};
 Object.assign(nodes,{'#importItems':detail,'#importQueue':{innerHTML:''},'#importItemsStatus':{textContent:''}});
 const state={runs:[{id:'A'},{id:'B'}],runId:'A',items:[{id:1,title:'A first',state:'unresolved'},{id:2,title:'A second',state:'unresolved'}],revision:0,filter:'all',review:'all',loadedKey:'A',total:2};
 const ctx=context({document:{querySelector:q=>nodes[q]},$:(q)=>nodes[q],importsState:state,importViewKey:()=>state.runId,importPassesStateFilter:()=>true,importPassesReviewFilter:()=>true,importItemHtml:(_r,i)=>'<div data-input="'+i.id+'"></div>',updateImportReviewCounts:()=>{},renderImportPagination:()=>{}});
 vm.runInContext(section('function renderImportItems(){','function loadImportItems(){'),ctx);
 ctx.renderImportItems();assert.equal((detail.innerHTML.match(/data-input/g)||[]).length,1);nodes['#importSearch-1'].value='My search';
 ctx.selectImportDesk(2);nodes['#importSearch-2'].value='Second search';ctx.selectImportDesk(1);assert.equal(nodes['#importSearch-1'].value,'My search');
 const before=renders;ctx.patchImportWorkspace(state.items[1]);assert.equal(renders,before,'Unrelated item update must preserve selected input DOM');
 state.runId='B';ctx.renderImportItems();state.items=[{id:3,title:'B item'}];state.loadedKey='B';ctx.renderImportItems();
 state.runId='A';state.items=[{id:1,title:'A item'}];state.loadedKey='A';ctx.renderImportItems();assert.equal(nodes['#importSearch-1'].value,'My search');
});

test('Selecting a new comparison survives refresh and advances only after its pair disappears',()=>{
 const ctx=context({duplicateSubbedSet:()=>new Set(),duplicateReviewButtonAttributes:()=>'',entryCoverUrl:String,duplicateEntryCard:e=>`<article>Entry ${e.id}</article>`,pairEvidence:p=>`<div>Pair ${p.left_id}:${p.right_id}</div>`});
 const rec=(a,b)=>({entry:{id:a,preferred_title:'Title '+a},candidateIds:new Set([b]),entries:new Map([[b,{id:b,preferred_title:'Candidate '+b}]]),pairs:[{left_id:a,right_id:b}]});
 ctx.renderCompareDesk([rec(1,2),rec(3,4)]);vm.runInContext('workbenchState.duplicateLeft=3;workbenchState.duplicateRight=4',ctx);
 ctx.renderCompareDesk([rec(1,2),rec(3,4)]);assert.equal(vm.runInContext('workbenchState.duplicateLeft',ctx),3);
 ctx.renderCompareDesk([rec(1,2)]);assert.equal(vm.runInContext('workbenchState.duplicateLeft',ctx),1);
});

test('Unknown chapter counts remain unknown and per-source gaps are not summed into a false total',()=>{
 const ctx=context({entryCoverUrl:String,activeInspector:{subbed:null},subscriptionStatsFor:()=>({chapter_count:null,downloaded_chapter_count:null}),fmtChapterNumber:String});
 const html=ctx.renderSubbedRows([{id:1,preferred_title:'Test',match_count:2}],{},new Map([[1,[{source_id:'a',chapters_checked:true,missing_chapter_count:3},{source_id:'b',chapters_checked:true,missing_chapter_count:3}]]]));
 assert(html.includes('Unknown'));assert(html.includes('a: 3 missing'));assert(html.includes('b: 3 missing'));assert(!html.includes('6 missing'));
});

test('Switching users cannot migrate the previous profile into an empty new profile',async()=>{
 const local={theme:'rose',customColor:'#FFFFFF',density:'compact'},storage={'autosourcarr.preferenceOwner':'authelia:alice'};let applied;
 const ctx=context({UI_PREF_LOCAL_KEYS:{theme:'t',customColor:'c',density:'d'},DEFAULT_CUSTOM_COLOR:'#ff2d78',uiPreferencesState:{revisions:{},values:{},pending:{}},localStorage:{getItem:k=>storage[k]||null,setItem:(k,v)=>storage[k]=v},readLocalUiPreference:k=>local[k],writeLocalUiPreference:(k,v)=>{if(v==null)delete local[k];else local[k]=v;},api:async()=>({user:'bob',authelia:true,values:{}}),applyUiPreferences:v=>{applied=v;},updateUiPreferenceOwner:()=>{},setTimeout:()=>{},clearTimeout:()=>{},flushUiPreferenceSave:()=>{}});
 vm.runInContext(section('async function loadUiPreferences(){','function rememberClientError('),ctx);await ctx.loadUiPreferences();assert.equal(applied.theme,'blue');assert.equal(applied.density,'comfortable');assert.equal(storage['autosourcarr.preferenceOwner'],'authelia:bob');assert.deepEqual(Object.keys(ctx.uiPreferencesState.pending).sort(),['colorRolesVersion','secondaryColor','tertiaryColor']);assert.equal(ctx.uiPreferencesState.pending.secondaryColor,'#7DB0FF');
});

(async()=>{for(const t of tests){await t.fn();console.log('PASS '+t.name);}console.log(`${tests.length} workspace behavior scenarios passed`);})().catch(e=>{console.error(e);process.exitCode=1;});
