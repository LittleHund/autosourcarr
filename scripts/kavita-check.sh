#!/usr/bin/env bash
# CHAP-7 probe: a library written by AutoSourcarr opens in Kavita.
#
#   scripts/kavita-check.sh LIBRARY_DIR [EXPECTED_SERIES] [EXPECTED_CHAPTERS]
#
# Runs Kavita in Docker with LIBRARY_DIR mounted read-only at /manga, registers the first (admin) user, creates a
# Manga library on /manga, scans it and asserts that every series folder appears with its chapters
# (defaults: 1 series, 2 chapters - the fake two-chapter series of the C# pipeline test). It first runs
# scripts/check-cbz-library.py on the same folder. Needs docker, curl and python3.
#
# API used (Kavita v0.8.9, API/Controllers): POST /api/Account/register (anonymous, first user becomes admin, returns
# a JWT), POST /api/Library/create, GET /api/Library/libraries, POST /api/Library/scan?libraryId=&force=true,
# POST /api/Series/all-v2 (filter body), GET /api/Series/volumes?seriesId=, GET /api/health.
# /api/Series/all is gone in 0.8.x; all-v2 replaces it.
set -euo pipefail

LIB=${1:-${LIB:-}}
EXPECTED_SERIES=${2:-1}
EXPECTED_CHAPTERS=${3:-2}
KAVITA_IMAGE=${KAVITA_IMAGE:-jvmilazz0/kavita:0.8.9}
PORT=${KAVITA_PORT:-5000}
NAME=${KAVITA_CONTAINER:-autosourcarr-kavita-check}
BASE="http://127.0.0.1:${PORT}"
HERE=$(cd "$(dirname "$0")" && pwd)

if [[ -z "$LIB" || ! -d "$LIB" ]]; then
  echo "usage: $0 LIBRARY_DIR [EXPECTED_SERIES] [EXPECTED_CHAPTERS]" >&2
  exit 2
fi
LIB=$(cd "$LIB" && pwd)

python3 -I "$HERE/check-cbz-library.py" "$LIB" --expect-series "$EXPECTED_SERIES" --expect-chapters "$EXPECTED_CHAPTERS"

cleanup() {
  if [[ "${KEEP_KAVITA:-0}" != 1 ]]; then docker rm -f "$NAME" >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT
docker rm -f "$NAME" >/dev/null 2>&1 || true
docker run -d --name "$NAME" -p "${PORT}:5000" -v "$LIB:/manga:ro" "$KAVITA_IMAGE" >/dev/null

json() { python3 -I -c "import json,sys; data=json.load(sys.stdin); print($1)"; }

echo "Waiting for Kavita ($KAVITA_IMAGE) on $BASE"
for _ in $(seq 1 90); do
  if curl -fsS "$BASE/api/health" >/dev/null 2>&1; then break; fi
  sleep 2
done
curl -fsS "$BASE/api/health" >/dev/null || { docker logs "$NAME" | tail -50; echo "Kavita did not start" >&2; exit 1; }

PASSWORD="Autosourcarr-Check-$RANDOM-Aa1!"
TOKEN=$(curl -fsS -X POST "$BASE/api/Account/register" -H 'Content-Type: application/json' \
  -d "{\"username\":\"autosourcarr\",\"email\":\"\",\"password\":\"$PASSWORD\"}" | json 'data["token"]')
AUTH=(-H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json')

# Manga = 0, FileTypeGroup Archive = 1.
curl -fsS -X POST "$BASE/api/Library/create" "${AUTH[@]}" -d '{
  "id": 0, "name": "AutoSourcarr", "type": 0, "folders": ["/manga"], "folderWatching": false,
  "includeInDashboard": true, "includeInSearch": true, "manageCollections": false, "manageReadingLists": false,
  "allowScrobbling": false, "allowMetadataMatching": false, "enableMetadata": true, "removePrefixForSortName": false,
  "inheritWebLinksFromFirstChapter": false, "defaultLanguage": "", "fileGroupTypes": [1], "excludePatterns": []
}' >/dev/null
LIBRARY_ID=$(curl -fsS "$BASE/api/Library/libraries" "${AUTH[@]}" | json '[l["id"] for l in data if l["name"]=="AutoSourcarr"][0]')
curl -fsS -X POST "$BASE/api/Library/scan?libraryId=${LIBRARY_ID}&force=true" "${AUTH[@]}" >/dev/null

echo "Scanning library $LIBRARY_ID"
SERIES='[]'
for _ in $(seq 1 60); do
  SERIES=$(curl -fsS -X POST "$BASE/api/Series/all-v2?PageNumber=1&PageSize=0" "${AUTH[@]}" \
    -d '{"id":0,"statements":[],"combination":1,"limitTo":0}')
  COUNT=$(printf '%s' "$SERIES" | json 'len(data)')
  if [[ "$COUNT" -ge "$EXPECTED_SERIES" ]]; then break; fi
  sleep 2
done
COUNT=$(printf '%s' "$SERIES" | json 'len(data)')
if [[ "$COUNT" -ne "$EXPECTED_SERIES" ]]; then
  echo "Kavita shows $COUNT series, expected $EXPECTED_SERIES" >&2
  docker logs "$NAME" | grep -i -E 'scan|parse|error' | tail -40 >&2 || true
  exit 1
fi

TOTAL=0
for ID in $(printf '%s' "$SERIES" | json '" ".join(str(s["id"]) for s in data)'); do
  CHAPTERS=0
  for _ in $(seq 1 30); do
    VOLUMES=$(curl -fsS "$BASE/api/Series/volumes?seriesId=$ID" "${AUTH[@]}")
    CHAPTERS=$(printf '%s' "$VOLUMES" | json 'sum(len(v.get("chapters") or []) for v in data)')
    [[ "$CHAPTERS" -gt 0 ]] && break
    sleep 2
  done
  NAMEOF=$(printf '%s' "$SERIES" | json "[s['name'] for s in data if s['id']==$ID][0]")
  echo "  $NAMEOF: $CHAPTERS chapters ($(printf '%s' "$VOLUMES" | json '", ".join(sorted(c["range"] for v in data for c in v.get("chapters") or []))'))"
  TOTAL=$((TOTAL + CHAPTERS))
done
if [[ "$TOTAL" -ne "$EXPECTED_CHAPTERS" ]]; then
  echo "Kavita shows $TOTAL chapters, expected $EXPECTED_CHAPTERS" >&2
  exit 1
fi
echo "Kavita check passed: $COUNT series, $TOTAL chapters"
