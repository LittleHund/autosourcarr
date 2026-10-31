'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
// A small control fixture tests response ownership; rendered layout uses a browser.
class Element {
  constructor(tag='div'){this.tagName=tag.toUpperCase();this.nodes=[];this.children=[];this.dataset={};this.isConnected=true;this.attributes={};this.value='';this.checked=false;this.disabled=false;}
  set disabled(value){this._disabled=Boolean(value);}get disabled(){return this._disabled;}
  set innerHTML(html){
    this.nodes=[];this.children=[];this._html=html;
    for(const match of html.matchAll(/<([a-z][a-z0-9]*)\b([^>]*)>/g)){
      const node=new Element(match[1]);
      for(const attr of match[2].matchAll(/([a-zA-Z0-9_-]+)(?:="([^"]*)")?/g)){
        node.attributes[attr[1]]=attr[2]??'';
        if(attr[1].startsWith('data-'))node.dataset[attr[1].slice(5).replace(/-([a-z])/g,(_,c)=>c.toUpperCase())]=attr[2]??'';
      }
      node.value=node.attributes.value||'';node.checked='checked' in node.attributes;node.disabled='disabled' in node.attributes;this.nodes.push(node);
    }
  }
  get innerHTML(){return this._html;}
  set textContent(text){this.nodes=[];this.children=[];this._text=text;}get textContent(){return this._text||'';}
  all(){const result=[],seen=new Set();const visit=n=>{if(seen.has(n))return;seen.add(n);result.push(n);for(const c of [...n.nodes,...n.children])if(c instanceof Element)visit(c);};for(const n of [...this.nodes,...this.children])if(n instanceof Element)visit(n);return result;}
  querySelectorAll(selector){return this.all().filter(n=>selector.split(',').some(s=>{
    s=s.trim();const checked=s.endsWith(':checked');s=s.replace(':checked','');if(checked&&!n.checked)return false;
    if(s[0]==='#')return n.attributes.id===s.slice(1);
    const attr=s.match(/^\[([^=\]]+)(?:=([^\]]+))?\]$/);if(attr)return attr[1] in n.attributes&&(attr[2]===undefined||n.attributes[attr[1]]===attr[2]);
    return n.tagName.toLowerCase()===s;
  }));}
  querySelector(selector){let node=this.querySelectorAll(selector)[0];if(!node){node=new Element(selector);this.nodes.push(node);if(selector[0]==='['){const key=selector.slice(1,-1).split('=')[0];node.attributes[key]='';}}return node;}
  append(...nodes){this.children.push(...nodes);if(this.tagName==='SELECT'&&!this.value)this.value=nodes[0]?.value||'';}
  replaceChildren(...nodes){this.nodes=[];this.children=nodes;}
  setAttribute(name,value){this.attributes[name]=value;}
  addEventListener(){}
  focus(){document.activeElement=this;}
}
const pending=[],document={createElement:tag=>new Element(tag),createTextNode:text=>text,body:new Element(),activeElement:null},window={};
const api=(url,options)=>new Promise((resolve,reject)=>pending.push({url,options,resolve,reject}));
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../app/static/catalog-sharing.js'),'utf8'),{window,document,api,AbortController,Blob,downloadBlob(){},loadSubbed:async()=>{}});
const flush=()=>new Promise(resolve=>setImmediate(resolve));
const preview=id=>({id,revision:'0'.repeat(64),items:[],decisions:[],added:1,reused:0,retained:0,offset:0,limit:100,item_total:0,decision_total:0});
(async()=>{
  const body=new Element(),abort=new AbortController(),messages=[];
  const mounting=window.AutoSourcarrCatalogSharing.mount({body,signal:abort.signal,status:text=>messages.push(text),close(){},async write(button,operation,after){try{await after(await operation());}catch{}}});
  pending.at(-1).resolve({entries:0,files_added:0,pending_decisions:0,retained_records:0});await mounting;
  const root=body.children[0],get=s=>root.querySelector(s),files=get('[data-files]');
  files.files=[{name:'first.ascatalog',size:100}];files.onchange();
  const first=get('[data-preview]').onclick(),late=pending.at(-1);
  files.files=[{name:'second.ascatalog',size:100}];files.onchange();
  const second=get('[data-preview]').onclick(),current=pending.at(-1);
  current.resolve(preview('second'));await second;late.resolve(preview('first'));await first;
  assert.equal(late.options.signal.aborted,true);
  assert.equal(get('[data-apply]').disabled,false);
  const applying=get('[data-apply]').onclick({currentTarget:get('[data-apply]')}),write=pending.at(-1);
  assert.equal(JSON.parse(write.options.body).id,'second','A late preview must not replace the selected file owner');
  assert.equal(files.disabled,true);
  const count=pending.length;await get('[data-apply]').onclick({currentTarget:get('[data-apply]')});assert.equal(pending.length,count,'Double click cannot launch another write');
  write.reject(Error('Unknown response after commit'));await flush();
  const reconciliation=pending.at(-1);assert.ok(reconciliation.url.endsWith('/stages/second'));
  await get('[data-apply]').onclick({currentTarget:get('[data-apply]')});assert.equal(pending.at(-1),reconciliation,'An uncertain outcome cannot blindly replay');
  reconciliation.resolve({id:'second',state:'applied',result:{added:1,reused:0,retained:0,decisions_applied:0,decisions_pending:0}});await applying;
  assert.equal(get('[data-apply]').disabled,true);
  assert.match(get('[data-preview-result]').children[0].textContent,/1 entries added/);
  pending.at(-1).resolve({entries:1,files_added:1,pending_decisions:0,retained_records:0});await flush();
  files.files=[{name:'closed.ascatalog',size:100}];files.onchange();const closed=get('[data-preview]').onclick(),afterClose=pending.at(-1);abort.abort();afterClose.resolve(preview('closed'));await closed;
  assert.equal(afterClose.options.signal.aborted,true);assert.equal(get('[data-apply]').disabled,true,'Closing the owner cannot revive an import preview');
  console.log('Sharing controller: file ownership, out-of-order responses, single write, uncertain-result reconciliation, abort ownership verified');
})().catch(error=>{console.error(error);process.exitCode=1;});
