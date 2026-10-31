const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict');
const root=__dirname+'/../app/static/',app=fs.readFileSync(root+'app.js','utf8'),wb=fs.readFileSync(root+'workbench.js','utf8');
const ctx={document:{querySelector:()=>null},console};vm.createContext(ctx);vm.runInContext(wb,ctx);
vm.runInContext(app.slice(app.indexOf('function normalizeHexColor('),app.indexOf('function syncCustomColorInputs(')),ctx);
const plain=x=>JSON.parse(JSON.stringify(x));
const migrated=plain(ctx.migrateColorRoles({theme:'custom',customColor:'#F300E6',secondaryColor:'#5B04DC',themeStyle:'dual'}));
assert.equal(migrated.secondaryColor,'#F300E6');assert.equal(migrated.tertiaryColor,'#5B04DC');assert.equal(migrated.colorRolesVersion,2);
assert.deepEqual(plain(ctx.migrateColorRoles(migrated)),migrated,'Migration must be idempotent');
const saved={theme:'blue',colorRolesVersion:2,secondaryColor:'#123456',tertiaryColor:'#987654'};assert.deepEqual(plain(ctx.migrateColorRoles(saved)),saved);
const pal=(style,a,b,c,strength=25)=>ctx.appearancePalette(style,ctx.hexToRgb(a),ctx.hexToRgb(b),strength,'accent',ctx.hexToRgb(c));
const parse=x=>x.startsWith('#')?ctx.hexToRgb(x):{r:+x.match(/\d+/g)[0],g:+x.match(/\d+/g)[1],b:+x.match(/\d+/g)[2]};
let count=0;
for(const style of ['neutral','tonal','dual','paper','graphite','aurora']){
 const p=pal(style,'#FF0000','#008800','#0000FF'),newPrimary=pal(style,'#FFFF00','#008800','#0000FF'),newSecondary=pal(style,'#FF0000','#880000','#0000FF'),newThird=pal(style,'#FF0000','#008800','#00FFFF');
 assert.equal(p['accent-border'],'rgb(255 0 0)');
 assert.equal(p.card,newPrimary.card,'Primary cannot recolor the card');
 assert.notEqual(p.card,newSecondary.card,'Secondary must control the card');
 assert.equal(p.card,newThird.card,'Third color cannot recolor normal cards');
 if(style==='dual'){
  assert.equal(p['evidence-card'],newSecondary['evidence-card']);
  assert.notEqual(p['evidence-card'],newThird['evidence-card']);
 }else{assert.equal(p['evidence-card'],p.card);assert.equal(p['evidence-card'],newThird['evidence-card']);}
 for(const a of ['#000000','#FFFFFF','#F300E6'])for(const b of ['#000000','#FFFFFF','#008800'])for(const c of ['#000000','#FFFFFF','#5B04DC'])for(const n of [10,50]){
  const q=pal(style,a,b,c,n);
  for(const token of ['text','muted','accent','tertiary','good','warn','bad'])for(const surface of ['card','evidence-card'])assert(ctx.colorContrast(parse(q[token]),parse(q[surface]))>=4.5,`${style} ${token} on ${surface}`);
  count++;
 }
}
console.log('PASS v0.3.2 color-role migration, independent borders/cards/evidence, and '+count+' contrast combinations');
