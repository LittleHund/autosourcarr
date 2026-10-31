"""Run interaction regressions without requiring an external browser or source."""
import subprocess
from pathlib import Path


def test_tags_archived_sources_and_entry_metadata_interactions():
    static = Path(__file__).resolve().parents[1] / "app" / "static"
    result = subprocess.run(
        ["node", "-", str(static)],
        input=r"""
const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict');
const root=process.argv[2],html=fs.readFileSync(root+'/index.html','utf8');
class Element {
  constructor(tag='div'){this.tagName=tag.toUpperCase();this.children=[];this.dataset={};this.value='';this.disabled=false;this._text='';this.isConnected=true;this.selectedIndex=-1;this.className='';this.classList={contains:()=>false};}
  set textContent(value){this._text=String(value);this.children=[];}
  get textContent(){return this._text+this.children.map(el=>el.textContent).join('');}
  append(...children){this.children.push(...children);}
  replaceChildren(...children){this.children=children;this._text='';}
  querySelectorAll(selector){const found=[];for(const child of this.children){if(selector==='[data-tag-decision]'&&child.dataset.tagDecision)found.push(child);if(selector==='[data-orphan-mutation]'&&child.dataset.orphanMutation)found.push(child);found.push(...child.querySelectorAll(selector));}return found;}
}
const nodes=Object.fromEntries([...html.matchAll(/\bid="([^"]+)"/g)].map(m=>[m[1],new Element()]));
nodes.tagReviewPanel.append(nodes.tagSuggestions,nodes.tagDecisions,nodes.tagSame,nodes.tagDifferent,nodes.tagClear);
nodes.sourceOrphanedPanel.append(nodes.sourceOrphanList);nodes.sourceInstalledOrphanPanel.append(nodes.sourceInstalledOrphanList);
const calls=[],notices=[],navigation=[];
let apiImpl=()=>{throw Error('Unexpected API call');};
const ctx={document:{getElementById:id=>nodes[id],createElement:tag=>new Element(tag),hidden:false,addEventListener(){},querySelectorAll:selector=>Object.values(nodes).flatMap(n=>n.querySelectorAll(selector))},console,URL,WeakMap,Promise,Number,setTimeout,clearTimeout,MutationObserver:class{observe(){}},confirm:()=>true,toast:x=>notices.push(x),setSourceSubtab:x=>navigation.push(x),setManualActionsSubtab:x=>navigation.push(x),switchTab:x=>navigation.push(x),api:(path,opts)=>{calls.push({path,opts});return apiImpl(path,opts);}};
ctx.window=ctx;vm.createContext(ctx);
vm.runInContext(fs.readFileSync(root+'/tag-review.js','utf8'),ctx);
vm.runInContext(fs.readFileSync(root+'/orphan-sources.js','utf8'),ctx);
const event=()=>({preventDefault(){}}),flush=()=>new Promise(resolve=>setImmediate(resolve));
const deferred=()=>{let resolve;const promise=new Promise(r=>{resolve=r;});return{promise,resolve};};
const all=root=>[root,...root.children.flatMap(all)];
const first={id:1,name:'science fiction',namespace:'genre'},second={id:2,name:'sci-fi',namespace:'genre'};
const base=path=>Promise.resolve(path.includes('/suggestions')?{items:[{left:first,right:second,score:.9,reason:'Possible synonym'}]}:path.endsWith('/reviews')?{items:[]}:{items:[first,second],total:2});
(async()=>{
  // Manual selection is independent of suggestions/catalog pagination and is sent as original IDs.
  apiImpl=base;await ctx.loadTagReview();
  nodes.tagLeftForm.onsubmit(event());nodes.tagRightForm.onsubmit(event());await flush();
  nodes.tagLeftSelect.value='1';nodes.tagLeftSelect.onchange();nodes.tagRightSelect.value='2';nodes.tagRightSelect.onchange();
  assert.equal(nodes.tagSame.disabled,false);await nodes.tagSame.onclick();
  let write=calls.find(c=>c.path==='/api/admin/tags/review');assert.deepEqual(JSON.parse(write.opts.body),{left_id:1,right_id:2,decision:'same'});
  nodes.tagRightSelect.value='1';nodes.tagRightSelect.onchange();assert.equal(nodes.tagSame.disabled,true,'Cannot match a tag to itself');
  // A slow search cannot overwrite a newer picker result or revive a stale selected tag.
  const oldSearch=deferred();apiImpl=path=>path.includes('search=old')?oldSearch.promise:Promise.resolve({items:[second],total:1});
  nodes.tagLeftSearch.value='old';nodes.tagLeftForm.onsubmit(event());
  nodes.tagLeftSearch.value='new';nodes.tagLeftForm.onsubmit(event());await flush();
  oldSearch.resolve({items:[first],total:1});await flush();assert.equal(nodes.tagLeftSelect.children[0].value,'2');assert.equal(nodes.tagSame.disabled,true);
  // Slow suggestion responses cannot reinstate a review after refresh completed.
  const oldSuggestions=deferred();let delayed=true;
  apiImpl=path=>path.endsWith('/suggestions')&&delayed?oldSuggestions.promise:Promise.resolve({items:[],total:0});
  const oldLoad=ctx.loadTagReview();delayed=false;await ctx.loadTagReview();oldSuggestions.resolve({items:[{left:first,right:second,score:1}]});await oldLoad;
  assert(nodes.tagSuggestions.textContent.includes('No suggested synonyms'));
  // Gallery metadata loads independently and late responses cannot change a newer inspector.
  const target=new Element();target.dataset.entryTags='1';const stale=deferred();
  apiImpl=path=>path.includes('/entries/1/')?stale.promise:Promise.resolve({metadata_basis:'source',tags:[second],gallery:[{raw_title:'[Circle (Artist)] Example title [English]',title:'Example title',artist:['Artist'],circle:'Circle',language:'English',category:'Artbook',parody:'Original',kind:'collection',provenance:'source tags',source_id:'sample'}]});
  const oldEntry=ctx.loadEntryTags(target,1);target.dataset.entryTags='2';await ctx.loadEntryTags(target,2);
  stale.resolve({tags:[{name:'stale'}],gallery:[]});await oldEntry;
  for(const text of ['Collection / anthology','Original source title','Artist','Circle','Artbook','English','Original','source tags','external database match is not required'])assert(target.textContent.includes(text),text);
  assert(!target.textContent.includes('stale'));
  // Archived installation must move the source to Installed orphan only after server inventory confirms it.
  let installed=false;
  const row=()=>({pkgName:'eu.kanade.tachiyomi.extension.en.example',name:'Example archive',lang:'en',installed,state:installed?'installed-unverified':'available',origin:'Keiyoushi',can_install:!installed,can_repair:false,commit_url:'javascript:alert(1)'});
  apiImpl=(path,options)=>{if(path.endsWith('/install')){installed=true;return Promise.resolve({message:'Installed'});}return Promise.resolve({items:[row()],refresh:{state:'complete',message:'History checked'},worker_configured:false});};
  await ctx.loadSourceOrphans();assert(nodes.sourceOrphanList.textContent.includes('Example archive'));assert(!nodes.sourceInstalledOrphanList.textContent.includes('Example archive'));
  assert(!all(nodes.sourceOrphanList).some(node=>node.href),'Non-HTTPS provenance link is not clickable');
  await all(nodes.sourceOrphanList).find(node=>node.dataset.orphanMutation==='install').onclick();
  assert(!nodes.sourceOrphanList.textContent.includes('Example archive'));assert(nodes.sourceInstalledOrphanList.textContent.includes('Example archive'));
  assert(nodes.sourceInstalledOrphanList.textContent.includes('Repair worker is not configured'));
  assert(!all(nodes.sourceInstalledOrphanList).some(node=>node.dataset.orphanMutation==='repair'));
  assert(nodes.sourceInstalledOrphanList.textContent.includes('functionality unverified'));
  // A missing binary can be rebuilt without installing it first; job creation is not installation.
  let recoveryRequested=false;
  apiImpl=(path,options)=>{if(path.endsWith('/repair')){recoveryRequested=true;return Promise.resolve({id:'recovery-1'});}return Promise.resolve({items:[{...row(),installed:false,state:'available',archive_url:'https://example.invalid/removed.jar',archive_available:false,can_install:false,can_repair:true,repair_job_id:recoveryRequested?'recovery-1':null}],refresh:{state:'complete'},worker_configured:true});};
  await ctx.loadSourceOrphans();
  assert(!all(nodes.sourceOrphanList).some(node=>node.dataset.orphanMutation==='install'),'Known dead download does not offer installation');
  const rebuild=all(nodes.sourceOrphanList).find(node=>node.dataset.orphanMutation==='repair');assert.equal(rebuild.textContent,'Build and check archive');
  await rebuild.onclick();assert(recoveryRequested);assert(nodes.sourceOrphanList.textContent.includes('View repair evidence'));assert(!nodes.sourceInstalledOrphanList.textContent.includes('Example archive'));
  // Cached inventory refresh is not an operation lock; preserve enabled repair controls and poll promptly.
  const delays=[];ctx.setTimeout=(fn,delay)=>{delays.push(delay);return 1;};ctx.clearTimeout=()=>{};
  nodes.sources.classList.contains=()=>true;nodes.sourceOrphanedPanel.classList.contains=()=>true;
  apiImpl=()=>Promise.resolve({items:[{...row(),can_repair:true}],refresh:{state:'complete',checked_at:1790526381},operation:{state:'idle'},inventory_known:true,inventory_refreshing:true,worker_configured:true});
  await ctx.loadSourceOrphans();assert.equal(delays.at(-1),4000);
  assert(nodes.sourceOrphanStatus.textContent.includes('Checking installed sources'));assert(!nodes.sourceOrphanStatus.textContent.includes('1970'));
  assert.equal(all(nodes.sourceInstalledOrphanList).find(node=>node.dataset.orphanMutation==='repair').disabled,false);
  console.log('PASS original tag IDs, manual decisions, stale response guards, gallery provenance, archived recovery, safe links and nonblocking inventory polling');
})().catch(error=>{console.error(error);process.exitCode=1;});
""",
        text=True,
        capture_output=True,
        timeout=20,
    )
    assert result.returncode == 0, result.stdout + result.stderr
