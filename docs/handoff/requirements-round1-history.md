# AutoSourcarr requirements

Oct 26, 2026 · @Hund

Every requirement from the knowledge DB (244 requirement, 36 decision, 93 workflow and 87 open finding records), merged into one line each and checked against the 0.4.4 code. IDs point to the full record.

Status: **Done** = in the code and covered by tests. **Partial** = works with a known gap. **Open** = not done. Almost nothing has been checked live on the NAS, a real browser or a phone; that is noted only where it matters. Comment on any line you want changed; section 13 lists the decisions that need your answer.

## 1. Deployment and architecture

| # | Requirement | Status | Notes / gap |
| --- | --- | --- | --- |
| D1 | Normal deployment is only AutoSourcarr + its main Suwayomi; two instances = four services. Komf may stay separate. (REQ-DEP-01, DEP-REQ-01, REQ-DEP-ALL-INTERNAL) | Done | Binding rule. No helper services, Docker socket or sibling containers. |
| D2 | Every feature runs inside its own instance and is usable from the Web UI: source engine, repair, translation, reader building. (REQ-DEP-01, DEC-integrated) | Done |  |
| D3 | No separate source-host container; keep the old Tachidesk profile mount. (REQ-DEP-04) | Done | The embedded engine is still Suwayomi-derived. |
| D4 | Two instances share one image but keep config, databases, source profiles, reader URLs/tokens and signing keys separate. Both use internal port 8282. (REQ-DEP-02, DEP-REQ-02/03) | Done | Needs separate writable host mounts. |
| D5 | Compose stays minimal; settings and logins live in the setup wizard and Settings. (REQ-DEP-03, REQ0333-NO-COMPOSE) | Partial | Templates still force Suwayomi `AUTH_MODE: none` and `WEB_UI_ENABLED` and publish port 30058 (DEP-F04). |
| D6 | First boot reaches the setup wizard with no hidden dependencies. (REQ-DEP-08, DEP-REQ-06) | Partial | Compose still requires `config/autosourcarr.env`; a fresh config folder fails before the wizard (DEP-F03). |
| D7 | Updates keep `/config`, `/data`, source profile, reader token and signing key. Never reset data to upgrade. (REQ-DEP-05, DEP-REQ-05) | Done | 0.4.4 adds a schema stamp so an older image refuses a newer database. |
| D8 | Image rebuild/redeploy includes all tools; a restart does not rebuild. (REQ-DEP-06) | Done | Cached rebuilds do not refresh OS packages (DEP-F10). |
| D9 | Paste Compose, the image builds or pulls, it runs; no host init scripts. (PREF-20261019) | Partial | Image is built locally (`pull_policy: build`); no prebuilt image is published. |
| D10 | Restrictive defaults inside the image: non-root 568:568, `no-new-privs`. (DEC0331) | Done | Host AppArmor/namespace limits still block repair on your NAS (BLOCKER0330). |
| D11 | Keep SQLite; no Redis or second catalogue database without measurements. (REQ-PERF-05, DEC-sqlite) | Done |  |
| D12 | Full C#/.NET migration with image-only Compose. (REQ-20261019-DOTNET-MIGRATION) | Open (paused) | Stopped at stage 26. Production is still Python 0.4.x. |

## 2. Sources, repositories and repair

