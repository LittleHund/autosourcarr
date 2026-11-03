#!/usr/bin/env node
// PIPE-5: compares this build's page screenshots with the previous release's and lists the pages that changed, so
// unintended changes are seen before release. A report, not a gate (the layout and contrast checks are the gates).
//
//   node scripts/screenshot-diff.mjs PREVIOUS_DIR CURRENT_DIR [--out DIFF_DIR] [--threshold 0.001]
//                                    [--max-changed N] [--summary FILE]
//
// Both folders hold <project>/<page>.png (tests/e2e writes them with E2E_SCREENSHOT_DIR). A missing or empty
// PREVIOUS_DIR means "first run": every page is listed as the new baseline. A page counts as changed when its size
// differs or more than --threshold of its pixels differ by more than 16 in any channel. With --out, a diff image
// (changed pixels in magenta over a faded copy) is written per changed page. No dependencies: PNG is decoded with
// node:zlib (8-bit greyscale/RGB/RGBA, non-interlaced, which is what Chromium writes).
//
// As a release gate (phase 13 audit): --max-changed N exits 4 when more than N pages changed or are gone, so the
// pipeline stops until a person reviewed them; --summary FILE appends a Markdown list of the changed pages (for
// $GITHUB_STEP_SUMMARY). Exit codes: 0 ok (or first baseline), 1 an image could not be read, 2 usage, 4 over the limit.
import {appendFileSync, existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync} from 'node:fs';
import {dirname, join, relative} from 'node:path';
import {deflateSync, inflateSync} from 'node:zlib';

const args = process.argv.slice(2);
const option = name => { const i = args.indexOf(name); return i >= 0 ? args.splice(i, 2)[1] : undefined; };
const outDir = option('--out');
const threshold = Number(option('--threshold') ?? 0.001);
const maxChangedText = option('--max-changed');
const maxChanged = maxChangedText === undefined ? Infinity : Number(maxChangedText);
const summaryFile = option('--summary');
if (!Number.isFinite(threshold) || threshold < 0 || Number.isNaN(maxChanged) || maxChanged < 0) {
  console.error('--threshold and --max-changed must be non-negative numbers'); process.exit(2);
}
const [previousDir, currentDir] = args;
if (!currentDir) { console.error('Usage: node scripts/screenshot-diff.mjs PREVIOUS_DIR CURRENT_DIR [--out DIR] [--threshold 0.001]'); process.exit(2); }

function list(dir) {
  const files = [];
  const walk = folder => { for (const name of readdirSync(folder)) {
    const path = join(folder, name);
    if (statSync(path).isDirectory()) walk(path); else if (name.endsWith('.png')) files.push(relative(dir, path).split('\\').join('/'));
  } };
  if (dir && existsSync(dir)) walk(dir);
  return files.sort();
}

function decode(buffer) {
  if (buffer.readUInt32BE(0) !== 0x89504e47) throw new Error('not a PNG');
  let offset = 8, width = 0, height = 0, depth = 0, type = 0, interlace = 0;
  const data = [];
  while (offset < buffer.length) {
    const length = buffer.readUInt32BE(offset), kind = buffer.toString('ascii', offset + 4, offset + 8);
    const chunk = buffer.subarray(offset + 8, offset + 8 + length);
    if (kind === 'IHDR') { width = chunk.readUInt32BE(0); height = chunk.readUInt32BE(4); depth = chunk[8]; type = chunk[9]; interlace = chunk[12]; }
    else if (kind === 'IDAT') data.push(chunk);
    else if (kind === 'IEND') break;
    offset += 12 + length;
  }
  const channels = {0: 1, 2: 3, 4: 2, 6: 4}[type];
  if (depth !== 8 || !channels || interlace) throw new Error(`unsupported PNG (depth ${depth}, type ${type}, interlace ${interlace})`);
  const raw = inflateSync(Buffer.concat(data)), stride = width * channels, pixels = Buffer.alloc(width * height * 4);
  let previous = Buffer.alloc(stride), position = 0;
  for (let y = 0; y < height; y++) {
    const filter = raw[position++], line = Buffer.from(raw.subarray(position, position + stride));
    position += stride;
    for (let x = 0; x < stride; x++) {
      const a = x >= channels ? line[x - channels] : 0, b = previous[x], c = x >= channels ? previous[x - channels] : 0;
      let add = 0;
      if (filter === 1) add = a; else if (filter === 2) add = b; else if (filter === 3) add = (a + b) >> 1;
      else if (filter === 4) { const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c); add = pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
      line[x] = (line[x] + add) & 0xff;
    }
    for (let x = 0; x < width; x++) {
      const s = x * channels, d = (y * width + x) * 4;
      const grey = channels <= 2;
      pixels[d] = line[s]; pixels[d + 1] = grey ? line[s] : line[s + 1]; pixels[d + 2] = grey ? line[s] : line[s + 2];
      pixels[d + 3] = channels === 4 ? line[s + 3] : channels === 2 ? line[s + 1] : 255;
    }
    previous = line;
  }
  return {width, height, pixels};
}

