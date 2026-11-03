#!/usr/bin/env bash
set -euo pipefail
PROJECT_ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
# Builds the Python 0.4.5 image (autosourcarr/), so the tag is that app's version (autosourcarr/VERSION), not the
# 0.5.0 release version in the root VERSION (CLAUDE.md §4, version split). The 0.5.0 image is docker/Dockerfile.
VERSION=$(tr -d '\r\n' < "$PROJECT_ROOT/autosourcarr/VERSION")
# Optional manual/prebuild helper. Normal Compose deployment builds automatically.
# Keep both the stable Compose tag and a version alias for intentional rollback.
docker build -t autosourcarr:local -t "autosourcarr:$VERSION" "$PROJECT_ROOT/autosourcarr"
printf 'Built autosourcarr:local and autosourcarr:%s. This does not switch a running container; Save/Redeploy the integrated Compose service. Future builds are automatic.\n' "$VERSION"
