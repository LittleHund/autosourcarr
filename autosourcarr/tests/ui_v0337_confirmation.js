const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
class Element{
 constructor(tag){this.tag=tag;this.children=[];this.events={};this.attributes={};this.disabled=false;this.value='';this.isConnected=true;}
 append(...nodes){for(const node of nodes){node.parent=this;this.children.push(node);}}
 setAttribute(key,value){this.attributes[key]=value;}
 addEventListener(key,fn){this.events[key]=fn;}
 remove(){this.isConnected=false;if(this.parent)this.parent.children=this.parent.children.filter(x=>x!==this);}
 focus(){document.activeElement=this;}
 showModal(){this.open=true;}
 close(value=''){this.returnValue=value;this.open=false;this.events.close?.({});}
 fire(name){return this.events[name]?.({preventDefault(){},target:this});}
}
const document={body:new Element('body'),activeElement:null,createElement:tag=>new Element(tag)};
const context={window:null,document,console};context.window=context;
vm.createContext(context);
const appSource=fs.readFileSync(__dirname+'/../app/static/app.js','utf8');
for(const version of ['0.3.37','0.3.36']){
 const versionContext={document:{currentScript:{src:'https://private.invalid/static/app.js?v='+version},querySelector:()=>null}};
 vm.createContext(versionContext);vm.runInContext(appSource.split('\n')[0]+';globalThis.reported=UI_BUILD_VERSION;',versionContext);
 assert.equal(versionContext.reported,version);
}
vm.runInContext(fs.readFileSync(__dirname+'/../app/static/confirmation-dialog.js','utf8'),context);
const walk=node=>[node,...node.children.flatMap(walk)],find=(node,id)=>walk(node).find(x=>x.id===id);
const text=node=>[node.textContent||'',...node.children.map(text)].join(' ');
const tick=()=>new Promise(resolve=>setImmediate(resolve));
(async()=>{
 const button=new Element('button');context.$=selector=>selector==='#resetAllManualActions'?button:null;
 context.prompt=()=>{throw Error('Clipped native prompt must not be used');};
 context.toast=()=>{};context.catalogViewRevision=0;let reloads=0;context.location={reload:()=>reloads++};
 const preview={revision:'reviewed-revision',counts:[{label:'Manual named entries',count:18}],retained:'Retained '+('library data '.repeat(300)),effect:'Effect '+('review details '.repeat(300))};
 const requests=[];context.api=async(path,options)=>{requests.push({path,options});return options?.method==='POST'?{ok:true}:preview;};
 const source=fs.readFileSync(__dirname+'/../app/static/app.js','utf8');
 vm.runInContext(source.slice(source.indexOf('// Preview before confirmation.')),context);
 let action=button.onclick();await tick();
 const dialog=document.body.children[0];assert.equal(dialog.tag,'dialog');assert(dialog.open);
 assert(text(dialog).includes('Manual named entries: 18'));assert(text(dialog).includes(preview.retained));
 const phrase=find(dialog,'confirmationPhrase'),input=find(dialog,'confirmationInput'),confirm=find(dialog,'confirmationAccept');
 assert.equal(phrase.textContent,'RESET ALL MANUAL ACTIONS');assert.equal(phrase.parent.className,'confirmationFooter');
 assert.equal(document.activeElement,input);assert(confirm.disabled);assert.equal(requests.length,1);
 input.value='RESET';input.fire('input');assert(confirm.disabled);
 input.value='RESET ALL MANUAL ACTIONS';input.fire('input');assert(!confirm.disabled);
 confirm.fire('click');await action;
 assert.equal(requests.length,2);const post=requests[1];assert.equal(post.options.method,'POST');
 assert.deepEqual(JSON.parse(post.options.body),{revision:'reviewed-revision',confirmation:'RESET ALL MANUAL ACTIONS'});
 assert.equal(reloads,1);assert.equal(button.disabled,false);assert.equal(document.body.children.length,0);
 requests.length=0;reloads=0;action=button.onclick();await tick();document.body.children[0].fire('cancel');await action;
 assert.equal(requests.length,1);assert.equal(reloads,0);assert.equal(button.disabled,false);
 console.log('PASS visible typed phrase outside long preview, disabled-until-matched confirmation, exact revision/POST, cancel safety and ownership');
})().catch(error=>{console.error(error);process.exitCode=1;});
