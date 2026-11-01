/* Execute the shipped repair-form submit handler: the body matches POST /api/v1/source-maintenance/jobs
   (pkg_name plus optional new_host/https) and the new web address is trimmed to a host name or refused. */
const fs=require('fs'),path=require('path'),vm=require('vm'),assert=require('assert/strict');
const source=fs.readFileSync(path.join(__dirname,'../app/static/source-repairs.js'),'utf8');
const html=fs.readFileSync(path.join(__dirname,'../app/static/index.html'),'utf8');
for(const id of ['sourceRepairForm','sourceRepairPackage','sourceRepairNewHost','sourceRepairNewHostStatus'])assert(html.includes('id="'+id+'"'),id);
for(const gone of ['sourceRepairModule','sourceRepairRevision','sourceRepairIssue'])assert(!html.includes('id="'+gone+'"')&&!source.includes(gone),gone+' is ignored by the server and must not be asked for');
const start=source.indexOf('  function repairNewHost(value){');
const end=source.indexOf('  window.loadSourceRepairs=load;',start);
assert(start>0&&end>start);
let submitHandler;const submit={disabled:false};
const elements={
  sourceRepairPackage:{value:'eu.kanade.tachiyomi.extension.en.example'},
  sourceRepairNewHost:{value:'',focused:false,focus(){this.focused=true;}},
  sourceRepairNewHostStatus:{textContent:'Only the site name is used.',dataset:{}},
  sourceRepairForm:{addEventListener:(name,fn)=>{assert.equal(name,'submit');submitHandler=fn;}},
};
const calls=[];
const context={byId:id=>elements[id],URL,api:async(url,opts)=>{calls.push({url,opts});return {id:'job1'};},
  repairSubmitting:false,repairBlocked:()=>false,storageError:'',toast:message=>{throw new Error('unexpected toast '+message);},
  window:{openSourceRepairTask:async()=>{}}};
vm.createContext(context);
vm.runInContext('var repairSubmitting=false;'+source.slice(start,end)+'\nthis.repairNewHost=repairNewHost;',context);
const send=async value=>{
  elements.sourceRepairNewHost.value=value;
  const before=calls.length;
  await submitHandler({preventDefault(){},target:{querySelector:()=>submit}});
  return calls.length>before?JSON.parse(calls[calls.length-1].opts.body):null;
};
(async()=>{
  assert.deepEqual(await send(''),{pkg_name:'eu.kanade.tachiyomi.extension.en.example'});
  assert.equal(calls[0].url,'/api/v1/source-maintenance/jobs');assert.equal(calls[0].opts.method,'POST');
  assert.deepEqual(await send('  New-Site.Example. '),{pkg_name:'eu.kanade.tachiyomi.extension.en.example',new_host:'new-site.example'});
  assert.deepEqual(await send('https://www.new.example/manga/one?page=2#top'),
    {pkg_name:'eu.kanade.tachiyomi.extension.en.example',new_host:'www.new.example',https:true});
  assert.deepEqual(await send('http://new.example/'),{pkg_name:'eu.kanade.tachiyomi.extension.en.example',new_host:'new.example'});
  assert.deepEqual(await send('new.example/path'),{pkg_name:'eu.kanade.tachiyomi.extension.en.example',new_host:'new.example'});
  assert.equal(context.repairNewHost('bücher.example').host,'xn--bcher-kva.example');
  for(const bad of ['ftp://new.example','new.example:8080','user@new.example','localhost','not a host','-bad.example','javascript:alert(1)']){
    elements.sourceRepairNewHost.focused=false;
    assert.equal(await send(bad),null,bad+' must not be sent');
    assert(elements.sourceRepairNewHostStatus.textContent.startsWith('New web address'),bad);
    assert(elements.sourceRepairNewHost.focused,bad);
  }
  await send('new.example');assert.equal(elements.sourceRepairNewHostStatus.textContent,'Only the site name is used.','A valid address restores the hint');
  assert.equal(submit.disabled,false);
  console.log('Repair form: pkg_name with optional new_host/https, links trimmed to the host, invalid addresses refused before any POST');
})().catch(error=>{console.error(error);process.exitCode=1;});
