#!/usr/bin/env node
// Short text summary of a Playwright JSON report, for probe/playwright.txt.
// Usage: node scripts/playwright-summary.mjs [test-results/playwright.json]
import {existsSync, readFileSync} from 'node:fs';

const file = process.argv[2] || 'test-results/playwright.json';
if (!existsSync(file)) { console.log(`playwright: no report at ${file} (the run did not start or crashed early)`); process.exit(1); }
const report = JSON.parse(readFileSync(file, 'utf8'));
const s = report.stats || {};
console.log(`playwright ${report.config?.version || ''}: expected=${s.expected ?? 0} unexpected=${s.unexpected ?? 0} flaky=${s.flaky ?? 0} skipped=${s.skipped ?? 0} duration_s=${Math.round((s.duration || 0) / 1000)}`);

const failures = [], perProject = new Map(), notes = new Set();
const walk = (suite, titles) => {
  for (const spec of suite.specs || []) for (const test of spec.tests || []) {
    const project = test.projectName, row = perProject.get(project) || {passed: 0, failed: 0, skipped: 0, flaky: 0};
    const status = test.status; // expected | unexpected | flaky | skipped
    if (status === 'expected') row.passed++; else if (status === 'unexpected') row.failed++; else if (status === 'flaky') row.flaky++; else row.skipped++;
    perProject.set(project, row);
    for (const note of test.annotations || []) if (note.type !== 'skip') notes.add(`${note.type}: ${note.description}`);
    if (status === 'unexpected' || status === 'flaky') {
      const result = (test.results || []).find(r => r.status !== 'passed') || {};
      const message = String(result.error?.message || result.errors?.[0]?.message || '').replace(/\u001b\[[0-9;]*m/g, '').split('\n').filter(Boolean).slice(0, 3).join(' | ');
      failures.push(`${status === 'flaky' ? 'FLAKY' : 'FAIL'} [${project}] ${[...titles, spec.title].join(' > ')}: ${message.slice(0, 600)}`);
    }
  }
  for (const child of suite.suites || []) walk(child, [...titles, child.title].filter(Boolean));
};
for (const suite of report.suites || []) walk(suite, [suite.title].filter(Boolean));

console.log('\nper project (passed/failed/flaky/skipped):');
for (const [project, row] of [...perProject].sort()) console.log(`  ${project}: ${row.passed}/${row.failed}/${row.flaky}/${row.skipped}`);
if (notes.size) { console.log('\nnotes:'); for (const note of notes) console.log('  ' + note); }
if (failures.length) { console.log(`\nfailures (${failures.length}):`); for (const line of failures) console.log('  ' + line); }
process.exit(s.unexpected ? 1 : 0);
