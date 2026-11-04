#!/usr/bin/env bash
# Checks that the cutover wrappers pass the tool's exit code through, print the verdict line, and say DO NOT DEPLOY on
# every non-zero code (and only then). A stub stands in for dotnet: nothing is built or migrated. Each run works in its
# own folder under the git-ignored .build/wrapper-test/ (a few bytes); nothing is deleted. Run by scripts/check-project.sh.
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
verify() { # name code expected must-contain must-not-contain output
  local name=$1 code=$2 expected=$3 text=$4 absent=$5 out=$6
  if [[ $code -ne $expected || $out != *"$text"* || ( -n $absent && $out == *"$absent"* ) ]]; then
    echo "FAIL wrapper $name: exit $code (want $expected), output: $out"; failures=$((failures+1))
  else echo "PASS wrapper $name"; fi
}
# check <name> <expected exit> <must contain> <must not contain> [VAR=value ...] -- <wrapper arguments>
check() {
  local name=$1 expected=$2 text=$3 absent=$4; shift 4
  local envs=(AUTOSOURCARR_DOTNET="$stub"); while [[ $1 != -- ]]; do envs+=("$1"); shift; done; shift
  local out code
  set +e; out=$(env "${envs[@]}" bash "$repo/scripts/migrate-from-0.4.sh" "$@" 2>&1); code=$?; set -e
  verify "$name" "$code" "$expected" "$text" "$absent" "$out"
}
check ok 0 "PARITY OK" "DO NOT DEPLOY" STUB_EXIT=0 -- "$work/in" "$work/o0"
check parity-failed 1 "PARITY FAILED" "Output:" STUB_EXIT=1 -- "$work/in" "$work/o1"
check tool-refused 2 "DO NOT DEPLOY" "Output:" STUB_EXIT=2 -- "$work/in" "$work/o2"
check migration-error 3 "DO NOT DEPLOY" "Output:" STUB_EXIT=3 -- "$work/in" "$work/o3"
check build-failed 3 "DO NOT DEPLOY" "Output:" AUTOSOURCARR_FORCE_BUILD=1 STUB_BUILD=1 STUB_EXIT=0 -- "$work/in" "$work/ob"
check service-without-compose 2 "--service needs --compose" "Output:" STUB_EXIT=0 -- "$work/in" "$work/os" --service x
if command -v pwsh >/dev/null 2>&1 && [[ $(uname -s) == Linux ]]; then
  ps() { # name expected must-contain must-not-contain [VAR=value ...] -- <pwsh -Command text>
    local name=$1 expected=$2 text=$3 absent=$4; shift 4
    local envs=(AUTOSOURCARR_DOTNET="$stub"); while [[ $1 != -- ]]; do envs+=("$1"); shift; done; shift
    local out code
    set +e; out=$(cd "$work" && env "${envs[@]}" pwsh -NoProfile -Command "$1" 2>&1); code=$?; set -e
    verify "ps1 $name" "$code" "$expected" "$text" "$absent" "$out"
  }
  script="$repo/scripts/migrate-from-0.4.ps1"
  for want in 0 1 2 3; do
    text="DO NOT DEPLOY"; absent="Output:"; [[ $want -eq 0 ]] && { text="PARITY OK"; absent="DO NOT DEPLOY"; }
    ps "exit $want" "$want" "$text" "$absent" STUB_EXIT=$want -- "& '$script' '$work/in' '$work/p$want'; exit \$LASTEXITCODE"
  done
  ps build-failed 3 "DO NOT DEPLOY" "Output:" AUTOSOURCARR_FORCE_BUILD=1 STUB_BUILD=1 -- "& '$script' '$work/in' '$work/pb'; exit \$LASTEXITCODE"
  ps service-without-compose 2 "-Service needs -Compose" "Output:" -- "& '$script' '$work/in' '$work/ps' -Service x; exit \$LASTEXITCODE"
  # A profile setting that turns native exit codes into errors must not hide the tool's exit code.
  ps native-error-preference 1 "PARITY FAILED" "Output:" STUB_EXIT=1 -- "\$PSNativeCommandUseErrorActionPreference = \$true; & '$script' '$work/in' '$work/pn'; exit \$LASTEXITCODE"
  # A relative destination resolves against the PowerShell location (here $work), not the process directory.
  ps relative-destination 0 "PARITY OK" "DO NOT DEPLOY" STUB_EXIT=0 -- "Set-Location '$work'; & '$script' '$work/in' 'relative-out'; exit \$LASTEXITCODE"
  if [[ ! -f "$work/relative-out/parity-report.md" ]]; then echo "FAIL wrapper ps1 relative-destination: the report is not in $work/relative-out"; failures=$((failures+1)); fi
else
  echo "SKIP wrapper ps1 (needs pwsh on Linux)"
fi
if [[ $failures -ne 0 ]]; then exit 1; fi
echo "migrate wrappers OK"
