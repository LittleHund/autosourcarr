// WCAG AA contrast for every theme x mode x colour-role pair of the shipped palette table (theme.js),
// every derived placement/strength/card/custom-colour combination, and the CSS fallback palette.
// Text 4.5:1; control border, focus ring, borders and fills 3:1. Prints a table; exits 1 on any failure.
const fs=require('fs'),path=require('path'),vm=require('vm'),assert=require('node:assert/strict');
const root=path.join(__dirname,'../app/static');
const ctx={console};vm.createContext(ctx);vm.runInContext(fs.readFileSync(path.join(root,'theme.js'),'utf8'),ctx,{filename:'theme.js'});
const {THEME_TABLE,THEME_ACCENTS,THEME_PLACEMENTS,THEME_CONTRAST_RULES}=ctx;
const failures=[],minByRole={};let checked=0;
function check(label,vars){
  for(const r of ctx.themeContrastReport(vars)){
    checked++;const key=r.role+' / '+r.kind;
    if(!minByRole[key]||r.ratio<minByRole[key].ratio)minByRole[key]={ratio:r.ratio,where:label+' on '+r.surface};
    if(r.ratio<r.min-1e-9)failures.push(`${label}: ${r.role} ${vars[r.role]} on ${r.surface} ${vars[r.surface]} = ${r.ratio.toFixed(2)} < ${r.min}`);
  }
}
// Every rule role must exist in every built palette (no silently skipped pair).
const required=new Set(THEME_CONTRAST_RULES.flatMap(r=>[...r.roles,...r.on]));

// 1. The table itself: base + accent roles must pass without any runtime adjustment.
const rows=[];
for(const theme of THEME_ACCENTS)for(const mode of ['dark','light']){
  const built=ctx.buildThemePalette({theme,mode,style:'neutral',cardFill:'neutral'}),v=built.vars;
  for(const k of required)assert(v[k],`${theme} ${mode} is missing role ${k}`);
  for(const [k,val] of Object.entries(THEME_TABLE.base[mode]))assert.equal(v[k],val,`${theme} ${mode} ${k}: table value must pass AA unadjusted`);
  for(const k of ['accent','accent-soft','header','header-muted'])assert.equal(v[k],THEME_TABLE.accents[theme][mode][k],`${theme} ${mode} ${k}: table value must pass AA unadjusted`);
  assert.equal(v['accent-border'],THEME_TABLE.accents[theme][mode].accent);
  check(`${theme}/${mode}`,v);
  const rep=ctx.themeContrastReport(v),min=(roles,kind)=>Math.min(...rep.filter(r=>roles.includes(r.role)&&(!kind||r.kind===kind)).map(r=>r.ratio));
  rows.push({theme:`${theme}/${mode}`,text:min(['text']),muted:min(['muted']),accent:min(['accent']),status:min(['good','warn','bad']),'on-accent':min(['on-accent']),header:min(['header-text','header-muted']),'control-border':min(['control-border']),focus:min(['focus']),'accent-border':min(['accent-border']),fill:min(['accent-fill','progress'])});
}
// 2. Default appearance (cards tinted with the accent) and every derived combination.
const userColors=['#000000','#FFFFFF','#808080','#0000FF','#FFFF00','#FF2D78','#EEB392','#00FF00'];
let combos=0;
for(const mode of ['dark','light'])for(const theme of [...THEME_ACCENTS,'custom'])for(const style of [...THEME_PLACEMENTS,'paper'])for(const strength of [10,25,50])for(const cardFill of ['accent','neutral']){
  const customs=theme==='custom'?userColors:[undefined];
  for(const custom of customs)for(const secondary of [undefined,'#000000','#FFFFFF','#EEB392'])for(const tertiary of [undefined,'#000000','#FFFFFF']){
    const v=ctx.buildThemePalette({theme,mode,style,strength,cardFill,custom,secondary,tertiary}).vars;
    for(const k of required)assert(v[k],`missing ${k}`);
    check(`${theme}/${mode}/${style}/${strength}/${cardFill}/${custom||'-'}/${secondary||'-'}/${tertiary||'-'}`,v);combos++;
  }
}
// 3. A user colour below AA is adjusted and reported, never applied silently.
for(const mode of ['dark','light']){
  const bad=mode==='dark'?'#000000':'#FFFFFF',r=ctx.buildThemePalette({theme:'custom',mode,custom:bad});
  assert(r.adjustments.some(a=>a.role==='primary'&&a.from===bad&&a.to===r.vars.accent),`${mode}: unreadable custom primary must be reported as adjusted`);
  assert.notEqual(r.vars.accent,bad);
  const ok=ctx.buildThemePalette({theme:'custom',mode,cardFill:'neutral',custom:mode==='dark'?'#7DB0FF':'#2460A8'});
  assert(!ok.adjustments.some(a=>a.role==='primary'),`${mode}: a passing custom primary is kept as chosen`);
}
// 4. Legacy preference values map to the new ones.
assert.equal(ctx.normalizeColorMode('paper'),'auto');assert.equal(ctx.normalizeColorMode('light'),'light');
assert.equal(ctx.themePlacement('paper','light'),'neutral');assert.equal(ctx.themePlacement('graphite','light'),'neutral');assert.equal(ctx.themePlacement('graphite','dark'),'graphite');

