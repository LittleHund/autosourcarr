# Installation

[Back to the overview](../README.md) · [User guide](USER_GUIDE.md)

These instructions describe **0.4.4**, which builds a local Docker image from the
complete project. The approved C#/.NET and prebuilt-image migration is still in
progress. A published AutoSourcarr image is not yet available from this project.

## Docker Compose

You need Docker Compose and persistent storage. The standard stack contains
AutoSourcarr plus the main Suwayomi server used for your library. AutoSourcarr's
source engine, reader builder and repair tools run inside its own container.

1. Extract the complete project and open its directory containing `VERSION` and
   [docker-compose.yml](../docker-compose.yml).
2. Copy [.env.example](../.env.example) to `.env`. Set `DATA_ROOT` and
   `AUTOSOURCARR_CONFIG_ROOT` to your persistent host directories.
3. For a **new installation**, copy
   [config/autosourcarr.env.example](../config/autosourcarr.env.example) and
   [config/source-host.env.example](../config/source-host.env.example) into that
   config directory, removing `.example` from their names. Make the retained
   config/data/source directories writable by UID/GID **568**. For an existing
   installation, retain its active files and mounts.
4. Set the reader and main-server URLs in `autosourcarr.env`. The shared-network
   defaults are `AUTOSOURCARR_INTERNAL_URL=http://autosourcarr:8282` and
   `MAIN_SUWAYOMI_URL=http://suwayomi:30058`. For Android, also configure the public
   URL and reader token described below.
5. Start the stack:

   ```bash
   docker compose up -d
   ```

Open `http://YOUR-SERVER:8282` using the port you configured. Complete the setup
wizard. **Settings** edits the retained configuration; see the
[configuration reference](../config/README.md) for precedence and storage details.

Current AI source execution requires a passing isolation check. Read
[repair host permissions](../security/repair-host-setup.md) before enabling a
custom AppArmor policy. The current release can require scoped host permissions;
successful app startup or AI sign-in does not prove repair readiness.

## TrueNAS

Use [docker-compose.truenas.yml](../docker-compose.truenas.yml) with
**Apps → Install via YAML**. Change its source, config and data paths to your
actual retained datasets. It builds `autosourcarr:local` on Save/Redeploy.

For dataset layout, existing-stack integration, port mappings and troubleshooting,
see [TrueNAS deployment](../TRUENAS.md#recommended-dataset-layout). That document
also retains older upgrade notes; use this guide and the current changelog for
the current release. Do not copy an old helper-service deployment over a current
integrated installation.

If you already have a main Suwayomi server, connect AutoSourcarr to it rather than
creating a second library. Each separate AutoSourcarr instance needs its own
config, data, installed sources and source-engine profile.

## Sources and readers

1. Open **Sources → Repositories** to manage Keiyoushi or other repositories.
   Use the UI's apply/restart action when prompted.
2. Install extensions through **Sources → Discover**, then choose languages,
   source priority and the separate Search/Chapters switches under Installed.
3. Use **Sources → Open WebView** for a source that needs browser interaction.
4. Open **Status → Build extensions** and download the resulting reader files.

| Reader | File | Address used |
| --- | --- | --- |
| Main Suwayomi | JAR, installed through Suwayomi's external-extension facility | `AUTOSOURCARR_INTERNAL_URL`, reachable from the main server |
| Mihon/Tachiyomi-compatible Android client | APK | `AUTOSOURCARR_PUBLIC_URL`, reachable from the device, with its reader API token |

Browse **AutoSourcarr** in your main reader after installing its extension. The
integrated source engine does not need its own manga library.

Changing a URL or reader token requires rebuilding and reinstalling the reader
artifact. Keep the persistent `/config/.extension-signing/` directory: Android
updates need the same signing identity. A different old signing certificate can
cause `INSTALL_FAILED_UPDATE_INCOMPATIBLE`; back up the old identity before
considering an uninstall/reinstall.

The legacy external build commands remain `bash extension/build.sh` on
Linux/macOS, or `.\extension\build.ps1` in Windows PowerShell with Docker Desktop.
The integrated Web UI is the normal build path.

## Android and remote access

Docker's `http://autosourcarr:8282` service name is not an address an Android device
can resolve. Configure a LAN URL or your authenticated HTTPS hostname:

```dotenv
AUTOSOURCARR_INTERNAL_URL=http://autosourcarr:8282
AUTOSOURCARR_PUBLIC_URL=https://autosourcarr.example.com
AUTOSOURCARR_READER_API_TOKEN=<random-secret>
```

Generate a reader token with `openssl rand -hex 32`, save it in your retained
configuration, restart the instance and rebuild the Android APK. The token is
sent as `X-AutoSourcarr-API-Key` on reader requests.

With Authelia, place this narrow reader rule before your normal authenticated UI
rule. The reader API still requires AutoSourcarr's token:

```yaml
access_control:
  rules:
    - domain: autosourcarr.example.com
      resources:
        - '^/api/reader/v1([/?].*)?$'
      policy: bypass
    - domain: autosourcarr.example.com
      policy: two_factor
```

Do not bypass `/api/v1/`, `/api/admin/`, `/` or the entire hostname. Reader access
allows only authenticated GET/HEAD requests. Do not expose the raw app or source
engine port directly to the Internet. An optional `ADMIN_TOKEN` also protects the
admin API; it does not replace network access control.

## Upgrading and backups

Take a consistent stopped-instance backup before replacing source files or
rebuilding. Preserve:

- Active env/config files and `/config/.extension-signing/`.
- Both `catalog.sqlite` and `chapter-origins.sqlite`, including their consistent
  SQLite state. Keep the separately mounted database directory if configured.
- The whole persistent `/data`, source-engine profile and reader/library storage,
  including downloads, retained code and repair rollback workspaces.

Replace the project source with the complete new release while retaining those
mounts and files, rebuild/redeploy the existing image, and refresh the browser.
Rebuild the reader extension when the release requires it. Restarting an older
image alone does not load changed source files.

Encrypted instance backups are replacement restores for their original application
version and paths. Restore an older backup using that version first, then upgrade.
Do not bypass restore checks or open upgraded data with an older application.
**Database sharing** is a separate, additive catalogue exchange, not a backup
restore. See the [user guide](USER_GUIDE.md#sharing-catalogues).

The default app DB files are `/data/catalog.sqlite` and
`/data/chapter-origins.sqlite`. To relocate them, first mount persistent storage,
then use **Settings → Configuration → Database storage → Move databases**.
AutoSourcarr stages and validates SQLite backups before switching. Keep its old
files until the new location survives a redeploy. Copying only live main SQLite
files can omit committed WAL data.

See [CHANGELOG](../CHANGELOG.md) for release-specific steps and validation limits.
