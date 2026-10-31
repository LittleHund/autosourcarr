const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict');
const root=__dirname+'/../app/static/',source=fs.readFileSync(root+'app.js','utf8'),workbench=fs.readFileSync(root+'workbench.js','utf8');
const section=(a,b)=>source.slice(source.indexOf(a),source.indexOf(b,source.indexOf(a)));
const ctx={document:{querySelector:()=>null},console,esc:s=>String(s).replaceAll('<','&lt;').replaceAll('>','&gt;')};
vm.createContext(ctx);vm.runInContext(workbench,ctx);
vm.runInContext(section('function normalizeHexColor(', 'function syncCustomColorInputs('),ctx);
const parse=value=>value.startsWith('#')?ctx.hexToRgb(value):Object.fromEntries(value.match(/\d+/g).map((v,i)=>[['r','g','b'][i],Number(v)]));
let combinations=0;
for(const style of ['neutral','tonal','dual','paper','graphite','aurora'])for(const primary of ['#000000','#FFFFFF','#0000FF','#00FF00','#FF0000','#7DB0FF','#F1BD55','#61D49A','#B69AFF','#F08CA5','#62D4DF'])for(const secondary of ['#000000','#FFFFFF','#EEB392']){
 const p=ctx.appearancePalette(style,ctx.hexToRgb(primary),ctx.hexToRgb(secondary));
 for(const token of ['text','muted','accent','secondary','good','warn','bad'])for(const surface of ['bg','panel','panel2','chrome','control','control-hover','accent-soft','secondary-soft']){
  assert(ctx.colorContrast(parse(p[token]),parse(p[surface]))>=4.5,`${style} ${primary} ${secondary} ${token} on ${surface}`);
 }
 assert(ctx.colorContrast(parse(p.accent),parse(p['on-accent']))>=4.5,'Button text contrast');
 assert.deepEqual(parse(p['accent-border']),{...ctx.hexToRgb(primary)},'Primary controls the exact border hue');
 combinations++;
}
console.log('PASS '+combinations+' style/color combinations retain readable text, status and action colors');
const raw='HTTP error 521\n at extension.Source(Source.kt:42)\nCaused by: upstream refused request';
const text=ctx.diagnosticExportText({history_count:2,items:[{message:raw,detail:'all details',resolved:false},{message:'older',resolved:true,resolved_at:'then'}]},[{message:'browser timeout',count:4,detail:'X-AutoSourcarr-API-Key: reader-secret\nAUTOSOURCARR_READER_API_TOKEN=reader-secret\nAuthorization: Bearer secret\nhttps://example.test/chapter?token=secret&part=3'}],{version:'0.2.93',source_host:{ok:true,url:'http://private:4567',token:'do-not-export'}},'offline');
assert(text.includes(raw));assert(text.includes('all details'));assert(text.includes('resolved_at: then'));assert(text.includes('count: 4'));assert(text.includes('PARTIAL EXPORT'));assert(!text.includes('secret'));assert(!text.includes('do-not-export'));assert(text.includes('part=[redacted]'));
console.log('PASS Error export includes complete retained evidence, resolved history and partial-refresh labeling, with common credentials redacted');
vm.runInContext("var importsState={runId:'A',items:[{id:1,state:'unresolved',suggestions:[{id:10,preferred_title:'First'},{id:20,preferred_title:'Second'}]}]};",ctx);
ctx.importCandidateHtml=(_run,_item,c)=>'<article>Candidate '+c.id+'</article>';
const item=ctx.importsState.items[0];let rendered=ctx.importCandidateChoices('A',item);
assert(rendered.includes('<article>Candidate 10</article>'));assert(!rendered.includes('<article>Candidate 20</article>'));
ctx.selectImportCandidate(1,20);rendered=ctx.importCandidateChoices('A',item);assert(rendered.includes('<article>Candidate 20</article>'));
const fresh=JSON.parse(JSON.stringify(item));delete fresh.ui_candidate_id;assert(ctx.importCandidateChoices('A',fresh).includes('Candidate 20'),'Selection survives a fresh polled snapshot');assert(ctx.importCandidateChoices('B',fresh).includes('Candidate 10'),'Selection is isolated by run');
item.suggestions=[item.suggestions[0]];assert(ctx.importCandidateChoices('A',item).includes('Candidate 10'));
console.log('PASS Import candidate selection shows one detail and recovers if that candidate disappears');

(async()=>{
 const elements={'#downloadApk':{},'#apkDownloadStatus':{}};ctx.document.querySelector=q=>elements[q];let saved=0;
 ctx.api=async()=>({type:'text/html',size:100});ctx.downloadBlob=()=>saved++;
 vm.runInContext("apkDownloadState={available:true,downloading:false,version:'0.2.93'}",ctx);
 await ctx.downloadCurrentApk();assert.equal(saved,0);assert.match(elements['#apkDownloadStatus'].textContent,/sign-in/);
 ctx.api=async()=>({type:'application/vnd.android.package-archive',size:100});await ctx.downloadCurrentApk();assert.equal(saved,1);
 console.log('PASS APK download rejects successful HTML sign-in responses and accepts the APK MIME type');
})().catch(e=>{console.error(e);process.exitCode=1;});
