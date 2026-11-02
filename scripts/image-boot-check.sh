#!/usr/bin/env bash
# PERF-7 / PIPE-8 boot-time gate for the 0.5.0 image. Starts the image like the Compose file does (UID/GID 568,
# fresh temporary /data and /config, no capabilities, no-new-privileges, no Docker socket), then measures:
#   live   seconds until GET /api/v1/live answers 200
#   ready  seconds until the app is live, the Web UI page is the bundled one (/static/dist/) and the source host
#          answers on 127.0.0.1:4567 inside the container (any HTTP status: the host requires its per-run token)
# Budget: ready within 300 s (5 min, hard), target 60 s (1 min, reported). The container keeps running for the
# browser checks; stop it with `docker rm -f <name>`.
#
#   bash scripts/image-boot-check.sh IMAGE [OUT_FILE] [PORT] [NAME]
#
# Exit: 0 within budget, 1 over budget or never ready, 2 usage/start error. Needs docker, curl and sudo (to give the
# temporary folders to 568:568, as TrueNAS does for the app datasets).
set -uo pipefail
image=${1:?usage: image-boot-check.sh IMAGE [OUT_FILE] [PORT] [NAME]}
out=${2:-boot-time.txt}
port=${3:-18282}
name=${4:-autosourcarr-boot}
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
if ! docker run -d --name "$name" --user 568:568 --cap-drop ALL --security-opt no-new-privileges:true ${extra[@]+"${extra[@]}"} \
      -e TZ=Europe/Amsterdam -p "127.0.0.1:$port:8282" \
      -v "$root/data:/data" -v "$root/config:/config" "$image" >/dev/null; then
  printf 'image=%s\nresult=start-failed\n' "$image" > "$out"
  cat "$out"; exit 2
fi
elapsed() { awk -v a="$start" -v b="$(date +%s.%N)" 'BEGIN { printf "%.1f", b - a }'; }

live=""; ui=""; host=""; ready=""
while :; do
  now=$(elapsed)
  if awk -v n="$now" -v b="$budget" 'BEGIN { exit !(n > b) }'; then break; fi
  if [ "$(docker inspect -f '{{.State.Running}}' "$name" 2>/dev/null)" != true ]; then break; fi
  if [ -z "$live" ] && curl -fsS -m 3 -o /dev/null "http://127.0.0.1:$port/api/v1/live"; then live=$now; fi
  if [ -n "$live" ] && [ -z "$ui" ] && curl -fsS -m 5 "http://127.0.0.1:$port/" 2>/dev/null | grep -q '/static/dist/'; then ui=$(elapsed); fi
  if [ -z "$host" ]; then
    code=$(docker exec "$name" curl -s -m 3 -o /dev/null -w '%{http_code}' http://127.0.0.1:4567/v1/health 2>/dev/null || true)
    [ -n "$code" ] && [ "$code" != 000 ] && host=$(elapsed)
  fi
  if [ -n "$live" ] && [ -n "$ui" ] && [ -n "$host" ]; then ready=$(elapsed); break; fi
  sleep 1
done

verdict=fail
if [ -n "$ready" ]; then
  if awk -v r="$ready" -v t="$target" 'BEGIN { exit !(r <= t) }'; then verdict=pass-target; else verdict=pass-budget; fi
fi
{
  printf 'image=%s\n' "$image"
  printf 'live_seconds=%s\n' "${live:-none}"
  printf 'ui_seconds=%s\n' "${ui:-none}"
  printf 'source_host_seconds=%s\n' "${host:-none}"
  printf 'ready_seconds=%s\n' "${ready:-none}"
  printf 'target_seconds=%s\nbudget_seconds=%s\nresult=%s\n' "$target" "$budget" "$verdict"
  printf 'health=%s\n' "$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$name" 2>/dev/null)"
  printf 'aot_cache=%s\n' "$(docker exec "$name" cat /opt/source-host/aot/status.txt 2>/dev/null || echo unknown)"
  printf 'image_size_bytes=%s\n' "$(docker image inspect -f '{{.Size}}' "$image" 2>/dev/null)"
} > "$out"
cat "$out"
docker logs "$name" 2>&1 | tail -n 200 > "${out%.txt}-container.log" || true
[ "$verdict" != fail ]
