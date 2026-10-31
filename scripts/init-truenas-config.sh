#!/usr/bin/env bash
set -euo pipefail
BASE="${1:-/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr}"
PROJECT="${2:-$BASE/project}"
CONFIG="${3:-$BASE/config}"
PROJECT_CONFIG="$PROJECT/config"

mkdir -p "$CONFIG"
chmod 700 "$CONFIG" 2>/dev/null || true

# Keep user/runtime values outside project/. Replacing project/ with a new release
# can therefore never wipe API credentials, URLs, source-host settings, or signing
# identity. Existing project-local values are migrated once and never overwrite a
# persistent value on later releases.
merge_env_file() {
  local name="$1"
  local dst="$CONFIG/$name.env"
  local old="$PROJECT_CONFIG/$name.env"
  local example="$PROJECT_CONFIG/$name.env.example"

  if [[ ! -s "$dst" ]]; then
    if [[ -s "$old" ]]; then
      cp "$old" "$dst"
      echo "Migrated $old -> $dst"
    elif [[ -s "$example" ]]; then
      cp "$example" "$dst"
      echo "Created $dst from bundled template"
    else
      echo "Missing both $old and $example" >&2
      exit 1
    fi
  fi

  # Add newly introduced variables without changing existing user values.
  for src in "$old" "$example"; do
    [[ -s "$src" ]] || continue
    while IFS= read -r line || [[ -n "$line" ]]; do
      case "$line" in
        ''|'#'*) continue ;;
      esac
      key="${line%%=*}"
      [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || continue
      if ! grep -qE "^${key}=" "$dst"; then
        printf '\n%s\n' "$line" >> "$dst"
        echo "Added new setting $key to $dst"
      fi
    done < "$src"
  done
  chmod 600 "$dst" 2>/dev/null || true
}

merge_env_file autosourcarr
merge_env_file source-host

# Create one persistent reader credential if the user has not set one yet. The
# same value is read by the backend at runtime and embedded into Android builds.
# It therefore survives replacing project/ and never needs to be re-entered per
# release. Do not print the secret to stdout.
AUTO_ENV="$CONFIG/autosourcarr.env"
if grep -q '^AUTOSOURCARR_READER_API_TOKEN=$' "$AUTO_ENV"; then
  if command -v openssl >/dev/null 2>&1; then
    READER_TOKEN="$(openssl rand -hex 32)"
  else
    READER_TOKEN="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
  fi
  tmp="${AUTO_ENV}.tmp.$$"
  awk -v token="$READER_TOKEN" '
    $0 == "AUTOSOURCARR_READER_API_TOKEN=" { print "AUTOSOURCARR_READER_API_TOKEN=" token; next }
    { print }
  ' "$AUTO_ENV" > "$tmp"
  chmod 600 "$tmp" 2>/dev/null || true
  mv "$tmp" "$AUTO_ENV"
  unset READER_TOKEN
  echo "Generated persistent AutoSourcarr reader API credential in $AUTO_ENV"
fi

# v0.2.68 stored the Android signing identity under project/config. Preserve it
# automatically when upgrading so normal APK updates keep working.
if [[ -d "$PROJECT_CONFIG/.extension-signing" && ! -e "$CONFIG/.extension-signing" ]]; then
  cp -a "$PROJECT_CONFIG/.extension-signing" "$CONFIG/.extension-signing"
  echo "Migrated Android signing identity to $CONFIG/.extension-signing"
fi
mkdir -p "$CONFIG/.extension-signing"
chmod 700 "$CONFIG/.extension-signing" 2>/dev/null || true

echo "Persistent AutoSourcarr configuration: $CONFIG"
echo "Release files remain replaceable under: $PROJECT"