| # | Requirement | Status | Notes / gap |
| --- | --- | --- | --- |
| S1 | Configure Tachiyomi/Mihon repositories in the Web UI; Keiyoushi is the default. (REQ-SRC-01/04) | Done | URL `keiyoushi/extensions/raw/repo/index.pb` matches Keiyoushi's current README. |
| S2 | Install source APKs by drag-and-drop with validation; manual APKs are labelled orphaned. (REQ-SRC-02/04, UI-R16) | Done | Installed extensions are trusted code (F-SRC-10). |
| S3 | Find orphaned/retired sources from older repo snapshots; Orphaned and Installed-orphan as collapsible groups. (REQ-SRC-03, UI-R17) | Done | Sampled history only (F-SRC-08). The daily watcher misses some orphan labels (F-SRC-02). |
| S4 | Source settings editable from Installed and Discover. (UI-R15) | Done |  |
| S5 | Source WebView, user-agent presets/custom/reset, clear cookies and WebView data. (REQ-SRC-13/14, UI-R19) | Done | Pinned WebView hook unverified (F-SRC-03). |
| S6 | Stop WAF/CAPTCHA retry floods, show Needs attention, auto-retry at most once an hour. (REQ-SRC-11, UI-R18) | Done |  |
| S7 | Bundled challenge solver, without claiming CAPTCHA solving. (REQ-SRC-12) | Partial | FlareSolverr's README says none of its CAPTCHA solvers currently work; Cloudflare challenges only. |
| S8 | Source health: working samples, last check/pass/repair dates, daily + manual checks. (REQ-20261011-SOURCE-HEALTH) | Done |  |
| S9 | Look for an official same-source replacement before repairing; never substitute look-alikes. (REQ-SRC-05) | Done |  |
| S10 | Keep the original raw source code (`/sources/raw`) and manage repaired/retry/revert versions. (REQ-20261017-RAW-FIRST-REPAIR) | Done (code) | No repair has completed on your NAS yet. |
| S11 | Keep the last working package before any change. (REQ-SRC-06) | Partial | Kept, but "working" is inferred from evidence, not proven at runtime. |
| S12 | Repair keeps package, identity and structure; minimal edits. (REQ-SRC-07) | Done |  |
| S13 | If a site moved, update to its new official domain. (REQ-SRC-08, SOURCES REQ-SRC-15) | Partial | Address checks exist; no approved domain-migration workflow (F-SRC-01). |
| S14 | Repair must pass title, metadata, chapter-list and page checks; compiling is not enough. (REQ-SRC-09) | Done | Sampled checks (F-SRC-09). |
| S15 | Repair is isolated from live config, data, profiles and other jobs; fails closed. (REQ-SRC-10) | Done | No per-job memory, process or disk limits (REVIEW26-REPAIR-RESOURCE-BUDGET). |
| S16 | Automatically inspect/repair orphaned sources. (SOURCES REQ-SRC-17) | Partial | Opt-in only. |
| S17 | Repair jobs and evidence live in Tasks. (REQ-20261011-REPAIR-TASKS) | Done |  |
| S18 | AI providers: ChatGPT/Codex account, OpenAI, Claude, Gemini, OpenRouter, Ollama, Custom. (REQ-REPAIR-01) | Done |  |
| S19 | Sign in to the AI account inside the wizard (device code). (REQ-REPAIR-02, REQUEST0328-0334) | Partial | Last report: no code shown and the failure appears instantly. Diagnostics were added; not confirmed fixed. |
| S20 | Repair/AI setup needs no Compose edits or hard-coded instance names. (REQ-DEP-07, REQ-REPAIR-04) | Done |  |
| S21 | Source work is paced and bounded to avoid bans and timeouts. (REQ-PERF-03) | Done | Not benchmarked live. |

## 3. Matching and duplicates

| # | Requirement | Status | Notes / gap |
| --- | --- | --- | --- |
| M1 | Automatic matches need >= 0.94 similarity to the full original or a known alternative title; extra words lower the score; summaries never lower the floor. (REQ-MATCH-02) | Done |  |
| M2 | Reject two-word partial matches and single-word titles unless the whole source title matches. | Done |  |
| M3 | Colored/monochrome, manga/webtoon and animated/still are always separate entries; "(colored)" is added only when both exist; "Colored Pencil" stays a title. | Done |  |
| M4 | Reject placeholder aliases ("no alternative title"); keep quoted/parenthesised titles whole; split comma lists only at real separators; strip source names from titles. | Done |  |
| M5 | Duplicate auto-merge needs >= 2 exact titles, or summary >= 0.70 plus one exact title; preview and confirm. (ARCHIVE-REQ-DUPLICATE-SAFE) | Done | Per-source policies can be stricter. |
| M6 | Choose checks (title, summary, author/artist, tags) and thresholds per source and custom group. (REQ-MATCH-04, UI-R28) | Done |  |
| M7 | Same-source and cross-source rules are independent; the source rule beats the group rule; both sides must pass. (REQ-MATCH-05) | Done |  |
| M8 | Missing evidence means manual review, never a fallback from another source. (REQ-MATCH-06) | Done | Stricter new checks can stay blocked by old missing metadata (MATCH25-F07). |
| M9 | Rule presets: built-in and your own, create/edit/copy/delete. (REQUEST0328/0329) | Done |  |
| M10 | Inspect automatically combined duplicates with before-snapshots, reason and date; split from there. (REQ-MATCH-03) | Done | Merges from before the audit existed cannot be reconstructed. |
| M11 | Flag entries that may mix different works, without splitting automatically. (REQ-MATCH-07) | Done | Advisory; entries with > 512 candidate pairs are partly checked. |
| M12 | Manual choices (titles, matches, groups, rejections, separate entries) survive updates and automatic work. (REQ-MATCH-01) | Partial | Interrupted imports and late retries can still leave stale rows. |
| M13 | Manual titles apply only to that entry and the matches attached at the time. (ARCHIVE-REQ-MANUAL-SCOPE) | Done |  |
| M14 | The main title follows the most-bound source title unless manually changed. | Done |  |
| M15 | A manual match forces the choice and removes it from other entries. | Done |  |
| M16 | Reset all manual actions with preview, counts and confirmation, keeping library and history. (REQ-MATCH-08, UI-R26) | Done |  |
| M17 | Tag synonyms normalised; manual tag equivalence/separation; generic tags never count as proof. (REQ-MATCH-09) | Partial | A delayed gallery attach can still use tag evidence from before a newer manual separation (MATCH-F05). |
| M18 | Gallery titles become metadata (artist/circle/type/language/part); per-source gallery grouping on/off with its own criteria. (REQ-GALLERY-01/02) | Done |  |
| M19 | Same-work gallery uploads stay separate chapters; numbered parts share one work. (IMP-REQ-07/08) | Done |  |
| M20 | Machine translations help search only; they never create matches. | Done |  |

