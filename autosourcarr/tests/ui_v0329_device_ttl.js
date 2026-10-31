/* Device-code expiry belongs to the worker clock; this is a helper contract test. */
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const helper=process.argv[2]||path.resolve(__dirname,'../app/static/repair-login.js');
const source=fs.readFileSync(helper,'utf8');
const workerNow=1791244800000;
function context(offset){
  const sandbox={Date:{now:()=>workerNow+offset}};
  sandbox.globalThis=sandbox;
  vm.runInNewContext(source,sandbox,{filename:helper});
  return sandbox.AutoSourcarrRepairLogin;
}
const state={busy:true,action:'login',state:'running',verification_url:'https://auth.openai.com/codex/device',
             device_code:'ABCD-EFGH',expires_at:workerNow/1000+900};
let cases=0;
function check(offset,extra,expected,label){
  assert.equal(context(offset).valid({...state,...extra}),expected,label);
  cases++;
}
check(3600000,{expires_in:900},true,'browser one hour ahead must display a live worker code');
check(-3600000,{expires_in:900},true,'browser one hour behind must display a live worker code');
check(0,{expires_in:900},true,'normal clock still displays live code');
check(-3600000,{expires_in:0},false,'expired worker TTL must not fall back to behind browser clock');
check(0,{expires_in:0},false,'zero TTL is expired despite future absolute timestamp');
check(0,{expires_in:-1},false,'negative TTL is expired');
check(0,{expires_in:900,busy:false},false,'terminal action never exposes the code');
check(0,{expires_in:900,state:'cancelled'},false,'cancelled action cannot expose the code while its child drains');
check(0,{expires_in:900,state:'failed'},false,'failed action cannot expose a stale code');
check(0,{expires_in:900,state:'completed'},false,'completed action cannot expose a stale code');
check(0,{expires_in:900,action:'logout'},false,'another account action never exposes the code');
check(0,{expires_in:900,verification_url:'https://evil.example/codex/device'},false,'official URL allowlist stays required');
check(0,{expires_in:900,device_code:'access_token=PRIVATE'},false,'only code shape is accepted');
check(0,{},true,'legacy worker without remaining TTL keeps absolute expiry contract');
check(0,{expires_at:workerNow/1000-1},false,'legacy expired response stays rejected');
console.log(cases+' device TTL/clock/ownership/URL helper cases passed');
