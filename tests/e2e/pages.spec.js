// Every top-level page and its main sub-tabs, in this project's size and theme (UI-1, UI-2, UI-14).
const {test, expect, signIn, expectNoHorizontalScroll, expectTouchTargets, expectContrast, expectNoErrors, openSidebar, captureScreenshot} = require('./fixtures');

// Sub-tab buttons per page, by the data attribute the app switches on.
const SUBTABS = {sources: 'data-source-subtab', settings: 'data-settings-subtab', 'manual-actions': 'data-manual-action-subtab'};

async function closeSidebarIfOpen(page) {
  if (await page.evaluate(() => document.documentElement.classList.contains('riSidebarOpen'))) await page.locator('#riMenuToggle').click();
}

async function checkLayout(page, problems, where) {
  await page.waitForLoadState('networkidle', {timeout: 5_000}).catch(() => {});
  await expectNoHorizontalScroll(page, where);
  await expectTouchTargets(page, where);
  await expectContrast(page, where);
  expectNoErrors(problems, where);
  await captureScreenshot(page, where);
}

test('sidebar is reachable', async ({page, sizeName, theme}) => {
  await signIn(page, theme);
  const toggle = page.locator('#riMenuToggle');
  if (sizeName === 'phone') await expect(toggle, 'phone layout uses the menu button').toBeVisible();
  await openSidebar(page);
  const nav = page.locator('#riSidebar button[data-tab]');
  expect(await nav.count(), 'sidebar lists the pages').toBeGreaterThan(5);
  for (const button of await nav.all()) {
    await button.scrollIntoViewIfNeeded();
    await expect(button).toBeVisible();
  }
  const box = await page.locator('#riSidebar').boundingBox();
  expect(box && box.x >= -1 && box.x + Math.min(box.width, 200) <= page.viewportSize().width + 1, 'sidebar is inside the viewport').toBe(true);
});

test('every page and sub-tab keeps the layout', async ({page, problems, theme}) => {
  test.slow();
  await signIn(page, theme);
  await checkLayout(page, problems, 'start page');
  await openSidebar(page);
  const routes = await page.locator('#riSidebar button[data-tab][data-route]').evaluateAll(buttons =>
    buttons.map(button => ({tab: button.dataset.tab, route: button.dataset.route})));
  await closeSidebarIfOpen(page);
  expect(routes.length).toBeGreaterThan(5);
  const visited = new Set();
  for (const {tab, route} of routes) {
    await openSidebar(page);
    await page.locator(`#riSidebar button[data-route="${route}"]`).click();
    await closeSidebarIfOpen(page);
    await expect(page.locator(`section.tab#${CSS_ID(tab)}`), `${route} shows its page`).toHaveClass(/\bactive\b/);
    await checkLayout(page, problems, route);
    if (visited.has(tab) || !SUBTABS[tab]) { visited.add(tab); continue; }
    visited.add(tab);
    const attribute = SUBTABS[tab];
    const names = await page.locator(`section.tab#${CSS_ID(tab)} [${attribute}]`).evaluateAll((buttons, name) =>
      [...new Set(buttons.filter(b => b.getClientRects().length).map(b => b.getAttribute(name)))], attribute);
    for (const name of names) {
      const button = page.locator(`section.tab#${CSS_ID(tab)} [${attribute}="${name}"]:visible`).first();
      await button.click();
      await checkLayout(page, problems, `${route} > ${name}`);
    }
  }
});

// Tab ids are plain words with hyphens; keep the selector honest if that ever changes.
function CSS_ID(id) {
  if (!/^[a-z][a-z0-9-]*$/.test(id)) throw new Error('Unexpected tab id ' + id);
  return id;
}