// 5. CSS fallback palette (first paint, no JS) equals the table and passes too.
const style=fs.readFileSync(path.join(root,'style.css'),'utf8'),workbench=fs.readFileSync(path.join(root,'workbench.css'),'utf8');
const declarations=block=>Object.fromEntries([...block.matchAll(/--([a-z0-9-]+):\s*(#[0-9a-fA-F]{6})/g)].map(m=>[m[1],m[2].toUpperCase()]));
const darkBlock=style.match(/\/\* theme fallback: dark \*\/\s*:root\{([^}]*)\}/),lightBlock=style.match(/:root\[data-color-mode=light\]\{([^}]*)\}/),mediaBlock=style.match(/@media \(prefers-color-scheme:light\)\{:root:not\(\[data-color-mode=dark\]\)\{([^}]*)\}\}/);
assert(darkBlock&&lightBlock&&mediaBlock,'style.css must start with the theme fallback blocks');
assert.equal(lightBlock[1],mediaBlock[1],'device-light and explicit-light fallbacks must be identical');
for(const [mode,block] of [['dark',darkBlock[1]],['light',lightBlock[1]]]){
  const expected=ctx.buildThemePalette({theme:'blue',mode,cardFill:'neutral'}).vars,css=declarations(block);
  for(const [k,v] of Object.entries(expected))if(/^#/.test(v))assert.equal(css[k],v.toUpperCase(),`CSS fallback ${mode} --${k}`);
  check(`css-fallback/${mode}`,css);
}
for(const theme of THEME_ACCENTS)for(const mode of ['dark','light']){
  const sel=mode==='dark'?`\\[data-theme=${theme}\\]`:`\\[data-color-mode=light\\]\\[data-theme=${theme}\\]`;
  const m=workbench.match(new RegExp(`(?:^|[}\\s])${sel}\\{([^}]*)\\}`));assert(m,`workbench.css lacks the ${theme} ${mode} accent fallback`);
  const css=declarations(m[1]),table=THEME_TABLE.accents[theme][mode];
  for(const k of ['accent','accent-soft','header','header-muted'])assert.equal(css[k],table[k],`${theme} ${mode} CSS --${k}`);
}

const pad=(s,n)=>String(s).padEnd(n);
const cols=Object.keys(rows[0]);
console.log('Theme contrast (minimum ratio per role over its surfaces; AA: text 4.5, UI parts 3.0)');
console.log(cols.map((c,i)=>pad(c,i?14:14)).join(''));
for(const r of rows)console.log(cols.map(c=>pad(typeof r[c]==='number'?r[c].toFixed(2):r[c],14)).join(''));
console.log('\nMinimum per role over all '+combos+' derived combinations, the table and the CSS fallback:');
for(const [k,v] of Object.entries(minByRole).sort())console.log('  '+pad(k,26)+v.ratio.toFixed(2)+'  ('+v.where+')');
if(failures.length){console.error('\nFAIL '+failures.length+' pairs below WCAG AA:\n'+failures.slice(0,40).join('\n'));process.exit(1);}
console.log(`\nPASS WCAG AA: ${checked} pairs in ${rows.length} themes, ${combos} combinations and the CSS fallback`);
