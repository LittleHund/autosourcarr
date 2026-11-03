<p align="center">
  <img src="autosourcarr/app/static/icons/app-icon-192.png" width="96" height="96" alt="AutoSourcarr logo">
</p>

# AutoSourcarr

AutoSourcarr brings manga and similar media from multiple sources together in one
catalogue. Browse, match titles and choose chapter sources through the Web UI to
create one big, complete library.

Use AutoSourcarr as its own source for Tachiyomi/Mihon, or use it as a downloader
for your own Komga/Suwayomi servers.

**Current release: 0.5.0** (C#/.NET 10) · [Install 0.5.0](#install-050) ·
[Upgrade from 0.4.x](docs/upgrade-from-0.4.md) · [User guide](docs/USER_GUIDE.md) · [Changelog](CHANGELOG.md)

## Features

- Search multiple Mihon/Keiyoushi-compatible sources from one place.
- Combine matching titles and chapters, with manual control over matches and editions.
- Manage your Suwayomi library, imports, missing chapters and download queue.
- Keep galleries separate or combine them as chapters, with rules for each source.
- Check source health and manage AI-assisted repairs with retained code and rollback.
- Share catalogue entries and saved decisions between instances without a central server.
- Back up your instance and download redacted diagnostic logs from the Web UI.

## Install 0.5.0

0.5.0 is one image (`docker/Dockerfile`, built from the project folder) with the source host, WebView, repair
runner, reader extension repository and Web UI inside. A main Suwayomi is optional. Coming from 0.4.x? Follow
[Upgrade from 0.4.x](docs/upgrade-from-0.4.md) first.

1. Copy [docker-compose.0.5.0.yml](docker-compose.0.5.0.yml) and change its paths to your datasets (coming from
   0.4.x: the new datasets filled by the migration tool, never the 0.4.x ones). Mounts:
   `/data` (databases, source host, covers), `/config` (`config.json`, `config.key`, accounts) and, optionally,
   your library or old downloads (read-only). Delete the `private-*` services for a single instance.
2. Give UID/GID **568:568** the data and config folders. The image runs as 568:568 without a Docker socket or
   helper services. Keep `restart: unless-stopped`: the app exits with code 75 when it restarts itself.
3. Start it (`docker compose -f docker-compose.0.5.0.yml up -d`; on TrueNAS: **Apps → Install via YAML**) and open
   `http://YOUR-SERVER:8282`. The setup wizard writes `config.json`; an old `/config/autosourcarr.env` is converted
   on first start and kept as a backup.
4. Install your sources under **Sources**. Connect your own Suwayomi under **Connections → Suwayomi** if you use one.
5. Readers: open **System → Reader setup**. Each server slot shows a QR code and link that add the extension
   repository `https://<your address>/api/reader/repo/index.min.json` to Mihon (`tachiyomi://add-repo` for older
   forks), and a QR code with the slot's address and device key. Install AutoSourcarr from Browse → Extensions.
   Readers that used the 0.4.x extension install it again from this repository.

Behind a reverse proxy ([Authelia](docs/reverse-proxy/authelia.md), [Nginx Proxy Manager](docs/reverse-proxy/nginx-proxy-manager.md)):

- keep the reader rule `^/api/reader/v1([/?].*)?$` (bypass; AutoSourcarr checks the reader key);
- add a GET/HEAD-only bypass for the public repository files under `/api/reader/repo/`;
- with external sign-in (Authelia `Remote-User`), set `AUTH_TRUSTED_PROXIES` to the proxy's address or Docker
  network, otherwise sign-in through the proxy is refused.

### Previous version (rollback): 0.4.5

The Python 0.4.5 app (`autosourcarr/`, its own `autosourcarr/VERSION`) stays in the project unchanged until the
0.5.0 deploy is confirmed. It builds its image from source with `docker-compose.yml` / `docker-compose.truenas.yml`;
see the [0.4.x installation guide](docs/INSTALLATION.md). Rollback: 0.5.0 runs on **new** datasets filled by the migration tool; the 0.4.x datasets stay untouched. To go back, stop 0.5.0 and start 0.4.5 with its old Compose file on the original 0.4.x datasets. Never point 0.4.x at the 0.5.0 folders: the 0.5.0 database migrations are forward-only, and 0.4.5 never reads `config.json` (it reads only `autosourcarr.env` and `source-host.env`), so everything changed in 0.5.0 would silently be missing. Decisions made in 0.5.0 after the switch are not in the 0.4.x data.

1. Follow the [Docker Compose / TrueNAS installation guide](docs/INSTALLATION.md).
2. Open the Web UI, complete setup and install your sources under **Sources**.
3. Build the reader extension under **Status → Build extensions** and install the
   JAR in Suwayomi or the APK in a compatible Android reader.

## Documentation

| Topic | Guide |
| --- | --- |
| Install 0.5.0 | [Install 0.5.0](#install-050), [docker-compose.0.5.0.yml](docker-compose.0.5.0.yml) |
| Upgrade from 0.4.x | [Upgrade from 0.4.x](docs/upgrade-from-0.4.md) |
| Reverse proxy (Authelia, Nginx Proxy Manager) | [Authelia](docs/reverse-proxy/authelia.md), [NPM](docs/reverse-proxy/nginx-proxy-manager.md) |
| 0.5.0 settings reference | [Settings](docs/settings.md) |
| Install 0.4.5 (rollback) | [Installation (0.4.x)](docs/INSTALLATION.md) |
| Matching, galleries, health, repairs and sharing | [User guide](docs/USER_GUIDE.md) |
| Persistent settings and storage | [Configuration](config/README.md) |
| Repair isolation requirements | [Host permissions](security/repair-host-setup.md) |
| TrueNAS deployment details | [TrueNAS](TRUENAS.md#recommended-dataset-layout) |
| Changes and known verification limits | [Changelog](CHANGELOG.md) |

## Development

Run from the project directory (.NET 10 SDK, Node, Python with the pinned test dependencies):

```bash
dotnet restore --locked-mode && dotnet build -c Release
dotnet run -c Release --project tests/AutoSourcarr.Tests
node scripts/bundle-ui.mjs --check
python scripts/print-compatibility.py --check
bash scripts/check-project.sh
```

The 0.5.0 backend is C#/.NET 10 with SQLite (`src/`), the source host and reader extension use Kotlin, and the Web UI
uses JavaScript. The Python 0.4.5 backend (`autosourcarr/app`, tests in `autosourcarr/tests`) stays until the
0.5.0 deploy is confirmed. Full runtime checks require Linux and the image's JVM/build tools. See the changelog for
checks that remain unverified.

## Support and security

For a problem report, include your version, reproduction steps and the redacted file
from **Status → Download diagnostic logs**. Review the file before sharing it in
[GitHub Issues](https://github.com/LittleHund/autosourcarr/issues).

Keep remote access behind an authenticated reverse proxy or VPN. Readers use a device key
(0.5.0: **System → Keys**; 0.4.5: `AUTOSOURCARR_READER_API_TOKEN`); with Authelia, use `policy: bypass` only for
`^/api/reader/v1([/?].*)?$` and, in 0.5.0, the GET/HEAD repository files under `/api/reader/repo/`.
Do not bypass `/api/v1/` or the whole application. See [Authelia](docs/reverse-proxy/authelia.md) for the full example.

## License

[MIT](LICENSE). Third-party components retain their own licenses.
