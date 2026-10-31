#!/usr/bin/env bash
# Called after signing/validation by the SDK builder; never exposes credentials.
set -euo pipefail
APK="${1:?APK required}"; CONFIG_DIR="${2:?Config directory required}"; VERSION="${3:?Version required}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Invalid release version' >&2; exit 2; }
PACKAGE='eu.kanade.tachiyomi.extension.all.autosourcarr'
IFS=. read -r major minor patch <<< "$VERSION"
CODE=$((10#$major * 1000000 + 10#$minor * 1000 + 10#$patch))
(( CODE >= 9 )) || CODE=9
EXPECTED="1.4.$CODE"
AAPT="$(command -v aapt || true)"
APKSIGNER="$(command -v apksigner || true)"
for base in "${ANDROID_HOME:-}" "${ANDROID_SDK_ROOT:-}" /opt/android-sdk /opt/android-sdk-linux /sdk /usr/local/lib/android/sdk; do
  [[ -n "$base" && -d "$base/build-tools" ]] || continue
  if [[ -z "$AAPT" ]]; then AAPT="$(find "$base/build-tools" -maxdepth 2 -type f -name aapt -print | sort -V | tail -n 1)"; fi
  if [[ -z "$APKSIGNER" ]]; then APKSIGNER="$(find "$base/build-tools" -maxdepth 2 -type f -name apksigner -print | sort -V | tail -n 1)"; fi
done
[[ -n "$AAPT" && -n "$APKSIGNER" ]] || { echo 'Android aapt/apksigner are required for publication.' >&2; exit 3; }
"$APKSIGNER" verify "$APK" >/dev/null
BADGING="$("$AAPT" dump badging "$APK")"
ACTUAL_PACKAGE="$(sed -n "s/^package: name='\([^']*\)'.*/\1/p" <<< "$BADGING")"
ACTUAL_VERSION="$(sed -n "s/^package:.*versionName='\([^']*\)'.*/\1/p" <<< "$BADGING")"
[[ "$ACTUAL_PACKAGE" == "$PACKAGE" && "$ACTUAL_VERSION" == "$EXPECTED" ]] || { echo 'APK package or compiled version differs from this release; refusing publication.' >&2; exit 4; }
SHA="$(sha256sum "$APK" | awk '{print $1}')"
SIZE="$(wc -c < "$APK" | tr -d '[:space:]')"
[[ "$SHA" =~ ^[a-f0-9]{64}$ && "$SIZE" =~ ^[0-9]+$ ]] || exit 5
(( SIZE > 0 && SIZE <= 33554432 )) || { echo 'APK exceeds the download size limit.' >&2; exit 5; }
umask 077
DEST="$CONFIG_DIR/extension-downloads"
mkdir -p "$DEST"
STAGE="$(mktemp -d "$DEST/.publish.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT
cp "$APK" "$STAGE/artifact.apk"
[[ "$(sha256sum "$STAGE/artifact.apk" | awk '{print $1}')" == "$SHA" ]] || exit 6
printf '{"app_version":"%s","package":"%s","extension_version":"%s","sha256":"%s","size":%s,"signature_verified":true}\n' "$VERSION" "$PACKAGE" "$EXPECTED" "$SHA" "$SIZE" > "$STAGE/current.json"
# Prepare ownership and permissions BEFORE replacing the pointer. If a root SDK
# cannot hand files to the app user (for example a TrueNAS ACL/chown failure), its
# previous downloadable APK remains selected and readable. Standalone invocations
# without explicit ownership continue to use the caller's uid/gid.
PUBLISH_UID="${AUTOSOURCARR_PUBLISH_UID:-$(id -u)}"
PUBLISH_GID="${AUTOSOURCARR_PUBLISH_GID:-$(id -g)}"
[[ "$PUBLISH_UID" =~ ^[0-9]+$ && "$PUBLISH_GID" =~ ^[0-9]+$ ]] || { echo 'Publication UID/GID must be numeric.' >&2; exit 7; }
chmod 600 "$STAGE/artifact.apk" "$STAGE/current.json"
for prepared in "$STAGE/artifact.apk" "$STAGE/current.json" "$DEST"; do
  if [[ "$(stat -c '%u:%g' "$prepared")" != "$PUBLISH_UID:$PUBLISH_GID" ]]; then
    chown "$PUBLISH_UID:$PUBLISH_GID" "$prepared"
  fi
done
chmod 700 "$DEST"
mv "$STAGE/artifact.apk" "$DEST/$VERSION-$SHA.apk"
# Only this small pointer is replaced. A failed build keeps the last good one.
mv "$STAGE/current.json" "$DEST/current.json"
printf 'Published current APK for the AutoSourcarr Status download.\n'
