#!/usr/bin/env node
// Phase 13: prepares a freshly booted image instance for the browser tests (tests/e2e), the way a person would on
// first boot: creates the owner from tests/e2e/fixtures/owner.json (test-only credentials for a throwaway instance on
// 127.0.0.1) and finishes the setup wizard without changing a setting (the same request the wizard's Finish sends).
//
//   node scripts/image-e2e-prepare.mjs http://127.0.0.1:18282 [tests/e2e/fixtures/owner.json]
//
// Exit 0 when the owner exists and setup is finished (also when they already were), 1 otherwise.
import {readFileSync} from 'node:fs';
import {randomUUID} from 'node:crypto';

const base = (process.argv[2] || '').replace(/\/$/, '');
const ownerFile = process.argv[3] || 'tests/e2e/fixtures/owner.json';
if (!/^http:\/\/(127\.0\.0\.1|localhost)(:\d+)?$/.test(base)) {
  console.error('Usage: node scripts/image-e2e-prepare.mjs http://127.0.0.1:PORT [owner.json] (loopback test instance only)');
  process.exit(2);
}
const owner = JSON.parse(readFileSync(ownerFile, 'utf8'));
// Same-origin headers: the cross-site write guard accepts them like a browser page of this instance.
const headers = {'Content-Type': 'application/json', Origin: base, 'Sec-Fetch-Site': 'same-origin'};
let cookie = '';
async function call(method, path, body) {
  const response = await fetch(base + path, {method, headers: {...headers, ...(cookie ? {Cookie: cookie} : {})},
    body: body === undefined ? undefined : JSON.stringify(body), redirect: 'manual'});
  const set = response.headers.getSetCookie?.() || [];
  if (set.length) cookie = set.map(line => line.split(';')[0]).join('; ');
  const text = await response.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { json = null; }
  return {status: response.status, json, text};
}

const status = await call('GET', '/api/v1/auth/status');
if (status.status !== 200) { console.error(`auth status: HTTP ${status.status} ${status.text}`); process.exit(1); }
console.log(`auth method ${status.json.method}, setup required ${status.json.setup_required}`);
if (status.json.setup_required) {
  const created = await call('POST', '/api/v1/auth/setup', {username: owner.username, password: owner.password});
  if (created.status !== 201) { console.error(`create owner: HTTP ${created.status} ${created.text}`); process.exit(1); }
  console.log('owner created');
} else {
  const login = await call('POST', '/api/v1/auth/login', {username: owner.username, password: owner.password});
  if (login.status !== 200) { console.error(`owner sign-in: HTTP ${login.status} ${login.text}`); process.exit(1); }
}
const setup = await call('GET', '/api/v1/setup');
if (setup.status !== 200 || !setup.json?.revision) { console.error(`setup read: HTTP ${setup.status} ${setup.text}`); process.exit(1); }
const finished = await call('POST', '/api/v1/setup/finish', {revision: setup.json.revision, transaction: randomUUID(), changes: {}});
if (finished.status !== 200) { console.error(`setup finish: HTTP ${finished.status} ${finished.text}`); process.exit(1); }
console.log('setup finished');
