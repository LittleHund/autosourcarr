/* AutoSourcarr theme module: the single palette table and the code that applies it.
   Six accents x dark/light, every colour role. Light/dark follows the device setting
   unless the user picks a mode. Every built palette is held to WCAG AA:
   text 4.5:1, UI parts (borders, focus ring, fills) 3:1. User colours are adjusted
   until they pass and the adjustment is reported, never applied silently below AA.
   Loaded before app.js; depends on app.js helpers ($, toast, readLocalUiPreference,
   writeLocalUiPreference, queueUiPreferenceSave) only at call time. */
var THEME_AA={text:4.5,ui:3};
var THEME_ACCENTS=['blue','amber','emerald','violet','rose','cyan'];
var THEME_PLACEMENTS=['neutral','tonal','dual','graphite','aurora'];
var DEFAULT_CUSTOM_COLOR='#ff2d78';
var DEFAULT_TERTIARY_COLOR='#EEB392';
var UI_THEMES=new Set([...THEME_ACCENTS,'custom']);
// The palette table. Base roles are shared by every accent; accent roles per accent and mode.
var THEME_TABLE={
  base:{
    dark:{bg:'#1B1F26',panel:'#272C33',panel2:'#22272D',chrome:'#12161D',control:'#2C3138','control-hover':'#363B43',sidebar:'#12161D',toolbar:'#22272D','sidebar-active':'#2C3138',line:'#434851','control-border':'#7B818A',text:'#E5E8ED',muted:'#ADB1B9',good:'#7ACE87',warn:'#FAC053',bad:'#FF958D'},
    light:{bg:'#F4F7FB',panel:'#FFFFFF',panel2:'#EDF0F6',chrome:'#E4E8EF',control:'#F1F4F8','control-hover':'#E1E5EB',sidebar:'#E7EBF2',toolbar:'#EFF2F7','sidebar-active':'#D8DEE9',line:'#CDD1D8','control-border':'#70757C',text:'#171B21',muted:'#4C5057',good:'#13662B',warn:'#7E4F04',bad:'#B02B27'},
  },
  // "Graphite" placement: near-black surfaces, dark mode only (light mode uses neutral).
  graphite:{bg:'#0B0D10',panel:'#14171B',panel2:'#101317',chrome:'#08090B',control:'#1C1F24','control-hover':'#252930',sidebar:'#08090B',toolbar:'#101317','sidebar-active':'#1C1F24',line:'#30343B'},
  accents:{
    blue:{seed:'#7DB0FF',dark:{accent:'#7DB0FF','accent-soft':'#2A394D',header:'#26538A','header-muted':'#D1DFF2'},light:{accent:'#2460A8','accent-soft':'#D9EAFF',header:'#26538A','header-muted':'#D1DFF2'}},
    amber:{seed:'#F1BD55',dark:{accent:'#F1BD55','accent-soft':'#443520',header:'#734800','header-muted':'#EADCC9'},light:{accent:'#8A5200','accent-soft':'#F6E5CF',header:'#734800','header-muted':'#EADCC9'}},
    emerald:{seed:'#61D49A',dark:{accent:'#61D49A','accent-soft':'#273E2F',header:'#0C6338','header-muted':'#CFE4D5'},light:{accent:'#007039','accent-soft':'#D6EFDE',header:'#0C6338','header-muted':'#CFE4D5'}},
    violet:{seed:'#B69AFF',dark:{accent:'#B69AFF','accent-soft':'#39344B',header:'#584389','header-muted':'#DFDBF0'},light:{accent:'#6C50AB','accent-soft':'#E9E4FE',header:'#584389','header-muted':'#DFDBF0'}},
    rose:{seed:'#F08CA5',dark:{accent:'#F08CA5','accent-soft':'#4A2F35',header:'#80364B','header-muted':'#F1D6DB'},light:{accent:'#9F3F5B','accent-soft':'#FEDFE5',header:'#80364B','header-muted':'#F1D6DB'}},
    cyan:{seed:'#62D4DF',dark:{accent:'#62D4DF','accent-soft':'#1C3E42',header:'#006069','header-muted':'#C8E4E7'},light:{accent:'#006B78','accent-soft':'#CEEFF3',header:'#006069','header-muted':'#C8E4E7'}},
  },
};
// Contrast rules checked at runtime (adjustment) and by tests/ui_v050_theme_contrast.js.
var THEME_SURFACE_ROLES=['bg','panel','panel2','chrome','control','control-hover','sidebar','toolbar','sidebar-active','card','card-secondary','evidence-card','accent-soft','secondary-soft'];
var THEME_CONTRAST_RULES=[
  {roles:['text','muted','accent','secondary','tertiary','good','warn','bad'],on:THEME_SURFACE_ROLES,min:THEME_AA.text,kind:'text'},
  {roles:['on-accent'],on:['accent-fill'],min:THEME_AA.text,kind:'text'},
  {roles:['header-text','header-muted'],on:['header'],min:THEME_AA.text,kind:'text'},
  {roles:['control-border'],on:['bg','panel','panel2','chrome','control','card','card-secondary','evidence-card'],min:THEME_AA.ui,kind:'ui'},
  {roles:['focus'],on:['bg','panel','panel2','chrome','control','card','evidence-card'],min:THEME_AA.ui,kind:'ui'},
  {roles:['accent-border'],on:['bg','panel','panel2','control','card','card-secondary','evidence-card'],min:THEME_AA.ui,kind:'ui'},
  {roles:['accent-fill','progress'],on:['bg','panel','panel2','control'],min:THEME_AA.ui,kind:'ui'},
];

