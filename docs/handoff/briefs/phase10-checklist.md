# Phase 10 checklist (Chapters and reader), rebuilt 2026-11-01

Sources: `conversion-plan.md` phase 10 text, `requirements.md` (CHAP-1..7, READ-1..5, MAT-20),
`docs/port-map.md` phase 10 rows, the carried items in `project-docs/checkpoint.md`, and the
area code in WIP commit 3ed0b4e. Ticks are filled in at phase end.

## (a) Plan text and "Done when"

| Item | Area | State |
| --- | --- | --- |
| Numbering words (Vol, Part, Act, Stage, No., Episode) and part order | 10A ChapterNumbering | |
| Season continuation naming ("Chapter 41: S2 Ch.1: …") | 10A ChapterNumbering | |
| Group priority: member with most chapters serves, no own WebView | 10A ServingGroups | |
| Quality score from sample pages; failed samples keep the old score | 10A CandidateQuality | |
| Opt-in quality upgrades with minimum gain and cutoff | 10A ServingUpgrades | |
| Existing reader serving kept (`/api/reader/v1` behaviour identical) | 10A ReaderBrowse / ReaderEndpoints | |
| Downloads as `Series/Chapter.cbz` + `cover.jpg` + `ComicInfo.xml` | 10B Library | |
| Prebuilt reader extension, signed (user secret), own repo index | 10D | |
| 5 server slots | 10D | |
| Per-source APK/JAR downloads, never stale | 10D InstalledExtensionDownloads | |
| Authelia and Nginx Proxy Manager examples | 10D docs/reverse-proxy | |
| UI pages for all of the above | 10A–10D | |
| Done when: Mihon reads from two servers with separate device keys | 10D | needs a phone or emulator: report as not run unless it is |
| Done when: a downloaded series opens in Mihon's local source and Kavita | 10B check-cbz-library.py + kavita-check.sh | Kavita check in probe; Mihon local source not runnable here |

## (b) Requirements

CHAP-1 · CHAP-2 · CHAP-3 · CHAP-4 · CHAP-5 · CHAP-6 · CHAP-7 · READ-1 · READ-2 · READ-3 · READ-4
(rule: key protection and `^/api/reader/v1([/?].*)?$` unchanged; `/api/reader/repo/` GET/HEAD public) ·
READ-5 · MAT-20 (translations help search only, never stored as titles/evidence).

## (c) Port-map rows

Method groups: Catalogue/Browse (11), Catalogue/Chapters (28), Catalogue/Gallery (10),
Catalogue/Metadata hydration (23), Catalogue/Metadata identity (11), Catalogue/Tags (1),
Imaging (27). Modules: asset_decode, catalog_layout, catalog_views, cover_cache, gallery_metadata,
gallery_policy, image_safety, library_projection, metadata_catalog, metadata_fields, quality,
serving_profiles, tag_store, thumbnail_fingerprint, translation. Routes: `LibraryEndpoints`
(9 routes, CBZ downloads) plus the chapter/serving/catalogue/tags/reader-extension groups in the
area endpoint classes. Health: translation.

Carried in: import exact-thumbnail tie-break · machine translation in search · gallery identity ·
page colour sampling (`IPageImageSource`) · chapter refresh after discovery attach · entry-id token
in the reader source · library-monitor safe replay · RuntimeMetrics call sites · duplicate test set
runs automatically when the user fixture exists (0 wrong merges).

## (d) UI pages moved to `/api/v1`

Chapters / entry detail, serving profiles and upgrades, gallery, library and downloads, activity
history, tags review, catalogue views, System → reader setup (QR), Installed tab downloads.

## (e) Phase end

Green probe (build + all tests) · local check-project + ci-pytest · independent audit, findings fixed
or recorded · push `conversion`, green CI (run id) · ZIP + sha256 · knowledge records
`phase10-records.json` · port-map ticks · PC sync · checkpoint (repo copy) · report.
