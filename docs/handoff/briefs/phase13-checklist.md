# Phase 13 checklist (Pipeline completion), built 2026-11-02

Sources: `conversion-plan.md` phase 13 text, `requirements.md` (PIPE-1–7, PIPE-8, PIPE-9, ARC-2, ARC-4, ARC-8,
PERF-2, PERF-7, SRC-5), the phase 10 checklist rows owned by phase 13, port-map rows moved here (native launcher
and resolver removal, source host as a low-rights user, runner binary in the image), and the phase 11 note that the
image serves the UI bundle. Gate: publishing to ghcr is hard rule 15 — build and test the image in CI, never push
it without the owner's go.

## (a) Plan text and "Done when"

| Item | Area | State |
| --- | --- | --- |
| Multi-stage 0.5.0 image: ReadyToRun .NET 10 app, `jlink` Java 25 runtime, JCEF + Xvfb, AOT class cache from a training run | P pipeline | |
| NuGet and Gradle caches; Kotlin host built only when its files change; Playwright only when UI files change | P | |
| Pinned base-image digests and package/tool hashes; pins updated by the pipeline (PIPE-3) | P | |
| Image target `ghcr.io/littlehund/autosourcarr` (public) — publish step prepared, gated | P | |
| Compatibility list published with each build (PIPE-4) | P | |
| Playwright screenshots with diff against the previous release, in the official Playwright image (PIPE-5) | P | |
| Contrast and boot-time gates; boot under 5 min measured (PERF-7, PIPE-8) | P | |
| Mihon release watch reports backup-schema changes (PIPE-9) | P | |
| Job-queue tuning: benchmark (large-library page load, queue under load), CPU/memory limits per job type (PERF-2, PIPE-8) | R runtime | |
| Image serves the minified UI bundle (`dist/`) | P | |
| Done when: one pipeline run produces the image and the reader extension, with boot under 5 min measured | P | |

## (b) Requirements

PIPE-1 pull a ready image, restart does not rebuild · PIPE-2 one pipeline: fresh OS packages, pinned versions, tests,
browser checks, boot time · PIPE-3 pins by fingerprint · PIPE-4 compatibility list · PIPE-5 screenshots + diff, broken
layout stops the release · PIPE-6 green suite · PIPE-7 source runtime on Suwayomi v2.4.2366 · PIPE-8 performance gate ·
PIPE-9 backup-format watch · ARC-2 · ARC-4 several instances · ARC-8 safe defaults in the image, nothing needs host
permissions · PERF-2 · PERF-7 · SRC-5 WebView in the low-rights runner.

## (c) Carried into phase 13

| Item | Area |
| --- | --- |
| Reader extension built into the image (`scripts/build-reader-extension.sh`) | P |
| Repair runner binary in the image and used by the app (today only tests) | R |
| Source host as a separate low-rights user inside the container | R (+P for the image user) |
| WebView sandboxed in the runner (SRC-5, Landlock) | R |
| PERF-2 CPU/memory limits; health, raw archive and catalogue review moved into the job queue | R |
| DNS rebinding, public-suffix cookie check, SNI in CONNECT tunnels (LIMIT records, egress proxy) | R |
| Continuous catalogue discovery crawler (`schedule_continuous_catalog_discovery`) | R |
| `RefreshMediaVariants` startup step (reported "unavailable") | R |
| Remove the native startup resolver / PID 1 launcher and `src/AutoSourcarr.Host` with the image switch | P + R |
| Phase 12 limits that need the image (integrated restart after restore) | R |

## (d) Phase end

Green probe incl. an image build + boot-time run · local build/tests/check-project · independent audit, findings fixed or
recorded · push `conversion`, green CI (run id) · ZIP + sha256 · knowledge records `phase13-records.json` · port-map ticks ·
PC sync · checkpoint · report. Then STOP: phase 14 (cutover) needs the owner.
