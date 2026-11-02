# Phase 13 probe steps (image build, boot time, browser checks against the image)

For the coordinator: add these to `.github/workflows/probe.yml` on `ci-probe` so ONE probe run builds the 0.5.0
image (no push, nothing leaves the runner), boots it, measures the boot time and runs Playwright against it.
Nothing here publishes to ghcr (hard rule 15). Written 2026-11-02 by area P; not run yet.

## 1. Job settings

- `timeout-minutes: 150` → `240` (the image build adds about 20–40 min on a cold cache: R2R publish, apt, the
  255 MB JCEF download, jlink, the AOT training run; Playwright against the image adds 15–40 min).
- No new permissions, services or secrets. The runner's Docker (BuildKit) is enough; `docker buildx` with the default
  `docker` driver supports the named build contexts used below.

## 2. Probe script additions

Insert **after** the "Source host (phase 4)" block (it sets `$h` and builds `.build/autosourcarr-source-host.jar`)
and the "READ-1 reader extension" block (it sets `$rx` and writes `.build/reader-extension`), and **before** the
"Mihon backup-format watch" line:

```bash
          # Phase 13: the 0.5.0 image (docker/Dockerfile, no push), boot time (PERF-7) and Playwright against it (PIPE-5).
          ib=skipped; boot=none; ipw=skipped
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
              bash scripts/image-boot-check.sh autosourcarr:probe probe/boot-time.txt 18282 autosourcarr-probe > /dev/null 2>&1
              boot=$(grep -o '^ready_seconds=.*' probe/boot-time.txt | cut -d= -f2)
              if grep -q '^result=pass' probe/boot-time.txt; then
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

Optional: set `E2E_IMAGE_THEMES: blue-dark,blue-light` in the step's `env:` for a shorter first run (the release
workflow always runs every theme). Leave it empty for the full 180-test matrix.

## 3. Result fields

Append three fields to the `printf` that writes `probe/result.txt` (format string and arguments):

```bash
          printf '...existing fields...\nimage_build=%s\nboot_seconds=%s\nimage_playwright=%s\n' ...existing args... "$ib" "$boot" "$ipw" > probe/result.txt
```

i.e. add `\nimage_build=%s\nboot_seconds=%s\nimage_playwright=%s` before the final `\n` of the format and
`"$ib" "$boot" "$ipw"` after `"$ub"`.

## 4. Reading the results

| File / field | Green |
| --- | --- |
| `image_build=0`, `probe/image-build.txt` | `result=0`; `AOT cache: <size>` (a `none` line is a finding, not a build failure); 2 static binaries in `/opt/autosourcarr-native`; `index.min.json` in `/app/reader-extension`; `id` shows `uid=568` |
| `boot_seconds`, `probe/boot-time.txt` | `result=pass-target` (≤ 60 s) or `pass-budget` (≤ 300 s); `fail` or `none` = gate failed; `probe/boot-time-container.log` holds the container log tail |
| `image_playwright=0`, `probe/image-playwright.txt` | 0 failed; `screenshots=` > 0 (first run is the baseline) |
| `probe/contrast.txt` | ends with `PASS WCAG AA` |

The probe commits `probe/` only; screenshots stay on the runner (the release workflow uploads them as the
`ui-screenshots` artifact, the baseline for the next release's diff).

## 5. What the probe does not cover

- Publishing (owner gate) and the `release` environment.
- The in-Docker `reader-extension` and `source-host` stages (the probe hands both in as build contexts, like the
  release workflow); a plain `docker build` without them builds both inside BuildKit and is not tested here.
- The low-rights source host user (569) at run time: the image has the account; area R starts the host under it.
