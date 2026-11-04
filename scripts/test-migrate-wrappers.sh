#!/usr/bin/env bash
# Checks that the cutover wrappers pass the tool's exit code through and say DO NOT DEPLOY on every non-zero code.
# A stub stands in for dotnet: nothing is built or migrated. Each run works in its own folder under the git-ignored
# .build/wrapper-test/ (a few bytes); nothing is deleted. Run by scripts/check-project.sh.
set -euo pipefail
repo=$(cd "$(dirname "$0")/.." && pwd)
work="$repo/.build/wrapper-test/run-$(date +%Y%m%d%H%M%S)-$$"
mkdir -p "$work/in/data" "$work/in/config"
stub="$work/dotnet-stub"
cat > "$stub" <<'STUB'
#!/usr/bin/env bash
case "$1" in restore|build) exit "${STUB_BUILD:-0}" ;; esac
destination=$4; mkdir -p "$destination"
case "${STUB_EXIT:-0}" in 0) verdict="PARITY OK: 0 lost manual decisions, 0 lost subscriptions" ;; 1) verdict="PARITY FAILED: 1 lost manual decisions" ;; *) verdict="" ;; esac
if [[ -n "$verdict" ]]; then printf '**%s**\n' "$verdict" > "$destination/parity-report.md"; fi
exit "${STUB_EXIT:-0}"
STUB
chmod +x "$stub"
failures=0
# check <name> <expected exit> <text the output must contain> [VAR=value ...] -- <wrapper arguments>
check() {
  local name=$1 expected=$2 text=$3; shift 3
  local envs=(); while [[ $1 != -- ]]; do envs+=("$1"); shift; done; shift
  local out code
  set +e; out=$(env AUTOSOURCARR_DOTNET="$stub" "${envs[@]}" bash "$repo/scripts/migrate-from-0.4.sh" "$@" 2>&1); code=$?; set -e
  if [[ $code -ne $expected || $out != *"$text"* ]]; then echo "FAIL wrapper $name: exit $code (want $expected), output: $out"; failures=$((failures+1)); else echo "PASS wrapper $name"; fi
}
check ok 0 "Output:" STUB_EXIT=0 -- "$work/in" "$work/o0"
check parity-failed 1 "DO NOT DEPLOY" STUB_EXIT=1 -- "$work/in" "$work/o1"
check migration-error 3 "DO NOT DEPLOY" STUB_EXIT=3 -- "$work/in" "$work/o3"
check build-failed 3 "DO NOT DEPLOY" AUTOSOURCARR_FORCE_BUILD=1 STUB_BUILD=1 STUB_EXIT=0 -- "$work/in" "$work/ob"
check service-without-compose 2 "--service needs --compose" STUB_EXIT=0 -- "$work/in" "$work/os" --service x
if command -v pwsh >/dev/null 2>&1 && [[ $(uname -s) == Linux ]]; then
  for want in 0 1 3; do
    text="DO NOT DEPLOY"; [[ $want -eq 0 ]] && text="Output:"
    set +e; out=$(AUTOSOURCARR_DOTNET="$stub" STUB_EXIT=$want pwsh -NoProfile -File "$repo/scripts/migrate-from-0.4.ps1" "$work/in" "$work/p$want" 2>&1); code=$?; set -e
    if [[ $code -ne $want || $out != *"$text"* ]]; then echo "FAIL wrapper ps1 exit $want: got $code, output: $out"; failures=$((failures+1)); else echo "PASS wrapper ps1 exit $want"; fi
  done
else
  echo "SKIP wrapper ps1 (needs pwsh on Linux)"
fi
if [[ $failures -ne 0 ]]; then exit 1; fi
echo "migrate wrappers OK"
