/* Actual request/error helpers with controlled body outcomes; no network/browser. */
'use strict';
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const source=fs.readFileSync(path.join(__dirname,'../app/static/app.js'),'utf8');
function block(start,end){const a=source.indexOf(start),b=source.indexOf(end,a);assert.ok(a>=0&&b>a);return source.slice(a,b);}
const finished=[];
const context={performance,AbortController,setTimeout,clearTimeout,token:'',adminAuthDismissed:false,
  UI_BUILD_VERSION:'fixture',lastHealthSnapshot:{version:'fixture'},clientErrors:[],
  beginManualActionsMutation:(_url,method)=>method!=='GET',finishManualActionsMutation:failed=>finished.push(failed)};
vm.createContext(context);
vm.runInContext(block('function rememberClientError(', 'const uiTimingSamples='),context);
vm.runInContext(block('async function api(', "window.addEventListener('unhandledrejection'"),context);
const options={timeoutMs:0,noMutationRefresh:true};
const response=body=>({ok:true,status:200,headers:{get:()=>null},...body});
function prior(url,method='GET'){const item={id:url,path:url,method,message:'earlier failure',resolved:false};context.clientErrors.push(item);return item;}
(async()=>{
  const jsonPrior=prior('/fixture');
  context.fetch=async()=>response({json:async()=>{throw new SyntaxError('controlled malformed JSON');}});
  await assert.rejects(context.api('/fixture',options),/controlled malformed/);
  assert.equal(jsonPrior.resolved,false,'HTTP headers do not establish successful body decoding');
  assert.ok(context.clientErrors.some(item=>!item.resolved&&item.message.includes('controlled malformed')));
  context.fetch=async()=>response({json:async()=>({value:1})});
  assert.equal((await context.api('/fixture',options)).value,1);
  assert.equal(jsonPrior.resolved,true);
  assert.ok(context.clientErrors.filter(item=>item.path==='/fixture').every(item=>item.resolved));

  const blobPrior=prior('/blob');
  context.fetch=async()=>response({blob:async()=>{throw new TypeError('controlled blob failure');}});
  await assert.rejects(context.api('/blob',{...options,responseType:'blob'}),/controlled blob failure/);
  assert.equal(blobPrior.resolved,false);
  const blob=new Blob(['fixture']);
  context.fetch=async()=>response({blob:async()=>blob});
  assert.equal(await context.api('/blob',{...options,responseType:'blob'}),blob);
  assert.equal(blobPrior.resolved,true);

  const mutationPrior=prior('/mutation','POST');
  context.fetch=async()=>response({json:async()=>{throw new SyntaxError('controlled mutation body failure');}});
  await assert.rejects(context.api('/mutation',{...options,method:'POST'}),/mutation body failure/);
  assert.equal(mutationPrior.resolved,false);assert.equal(finished.at(-1),true);
  context.fetch=async()=>response({json:async()=>({saved:true})});
  await context.api('/mutation',{...options,method:'POST'});
  assert.equal(mutationPrior.resolved,true);assert.equal(finished.at(-1),false);

  const cancelPrior=prior('/cancel'),controller=new AbortController(),before=context.clientErrors.length;
  context.fetch=async()=>response({json:async()=>{controller.abort();const error=new Error('controlled cancellation');error.name='AbortError';throw error;}});
  await assert.rejects(context.api('/cancel',{...options,signal:controller.signal}),error=>error.name==='AbortError'&&error.expectedCancellation);
  assert.equal(cancelPrior.resolved,false);assert.equal(context.clientErrors.length,before);
  console.log('API recovery follows successful body decoding; blob, mutation and cancellation ownership preserved');
})().catch(error=>{console.error(error);process.exitCode=1;});
