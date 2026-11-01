# Phase 0 stage 4 — version check of R1–R28 (2026-10-31)

Re-check of the research findings from 2026-10-27 against the live sources. **Changed** = a fact moved; **confirmed** = still as researched; **not re-verified** = the source could not be fetched from the container or the finding is not version-bound (counts, API shapes, pricing). Nothing below changes the plan's hours; three items change pins or add a small rule.

| # | Topic | Status | What is current (31 Oct 2026) | Effect on the plan |
| --- | --- | --- | --- | --- |
| R1 | Mihon | **changed (newer release)** | Mihon **v0.21.1** (12 Oct 2026) is current. Its notes repeat that v0.21.x is the last major version that restores `.tachibk` and the next one drops Tachiyomi 1.4 extensions. v0.20.0 added `tachiyomix` 1.6 extensions and the 1.6 index format and renamed "extension repo" to "extension store"; v0.21.0 limits what extensions can reach in the app and installs/updates extensions only from their own store. The backups guide still lists only `.tachibk`/`.proto.gz`; no new format is published. | None. The extension host must load 1.4 **and** 1.6 extensions and read the 1.6 index (phase 4); PIPE-9 watches the next Mihon major for the new backup format. Pin the reader-extension compatibility target at Mihon 0.21.x. |
| R2, R12 | Mihon backup `.proto` | confirmed | Still no published `.proto`; converters read Mihon's model classes. | None (derive the schema from the model classes, PIPE-9). |
| R3 | Suwayomi | confirmed | **v2.4.2366** (30 Sep 2026) is still the latest stable; the preview channel's newest build (v2.3.2360, Sep) is older than it. | Pin v2.4.2366 for the extension-host loader and store code. |
| R4, R7 | Landlock / TrueNAS kernel | confirmed | TrueNAS 25.10.7 ships Linux **6.12.105** (release notes). Landlock on 6.12 is ABI 6 (TCP rules need ABI 4 / 6.7+). | None; the start-up ABI check stays. |
| R5 | .NET 10 | **changed (patch)** | **.NET 10.0.14** (25 Oct 2026, out-of-band security release) with SDKs **10.0.403** and 10.0.114. Fixes CVE-2026-111497 (Brotli decoder out-of-bounds write — affects `HttpClient` response decompression and ASP.NET request decompression) and CVE-2026-70197 (SocketAsyncEventArgs). .NET 11 still due in November as a short-term release. | `global.json` pins 10.0.402 with `rollForward: latestPatch`, so 10.0.403 is picked up automatically; CI and the image use 10.0.14. Because the source transport decompresses Brotli from arbitrary sites, the image must never ship a runtime older than 10.0.14. |
| R6, R24 | FlareSolverr / Byparr | partly confirmed | FlareSolverr **v3.5.2** is the current image (TrueNAS catalogue updated 27 Oct 2026). The release notes themselves could not be fetched from the container, so the API surface (`POST /v1`, `returnOnlyCookies`, sessions) is carried over from the 27 Oct research unverified. | None now; phase 7 re-reads the README before writing the connection. |
| R8, R17 | SSE | not re-verified | Framework feature (.NET 10) and nginx behaviour; not version-bound. | None. |
| R9, R26 | Java | **changed (patch)** | Temurin **25.0.5+8** (22 Oct 2026), security support to 30 Sep 2031; Temurin 21.0.13+9 for comparison. | Pin the jlink runtime at 25.0.5 (the AOT cache is tied to the exact JDK, so the pin is part of the image build). |
| R10 | MalwareBazaar | not re-verified | Pricing/terms page not fetched. | None. |
| R11, R22 | CBZ / ComicInfo / local source | not re-verified (static formats) | — | None. |
| R13 | Sonarr queue rules | not re-verified (code reading, not a version) | — | None. |
| R14, R15 | UI palette / bundling | not applicable | Own code. | None. |
| R16 | GitHub Actions/Packages | not re-verified | — | None (repo stays public). |
| R18 | iOS web push | not re-verified | — | None (0.6.0). |
| R19 | Komf | not re-verified | — | None. |
| R20, R23 | Keiyoushi counts and publisher sources | not re-verified (counts drift weekly) | — | None. |
| R21 | landlock-sharp | not re-verified | Reference only; own wrapper is written in phase 6. | None. |
| R22 | FluentMigrator | confirmed | **8.0.1** (21 Jan 2026) is still the latest. | Pin 8.0.1. |
| R25 | JCEF X11 libraries | not re-verified | — | None. |
| R27 | AniList | confirmed, one addition | Standard limit 90/min; the temporary **30/min** cap is still in force and still invisible in the headers; 429 carries `Retry-After` and `X-RateLimit-Reset`; a separate **burst limiter** exists with undocumented thresholds. | Metadata client: 30/min, honour `Retry-After`, and space requests evenly (no bursts) — add "no bursts" to the phase 10 client rules. |
| R28 | MangaDex | confirmed, one addition | ~5 req/s per IP (a minimum guarantee), 429 then a temporary 403 ban if you keep going; honest `User-Agent`; no `Via` header; hotlinked images get a deliberately wrong image, so images must be proxied (and the proxy adds CORS). **Per-endpoint caps** exist, notably `GET /at-home/server/{id}` **40/min** (the call that resolves chapter page servers). | Phase 10 page resolution paces MangaDex at-home lookups to 40/min and caches the server answer; the R4 proxy stays transparent (no `Via`, same user agent). |

## Pins carried into stage 5 (CI) and phase 14 (image)

- .NET SDK 10.0.403 / runtime 10.0.14 (global.json already rolls forward to it)
- Temurin 25.0.5+8, jlink runtime
- Suwayomi v2.4.2366 (loader/store code), Mihon 0.21.x compatibility target, extension API 1.6 + 1.4
- FluentMigrator 8.0.1; Microsoft.Data.Sqlite 10.0.13 → bump to 10.0.14 with the runtime
- FlareSolverr-compatible solver API as of v3.5.2 (re-read in phase 7)

## Sources

- Mihon changelog v0.21.1: https://mihon.app/changelogs/v0.21.1 ; releases: https://github.com/mihonapp/mihon/releases ; backups guide: https://mihon.app/docs/guides/backups
- Suwayomi releases: https://github.com/Suwayomi/Suwayomi-Server/releases ; preview: https://github.com/Suwayomi/Suwayomi-Server-preview/releases
- .NET 10.0.14 release notes: https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.14/10.0.14.md
- TrueNAS 25.10.7 release notes: https://update-master.ixsystems.com/scale/TrueNAS-SCALE-Goldeye/TrueNAS-SCALE-25.10.7.release-notes.txt
- Temurin versions: https://endoflife.date/eclipse-temurin
- FluentMigrator versions: https://nubrowse.com/packages/FluentMigrator.Abstractions/
- FlareSolverr current image: https://apps.truenas.com/catalog/flaresolverr_community/
- AniList rate limiting: https://docs.anilist.co/guide/rate-limiting
- MangaDex limitations: https://api.mangadex.org/docs/2-limitations
