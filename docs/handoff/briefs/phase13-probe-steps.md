# Phase 13 probe steps (runtime checks, image build, boot time, browser checks against the image)

For the coordinator: paste this into `.github/workflows/probe.yml` on `ci-probe`. One probe run then:
- checks the source host sandbox;
- runs the Linux-only runtime tests and the performance benchmark;
- builds the 0.5.0 image (no push; nothing leaves the runner);
- boots it, measures the boot time and checks the source host sandbox and WebView inside it;
- runs Playwright against it.

Nothing here publishes to ghcr (hard rule 15). Revision 2 (2026-11-02, area P, on b95a7d0), not run yet. It keeps
the image/boot/Playwright block of revision 1 (first image probe: build OK, boot gate fixed in 71de9da).

## 1. Job settings

- `timeout-minutes: 240`: the image build adds 2–40 min depending on the cache, Playwright against the image
  15–40 min, and the benchmark about 1 min.
- No new permissions, services or secrets. The runner's Docker (BuildKit, default `docker` driver) supports the
  named build contexts. gcc, curl and node are on the runner already; the tests run as the non-root `runner` user.

## 2. Replacement block

Use it as a replacement for the revision-1 "Phase 13" block, or insert it fresh. It goes **after** the
"Source host (phase 4)" block (which sets `$h` and builds `.build/autosourcarr-source-host.jar`) and the
"READ-1 reader extension" block (which sets `$rx` and writes `.build/reader-extension`), and **before** the
"Mihon backup-format watch" line. It uses `$b` (the build result) and `$sha` from the top of the probe script.

```bash
          # Phase 13 (area R + P): source host sandbox proof, Linux runtime tests, performance benchmark (PIPE-8),
          # then the 0.5.0 image (docker/Dockerfile, no push), boot time + sandbox/WebView runtime check (PERF-7,
          # SRC-5) and Playwright against the running image (PIPE-5).
          sbx=skipped; lrt=skipped; bm=skipped; ib=skipped; boot=none; ipw=skipped
          bash scripts/source-host-sandbox-check.sh > probe/source-host-sandbox.txt 2>&1; sbx=$?
          if [ "$b" = 0 ]; then
            nbin=$RUNNER_TEMP/native; mkdir -p "$nbin"
            nflags="-O2 -Wall -Werror -U_FORTIFY_SOURCE -D_FORTIFY_SOURCE=3 -fstack-protector-strong -fstack-clash-protection -fcf-protection=full -fPIE -static-pie -Wl,-z,relro,-z,now,-z,noexecstack"
            if gcc $nflags -o "$nbin/repair-runner" native/repair-runner.c > probe/linux-runtime.txt 2>&1 \
               && gcc $nflags -o "$nbin/source-host-sandbox" native/source-host-sandbox.c >> probe/linux-runtime.txt 2>&1; then
              AUTOSOURCARR_REPAIR_RUNNER=$nbin/repair-runner AUTOSOURCARR_SOURCE_HOST_SANDBOX=$nbin/source-host-sandbox \
                timeout 900 dotnet run --project tests/AutoSourcarr.Tests -c Release --no-build -- \
                --filter "repair isolation (linux)" --filter "source host sandbox (linux)" >> probe/linux-runtime.txt 2>&1; lrt=$?
            else lrt=compile; fi
            timeout 900 dotnet run --project tests/AutoSourcarr.Tests -c Release --no-build -- --benchmark probe/benchmark.json --gate > probe/benchmark.txt 2>&1; bm=$?
          fi
          if [ -f docker/Dockerfile ] && [ "$h" = 0 ] && [ "$rx" = 0 ]; then
            mkdir -p .build/source-host-out && cp .build/autosourcarr-source-host.jar .build/source-host-out/
            start_build=$(date +%s)
            timeout 3600 docker buildx build -f docker/Dockerfile \
              --build-context source-host=.build/source-host-out \
              --build-context reader-extension=.build/reader-extension \
              --build-arg VERSION="$(tr -d '\r\n' < VERSION)" --build-arg REVISION="$sha" \
              --load -t autosourcarr:probe . > probe/image-build.log 2>&1; ib=$?
            {
              echo "result=$ib"; echo "seconds=$(( $(date +%s) - start_build ))"
              docker image inspect -f 'size_bytes={{.Size}}' autosourcarr:probe 2>/dev/null
              docker run --rm --entrypoint sh autosourcarr:probe -c 'cat /opt/source-host/aot/status.txt; echo "jlink: $(cat /opt/source-jre/autosourcarr-modules.txt)"; ls -l /opt/autosourcarr-native /app/reader-extension; id' 2>&1
              echo "--- build log (errors and last lines)"
              grep -E "ERROR|error:|failed|UNSIGNED|AOT cache|jlink modules" probe/image-build.log | head -60
              tail -n 60 probe/image-build.log
            } > probe/image-build.txt 2>&1
            rm -f probe/image-build.log
            if [ "$ib" = 0 ]; then
              # Exit 0 = boot + runtime OK, 1 = boot over budget, 2 = start error, 3 = booted but sandbox/WebView check failed.
              bash scripts/image-boot-check.sh autosourcarr:probe probe/boot-time.txt 18282 autosourcarr-probe > /dev/null 2>&1; bootcode=$?
              boot=$(grep -o '^ready_seconds=.*' probe/boot-time.txt | cut -d= -f2)
              if [ "$bootcode" = 0 ] || [ "$bootcode" = 3 ]; then
                node scripts/image-e2e-prepare.mjs http://127.0.0.1:18282 tests/e2e/fixtures/owner.json > probe/image-playwright.log 2>&1; prep=$?
                ipw=$prep
                if [ "$prep" = 0 ]; then
                  # Official Playwright image, pinned like release.yml; node_modules comes from the npm ci above.
                  timeout 2700 docker run --rm --network host --ipc=host -v "$PWD:/work" -w /work -e CI=1 \
                    -e E2E_BASE_URL=http://127.0.0.1:18282 -e E2E_SCREENSHOT_DIR=/work/.build/image-screenshots \
                    -e E2E_JSON=/work/test-results/playwright-image.json -e E2E_THEMES="${E2E_IMAGE_THEMES:-}" \
                    mcr.microsoft.com/playwright:v1.64.1-jammy@sha256:a3966b653e8d48a30901889081b616307fca73e0d843a117fadf6609397dac38 \
                    npx playwright test >> probe/image-playwright.log 2>&1; ipw=$?
                  sudo chown -R "$(id -u):$(id -g)" . 2>/dev/null || true
                fi
                { node scripts/playwright-summary.mjs test-results/playwright-image.json 2>&1
                  echo "screenshots=$(find .build/image-screenshots -name '*.png' 2>/dev/null | wc -l)"
                  node scripts/screenshot-diff.mjs "" .build/image-screenshots 2>&1
                  echo "--- log tail"; tail -c 20000 probe/image-playwright.log; } > probe/image-playwright.txt
                rm -f probe/image-playwright.log
              fi
              docker rm -f autosourcarr-probe > /dev/null 2>&1 || true
            fi
            node autosourcarr/tests/ui_v050_theme_contrast.js > probe/contrast.txt 2>&1
          fi
```

