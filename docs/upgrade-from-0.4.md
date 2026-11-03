# Upgrade from 0.4.x to 0.5.0

0.5.0 is the C#/.NET rewrite of AutoSourcarr. Your catalogue, manual decisions and settings move over, but the
upgrade is done on a **copy** on your PC first: one command migrates a snapshot of your 0.4.x data and writes a
parity report that proves nothing you decided was lost. Only when you are happy with the report do you deploy 0.5.0,
on the migrated copy. Your 0.4.x folders are never changed, so switching back is always possible.

With two instances (`autosourcarr` and `private-autosourcarr`) do every step once per instance, each into its own
output folder.

## 1. Make a snapshot

Pick one of the two ways.

**A. Download a backup from 0.4.5 (easiest, 0.4.5 keeps running).** In 0.4.5 open *System → Backups*, create a
backup with a passphrase (12 characters or more) and download it (`<id>.ascbackup`). The backup holds both databases,
your settings (`/config`) and the retained instance files; it leaves out caches (cover cache, logs, downloads).

**B. Copy the folders (also for 0.4.0–0.4.4).** Stop the 0.4.x container first (TrueNAS: stop the app), then copy
to your PC, into one folder:

```
snapshot\
  data\      <- the dataset mounted at /data   (e.g. .../autosourcarr/autosourcarr-data)
  config\    <- the dataset mounted at /config (e.g. .../autosourcarr/config, with autosourcarr.env)
  database\  <- only if DATABASE_DIR pointed outside /data: that folder
```

Start 0.4.x again afterwards if you like; the snapshot is what counts. The tool refuses a folder that looks live (a
database journal written in the last 10 minutes, a database another program holds open, or `/data`/`/config` inside
a container), so always run it on the copy.

## 2. Run the migration

Needs the .NET 10 SDK on the PC (`dotnet --version` shows 10.x) and this project folder. No Docker and no access to
your server are needed; the tool builds itself the first time. Use the checkout of the same 0.5.0 release you will
deploy, so the migrations and one-time steps are the ones that image runs.

Windows (PowerShell, from the project folder):

```powershell
.\scripts\migrate-from-0.4.ps1 D:\snapshots\6f1c...e2.ascbackup D:\autosourcarr-0.5.0
# or, for copied folders:
.\scripts\migrate-from-0.4.ps1 D:\snapshots\snapshot D:\autosourcarr-0.5.0
```

Linux/macOS: `scripts/migrate-from-0.4.sh <backup file | snapshot folder> <output folder>`.

For a backup the passphrase is asked (nothing is shown while you type; it is never stored or logged). The output
folder must be new or empty. Directly, without the wrapper:
`dotnet src/AutoSourcarr.Api/bin/Release/net10.0/AutoSourcarr.Api.dll --migrate-0.4 <input> <output>`.

What the tool does, on the copy only:

1. copies the snapshot to `<output>/original-0.4/` (the input is only read) and from there to `<output>/data` and
   `<output>/config`;
2. converts `autosourcarr.env` (+ `source-host.env`) to `config.json` + `config.key`, exactly as 0.5.0 does at its
   first start; the env files stay next to it and a copy goes to `config/backups/config-conversion-…`;
3. opens 0.5.0 once **offline** on the copy: the update guard takes its automatic backup, the database migrations run,
   and the one-time startup steps that need only local data run (merge-safety repair, startup catalogue maintenance,
   main-title consensus, interrupted chapter changes). No source engine, no Suwayomi and no internet are used; work
   that needs them (feed refresh, metadata lookups, startup dedup, Subbed orphan aliases) waits for the real first start;
4. compares the original 0.4.x databases (opened read-only) with the migrated ones and writes the report.

Exit code 0 = parity OK, 1 = something was lost (see the report), 2 = refused input, 3 = error. The input is never
changed in any case.

## 3. Read the report

`<output>/parity-report.md` (and the same data as `parity-report.json`). The first line is the verdict:

```
PARITY OK: 0 lost manual decisions, 0 lost subscriptions
```

or `PARITY FAILED: …` with the kinds that lost something. Sections:

