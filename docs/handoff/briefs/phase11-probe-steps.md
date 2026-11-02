# Phase 11 probe steps: Playwright (agent W)

For the coordinator, who manages the `ci-probe` branch. Adds the browser run ("Done when: Playwright passes at
phone, tablet and desktop in every theme") to `.github/workflows/probe.yml`. First probe: 9debeae (9 passed, 169 failed; fixed since). Second round
(2026-11-02, on the PC with the pinned Chromium): all 36 projects 156 passed, 0 failed, 24 skipped; the same
with `E2E_BUNDLE=1` (all 36 projects against the minified bundle) 156/0/24.

What runs: `playwright.config.js` + `tests/e2e/`. 36 projects = 3 sizes (phone 375×812 touch, tablet 768×1024
touch, desktop 1440×900) × 6 accents (theme.js `THEME_ACCENTS` via `tests/e2e/themes.js`) × dark/light (`autosourcarr.colorMode`).
5 tests per project = 180; the drawer test skips at tablet and desktop, where the sidebar is always shown (24 skips).
Each test signs in as its own admin account (created by the owner through `/api/v1/accounts`): UI preferences
are stored per account, and parallel tests on one account overwrote each other's theme.
The server is the C# test host `--serve-ui` (`tests/AutoSourcarr.Tests/Api/UiTestHost.cs`): controlled
external-mode runtime on 127.0.0.1:18282, owner from `tests/e2e/fixtures/owner.json`, setup finished,
the 0.4.4 fixture catalogue, no source engine. Playwright starts and stops it (`webServer`).

## 1. New workflow step, before "Probe"

```yaml
      - uses: actions/setup-node@v4
        with:
          node-version: "22"
          cache: npm
```

## 2. Inside the "Probe" step, after the `if [ $b -eq 0 ]; then ... fi` test block (needs the Release build)

```bash
          # Phase 11: Web UI bundle and Playwright at phone/tablet/desktop in every theme.
          pw=skipped; pwb=skipped; ub=skipped
          if [ $b -eq 0 ]; then
            { npm ci --no-audit --no-fund && npx playwright install --with-deps chromium; } > probe/playwright-install.log 2>&1
            node scripts/bundle-ui.mjs --check > probe/bundle-ui.txt 2>&1 && node scripts/bundle-ui.mjs >> probe/bundle-ui.txt 2>&1; ub=$?
            CI=1 E2E_JSON=test-results/playwright.json timeout 2700 npx playwright test > probe/playwright.log 2>&1; pw=$?
            node scripts/playwright-summary.mjs test-results/playwright.json > probe/playwright.txt 2>&1
            # The same checks against the minified bundle the image will serve (all projects; ~4 min locally, more on 2 cores).
            if [ "$ub" = 0 ]; then
              CI=1 E2E_BUNDLE=1 E2E_JSON=test-results/playwright-bundle.json \
                timeout 2700 npx playwright test > probe/playwright-bundle.log 2>&1; pwb=$?
              node scripts/playwright-summary.mjs test-results/playwright-bundle.json > probe/playwright-bundle.txt 2>&1
            fi
            tail -c 30000 probe/playwright.log > probe/playwright.tail.log; rm -f probe/playwright.log
            tail -c 10000 probe/playwright-bundle.log > probe/playwright-bundle.tail.log 2>/dev/null; rm -f probe/playwright-bundle.log
            tail -c 4000 probe/playwright-install.log > probe/playwright-install.tail.log; rm -f probe/playwright-install.log
          fi
```

## 3. Add to the `printf` that writes `probe/result.txt`

Append `playwright=%s\nplaywright_bundle=%s\nui_bundle=%s\n` to the format and `"$pw" "$pwb" "$ub"` to the arguments.

## Reading the result

- `probe/result.txt`: `playwright=0` means every project passed; `ui_bundle=0` the bundle built.
- `probe/playwright.txt`: totals, passed/failed/flaky/skipped per project, notes (for example a popup that is
  full-screen at phone size, where "outside click" does not apply) and the first lines of each failure.
- Traces and screenshots of failures stay in `test-results/` on the runner (not committed). To keep them, add
  `actions/upload-artifact@v4` for `test-results/` and `playwright-report/` with `if: always()`.
- Local narrowing: `E2E_VIEWPORTS=phone E2E_THEMES=rose-light npx playwright test` (needs a Release build of
  `tests/AutoSourcarr.Tests` and a Chromium from `npx playwright install chromium`).

## What each test asserts

| Test | Checks |
| --- | --- |
| `pages.spec.js` sidebar is reachable | phone shows the menu button; every sidebar page button can be reached and is visible; sidebar inside the viewport |
| `pages.spec.js` every page and sub-tab | sign in through `/login`; every sidebar page and the Sources, Settings and Decisions sub-tabs: document width ≤ viewport (wide tables scroll inside their own scroller), on a coarse pointer the first 60 visible controls ≥ 44×44 px (a checkbox counts its label; links in running text are exempt), text contrast from computed styles (up to 400 visible text elements: colour against the composited background with ancestor opacity applied, ≥ 4.5:1, ≥ 3:1 for large text; disabled controls exempt; worst offenders listed), no uncaught page errors and no console errors (HTTP 4xx/5xx "Failed to load resource" lines are expected: the test instance has no source engine). Layout and contrast checks are soft, so one run lists every page's findings. A too-wide phone page zooms out instead of scrolling, so width is compared with the configured viewport, not `innerWidth` |
| `popups.spec.js` (3 tests) | UI-5: search dialog, setup wizard and the phone/tablet navigation drawer close on Escape and on a click outside |

Failures in touch-target size, horizontal scroll or contrast are layout findings (CSS), not test bugs;
console-error failures need a look at the listed message first.
