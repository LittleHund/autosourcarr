<p align="center">
  <img src="autosourcarr/app/static/icons/app-icon-192.png" width="96" height="96" alt="AutoSourcarr logo">
</p>

# AutoSourcarr

AutoSourcarr brings manga and similar media from multiple sources together in one
catalogue. Browse, match titles and choose chapter sources through the Web UI to
create one big, complete library.

Use AutoSourcarr as its own source for Tachiyomi/Mihon, or use it as a downloader
for your own Komga/Suwayomi servers.

**Current release: 0.4.5** · [Installation](docs/INSTALLATION.md) ·
[User guide](docs/USER_GUIDE.md) · [Changelog](CHANGELOG.md)

## Features

- Search multiple Mihon/Keiyoushi-compatible sources from one place.
- Combine matching titles and chapters, with manual control over matches and editions.
- Manage your Suwayomi library, imports, missing chapters and download queue.
- Keep galleries separate or combine them as chapters, with rules for each source.
- Check source health and manage AI-assisted repairs with retained code and rollback.
- Share catalogue entries and saved decisions between instances without a central server.
- Back up your instance and download redacted diagnostic logs from the Web UI.

## Getting started

1. Follow the [Docker Compose / TrueNAS installation guide](docs/INSTALLATION.md).
2. Open the Web UI, complete setup and install your sources under **Sources**.
3. Build the reader extension under **Status → Build extensions** and install the
   JAR in Suwayomi or the APK in a compatible Android reader.

The standard stack contains AutoSourcarr and your main Suwayomi server. Source
execution and supporting tools run inside AutoSourcarr. Browsing does not add
anything to your reader's library until you choose to add it.

Release 0.4.5 builds its image from source. The C#/.NET migration and prebuilt-image
setup are in progress; use the current installation guide for this release.

## Documentation

| Topic | Guide |
| --- | --- |
| Install, connect a reader or upgrade | [Installation](docs/INSTALLATION.md) |
| Matching, galleries, health, repairs and sharing | [User guide](docs/USER_GUIDE.md) |
| Persistent settings and storage | [Configuration](config/README.md) |
| Repair isolation requirements | [Host permissions](security/repair-host-setup.md) |
| TrueNAS deployment details | [TrueNAS](TRUENAS.md#recommended-dataset-layout) |
| Changes and known verification limits | [Changelog](CHANGELOG.md) |

## Development

Run from the project directory with the pinned test dependencies installed:

```bash
python -m pip install -r autosourcarr/requirements-test.txt
PYTHONPATH=autosourcarr python -m pytest -q autosourcarr/tests
bash scripts/check-project.sh
```

The current backend uses Python and SQLite, the Web UI uses JavaScript, and the
reader extension uses Kotlin. Full runtime checks require Linux and the image's
JVM/build tools. See the changelog for checks that remain unverified.

## Support and security

For a problem report, include your version, reproduction steps and the redacted file
from **Status → Download diagnostic logs**. Review the file before sharing it in
[GitHub Issues](https://github.com/LittleHund/autosourcarr/issues).

Keep remote access behind an authenticated reverse proxy or VPN. Android readers
use `AUTOSOURCARR_READER_API_TOKEN`; with Authelia, use `policy: bypass` only for
`^/api/reader/v1([/?].*)?$`. Do not bypass `/api/v1/` or the whole application.
See [reader access](docs/INSTALLATION.md#android-and-remote-access) for the full example.

## License

[MIT](LICENSE). Third-party components retain their own licenses.
