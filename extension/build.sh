#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Explicit paths must never silently fall back to another instance's credentials.
CONFIG_ROOT="${AUTOSOURCARR_HOST_CONFIG_DIR:-$(cd "$ROOT/.." && pwd)/config}"
if [[ -z "${AUTOSOURCARR_HOST_CONFIG_DIR:-}" && ! -s "$CONFIG_ROOT/autosourcarr.env" && -s "$ROOT/config/autosourcarr.env" ]]; then
  CONFIG_ROOT="$ROOT/config"
fi
[[ -s "$CONFIG_ROOT/autosourcarr.env" ]] || { echo "Missing $CONFIG_ROOT/autosourcarr.env; mount or initialize your actual config first." >&2; exit 21; }

args=(--rm -v "$ROOT:/workspace" -v "$CONFIG_ROOT:/autosourcarr-config"
  -e AUTOSOURCARR_CONFIG_DIR=/autosourcarr-config -w /workspace)
if [[ -n "${AUTOSOURCARR_HOST_PRIVATE_CONFIG_DIR:-}" ]]; then
  [[ -s "$AUTOSOURCARR_HOST_PRIVATE_CONFIG_DIR/autosourcarr.env" ]] || { echo "Missing private autosourcarr.env; no fallback was used." >&2; exit 21; }
  [[ ! "$CONFIG_ROOT/autosourcarr.env" -ef "$AUTOSOURCARR_HOST_PRIVATE_CONFIG_DIR/autosourcarr.env" ]] || { echo 'Normal and private configurations must be separate.' >&2; exit 22; }
  args+=(-v "$AUTOSOURCARR_HOST_PRIVATE_CONFIG_DIR:/autosourcarr-private-config"
    -e AUTOSOURCARR_PRIVATE_CONFIG_DIR=/autosourcarr-private-config)
fi
for key in AUTOSOURCARR_PUBLISH_UID AUTOSOURCARR_PUBLISH_GID AUTOSOURCARR_BUILD_MODE; do
  [[ -z "${!key:-}" ]] || args+=(-e "$key=${!key}")
done

docker run "${args[@]}" ghcr.io/cirruslabs/android-sdk:35 \
  bash /workspace/extension/build-in-container.sh "$@"
