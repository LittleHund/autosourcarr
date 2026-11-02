// Shared fixtures: the project's theme and size, a signed-in page, and the layout checks every page gets.
const base = require('@playwright/test');
const owner = require('./fixtures/owner.json');
const {applyTheme, appliedTheme} = require('./themes');

// Chromium reports every HTTP error response as a console error. The test instance has no source engine,
// so source calls answer 5xx by design; those lines are not page errors. Everything else fails the test.
const EXPECTED_CONSOLE = [/^Failed to load resource: the server responded with a status of (4\d\d|5\d\d)/];

const test = base.test.extend({
  theme: [null, {option: true}],
  sizeName: ['desktop', {option: true}],
  problems: async ({}, use) => { await use({console: [], page: []}); },
  page: async ({page, context, theme, problems}, use) => {
    if (theme) await applyTheme(context, theme);
    page.on('pageerror', error => problems.page.push(String(error && error.stack || error)));
    page.on('console', message => {
      if (message.type() !== 'error') return;
      const text = message.text();
      if (!EXPECTED_CONSOLE.some(pattern => pattern.test(text))) problems.console.push(`${text} (${message.location().url || 'inline'})`);
    });
    await use(page);
  },
});

/** Signs in through the login page, as a person would, and waits for the app shell. */
async function signIn(page, theme) {
  await page.goto('/login?next=/');
  await page.locator('#loginName').fill(owner.username);
  await page.locator('#loginPassword').fill(owner.password);
  await page.locator('#loginSubmit').click();
  await page.waitForURL(url => !url.pathname.startsWith('/login'));
  await page.locator('#riSidebar').waitFor({state: 'attached'});
  await page.waitForLoadState('networkidle').catch(() => {});
  base.expect(await page.locator('#setupModal').isHidden(), 'setup is finished, so the wizard stays closed').toBe(true);
  if (theme) {
    const applied = await appliedTheme(page);
    base.expect(applied.accent, 'theme accent applied').toBe(theme.accent);
    base.expect(applied.scheme, 'light/dark applied').toBe(theme.scheme);
  }
}

/** The page never scrolls sideways; wide content scrolls inside its own scroller. */
async function expectNoHorizontalScroll(page, where) {
  const result = await page.evaluate(() => {
    const width = window.innerWidth, doc = document.documentElement;
    const scrollers = el => { for (let node = el.parentElement; node && node !== document.body; node = node.parentElement) {
      const style = getComputedStyle(node);
      if (['auto', 'scroll', 'hidden', 'clip'].includes(style.overflowX) || node.matches('[data-table-scroller],.riTableScroll,.tablewrap,.sourceHealthTableWrap')) return true;
    } return false; };
    const offenders = [];
    if (doc.scrollWidth > width + 1) for (const el of document.body.querySelectorAll('*')) {
      const rect = el.getBoundingClientRect();
      if (rect.width && rect.height && rect.right > width + 1 && getComputedStyle(el).position !== 'fixed' && !scrollers(el)) {
        offenders.push(`${el.tagName.toLowerCase()}${el.id ? '#' + el.id : ''}${el.className && typeof el.className === 'string' ? '.' + el.className.trim().split(/\s+/).slice(0, 2).join('.') : ''} right=${Math.round(rect.right)}`);
        if (offenders.length >= 8) break;
      }
    }
    return {scrollWidth: doc.scrollWidth, width, offenders};
  });
  base.expect(result.scrollWidth, `${where}: document is ${result.scrollWidth}px wide in a ${result.width}px viewport; widest: ${result.offenders.join(', ')}`)
    .toBeLessThanOrEqual(result.width + 1);
}

/** On a coarse pointer the visible controls are at least 44x44 px (their label counts for checkboxes). */
async function expectTouchTargets(page, where) {
  const result = await page.evaluate(() => {
    if (!matchMedia('(pointer: coarse)').matches) return {coarse: false, small: [], checked: 0};
    const controls = [...document.querySelectorAll('button, select, input:not([type="hidden"]), textarea, summary, [role="button"], [role="tab"], a[href]')]
      .filter(el => {
        if (el.closest('[hidden],[inert]') || el.disabled) return false;
        // Links inside running text are exempt (WCAG 2.5.8 inline exception).
        if (el.tagName === 'A' && el.closest('p, li, td') && !el.matches('.button, [class*="Button"], [class*="Action"]')) return false;
        const rect = el.getBoundingClientRect(), style = getComputedStyle(el);
        return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && rect.bottom > 0 && rect.top < innerHeight && rect.right > 0 && rect.left < innerWidth;
      });
    const sample = controls.slice(0, 60), small = [];
    for (const el of sample) {
      let rect = el.getBoundingClientRect();
      const label = el.matches('input[type="checkbox"],input[type="radio"]') && (el.closest('label') || (el.id && document.querySelector(`label[for="${CSS.escape(el.id)}"]`)));
      if (label) { const outer = label.getBoundingClientRect(); rect = {width: Math.max(rect.width, outer.width), height: Math.max(rect.height, outer.height)}; }
      if (rect.width < 43.5 || rect.height < 43.5) small.push(`${el.tagName.toLowerCase()}${el.id ? '#' + el.id : ''} "${(el.getAttribute('aria-label') || el.textContent || '').trim().slice(0, 30)}" ${Math.round(rect.width)}x${Math.round(rect.height)}`);
    }
    return {coarse: true, small, checked: sample.length};
  });
  if (!result.coarse) return;
  base.expect(result.small, `${where}: ${result.small.length} of ${result.checked} sampled controls are smaller than 44x44 px`).toEqual([]);
}

function expectNoErrors(problems, where) {
  base.expect(problems.page, `${where}: uncaught page errors`).toEqual([]);
  base.expect(problems.console, `${where}: console errors`).toEqual([]);
}

/** Phone and narrow layouts hide the sidebar behind the menu button; wide layouts show it. */
async function openSidebar(page) {
  const toggle = page.locator('#riMenuToggle');
  if (await toggle.isVisible() && await toggle.getAttribute('aria-expanded') !== 'true') await toggle.click();
}

module.exports = {test, expect: base.expect, signIn, expectNoHorizontalScroll, expectTouchTargets, expectNoErrors, openSidebar};