function normalizeHexColor(value){
  let raw=String(value||'').trim().replace(/^#/,'');
  if(/^[0-9a-fA-F]{3}$/.test(raw))raw=raw.split('').map(x=>x+x).join('');
  if(!/^[0-9a-fA-F]{6}$/.test(raw))return '';
  return `#${raw.toUpperCase()}`;
}
function hexToRgb(value){const hex=normalizeHexColor(value);if(!hex)return null;return {r:parseInt(hex.slice(1,3),16),g:parseInt(hex.slice(3,5),16),b:parseInt(hex.slice(5,7),16)};}
function rgbToHex(r,g,b){const clamp=v=>Math.max(0,Math.min(255,Number(v)||0));return '#'+[r,g,b].map(v=>Math.round(clamp(v)).toString(16).padStart(2,'0')).join('').toUpperCase();}
function mixRgb(rgb,target,amount){const a=Math.max(0,Math.min(1,Number(amount)||0));return {r:Math.round(rgb.r+(target.r-rgb.r)*a),g:Math.round(rgb.g+(target.g-rgb.g)*a),b:Math.round(rgb.b+(target.b-rgb.b)*a)};}
function rgbCss(rgb){return `rgb(${rgb.r} ${rgb.g} ${rgb.b})`;}
function themeColorRgb(value){if(value&&typeof value==='object')return value;const s=String(value||'');if(s.startsWith('#'))return hexToRgb(s);const n=s.match(/\d+(\.\d+)?/g);return n&&n.length>=3?{r:+n[0],g:+n[1],b:+n[2]}:null;}
function colorLuminance(c){const f=v=>{v/=255;return v<=.04045?v/12.92:((v+.055)/1.055)**2.4;};return .2126*f(c.r)+.7152*f(c.g)+.0722*f(c.b);}
function colorContrast(a,b){const x=colorLuminance(themeColorRgb(a)),y=colorLuminance(themeColorRgb(b));return (Math.max(x,y)+.05)/(Math.min(x,y)+.05);}
// Moves a colour toward white (dark surfaces) or black (light surfaces) until it reaches `min` on every surface.
function readableColor(color,surfaces,light=false,min=THEME_AA.text){
  let safe={...themeColorRgb(color)};const target=light?{r:0,g:0,b:0}:{r:255,g:255,b:255};
  const list=surfaces.map(themeColorRgb);
  for(let i=0;i<100&&list.some(bg=>colorContrast(safe,bg)<min);i++)safe=mixRgb(safe,target,.09);
  return safe;
}
function normalizeThemeStyle(value){return ['neutral','tonal','dual','paper','graphite','aurora'].includes(value)?value:'neutral';}
function normalizeColorMode(value){return value==='dark'||value==='light'?value:'auto';}
function normalizeAccentStrength(value){const n=Number(value);return Number.isFinite(n)&&n>=10&&n<=50?n:25;}
function normalizeCardFill(value){return value==='neutral'?'neutral':'accent';}
// "paper" was a light style before 0.5.0; it now means neutral placement in light mode.
function themePlacement(style,mode){const s=normalizeThemeStyle(style);if(s==='paper')return 'neutral';if(s==='graphite'&&mode==='light')return 'neutral';return s;}
function migrateColorRoles(values){
  if(Number(values.colorRolesVersion)>=2)return values;
  const presets=Object.fromEntries(THEME_ACCENTS.map(n=>[n,THEME_TABLE.accents[n].seed]));
  const primary=values.theme==='custom'?(normalizeHexColor(values.customColor)||'#FF2D78'):(presets[values.theme]||presets.blue);
  return {...values,secondaryColor:primary,tertiaryColor:normalizeHexColor(values.secondaryColor)||DEFAULT_TERTIARY_COLOR,colorRolesVersion:2};
}

// Builds every colour role for one theme. Returns {vars, adjustments, mode, placement}.
function buildThemePalette(options={}){
  const mode=options.mode==='light'?'light':'dark',light=mode==='light';
  const theme=THEME_TABLE.accents[options.theme]?options.theme:(options.theme==='custom'?'custom':'blue');
  const placement=themePlacement(options.style,mode);
  const intensity=normalizeAccentStrength(options.strength)/100,cardFill=normalizeCardFill(options.cardFill);
  const table=theme==='custom'?null:THEME_TABLE.accents[theme][mode];
  const customHex=normalizeHexColor(options.custom)||normalizeHexColor(DEFAULT_CUSTOM_COLOR);
  const seedHex=table?table.accent:customHex;
  const secondHex=normalizeHexColor(options.secondary)||seedHex,thirdHex=normalizeHexColor(options.tertiary)||DEFAULT_TERTIARY_COLOR;
  const seed=hexToRgb(seedHex),second=hexToRgb(secondHex),third=hexToRgb(thirdHex);
  const p={...THEME_TABLE.base[mode]};
  if(placement==='graphite')Object.assign(p,THEME_TABLE.graphite);
  const tint=(base,color,n)=>{const c=mixRgb(hexToRgb(base),color,n);return rgbToHex(c.r,c.g,c.b);};
  // Cards have their own colour role; tint from the neutral base so styles do not compound.
  p.card=cardFill==='accent'?tint(p.panel,second,intensity):p.panel;
  p['card-secondary']=p.card;
  p['evidence-card']=placement==='dual'&&cardFill==='accent'?tint(p.panel,third,intensity):p.card;
  if(placement==='tonal'||placement==='aurora'){
    p.panel=tint(p.panel,second,placement==='tonal'?intensity:intensity*.5);
    p.panel2=tint(p.panel2,second,placement==='tonal'?Math.min(.55,intensity+.05):intensity*.65);
    p.chrome=tint(p.chrome,seed,.08);p.sidebar=tint(p.sidebar,seed,.08);
  }
  p['secondary-soft']=tint(p.panel,second,placement==='dual'?intensity*.65:(light?.06:.10));
  p['accent-soft']=table&&placement!=='tonal'&&placement!=='aurora'?table['accent-soft']:tint(p.panel,seed,light?.12:.12);
  // Surfaces: strong tints must never make text or muted text unreadable.
  const darkInk={r:0,g:0,b:0},whiteInk={r:255,g:255,b:255},surfaceTarget=light?whiteInk:darkInk;
  const text=hexToRgb(p.text),muted=hexToRgb(p.muted);
  for(const key of THEME_SURFACE_ROLES){let s=hexToRgb(p[key]);for(let i=0;i<100&&(colorContrast(text,s)<THEME_AA.text||colorContrast(muted,s)<THEME_AA.text);i++)s=mixRgb(s,surfaceTarget,.08);p[key]=rgbToHex(s.r,s.g,s.b);}
  const surfaces=THEME_SURFACE_ROLES.map(k=>hexToRgb(p[k]));
  const fit=(rgb,list,min)=>{const c=readableColor(rgb,list,light,min);return rgbToHex(c.r,c.g,c.b);};
  const adjustments=[];
  const note=(role,from,to)=>{if(normalizeHexColor(from)!==normalizeHexColor(to))adjustments.push({role,from:normalizeHexColor(from),to,mode});};
  p.accent=fit(seed,surfaces,THEME_AA.text);if(theme==='custom')note('primary',seedHex,p.accent);
  p.secondary=fit(second,surfaces,THEME_AA.text);note('secondary',secondHex,p.secondary);
  p.tertiary=fit(third,surfaces,THEME_AA.text);if(placement==='dual')note('tertiary',thirdHex,p.tertiary);
  for(const k of ['good','warn','bad'])p[k]=fit(hexToRgb(p[k]),surfaces,THEME_AA.text);
  p['accent-text']=p.accent;p.focus=p.accent;p['accent-fill']=p.accent;p.progress=p.accent;
  p['accent-chip']=p['accent-soft'];p['surface-highlight']=p['accent-soft'];
  // Borders keep the exact chosen hue when it already reaches 3:1.
  const borderSurfaces=['bg','panel','panel2','control','card','card-secondary','evidence-card'].map(k=>hexToRgb(p[k]));
  p['accent-border']=fit(seed,borderSurfaces,THEME_AA.ui);if(theme==='custom')note('border',seedHex,p['accent-border']);
  p['control-border']=fit(hexToRgb(p['control-border']),['bg','panel','panel2','chrome','control','card','card-secondary','evidence-card'].map(k=>hexToRgb(p[k])),THEME_AA.ui);
  const accentRgb=hexToRgb(p.accent),ink=light?'#FFFFFF':'#111317';
  p['on-accent']=colorContrast(accentRgb,hexToRgb(ink))>=THEME_AA.text?ink:(colorContrast(accentRgb,darkInk)>=colorContrast(accentRgb,whiteInk)?'#000000':'#FFFFFF');
  p['on-accent-fill']=p['on-accent'];
  if(table){p.header=table.header;p['header-muted']=table['header-muted'];}
  else{
    // The header always carries white text, in both modes: darken the custom hue until it does.
    let h=mixRgb(seed,darkInk,.35);for(let i=0;i<100&&colorContrast(h,whiteInk)<5;i++)h=mixRgb(h,darkInk,.08);
    let hm=mixRgb(h,whiteInk,.8);for(let i=0;i<100&&colorContrast(hm,h)<THEME_AA.text;i++)hm=mixRgb(hm,whiteInk,.2);
    p.header=rgbToHex(h.r,h.g,h.b);p['header-muted']=rgbToHex(hm.r,hm.g,hm.b);
  }
  p['header-text']='#FFFFFF';
  p['header-line']=fit(mixRgb(hexToRgb(p.header),darkInk,.25),[],1);
  return {vars:p,adjustments,mode,placement,theme};
}
// Every rule result for a built palette (used by the contrast test and the Settings notice).
function themeContrastReport(vars){
  const out=[];
  for(const rule of THEME_CONTRAST_RULES)for(const role of rule.roles)for(const surface of rule.on){
    if(!vars[role]||!vars[surface])continue;out.push({role,surface,kind:rule.kind,min:rule.min,ratio:colorContrast(vars[role],vars[surface])});
  }
  return out;
}
// Compatibility wrapper for the 0.3.x signature: (style, accentRgb, secondaryRgb, strength, cardFill, tertiaryRgb[, mode]).
function appearancePalette(style,accent,secondary,strength=25,cardFill='accent',tertiary=secondary,mode){
  const hex=c=>c?rgbToHex(c.r,c.g,c.b):'';
  const m=mode||(normalizeThemeStyle(style)==='paper'?'light':'dark');
  return buildThemePalette({theme:'custom',custom:hex(accent),secondary:hex(secondary),tertiary:hex(tertiary),style,strength,cardFill,mode:m}).vars;
}

function themeLocal(key){try{return typeof readLocalUiPreference==='function'?readLocalUiPreference(key):undefined;}catch(_){return undefined;}}
function deviceColorMode(){try{return typeof matchMedia==='function'&&matchMedia('(prefers-color-scheme: light)').matches?'light':'dark';}catch(_){return 'dark';}}
// Stored mode; before 0.5.0 only the "paper" style was light and "graphite" was explicitly dark.
function storedColorMode(){const raw=themeLocal('colorMode');if(raw)return normalizeColorMode(raw);const style=themeLocal('themeStyle');return style==='paper'?'light':style==='graphite'?'dark':'auto';}
function resolveColorMode(pref){const v=normalizeColorMode(pref);return v==='auto'?deviceColorMode():v;}

function syncCustomColorInputs(hex){const q=s=>typeof document!=='undefined'?document.querySelector(s):null;const rgb=hexToRgb(hex)||hexToRgb(DEFAULT_CUSTOM_COLOR);const normalized=rgbToHex(rgb.r,rgb.g,rgb.b);if(q('#customThemePicker'))q('#customThemePicker').value=normalized;if(q('#customThemeHex'))q('#customThemeHex').value=normalized;if(q('#customThemeR'))q('#customThemeR').value=rgb.r;if(q('#customThemeG'))q('#customThemeG').value=rgb.g;if(q('#customThemeB'))q('#customThemeB').value=rgb.b;if(q('#customThemePreview'))q('#customThemePreview').style.background=normalized;return normalized;}
function clearCustomPalette(){}
function themeAdjustmentText(adjustments){
  if(!adjustments.length)return 'All colours meet WCAG AA contrast (text 4.5:1, borders and controls 3:1).';
  const names={primary:'Primary text',border:'Primary border',secondary:'Secondary text',tertiary:'Tertiary text'};
  return 'Adjusted to keep WCAG AA contrast in '+adjustments[0].mode+' mode: '+adjustments.map(a=>`${names[a.role]||a.role} ${a.from} → ${a.to}`).join(', ')+'.';
}
function applyAppearancePalette(style,theme,custom,secondary){
  style=normalizeThemeStyle(style);const root=document.documentElement;
  const pref=storedColorMode(),mode=resolveColorMode(pref);
  const name=UI_THEMES.has(String(theme||''))?String(theme):'blue';
  const primary=name==='custom'?(normalizeHexColor(custom)||normalizeHexColor(DEFAULT_CUSTOM_COLOR)):THEME_TABLE.accents[name].seed;
  const migrated=Number(themeLocal('colorRolesVersion'))>=2;
  const second=migrated?(normalizeHexColor(secondary)||primary):primary;
  const third=normalizeHexColor(migrated?themeLocal('tertiaryColor'):secondary)||DEFAULT_TERTIARY_COLOR;
  const strength=normalizeAccentStrength(themeLocal('accentStrength')),cardFill=normalizeCardFill(themeLocal('cardFill'));
  const result=buildThemePalette({theme:name,mode,style,custom:primary,secondary:second,tertiary:third,strength,cardFill});
  for(const [key,value] of Object.entries(result.vars))root.style.setProperty('--'+key,value);
  root.style.setProperty('--danger','var(--bad)');root.style.setProperty('--border','var(--line)');
  root.style.colorScheme=mode;root.dataset.colorMode=mode;root.dataset.colorModePreference=pref;
  root.dataset.themeStyle=result.placement;root.dataset.riLight=String(mode==='light');root.dataset.cardFill=cardFill;
  const q=s=>document.querySelector(s);
  const fillControl=q('#cardFill');if(fillControl)fillControl.value=cardFill;
  const slider=q('#accentStrength'),label=q('#accentStrengthValue');if(slider)slider.value=String(strength);if(label)label.textContent=strength+'%';
  document.querySelectorAll('.styleChoice[data-theme-style]').forEach(b=>{const on=b.dataset.themeStyle===result.placement;b.classList.toggle('active',on);b.setAttribute('aria-checked',String(on));});
  document.querySelectorAll('.colorModeChoice[data-color-mode]').forEach(b=>{const on=b.dataset.colorMode===pref;b.classList.toggle('active',on);b.setAttribute('aria-checked',String(on));});
  const deviceNote=q('#colorModeDeviceNote');if(deviceNote)deviceNote.textContent=pref==='auto'?`Following this device: ${mode}.`:`Always ${mode}.`;
  const toggle=q('#riThemeToggle');if(toggle){const next=mode==='light'?'dark':'light';toggle.setAttribute('aria-label',`Switch to ${next} mode`);toggle.title=`Switch to ${next} mode`;}
  const panel=q('#secondaryThemeControls');if(panel)panel.hidden=false;
  const thirdPanel=q('#tertiaryThemeControls');if(thirdPanel)thirdPanel.hidden=result.placement!=='dual';
  for(const id of ['#tertiaryThemePicker','#tertiaryThemeHex']){const el=q(id);if(el)el.value=third;}
  for(const id of ['#secondaryThemePicker','#secondaryThemeHex']){const el=q(id);if(el)el.value=second;}
  const contrastNote=q('#themeContrastNote');if(contrastNote){contrastNote.textContent=themeAdjustmentText(result.adjustments);contrastNote.dataset.adjusted=String(result.adjustments.length>0);}
  return result;
}
function applyTheme(theme,customColor){
  const name=UI_THEMES.has(String(theme||''))?String(theme):'blue';
  document.documentElement.dataset.theme=name;writeLocalUiPreference('theme',name);
  const hex=normalizeHexColor(customColor||themeLocal('customColor'))||normalizeHexColor(DEFAULT_CUSTOM_COLOR);
  const result=applyAppearancePalette(themeLocal('themeStyle')||'neutral',name,hex,themeLocal('secondaryColor'));
  document.querySelectorAll('.themeChoice').forEach(b=>{const active=b.dataset.theme===name;b.classList.toggle('active',active);b.setAttribute('aria-checked',String(active));});
  const customPanel=document.querySelector('#customThemeControls');if(customPanel)customPanel.hidden=false;
  syncCustomColorInputs(name==='custom'?hex:THEME_TABLE.accents[name].seed);
  const preview=document.querySelector('#customThemePreview');if(preview)preview.style.background=hex;
  return result;
}
function applyCustomPalette(value){
  const hex=syncCustomColorInputs(normalizeHexColor(value)||DEFAULT_CUSTOM_COLOR);
  writeLocalUiPreference('customColor',hex);
  applyAppearancePalette(themeLocal('themeStyle')||'neutral','custom',hex,themeLocal('secondaryColor'));
  return hex;
}
function applyThemeStyle(value,save=false){
  const name=normalizeThemeStyle(value);writeLocalUiPreference('themeStyle',name);
  applyTheme(themeLocal('theme')||'blue',themeLocal('customColor'));
  if(save)queueUiPreferenceSave('themeStyle',name);
}
// Mode: auto (device), dark or light. Picking a mode retires the legacy "paper" style.
function applyColorMode(value,save=false){
  const mode=normalizeColorMode(value);
  if(themeLocal('themeStyle')==='paper'){writeLocalUiPreference('themeStyle','neutral');if(save)queueUiPreferenceSave('themeStyle','neutral');}
  writeLocalUiPreference('colorMode',mode);
  applyTheme(themeLocal('theme')||'blue',themeLocal('customColor'));
  if(save)queueUiPreferenceSave('colorMode',mode);
  return mode;
}
function commitCustomThemeColor(hex){
  const normalized=normalizeHexColor(hex);if(!normalized){toast('Enter a valid HEX color such as #FF2D78');return false;}
  applyCustomPalette(normalized);const result=applyTheme('custom',normalized);
  queueUiPreferenceSave('customColor',normalized);queueUiPreferenceSave('theme','custom');
  const changed=(result?.adjustments||[]).filter(a=>a.role==='primary'||a.role==='border');
  if(changed.length&&typeof toast==='function')toast(themeAdjustmentText(changed));
  return true;
}
function ensureColorRoles(){
  if(Number(themeLocal('colorRolesVersion'))>=2)return;
  const colors=migrateColorRoles({theme:themeLocal('theme'),customColor:themeLocal('customColor'),secondaryColor:themeLocal('secondaryColor')});
  for(const key of ['secondaryColor','tertiaryColor','colorRolesVersion']){writeLocalUiPreference(key,colors[key]);queueUiPreferenceSave(key,colors[key]);}
}
function commitSecondaryColor(value){
  const hex=normalizeHexColor(value);if(!hex){toast('Enter a valid secondary HEX color.');return;}
  ensureColorRoles();writeLocalUiPreference('secondaryColor',hex);const result=applyTheme(themeLocal('theme'),themeLocal('customColor'));queueUiPreferenceSave('secondaryColor',hex);
  const changed=(result?.adjustments||[]).filter(a=>a.role==='secondary');if(changed.length&&typeof toast==='function')toast(themeAdjustmentText(changed));
}
function commitTertiaryColor(value){
  const hex=normalizeHexColor(value);if(!hex){toast('Enter a valid tertiary HEX color.');return;}
  ensureColorRoles();writeLocalUiPreference('tertiaryColor',hex);const result=applyTheme(themeLocal('theme'),themeLocal('customColor'));queueUiPreferenceSave('tertiaryColor',hex);
  const changed=(result?.adjustments||[]).filter(a=>a.role==='tertiary');if(changed.length&&typeof toast==='function')toast(themeAdjustmentText(changed));
}
// Appearance controls owned by this module (called once from initializeWorkbench).
function bindThemeControls(){
  const q=s=>document.querySelector(s);
  for(const id of ['#tertiaryThemePicker','#tertiaryThemeHex'])q(id)?.addEventListener(id.endsWith('Picker')?'input':'change',event=>commitTertiaryColor(event.target.value));
  q('#accentStrength')?.addEventListener('input',event=>{const n=normalizeAccentStrength(event.target.value);writeLocalUiPreference('accentStrength',n);applyTheme(themeLocal('theme'),themeLocal('customColor'));queueUiPreferenceSave('accentStrength',n);});
  q('#cardFill')?.addEventListener('change',event=>{const value=normalizeCardFill(event.target.value);writeLocalUiPreference('cardFill',value);applyTheme(themeLocal('theme'),themeLocal('customColor'));queueUiPreferenceSave('cardFill',value);});
  document.querySelectorAll('.styleChoice[data-theme-style]').forEach(b=>b.addEventListener('click',()=>applyThemeStyle(b.dataset.themeStyle,true)));
  document.querySelectorAll('.colorModeChoice[data-color-mode]').forEach(b=>b.addEventListener('click',()=>applyColorMode(b.dataset.colorMode,true)));
  q('#customThemePicker')?.addEventListener('input',event=>commitCustomThemeColor(event.target.value));
  q('#secondaryThemePicker')?.addEventListener('input',event=>commitSecondaryColor(event.target.value));
  q('#secondaryThemeHex')?.addEventListener('change',event=>commitSecondaryColor(event.target.value));
}
// Before app.js runs: set the resolved mode so the CSS fallback palette paints the right
// mode on first frame, and follow later device changes while the preference is "auto".
(function themeBootstrap(){
  if(typeof document==='undefined'||!document.documentElement||!document.documentElement.dataset)return;
  let pref='auto';
  try{const raw=localStorage.getItem('autosourcarr.colorMode');const style=localStorage.getItem('autosourcarr.themeStyle');pref=raw?normalizeColorMode(raw):style==='paper'?'light':style==='graphite'?'dark':'auto';
    const theme=localStorage.getItem('autosourcarr.theme');if(UI_THEMES.has(String(theme||'')))document.documentElement.dataset.theme=theme;}catch(_){}
  const mode=resolveColorMode(pref);document.documentElement.dataset.colorMode=mode;document.documentElement.dataset.riLight=String(mode==='light');document.documentElement.style.colorScheme=mode;
  try{const media=matchMedia('(prefers-color-scheme: light)');const follow=()=>{if(storedColorMode()==='auto'&&typeof writeLocalUiPreference==='function')applyTheme(themeLocal('theme')||'blue',themeLocal('customColor'));};
    if(media.addEventListener)media.addEventListener('change',follow);else if(media.addListener)media.addListener(follow);}catch(_){}
})();
if(typeof module!=='undefined'&&module.exports)module.exports={THEME_TABLE,THEME_ACCENTS,THEME_PLACEMENTS,THEME_CONTRAST_RULES,THEME_SURFACE_ROLES,THEME_AA,buildThemePalette,themeContrastReport,appearancePalette,colorContrast,migrateColorRoles,normalizeColorMode,readableColor,hexToRgb};
