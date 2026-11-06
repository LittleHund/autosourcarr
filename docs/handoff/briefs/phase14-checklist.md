# Phase 14 checklist (Cutover), built 2026-11-03

Sources: `conversion-plan.md` phase 14 text, `requirements.md` (ARC-7, IMP-9), CLAUDE.md §6 items marked
"before cutover" / "fix at release", and the release rules (VERSION copies, `?v=`, extension version, CHANGELOG).
Gates (hard rule 15): merge to `main`, ghcr publish, any NAS change and the deploy each need the owner's explicit go.

## (a) Plan text and "Done when"

| Item | Owner | State |
| --- | --- | --- |
| Migrate a snapshot of the owner's 0.4.x data on the PC with ONE command | tool: Claude; run: owner | tool done (wrappers tested); owner run open |
| Compare counts, manual decisions and subscriptions with the Python app (parity report) | Claude | done (parity-report.md/.json, strict verdict) |
| Release 0.5.0 with CHANGELOG | Claude (prepare), owner (go) | prepared; CHANGELOG date set at release; owner go open |
| Deploy only after the owner's explicit go | owner | open |
| The Python app is retired (after the deploy is confirmed working) | owner go | open |
| Done when: zero lost manual decisions and subscriptions in the comparison | | open: needs the owner's snapshot run |

## (b) Requirements

ARC-7 updates keep data, keys and every changed setting; the owner is told if an update must change one (update
notice: settings that 0.5.0 drops or needs — e.g. AUTH_TRUSTED_PROXIES with external sign-in, retired link
settings, env → config.json conversion) · IMP-9 after a catalogue reset the merge log is kept and used to recheck
earlier merges (verify on the migrated snapshot) · hard rules 1 (nothing manual lost), 11 (the snapshot, report and
config stay on the PC; never committed, never indexed into the knowledge DB), 12, 15, 16.

## (c) Release prep (fix at release)

`VERSION` 0.5.0 (3 copies) · C# version constant `0.4.3-migration` → `0.5.0` (`Directory.Build.props`,
`ApplicationRuntime.Version`, tests) · static asset `?v=` · reader extension version/code · compatibility list ·
README, docs (install, upgrade from 0.4.x, Authelia/NPM, compose 0.5.0) · CHANGELOG 0.5.0 section final ·
remove `tests/AutoSourcarr.Tests/Fixtures/matching/duplicate-pairs.user.json` (agreed: before cutover) ·
`check-project.sh` version alignment.

## (d) Phase end

Green probe incl. image · local checks · independent audit · push `conversion`, green CI · ZIP · knowledge records
`phase14-records.json` · PC sync · checkpoint · report. Then the gates: owner runs the migration and reviews parity →
owner go for merge to `main` + ghcr publish → owner deploys (or asks for help) → retire Python after confirmation.

## State 2026-11-05

(b) ARC-7 update notice, IMP-9 merge recheck in the parity report: done. (c) release prep: done. (d) probe c49ef8e green (1166/1166, image boot 3.0 s), local checks 1-9, independent audits (phase 14 twice + re-audit of phases 0-13), conversion CI 41929556047 green, ZIP, records, PC sync, checkpoint: done. Gates: open.