| Section | What it shows |
| --- | --- |
| Manual decisions and subscriptions | Per kind: rows in 0.4.x and 0.5.0, missing and changed items (with the work's title). Subscriptions: the Subbed view of your main-Suwayomi library (each subscribed id must still open an entry) and monitored works. Manual: your titles and aliases, preferred titles, title overrides and scopes, manual/locked records, manual groups and bindings, separate entries, duplicate reviews ("different works"), mixed-entry and tag reviews, locked identities, deleted records, shared-catalogue bindings and decisions, source choices and groups, matching policies/presets, serving profiles, UI preferences. Imports: runs, per-title decisions, rejections, remembered manual choices. Chapter provenance: served origins, origin history, rejected chapter versions, hidden chapters, serving choices. Entry ids are compared through merge aliases. |
| Entries | 0.4.x entry ids that no longer lead to an entry (should be none). |
| Settings | Every `autosourcarr.env` / `source-host.env` key → its `config.json` value: kept, kept-but-unused, dropped (and why) or lost. Secrets (tokens, passwords, keys) are compared by presence and equality only and never printed; credentials inside URLs are masked. |
| Update notice (ARC-7) | What the update had to change, drop or now needs: the env → config.json move and the `env_file` line, retired Komf/AI-dashboard links, settings 0.5.0 would refuse, the new sign-in (see step 4), the `/api/reader/repo/` bypass, Compose variables. |
| IMP-9 | The merge log (`decision_log`) is present, and a dry run rechecks every earlier merge against the current rules: manual merges always hold; automatic merges hold, would not hold, or have no stored snapshot. Nothing is applied. |
| Files, keys and accounts | Files of the snapshot that are unchanged, changed or missing in the output (names only), key files by presence. |
| Row counts | Every table in both versions. Counts may move for rebuildable data; the manual kinds above are what must not. |
| Migration run | What the conversion and the offline start did. |

The report contains your manga titles and setting names, so keep it on your PC; it never contains secret values.

## 4. Deploy 0.5.0 (only after your go)

Nothing in this guide touches your server until you decide to. When the report says PARITY OK:

1. Stop the 0.4.x app. Keep its datasets exactly as they are (they are your rollback).
2. Create **new** datasets for 0.5.0 (e.g. `autosourcarr-data-0.5.0` and `config-0.5.0`), copy `<output>/data` and
   `<output>/config` into them (and `<output>/database` to the folder named in the report, if any), and give them to
   568:568. Copy the whole folders (`config.json` and `config.key` belong together). `original-0.4` stays on the PC.
3. Use `docker-compose.0.5.0.yml` and point its volumes at the new datasets (`…-data-0.5.0:/data`,
   `config-0.5.0:/config`). Remove the old `env_file:` line; keep Compose `environment:` values only if you still want
   them to override `config.json`.
4. Sign-in. 0.4.x relied on Authelia alone; 0.5.0 has its own sign-in (`AUTH_METHOD=forms` by default: you create the
   owner account at the first visit). To keep Authelia as the only sign-in set `AUTH_METHOD=external` and
   `AUTH_TRUSTED_PROXIES` to your reverse proxy's address (Settings or Compose) — without it identity headers are
   refused and a pinned notice says so.
5. Reverse proxy / Authelia (`docs/reverse-proxy/authelia.md`, `nginx-proxy-manager.md`): keep the unchanged
   `^/api/reader/v1([/?].*)?$` bypass and add the GET/HEAD-only bypass for the reader extension repository under
   `/api/reader/repo/`. Never bypass `/`, `/api/v1/` or `/api/reader/` as a whole.
6. Start 0.5.0. The first start finds the data already migrated; it runs the network work that was left out
   (Subbed orphan aliases, feeds, metadata) in the background. Settings shows the update notice until you dismiss it.
7. Extensions: 0.5.0 runs its own source host (`/data/source-host`); reinstall your sources from your repositories under
   Sources if they are not listed. Your main Suwayomi (optional in 0.5.0) can be connected under Connections → Suwayomi.

## 5. Roll back

Your 0.4.x datasets were never changed. To switch back: stop 0.5.0 and start 0.4.5 with its old Compose file on the
original 0.4.x datasets. Do **not** point 0.4.x at the 0.5.0 folders: 0.4.x cannot read a `config.json` that contains
`connections`, and the 0.5.0 database migrations are forward-only. Decisions you made in 0.5.0 after the switch are
not in the 0.4.x data; if you want them, export a shared catalogue from 0.5.0 first.

## Limits

- The subscriptions themselves live in your main Suwayomi library; the tool compares AutoSourcarr's saved view of
  them (`subbed_display_snapshot_v1`) and the monitored works, not the Suwayomi database.
- Reader progress is not stored by 0.4.x (Mihon keeps it), so there is nothing to compare.
- Compose `environment:` values are not part of a snapshot; compare them yourself with `docker-compose.0.5.0.yml`.
- The live-instance check is best effort; a folder mounted from the server cannot be checked fully.
- The automatic old-file cleanup of a version update does not run during the offline migration; start it from the
  cleanup in System after the first start if you like.
