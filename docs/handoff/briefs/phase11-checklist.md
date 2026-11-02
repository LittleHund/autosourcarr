# Phase 11 checklist (Web UI finish), built 2026-11-02

Sources: `conversion-plan.md` phase 11 text, `requirements.md` (UI-1..14, SET-1, SET-2, PERF-4, PERF-9),
`docs/port-map.md` (StaticEndpoints, UiPreferencesEndpoints), the phase 10 checklist rows owned by
phase 11, and the UI calls still on `/api/admin`. Ticks are filled in at phase end.

## (a) Plan text and "Done when"

| Item | Area | State |
| --- | --- | --- |
| Responsive layouts and touch targets (phone, tablet, desktop; no mouse-only feature) | T theme/layout | |
| One theme module replacing the two palette systems (CSS `:root` palette + JS `applyAppearancePalette`) | T | |
| Dark and light variants of all six accents; stronger control border (today 1.4–1.7:1; light accents 1.5–2.1:1) | T | |
| Unit test on the palette table: WCAG AA (text 4.5:1, large text/UI parts 3:1) in every theme | T | |
| Light/dark follows the device setting by default; each user can still pick a theme (UI-3) | T | |
| Wizard: required first, then optional and opt-in (SET-1); rerun, Cancel keeps a draft, Finish writes only changes (SET-2) | W wizard/bundle/e2e | |
| JS/CSS bundled and minified in CI | W | |
| Cache size limit in Settings, oldest/least-used replaced when full (PERF-4) | C backend | |
| Diagnostics download, sensitive data removed (PERF-9) | C | |
| Done when: Playwright passes at phone, tablet and desktop in every theme | W (runs in ci-probe) | |
| Done when: the contrast test reports AA everywhere | T | |

## (b) Requirements

UI-1 full width / no nested scrollbars · UI-2 sidebar · UI-3 six themes, AA, device default · UI-4 thumbnail
sizes · UI-5 popups close on outside click/Escape · UI-6 refresh returns exactly where you were (Partial) ·
UI-7 details order · UI-8 filter by source (local) · UI-9 side-by-side evidence · UI-10 chapter columns ·
UI-11 Search all · UI-12 no jumping · UI-13 live UI via SSE instead of polling · UI-14 PC/phone/tablet ·
SET-1 · SET-2 · PERF-4 · PERF-9.

## (c) Port-map rows and routes

StaticEndpoints `/` (exists) · UiPreferencesEndpoints (2 routes: `/api/admin/ui-preferences` GET/PUT → `/api/v1`).

## (d) UI calls still on `/api/admin`

`app.js` ui-preferences (5) · `instance-tools.js` `/api/admin/instance` (6, backup routes → phase 12, keep
the UI working: move only if a `/api/v1` route exists) · `internal-services.js` `/api/admin/services` (2,
→ 0.5.0 status page).

## (e) Carried into phase 11 (phase 10 checklist)

| Item | Area |
| --- | --- |
| Internal services panel (`/api/admin/services`, per-instance builder) → 0.5.0 status page | C (+UI) |
| Page-first lists (PERF-5) for the big lists (library, catalogue entries, matrix, history, tasks) | C (+UI) |
| Event-driven UI refresh (PERF-8, UI-13): EventSource on `/api/v1/events`, polling removed | L live |
| Persisted user-confirmed "Fixed" repair state (hard rule 6) | C |
| Split-path decision-log atomicity (split written in the same transaction as its log entry) | C |
| `CatalogueDiscovery.SourceResult` (exclude deferrals first, hard rule 3) | C |

## (f) Phase end

Green probe (build + all tests + Playwright) · local check-project + UI suites · independent audit, findings
fixed or recorded · push `conversion`, green CI (run id) · ZIP + sha256 · knowledge records
`phase11-records.json` · port-map ticks · PC sync · checkpoint (repo copy) · report.
