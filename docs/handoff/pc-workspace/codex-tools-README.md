# AutoSourcarr research consultation

This developer-only check implements the user's requirement to use current and prior
research for **every** app update. It uses the existing sibling
`autosourcarr data/AutoSourcarr-knowledge.sqlite3`. It does not install anything, call
providers, access app databases or run inside the app. Keep this folder and `.work/`
outside application ZIPs.

From the workspace root, before editing application source:

```powershell
python -B codex-tools/research_gate.py prepare --id update-YYYYMMDD-topic --task "Actual authorized update" --scope autosourcarr/app/affected.py --query "ownership cancellation" --out .work/update-YYYYMMDD-topic/consultation.json
```

Scope paths are relative to the **outer application project** `autosourcarr/`, so the
backend path starts `autosourcarr/app/`. Repeat `--scope` for dependent modules, tests,
generated copies and release/version files. Declare the actual expected scope; do not
use a broad directory simply to bypass review. Existing manual changes are part of
the initial baseline and remain outside the update unless explicitly authorized.

The generated JSON contains mandatory IDs, path/term retrieval results, nonterminal
findings, corpus counts and twelve domain assessments. Read full records with the
existing tool, expanding searches for synonyms, linked records and older research:

```powershell
python -B autosourcarr/scripts/knowledge-db.py --db "../autosourcarr data/AutoSourcarr-knowledge.sqlite3" show RESEARCH-20261011-2H
python -B autosourcarr/scripts/knowledge-db.py --db "../autosourcarr data/AutoSourcarr-knowledge.sqlite3" search "relevant term" --limit 12
```

Fill the assessment:

- `consulted`: each read record ID and a concrete decision about its use and limits.
  Current research, the prior four-hour review, current release review and knowledge
  requirements are mandatory. Add relevant older findings, requirements and guides.
- `domains`: every domain needs `applies: true/false`, a concrete reason and consulted
  record IDs for applicable domains. Consider all research topics; apply the relevant
  evidence without expanding the user's requested scope.
- `freshness`: explicitly review every cited stale, missing or changed code pin.
  State what the current code supports, what remains historical and what is unknown.
  Do not rewrite old hashes merely to make them current. If newly consulted records
  add stale pins, run `refresh` to populate them before completing their decisions.

Record the pre-edit consultation:

```powershell
python -B codex-tools/research_gate.py record .work/update-YYYYMMDD-topic/consultation.json --phase before
```

The receipt is `UPDATE-update-YYYYMMDD-topic` in the same DB. A matching original
baseline is required at closeout. If research inputs change, refresh the same assessment:

```powershell
python -B codex-tools/research_gate.py refresh .work/update-YYYYMMDD-topic/consultation.json
```

Refresh preserves the original source baseline and clears decisions for changed
record content/state or a changed working hash behind a freshness decision. Re-read
affected evidence and complete the refreshed assessment.
It does not refresh historical source hashes or undo app changes. For a scope correction,
review the actual diff and update the declared scope and domain decisions explicitly.

After implementing and running the repository's required checks, fill `verification`
with check names, `outcome: "pass"` or `"unavailable"`, and exact fresh evidence. Failed
checks need repair; retain the failures in the project record. Unavailable Linux/image/
JVM/browser/live evidence needs an explicit explanation, never a pass claim. Fill
`limits` with the material remaining gap, then run:

```powershell
python -B codex-tools/research_gate.py record .work/update-YYYYMMDD-topic/consultation.json --phase after
python -B autosourcarr/scripts/knowledge-db.py --db "../autosourcarr data/AutoSourcarr-knowledge.sqlite3" check
```

The check rejects missing reviews/domains, changed research inputs, unreviewed stale
evidence, changes outside declared public scope and absent preflight/validation. It
preserves receipt revisions in `record_history`. `check` performs validation without
writing a receipt. It never indexes active envs, accounts, keys or app data; do not put
secrets into assessment prose.

This verifies a recorded consultation process. It cannot prove comprehension, prevent
edits by tools that ignore project guidance or replace repository tests/live checks.
No new app release/version/archive is needed for research-only Codex guidance changes.

Local behavior checks use an in-memory DB and confined synthetic files:

```powershell
python -B codex-tools/test_research_gate.py
```
