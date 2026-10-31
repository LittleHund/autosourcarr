'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
class Node {
  constructor(){this.nodes=new Map();this.children=[];this.dataset={};this.isConnected=true;this.attributes={};this._text='';}
  set innerHTML(value){this.nodes.clear();this.children=[];this._html=value;}get innerHTML(){return this._html;}
  set textContent(value){this.nodes.clear();this.children=[];this._text=value;}get textContent(){return this._text;}
  querySelector(selector){if(!this.nodes.has(selector))this.nodes.set(selector,new Node());return this.nodes.get(selector);}
  querySelectorAll(){return [];}
  append(...nodes){this.children.push(...nodes);}
  replaceChildren(...nodes){this.nodes.clear();this.children=nodes;}
  setAttribute(name,value){this.attributes[name]=value;}
  focus(){}
}
const pending=[],document={createElement:()=>new Node(),body:new Node()},window={};
const api=(url,options)=>new Promise(resolve=>pending.push({url,options,resolve}));
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../app/static/identity-review.js'),'utf8'),{window,document,api,AbortController,URLSearchParams});
const data=id=>({entry_id:id,title:'Work '+id,revision:'0'.repeat(64),references:[],history:[],note:'Private review fixture'});
(async()=>{
  const body=new Node(),controller=new AbortController(),view=await window.AutoSourcarrIdentityReview.mount({body,signal:controller.signal,scoped:true,status(){},write(){throw Error('No write should occur');}});
  const detail=body.children[0].querySelector('[data-identity-detail]');
  async function choose(id){const promise=view.choose(id),request=pending.at(-1);request.resolve(data(id));await promise;}
  await choose(2);detail.querySelector('[data-note]').value='Work two draft';
  await choose(1);detail.querySelector('[data-note]').value='Work one draft';
  const second=view.choose(2),lateSecond=pending.at(-1);
  const third=view.choose(3),thirdRequest=pending.at(-1);
  assert.equal(lateSecond.options.signal.aborted,true);
  thirdRequest.resolve(data(3));await third;lateSecond.resolve(data(2));await second;
  assert.equal(detail.querySelector('h3').textContent,'Work 3','A late response cannot replace a newer selection');
  await choose(2);assert.equal(detail.querySelector('[data-note]').value,'Work two draft','Selecting again while a read is pending must not overwrite its retained draft with an empty form');
  await choose(1);assert.equal(detail.querySelector('[data-note]').value,'Work one draft');
  const last=view.choose(4),lastRequest=pending.at(-1);controller.abort();lastRequest.resolve(data(4));await last;
  assert.equal(lastRequest.options.signal.aborted,true);
  assert.notEqual(detail.querySelector('h3').textContent,'Work 4','Closing the owner cannot revive a late detail view');
  assert.ok(pending.every(r=>!r.options.method),'Selecting/refreshing identities never writes or queries a source');
  console.log('Identity review: out-of-order reads, rapid reselection draft retention, abort ownership and read-only selection verified');
})().catch(error=>{console.error(error);process.exitCode=1;});
