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
  // UI preferences (theme, colour mode) are stored per account on the server, and the first sign-in moves the
  // browser's local choice there. Parallel tests sharing one account would overwrite each other's theme, so
  // every test signs in as its own admin account, created by the owner through /api/v1/accounts.
  account: async ({playwright, baseURL}, use, testInfo) => {
    const api = await playwright.request.newContext({baseURL, extraHTTPHeaders: {Origin: baseURL, 'Sec-Fetch-Site': 'same-origin'}});
    const login = await api.post('/api/v1/auth/login', {data: {username: owner.username, password: owner.password}});
    base.expect(login.ok(), 'owner signs in through the API').toBe(true);
    const name = 'e2e' + require('crypto').createHash('sha256').update(testInfo.testId + ':' + testInfo.retry).digest('hex').slice(0, 20);
    const created = await api.post('/api/v1/accounts', {data: {username: name, password: owner.password, role: 'admin'}});
    base.expect([201, 400].includes(created.status()), `test account created (HTTP ${created.status()} ${await created.text()})`).toBe(true);
    await api.dispose();
    await use({username: name, password: owner.password});
  },
  page: async ({page, context, theme, problems, account}, use) => {
    page.account = account;
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
  const who = page.account || owner;
  await page.locator('#loginName').fill(who.username);
  await page.locator('#loginPassword').fill(who.password);
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
  // On a phone a too-wide page does not scroll: the browser zooms out and innerWidth grows past the device
  // width. Compare with the configured viewport, not innerWidth.
  const result = await page.evaluate(device => {
    const width = device, doc = document.documentElement;
    const scrollers = el => { for (let node = el.parentElement; node && node !== document.body; node = node.parentElement) {
      const style = getComputedStyle(node);
      if (['auto', 'scroll', 'hidden', 'clip'].includes(style.overflowX) || node.matches('[data-table-scroller],.riTableScroll,.tablewrap,.sourceHealthTableWrap')) return true;
    } return false; };
    // Fixed layers (header, drawer) stretch with the zoomed-out page; report the innermost element that sticks out.
    const inFixed = el => { for (let node = el; node && node !== document.body; node = node.parentElement) if (getComputedStyle(node).position === 'fixed') return true; return false; };
    const offenders = [];
    if (Math.max(doc.scrollWidth, window.innerWidth) > width + 1) for (const el of document.body.querySelectorAll('*')) {
      const rect = el.getBoundingClientRect();
      if (rect.width && rect.height && rect.right > width + 1 && !inFixed(el) && !scrollers(el) && ![...el.children].some(child => child.getBoundingClientRect().right > width + 1)) {
        offenders.push(`${(() => { const up = el.closest('[id]'); return up && up !== el ? '#' + up.id + ' ' : ''; })()}${el.tagName.toLowerCase()}${el.id ? '#' + el.id : ''}${el.className && typeof el.className === 'string' ? '.' + el.className.trim().split(/\s+/).slice(0, 2).join('.') : ''} right=${Math.round(rect.right)}`);
        if (offenders.length >= 8) break;
      }
    }
    return {scrollWidth: Math.max(doc.scrollWidth, window.innerWidth), width, offenders};
  }, page.viewportSize().width);
  base.expect.soft(result.scrollWidth, `${where}: document is ${result.scrollWidth}px wide in a ${result.width}px viewport; widest: ${result.offenders.join(', ')}`)
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
      if (rect.width < 43.5 || rect.height < 43.5) small.push(`${el.tagName.toLowerCase()}${el.id ? '#' + el.id : ''}${el.type && el.tagName === 'INPUT' ? '[type=' + el.type + ']' : ''}${el.name ? '[name=' + el.name + ']' : ''} in ${el.parentElement?.id ? '#' + el.parentElement.id : '.' + String(el.parentElement?.className || '').split(' ')[0]} "${(el.getAttribute('aria-label') || el.textContent || '').trim().slice(0, 30)}" ${Math.round(rect.width)}x${Math.round(rect.height)}`);
    }
    return {coarse: true, small, checked: sample.length};
  });
  if (!result.coarse) return;
  base.expect.soft(result.small, `${where}: ${result.small.length} of ${result.checked} sampled controls are smaller than 44x44 px`).toEqual([]);
}

/**
 * WCAG AA on what is actually drawn: visible text elements, their computed colour against the effective
 * background (ancestor backgrounds composited, opacity applied). 4.5:1 for normal text, 3:1 for large text
 * (24px, or 18.66px bold). Disabled controls are exempt (WCAG 1.4.3); text over images/gradients is skipped.
 */
