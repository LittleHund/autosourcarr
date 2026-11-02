// Phase 11 browser tests: every page at phone, tablet and desktop size, in every theme.
// Runs in the ci-probe workflow (docs/handoff/briefs/phase11-probe-steps.md); browsers are installed there.
// The app is the controlled external-mode runtime served by the C# test host (UiTestHost, --serve-ui).
// Filters: E2E_VIEWPORTS=phone,tablet  E2E_THEMES=blue-dark,rose-light  E2E_BUNDLE=1 (serve dist/index.html).
const os = require('os');
const path = require('path');
const {defineConfig} = require('@playwright/test');
const {themes} = require('./tests/e2e/themes');

const port = Number(process.env.E2E_PORT || 18282);
const viewports = [
  {name: 'phone', viewport: {width: 375, height: 812}, isMobile: true, hasTouch: true, deviceScaleFactor: 2},
  {name: 'tablet', viewport: {width: 768, height: 1024}, isMobile: true, hasTouch: true, deviceScaleFactor: 2},
  {name: 'desktop', viewport: {width: 1440, height: 900}, isMobile: false, hasTouch: false, deviceScaleFactor: 1},
];
const pick = (list, variable) => {
  const wanted = (process.env[variable] || '').split(',').map(s => s.trim()).filter(Boolean);
  return wanted.length ? list.filter(item => wanted.includes(item.name)) : list;
};
const testHost = process.env.E2E_TEST_HOST || 'tests/AutoSourcarr.Tests/bin/Release/net10.0/AutoSourcarr.Tests.dll';
const dataRoot = process.env.E2E_DATA_ROOT || path.join(os.tmpdir(), 'autosourcarr-e2e');
const serve = ['dotnet', JSON.stringify(testHost), '--serve-ui', JSON.stringify(dataRoot), String(port), 'tests/e2e/fixtures/owner.json',
  ...(process.env.E2E_BUNDLE === '1' ? ['--bundle', 'autosourcarr/app/static/dist'] : [])].join(' ');

module.exports = defineConfig({
  testDir: 'tests/e2e',
  timeout: 120_000,
  expect: {timeout: 10_000},
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  // One server instance is shared; keep the load small enough for a 2-core runner.
  workers: process.env.CI ? 2 : undefined,
  reporter: [['list'], ['json', {outputFile: process.env.E2E_JSON || 'test-results/playwright.json'}], ['html', {open: 'never', outputFolder: 'playwright-report'}]],
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    browserName: 'chromium',
  },
  projects: pick(viewports, 'E2E_VIEWPORTS').flatMap(size => pick(themes, 'E2E_THEMES').map(theme => ({
    name: `${size.name}-${theme.name}`,
    use: {
      viewport: size.viewport, isMobile: size.isMobile, hasTouch: size.hasTouch, deviceScaleFactor: size.deviceScaleFactor,
      colorScheme: theme.colorScheme, theme, sizeName: size.name,
    },
  }))),
  webServer: {
    command: serve,
    url: `http://127.0.0.1:${port}/api/v1/live`,
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
    stdout: 'pipe',
    stderr: 'pipe',
  },
});
