#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
shopt -s nullglob
obsolete=()
for f in autosourcarr-v*-compose*.yml autosourcarr-v*-upgrade*.md autosourcarr-v*-regression-audit*.md autosourcarr-v*-SHA256*.txt UPGRADE-*.md REGRESSION-AUDIT*.md; do [[ -e "$f" ]] && obsolete+=("$f"); done
for f in OVERLAY-UPDATES.md RENAME-TO-AUTOSOURCARR.md OPTIMIZATION-REPORT.md; do [[ -e "$f" ]] && obsolete+=("$f"); done
if ((${#obsolete[@]})); then echo 'Removing obsolete one-off project/update files:'; printf '  %s\n' "${obsolete[@]}"; rm -f -- "${obsolete[@]}"; else echo 'No obsolete one-off project/update files found.'; fi
echo 'Release history is consolidated in CHANGELOG.md; Compose helper filename is compose-env-file.yml.'
