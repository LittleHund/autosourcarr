#!/usr/bin/env bash
# READ-1: builds the ONE prebuilt AutoSourcarr reader extension (Mihon/Tachiyomi APK + Suwayomi/source-host JAR)
# with no address or key baked in. Every server slot (5 by default, AUTOSOURCARR_READER_SLOTS for more, max 20)
# is configured in the reader's source settings. Writes the extension repository AutoSourcarr serves at
# /api/reader/repo/ (index.min.json, repo.json, apk/, icon/, jar/).
#
#   bash scripts/build-reader-extension.sh [output-dir]       (default: .build/reader-extension)
#
# Signing (GitHub secrets the owner adds; never commit them):
#   AUTOSOURCARR_EXT_KEYSTORE_B64     base64 of the existing signing keystore (.jks / .p12)
#   AUTOSOURCARR_EXT_KEYSTORE_PASSWORD, AUTOSOURCARR_EXT_KEY_ALIAS, AUTOSOURCARR_EXT_KEY_PASSWORD
# Without them the build is signed with a throwaway debug key and prints UNSIGNED-FOR-RELEASE: readers cannot
# update such an APK to (or from) the release-signed one.
#
# Needs a JDK the pinned Keiyoushi build accepts, the Android SDK (ANDROID_HOME / ANDROID_SDK_ROOT; GitHub's
# ubuntu runners have it), git, unzip and python3, plus network access to GitHub, Google Maven and Maven Central.
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
out=${1:-$root/.build/reader-extension}
work=${READER_EXTENSION_WORK:-$root/.build/keiyoushi}
pin=$(tr -d '\r\n ' < "$root/extension/keiyoushi-default.lock")
[[ "$pin" =~ ^[a-f0-9]{40}$ ]] || { echo 'extension/keiyoushi-default.lock must hold one full commit SHA.' >&2; exit 2; }
version=$(tr -d '\r\n' < "$root/VERSION")
for tool in git unzip python3; do command -v "$tool" >/dev/null || { echo "$tool is required" >&2; exit 2; }; done

find_apksigner() {
  command -v apksigner 2>/dev/null && return 0
  local base
  for base in "${ANDROID_HOME:-}" "${ANDROID_SDK_ROOT:-}" /usr/local/lib/android/sdk /opt/android-sdk; do
    [[ -n "$base" && -d "$base/build-tools" ]] || continue
    find "$base/build-tools" -maxdepth 2 -type f -name apksigner -print 2>/dev/null | sort -V | tail -n 1 | grep . && return 0
  done
  return 1
}

# 1. The pinned Keiyoushi checkout (same pin as the per-instance builder).
if [[ ! -d "$work/.git" ]]; then
  rm -rf "$work"; mkdir -p "$work"
  git -C "$work" init -q
  git -C "$work" remote add origin https://github.com/keiyoushi/extensions-source.git
fi
git -C "$work" fetch -q --depth 1 origin "$pin"
git -C "$work" checkout -q --force FETCH_HEAD
git -C "$work" clean -qfdx -e .gradle -e build
[[ "$(git -C "$work" rev-parse HEAD)" == "$pin" ]] || { echo 'Keiyoushi checkout differs from the pin.' >&2; exit 3; }

# 2. Stage the module: an explicit file list (never a recursive copy of a possibly stale tree).
target="$work/src/all/autosourcarr"
src_rel=src/eu/kanade/tachiyomi/extension/all/autosourcarr
rm -rf "$target"; mkdir -p "$target/$src_rel"
cp "$root/extension/module/build.gradle.kts" "$target/build.gradle.kts"
cp -a "$root/extension/module/res" "$target/res"
for file in AutoSourcarr.kt Dto.kt Filters.kt MiniJson.kt BuildAuth.kt; do
  cp "$root/extension/module/src/autosourcarr/$file" "$target/$src_rel/$file"
done
# The project BuildAuth.kt is credential-free; make sure nothing was baked in.
grep -q 'AUTOSOURCARR_READER_API_KEY = ""' "$target/$src_rel/BuildAuth.kt" || { echo 'BuildAuth.kt carries a reader key; refusing a release build.' >&2; exit 4; }
grep -q 'AUTOSOURCARR_DEFAULT_BASE_URL = ""' "$target/$src_rel/BuildAuth.kt" || { echo 'BuildAuth.kt carries an address; refusing a release build.' >&2; exit 4; }

# 3. Signing: Keiyoushi signs the APK and the JAR itself when <root>/signingkey.jks exists.
keystore="$work/signingkey.jks"
cleanup() { rm -f "$keystore"; }
trap cleanup EXIT
signed=0
if [[ -n "${AUTOSOURCARR_EXT_KEYSTORE_B64:-}" ]]; then
  for name in AUTOSOURCARR_EXT_KEYSTORE_PASSWORD AUTOSOURCARR_EXT_KEY_ALIAS; do
    [[ -n "${!name:-}" ]] || { echo "$name must be set with AUTOSOURCARR_EXT_KEYSTORE_B64." >&2; exit 5; }
  done
  (umask 077; printf '%s' "$AUTOSOURCARR_EXT_KEYSTORE_B64" | base64 -d > "$keystore")
  [[ -s "$keystore" ]] || { echo 'AUTOSOURCARR_EXT_KEYSTORE_B64 did not decode to a keystore.' >&2; exit 5; }
  export KEY_STORE_PASSWORD="$AUTOSOURCARR_EXT_KEYSTORE_PASSWORD" ALIAS="$AUTOSOURCARR_EXT_KEY_ALIAS"
  export KEY_PASSWORD="${AUTOSOURCARR_EXT_KEY_PASSWORD:-$AUTOSOURCARR_EXT_KEYSTORE_PASSWORD}"
  signed=1
