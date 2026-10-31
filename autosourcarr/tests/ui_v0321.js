const {harness,tick}=require('./ui_setup.js');
const assert=require('assert/strict');
(async()=>{
 const h=harness();
 h.data.fields.push(
  {key:'REPAIR_AI_PROVIDER',step:'repair',kind:'choice',label:'AI',options:[['chatgpt','ChatGPT'],['claude','Claude'],['custom','Custom']],description:'',value:'chatgpt'},
  {key:'REPAIR_AI_API_KEY',step:'repair',kind:'secret',label:'Key',description:'',value:'',configured:true},
  {key:'REPAIR_AI_BASE_URL',step:'repair',kind:'url',label:'URL',description:'',value:''},
  {key:'REPAIR_AI_PROTOCOL',step:'repair',kind:'choice',label:'Format',options:[['responses','Responses'],['anthropic','Anthropic']],description:'',value:'responses'},
  {key:'REPAIR_AI_MODEL',step:'repair',kind:'text',label:'Model',description:'',value:''});
 let logged=false,cancelled=0,operation_id='';const base=h.context.api;
 h.context.api=async(path,opts)=>{
  if(path.endsWith('/control/login')){logged=true;operation_id=JSON.parse(opts.body).operation_id;return {busy:true,operation_id};}
  if(path.endsWith('/control/cancel-login')){cancelled++;logged=false;return {};}
  if(path.endsWith('/control'))return {busy:logged,operation_id,action:'login',device_code:'ABCD-EFGH',verification_url:'https://auth.openai.com/codex/device',expires_at:Date.now()/1000+120};
  return base(path,opts);
 };
 await h.context.AutoSourcarrSetup.open();for(let i=0;i<7;i++)await h.next();await tick();
 assert.equal(h.nodes.get('setupTitle').textContent,'Repair and AI');assert(h.nodes.get('setupAccount-login'));
 assert(!h.calls.some(c=>c[0].endsWith('/finish')));
 await h.nodes.get('setupAccount-login').events.click();
 assert(h.nodes.get('setupDeviceCode').innerHTML.includes('ABCD-EFGH'));assert(h.nodes.get('setupNext').disabled);assert(h.nodes.get('setupBack').disabled);
 const title=h.nodes.get('setupTitle').textContent;await h.next();assert.equal(h.nodes.get('setupTitle').textContent,title);
 h.context.AutoSourcarrSetup.cancel();await tick();assert.equal(cancelled,1);assert(h.nodes.get('setupModal').hidden);
 for(const callback of h.timers)callback();await tick();assert.equal(h.nodes.get('setupBody').innerHTML,'');
 await h.context.AutoSourcarrSetup.open();for(let i=0;i<7;i++)await h.next();
 const select=h.nodes.get('setup-REPAIR_AI_PROVIDER');select.value='claude';select.events.change();
 assert.equal(h.nodes.get('setup-REPAIR_AI_BASE_URL').value,'https://api.anthropic.com/v1');
 h.nodes.get('setup-REPAIR_AI_MODEL').value='my-available-model';h.nodes.get('setup-REPAIR_AI_API_KEY').value='private-new-key';
 for(let i=0;i<4;i++)await h.next();
 const post=h.calls.find(c=>c[0].endsWith('/finish'));assert(post);const changes=JSON.parse(post[1].body).changes;
 assert.equal(changes.REPAIR_AI_PROVIDER,'claude');assert.equal(changes.REPAIR_AI_API_KEY,'private-new-key');assert.equal(changes.REPAIR_AI_PROTOCOL,'anthropic');
 assert(!JSON.stringify([...h.storage]).includes('private-new-key'));
 // A definitive rejected start cannot cancel another browser's operation.
 const rejected=harness();rejected.data.fields.push({key:'REPAIR_AI_PROVIDER',step:'repair',kind:'choice',label:'AI',options:[['chatgpt','ChatGPT']],description:'',value:'chatgpt'});
 let cancelOther=0;const api=rejected.context.api;
 rejected.context.api=async(path,opts)=>{
   if(path.endsWith('/control/login')){const error=new Error('Already running');error.status_code=409;throw error;}
   if(path.endsWith('/control/cancel-login')){cancelOther++;return {};}
   if(path.endsWith('/control'))return {busy:false};
   return api(path,opts);
 };
 await rejected.context.AutoSourcarrSetup.open();for(let i=0;i<7;i++)await rejected.next();await tick();
 await rejected.nodes.get('setupAccount-login').events.click();rejected.context.AutoSourcarrSetup.cancel();await tick();assert.equal(cancelOther,0);
 console.log('0.3.21 setup: inline login before Finish, cancellation/navigation guards, provider preset, isolated key draft passed');
})().catch(e=>{console.error(e);process.exitCode=1;});
