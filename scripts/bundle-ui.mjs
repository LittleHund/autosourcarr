#!/usr/bin/env node
// Bundles and minifies the Web UI for the image (phase 11, UI build in CI).
//
//   node scripts/bundle-ui.mjs            writes autosourcarr/app/static/dist/
//   node scripts/bundle-ui.mjs --check    builds into a temporary folder and fails on any problem
//   node scripts/bundle-ui.mjs --out DIR  writes the bundle somewhere else
//
// Output: app.<hash>.js (every classic <script src="/static/..."> of index.html, in page order),
// ui.<hash>.css (ui.css), index.html (the same page pointing at both, ?v= kept) and manifest.json.
// Sources stay as they are: development and the node UI tests keep loading the separate files.
// The image (phase 13) serves the bundle by copying dist/index.html over static/index.html.
//
// The scripts are classic scripts that share one global scope (inline onclick handlers call their
// functions), so they are concatenated, not wrapped, and minified without renaming top-level names.
import {createHash} from 'node:crypto';
import {existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {dirname, join, relative, resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
import {transform} from 'esbuild';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const staticDir = join(root, 'autosourcarr', 'app', 'static');
const args = process.argv.slice(2);
const check = args.includes('--check');
const outIndex = args.indexOf('--out');
if (args.some((arg, i) => !['--check', '--out'].includes(arg) && args[i - 1] !== '--out') || (outIndex >= 0 && !args[outIndex + 1])) {
  console.error('Usage: node scripts/bundle-ui.mjs [--check] [--out DIR]');
  process.exit(2);
}
const target = ['es2022', 'chrome109', 'firefox115', 'safari16'];
const problems = [];
const fail = message => problems.push(message);

const staticPath = url => join(staticDir, ...url.replace(/^\/static\//, '').split('/'));
const html = readFileSync(join(staticDir, 'index.html'), 'utf8');
const scriptTag = /<script\b([^>]*)><\/script>/g;
const scripts = [];
for (const match of html.matchAll(scriptTag)) {
  const src = /\bsrc="([^"]+)"/.exec(match[1])?.[1];
  if (!src) { fail('index.html has an inline or src-less <script>; the bundle cannot keep its position.'); continue; }
  if (/\btype="module"|\basync\b|\bdefer\b/.test(match[1])) fail(`${src}: module/async/defer scripts are not bundled.`);
  const [path, query = ''] = src.split('?');
  if (!path.startsWith('/static/')) { fail(`${src}: only /static/ scripts can be bundled.`); continue; }
  scripts.push({src, path, version: new URLSearchParams(query).get('v') || ''});
}
const style = /<link\b[^>]*\bhref="(\/static\/ui\.css(?:\?[^"]*)?)"[^>]*\brel="stylesheet"[^>]*\/?>|<link\b[^>]*\brel="stylesheet"[^>]*\bhref="(\/static\/ui\.css(?:\?[^"]*)?)"[^>]*\/?>/.exec(html);
if (!style) fail('index.html does not link /static/ui.css.');
if (!scripts.length) fail('index.html has no scripts.');
const versions = new Set(scripts.map(item => item.version));
if (versions.size !== 1 || !/^\d+\.\d+\.\d+$/.test([...versions][0] || '')) fail(`index.html scripts must share one ?v=X.Y.Z (found ${[...versions].join(', ') || 'none'}).`);
const version = [...versions][0] || '';

// Every other /static/ file the page names (icons and the like) must exist too.
for (const match of html.matchAll(/(?:src|href)="(\/static\/[^"?#]+)/g))
  if (!existsSync(staticPath(match[1]))) fail(`index.html references a missing file: ${match[1]}`);

// A directive prologue would make the whole concatenated bundle strict (or lose it): refuse it.
const leadingDirective = source => /^(?:\s|\/\/[^\n]*\n|\/\*[\s\S]*?\*\/)*(['"])use strict\1/.test(source);

const pieces = [];
for (const item of scripts) {
  const file = staticPath(item.path);
  if (!existsSync(file)) { fail(`missing script ${item.path}`); continue; }
  const source = readFileSync(file, 'utf8');
  if (leadingDirective(source)) fail(`${item.path} starts with 'use strict'; move it inside the file's function.`);
  try {
    const result = await transform(source, {loader: 'js', minify: true, target, legalComments: 'none', sourcefile: item.path, charset: 'utf8'});
    pieces.push(`/* ${item.path} */\n${result.code.trim()}\n`);
  } catch (error) { fail(`${item.path}: ${error.message.split('\n')[0]}`); }
}

let css = '';
if (style) {
  const href = (style[1] || style[2]).split('?')[0];
  const file = staticPath(href);
  if (!existsSync(file)) fail(`missing stylesheet ${href}`);
  else {
    const source = readFileSync(file, 'utf8');
    for (const match of source.matchAll(/url\(\s*(['"]?)([^'")]+)\1\s*\)/g)) {
      const url = match[2];
      if (/^(data:|#)/.test(url)) continue;
      if (!url.startsWith('/static/')) fail(`ui.css url(${url}) must be an absolute /static/ path so it resolves from dist/.`);
      else if (!existsSync(staticPath(url.split(/[?#]/)[0]))) fail(`ui.css references a missing file: ${url}`);
    }
    try {
      css = (await transform(source, {loader: 'css', minify: true, target, legalComments: 'none', sourcefile: href, charset: 'utf8'})).code;
    } catch (error) { fail(`${href}: ${error.message.split('\n')[0]}`); }
  }
}

if (problems.length) {
  for (const problem of problems) console.error('bundle-ui: ' + problem);
  process.exit(1);
}

const js = `/* AutoSourcarr ${version} UI bundle: ${scripts.length} scripts in index.html order. Generated by scripts/bundle-ui.mjs. */\n${pieces.join(';\n')}`;
const hash = text => createHash('sha256').update(text).digest('hex').slice(0, 12);
const jsName = `app.${hash(js)}.js`, cssName = `ui.${hash(css)}.css`;

// The bundle takes the place of the first script; the other script tags go away.
let first = true;
let page = html.replace(scriptTag, () => {
  if (!first) return '';
  first = false;
  return `<script src="/static/dist/${jsName}?v=${version}"></script>`;
});
page = page.replace(style[0], `<link href="/static/dist/${cssName}?v=${version}" rel="stylesheet"/>`);
page = page.replace(/\n{2,}(?=<\/body>)/, '\n');

const out = check ? mkdtempSync(join(tmpdir(), 'autosourcarr-ui-')) : resolve(outIndex >= 0 ? args[outIndex + 1] : join(staticDir, 'dist'));
try {
  if (!check) rmSync(out, {recursive: true, force: true});
  mkdirSync(out, {recursive: true});
  writeFileSync(join(out, jsName), js);
  writeFileSync(join(out, cssName), css);
  writeFileSync(join(out, 'index.html'), page);
  const manifest = {version, js: jsName, css: cssName, scripts: scripts.map(item => item.path), stylesheet: '/static/ui.css'};
  writeFileSync(join(out, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');

  // Self-check of what was written: the page names exactly the two bundle files and nothing unbundled.
  const written = readFileSync(join(out, 'index.html'), 'utf8');
  const named = [...written.matchAll(/<script\b[^>]*\bsrc="([^"]+)"/g)].map(m => m[1]);
  if (named.length !== 1 || named[0] !== `/static/dist/${jsName}?v=${version}`) throw new Error('dist/index.html must load exactly the bundle script.');
  if (!written.includes(`/static/dist/${cssName}?v=${version}`) || written.includes('/static/ui.css')) throw new Error('dist/index.html must load exactly the bundled stylesheet.');
  const sourceBytes = scripts.reduce((sum, item) => sum + readFileSync(staticPath(item.path)).length, 0);
  const kb = bytes => (bytes / 1024).toFixed(0) + ' KiB';
  console.log(`bundle-ui: ${scripts.length} scripts ${kb(sourceBytes)} -> ${jsName} ${kb(Buffer.byteLength(js))}; ui.css -> ${cssName} ${kb(Buffer.byteLength(css))}`
    + (check ? ' (check only, nothing written to the repo)' : ` in ${relative(root, out) || out}`));
} catch (error) {
  console.error('bundle-ui: ' + error.message);
  process.exitCode = 1;
} finally {
  if (check) rmSync(out, {recursive: true, force: true});
}
