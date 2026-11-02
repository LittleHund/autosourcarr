// The one place the browser tests learn the theme list from: theme.js THEME_ACCENTS (six accents) x dark/light.
// Light/dark is set through `autosourcarr.colorMode` (result on html[data-color-mode]); the placement style stays neutral.
const fs = require('fs');
const path = require('path');

const STATIC = path.join(__dirname, '..', '..', 'autosourcarr', 'app', 'static');
// Same keys app.js uses (UI_PREF_LOCAL_KEYS.theme / .themeStyle).
const STORAGE = {accent: 'autosourcarr.theme', style: 'autosourcarr.themeStyle', mode: 'autosourcarr.colorMode'};
const SCHEMES = [
  {name: 'dark', style: 'neutral', colorScheme: 'dark'},
  {name: 'light', style: 'neutral', colorScheme: 'light'},
];

function accents() {
  const source = fs.readFileSync(path.join(STATIC, 'theme.js'), 'utf8');
  const match = /var\s+THEME_ACCENTS\s*=\s*\[([^\]]*)\]/.exec(source);
  if (!match) throw new Error('THEME_ACCENTS not found in theme.js; update tests/e2e/themes.js');
  const list = [...match[1].matchAll(/'([^']+)'|"([^"]+)"/g)].map(m => m[1] || m[2]);
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

/** What the page shows: accent and light/dark (theme.js sets html[data-color-mode]). */
async function appliedTheme(page) {
  return page.evaluate(() => {
    const root = document.documentElement;
    return {accent: root.dataset.theme, scheme: root.dataset.colorMode || ''};
  });
}

module.exports = {themes, applyTheme, appliedTheme, STORAGE};
