# Phase 12 checklist (Backup and rollback), built 2026-11-02

Sources: `conversion-plan.md` phase 12 text, `requirements.md` (SET-3, SET-4, SET-6, SET-8, SET-9; PIPE-4
for the version check), `docs/port-map.md` (`instance_backup.py` → InstanceBackups, `setup_cleanup.py`,
BackupEndpoints 6 routes), and the phase 11 limit "instance backup routes answer 404".

## (a) Plan text and "Done when"

| Item | State |
| --- | --- |
| Versioned backup format for settings, profiles and catalogues (secrets excluded; SET-12 encrypted secrets with recovery codes is phase 17) | done |
| One format, also used for sharing (SET-8; the catalogue part = the phase 9 share format) | done |
| Automatic backup before every update or migration (SET-9) | done |
| One-click rollback when an update or migration fails (SET-9) | done |
| Old-file removal (SET-3; `setup_cleanup.py`; automatic on image updates) | done |
| Their UI pages (Settings / System → Backups; instance tools dialog) on `/api/v1` | done |
| Done when: a backup restores on a clean instance | done |
| Done when: a failed update rolls back | done |

## (b) Requirements

SET-3 old files removed (automatic on image updates) · SET-4 link settings only for what the app needs
(optional Komf/AI dashboard links removed) · SET-6 full reset keeps config and library · SET-8 Mihon-like
versioned backup/restore, secrets excluded, used for sharing, version checked against PIPE-4 · SET-9 automatic
backup before every update/migration, one-click rollback · hard rules 1, 11, 16 (restore never imports secrets
from outside the format; never touches live config while preparing a release).

## (c) Port-map rows and routes

`instance_backup.py` (create, export, listing, delete, stage, verify_stage, arm, apply_pending; password
protection) · `setup_cleanup.py` (scan, cleanup_after_setup) · BackupEndpoints: `GET/POST /api/admin/instance/backups`,
`GET …/{id}/download`, `DELETE …/{id}`, `POST /api/admin/instance/restore/preview`, `POST …/restore/apply` → `/api/v1`.

## (d) UI pages

`instance-tools.js` (6 calls on `/api/admin/instance`) → `/api/v1`; wizard cleanup status (today "no cleanup
status is available"); System page backup list, create, download, delete, restore preview/apply, rollback notice.

## (e) Phase end

Green probe (build + all tests + Playwright) · local check-project + UI suites · independent audit, findings fixed or
recorded · push `conversion`, green CI (run id) · ZIP + sha256 · knowledge records `phase12-records.json` · port-map
ticks · PC sync · checkpoint (repo copy) · report.

Result (2026-11-02): all rows done (commits eff800c, fc14eb2, 93cf430, 1dd9a7d); two independent audits, all findings fixed.
