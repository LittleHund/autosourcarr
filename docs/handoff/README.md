# Handoff bundle (snapshot 2026-11-01)

Everything a new agent needs that used to live only in Claude Docs, the Claude Project, the
cloud session's scratch folder or the user's PC. These are **copies taken on 2026-11-01**; the
originals may change later. Start with `/CLAUDE.md` at the repo root.

| File | Copied from | What it is |
| --- | --- | --- |
| `conversion-plan.md` | Claude Docs "AutoSourcarr conversion plan" (artifact `4cf80085-d5a6-40e1-9c4e-c40a0e4fbbba`) | The phase plan (0–14 to cutover, 15–17 for 0.6.0): per-phase text, "Done when", plan-hours. |
| `requirements.md` | Claude Docs "AutoSourcarr requirements", tab "Requirements" (rev 190) | Current agreed requirements by area (ARC … DEV), status, release split (section 18). |
| `requirements-round1-history.md` | Same doc, tab "Round 1 (history)" | First version (old IDs D1, S1, M1 …); the "Was" column in `requirements.md` points here. |
| `project-docs/checkpoint.md` | Claude Project `claude/checkpoint.md` | Time table, per-phase checklist results, carried items, open user decisions (state at phase 9 end). |
| `project-docs/requirements-round2-user-notes.md` | Claude Project | The user's own words for every round-1 requirement (source of the round-2 changes). |
| `project-docs/requirements-changes-2026-10-29.md` | Claude Project | Research edits and PROP-1..4 (accepted, see `requirements.md` §18). |
| `project-docs/version-check-2026-10-31.md` | Claude Project | Pinned versions (.NET 10.0.14, Temurin 25.0.5, Suwayomi v2.4.2366, Mihon 0.21.x, FluentMigrator 8.0.1) and API limits (AniList 30/min, MangaDex at-home 40/min). |
| `project-docs/review-2026-10-26.md` | Claude Project | Review of 0.4.3/0.4.4 and the knowledge DB. |
| `project-docs/CLAUDE-project-2026-10-26-OUTDATED.md` | Claude Project `claude/CLAUDE.md` | Old guidance ("paused at stage 26"). Superseded by `/CLAUDE.md`; kept for history. |
| `briefs/phase10-common.md`, `briefs/phase10-integration.md` | Session scratch | Phase 10 rules for agents and the **full wiring brief** (none of it is applied yet, see `/CLAUDE.md`). |
| `briefs/phase8-*.md`, `briefs/phase9-*.md`, `briefs/phase6-plan.md` | Session scratch | Earlier phase briefs; show the brief format that worked. |
| `briefs/agent-briefs/` | Session sub-agent logs | Exact prompts given to the phase 10 area agents (10A–10D), the integration agent, the audit template and a fix agent, with the final report where it was preserved. |
| `briefs/checkpoint-history/` | Session scratch | Checkpoint versions at the end of phases 2, 3, 6, 7, 8, 9. |
| `knowledge-records/phase0..9-records.json` | Session outputs | Records imported into the knowledge DB at each phase end (format for the next phases). |
| `pc-workspace/AGENTS.md` | PC `F:\claude projects\autosourcarr\AGENTS.md` | Codex guidance (roles, skills, preserve-behaviour rules). NAS path redacted to `<nas-app-root>`. |
| `pc-workspace/PROJECT_CONTEXT.md` | PC workspace root | Long history 0.1 → 0.4.x (142 KB). Read the section you need. Same redaction. |
| `pc-workspace/codex-tools-README.md` | PC `codex-tools/README.md` | The Codex research-gate tool (PC only). |

## Not copied (and why)

| Item | Where it is | Why not here |
| --- | --- | --- |
| `AutoSourcarr-knowledge.sqlite3` (≈237 MB) | PC `F:\claude projects\autosourcarr data\` | Too large and holds indexed code/evidence; reachable only through the PC link. The JSON records above are its phase-0..9 additions. |
| Release and phase ZIPs, full backup | PC `F:\claude projects\autosourcarr data\` | Large; rebuildable with `git archive`. |
| `codex-tools/*.py`, `kb-maintenance.py`, `cleanup-autosourcarr.ps1` | PC workspace root | PC tools, not app code. |
| Env files, keys, accounts, runtime databases, user config | NAS / PC | Never pushed (hard rule 11). |
