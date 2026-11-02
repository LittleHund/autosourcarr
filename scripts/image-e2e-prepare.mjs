#!/usr/bin/env node
// Phase 13: prepares a freshly booted image instance for the browser tests (tests/e2e), the way a person would on
// first boot: creates the owner from tests/e2e/fixtures/owner.json (test-only credentials for a throwaway instance on
// 127.0.0.1) and finishes the setup wizard without changing a setting (the same request the wizard's Finish sends).
//
//   node scripts/image-e2e-prepare.mjs http://127.0.0.1:18282 [owner.json] [--health FILE] [--wait-webview SECONDS]
//
// --health FILE: afterwards, signed in, reads /api/v1/health, waits (default 180 s) until the source host's WebView
// (JCEF) reached a final state, writes the JSON to FILE and prints source_sandbox=, repair_isolation= and webview=
// lines for scripts/image-boot-check.sh.
//
// The app exits with code 75 when it must restart itself (setup restart, armed restore); the container's restart
// policy brings it back. Every step here therefore waits for /api/v1/live again after setup.
// Exit 0 when the owner exists and setup is finished (also when they already were), 1 otherwise.
import {readFileSync, writeFileSync} from 'node:fs';
import {randomUUID} from 'node:crypto';

const args = process.argv.slice(2);
const option = name => { const i = args.indexOf(name); return i >= 0 ? args.splice(i, 2)[1] : undefined; };
const healthFile = option('--health');
const waitWebView = Number(option('--wait-webview') ?? 180);
const base = (args[0] || '').replace(/\/$/, '');
const ownerFile = args[1] || 'tests/e2e/fixtures/owner.json';
if (!/^http:\/\/(127\.0\.0\.1|localhost)(:\d+)?$/.test(base)) {
  console.error('Usage: node scripts/image-e2e-prepare.mjs http://127.0.0.1:PORT [owner.json] [--health FILE] (loopback test instance only)');
  process.exit(2);
}
const owner = JSON.parse(readFileSync(ownerFile, 'utf8'));
// Same-origin headers: the cross-site write guard accepts them like a browser page of this instance.
const headers = {'Content-Type': 'application/json', Origin: base, 'Sec-Fetch-Site': 'same-origin'};
let cookie = '';
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function call(method, path, body) {
  const response = await fetch(base + path, {method, headers: {...headers, ...(cookie ? {Cookie: cookie} : {})},
    body: body === undefined ? undefined : JSON.stringify(body), redirect: 'manual', signal: AbortSignal.timeout(30_000)});
  const set = response.headers.getSetCookie?.() || [];
  if (set.length) cookie = set.map(line => line.split(';')[0]).join('; ');
  const text = await response.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { json = null; }
  return {status: response.status, json, text};
}
async function waitLive(seconds) {
  for (let i = 0; i < seconds; i++) {
    try { if ((await call('GET', '/api/v1/live')).status === 200) return true; } catch { /* restarting */ }
    await sleep(1000);
  }
  return false;
}
async function signIn() {
  const status = await call('GET', '/api/v1/auth/status');
  if (status.status !== 200) throw new Error(`auth status: HTTP ${status.status} ${status.text}`);
  if (status.json.setup_required) {
    const created = await call('POST', '/api/v1/auth/setup', {username: owner.username, password: owner.password});
    if (created.status !== 201) throw new Error(`create owner: HTTP ${created.status} ${created.text}`);
    console.log(`auth method ${status.json.method}: owner created`);
  } else {
    const login = await call('POST', '/api/v1/auth/login', {username: owner.username, password: owner.password});
    if (login.status !== 200) throw new Error(`owner sign-in: HTTP ${login.status} ${login.text}`);
  }
}

try {
  if (!await waitLive(300)) throw new Error('the instance is not live');
  await signIn();
  const setup = await call('GET', '/api/v1/setup');
  if (setup.status !== 200 || !setup.json?.revision) throw new Error(`setup read: HTTP ${setup.status} ${setup.text}`);
  const finished = await call('POST', '/api/v1/setup/finish', {revision: setup.json.revision, transaction: randomUUID(), changes: {}});
  if (finished.status !== 200) throw new Error(`setup finish: HTTP ${finished.status} ${finished.text}`);
  console.log(`setup finished${finished.json?.restart_required ? ' (restart required)' : ''}`);
  // A setup restart (exit 75) may follow; wait until the instance answers again and sign in anew if needed.
  await sleep(3000);
  if (!await waitLive(300)) throw new Error('the instance did not come back after setup');
  if ((await call('GET', '/api/v1/auth/status')).json?.signed_in !== true) await signIn();

  if (healthFile) {
    let health = null, webview = 'unknown';
    const final = state => state === 'ready' || state === 'disabled' || state.startsWith('failed');
    for (let waited = 0; waited <= waitWebView; waited += 5) {
      const read = await call('GET', '/api/v1/health').catch(() => null);
      if (read?.status === 200 && read.json) {
        health = read.json;
        webview = String(health.source_host?.detail?.webview ?? 'unknown');
        if (final(webview)) break;
      }
      await sleep(5000);
    }
    if (!health) throw new Error('/api/v1/health did not answer');
    writeFileSync(healthFile, JSON.stringify(health, null, 2));
    const line = (name, value) => console.log(`${name}=${value == null ? 'missing' : value.state ?? 'unknown'}${value?.reason ? ` (${value.reason})` : ''}`);
    line('source_sandbox', health.source_sandbox);
    console.log(`source_sandbox_detail=${JSON.stringify(health.source_sandbox ?? null)}`);
    line('repair_isolation', health.repair_isolation);
    console.log(`repair_isolation_detail=${JSON.stringify(health.repair_isolation ?? null)}`);
    console.log(`webview=${webview}`);
  }
} catch (error) {
  console.error(String(error.message || error));
  process.exit(1);
}
