"""Run the shipped UI with controlled DOM/HTTP fixtures; no live source traffic."""
from html.parser import HTMLParser
from pathlib import Path

from test_v086_ui import block, node


def test_orphans_are_persistent_collapsible_discover_groups():
    class Tree(HTMLParser):
        def __init__(self):
            super().__init__()
            self.stack = []
            self.nodes = {}

        def handle_starttag(self, tag, attrs):
            attrs = dict(attrs)
            if attrs.get("id"):
                self.nodes[attrs["id"]] = (tag, attrs, self.stack.copy())
            if tag not in {"input", "img", "meta", "link", "br", "hr"}:
                self.stack.append((tag, attrs.get("id")))

        def handle_endtag(self, tag):
            for index in range(len(self.stack) - 1, -1, -1):
                if self.stack[index][0] == tag:
                    del self.stack[index:]
                    break

    parser = Tree()
    parser.feed((Path(__file__).resolve().parents[1] / "app/static/index.html").read_text())
    for identity, key in [("sourceOrphanedPanel", "orphaned"),
                          ("sourceInstalledOrphanPanel", "installed-orphan")]:
        tag, attrs, ancestors = parser.nodes[identity]
        assert tag == "details"
        assert attrs["data-source-section"] == key
        assert ("div", "sourceManagePanel") in ancestors
        assert ("div", "sourceExtensionSections") in ancestors
    for identity in ["matrixSource", "subbedSource", "browseConnectedSource"]:
        assert parser.nodes[identity][0] == "select"


DETAIL_DOM = r'''
const assert=require('node:assert/strict'),window=globalThis;
class Node {
 constructor(){this.innerHTML='';this.textContent='';this.children=[];this.isConnected=true;this.dataset={};this.open=false;this.hidden=false;}
 append(node){this.children.push(node)}
 click(){this.onclick?.()}
}
class Root extends Node {
 set innerHTML(value){this._html=value;for(const n of Object.values(this.nodes||{}))n.isConnected=false;this.nodes={};
 for(const m of value.matchAll(/<details[^>]*data-detail-section="([^"]+)"([^>]*)>/g)){const n=new Node();n.dataset.detailSection=m[1];n.open=m[2].includes('open');this.nodes['[data-detail-section="'+m[1]+'"]']=n;}
 for(const m of value.matchAll(/<input id="([^"]+)"(?: value="([^"]*)")?/g)){const n=new Node();n.id=m[1];n.tagName='INPUT';n.defaultValue=n.value=m[2]||'';n.focus=()=>document.activeElement=n;n.setSelectionRange=(a,b)=>{n.selectionStart=a;n.selectionEnd=b;};this.nodes['#'+n.id]=n;}
 for(const name of ['data-entry-chapters','data-entry-tags','data-gallery-metadata','data-load-history'])if(value.includes(name))this.nodes['['+name+']']=new Node();}
 get innerHTML(){return this._html||''}
 querySelector(selector){return this.nodes[selector]||null}
 querySelectorAll(selector){return selector==='input[id],textarea[id]'?Object.values(this.nodes).filter(n=>n.tagName==='INPUT'):selector.startsWith('[data-detail-section]')?Object.values(this.nodes).filter(n=>n.dataset.detailSection&&(!selector.includes('[open]')||n.open)):[]}
 contains(node){return Object.values(this.nodes).includes(node)}
}
const root=new Root(),activeInspector={matrix:1},entryInspectorGeneration={matrix:1},browseState={selectedEntryId:1};let browseInspectorGeneration=1;
const nodes={'#matrixInspector':root,'#browseInspectorBody':root,'#historyBox-matrix':new Node()};
const $=key=>nodes[key]||null,matrixControls=()=>({inspector:root});
const document={documentElement:{dataset:{ui:'workbench'}},createElement:()=>new Node()};
const esc=x=>String(x??''),jsq=esc,entryCoverUrl=(id,rev)=>'cover/'+id+'?'+rev,sourceCoverUrl=entryCoverUrl;
const sourceRows=[],coverRenderNonce=77,groupSources=rows=>rows.map(row=>({name:row.name,variants:[row]})),groupSummary=()=>'',selectedVariants=g=>g.variants,langSummary=()=>'',langLabel=String;
const applyEntrySnapshotEverywhere=()=>{},noteEntryThumbnail=()=>{},loadHistory=async()=>{};
const tags=[];window.loadEntryTags=(...args)=>tags.push(args);
const calls=[],pending=[];let entry={id:1,preferred_title:'An example',match_count:1,matches:[{source_id:'9223372036854775806',source_name:'Archived source',source_title:'First gallery',source_manga_id:55,confidence:1}],titles:[{title:'Alternate',title_type:'native'}],source_variants:[]};
const api=(url,opts)=>{calls.push(url);if(url.endsWith('/chapters'))return new Promise((resolve,reject)=>pending.push({resolve,reject}));return Promise.resolve(entry)};
const tick=()=>new Promise(resolve=>setImmediate(resolve));
'''


