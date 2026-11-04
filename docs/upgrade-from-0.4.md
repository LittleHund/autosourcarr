# Upgrade from 0.4.x to 0.5.0

0.5.0 is the C#/.NET rewrite of AutoSourcarr. Your catalogue, manual decisions and settings move over, but the
upgrade is done on a **copy** on your PC first: one command migrates a snapshot of your 0.4.x data and writes a
parity report that proves nothing you decided was lost. Only when the report says PARITY OK do you deploy 0.5.0,
on **new** datasets filled with the migrated copy. Your 0.4.x datasets are never changed, so switching back is
always possible.

With two instances (`autosourcarr` and `private-autosourcarr`) do every step once per instance, each into its own
output folder.

## 1. Make a snapshot

Pick one of the two ways; A is preferred.

**A. Download a backup from 0.4.5 (preferred; 0.4.5 keeps running).** In 0.4.5 open *System → Backups*, create a
backup with a passphrase (12 characters or more) and download it (`<id>.ascbackup`). The backup holds both databases,
your settings (`/config`) and the retained instance files, and records the paths 0.4.5 really used (`DATA_DIR`,
`DATABASE_DIR`). It leaves out caches (cover cache, logs, downloads).

**B. Copy the folders (also for 0.4.0–0.4.4).** **Stop the 0.4.x app first** (TrueNAS: stop the app) so the
databases are complete, then copy to your PC, into one folder, keeping timestamps:

```
snapshot\
  data\      <- the dataset mounted at /data   (e.g. .../autosourcarr/autosourcarr-data)
  config\    <- the dataset mounted at /config (e.g. .../autosourcarr/config, with autosourcarr.env)
  database\  <- only if DATABASE_DIR pointed outside DATA_DIR: that folder
```

- Windows: `robocopy \\NAS\share\autosourcarr\autosourcarr-data D:\snapshots\snapshot\data /E /COPY:DAT /DCOPY:T`
  (the same for `config`).
- Linux/macOS: `cp -a /mnt/.../autosourcarr-data ~/snapshot/data` (or `rsync -a`).

Start 0.4.x again afterwards if you like; the snapshot is what counts. The tool refuses a folder that looks live (a
database journal written in the last 10 minutes, a database another program holds open, or `/data`/`/config` inside
a container), so always run it on the copy.

Pass your 0.4.x Compose file with `-Compose <file>` (PowerShell) / `--compose <file>` so its `environment:` values are
compared too (and `DATA_DIR`/`DATABASE_DIR`, if set there, apply). The tool uses the service named by `-Service`/`--service
<name>`, the only service, or the one service whose `env_file` is `autosourcarr.env` (the 0.4.5 files: autosourcarr +
suwayomi need no `--service`). With two AutoSourcarr instances, pass `--service`. It refuses that service if a value
comes from the host shell (`- KEY`), uses interpolation (`${...}`), anchors, merge keys or block/flow styles; other
services do not matter. The report says where the paths came from and which service was compared.

