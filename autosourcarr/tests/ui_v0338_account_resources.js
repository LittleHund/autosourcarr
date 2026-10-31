/* Real UI allowlist; no browser, live account or HTTP call. */
'use strict';
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const scope={};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../app/static/repair-login.js'),'utf8'),scope);
const helper=scope.AutoSourcarrRepairLogin;
const state={state:'failed',action:'login',diagnostics:{reason:'resource-limit',phase:'starting',
  exit_code:70,http_status:null,code_received:false,launcher_stage:'exec',os_error:11,
  resources:{nproc_soft:'9007199254740993',nproc_hard:'unlimited',cgroup_pids_current:'0',cgroup_pids_max:'512',
    cgroup_scope:'visible-v2-only',ancestor_limits:'unknown',open_files_soft:true,open_files_hard:1024,
    cgroup_memory_max_bytes:'18446744073709551616',address_space_soft_bytes:'/private/DO_NOT_EXPORT',private_password:'DO_NOT_EXPORT'}}};
const result=JSON.parse(JSON.stringify(helper.diagnostics(state)));
assert.deepEqual(result.resources,{nproc_soft:'9007199254740993',nproc_hard:'unlimited',cgroup_pids_current:'0',
  cgroup_pids_max:'512',cgroup_scope:'visible-v2-only',ancestor_limits:'unknown'});
assert.equal(result.os_error,11);
assert.ok(!JSON.stringify(result).includes('DO_NOT_EXPORT'));
for(const resources of [null,[],'private', {nproc_soft:'12\n'}, {cgroup_pids_current:'unlimited'}]){
  const data=helper.diagnostics({...state,diagnostics:{...state.diagnostics,resources}});
  assert.equal(data.reason,'resource-limit');assert.equal(data.resources,undefined);
}
const legacy=helper.diagnostics({...state,diagnostics:{...state.diagnostics,resources:undefined}});
assert.equal(legacy.resources,undefined);
console.log('Account UI resource evidence: exact strings, private extras rejected, legacy compatible');