def test_details_render_before_chapters_and_failed_chapter_retry_is_local():
    helpers = block("function entryDetailSection(", "// Inline entry inspector")
    renderer = block("function metadataIdentityHtml(", "window.closeInlineEntry=")
    node(DETAIL_DOM + helpers + renderer + r'''
(async()=>{
 await renderInlineEntry('matrix',1,null,{generation:1});
 assert.deepEqual(calls,['/api/admin/entries/1'],'Opening details must not wait for source chapter retrieval');
 const order=[...root.innerHTML.matchAll(/data-detail-section="([^"]+)"/g)].map(m=>m[1]);
 assert.deepEqual(order,['titles','tags','metadata','matches','chapters','history']);
 assert(root.innerHTML.includes('First gallery'),'Linked source remains visible before source-filter inventory loads');
 assert(root.innerHTML.includes('Archived source'));
 assert.equal(tags.length,1);assert.equal(tags[0][3],root.querySelector('[data-gallery-metadata]'));
 const chapters=root.querySelector('[data-detail-section="chapters"]'),list=root.querySelector('[data-entry-chapters]');
 assert.equal(chapters.open,false);chapters.open=true;chapters.ontoggle();chapters.ontoggle();
 assert.equal(pending.length,1,'Opening a loading section does not duplicate requests');
 pending[0].reject(Error('source engine busy'));await tick();
 assert(list.textContent.includes('source engine busy'));assert.equal(list.children[0].textContent,'Retry chapters');
 assert(root.innerHTML.includes('Alternate'),'Chapter failure cannot erase the main inspector');
 list.children[0].click();assert.equal(pending.length,2);
 pending[1].resolve({items:[{id:8,display_name:'Chapter 2',serving_source_name:'Gallery',candidate_count:1}]});await tick();
 assert(list.innerHTML.includes('Chapter 2'));
 chapters.open=false;chapters.ontoggle();chapters.open=true;chapters.ontoggle();assert.equal(pending.length,2,'Loaded chapters remain on collapse/reopen');
})().catch(error=>{console.error(error);process.exitCode=1});
''')


def test_inspector_reopen_discards_pending_chapters_and_preserves_section_choice():
    helpers = block("function entryDetailSection(", "// Inline entry inspector")
    renderer = block("function metadataIdentityHtml(", "window.closeInlineEntry=")
    node(DETAIL_DOM + helpers + renderer + r'''
(async()=>{
 await renderInlineEntry('matrix',1,null,{generation:1});
 root.querySelector('[data-detail-section="tags"]').open=false;
 root.querySelector('[data-detail-section="chapters"]').open=true;
 root.querySelector('[data-detail-section="chapters"]').ontoggle();
 const oldList=root.querySelector('[data-entry-chapters]');
 entryInspectorGeneration.matrix=2;
 await renderInlineEntry('matrix',1,null,{generation:2,showLoading:false});
 assert.equal(root.querySelector('[data-detail-section="tags"]').open,false);
 assert.equal(root.querySelector('[data-detail-section="chapters"]').open,true);
 assert.equal(pending.length,2,'Open chapter section refreshes with the new entry snapshot');
 pending[0].resolve({items:[{id:1,display_name:'STALE CHAPTER'}]});await tick();
 assert(!oldList.innerHTML.includes('STALE'));
 pending[1].resolve({items:[{id:2,display_name:'CURRENT CHAPTER'}]});await tick();
 assert(root.querySelector('[data-entry-chapters]').innerHTML.includes('CURRENT CHAPTER'));
})().catch(error=>{console.error(error);process.exitCode=1});
''')


