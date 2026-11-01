#!/usr/bin/env bash
# One instance by default. Set AUTOSOURCARR_PRIVATE_CONFIG_DIR to build both
# instances sequentially, reading each autosourcarr.env and signing key separately.
set -euo pipefail
ROOT="${AUTOSOURCARR_BUILD_ROOT:-/workspace}"
BUILD_MODE="${AUTOSOURCARR_BUILD_MODE:-auto}"
CHECK_ONLY=0
READER_CONFIG_CHECK="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/reader_build_config.py"
usage() {
  cat <<'EOF'
Usage: build-in-container.sh [--both | --single] [--check]

  --both   Require and build normal + private JAR/APK sequentially.
  --single Build the normal config only (the original default).
  --check  Check configuration and show output paths without compiling or writing.

Mount each app's actual config directory and set:
  AUTOSOURCARR_CONFIG_DIR=/config-main
  AUTOSOURCARR_PRIVATE_CONFIG_DIR=/config-private
  AUTOSOURCARR_BUILD_MODE=paired  (fails clearly if the private mount is missing)
Each directory must contain its own autosourcarr.env. Preserve existing signing
keys there; the first build creates a key only when no signing state exists.
Do not add either app's env_file to this shared builder.
Paired output: /workspace/extension/dist-instances/{autosourcarr,private-autosourcarr}/
Single output: /workspace/extension/dist/
EOF
}
for argument in "$@"; do
  case "$argument" in
    --both) BUILD_MODE=paired ;;
    --single) BUILD_MODE=single ;;
    --check) CHECK_ONLY=1 ;;
    --help|-h) usage; exit 0 ;;
    *) printf 'ERROR: unknown builder option: %s\n' "$argument" >&2; usage >&2; exit 24 ;;
  esac
done
case "$BUILD_MODE" in auto|single|paired) ;; *) echo 'ERROR: AUTOSOURCARR_BUILD_MODE must be auto, single or paired.' >&2; exit 24 ;; esac
EXPLICIT_CONFIG=0
[[ -z "${AUTOSOURCARR_CONFIG_DIR:-}" ]] || EXPLICIT_CONFIG=1
PAIRED_BUILD=0
if [[ "$BUILD_MODE" == paired || ( "$BUILD_MODE" == auto && -n "${AUTOSOURCARR_PRIVATE_CONFIG_DIR:-}" ) ]]; then
  PAIRED_BUILD=1
  [[ -n "${AUTOSOURCARR_PRIVATE_CONFIG_DIR:-}" ]] || {
    echo 'ERROR: paired build requested but AUTOSOURCARR_PRIVATE_CONFIG_DIR is not set. Mount the private app config and set its container path; private APK/JAR files were not built.' >&2
    exit 21
  }
fi

