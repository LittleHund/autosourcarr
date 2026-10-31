// 0.4.5: entries get "Open in WebView". The button targets the primary source record's
// cached page address and falls back to the source when the address is missing.
const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict');
const root=__dirname+'/../app/static/',app=fs.readFileSync(root+'app.js','utf8');
const opened=[];
const ctx={console,openSourceWebview:(url,sourceId)=>opened.push([url,sourceId]),jsq:s=>String(s).replace(/\\/g,'\\\\').replace(/'/g,"\\'")};
ctx.window=ctx;vm.createContext(ctx);
vm.runInContext(app.slice(app.indexOf('function primaryEntrySource('),app.indexOf('async function renderInlineEntry(')),ctx);

const entry={matches:[
 {source_id:'111',real_url:'https://a.example/manga/1',locked:0},
 {source_id:'222',real_url:'https://b.example/title/2',locked:1}]};
assert.equal(ctx.primaryEntrySource(entry).source_id,'222','A manually locked match is the primary source');
assert.equal(ctx.primaryEntrySource({matches:[entry.matches[0]]}).source_id,'111');
assert.equal(ctx.entryWebviewButton({matches:[]}),'','No button without a source record');
const html=ctx.entryWebviewButton(entry);
assert(html.includes('Open in WebView')&&html.includes("openEntrySourceWebview('https://b.example/title/2','222')"),html);

ctx.openEntrySourceWebview('https://b.example/title/2','222');
ctx.openEntrySourceWebview('','111');
assert.deepEqual(opened,[['https://b.example/title/2','222'],['','111']],'Missing address falls back to the source id');

for(const marker of ['${entryWebviewButton(e)}<button class="danger" onclick="deleteEntry(','</span>${entryWebviewButton(e)}</div><div class="entryDescription"'])
 assert(app.includes(marker),'Entry views must render the header button: '+marker);
assert.equal((app.match(/openEntrySourceWebview\('\$\{jsq\(m\.real_url\|\|''\)\}'/g)||[]).length,3,'Both entry views and the header use the cached source address');
console.log('PASS v0.4.5 entries open their primary source in the WebView');