## 4. Imports (Mihon/Tachiyomi backups)

| # | Requirement | Status | Notes / gap |
| --- | --- | --- | --- |
| I1 | Imports list Unresolved / Added / Failed / Existing with backup title, source, candidates and Match / Search / Skip / Retry. | Done |  |
| I2 | Combine backup metadata with MangaDex/AniList metadata; match on full/alternate titles and summaries. | Done |  |
| I3 | Check existing entries first; keep existing main-library subscriptions; add-only. (IMP-REQ-01) | Done |  |
| I4 | Manual Match/Retry moves the row out of Unresolved and adds it to the real main Suwayomi; stale responses never bring it back. (REQ-IMPORT-01, IMP-REQ-02) | Partial | A hard crash mid-action can strand a row (NEXT-IMPORT-RECOVERY). |
| I5 | A newer manual action always wins over older background work. (IMP-REQ-03, REQ-IMPORT-02) | Partial | A late library retry is not fenced against a newer Skip (NEXT-IMPORT-REVISION). |
| I6 | Manual actions run alongside automatic tasks. (IMP-REQ-04, REQ-PERF-02) | Done | Reject's remote cleanup still waits for background capacity. |
| I7 | Search fallback drops a leading "The" and a "(colored)" suffix only when needed. | Done |  |
| I8 | Timeouts and cooldowns are retryable, never proof a source lacks the title. (REQ-IMPORT-03) | Done |  |
| I9 | Ordinary rebuilds keep manual matches/groups/titles and import decisions. (IMP-REQ-05) | Partial | A catalogue reset deletes duplicate-review and merge-audit history (IMP-F-05). |
| I10 | Title-only imports can still resolve metadata. | Done |  |

## 5. Metadata, identity and sharing

| # | Requirement | Status | Notes / gap |
| --- | --- | --- | --- |
| X1 | MangaDex and AniList lead identity metadata, plus other reliable databases. A metadata match alone never proves a chapter source. (ARCHIVE-REQ-IDENTITY) | Done |  |
| X2 | Matched entries get cover, summary and all metadata; removing a source removes its metadata. | Done |  |
| X3 | Summaries are real synopses: alt-title lists, links and appended metadata are stripped before comparing. | Done |  |
| X4 | Portable "ASC Media ID v1" so the same work has the same ID across instances. (DEC-20261018-PORTABLE-IDENTITY) | Done |  |
| X5 | Combine two or more catalogues additively, including manual decisions, without a shared server. (REQ-20261019-CATALOGUE-SHARING) | Done | File exchange only; no hosted catalogue. |
| X6 | Optional per-field display ownership (lock description/artwork/creators). (ARRPROP P11) | Done |  |

## 6. Chapters and serving