# Read literal dotenv values without executing shell code or substituting a
# different instance's environment. Support ordinary quotes, spaces and CRLF.
read_config_value() {
  local file="$1/autosourcarr.env" key="$2"
  [[ -f "$file" ]] || return 0
  awk -v wanted="$key" '
    {sub(/\r$/, ""); line=$0; sub(/^[ \t]*export[ \t]+/, "", line);
     equals=index(line,"="); if (!equals) next;
     name=substr(line,1,equals-1); gsub(/^[ \t]+|[ \t]+$/, "", name);
     if (name!=wanted) next;
     value=substr(line,equals+1); gsub(/^[ \t]+|[ \t]+$/, "", value);
     quote=substr(value,1,1);
     if (quote=="\"" || quote==sprintf("%c",39)) {
       closing=index(substr(value,2),quote);
       if (closing && substr(value,closing+2) ~ /^[ \t]*(#.*)?$/)
         value=substr(value,2,closing-1);
     } else {sub(/[ \t]+#.*/, "", value); sub(/[ \t]+$/, "", value)}
     print value; exit}
  ' "$file"
}

require_config() {
  [[ -s "$1/autosourcarr.env" ]] || {
    printf 'ERROR: missing configuration: %s/autosourcarr.env. Mount the correct instance config; no fallback was used.\n' "$1" >&2
    exit 21
  }
}
if [[ "$EXPLICIT_CONFIG" == "1" || "$PAIRED_BUILD" == "1" ]]; then
  require_config "${AUTOSOURCARR_CONFIG_DIR:-$ROOT/config}"
fi
if [[ "$PAIRED_BUILD" == "1" ]]; then
  require_config "$AUTOSOURCARR_PRIVATE_CONFIG_DIR"
  # Both destinations must be explicit; never build a private JAR for the normal
  # default Docker URL merely because a copied env omitted a key.
  for config in "${AUTOSOURCARR_CONFIG_DIR:-$ROOT/config}" "$AUTOSOURCARR_PRIVATE_CONFIG_DIR"; do
    for key in AUTOSOURCARR_INTERNAL_URL AUTOSOURCARR_PUBLIC_URL AUTOSOURCARR_READER_API_TOKEN; do
      value="$(read_config_value "$config" "$key")"
      [[ -n "$value" ]] || { printf 'ERROR: %s must be set in %s/autosourcarr.env.\n' "$key" "$config" >&2; exit 21; }
    done
  done
  if [[ "${AUTOSOURCARR_CONFIG_DIR:-$ROOT/config}/autosourcarr.env" -ef "$AUTOSOURCARR_PRIVATE_CONFIG_DIR/autosourcarr.env" ]]; then
    echo 'ERROR: normal and private instances must have separate configuration files.' >&2
    exit 22
  fi
  main_internal="$(read_config_value "${AUTOSOURCARR_CONFIG_DIR:-$ROOT/config}" AUTOSOURCARR_INTERNAL_URL)"
  private_internal="$(read_config_value "$AUTOSOURCARR_PRIVATE_CONFIG_DIR" AUTOSOURCARR_INTERNAL_URL)"
  if [[ "${main_internal%/}" == "${private_internal%/}" ]]; then
    echo 'ERROR: the two configs point at the same AUTOSOURCARR_INTERNAL_URL. Set the private config to its own AutoSourcarr service before building.' >&2
    exit 22
  fi
fi
for owner in "${AUTOSOURCARR_PUBLISH_UID:-0}" "${AUTOSOURCARR_PUBLISH_GID:-0}"; do
  [[ "$owner" =~ ^[0-9]+$ ]] || { echo 'Publication UID/GID must be numeric.' >&2; exit 23; }
done

check_instance() {
  local name="$1" config="$2" output="$3" token internal public key value
  local AUTOSOURCARR_INTERNAL_URL="${AUTOSOURCARR_INTERNAL_URL:-}"
  local AUTOSOURCARR_PUBLIC_URL="${AUTOSOURCARR_PUBLIC_URL:-}"
  local AUTOSOURCARR_READER_API_TOKEN="${AUTOSOURCARR_READER_API_TOKEN:-}"
  if [[ "$PAIRED_BUILD" == 1 ]]; then
    AUTOSOURCARR_INTERNAL_URL= AUTOSOURCARR_PUBLIC_URL= AUTOSOURCARR_READER_API_TOKEN=
  fi
  internal="${AUTOSOURCARR_INTERNAL_URL:-$(read_config_value "$config" AUTOSOURCARR_INTERNAL_URL)}"
  internal="${internal:-http://autosourcarr:8282}"
  public="${AUTOSOURCARR_PUBLIC_URL:-$(read_config_value "$config" AUTOSOURCARR_PUBLIC_URL)}"
  public="${public:-$internal}"
  token="${AUTOSOURCARR_READER_API_TOKEN:-$(read_config_value "$config" AUTOSOURCARR_READER_API_TOKEN)}"
  [[ -s "$READER_CONFIG_CHECK" ]] && command -v python3 >/dev/null || {
    echo 'ERROR: shared reader configuration checker is missing. Restore the complete project/image before building.' >&2; exit 24;
  }
  AUTOSOURCARR_INTERNAL_URL="$internal" AUTOSOURCARR_PUBLIC_URL="$public" AUTOSOURCARR_READER_API_TOKEN="$token" \
    python3 "$READER_CONFIG_CHECK" --check-env --name "$name"
  if [[ -s "$config/.extension-signing/autosourcarr-extension.jks" && ! -s "$config/.extension-signing/signing.env" ]] ||
     [[ -s "$config/.extension-signing/signing.env" && ! -s "$config/.extension-signing/autosourcarr-extension.jks" ]]; then
    printf 'ERROR: %s signing state is incomplete; restore both retained signing files before building.\n' "$name" >&2; exit 15
  fi
  printf '  %s: config %s → %s\n' "$name" "$config" "$output"
  printf '    JAR endpoint: %s; Android endpoint: %s\n' "$internal" "$public"
}
BUILD_OUTPUT_DIR="${AUTOSOURCARR_BUILD_OUTPUT_DIR:-$ROOT/extension/dist-instances}"
[[ "$CHECK_ONLY" == 1 ]] || echo 'Waiting for the shared AutoSourcarr extension builder...'
if [[ "$PAIRED_BUILD" == 1 ]]; then
  echo 'Build plan: normal JAR → normal APK → private JAR → private APK.'
  check_instance autosourcarr "${AUTOSOURCARR_CONFIG_DIR:-$ROOT/config}" "$BUILD_OUTPUT_DIR/autosourcarr"
  check_instance private-autosourcarr "$AUTOSOURCARR_PRIVATE_CONFIG_DIR" "$BUILD_OUTPUT_DIR/private-autosourcarr"
else
  echo 'Build plan: one instance only; private APK/JAR files will NOT be built.'
  echo 'To build both, mount the private config and set AUTOSOURCARR_PRIVATE_CONFIG_DIR plus AUTOSOURCARR_BUILD_MODE=paired.'
  check_instance autosourcarr "${AUTOSOURCARR_CONFIG_DIR:-$ROOT/config}" "${AUTOSOURCARR_INSTANCE_DIST:-$ROOT/extension/dist}"
fi
if [[ "$CHECK_ONLY" == 1 ]]; then
  echo 'Configuration check passed. No files were written, no downloads or compilation were run.'
  exit 0
fi

# Serialize every invocation, including old single-instance Compose services that
# now call this updated script. The same lock covers checkout, caches and outputs.
command -v flock >/dev/null || { echo 'The Android SDK image needs flock (util-linux).' >&2; exit 24; }
mkdir -p "$ROOT/extension"
exec 9>"$ROOT/extension/.builder.lock"
flock -x 9

build_single_instance() (

if ! command -v git >/dev/null 2>&1; then
  apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq git >/dev/null
fi

WORK="$ROOT/extension/.build/extensions-source"
TARGET="$WORK/src/all/autosourcarr"
LEGACY_TARGET="$WORK/src/all/suwayomicompanion"
PUBLISH_DIST="${AUTOSOURCARR_INSTANCE_DIST:-$ROOT/extension/dist}"
mkdir -p "$(dirname "$PUBLISH_DIST")"
DIST="$(mktemp -d "$ROOT/extension/.dist-stage.XXXXXX")"
JAR_LIST=""
OUTPUT_SWAPPED=0
OUTPUT_COMMITTED=0
cleanup_build() {
  [[ -z "$JAR_LIST" ]] || rm -f "$JAR_LIST"
  [[ -z "$DIST" ]] || rm -rf "$DIST"
  # Distribution handoff precedes the shared reader-generation commit.
  # Failed preparation restores the preceding output without changing downloads.
  if [[ "$OUTPUT_SWAPPED" == 1 && "$OUTPUT_COMMITTED" != 1 ]]; then
    rm -rf "$PUBLISH_DIST"
  fi
  if [[ ! -e "$PUBLISH_DIST" && -d "$PUBLISH_DIST.previous" ]]; then
    mv "$PUBLISH_DIST.previous" "$PUBLISH_DIST"
  fi
}
trap cleanup_build EXIT
LOCK_FILE="$ROOT/extension/keiyoushi-commit.lock"
DEFAULT_LOCK="$ROOT/extension/keiyoushi-default.lock"
UPDATE_KEIYOUSHI="${KEIYOUSHI_UPDATE:-0}"
GRADLE_BOOTSTRAP_RETRIES="${GRADLE_BOOTSTRAP_RETRIES:-4}"
GRADLE_BUILD_RETRIES="${GRADLE_BUILD_RETRIES:-2}"
GRADLE_RETRY_DELAY_SECONDS="${GRADLE_RETRY_DELAY_SECONDS:-5}"

# A drag-and-drop overlay cannot remove helper files whose old names contained a
# release number. Clean those legacy files whenever the builder runs. The current
# helper deliberately has the stable name compose-env-file.yml.
if [[ -x "$ROOT/scripts/cleanup-old-helper-files.sh" ]]; then
  "$ROOT/scripts/cleanup-old-helper-files.sh"
fi

# Keep Gradle itself and downloaded dependencies inside the persistent project
# mount. Recreating the temporary builder container therefore does not force a
# fresh Gradle distribution/dependency download every time.
export GRADLE_USER_HOME="${GRADLE_USER_HOME:-$ROOT/extension/.gradle-home}"
mkdir -p "$GRADLE_USER_HOME"

# Existing per-instance selections take precedence over the shipped default.
# The distinct default filename cannot overwrite a retained host-builder pin
# when an archive is copied over the existing project.
[[ "$UPDATE_KEIYOUSHI" == 0 || "$UPDATE_KEIYOUSHI" == 1 ]] || { echo 'KEIYOUSHI_UPDATE must be 0 or 1.' >&2; exit 27; }
PINNED_COMMIT=""
if [[ "$UPDATE_KEIYOUSHI" != 1 ]]; then
  SELECTED_LOCK="$LOCK_FILE"
  [[ -s "$SELECTED_LOCK" ]] || SELECTED_LOCK="$DEFAULT_LOCK"
  [[ -s "$SELECTED_LOCK" ]] || { echo 'The reviewed Keiyoushi default pin is missing; rebuild this release image.' >&2; exit 27; }
  PINNED_COMMIT="$(tr -d '\r\n ' < "$SELECTED_LOCK")"
  [[ "$PINNED_COMMIT" =~ ^[a-f0-9]{40}$ ]] || { echo 'Keiyoushi pin must be one complete commit SHA; no build was started.' >&2; exit 27; }
fi
REPOSITORY='https://github.com/keiyoushi/extensions-source.git'
if [[ ! -d "$WORK/.git" ]]; then
  mkdir -p "$WORK"
  git init "$WORK"
  git -C "$WORK" remote add origin "$REPOSITORY"
fi
ORIGIN="$(git -C "$WORK" remote get-url origin)"
[[ "$ORIGIN" == "$REPOSITORY" || "$ORIGIN" == "${REPOSITORY%.git}" ]] || { echo 'Keiyoushi checkout has an unexpected origin; refusing to build it.' >&2; exit 27; }
if [[ "$UPDATE_KEIYOUSHI" == 1 ]]; then
  git -C "$WORK" fetch --depth=1 origin main
  git -C "$WORK" reset --hard origin/main
  PINNED_COMMIT="$(git -C "$WORK" rev-parse HEAD)"
  [[ "$PINNED_COMMIT" =~ ^[a-f0-9]{40}$ ]] || { echo 'Keiyoushi update did not resolve to a complete commit SHA.' >&2; exit 27; }
else
  if ! git -C "$WORK" cat-file -e "${PINNED_COMMIT}^{commit}" 2>/dev/null; then
    git -C "$WORK" fetch --depth=1 origin "$PINNED_COMMIT"
  fi
  git -C "$WORK" reset --hard "$PINNED_COMMIT"
fi
[[ "$(git -C "$WORK" rev-parse HEAD)" == "$PINNED_COMMIT" ]] || { echo 'Keiyoushi checkout differs from the selected commit.' >&2; exit 27; }
PIN_STAGE="$(mktemp "$LOCK_FILE.XXXXXX")"
printf '%s\n' "$PINNED_COMMIT" > "$PIN_STAGE"
chmod 644 "$PIN_STAGE"
mv "$PIN_STAGE" "$LOCK_FILE"
printf 'Using pinned Keiyoushi revision: %s\n' "$PINNED_COMMIT"

# The Keiyoushi checkout is persisted between builder runs. `git reset --hard`
# does not remove untracked custom source directories, so explicitly remove both
# the current module and the pre-rename legacy module before copying AutoSourcarr.
rm -rf "$TARGET" "$LEGACY_TARGET"
mkdir -p "$TARGET"
# Copy only the current extension module files. This intentionally does not copy
# arbitrary stale Kotlin files that may remain when a user updates the project by
# dragging a new release over the old project directory.
cp "$ROOT/extension/module/build.gradle.kts" "$TARGET/build.gradle.kts"
if [[ -d "$ROOT/extension/module/res" ]]; then
  cp -a "$ROOT/extension/module/res" "$TARGET/res"
fi
# The project keeps the Kotlin files at a short path; the Keiyoushi checkout gets the
# conventional package directory. The package declaration itself is unchanged.
SRC_REL="src/eu/kanade/tachiyomi/extension/all/autosourcarr"
PROJECT_SRC_REL="src/autosourcarr"
mkdir -p "$TARGET/$SRC_REL"
for file in AutoSourcarr.kt Dto.kt Filters.kt MiniJson.kt BuildAuth.kt; do
  cp "$ROOT/extension/module/$PROJECT_SRC_REL/$file" "$TARGET/$SRC_REL/$file"
done

cd "$WORK"

export AUTOSOURCARR_VERSION="$(tr -d '\r\n' < "$ROOT/VERSION")"
printf 'Building AutoSourcarr backend/source version: %s\n' "$AUTOSOURCARR_VERSION"

# Make the Gradle wrapper tolerant of slower/interrupted downloads. The wrapper
# itself has no useful retry policy, so bootstrap it separately before building.
WRAPPER_PROPS="$WORK/gradle/wrapper/gradle-wrapper.properties"
if [[ -f "$WRAPPER_PROPS" ]]; then
  if grep -q '^networkTimeout=' "$WRAPPER_PROPS"; then
    sed -i 's/^networkTimeout=.*/networkTimeout=60000/' "$WRAPPER_PROPS"
  else
    printf '\nnetworkTimeout=60000\n' >> "$WRAPPER_PROPS"
  fi
fi

DIST_URL="$(grep '^distributionUrl=' "$WRAPPER_PROPS" 2>/dev/null | cut -d= -f2- | sed 's/\\:/:/g' || true)"
DIST_ZIP="${DIST_URL##*/}"
DIST_DIR_NAME="${DIST_ZIP%.zip}"

gradle_ready=0
for ((attempt=1; attempt<=GRADLE_BOOTSTRAP_RETRIES; attempt++)); do
  printf 'Preparing Gradle (attempt %d/%d)...\n' "$attempt" "$GRADLE_BOOTSTRAP_RETRIES"
  if ./gradlew --no-daemon --version >/dev/null; then
    gradle_ready=1
    break
  fi

  if (( attempt < GRADLE_BOOTSTRAP_RETRIES )); then
    # Remove only the incomplete wrapper distribution before retrying. A fully
    # downloaded/cached distribution is retained across future builder runs.
    if [[ -n "$DIST_DIR_NAME" ]]; then
      rm -rf "$GRADLE_USER_HOME/wrapper/dists/$DIST_DIR_NAME"
    fi
    find "$GRADLE_USER_HOME/wrapper/dists" -type f \( -name '*.part' -o -name '*.lck' \) -delete 2>/dev/null || true
    sleep "$((GRADLE_RETRY_DELAY_SECONDS * attempt))"
  fi
done

if [[ "$gradle_ready" != "1" ]]; then
  echo "Unable to download/start the Gradle wrapper after $GRADLE_BOOTSTRAP_RETRIES attempts." >&2
  exit 3
fi

# Build the Suwayomi JAR and Android APK with different network base URLs.
# A Docker service name such as http://autosourcarr:8282 is correct for Suwayomi
# on the shared Compose network but cannot resolve on an Android phone/tablet.
# Conversely, an external reverse-proxy hostname may sit behind interactive Authelia
# and is not appropriate for the internal JAR.  Read both from the project env file.
CONFIG_DIR="${AUTOSOURCARR_CONFIG_DIR:-$ROOT/config}"

# v0.2.69 moves credentials/signing state out of the replaceable project tree.
# When the persistent mount is present but has not been initialized yet, rescue
# the v0.2.68 files before generating anything new. This is especially important
# for Android: losing the old private signing key makes Android reject updates.
if [[ "$CONFIG_DIR" != "$ROOT/config" && "$PAIRED_BUILD" == "0" ]] && {
  [[ "$EXPLICIT_CONFIG" == "0" ]] || cmp -s "$CONFIG_DIR/autosourcarr.env" "$ROOT/config/autosourcarr.env"
}; then
  mkdir -p "$CONFIG_DIR"
  if [[ ! -s "$CONFIG_DIR/autosourcarr.env" && -s "$ROOT/config/autosourcarr.env" ]]; then
    cp "$ROOT/config/autosourcarr.env" "$CONFIG_DIR/autosourcarr.env"
    chmod 600 "$CONFIG_DIR/autosourcarr.env" 2>/dev/null || true
    echo "Migrated project-local AutoSourcarr env into persistent config."
  fi
  if [[ ! -e "$CONFIG_DIR/.extension-signing" && -d "$ROOT/config/.extension-signing" ]]; then
    cp -a "$ROOT/config/.extension-signing" "$CONFIG_DIR/.extension-signing"
    echo "Migrated project-local Android signing identity into persistent config."
  fi
fi

read_project_env() {
  read_config_value "$CONFIG_DIR" "$1"
}

# A paired build must never inherit the other instance's Compose env_file.
if [[ "$PAIRED_BUILD" == "1" ]]; then
  unset AUTOSOURCARR_INTERNAL_URL AUTOSOURCARR_PUBLIC_URL AUTOSOURCARR_READER_API_TOKEN
  unset AUTOSOURCARR_AUTHELIA_BASIC_USERNAME AUTOSOURCARR_AUTHELIA_BASIC_PASSWORD
fi
INTERNAL_BASE_URL="${AUTOSOURCARR_INTERNAL_URL:-$(read_project_env AUTOSOURCARR_INTERNAL_URL)}"
ANDROID_BASE_URL="${AUTOSOURCARR_PUBLIC_URL:-$(read_project_env AUTOSOURCARR_PUBLIC_URL)}"
READER_API_TOKEN="${AUTOSOURCARR_READER_API_TOKEN:-$(read_project_env AUTOSOURCARR_READER_API_TOKEN)}"
LEGACY_AUTHELIA_USER="${AUTOSOURCARR_AUTHELIA_BASIC_USERNAME:-$(read_project_env AUTOSOURCARR_AUTHELIA_BASIC_USERNAME)}"
LEGACY_AUTHELIA_PASS="${AUTOSOURCARR_AUTHELIA_BASIC_PASSWORD:-$(read_project_env AUTOSOURCARR_AUTHELIA_BASIC_PASSWORD)}"
INTERNAL_BASE_URL="${INTERNAL_BASE_URL:-http://autosourcarr:8282}"
ANDROID_BASE_URL="${ANDROID_BASE_URL:-$INTERNAL_BASE_URL}"
INTERNAL_BASE_URL="${INTERNAL_BASE_URL%/}"
ANDROID_BASE_URL="${ANDROID_BASE_URL%/}"

if [[ -n "$LEGACY_AUTHELIA_USER" || -n "$LEGACY_AUTHELIA_PASS" ]]; then
  echo 'WARNING: AUTOSOURCARR_AUTHELIA_BASIC_USERNAME/PASSWORD are deprecated and ignored by v0.2.68+ Android builds.' >&2
  echo 'Use AUTOSOURCARR_READER_API_TOKEN and bypass only /api/reader/v1/ in Authelia.' >&2
fi
# Settings may have changed while the checkout/toolchain was prepared. Validate
# the exact values about to be embedded through the same contract used at entry.
AUTOSOURCARR_INTERNAL_URL="$INTERNAL_BASE_URL" AUTOSOURCARR_PUBLIC_URL="$ANDROID_BASE_URL" AUTOSOURCARR_READER_API_TOKEN="$READER_API_TOKEN" \
  python3 "$READER_CONFIG_CHECK" --check-env --name "${AUTOSOURCARR_INSTANCE_NAME:-autosourcarr}"
if (( ${#READER_API_TOKEN} < 64 )); then
  echo 'WARNING: use a random 64+ character AUTOSOURCARR_READER_API_TOKEN where possible.' >&2
fi

write_auth_config() {
  local api_prefix="$1" reader_key="$2"
  cat > "$TARGET/$SRC_REL/BuildAuth.kt" <<EOF
package eu.kanade.tachiyomi.extension.all.autosourcarr

// Generated only inside the isolated Keiyoushi build checkout. The project source
// stays credential-free. Android uses the token-protected reader prefix; Suwayomi
// continues to use the ordinary Docker-internal API without an external credential.
internal const val AUTOSOURCARR_API_PREFIX = "$api_prefix"
internal const val AUTOSOURCARR_READER_API_KEY = "$reader_key"
// Server slot 1's default address; slots stay editable in the reader's source settings.
internal const val AUTOSOURCARR_DEFAULT_BASE_URL = "${AUTH_DEFAULT_BASE_URL:-}"
EOF
}

SIGN_DIR="$CONFIG_DIR/.extension-signing"
SIGN_KEYSTORE="$SIGN_DIR/autosourcarr-extension.jks"
SIGN_ALIAS="autosourcarr"

prepare_signing_key() {
  python3 "$ROOT/extension/signing_state.py" "$CONFIG_DIR"
}

find_apksigner() {
  if command -v apksigner >/dev/null 2>&1; then
    command -v apksigner
    return 0
  fi
  local base candidate
  for base in "${ANDROID_HOME:-}" "${ANDROID_SDK_ROOT:-}" /opt/android-sdk /opt/android-sdk-linux /sdk /usr/local/lib/android/sdk; do
    [[ -n "$base" && -d "$base/build-tools" ]] || continue
    candidate="$(find "$base/build-tools" -maxdepth 2 -type f -name apksigner -print 2>/dev/null | sort -V | tail -n 1)"
    if [[ -n "$candidate" ]]; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done
  return 1
}

sign_android_apk() {
  local input_apk="$1" output_apk="$2"
  local password apksigner status report digest
  password="$(prepare_signing_key)" || { status=$?; echo "ERROR: signing identity preparation failed (exit $status)." >&2; return "$status"; }
  apksigner="$(find_apksigner)" || { echo 'ERROR: apksigner was not found in the Android builder image.' >&2; return 17; }
  if [[ -z "$apksigner" ]]; then
    echo 'ERROR: apksigner was not found in the Android builder image.' >&2
    return 17
  fi
  AUTOSOURCARR_SIGNING_PASSWORD="$password" "$apksigner" sign \
    --ks "$SIGN_KEYSTORE" \
    --ks-key-alias "$SIGN_ALIAS" \
    --ks-pass env:AUTOSOURCARR_SIGNING_PASSWORD \
    --key-pass env:AUTOSOURCARR_SIGNING_PASSWORD \
    --out "$output_apk" \
    "$input_apk" >/dev/null || { status=$?; echo "ERROR: APK signing failed (exit $status)." >&2; return "$status"; }
  report="$("$apksigner" verify --verbose --print-certs "$output_apk")" || {
    status=$?; echo "ERROR: APK signature verification failed (exit $status)." >&2; return "$status";
  }
  digest="$(awk -F': ' '/Signer #1 certificate SHA-256 digest:/ {print $2; exit}' <<< "$report")" || return $?
  [[ "$digest" =~ ^[0-9a-fA-F]{64}$ ]] || { echo 'ERROR: could not read a valid Android signing certificate fingerprint.' >&2; return 18; }
  printf '%s\n' "$digest"
}

printf 'Suwayomi JAR base URL: %s\n' "$INTERNAL_BASE_URL"
printf 'Android APK base URL: %s\n' "$ANDROID_BASE_URL"
printf 'Android reader API: /api/reader/v1/ with AutoSourcarr token authentication\n'
if [[ "$ANDROID_BASE_URL" == *'://autosourcarr:'* || "$ANDROID_BASE_URL" == *'://autosourcarr/'* ]]; then
  if [[ "${AUTOSOURCARR_ALLOW_DOCKER_ANDROID_URL:-0}" != "1" ]]; then
    echo 'ERROR: Android APK base URL is the Docker-only hostname autosourcarr. Set AUTOSOURCARR_PUBLIC_URL to the HTTPS hostname reachable from Android, then rebuild.' >&2
    exit 11
  fi
  echo 'WARNING: building Android APK with Docker-only autosourcarr hostname because AUTOSOURCARR_ALLOW_DOCKER_ANDROID_URL=1.' >&2
fi

run_release_build() {
  local label="$1" base_url="$2"
  local ok=0
  for ((attempt=1; attempt<=GRADLE_BUILD_RETRIES; attempt++)); do
    printf 'Building %s (attempt %d/%d) with base URL %s...\n' "$label" "$attempt" "$GRADLE_BUILD_RETRIES" "$base_url"
    if AUTOSOURCARR_EXTENSION_BASE_URL="$base_url" ./gradlew --no-daemon --max-workers=2 --no-parallel '-Dorg.gradle.jvmargs=-Xmx1536m -XX:ActiveProcessorCount=2' src:all:autosourcarr:assembleRelease; then
      ok=1
      break
    fi
    if (( attempt < GRADLE_BUILD_RETRIES )); then
      sleep "$((GRADLE_RETRY_DELAY_SECONDS * attempt))"
    fi
  done
  [[ "$ok" == "1" ]] || { echo "$label build failed after $GRADLE_BUILD_RETRIES attempts." >&2; return 1; }
}

# First build the JVM-ready Suwayomi artifact against the internal service URL.
# It talks directly over the Docker network and keeps the ordinary API prefix.
AUTH_DEFAULT_BASE_URL="$INTERNAL_BASE_URL"
write_auth_config "/api/v1" ""
run_release_build 'Suwayomi release JAR' "$INTERNAL_BASE_URL" || exit 4
JAR_ROOT="$TARGET/build/outputs/jar/release"
mapfile -t jars < <(find "$JAR_ROOT" -maxdepth 1 -type f -name '*.jar' -print 2>/dev/null | sort)
if [[ "${#jars[@]}" -ne 1 ]]; then
  echo "Expected exactly one release JAR under $JAR_ROOT, found ${#jars[@]}." >&2
  find "$TARGET/build/outputs" -maxdepth 5 -type f -print 2>/dev/null || true
  exit 2
fi
SOURCE_JAR="${jars[0]}"
[[ -s "$SOURCE_JAR" ]] || { echo "JAR is empty: $SOURCE_JAR" >&2; exit 5; }
TEMP_JAR="$DIST/.autosourcarr-suwayomi.jar"
cp "$SOURCE_JAR" "$TEMP_JAR"

# Rebuild with the externally reachable URL for Android. Inject the dedicated
# AutoSourcarr reader API prefix/key only into this artifact. clean is important: the
# Gradle source configuration is evaluated per invocation and both variants otherwise
# share the same output path/cache key.
AUTH_DEFAULT_BASE_URL="$ANDROID_BASE_URL"
write_auth_config "/api/reader/v1" "$READER_API_TOKEN"
AUTOSOURCARR_EXTENSION_BASE_URL="$ANDROID_BASE_URL" ./gradlew --no-daemon --max-workers=2 --no-parallel '-Dorg.gradle.jvmargs=-Xmx1536m -XX:ActiveProcessorCount=2' src:all:autosourcarr:clean >/dev/null
run_release_build 'Tachiyomi/Mihon release APK' "$ANDROID_BASE_URL" || exit 4
APK_ROOT="$TARGET/build/outputs/apk/release"
mapfile -t apks < <(find "$APK_ROOT" -maxdepth 1 -type f -name '*.apk' -print 2>/dev/null | sort)
if [[ "${#apks[@]}" -ne 1 ]]; then
  echo "Expected exactly one release APK under $APK_ROOT, found ${#apks[@]}." >&2
  find "$TARGET/build/outputs" -maxdepth 5 -type f -print 2>/dev/null || true
  exit 9
fi
SOURCE_APK="${apks[0]}"
[[ -s "$SOURCE_APK" ]] || { echo "APK is empty: $SOURCE_APK" >&2; exit 10; }
SIGNED_SOURCE_APK="$DIST/.autosourcarr-android-signed.apk"
SIGNER_SHA256="$(sign_android_apk "$SOURCE_APK" "$SIGNED_SOURCE_APK")"
[[ -s "$SIGNED_SOURCE_APK" ]] || { echo "Signed APK is empty: $SIGNED_SOURCE_APK" >&2; exit 18; }
TEMP_APK="$DIST/.autosourcarr-android.apk"
cp "$SIGNED_SOURCE_APK" "$TEMP_APK"

ARTIFACT_NAME=AutoSourcarr
[[ "${AUTOSOURCARR_BUILD_INSTANCE:-}" != private-autosourcarr ]] || ARTIFACT_NAME=AutoSourcarr-private
INSTALL_JAR="$DIST/${ARTIFACT_NAME}-v${AUTOSOURCARR_VERSION}-Suwayomi.jar"
INSTALL_APK="$DIST/${ARTIFACT_NAME}-v${AUTOSOURCARR_VERSION}-Tachiyomi-Mihon.apk"
mv "$TEMP_JAR" "$INSTALL_JAR"
mv "$TEMP_APK" "$INSTALL_APK"

JAR_LIST="$(mktemp)"
jar tf "$INSTALL_JAR" > "$JAR_LIST" || { echo "Invalid ZIP/JAR: $SOURCE_JAR" >&2; exit 6; }
grep -qx 'AndroidManifest.xml' "$JAR_LIST" || { echo "JAR is missing AndroidManifest.xml" >&2; exit 7; }
grep -qE '\.class$' "$JAR_LIST" || { echo "JAR contains no JVM class bytecode" >&2; exit 8; }
# Do NOT require the source class to keep its Kotlin source filename/path here.
# Keiyoushi's createReleaseExtensionJar task runs ProGuard and may emit a generated
# or renamed entry class while also updating AndroidManifest.xml accordingly. The
# v0.2.11 builder incorrectly required AutoSourcarr.class and therefore aborted
# after a completely successful Gradle build. Suwayomi resolves the actual main
# class from the JAR's AndroidManifest.xml, so validate the official artifact as a
# whole instead of second-guessing Keiyoushi's generated class name.

bash "$ROOT/extension/verify-suwayomi-jar.sh" "$INSTALL_JAR"
bash "$ROOT/extension/verify-tachiyomi-apk.sh" "$INSTALL_APK"

# Publication ownership is prepared on staged files before either app pointer or
# artifact directory is replaced. Keep the persistent config and signing keys as-is.
PUBLISH_UID="${AUTOSOURCARR_PUBLISH_UID:-$(stat -c %u "$CONFIG_DIR")}"
PUBLISH_GID="${AUTOSOURCARR_PUBLISH_GID:-$(stat -c %g "$CONFIG_DIR")}"

cat > "$DIST/README.txt" <<EOF
AutoSourcarr $AUTOSOURCARR_VERSION extension artifacts (${AUTOSOURCARR_BUILD_INSTANCE:-autosourcarr})

Suwayomi (corresponding library instance):
  $(basename "$INSTALL_JAR")

Tachiyomi / Mihon-compatible Android clients:
  $(basename "$INSTALL_APK")

Suwayomi JAR base URL: $INTERNAL_BASE_URL
Android APK base URL: $ANDROID_BASE_URL
Android reader API auth: token-protected /api/reader/v1/
Android signing certificate SHA-256: $SIGNER_SHA256

Suwayomi 2.3.2243+ supports external extension JAR files directly. AutoSourcarr
uses the Keiyoushi release JAR there so Suwayomi does not have to convert the APK
with dex2jar. The APK is the normal Android extension artifact produced by the same
Keiyoushi Release build and is intended for Tachiyomi/Mihon-compatible clients.
EOF

printf 'Built and validated Suwayomi JAR: %s\n' "$INSTALL_JAR"
printf '  Size: %s bytes\n' "$(wc -c < "$INSTALL_JAR")"
printf '  SHA-256: %s\n' "$(sha256sum "$INSTALL_JAR" | awk '{print $1}')"
printf 'Built and validated Tachiyomi/Mihon APK: %s\n' "$INSTALL_APK"
printf '  Signing certificate SHA-256: %s\n' "$SIGNER_SHA256"
printf '  Size: %s bytes\n' "$(wc -c < "$INSTALL_APK")"
printf '  SHA-256: %s\n' "$(sha256sum "$INSTALL_APK" | awk '{print $1}')"

# apksigner may create a v4 sidecar for its temporary --out path. These staging
# files are not the installable APK and must not leak into the published folder.
rm -f "$SIGNED_SOURCE_APK" "$SIGNED_SOURCE_APK.idsig"
chmod 700 "$DIST"
chmod 600 "$INSTALL_JAR" "$INSTALL_APK" "$DIST/README.txt"
chown -R "$PUBLISH_UID:$PUBLISH_GID" "$DIST"

# Complete the local distribution handoff before selecting a reader generation.
APK_NAME="$(basename "$INSTALL_APK")"
JAR_NAME="$(basename "$INSTALL_JAR")"
rm -rf "$PUBLISH_DIST.previous"
if [[ -e "$PUBLISH_DIST" ]]; then mv "$PUBLISH_DIST" "$PUBLISH_DIST.previous"; fi
mv "$DIST" "$PUBLISH_DIST"
DIST=""
OUTPUT_SWAPPED=1
AUTOSOURCARR_PUBLISH_UID="$PUBLISH_UID" AUTOSOURCARR_PUBLISH_GID="$PUBLISH_GID" \
  python3 "$ROOT/extension/publish-pair.py" "$PUBLISH_DIST/$APK_NAME" "$PUBLISH_DIST/$JAR_NAME" "$CONFIG_DIR" "$AUTOSOURCARR_VERSION"
OUTPUT_COMMITTED=1
printf 'Published verified artifacts to %s (previous build retained).\n' "$PUBLISH_DIST"

)

if [[ "$PAIRED_BUILD" == "1" ]]; then
  EXPLICIT_CONFIG=1
  BUILD_OUTPUT_DIR="${AUTOSOURCARR_BUILD_OUTPUT_DIR:-$ROOT/extension/dist-instances}"
  for instance in autosourcarr private-autosourcarr; do
    case "$instance" in
      autosourcarr) instance_config="${AUTOSOURCARR_CONFIG_DIR:-$ROOT/config}" ;;
      private-autosourcarr) instance_config="$AUTOSOURCARR_PRIVATE_CONFIG_DIR" ;;
    esac
    printf '\nBuilding JAR and APK for %s...\n' "$instance"
    AUTOSOURCARR_CONFIG_DIR="$instance_config" \
      AUTOSOURCARR_INSTANCE_DIST="$BUILD_OUTPUT_DIR/$instance" \
      AUTOSOURCARR_BUILD_INSTANCE="$instance" build_single_instance
  done
  echo 'Both AutoSourcarr instance builds completed successfully.'
  printf 'Normal APK/JAR: %s/autosourcarr/\nPrivate APK/JAR: %s/private-autosourcarr/\n' "$BUILD_OUTPUT_DIR" "$BUILD_OUTPUT_DIR"
  echo 'The old extension/dist directory is not updated by a paired build.'
else
  build_single_instance
fi
