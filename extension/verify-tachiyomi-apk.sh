#!/usr/bin/env bash
set -euo pipefail
APK="${1:-}"
[[ -n "$APK" && -f "$APK" ]] || { echo "Usage: $0 <extension.apk>" >&2; exit 2; }
[[ -s "$APK" ]] || { echo "APK is empty: $APK" >&2; exit 3; }
command -v unzip >/dev/null 2>&1 || { echo "unzip is required to validate the APK" >&2; exit 4; }
LIST="$(mktemp)"
trap 'rm -f "$LIST"' EXIT
unzip -Z1 "$APK" > "$LIST" || { echo "Invalid ZIP/APK: $APK" >&2; exit 5; }
grep -qx 'AndroidManifest.xml' "$LIST" || { echo "APK is missing AndroidManifest.xml" >&2; exit 6; }
grep -qE '^classes([0-9]+)?\.dex$' "$LIST" || { echo "APK contains no classes.dex bytecode" >&2; exit 7; }
# Keiyoushi extension APKs carry Android resources even when the source itself is
# mostly Kotlin. Requiring resources.arsc catches accidentally copied JARs renamed
# to .apk without depending on Android SDK tooling on the host.
grep -qx 'resources.arsc' "$LIST" || { echo "APK is missing resources.arsc" >&2; exit 8; }
if command -v apksigner >/dev/null 2>&1; then
  apksigner verify --verbose "$APK" >/dev/null || { echo "APK signature verification failed: $APK" >&2; exit 9; }
fi
printf 'Validated Tachiyomi/Mihon extension APK: %s\n' "$APK"