const crcTable = Array.from({length: 256}, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
const crc = bytes => { let c = 0xffffffff; for (const byte of bytes) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
function encode({width, height, pixels}) {
  const chunk = (kind, body) => { const head = Buffer.alloc(8); head.writeUInt32BE(body.length, 0); head.write(kind, 4, 'ascii');
    const tail = Buffer.alloc(4); tail.writeUInt32BE(crc(Buffer.concat([head.subarray(4), body])), 0); return Buffer.concat([head, body, tail]); };
  const header = Buffer.alloc(13); header.writeUInt32BE(width, 0); header.writeUInt32BE(height, 4); header[8] = 8; header[9] = 6;
  const raw = Buffer.alloc((width * 4 + 1) * height);
  for (let y = 0; y < height; y++) pixels.copy(raw, y * (width * 4 + 1) + 1, y * width * 4, (y + 1) * width * 4);
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk('IHDR', header), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}

const before = new Set(list(previousDir)), after = list(currentDir);
if (!before.size) {
  const line = `screenshot-diff: no previous screenshots; ${after.length} page screenshot(s) form the first baseline.`;
  console.log(line);
  if (summaryFile) appendFileSync(summaryFile, `### Screenshot diff\n\n${line}\n`);
  process.exit(0);
}
const changed = [], added = [], failed = [];
let same = 0;
for (const file of after) {
  if (!before.has(file)) { added.push(file); continue; }
  before.delete(file);
  try {
    const a = decode(readFileSync(join(previousDir, file))), b = decode(readFileSync(join(currentDir, file)));
    if (a.width !== b.width || a.height !== b.height) { changed.push(`${file} (size ${a.width}x${a.height} -> ${b.width}x${b.height})`); continue; }
    let count = 0;
    const mask = outDir ? Buffer.from(b.pixels) : null;
    for (let i = 0; i < a.pixels.length; i += 4) {
      const delta = Math.max(Math.abs(a.pixels[i] - b.pixels[i]), Math.abs(a.pixels[i + 1] - b.pixels[i + 1]), Math.abs(a.pixels[i + 2] - b.pixels[i + 2]));
      if (delta > 16) { count++; if (mask) { mask[i] = 255; mask[i + 1] = 0; mask[i + 2] = 255; mask[i + 3] = 255; } }
      else if (mask) { mask[i] = 128 + (mask[i] >> 1); mask[i + 1] = 128 + (mask[i + 1] >> 1); mask[i + 2] = 128 + (mask[i + 2] >> 1); }
    }
    const share = count / (a.width * a.height);
    if (share > threshold) {
      changed.push(`${file} (${(share * 100).toFixed(2)} % of pixels)`);
      if (mask) { const target = join(outDir, file); mkdirSync(dirname(target), {recursive: true}); writeFileSync(target, encode({...b, pixels: mask})); }
    } else same++;
  } catch (error) { failed.push(`${file}: ${error.message}`); }
}
const removed = [...before];
console.log(`screenshot-diff: ${after.length} page screenshot(s): ${same} unchanged, ${changed.length} changed, ${added.length} new, ${removed.length} gone, ${failed.length} unreadable.`);
for (const [title, items] of [['Changed', changed], ['New', added], ['Gone', removed], ['Unreadable', failed]])
  if (items.length) { console.log(`${title}:`); for (const item of items) console.log(`  ${item}`); }
const over = changed.length + removed.length > maxChanged;
if (summaryFile) {
  const lines = [`### Screenshot diff`, '', `${same} unchanged, ${changed.length} changed, ${added.length} new, ${removed.length} gone, ${failed.length} unreadable`
    + (Number.isFinite(maxChanged) ? ` (limit ${maxChanged} changed or gone${over ? ': **over the limit, review needed**' : ''})` : '') + '.', ''];
  for (const [title, items] of [['Changed', changed], ['Gone', removed], ['New', added], ['Unreadable', failed]])
    if (items.length) { lines.push(`**${title}**`, ''); for (const item of items) lines.push(`- \`${item}\``); lines.push(''); }
  appendFileSync(summaryFile, lines.join('\n') + '\n');
}
if (over) console.log(`screenshot-diff: ${changed.length + removed.length} page(s) changed or gone, more than the limit of ${maxChanged}; review them before releasing.`);
process.exit(failed.length ? 1 : over ? 4 : 0);
