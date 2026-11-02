#!/usr/bin/env bash
# PERF-7 / PIPE-8 boot-time gate for the 0.5.0 image. Starts the image like the Compose file does (UID/GID 568,
# fresh temporary /data and /config, no capabilities, no-new-privileges, restart policy unless-stopped, no Docker
# socket), then measures:
#   live   seconds until GET /api/v1/live answers 200
#   ready  seconds until the app is live, the Web UI page is the bundled one (/static/index.html loads /static/dist/)
#          and the source host answers on 127.0.0.1:4567 inside the container (any HTTP status: it needs its token)
# Budget: ready within 300 s (5 min, hard), target 60 s (1 min, reported).
#
# Runtime check (SRC-5, phase 13), after ready: signs in with the test owner (tests/e2e/fixtures/owner.json, a
# throwaway loopback instance), finishes setup, reads /api/v1/health and records source_sandbox, repair_isolation and
# the source host WebView (JCEF) state. It passes when the source host runs Landlock-sandboxed and its WebView
# reached "ready" inside that sandbox. BOOT_RUNTIME_CHECK=0 skips it (needs node).
#
# Exit code 75 is the app's own restart request (setup restart, armed restore): like Compose's
# `restart: unless-stopped`, the container is restarted by Docker and the measurement goes on; restarts are recorded.
# The container keeps running for the browser checks; stop it with `docker rm -f <name>`.
#
#   bash scripts/image-boot-check.sh IMAGE [OUT_FILE] [PORT] [NAME]
#
# Exit: 0 boot within budget and runtime check passed (or skipped); 1 over budget or never ready; 2 start error;
# 3 boot passed but the runtime check failed. Needs docker, curl, sudo (the temporary folders go to 568:568, as
# TrueNAS does for the app datasets) and node for the runtime check.
set -uo pipefail
image=${1:?usage: image-boot-check.sh IMAGE [OUT_FILE] [PORT] [NAME]}
out=${2:-boot-time.txt}
port=${3:-18282}
name=${4:-autosourcarr-boot}
repo=$(cd "$(dirname "$0")/.." && pwd)
budget=300
target=60
root=$(mktemp -d "${RUNNER_TEMP:-/tmp}/autosourcarr-boot.XXXXXX")
mkdir -p "$root/data" "$root/config"
sudo chown -R 568:568 "$root" 2>/dev/null || chown -R 568:568 "$root" 2>/dev/null || chmod 0777 "$root/data" "$root/config"
docker rm -f "$name" >/dev/null 2>&1 || true

# BOOT_READ_ONLY=1 also makes the image file system read-only (optional hardening, not the Compose default).
extra=()
if [ "${BOOT_READ_ONLY:-0}" = 1 ]; then
  extra=(--read-only --tmpfs /tmp:rw,nosuid,nodev,size=1g --tmpfs /home/autosourcarr:rw,nosuid,nodev,uid=568,gid=568,size=256m)
fi
start=$(date +%s.%N)
if ! docker run -d --name "$name" --user 568:568 --cap-drop ALL --security-opt no-new-privileges:true \
      --restart unless-stopped ${extra[@]+"${extra[@]}"} \
      -e TZ=Europe/Amsterdam -p "127.0.0.1:$port:8282" \
      -v "$root/data:/data" -v "$root/config:/config" "$image" >/dev/null; then
  printf 'image=%s\nresult=start-failed\n' "$image" > "$out"
  cat "$out"; exit 2
fi
elapsed() { awk -v a="$start" -v b="$(date +%s.%N)" 'BEGIN { printf "%.1f", b - a }'; }
restarts() { docker inspect -f '{{.RestartCount}}' "$name" 2>/dev/null || echo 0; }
exit_code() { docker inspect -f '{{.State.ExitCode}}' "$name" 2>/dev/null || echo unknown; }