async function expectContrast(page, where) {
  const result = await page.evaluate(() => {
    const canvas = document.createElement('canvas'); canvas.width = canvas.height = 1;
    const ctx = canvas.getContext('2d', {willReadFrequently: true});
    const cache = new Map();
    const rgba = css => {
      if (cache.has(css)) return cache.get(css);
      ctx.clearRect(0, 0, 1, 1); ctx.fillStyle = '#000'; ctx.fillStyle = css; ctx.fillRect(0, 0, 1, 1);
      const [r, g, b, a] = ctx.getImageData(0, 0, 1, 1).data, value = [r, g, b, a / 255];
      cache.set(css, value); return value;
    };
    const over = (top, bottom) => { const a = top[3]; return [0, 1, 2].map(i => top[i] * a + bottom[i] * (1 - a)).concat(1); };
    const lum = c => { const f = v => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; }; return 0.2126 * f(c[0]) + 0.7152 * f(c[1]) + 0.0722 * f(c[2]); };
    const ratio = (a, b) => { const x = lum(a), y = lum(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };
    const root = rgba(getComputedStyle(document.documentElement).backgroundColor);
    const pageBg = root[3] ? over(root, [255, 255, 255, 1]) : rgba(getComputedStyle(document.body).backgroundColor);
    function background(el) {
      const layers = [];
      for (let node = el; node; node = node.parentElement) {
        const style = getComputedStyle(node);
        if (style.backgroundImage !== 'none') return null;
        const color = rgba(style.backgroundColor);
        if (color[3] > 0) { layers.push(color); if (color[3] >= 1) break; }
      }
      let result = over(pageBg[3] ? pageBg : [255, 255, 255, 1], [255, 255, 255, 1]);
      for (const layer of layers.reverse()) result = over(layer, result);
      return result;
    }
    const opacity = el => { let value = 1; for (let node = el; node; node = node.parentElement) value *= Number(getComputedStyle(node).opacity); return value; };
    const failures = []; let checked = 0, skipped = 0;
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    const seen = new Set();
    while (walker.nextNode() && checked < 400) {
      const text = walker.currentNode.textContent.trim(), el = walker.currentNode.parentElement;
      if (!text || !el || seen.has(el)) continue;
      seen.add(el);
      if (el.closest('[hidden],[inert],[aria-hidden="true"],script,style,svg,option,.srOnly,.riSkip') || el.closest(':disabled,[aria-disabled="true"]')) continue;
      const rect = el.getBoundingClientRect(), style = getComputedStyle(el);
      if (!rect.width || !rect.height || style.visibility !== 'visible' || rect.bottom < 0 || rect.top > innerHeight || rect.right < 0 || rect.left > innerWidth) continue;
      if (rect.width < 2 || rect.height < 2 || style.clip === 'rect(0px, 0px, 0px, 0px)') continue;
      let bg = background(el);
      if (!bg) { skipped++; continue; }
      let fg = rgba(style.color);
      fg = over(fg, bg);
      // A translucent ancestor (opacity < 1) blends its whole content, text and backgrounds, into what is behind it.
      const alpha = opacity(el);
      if (alpha < 0.05) continue; // fully transparent: not drawn
      if (alpha < 1) {
        let top = el; for (let node = el; node; node = node.parentElement) if (Number(getComputedStyle(node).opacity) < 1) top = node;
        const behind = top.parentElement ? background(top.parentElement) : pageBg;
        if (!behind) { skipped++; continue; }
        fg = over([fg[0], fg[1], fg[2], alpha], behind); bg = over([bg[0], bg[1], bg[2], alpha], behind);
      }
      const size = parseFloat(style.fontSize), bold = Number(style.fontWeight) >= 700;
      const large = size >= 24 || (bold && size >= 18.66), need = large ? 3 : 4.5, value = ratio(fg, bg);
      checked++;
      if (value < need - 0.005) failures.push({value, need, label: `${el.tagName.toLowerCase()}${el.id ? '#' + el.id : ''}${typeof el.className === 'string' && el.className.trim() ? '.' + el.className.trim().split(/\s+/).slice(0, 2).join('.') : ''} "${text.slice(0, 24)}" ${value.toFixed(2)}:1 < ${need} (${Math.round(size)}px, fg rgb(${fg.slice(0, 3).map(Math.round)}) on rgb(${bg.slice(0, 3).map(Math.round)}))`});
    }
    failures.sort((a, b) => a.value / a.need - b.value / b.need);
    return {checked, skipped, failures: failures.slice(0, 10).map(f => f.label), count: failures.length};
  });
  base.expect.soft(result.failures, `${where}: ${result.count} of ${result.checked} sampled text elements are below WCAG AA contrast (worst first; ${result.skipped} over images skipped)`).toEqual([]);
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

/**
 * PIPE-5 (phase 13): with E2E_SCREENSHOT_DIR set, one full-page screenshot per page, size and theme, as
 * <dir>/<project>/<page>.png, for scripts/screenshot-diff.mjs against the previous release. Otherwise a no-op.
 */
async function captureScreenshot(page, where) {
  const dir = process.env.E2E_SCREENSHOT_DIR;
  if (!dir) return;
  const path = require('path');
  const project = base.test.info().project.name;
  const name = String(where).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'page';
  await page.screenshot({path: path.join(dir, project, name + '.png'), fullPage: true, animations: 'disabled', caret: 'hide'})
    .catch(error => console.warn(`screenshot ${project}/${name} failed: ${error.message}`));
}

module.exports = {test, expect: base.expect, signIn, expectNoHorizontalScroll, expectTouchTargets, expectContrast, expectNoErrors, openSidebar, captureScreenshot};
