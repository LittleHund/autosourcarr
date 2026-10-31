#!/usr/bin/env bash
set -euo pipefail
SUWAYOMI_URL="${1:-${MAIN_SUWAYOMI_URL:-http://suwayomi:30058}}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mapfile -t jars < <(find "$ROOT/dist" -maxdepth 1 -type f -name '*.jar' -print 2>/dev/null | sort)
if [[ "${#jars[@]}" -ne 1 ]]; then echo "Expected exactly one JAR in $ROOT/dist; build first." >&2; exit 2; fi
JAR="${jars[0]}"
command -v curl >/dev/null 2>&1 || { echo 'curl is required.' >&2; exit 3; }
bash "$ROOT/verify-suwayomi-jar.sh" "$JAR"
OPERATIONS='{"query":"mutation Install($input: InstallExternalExtensionInput!) { installExternalExtension(input: $input) { extension { pkgName name versionName isInstalled } } }","variables":{"input":{"clientMutationId":"autosourcarr-install","extensionFile":null}}}'
MAP='{"0":["variables.input.extensionFile"]}'
response="$(curl --fail-with-body --silent --show-error -X POST "$SUWAYOMI_URL/api/graphql" -F "operations=$OPERATIONS" -F "map=$MAP" -F "0=@$JAR;type=application/java-archive")"
printf '%s\n' "$response"
if printf '%s' "$response" | grep -q '"errors"'; then echo 'Suwayomi returned a GraphQL error.' >&2; exit 4; fi
echo 'AutoSourcarr JAR upload completed.'