def test_matrix_source_filter_prevents_stale_response_and_preserves_large_ids():
    loader = block("function loadMatrixView(", "function loadMatrix(options")
    node(r'''
const assert=require('node:assert/strict');const search={value:''},source={value:'9223372036854775806'},count={textContent:''};
const matrixControls=()=>({search,source,count}),matrixRequests={matrix:new Map()},matrixViewRetry={matrix:null};let dataset=null;
const matrixDataset=()=>dataset,setMatrixDataset=(kind,data)=>dataset=data;
const preserveMatrixChapterStats=()=>{},activeSurface=()=>true,hydrateMatrixDuplicates=()=>{},hydrateMatrixChapters=()=>{};
const requests=[],rendered=[];const api=url=>new Promise(resolve=>requests.push({url,resolve}));const renderMatrix=()=>rendered.push(dataset.entries[0].id);
''' + loader + r'''
(async()=>{
 const old=loadMatrixView('matrix'),same=loadMatrixView('matrix');assert.equal(requests.length,1);assert(requests[0].url.includes('source_id=9223372036854775806'));
 source.value='11';const fresh=loadMatrixView('matrix');assert.equal(requests.length,2);
 const payload=id=>({entries:[{id}],sources:[],matches:[],variants:[]});
 requests[1].resolve(payload(22));await fresh;
 requests[0].resolve(payload(99));await Promise.all([old,same]);
 assert.deepEqual(rendered,[22]);assert.equal(dataset.entries[0].id,22);
})().catch(error=>{console.error(error);process.exitCode=1});
''')


def test_duplicate_pending_requests_coalesce_back_off_and_stop_when_hidden():
    hydrate = block("async function hydrateMatrixDuplicates(", "function updateMatrixCoverageLabel(")
    node(r'''
const assert=require('node:assert/strict'),duplicates={matrix:{entries:[{id:1},{id:2}],duplicates:{}},subbed:{entries:[{id:2},{id:1}],duplicates:{}}};
const matrixDataset=kind=>duplicates[kind],duplicateHydrateRetry={matrix:null,subbed:null},duplicateSnapshotRequests=new Map(),duplicatePollAttempts={matrix:0,subbed:0};
const document={hidden:false,querySelector:()=>true};let active='matrix';const activeSurface=kind=>active===kind;
const timers=[],requests=[];const setTimeout=(fn,delay)=>{timers.push({fn,delay});return timers.length;},clearTimeout=()=>{};
const api=()=>new Promise(resolve=>requests.push(resolve));const requestSurfaceRender=()=>{};const tick=()=>new Promise(resolve=>setImmediate(resolve));
''' + hydrate + r'''
(async()=>{
 const a=hydrateMatrixDuplicates('matrix',duplicates.matrix),b=hydrateMatrixDuplicates('subbed',duplicates.subbed);
 assert.equal(requests.length,1,'The same IDs across two views share one pending request');
 requests[0]({pending:true,duplicates:{}});await Promise.all([a,b]);assert.equal(timers.length,1);assert.equal(timers[0].delay,5000);
 timers[0].fn();requests[1]({pending:true,duplicates:{}});await tick();assert.equal(timers[1].delay,10000);
 document.hidden=true;timers[1].fn();await tick();assert.equal(requests.length,2,'Hidden tabs stop polling');
 document.hidden=false;const complete=hydrateMatrixDuplicates('matrix',duplicates.matrix);requests[2]({pending:false,duplicates:{}});await complete;assert.equal(duplicatePollAttempts.matrix,0);
})().catch(error=>{console.error(error);process.exitCode=1});
''')


def test_stable_detail_refresh_preserves_newest_drafts_focus_and_collapse_changes():
    helpers = block("function entryDetailSection(", "// Inline entry inspector")
    renderer = block("function metadataIdentityHtml(", "window.closeInlineEntry=")
    node(DETAIL_DOM.replace("const api=", "let api=") + helpers + renderer + r'''
(async()=>{
 await renderInlineEntry('matrix',1,null,{generation:1});
 const title=root.querySelector('#mainTitle-matrix'),alias=root.querySelector('#newAlias-matrix');
 title.value='Initial draft';alias.value='New alias';
 let complete;api=()=>new Promise(resolve=>complete=resolve);
 entryInspectorGeneration.matrix=2;
 const refresh=renderInlineEntry('matrix',1,null,{generation:2,showLoading:false});
 // The server is slow: changes made after the refresh began must also survive.
 title.value='Newest unsaved draft';title.focus();title.setSelectionRange(3,8);
 alias.value='Newest alias';root.querySelector('[data-detail-section="tags"]').open=false;
 complete({...entry,preferred_title:'Updated server title'});await refresh;
 const replacement=root.querySelector('#mainTitle-matrix');
 assert.equal(replacement.value,'Newest unsaved draft');assert.equal(replacement.defaultValue,'Updated server title');
 assert.equal(root.querySelector('#newAlias-matrix').value,'Newest alias');
 assert.equal(document.activeElement,replacement);assert.equal(replacement.selectionStart,3);assert.equal(replacement.selectionEnd,8);
 assert.equal(root.querySelector('[data-detail-section="tags"]').open,false);
})().catch(error=>{console.error(error);process.exitCode=1});
''')


