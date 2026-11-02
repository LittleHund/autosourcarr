// The one place the browser tests learn the theme list from.
// Today the six accents live in app.js (UI_THEMES, minus the free "custom" colour) and light/dark is the
// theme style (neutral = dark, paper = light). Agent T's theme.js (phase 11) keeps the accent in
// `autosourcarr.theme` and light/dark in `autosourcarr.colorMode` (result on html[data-color-mode]); both
// keys are set so the specs work before and after it lands. When theme.js is merged, read its list here
// instead of app.js; the config and specs only use `themes`, `applyTheme` and `appliedTheme`.
const fs = require('fs');
const path = require('path');

const STATIC = path.join(__dirname, '..', '..', 'autosourcarr', 'app', 'static');
// Same keys app.js uses (UI_PREF_LOCAL_KEYS.theme / .themeStyle).
const STORAGE = {accent: 'autosourcarr.theme', style: 'autosourcarr.themeStyle', mode: 'autosourcarr.colorMode'};
const SCHEMES = [
  {name: 'dark', style: 'neutral', colorScheme: 'dark'},
  {name: 'light', style: 'paper', colorScheme: 'light'},
];

function accents() {
  const source = fs.readFileSync(path.join(STATIC, 'app.js'), 'utf8');
  const match = /const\s+UI_THEMES\s*=\s*new Set\(\[([^\]]*)\]\)/.exec(source);
  if (!match) throw new Error('UI_THEMES not found in app.js; update tests/e2e/themes.js');
  const list = [...match[1].matchAll(/'([^']+)'|"([^"]+)"/g)].map(m => m[1] || m[2]).filter(name => name !== 'custom');
  if (list.length !== 6) throw new Error(`Expected six accent themes, found ${list.length}: ${list.join(', ')}`);
  return list;
}

const themes = accents().flatMap(accent => SCHEMES.map(scheme => ({...scheme, scheme: scheme.name, name: `${accent}-${scheme.name}`, accent})));

/** Sets the theme before any page script runs, through the same preference keys the app reads. */
async function applyTheme(context, theme) {
  await context.addInitScript(([keys, value]) => {
    try { localStorage.setItem(keys.accent, value.accent); localStorage.setItem(keys.style, value.style); localStorage.setItem(keys.mode, value.mode); } catch (_) {}
  }, [STORAGE, {accent: theme.accent, style: theme.style, mode: theme.scheme}]);
}

/** What the page shows: accent and light/dark, from theme.js's data-color-mode when present, else the theme style. */
async function appliedTheme(page) {
  return page.evaluate(() => {
    const root = document.documentElement;
    const mode = root.dataset.colorMode || (root.dataset.themeStyle === 'paper' ? 'light' : root.dataset.themeStyle ? 'dark' : '');
    return {accent: root.dataset.theme, scheme: mode};
  });
}

module.exports = {themes, applyTheme, appliedTheme, STORAGE};
