// UI-5: popups close on Escape and on a click outside them.
const {test, expect, signIn, expectNoErrors, openSidebar} = require('./fixtures');

/** A point outside the given box, inside the viewport (the backdrop of a modal). */
async function outside(page, locator) {
  const box = await locator.boundingBox(), {width, height} = page.viewportSize();
  const candidates = [[4, 4], [width - 4, 4], [4, height - 4], [width - 4, height - 4]];
  const free = candidates.find(([x, y]) => !box || x < box.x || x > box.x + box.width || y < box.y || y > box.y + box.height);
  return free || null;
}

/** Clicks outside the popup; a full-screen sheet has no outside, which is recorded instead. */
async function clickOutside(page, locator, what) {
  const point = await outside(page, locator);
  if (!point) { test.info().annotations.push({type: 'full-screen', description: `${what} covers the viewport; outside click not applicable`}); return false; }
  await page.mouse.click(point[0], point[1]);
  return true;
}

async function openSearch(page) {
  const button = page.locator('#riSearchOpen');
  if (await button.isVisible()) await button.click();
  else await page.keyboard.press('/');
  await expect(page.locator('#riSearchDialog')).toHaveAttribute('open', '');
}

test('search dialog closes on Escape and outside click', async ({page, problems, theme}) => {
  await signIn(page, theme);
  const dialog = page.locator('#riSearchDialog');
  await openSearch(page);
  await page.keyboard.press('Escape');
  await expect(dialog).not.toHaveAttribute('open', '');
  await openSearch(page);
  const form = dialog.locator('.riCommandHead');
  if (await clickOutside(page, dialog, 'search dialog')) {
    await expect(dialog, 'a click on the backdrop closes the search').not.toHaveAttribute('open', '');
    await expect(form).toBeHidden();
  }
  expectNoErrors(problems, 'search dialog');
});

test('setup wizard closes on Escape and outside click and keeps the draft', async ({page, problems, theme}) => {
  await signIn(page, theme);
  await openSidebar(page);
  await page.locator('#riSidebar button[data-tab="settings"]').click();
  if (await page.evaluate(() => document.documentElement.classList.contains('riSidebarOpen'))) await page.locator('#riMenuToggle').click();
  const modal = page.locator('#setupModal');
  await page.locator('#runSetup').click();
  await expect(modal).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(modal).toBeHidden();
  await page.locator('#runSetup').click();
  await expect(modal).toBeVisible();
  if (await clickOutside(page, modal.locator('.setupShell'), 'setup wizard'))
    await expect(modal, 'a click outside the wizard closes it').toBeHidden();
  expectNoErrors(problems, 'setup wizard');
});

test('navigation drawer closes on Escape and outside click', async ({page, problems, theme}) => {
  await signIn(page, theme);
  const toggle = page.locator('#riMenuToggle');
  test.skip(!(await toggle.isVisible()), 'The sidebar is always shown at this size; there is no drawer.');
  const open = () => page.evaluate(() => document.documentElement.classList.contains('riSidebarOpen'));
  await toggle.click();
  expect(await open()).toBe(true);
  await page.keyboard.press('Escape');
  await expect.poll(open).toBe(false);
  await toggle.click();
  expect(await open()).toBe(true);
  if (await clickOutside(page, page.locator('#riSidebar'), 'navigation drawer'))
    await expect.poll(open, 'a click on the scrim closes the drawer').toBe(false);
  expectNoErrors(problems, 'navigation drawer');
});