def test_history_late_failure_does_not_replace_newer_browse_history():
    from test_v089_ui import DETAIL_SETUP
    setup = DETAIL_SETUP.replace(
        "new Promise(resolve=>requests.push(resolve))",
        "new Promise((resolve,reject)=>requests.push({resolve,reject}))",
    )
    node(setup + "const browseState={selectedEntryId:1};let browseInspectorGeneration=1;\n" +
         block("window.loadHistory=", "window.openChapter=") + r'''
(async()=>{
 const old=loadHistory(1,'browse'),fresh=loadHistory(1,'browse');
 requests[1].resolve({history:[{served_at:'now',chapter:{display_name:'CURRENT'},source_name:'Source'}]});await fresh;
 requests[0].reject(Error('old transport failed'));await old;
 assert(historyBox.textContent.includes('CURRENT'));assert(!historyBox.textContent.includes('failed'));
 const leaving=loadHistory(1,'browse');browseInspectorGeneration++;historyBox={textContent:'new inspector'};
 requests[2].reject(Error('previous inspector failed'));await leaving;assert.equal(historyBox.textContent,'new inspector');
 const current=loadHistory(1,'browse');requests[3].reject(Error('current transport failed'));await current;
 assert(historyBox.textContent.includes('current transport failed'));
})().catch(error=>{console.error(error);process.exitCode=1});
''')


def test_saved_source_filter_keeps_selection_and_disables_ignored_live_controls():
    controls = block("function populateConnectedSourceFilters(", "function setBrowseMode(")
    query = block("function browseQueryUrl(", "function browseCardHtml(")
    node(r'''
const assert=require('node:assert/strict');
class Control {
 constructor(value='',label=''){this.value=value;this.selectedOptions=[{textContent:label}];this.disabled=false;}
 set innerHTML(value){this.html=value;this.value='';}
 insertAdjacentHTML(position,value){this.html+=value;}
}
const nodes={};const $=id=>nodes[id]||(nodes[id]=new Control());
const sourceRows=[],esc=String,langLabel=String,browseState={mode:'POPULAR',loading:false};
for(const id of ['matrixSource','subbedSource','browseConnectedSource'])nodes['#'+id]=new Control('9223372036854775806','Gallery · English');
nodes['#browseSource']=new Control('feed-group');nodes['#browseLanguage']=new Control('fr');nodes['#browseSearch']=new Control('local title');nodes['#browseSort']=new Control('title');nodes['#browsePageSize']=new Control('50');
const modes=['POPULAR','LATEST','SEARCH'].map(mode=>({dataset:{mode},classList:{toggle(){}}}));
const document={querySelectorAll:()=>modes},browseSelectedSourceIds=()=>['11'];
''' + controls + query + r'''
// A temporarily missing inventory row must not silently switch to All sources.
populateConnectedSourceFilters();
assert.equal($('#matrixSource').value,'9223372036854775806');assert($('#matrixSource').html.includes('retained filter'));
assert.equal($('#browseSource').disabled,true);assert.equal($('#browseLanguage').disabled,true);assert(modes.every(mode=>mode.disabled));
let params=new URLSearchParams(browseQueryUrl(1,true).split('?')[1]);
assert.equal(params.get('source_id'),'9223372036854775806');assert.equal(params.get('q'),'local title');assert.equal(params.get('mode'),'SEARCH');assert.equal(params.get('force_live'),'false');assert.deepEqual(params.getAll('sources'),[]);
$('#browseConnectedSource').value='';syncBrowseConnectionControls();
assert.equal($('#browseSource').disabled,false);assert(modes.every(mode=>!mode.disabled));
params=new URLSearchParams(browseQueryUrl(1,true).split('?')[1]);
assert.equal(params.get('mode'),'POPULAR');assert.equal(params.get('force_live'),'true');assert.deepEqual(params.getAll('sources'),['11']);
''')
