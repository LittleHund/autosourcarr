const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const elements={'#exportErrors':{},'#errorExportStatus':{}};
const ctx={document:{querySelector:key=>elements[key]||null},console,Blob};
vm.createContext(ctx);
vm.runInContext("const UI_BUILD_VERSION='0.3.36';var lastBackendErrors={items:[]};var lastHealthSnapshot={};var clientErrors=[{message:'Failed at https://private-browser.local/api/admin/test?q=private-search',detail:'Bearer private-bearer person@example.test 10.1.2.3 C:\\\\Users\\\\private-user\\\\project\\\\app.js'}];",ctx);
vm.runInContext(fs.readFileSync(__dirname+'/../app/static/workbench.js','utf8'),ctx);
(async()=>{
 let saved,filename,calls=0;
 ctx.downloadBlob=(blob,name)=>{saved=blob;filename=name;};
 ctx.api=async path=>{assert.equal(path,'/api/admin/diagnostics');calls++;return {
   runtime:{app_version:'0.3.36'},errors:{items:[]},internal_services:{services:{builder:{state:'failed',exit_code:17}}},
   logs:{retention:{bounded:true,files:3},entries:[{source:'builder',message:'Dto.kt:44: unresolved reference'}]},
   username:'private-identity',credential:'private-credential',workspaceId:'private-workspace'};};
 await ctx.exportErrors();
 const text=await saved.text();
 assert.equal(saved.type,'text/plain;charset=utf-8');assert.match(filename,/^AutoSourcarr-diagnostics-.*\.txt$/);
 for(const expected of ['Diagnostic format: 3','Dto.kt:44: unresolved reference','exit_code','17','Browser errors','bounded'])assert(text.includes(expected),expected);
 for(const hidden of ['private-browser.local','private-search','private-bearer','person@example.test','10.1.2.3','private-user','private-identity','private-credential','private-workspace'])assert(!text.includes(hidden),hidden);
 assert.equal(calls,1);assert.equal(elements['#exportErrors'].disabled,false);
 ctx.api=async()=>{throw Error('offline');};
 await ctx.exportErrors();assert((await saved.text()).includes('PARTIAL EXPORT'));
 assert((await saved.text()).includes('process logs were unavailable'));
 assert.equal(elements['#exportErrors'].disabled,false);
 elements['#repositoryList']={innerHTML:''};elements['#restartRepositorySources']={disabled:false};elements['#repositoryStatus']={};
 vm.runInContext('repositoryState={can_restart:true,repositories:[]};',ctx);ctx.renderRepositories();
 let restarts=0;ctx.api=async(path,opts)=>{assert.equal(path,'/api/admin/source-browser/restart');assert.equal(opts.method,'POST');restarts++;return {queued:true};};
 await ctx.applySavedRepositories();assert.equal(restarts,1);assert.match(elements['#repositoryStatus'].textContent,/sign-ins are retained/);
 vm.runInContext('repositoryState.can_restart=false;',ctx);ctx.renderRepositories();await ctx.applySavedRepositories();
 assert.equal(restarts,1);assert.equal(elements['#restartRepositorySources'].disabled,true);
 const translation=ctx.diagnosticCause({message:'All connection attempts failed',component:'translation'});
 assert.match(translation.next,/Settings.*Services/);assert(!translation.next.includes('autosourcarr-translate'));
 const source=ctx.diagnosticCause({message:'Name or service not known',component:'source host'});
 assert.match(source.next,/bundled AutoSourcarr image/);assert(!source.next.includes('external source host'));
 const dotted='package.'.repeat(8192);assert.equal(ctx.scrubDiagnosticExport(dotted),dotted);
 console.log('PASS one diagnostic download: process logs, browser context, privacy, unavailable sections and button recovery');
 console.log('PASS saved repositories use the authenticated internal restart with external-mode admission disabled');
})().catch(error=>{console.error(error);process.exitCode=1;});