Optional: set `E2E_IMAGE_THEMES: blue-dark,blue-light` in the step's `env:` for a shorter run. The release workflow
always runs every theme; leave the variable empty here for the full 180-test matrix.

## 3. Result fields

Append six fields to the `printf` that writes `probe/result.txt`:
- in the format string, add
  `\nsandbox_check=%s\nlinux_runtime=%s\nbenchmark=%s\nimage_build=%s\nboot_seconds=%s\nimage_playwright=%s`
  before the final `\n`;
- after `"$ub"`, add the arguments `"$sbx" "$lrt" "$bm" "$ib" "$boot" "$ipw"`.

If revision 1's three image fields are already in the printf, replace them with these six.

## 4. Reading the results

| File / field | Green |
| --- | --- |
| `sandbox_check=0`, `probe/source-host-sandbox.txt` | Ends with `0 failure(s)`. The `landlock` value in the proof line is ≥ 1 and `scoped` is 2 on the runner kernel. |
| `linux_runtime=0`, `probe/linux-runtime.txt` | Both filters PASS, no `SKIP` lines. A filter that registers no test exits non-zero; that means the runner lacks Landlock ABI 6 or the test runs as root. |
| `benchmark=0`, `probe/benchmark.json` | `"over_budget": []`. The budgets are in `PerformanceBenchmark.Budgets`; a local Windows run measured page p95 ≈ 3 ms and 1609 jobs/s. |
| `image_build=0`, `probe/image-build.txt` | `result=0`; `AOT cache: <size>`; 3 static binaries in `/opt/autosourcarr-native` (repair-runner, repair-launcher, source-host-sandbox); `index.min.json` in `/app/reader-extension`; `id` shows `uid=568`. |
| `boot_seconds`, `probe/boot-time.txt` | `result=pass-target` (≤ 60 s) or `pass-budget` (≤ 300 s); `runtime_result=pass`, which means `source_sandbox=sandboxed` and `webview=ready`. Also check `repair_isolation=` (state of the repair runner), `restarts=` and `last_exit_code=` (75 = the app's own restart request, which the restart policy follows). Health JSON: `probe/boot-time-health.json`; sign-in/health log: `probe/boot-time-runtime.log`; container log: `probe/boot-time-container.log`. |
| `image_playwright=0`, `probe/image-playwright.txt` | 0 failed; `screenshots=` > 0 (the first run is the baseline). |
| `probe/contrast.txt` | Ends with `PASS WCAG AA`. |

If `webview` is a `failed: …` state, the JCEF needs one more sandbox path. Read the reason in
`boot-time-container.log` (Chromium names the path it could not open). Then add that path to the image as
`ENV AUTOSOURCARR_SOURCE_HOST_RW=…` or `_RO=…` (colon-separated) in `docker/Dockerfile`. The current image needs
none: HOME, TMPDIR, XDG caches and `java.io.tmpdir` are all under `<data>/source-host`.

The probe commits `probe/` only. Screenshots stay on the runner; the release workflow uploads them as the
`ui-screenshots` artifact, which is the baseline for the next release's diff.

## 5. What the probe does not cover

- Publishing (owner gate) and the `release` environment.
- The in-Docker `reader-extension` and `source-host` stages. The probe hands both in as build contexts, like the
  release workflow; a plain `docker build` without them builds both inside BuildKit, and that path is not tested here.
- The optional uid 569 variant of the source host (not recommended; see `docker-compose.0.5.0.yml`). The image runs
  the host as 568 inside the Landlock sandbox.