else
  rm -f "$keystore"
  echo '::warning::UNSIGNED-FOR-RELEASE: AUTOSOURCARR_EXT_KEYSTORE_B64 is not set; the reader extension is signed with a throwaway debug key.' >&2
fi

# 4. Build (release APK + Keiyoushi's release JAR). No address: the gradle base URL is metadata only.
(
  cd "$work"
  export AUTOSOURCARR_VERSION="$version" AUTOSOURCARR_EXTENSION_BASE_URL="https://autosourcarr.invalid"
  ./gradlew --no-daemon -q src:all:autosourcarr:assembleRelease
)
cleanup
apk=$(find "$target/build/outputs/apk/release" -maxdepth 1 -type f -name '*.apk' | sort | head -n 1)
jar=$(find "$target/build/outputs/jar/release" -maxdepth 1 -type f -name '*.jar' | sort | head -n 1)
info="$target/build/keiyoushi-source-info.json"
[[ -s "$apk" && -s "$jar" && -s "$info" ]] || { echo 'Build did not produce the release APK, JAR and source info.' >&2; exit 6; }
bash "$root/extension/verify-tachiyomi-apk.sh" "$apk"
bash "$root/extension/verify-suwayomi-jar.sh" "$jar"
apksigner=$(find_apksigner) || { echo 'apksigner was not found in the Android SDK.' >&2; exit 7; }
certs=$("$apksigner" verify --print-certs "$apk")
fingerprint=$(awk -F': ' '/Signer #1 certificate SHA-256 digest:/ {print $2; exit}' <<< "$certs" | tr -d ': ' | tr 'A-F' 'a-f')
[[ "$fingerprint" =~ ^[0-9a-f]{64}$ ]] || { echo 'Could not read the APK signing fingerprint.' >&2; exit 7; }

# 5. The repository: legacy Tachiyomi/Mihon layout (index.min.json + repo.json, apk/, icon/), plus jar/.
rm -rf "$out.tmp"; mkdir -p "$out.tmp/apk" "$out.tmp/icon" "$out.tmp/jar"
apk_name=$(basename "$apk"); jar_name=$(basename "$jar")
cp "$apk" "$out.tmp/apk/$apk_name"; cp "$jar" "$out.tmp/jar/$jar_name"
cp "$root/extension/module/res/mipmap-xxxhdpi/ic_launcher.png" "$out.tmp/icon/eu.kanade.tachiyomi.extension.all.autosourcarr.png"
website=${AUTOSOURCARR_EXT_WEBSITE:-${GITHUB_REPOSITORY:+https://github.com/$GITHUB_REPOSITORY}}
python3 -I - "$info" "$out.tmp" "$apk_name" "$jar_name" "$fingerprint" "$signed" "$version" "$website" <<'PY'
import hashlib, json, sys
from pathlib import Path
info, out, apk, jar, fingerprint, signed, version, website = sys.argv[1:]
meta = json.loads(Path(info).read_text())
out = Path(out)
digest = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
index = [{
    "name": "Tachiyomi: " + meta["name"], "pkg": meta["packageName"], "apk": apk, "lang": "all",
    "code": meta["versionCode"], "version": meta["versionName"],
    # Keiyoushi contentWarning: 1 SAFE, 2 MIXED, 3 NSFW; the legacy index only knows nsfw 0/1.
    "nsfw": 0 if meta["contentWarning"] == 1 else 1,
    "sources": [{"name": s["name"], "lang": s["lang"], "id": s["id"], "baseUrl": ""} for s in meta["sources"]],
    # Extra fields (ignored by readers): AutoSourcarr checks downloads against them.
    "sha256": digest(out / "apk" / apk), "jar": jar, "jar_sha256": digest(out / "jar" / jar),
}]
(out / "index.min.json").write_text(json.dumps(index, separators=(",", ":")))
(out / "repo.json").write_text(json.dumps({"index_v2": None, "meta": {
    "name": "AutoSourcarr", "shortName": "AutoSourcarr", "website": website,
    "signingKeyFingerprint": fingerprint}}, indent=2))
(out / "build.json").write_text(json.dumps({"autosourcarr_version": version, "signed_for_release": signed == "1",
    "signing_fingerprint": fingerprint, "slots": len(meta["sources"])}, indent=2))
print(f"index: {meta['packageName']} {meta['versionName']} (code {meta['versionCode']}), {len(meta['sources'])} server slots")
PY
rm -rf "$out"; mv "$out.tmp" "$out"
echo "Reader extension repository: $out"
echo "  APK: apk/$apk_name  JAR: jar/$jar_name  signing SHA-256: $fingerprint"
if [[ "$signed" != 1 ]]; then
  echo 'UNSIGNED-FOR-RELEASE: signed with a throwaway debug key. Add the AUTOSOURCARR_EXT_* secrets before releasing.' >&2
fi
