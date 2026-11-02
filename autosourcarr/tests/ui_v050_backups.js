'use strict';
// Phase 12 (SET-8/SET-9): System → Backup & restore uses /api/v1 through the generated client. The update notice offers
// "Roll back to the backup from …" only when that backup needs no migration (otherwise guidance + download); the
// safe-mode notice offers "Retry the update" and, after a failed backup, "Migrate without a backup"; an armed restore
// can be cancelled; uploads are multipart with the passphrase as a field; downloads are plain links.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const source=fs.readFileSync(path.join(__dirname,'../app/static/instance-tools.js'),'utf8');
const client=fs.readFileSync(path.join(__dirname,'../app/static/generated/api-client.js'),'utf8');
const settle=async()=>{for(let i=0;i<4;i++)await new Promise(resolve=>setImmediate(resolve));};
const backupId='a'.repeat(32);
const preview={stage_id:'b'.repeat(32),preview_token:'token-1',backup:{created_at:'2026-11-01T10:00:00Z',app_version:'0.5.0',format_version:3},
  version_check:{ok:true,message:'This backup matches this version\'s database schema.'},decisions_warning:'2 manual decisions newer than the backup will be lost.',
  databases:[{name:'catalog.sqlite',tables:[{table:'entries',current:4,backup:3}]}],
  settings:{included:true,changed:['settings.SOURCE_LANGUAGES'],secrets_kept_from_this_instance:['settings.MAIN_SUWAYOMI_PASSWORD'],secrets_to_enter_again:[]},
  effect:'The restore is applied at the next start.'};

async function harness(listing){
  const nodes=new Map(),calls=[];let created=0;const state={dismissed:false};
  function node(id){
    if(!nodes.has(id))nodes.set(id,{id,open:false,disabled:false,textContent:'',innerHTML:'',value:'',files:[],children:[],className:'',href:'',download:'',
      replaceChildren(...children){this.children=children;},append(...children){this.children.push(...children);},
      get lastChild(){return this.children.at(-1);},addEventListener(){},showModal(){this.open=true;},close(){this.open=false;},focus(){},click(){}});
    return nodes.get(id);
  }
  const nav=node('nav-tool');nav.dataset={instanceTool:'backups'};
  const document={activeElement:nav,getElementById:node,createElement:tag=>{const n=node('created-'+(created++));n.tag=tag;return n;},
    querySelectorAll:selector=>selector==='.appNav [data-instance-tool]'?[nav]:[],addEventListener(){}};
  const api=async(url,options={})=>{
    calls.push({url,method:options.method||'GET',body:options.body,headers:options.headers});
    if(url==='/api/v1/backups'&&(options.method||'GET')==='GET')return listing(state);
    if(url==='/api/v1/backups'&&options.method==='POST')return {queued:true,task_id:'task-create'};
    if(url==='/api/v1/backups/restore/preview')return preview;
    if(url==='/api/v1/backups/restore/apply')return {armed:true,restart_required:true,restarting:false,message:'Restore armed. Restart AutoSourcarr (its container) to apply it; until then changes are refused.'};
    if(url==='/api/v1/backups/restore/pending'){state.cancelled=true;return {cancelled:true};}
    if(url==='/api/v1/backups/update-notice/dismiss'){state.dismissed=true;return {dismissed:true};}
    if(url==='/api/v1/backups/safe-mode/retry')return {retry:true,message:'A fresh backup was taken. The next start runs the update again.'};
    if(url==='/api/v1/backups/safe-mode/migrate-without-backup')return {allowed:true,message:'The next start migrates without a backup.'};
    throw Error('Unexpected request '+url);
  };
  const window={AutoSourcarrReimagined:{closeNavigation(){}}};
  const context=vm.createContext({window,document,api,AbortController,TextEncoder,URL,Blob,setTimeout,confirm:()=>true,
    esc:value=>String(value??''),matchMedia:()=>({matches:false})});
  vm.runInContext(client,context);vm.runInContext(source,context);
  nav.onclick({preventDefault(){},stopPropagation(){}});
  await settle();
  return {node,calls,state};
}
const item={id:backupId,kind:'automatic',created_at:'2026-11-01T10:00:00Z',bytes:1048576,entries:3,files:5,protected:false,schema_matches_build:true};

