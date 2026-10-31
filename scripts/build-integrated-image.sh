#!/usr/bin/env bash
set -euo pipefail
PROJECT_ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
VERSION=$(tr -d '\r\n' < "$PROJECT_ROOT/VERSION")
# Optional manual/prebuild helper. Normal Compose deployment builds automatically.
# Keep both the stable Compose tag and a version alias for intentional rollback.
docker build -t autosourcarr:local -t "autosourcarr:$VERSION" "$PROJECT_ROOT/autosourcarr"
printf 'Built autosourcarr:local and autosourcarr:%s. This does not switch a running container; Save/Redeploy the integrated Compose service. Future builds are automatic.\n' "$VERSION"
