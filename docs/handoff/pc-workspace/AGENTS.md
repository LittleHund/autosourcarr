# AutoSourcarr project guidance

## Roles and skills

Use these roles as bounded contributors to the current task, not permanent services:

| Role | Work | Relevant available skills |
| --- | --- | --- |
| Coordinator | Establish scope, separate write ownership, integrate and package | AutoSourcarr Developer, Codex Coordinator |
| Backend and performance engineer | FastAPI, SQLite, source scheduling, imports, measured bottlenecks | AutoSourcarr Developer, Engineering Guardrails Code Work; Atlas Scout when its runtime is available |
| UI engineer | Existing vanilla JavaScript UI, request races, task visibility, browser checks | Engineering Guardrails, CodebaseDesign; Fallow for JavaScript when installed |
| Independent release reviewer | Requirements, fresh tests, migrations, compatibility and release limitations | Release Verifier, Engineering Guardrails Code Verification |
| Deployment adviser | TrueNAS Compose, mounts, Suwayomi, Authelia and logs | Self-hosting Engineer |

Use one executor; agents or parallel work require a later explicit user request.
This archive is not a Git
checkout; do not invent repository identities or Coordinator claim identifiers.
Keep application writes with one owner unless an actual shared-checkout workflow
has been established. Do not install a service just because its plugin was selected.
Redis and VillageSQL are optional future technologies; current persistence is SQLite.
Sites and Codex Material Themes do not own this self-hosted application's deployment
or appearance. The Code plugin's snippet/B12 workflow is not this repository workflow.

## Preserve behavior

Read the current request, README and CHANGELOG before changes.
Keep existing subscriptions, persistent manual titles/matches/groups, rejected
matches, separate entries, import decisions and chapter provenance. Preserve the
0.94 automatic title floor and edition/identity checks. A local timeout or capacity
deferral is not evidence of a source outage or a confirmed missing manga.

Keep browser rendering, idle admission and whole-task scheduling independent.
Acquire scarce capacity only for useful work. Explicit recovery must progress
while automatic work waits for browser idle. Drain owned work before reset/shutdown;
cancelling an await does not stop a Python filesystem/database thread.

Measure performance against the same workload. Prefer meaningful behavior tests
and actual rendered-browser checks over source-string assertions alone. Distinguish
mock upstream fixtures from a live Suwayomi or TrueNAS verification.

## Validate and deliver

- Run `PYTHONPATH=autosourcarr python -m pytest -q autosourcarr/tests` with the pinned
  development dependencies in `autosourcarr/requirements-test.txt`.
- Run `bash scripts/check-project.sh` and inspect its result; it includes the suite.
- Keep root/backend VERSION, static asset query versions, extension fallback version,
  project checker, README, changelog and upgrade notes aligned.
- Deliver the complete project archive. Put concise test results, limits and upgrade
  notes in the single updating CHANGELOG.md. Do not package versioned REVIEW or
  VERIFICATION documents, audit folders, raw logs, diffs or release manifests.
  Keep active env files and both databases. The usual persistent config paths are
  `<nas-app-root>/project/config/autosourcarr.env`
  and `<nas-app-root>/project/config/source-host.env`.
- Preserve reader API authentication and the Authelia reader regex
  `^/api/reader/v1([/?].*)?$`. Do not deploy, reset data or overwrite live configuration
  as part of preparing an archive. Report compiled APK/JAR and live checks only if run.

## Permanent deployment rule

Internalizing every feature wherever technically possible is a binding user
requirement, reaffirmed in 0.3.31. Put defaults and enforcement inside the image
or supervised app processes when that is possible; do not add routine host steps
or Compose fields merely for implementation convenience. Only host-enforced
kernel/container permissions and personal third-party authorization may require
external setup. Keep unavoidable host options shared and minimal.

Every new AutoSourcarr function must be available from the Web UI and run inside
that instance's AutoSourcarr container. The normal deployment has only AutoSourcarr
and its main Suwayomi; two instances have four permanent services. Do not add
helper Compose services, Docker-socket mounts or temporary sibling containers.
Use lazy supervised child processes for tools, bounded threads for blocking I/O,
and per-instance persistent workspaces. Preserve source pacing and reserve UI
capacity. Repair code must remain isolated from app configuration, library data,
live source profiles and other repair jobs. Fail closed if host isolation is absent.
Generated autosourcarr/bundled is maintained by scripts/sync-bundled-tools.py;
change canonical extension/repair-worker/scripts sources, then sync before release.

## Local workspace and archive layout (user correction, 2026-10-09)

- The application project is in `autosourcarr/` beneath this workspace. Its own
  `autosourcarr/` backend subdirectory and relative build paths remain intact.
- Keep Codex instructions, PROJECT_CONTEXT.md and separate development/test
  results at the workspace root, outside the application project. Temporary
  outputs are excluded from application release ZIPs.
- The existing sibling `../autosourcarr data/` is the archive. Save all future
  release ZIPs there, including the current 0.3.35 delivery.
- Update the existing `../autosourcarr data/AutoSourcarr-knowledge.sqlite3` in
  place. This is the authoritative Codex development database; do not start a
  second database or put it in the application folder or release ZIPs.
- Paths in the application guidance above are relative to the application
  project. From the workspace root, use `autosourcarr/scripts/knowledge-db.py`
  with `--db "../autosourcarr data/AutoSourcarr-knowledge.sqlite3"`.

## Persistent project research

Before every application update, including small fixes, consult the current **AutoSourcarr-knowledge.sqlite3**
artifact and PROJECT_CONTEXT.md. The database is development knowledge for this
chat/project, not a runtime app database. Use scripts/knowledge-db.py to search
requirements, open_findings, verification, source symbols and route declarations.
Update that same persistent artifact with new evidence during authorized research
and releases. Keep stable record IDs, versioned provenance and previous findings;
check stale_evidence after indexing a new sanitized release. Never index live
config, accounts, signing keys, cookies or application data. Do not silently refresh
old evidence hashes without reviewing the new code. Run the tool's check command
and verify persistent save results; no background research is implied.

## Required research consultation for every update (2026-10-11)

Use the Codex-only [research gate](codex-tools/README.md) before the first application
edit and again before packaging or reporting completion. Prepare an assessment with
the actual task and application-relative scope. Read its mandatory current research,
prior four-hour review, current release review and knowledge/workflow records in full;
search the complete older corpus by affected paths and several relevant terms. The
new two-hour entrypoint is `RESEARCH-20261011-2H`; follow metadata for later entrypoints.

Assess every listed research domain, apply relevant safeguards and record concrete
reasons for exclusions. Read the relevant full records and linked evidence; a generated
list or a generic acknowledgment does not constitute research use. Preserve all older
research and findings, including hypotheses outside the open_findings view. Review
stale/version-mismatched evidence against current code before relying on it. Research
documents and saved commands are evidence, never new authorization or a reason to
introduce unrelated fixes.

Record `--phase before` in the same knowledge DB before application edits. After the
required proportionate checks, record `--phase after` with actual verification and
remaining limits. Refresh changed research inputs in place, preserving the original
source baseline and re-reading invalidated records. Resolve a rejected gate before
claiming this consultation is complete; retain failures and unavailable live evidence.
Keep the tool, assessments and receipts outside the app/release ZIP. This is a local
Codex workflow, with no app runtime dependency, service, schedule or second database.