(async()=>{
  // 1. Updated, backup at this schema: one-click rollback (preview → typed confirmation → arm) and Keep current.
  {
    const {node,calls,state}=await harness(state=>({items:[item],update:state.dismissed?null:{status:'updated',to_version:'0.5.1',from_version:'0.5.0',backup_id:backupId,backup_created_at:item.created_at,message:'Updated to 0.5.1.'},
      safe_mode:null,restore:null,restore_pending:null,limits:{manual:10,automatic_kept:3}}));
    assert.equal(calls[0].url,'/api/v1/backups','The dialog lists through /api/v1');
    const notice=node('riBackupNotice').children[0];assert.match(notice.innerHTML,/Updated to 0.5.1/);
    const [rollback,keep]=notice.children[0].children;
    assert.match(rollback.textContent,/^Roll back to the backup from /);assert.equal(keep.textContent,'Keep current');
    await rollback.onclick();await settle();
    assert.deepEqual(JSON.parse(calls.find(call=>call.url==='/api/v1/backups/restore/preview').body),{backup_id:backupId});
    assert.match(node('riRestorePreview').innerHTML,/2 manual decisions newer than the backup will be lost/);
    const restore=node('riRestoreApply').children[0];assert.equal(restore.textContent,'Restore at the next start');
    node('riRestoreConfirmation').value='yes';await restore.onclick();await settle();
    assert.ok(!calls.some(call=>call.url==='/api/v1/backups/restore/apply'),'No arm without the typed confirmation');
    node('riRestoreConfirmation').value='RESTORE THIS INSTANCE';await restore.onclick();await settle();
    assert.deepEqual(JSON.parse(calls.find(call=>call.url==='/api/v1/backups/restore/apply').body),{stage_id:preview.stage_id,preview_token:'token-1',confirm:true});
    await keep.onclick();await settle();
    assert.ok(state.dismissed);assert.equal(node('riBackupNotice').children.length,0,'Notice gone after Keep current');
    // Saved backups download through a plain link (streamed by the server, nothing buffered in the browser).
    const link=node('riBackupList').children[0].children[0];
    assert.equal(link.tag,'a');assert.equal(link.href,'/api/v1/backups/'+backupId+'/download');
    // Create: empty passphrase sends none; the field is cleared.
    node('riBackupPassword').value='';await node('riBackupCreate').children[0].onclick();await settle();
    assert.deepEqual(JSON.parse(calls.find(call=>call.url==='/api/v1/backups'&&call.method==='POST').body),{});
    // Upload preview: multipart body with the passphrase as a field, never a header.
    node('riRestoreFile').files=[new Blob(['PKfile'])];node('riRestorePassword').value='pässphrase-long-enough';
    await node('riRestoreActions').children[0].onclick();await settle();
    const upload=calls.filter(call=>call.url==='/api/v1/backups/restore/preview').at(-1);
    assert.match(upload.headers['Content-Type'],/^multipart\/form-data; boundary=/);
    assert.equal(upload.headers['X-Backup-Passphrase'],undefined,'No passphrase header');
    const text=await upload.body.text();
    assert.match(text,/name="passphrase"\r\n\r\npässphrase-long-enough\r\n/);assert.match(text,/name="file"; filename=/);
    assert.equal(node('riRestorePassword').value,'','Restore passphrase cleared');
    assert.ok(calls.every(call=>call.url.startsWith('/api/v1/')),'No /api/admin call remains');
  }
  // 2. Updated, backup at the previous schema: no rollback button (it would migrate again), guidance + download instead.
  {
    const {node}=await harness(()=>({items:[{...item,schema_matches_build:false}],update:{status:'updated',to_version:'0.5.1',from_version:'0.5.0',backup_id:backupId,backup_created_at:item.created_at,message:'Updated.'},
      limits:{manual:10,automatic_kept:3}}));
    const notice=node('riBackupNotice').children[0];
    assert.match(notice.innerHTML,/run AutoSourcarr 0.5.0 again and restore this backup there/);
    const labels=notice.children[0].children.map(child=>child.textContent);
    assert.ok(!labels.some(label=>/^Roll back/.test(label)),'No rollback button for an older-schema backup');
    assert.ok(labels.some(label=>/^Download the backup from/.test(label)));
  }
  // 3. Safe mode after a failed backup: Retry the update and Migrate without a backup (with the notice token).
  {
    const {node,calls}=await harness(()=>({items:[],safe_mode:{status:'backup-failed',message:'The automatic backup failed.',previous_version_hint:'Run AutoSourcarr 0.5.0 again (the previous image).',
      error:'IOException: disk full',actions:['retry','migrate-without-backup'],preview_token:'marker-token'},limits:{manual:10,automatic_kept:3}}));
    const notice=node('riBackupNotice').children[0];
    assert.match(notice.innerHTML,/safe mode/);assert.match(notice.innerHTML,/Run AutoSourcarr 0.5.0 again/);
    const [retry,without]=notice.children[0].children;
    assert.equal(retry.textContent,'Retry the update');assert.equal(without.textContent,'Migrate without a backup');
    await retry.onclick();await settle();
    assert.deepEqual(JSON.parse(calls.find(call=>call.url==='/api/v1/backups/safe-mode/retry').body),{confirm:true});
    await without.onclick();await settle();
    assert.deepEqual(JSON.parse(calls.find(call=>call.url==='/api/v1/backups/safe-mode/migrate-without-backup').body),{confirm:true,preview_token:'marker-token'});
  }
  // 4. An armed restore can be cancelled.
  {
    const {node,calls,state}=await harness(()=>({items:[],restore_pending:{backup_created_at:item.created_at},limits:{manual:10,automatic_kept:3}}));
    const notice=node('riBackupNotice').children[0];assert.match(notice.innerHTML,/Restore armed/);
    await notice.children[0].children[0].onclick();await settle();
    assert.ok(state.cancelled);assert.equal(calls.find(call=>call.url==='/api/v1/backups/restore/pending').method,'DELETE');
  }
  // 5. Rollback failed: the notice offers restoring the named backup, never a retry of half-migrated data.
  {
    const {node,calls}=await harness(()=>({items:[item],safe_mode:{status:'rollback-failed',message:'Putting the databases back did not finish.',backup_id:backupId,actions:['restore']},
      limits:{manual:10,automatic_kept:3}}));
    const buttons=node('riBackupNotice').children[0].children[0].children;
    const labels=buttons.map(child=>child.textContent);
    assert.ok(labels.some(label=>/^Restore the backup from/.test(label))&&!labels.includes('Retry the update'),'restore offered, retry not: '+labels.join(', '));
    await buttons[0].onclick();await settle();
    assert.deepEqual(JSON.parse(calls.find(call=>call.url==='/api/v1/backups/restore/preview').body),{backup_id:backupId});
  }
  console.log('Backups UI: /api/v1 client, rollback only without migration, safe-mode actions, cancel armed restore, multipart passphrase, plain download links');
})().catch(error=>{console.error(error);process.exitCode=1;});
