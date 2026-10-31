'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
// A small DOM ownership fixture: append moves real node objects, including drafts
// and event handlers. Browser rendering/keyboard activation are checked separately.
let focused=null;
class Node {
  constructor(id='',classes=[]){this.id=id;this.childNodes=[];this.parent=null;this.dataset={};this.attributes={};this.hidden=false;this.value='';this.listeners={};this.classList={contains:key=>classes.includes(key)};}
  append(...nodes){for(const node of nodes){if(node.parent)node.parent.childNodes.splice(node.parent.childNodes.indexOf(node),1);node.parent=this;this.childNodes.push(node);}}
  setAttribute(name,value){this.attributes[name]=value;}
  querySelector(selector){return selector==='h3'?this.childNodes[0]:null;}
  addEventListener(name,callback){this.listeners[name]=callback;}
  focus(){focused=this;}
}
const body=new Node(),title=new Node(),overview=new Node('riManageOverview'),form=new Node('riProfileForm'),retained=new Node('riRetainedProfiles'),ids=new Node('', ['riMediaIdentity']);
form.value='en, ja';const originalHandler=()=>{};form.onsubmit=originalHandler;body.append(title,overview,form,retained,ids);
const window={},document={createElement:()=>new Node()},selected=[];
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../app/static/manage-tabs.js'),'utf8'),{window,document});
const tabs=window.AutoSourcarrManageTabs.mount(body,{onSelect:key=>selected.push(key)}),root=body.childNodes.at(-1),list=root.childNodes[0];
assert.equal(body.childNodes[0],title);assert.equal(tabs.selected(),'overview');assert.equal(form.parent,tabs.panels.serving);
assert.equal(ids.parent,tabs.panels.ids);assert.equal(overview.parent,tabs.panels.overview);
tabs.select('serving');tabs.select('metadata');tabs.select('ids');tabs.select('serving');
assert.equal(form.value,'en, ja');assert.equal(form.onsubmit,originalHandler);assert.equal(retained.parent,tabs.panels.serving);
const buttons=list.childNodes;let prevented=0;
list.listeners.keydown({target:buttons[1],key:'ArrowRight',preventDefault:()=>prevented++});
assert.equal(focused,buttons[2]);assert.equal(tabs.selected(),'serving','Arrow focus must not activate a potentially lazy panel');
buttons[2].onclick();assert.equal(tabs.selected(),'metadata');
list.listeners.keydown({target:buttons[2],key:'End',preventDefault:()=>prevented++});assert.equal(focused,buttons[3]);
list.listeners.keydown({target:buttons[3],key:'Home',preventDefault:()=>prevented++});assert.equal(focused,buttons[0]);
assert.equal(prevented,3);assert.equal(buttons.filter(b=>b.tabIndex===0).length,1);
assert.equal(buttons[2].attributes['aria-selected'],'true');assert.equal(tabs.panels.serving.hidden,true);
console.log('Manage tabs: live draft/handler ownership, isolated panels, manual keyboard selection and roving focus verified');
const workbench=fs.readFileSync(path.join(__dirname,'../app/static/workbench.js'),'utf8');
const activity=workbench.slice(workbench.indexOf('function updateActivityStrip(){'),workbench.indexOf('function diagnosticCause('));
const manual={},automatic={};
vm.runInNewContext(activity+'\nupdateActivityStrip();',{document:{querySelector:s=>s==='#activityManualText'?manual:automatic},tasksState:{items:[
  {state:'running',origin:'manual',kind:'source-raw-input'},
  {state:'running',origin:'manual',kind:'source-repair',repair_state:'awaiting-raws'},
  {state:'running',origin:'manual',kind:'source-repair',activity:'running'},
  {state:'running',origin:'manual',kind:'metadata',activity:'queued'},
  {state:'completed',origin:'automatic',kind:'source-repair'}]}});
assert.equal(manual.textContent,'1 active · 1 waiting · 2 need input');assert.equal(automatic.textContent,'Idle');