live=""; ui=""; host=""; ready=""; stopped=""
while :; do
  now=$(elapsed)
  if awk -v n="$now" -v b="$budget" 'BEGIN { exit !(n > b) }'; then break; fi
  status=$(docker inspect -f '{{.State.Status}}' "$name" 2>/dev/null || echo missing)
  # "restarting" (exit 75 or a crash) is followed; a crash loop is cut off after 3 restarts.
  if [ "$status" = exited ] || [ "$status" = dead ] || [ "$status" = missing ] || [ "$(restarts)" -gt 3 ]; then
    stopped="status=$status exit_code=$(exit_code) restarts=$(restarts)"; break
  fi
  if [ "$status" = running ]; then
    if [ -z "$live" ] && curl -fsS -m 3 -o /dev/null "http://127.0.0.1:$port/api/v1/live"; then live=$now; fi
    if [ -n "$live" ] && [ -z "$ui" ] && curl -fsS -m 5 "http://127.0.0.1:$port/static/index.html" 2>/dev/null | grep -q '/static/dist/'; then ui=$(elapsed); fi
    if [ -z "$host" ]; then
      code=$(docker exec "$name" curl -s -m 3 -o /dev/null -w '%{http_code}' http://127.0.0.1:4567/v1/health 2>/dev/null || true)
      [ -n "$code" ] && [ "$code" != 000 ] && host=$(elapsed)
    fi
    if [ -n "$live" ] && [ -n "$ui" ] && [ -n "$host" ]; then ready=$(elapsed); break; fi
  fi
  sleep 1
done

verdict=fail
if [ -n "$ready" ]; then
  if awk -v r="$ready" -v t="$target" 'BEGIN { exit !(r <= t) }'; then verdict=pass-target; else verdict=pass-budget; fi
fi

# Runtime check: source host sandbox, repair isolation and the WebView inside the sandbox.
runtime=skipped; sandbox=none; repair=none; webview=none
if [ "$verdict" != fail ] && [ "${BOOT_RUNTIME_CHECK:-1}" = 1 ] && command -v node >/dev/null; then
  health_json="${out%.txt}-health.json"
  report=$(node "$repo/scripts/image-e2e-prepare.mjs" "http://127.0.0.1:$port" "$repo/tests/e2e/fixtures/owner.json" \
             --health "$health_json" --wait-webview "${BOOT_WEBVIEW_WAIT:-180}" 2>&1)
  printf '%s\n' "$report" > "${out%.txt}-runtime.log"
  sandbox=$(printf '%s\n' "$report" | sed -n 's/^source_sandbox=//p' | head -n 1)
  repair=$(printf '%s\n' "$report" | sed -n 's/^repair_isolation=//p' | head -n 1)
  webview=$(printf '%s\n' "$report" | sed -n 's/^webview=//p' | head -n 1)
  runtime=fail
  case "$sandbox" in sandboxed*) [ "$webview" = ready ] && runtime=pass ;; esac
fi

{
  printf 'image=%s\n' "$image"
  printf 'live_seconds=%s\n' "${live:-none}"
  printf 'ui_seconds=%s\n' "${ui:-none}"
  printf 'source_host_seconds=%s\n' "${host:-none}"
  printf 'ready_seconds=%s\n' "${ready:-none}"
  printf 'target_seconds=%s\nbudget_seconds=%s\nresult=%s\n' "$target" "$budget" "$verdict"
  printf 'restarts=%s\nlast_exit_code=%s\n' "$(restarts)" "$(exit_code)"
  [ -n "$stopped" ] && printf 'stopped=%s\n' "$stopped"
  printf 'source_sandbox=%s\nrepair_isolation=%s\nwebview=%s\nruntime_result=%s\n' "${sandbox:-none}" "${repair:-none}" "${webview:-none}" "$runtime"
  printf 'health=%s\n' "$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$name" 2>/dev/null)"
  printf 'aot_cache=%s\n' "$(docker exec "$name" cat /opt/source-host/aot/status.txt 2>/dev/null || echo unknown)"
  printf 'image_size_bytes=%s\n' "$(docker image inspect -f '{{.Size}}' "$image" 2>/dev/null)"
} > "$out"
cat "$out"
docker logs "$name" 2>&1 | tail -n 200 > "${out%.txt}-container.log" || true
[ "$verdict" = fail ] && exit 1
[ "$runtime" = fail ] && exit 3
exit 0
