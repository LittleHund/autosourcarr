'use strict';
// Phase 12 (SET-8/SET-9): System → Backup & restore uses /api/v1 through the generated client; the update notice offers
// "Roll back to the backup from …" (preview + typed confirmation + apply) and "Keep current"; no /api/admin call remains.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const source=fs.readFileSync(path.join(__dirname,'../app/static/instance-tools.js'),'utf8');
const client=fs.readFileSync(path.join(__dirname,'../app/static/generated/api-client.js'),'utf8');
const settle=()=>new Promise(resolve=>setImmediate(resolve));

(async()=>{
  const nodes=new Map(),calls=[];let created=0,dismissed=false;
  function node(id){
    if(!nodes.has(id))nodes.set(id,{id,open:false,disabled:false,textContent:'',innerHTML:'',value:'',files:[],children:[],className:'',
      replaceChildren(...children){this.children=children;},append(...children){this.children.push(...children);},
      get lastChild(){return this.children.at(-1);},addEventListener(){},showModal(){this.open=true;},close(){this.open=false;},focus(){},click(){}});
    return nodes.get(id);
  }
  const nav=node('nav-tool');nav.dataset={instanceTool:'backups'};
  const document={activeElement:nav,getElementById:node,createElement:()=>node('created-'+(created++)),
    querySelectorAll:selector=>selector==='.appNav [data-instance-tool]'?[nav]:[],addEventListener(){}};
  const backupId='a'.repeat(32);
  const preview={stage_id:'b'.repeat(32),preview_token:'token-1',backup:{created_at:'2026-11-01T10:00:00Z',app_version:'0.5.0',format_version:3},
    version_check:{ok:true,message:'This backup matches this version\'s database schema.'},
    databases:[{name:'catalog.sqlite',tables:[{table:'entries',current:4,backup:3}]}],
    settings:{included:true,changed:['settings.SOURCE_LANGUAGES'],secrets_kept_from_this_instance:['settings.MAIN_SUWAYOMI_PASSWORD'],secrets_to_enter_again:[]},
    effect:'The restore replaces both databases.'};
  const api=async(url,options={})=>{
    calls.push({url,method:options.method||'GET',body:options.body,headers:options.headers});
    if(url==='/api/v1/backups'&&(options.method||'GET')==='GET')return {items:[{id:backupId,kind:'automatic',created_at:'2026-11-01T10:00:00Z',bytes:1048576,entries:3,files:5,protected:false}],
      update:dismissed?null:{status:'rolled-back',to_version:'0.5.1',from_version:'0.5.0',backup_id:backupId,backup_created_at:'2026-11-01T10:00:00Z',message:'The update failed and was rolled back.',error:'InvalidOperationException: x'},
      limits:{manual:10,automatic_kept:3}};
    if(url==='/api/v1/backups'&&options.method==='POST')return {queued:true,task_id:'task-create'};
    if(url==='/api/v1/backups/restore/preview')return preview;
    if(url==='/api/v1/backups/restore/apply')return {queued:true,task_id:'task-restore',restart_required:true};
    if(url==='/api/v1/backups/update-notice/dismiss'){dismissed=true;return {dismissed:true};}
    throw Error('Unexpected request '+url);
  };
  const window={AutoSourcarrReimagined:{closeNavigation(){}}};
  const context=vm.createContext({window,document,api,AbortController,TextEncoder,URL,setTimeout,btoa,confirm:()=>false,
    esc:value=>String(value??''),matchMedia:()=>({matches:false})});
  vm.runInContext(client,context);vm.runInContext(source,context);
  nav.onclick({preventDefault(){},stopPropagation(){}});
  await settle();await settle();
  assert.equal(calls[0].url,'/api/v1/backups','The dialog lists through /api/v1');

  // The rollback notice with its two choices.
  const notice=node('riBackupNotice').children[0];assert.ok(notice,'Update notice shown');
  assert.match(notice.innerHTML,/Update rolled back/);
  const [rollback,keep]=notice.children[0].children;
  assert.match(rollback.textContent,/^Roll back to the backup from /);assert.equal(keep.textContent,'Keep current');

  // One click: preview of that backup, then the typed confirmation, then the apply with the preview token.
  await rollback.onclick();await settle();
  const previewCall=calls.find(call=>call.url==='/api/v1/backups/restore/preview');
  assert.deepEqual(JSON.parse(previewCall.body),{backup_id:backupId});
  assert.match(node('riRestorePreview').innerHTML,/Backup checked/);
  assert.match(node('riRestorePreview').innerHTML,/settings.MAIN_SUWAYOMI_PASSWORD/,'Kept secrets are named, never shown');
  const restore=node('riRestoreApply').children[0];
  node('riRestoreConfirmation').value='yes';await restore.onclick();await settle();
  assert.ok(!calls.some(call=>call.url==='/api/v1/backups/restore/apply'),'No apply without the typed confirmation');
  assert.match(node('riInstanceStatus').textContent,/Type RESTORE THIS INSTANCE/);
  node('riRestoreConfirmation').value='RESTORE THIS INSTANCE';await restore.onclick();await settle();
  const apply=calls.find(call=>call.url==='/api/v1/backups/restore/apply');
  assert.deepEqual(JSON.parse(apply.body),{stage_id:preview.stage_id,preview_token:'token-1',confirm:true});
  assert.match(node('riInstanceStatus').textContent,/Restore queued in Tasks/);

  // Keep current hides the notice.
  await keep.onclick();await settle();await settle();
  assert.ok(dismissed);assert.equal(node('riBackupNotice').children.length,0,'Notice gone after Keep current');

  // Create: an empty passphrase sends no passphrase; the field is cleared after use.
  node('riBackupPassword').value='';await node('riBackupCreate').children[0].onclick();await settle();
  const create=calls.find(call=>call.url==='/api/v1/backups'&&call.method==='POST');
  assert.deepEqual(JSON.parse(create.body),{});
  node('riBackupPassword').value='correct horse battery staple';await node('riBackupCreate').children[0].onclick();await settle();
  assert.deepEqual(JSON.parse(calls.filter(call=>call.url==='/api/v1/backups'&&call.method==='POST').at(-1).body),{passphrase:'correct horse battery staple'});
  assert.equal(node('riBackupPassword').value,'','Passphrase field cleared');

  // Upload preview: the raw file with the base64 passphrase header.
  node('riRestoreFile').files=[{size:100}];node('riRestorePassword').value='pässphrase-long-enough';
  await node('riRestoreActions').children[0].onclick();await settle();
  const upload=calls.filter(call=>call.url==='/api/v1/backups/restore/preview').at(-1);
  assert.equal(upload.headers['Content-Type'],'application/octet-stream');
  assert.equal(Buffer.from(upload.headers['X-Backup-Passphrase'],'base64').toString('utf8'),'pässphrase-long-enough');
  assert.equal(node('riRestorePassword').value,'','Restore passphrase cleared');

  assert.ok(calls.every(call=>call.url.startsWith('/api/v1/')),'No /api/admin call remains: '+calls.map(call=>call.url).join(', '));
  console.log('Backups UI: /api/v1 client, rollback notice with one-click rollback and Keep current, typed confirmation, passphrase handling');
})().catch(error=>{console.error(error);process.exitCode=1;});
