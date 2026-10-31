#!/usr/bin/env bash
# CI fixture for the lib 1.4 check: a local Komga server with one comic (CBZ of 3 PNG pages).
# Prints nothing on success; the source host check then uses http://127.0.0.1:25600 with the user below.
set -euo pipefail
root=${RUNNER_TEMP:-/tmp}/komga
mkdir -p "$root/comics/Fixture Comic" "$root/config"
python3 - "$root/comics/Fixture Comic/Fixture Comic 001.cbz" <<'PY'
import struct, sys, zipfile, zlib
def png(shade):
    width, height = 400, 600
    raw = b"".join(b"\x00" + bytes([shade, 255 - shade, 128]) * width for _ in range(height))
    def chunk(kind, data): return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")
with zipfile.ZipFile(sys.argv[1], "w") as archive:
    for index, shade in enumerate((40, 120, 200), 1): archive.writestr("%03d.png" % index, png(shade))
PY
docker run -d --name komga -p 127.0.0.1:25600:25600 -v "$root/config:/config" -v "$root/comics:/comics" gotson/komga:latest >/dev/null
for i in $(seq 1 90); do curl -sf http://127.0.0.1:25600/api/v1/claim >/dev/null 2>&1 && break; sleep 2; done
curl -sf -X POST http://127.0.0.1:25600/api/v1/claim -H 'X-Komga-Email: ci@example.org' -H 'X-Komga-Password: ci-komga-pass' >/dev/null
curl -sf -u ci@example.org:ci-komga-pass -X POST http://127.0.0.1:25600/api/v1/libraries -H 'Content-Type: application/json' \
  -d '{"name":"Comics","root":"/comics","importComicInfoBook":true,"importComicInfoSeries":true,"importComicInfoCollection":true,"importComicInfoReadList":true,"importEpubBook":true,"importEpubSeries":true,"importMylarSeries":true,"importLocalArtwork":true,"importBarcodeIsbn":false,"scanForceModifiedTime":false,"scanDeep":false,"repairExtensions":false,"convertToCbz":false,"emptyTrashAfterScan":false,"seriesCover":"FIRST"}' >/dev/null
for i in $(seq 1 60); do
  n=$(curl -sf -u ci@example.org:ci-komga-pass 'http://127.0.0.1:25600/api/v1/books?media_status=READY' | python3 -c 'import json,sys; print(json.load(sys.stdin).get("totalElements",0))' 2>/dev/null || echo 0)
  [ "$n" -ge 1 ] && exit 0; sleep 2
done
echo "Komga did not analyse the fixture book" >&2; exit 1
