#!/usr/bin/env bash
# Migrates a snapshot of AutoSourcarr 0.4.x to 0.5.0 on this machine and writes a parity report.
#
#   scripts/migrate-from-0.4.sh <0.4.5 backup file | folder with data/ and config/> <new or empty output folder>
#
# The source (a 0.4.5 instance backup from System -> Backups, or copies of the 0.4.x data/ and config/ folders, plus
# database/ when DATABASE_DIR was elsewhere) is only read; the passphrase of a backup is asked and never stored.
# The output gets data/ and config/ (mount them in docker-compose.0.5.0.yml), original-0.4/ (the untouched copy the
# comparison used) and parity-report.md / parity-report.json. Needs the .NET 10 SDK; no Docker.
set -euo pipefail
if [[ $# -ne 2 && $# -ne 4 && $# -ne 6 ]]; then
  echo "Usage: $0 <0.4.5 backup file | folder with data/ and config/> <new or empty output folder> [--compose <0.4.x compose file> [--service <name>]]" >&2
  exit 2
fi
repo=$(cd "$(dirname "$0")/.." && pwd)
project="$repo/src/AutoSourcarr.Api/AutoSourcarr.Api.csproj"
dll="$repo/src/AutoSourcarr.Api/bin/Release/net10.0/AutoSourcarr.Api.dll"
source=$1
destination=$2
extra=("${@:3}")
# --service only makes sense with --compose (the tool refuses it too; say so before building).
if [[ " ${extra[*]-} " == *" --service "* && " ${extra[*]-} " != *" --compose "* ]]; then echo "--service needs --compose <file>." >&2; exit 2; fi
# AUTOSOURCARR_DOTNET lets the wrapper test replace dotnet with a stub; normally it is the dotnet on PATH.
dotnet=${AUTOSOURCARR_DOTNET:-dotnet}

if [[ ! -e "$source" ]]; then echo "The source does not exist: $source" >&2; exit 2; fi
source=$(cd "$(dirname "$source")" && pwd)/$(basename "$source")
if [[ -e "$destination" && ! -d "$destination" ]]; then echo "The destination is a file: $destination" >&2; exit 2; fi
if [[ -d "$destination" && -n "$(ls -A "$destination")" ]]; then
  echo "The destination folder is not empty: $destination. Choose a new or empty folder." >&2
  exit 2
fi

# Best effort: refuse a live instance. Inside a container the 0.4.x mounts (/data, /config) are live; a snapshot has
# no database journal written in the last minutes.
in_container=false
if [[ -f /.dockerenv ]] || grep -qE '(docker|containerd|kubepods)' /proc/1/cgroup 2>/dev/null; then in_container=true; fi
case "$source" in
  /data|/data/*|/config|/config/*)
    if $in_container; then echo "The source is a live mount inside a container. Copy the folders (or download a backup) and run on the copy." >&2; exit 2; fi ;;
esac
if [[ -d "$source" ]]; then
  recent=$(find "$source" -maxdepth 4 -type f \( -name '*.sqlite-wal' -o -name '*.sqlite-journal' \) -size +0 -mmin -10 2>/dev/null | head -n 1 || true)
  if [[ -n "$recent" ]]; then
    echo "$(basename "$recent") was written less than 10 minutes ago: this looks like a running instance. Stop 0.4.5 (or download a backup) and use a copy." >&2
    exit 2
  fi
fi

if ! command -v "$dotnet" >/dev/null 2>&1; then echo "The .NET 10 SDK (dotnet) is needed: https://dotnet.microsoft.com/download" >&2; exit 2; fi
# AUTOSOURCARR_FORCE_BUILD=1 (the wrapper test) always builds.
if [[ -n "${AUTOSOURCARR_FORCE_BUILD:-}" || ! -f "$dll" ]] || [[ -n "$(find "$repo/src" \( -name bin -o -name obj \) -prune -o -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.json' -o -name '*.sql' \) -newer "$dll" -print -quit)" ]]; then
  echo "Building AutoSourcarr (Release)..."
  # A failed build is exit 3 (not 1, which means PARITY FAILED or MIGRATION FAILED) and never looks like a migration result.
  if ! "$dotnet" restore --locked-mode "$project" || ! "$dotnet" build -c Release --no-restore "$project"; then
    echo "The build failed. DO NOT DEPLOY anything; nothing was migrated and your 0.4.x data is unchanged." >&2
    exit 3
  fi
fi

set +e
# ${extra[@]+...} keeps bash 3.2 (macOS) from treating an empty array as unbound under set -u.
"$dotnet" "$dll" --migrate-0.4 "$source" "$destination" ${extra[@]+"${extra[@]}"}
code=$?
set -e
report="$destination/parity-report.md"
echo
if [[ -f "$report" ]]; then
  grep -m 1 -E '^\*\*(PARITY|MIGRATION) ' "$report" | tr -d '*' || true
  echo "Report:  $report"
fi
if [[ $code -eq 0 ]]; then
  echo "Output:  $destination (data/ -> /data, config/ -> /config)"
  echo "Next: read the report, then follow docs/upgrade-from-0.4.md (deploy only after your go)."
else
  echo "DO NOT DEPLOY this output. Read the report (or MIGRATION-INCOMPLETE.txt); your 0.4.x data is unchanged."
fi
exit $code