Keep the snapshot and the output folder outside OneDrive (and other cloud-sync folders): synced placeholders can make
files unreadable mid-run. The tool refuses to guess: `DATABASE_DIR` outside
`DATA_DIR` needs the `database\` folder, and databases in both `data\` and `database\` are refused as ambiguous.

## 2. Run the migration

Needs the .NET 10 SDK on the PC (`dotnet --version` shows 10.x) and this project folder. No Docker and no access to
your server are needed; the tool builds itself the first time. Use the checkout of the same 0.5.0 release you will
deploy, so the migrations and one-time steps are the ones that image runs.

Windows (PowerShell, from the project folder):

```powershell
.\scripts\migrate-from-0.4.ps1 D:\snapshots\6f1c...e2.ascbackup D:\autosourcarr-0.5.0
# or, for copied folders (optionally with -Compose D:\snapshots\docker-compose.yml -Service autosourcarr):
.\scripts\migrate-from-0.4.ps1 D:\snapshots\snapshot D:\autosourcarr-0.5.0
```

Linux/macOS: `scripts/migrate-from-0.4.sh <backup file | snapshot folder> <output folder> [--compose <file> [--service <name>]]`.

For a backup the passphrase is asked (nothing is shown while you type; it is never stored or logged). The output
folder must be new or empty. Directly, without the wrapper:
`dotnet src/AutoSourcarr.Api/bin/Release/net10.0/AutoSourcarr.Api.dll --migrate-0.4 <input> <output>`.

What the tool does, on the copy only:

1. copies the snapshot to `<output>/original-0.4/` (the input is only read) and from there to `<output>/data` and
   `<output>/config`;
2. converts `autosourcarr.env` (+ `source-host.env`) to `config.json` + `config.key`, exactly as 0.5.0 does at its
   first start; the env files stay next to it and a copy goes to `config/backups/config-conversion-…`;
3. opens 0.5.0 once **offline** on the copy: the update guard takes its automatic backup, the database migrations run,
   and the one-time first-start steps run: interrupted chapter changes, merge-safety repair, startup catalogue
   maintenance, main-title consensus (their versions are stored, so the real first start skips them; a one-time step
   that would run with a part this build does not have is MIGRATION FAILED instead of silently storing its marker).
   The startup duplicate sweep is **not** run offline: an automatic merge is a change your 0.4.x data never had, so it
   is left to the first real start (as at every 0.4.5 start). No source engine, no Suwayomi and no internet are used;
4. compares the original 0.4.x databases (opened read-only) with the migrated ones and writes the report.

**Not covered by the verdict** (these need the network and run in the background at the real first start; the report
lists them): the startup duplicate sweep with its identity and cover hydration (set `BACKGROUND_DEDUP_ON_STARTUP=false`
before the first start if you want to review duplicates by hand first), Subbed orphan aliases (needs the main
Suwayomi), metadata repair, the additional-record scan, feed refresh and crawling, translation enrichment, orphan
identity repair, resuming interrupted imports and reset-hint replays, and the tag index rebuild.

Exit code 0 = PARITY OK, 1 = PARITY FAILED or MIGRATION FAILED (see the report), 2 = refused input, 3 = the run
stopped half-way (`<output>/MIGRATION-INCOMPLETE.txt` says why, `migration-error.log` holds the full error; no report is
written). While a run is going, and after one that stopped, `MIGRATION-INCOMPLETE.txt` is also in the output's `data/`
and `config/` folders: never mount a folder that has it. The input is never changed.

## 3. Read the report

`<output>/parity-report.md` (and the same data as `parity-report.json`). The first line is the verdict:

```
PARITY OK: 0 lost manual decisions, 0 lost subscriptions
```

Anything else means **do not deploy**: `PARITY FAILED: …` names the kinds that lost or changed something;
`MIGRATION FAILED: …` means the update itself did not complete (0.5.0 would start in safe mode, the update status was
unexpected, or a required step failed) — the comparison is then shown for diagnosis only. Sections:

| Section | What it shows |
| --- | --- |
| Manual decisions and subscriptions | Per kind: rows in 0.4.x and 0.5.0, missing, changed and added items (with the work's title). Rows are compared **whole**: every column except the key and pure timestamps; the few other columns a kind leaves out are named in the table with the reason (for example source metadata fetched from the source). History tables compare every column as stored. NULL and '' differ, and so do 1 and 1.0. Entry ids are compared as the entry each side resolves to through its own merge aliases, and the number of rows per key must stay the same. Kinds that keep records out (deleted records, separate rules, reviews, rejections) also fail on rows the migration added. Subscriptions: the Subbed view of your main-Suwayomi library (each subscribed id must still open the same entry) and monitored works. Manual: titles and aliases, preferred titles, title overrides and scopes, display metadata you chose or locked, manual/locked records, every earlier merge, interrupted merges/splits (gone only when completed), manual groups and bindings, separate entries (the record must not be attached to the entry again), duplicate reviews ("different works" must stay two entries), mixed-entry and tag reviews, locked identities, deleted records, shared-catalogue bindings and decisions, source choices (incl. your official/unofficial choice) and groups, gallery policies, matching policies/presets, review workbench actions, serving profiles and policies, UI preferences, the app state in the settings table. Imports: runs, per-title decisions and suggestions, rejections, remembered manual choices. Chapter provenance: served origins, origin history, serving choices and upgrade history resolved to (entry, chapter key, source chapter) on both sides, so a rewritten or deleted chapter or candidate fails; rejected chapter versions. History tables and the shared-catalogue identity tables: row by row. |
| Merged by 0.5.0 | Entries the migration merged into another. A merge that involves a subscribed or monitored entry or any entry with a manual decision, or that fails the recheck, is FAILED; others are listed. |
| Entries | 0.4.x entries that no longer exist (any = FAILED), and entries that had source records and have none afterwards (any = FAILED). |
| Settings | Every `autosourcarr.env` / `source-host.env` key → its `config.json` value: kept, kept-but-unused, dropped (and why) or lost. Secrets (tokens, passwords, keys) are compared by presence and equality only and never printed; in URLs the credentials, query values, fragments and token-like path parts are masked. |
| Update notice (ARC-7) | What the update had to change, drop or now needs: the env → config.json move and the `env_file` line, retired Komf/AI-dashboard links, settings 0.5.0 would refuse, the new sign-in (see step 4), the `/api/reader/repo/` bypass, Compose variables. |
| IMP-9 | The merge log (`decision_log`) is present, and a dry run rechecks every earlier merge against the current rules: manual merges always hold; automatic merges hold, would not hold, or have no stored snapshot. Nothing is applied. |
| Files, keys and accounts | Every file of the snapshot (names only): a missing or changed file is FAILED — last-working repair packages, source and repair state, setup state, import uploads, reset hints, keys — except an explicit allowlist (caches and logs, the update guard's state file), each shown with its reason. |
| Table coverage | Every one of the 86 tables 0.4.5 creates (and any other table found): compared as which kind, counted, or counted only because it is rebuildable (with the reason). A 0.4.x table missing after the migration is FAILED. |
| Row counts | Every table in both versions. |
| Migration run | Where DATA_DIR/DATABASE_DIR came from, what the conversion and the offline start did. |

Older snapshots (0.4.0–0.4.3, and the 0.3.27 schema the tests cover): a kind compares the columns the old table has;
a kind whose query the old schema cannot answer is fine only while that table is empty, otherwise it is FAILED.

The report contains your manga titles and setting names, so keep it on your PC; it never contains secret values.

## 4. Deploy 0.5.0 (only after your go)

Nothing in this guide touches your server until you decide to. When the report says PARITY OK:

1. Stop the 0.4.x app. Keep its datasets exactly as they are (they are your rollback).
2. Create **new** datasets for 0.5.0 (`autosourcarr-data-0.5.0` and `config-0.5.0`), copy `<output>/data` and
   `<output>/config` into them with timestamps (and `<output>/database` to the dataset you mount at `DATABASE_DIR`, if
   the report names one), and give them to 568:568. Copy the whole folders (`config.json` and `config.key` belong
   together). `original-0.4` stays on the PC.
3. Use `docker-compose.0.5.0.yml`; its volumes already point at the `-0.5.0` datasets (adjust the pool path). Never
   mount the 0.4.x datasets there. Remove the old `env_file:` line; values under `environment:` override
   `config.json` (settings saved in the app still win), so keep only the ones you mean.
4. Sign-in. 0.4.x relied on Authelia alone; 0.5.0 has its own sign-in (`AUTH_METHOD=forms` by default: you create the
   owner account at the first visit). To keep Authelia as the only sign-in set `AUTH_METHOD=external` and
   `AUTH_TRUSTED_PROXIES` to your reverse proxy's address (Settings or Compose) — without it identity headers are
   refused and a pinned notice says so.
5. Reverse proxy / Authelia (`docs/reverse-proxy/authelia.md`, `nginx-proxy-manager.md`): keep the unchanged
   `^/api/reader/v1([/?].*)?$` bypass and add the GET/HEAD-only bypass for the reader extension repository under
   `/api/reader/repo/`. Never bypass `/`, `/api/v1/` or `/api/reader/` as a whole.
6. Start 0.5.0. The first start finds the data already migrated; it runs the network work listed above in the
   background. Settings shows the update notice until you dismiss it.
7. Extensions: 0.5.0 runs its own source host (`/data/source-host`); reinstall your sources from your repositories under
   Sources if they are not listed. Your main Suwayomi (optional in 0.5.0) can be connected under Connections → Suwayomi.

## 5. Roll back

0.5.0 runs on **new** datasets filled by the migration tool; the 0.4.x datasets stay untouched. To go back, stop
0.5.0 and start 0.4.5 with its old Compose file on the original 0.4.x datasets. Never point 0.4.x at the 0.5.0
folders: the 0.5.0 database migrations are forward-only, and 0.4.5 never reads `config.json` (it reads only
`autosourcarr.env` and `source-host.env`), so everything changed in 0.5.0 would silently be missing. Decisions made in
0.5.0 after the switch are not in the 0.4.x data; if you want them, export a shared catalogue from 0.5.0 first.

## Limits

- The subscriptions themselves live in your main Suwayomi library; the tool compares AutoSourcarr's saved view of
  them (`subbed_display_snapshot_v1`) and the monitored works, not the Suwayomi database.
- Reader progress is not stored by 0.4.x (Mihon keeps it), so there is nothing to compare.
- With `--compose`, the chosen service's plain `environment:` values are compared (a value 0.5.0 refuses fails the
  verdict); `env_file:` entries other than `autosourcarr.env` are not read. Without `--compose`, compare them yourself
  with `docker-compose.0.5.0.yml`.
- The live-instance check is best effort; a folder mounted from the server cannot be checked fully.
- The automatic old-file cleanup of a version update does not run during the offline migration; start it from the
  cleanup in System after the first start if you like.