| # | Requirement | Status | Notes / gap |
| --- | --- | --- | --- |
| C1 | Keep chapter/vol/part/act/stage/step/No./case/episode numbers (Act.09, Stage 1, No.2). | Done |  |
| C2 | Season-aware chapter selection (season 2 never wins chapter 1). | Done |  |
| C3 | Source groups share priority; per-source latest/missing counts; "Use as primary". | Done |  |
| C4 | Unified latest served chapter across sources; never serve partial combined pages (#001-002). | Done |  |
| C5 | Quality score per candidate; a failed or deferred sample keeps the last score. (ARRPROP P04) | Done | The DB still listed this as an open bug; it was fixed in 0.4.0. |
| C6 | Opt-in reviewed upgrades to better sources with cutoffs and minimum gain. (ARRPROP P12) | Done |  |
| C7 | Downloads go to the main Suwayomi; show total/downloaded chapters. | Done |  |

## 7. Reader (APK/JAR) and access

| # | Requirement | Status | Notes / gap |
| --- | --- | --- | --- |
| R1 | Build and serve a current APK (phone) and JAR (Suwayomi) per instance with the right public/internal URL, token and stable signing key. (REQ-READER-01) | Done (code) | No real APK/JAR build has been run in recent releases. |
| R2 | Builds are sequential within an instance; outputs isolated. (REQ-READER-02) | Done |  |
| R3 | Reader supports popular/latest/search/filters without waiting on every source. (REQ-READER-03) | Done |  |
| R4 | Reader API is key-protected; the Authelia bypass covers only `^/api/reader/v1([/?].*)?$`. (REQ-READER-04) | Done |  |
| R5 | APK/JAR downloads (now on the System page) refuse stale or mismatched builds. (UI-R22) | Done |  |
| R6 | The wizard should reject reader settings the builder will refuse (HTTP URL, empty key). (DEP-F08) | Open | Today you only find out when the build fails. |
| R7 | Cross-site browser writes to admin/API routes are refused. | Done (0.4.4) | New in 0.4.4. `ADMIN_TOKEN` is still empty by default. |

## 8. Web UI

| # | Requirement | Status | Notes / gap |
| --- | --- | --- | --- |
| U1 | Every tab uses the full width; no nested scrollbars. (REQ-UI-01) | Done |  |
| U2 | Clear navigation with errors visible at the top. Originally: catalogue tabs left, Status/Imports/Tasks/gear Settings right. (REQ-UI-02) | Done (changed) | Since 0.4.0 it is a Sonarr v5 sidebar: Manga (Add new, Library import), Match matrix (Duplicates, Mixed entries, Saved decisions), Tasks (Source repairs), Wanted, Sources, Needs attention, Statistics, Settings, System. Confirm this replaces the old layout. |
| U3 | Themes: Neutral, Tonal panels, Dual accents, Paper and ink, Graphite, Aurora; primary/secondary/tertiary colour roles; saved per user. (REQ-UI-04/05/06) | Done |  |
| U4 | Separate main and matched thumbnail sizes; thumbnails in the Imports list. (REQ-UI-07) | Done |  |
| U5 | Popups close on outside click and Escape. (REQ-UI-08) | Done | Custom dialogs lack full focus trapping (LANG-WEB-CUSTOM-MODAL-FOCUS). |
| U6 | Refresh restores tab, filters, page, scroll and expanded details, never secrets. (REQ-UI-09, UI-R11) | Partial |  |
| U7 | Details order: alternate titles, tags, metadata, matches, chapters, previously served; all collapsible. (REQ-UI-10) | Done |  |
| U8 | Filter entries by connected source without contacting websites. (REQ-UI-11) | Done |  |
| U9 | Side-by-side comparison for imports and duplicates. (REQ-UI-03) | Done |  |
| U10 | Chapters in 3 columns; source groups column. | Done |  |
| U11 | Subbed (now "Manga") has "Search all"; Match matrix does not. | Done |  |
| U12 | Pages do not jump on load/refresh; entry windows do not open/close by themselves. | Done |  |
| U13 | Rendered-browser checks. (UI-F07) | Open | No real-browser verification has been done. |

## 9. Tasks, speed and diagnostics

| # | Requirement | Status | Notes / gap |
| --- | --- | --- | --- |
| T1 | Every operation is a Task: Ongoing/Completed/Failed, progress, cancel, finish date/time, newest first. | Done |  |
| T2 | A frozen task is an error; timed-out items continue and can be retried. | Done |  |
| T3 | Fast boot; cached entries appear at once and refresh in the background. (REQ-PERF-01/04) | Partial | A cached feed with merged IDs can still wait for the writer lock (NEXT-CACHE-READ). |
| T4 | Local queue timeouts are not counted as source outages. (NEXT-ADMISSION, REQ-DIAG-03) | Partial | Some Browse admission timeouts are still misclassified. |
| T5 | Download one redacted diagnostics text file (System page). (REQ-DIAG-01) | Done |  |
| T6 | Group repeated errors with counts. (REQ-DIAG-02) | Done |  |
| T7 | Large catalogues: no full title scan per search. (NEXT-TITLE-INDEX, PERF025-F03) | Partial | Tag lists still build the whole vocabulary first. |

## 10. Setup and settings

| # | Requirement | Status | Notes / gap |
| --- | --- | --- | --- |
| W1 | Progressive first-boot setup: library, repositories, matching, reader, performance, tools, AI account, app links. (REQ-SETUP-01) | Done | Blocked by D6 on a fresh config folder. |
| W2 | Rerun setup from Settings; Cancel keeps a draft; Finish writes only changed values. (REQ-SETUP-02) | Done |  |
| W3 | Offer safe cleanup of known old app-generated files only. (REQ-SETUP-03) | Done |  |
| W4 | Browser links to Suwayomi/Komga/Komf/AI dashboards, separate from internal addresses. (REQ-SETUP-04) | Done |  |
| W5 | Selectable database location that moves both databases. (ARCHIVE-REQ-DB-LOCATION) | Done |  |
| W6 | Full reset keeps config and the main Suwayomi library; separate reset buttons. | Done |  |

## 11. How work is done (process requirements)

| # | Requirement | Status | Notes |
| --- | --- | --- | --- |
| P1 | One executor; no parallel agents unless asked. | Active | Now in CLAUDE.md. |
| P2 | Use the knowledge DB and earlier research before every app update. (REQ-20261011-TWO-HOUR-RESEARCH, WORKFLOW-20261011-RESEARCH-GATE) | Active | The formal receipt tool needs a shell on your PC. |
| P3 | Complete release ZIP; one CHANGELOG; no report/log/diff files in the ZIP; ZIPs saved to the archive. (REQ-RELEASE-01) | Active | 0.4.4 ZIP saved to the archive. |
| P4 | Report honestly what was and was not tested. (REQ-RELEASE-03, DEC-record-truth) | Active |  |
| P5 | Show a remaining-time estimate in every migration progress update. (PREF-20261022-CONVERSION-ETA) | Active when migration resumes |  |
| P6 | Keep the knowledge DB as the single development DB; never index secrets. (REQ-KNOW-01/02, FLOW-privacy) | Active | Stale paths and mixed priorities are fixed by `kb-maintenance.py` once you run it. |
| P7 | The knowledge DB updates on every AutoSourcarr prompt automatically. (REQ-KNOW-03) | Partial | No chat event can trigger it; it is updated when a session does the work. |

## 12. Open proposals (not approved yet)

| # | Proposal | Notes |
| --- | --- | --- |
| A1 | P16: profile and bound the cold perceptual-hash startup (one 3.8 MP image took 18 s). | Measured in a synthetic test only. |
| A2 | P17: narrower source-record hydration for large libraries. |  |
| A3 | P18: dirty-ID journal writes (fewer WAL writes). |  |
| A4 | P14: qualify Suwayomi v2.4.2366 (released 2026-09-30). | Capability checks exist; native upgrade not tested. 2.4 fixes failed extension updates losing the installed extension, nested-filter browsing and FlareSolverr POST bodies. |
| A5 | Fix the 40 stale tests so the Linux suite is green. | Test-only change. |
| A6 | Pin base images by digest and pip installs by hash. | Supply-chain hardening. |

## 13. Decisions for you

1. **Suwayomi access**: keep `AUTH_MODE: none` with port 30058 published, or drop the host port / enable Suwayomi auth? (D5)
2. **Admin token**: generate one on first boot so the admin API is not open on the LAN? (R7)
3. **First boot**: make `autosourcarr.env` optional so a fresh install reaches the wizard? Needs Compose 2.24+ on TrueNAS. (D6)
4. **Suwayomi 2.4**: test and move to v2.4.2366? (A4)
5. **Catalogue reset**: keep duplicate-review and merge-audit history? (I9)
6. **Moved websites**: let repair switch a source to a verified new official domain? (S13)
7. **.NET migration**: stay paused, resume, or drop? (D12)
8. **Stale tests**: fix the 40 failing tests? (A5)

## Sources

- [Suwayomi-Server releases](https://github.com/Suwayomi/Suwayomi-Server/releases) and [v2.4.2366 notes](https://github.com/Suwayomi/Suwayomi-Server/releases/tag/v2.4.2366)
- [FlareSolverr README](https://github.com/FlareSolverr/FlareSolverr)
- [Keiyoushi extensions README](https://github.com/keiyoushi/extensions)
