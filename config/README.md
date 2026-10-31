# Persistent environment configuration

The live TrueNAS runtime values are intentionally stored **outside** the replaceable `project/` release directory:

```text
autosourcarr/config/autosourcarr.env
autosourcarr/config/source-host.env
autosourcarr/config/.extension-signing/
```

The `project/config/*.env.example` files are release templates only. Run `scripts/init-truenas-config.sh` after copying a release: it migrates legacy `project/config` values once, adds newly introduced keys without overwriting your values, generates a reader API token when blank, and preserves the Android signing identity. Replacing all files under `project/` therefore does not reset URLs, API credentials, source-host settings, or APK signing.

Compose paths:

```text
/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/config/autosourcarr.env
/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/config/source-host.env
```

You may replace the complete `project/` directory on an upgrade. Do not delete the sibling `autosourcarr/config/` directory.
## TrueNAS Custom App / Compose behavior

`env_file:` is resolved by Docker Compose on the TrueNAS host before a container is
created. For the provided TrueNAS YAML, keep these persistent files at exactly:

- `/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/config/autosourcarr.env`
- `/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/config/source-host.env`

The files use plain `KEY=value` lines (no `export` statements and no YAML quoting).
They provide container runtime environment variables; Compose-variable substitution
inside the YAML is intentionally not dependent on them.



## Settings UI

The companion app exposes these variables through the cogwheel on the far right of the navigation bar. The AutoSourcarr container must have the whole persistent `autosourcarr/config` directory mounted read/write at `/config`; mounting only the individual env files is not sufficient because saves are atomic file replacements.

The supplied TrueNAS Compose files use:

```text
/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/config:/config
```

Saving edits the host-backed file immediately. The running process is intentionally not partially hot-reconfigured: recreate the Custom App for autosourcarr.env changes. In integrated mode the source child reads /config/source-host.env itself on each restart. The source engine enforces loopback port4567, no direct authentication, WebUI disabled and KCEF enabled; AutoSourcarr guards its WebView bridge. SOURCE_SUWAYOMI_URL is used only in external mode.

The reusable Compose helper is `compose-env-file.yml`. It deliberately has no release number in its filename.

## Optional main-Suwayomi download size

`MAIN_SUWAYOMI_DOWNLOADS_PATH` is set to `/mnt/manga-downloads` for this deployment. TrueNAS mounts `/mnt/hdd/storage/komga/manga` there read-only; the path is used only for the Status downloaded-size metric.


## Android reader API token

`AUTOSOURCARR_READER_API_TOKEN` is both a runtime backend secret and a build-time Android extension input. The APK sends it as `X-AutoSourcarr-API-Key` only to `/api/reader/v1/`. Configure Authelia to `bypass` only that reader prefix; AutoSourcarr itself validates the token there. The normal UI and `/api/v1/` stay behind the existing Authelia policy.

`AUTOSOURCARR_AUTHELIA_BASIC_USERNAME` and `AUTOSOURCARR_AUTHELIA_BASIC_PASSWORD` are deprecated compatibility fields and are ignored by new Android builds.

The builder uses the sibling `autosourcarr/config/.extension-signing/` directory. Preserve and back it up so future APKs keep the same Android signature.


## Database storage

`DATABASE_DIR=/data` keeps the two SQLite databases with the normal runtime data. To move them to another TrueNAS dataset/drive, mount that dataset into the container first (for example at `/database`) and use **Settings → Configuration → Database storage → Move databases**. The move UI copies and verifies the existing databases and performs a lossless live cut-over; do not point `DATABASE_DIR` manually at an empty directory. A separately configured database path fails closed if its mount/database files disappear.

## Continuous catalog discovery

`CONTINUOUS_CATALOG_DISCOVERY=true` keeps walking enabled source Latest/Popular feeds in the low-priority source lane. The cursor is persisted in the catalog, Subbed/import recovery outranks it, and `CONTINUOUS_CATALOG_BACKLOG_LIMIT` prevents the crawler from creating an unbounded enrichment queue.
