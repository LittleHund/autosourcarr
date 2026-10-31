#!/usr/bin/env bash
# Runs the Python regression suite and fails only on failures that are not in the
# recorded baseline (tests/python-baseline-failures.txt: stale legacy-UI/version/line-count
# tests and subprocess tests that need an installed venv, unchanged since 0.4.3).
# A test that leaves the baseline is reported so the list can be trimmed.
set -uo pipefail
cd "$(dirname "$0")/.."
baseline=tests/python-baseline-failures.txt
report=$(mktemp)
PYTHONDONTWRITEBYTECODE=1 PYTHONPATH=autosourcarr python -m pytest -q -p no:cacheprovider autosourcarr/tests 2>&1 | tee "$report" | tail -3
grep '^FAILED' "$report" | sed -e 's/^FAILED //' -e 's/ - .*//' | sort -u > "$report.failed"
sort -u "$baseline" > "$report.baseline"
new=$(comm -13 "$report.baseline" "$report.failed")
fixed=$(comm -23 "$report.baseline" "$report.failed")
if [[ -n "$fixed" ]]; then printf '\nBaseline failures that now pass (trim %s):\n%s\n' "$baseline" "$fixed"; fi
if [[ -n "$new" ]]; then
  printf '\nNEW FAILURES (not in baseline):\n%s\n' "$new"
  # GitHub annotations: visible through the API even when the raw log is not.
  while IFS= read -r test; do echo "::error title=new pytest failure::$test"; done <<< "$new"
  echo "::error title=pytest summary::$(tail -1 "$report")"
  while IFS= read -r test; do
    # The failure section of this test (pytest's "____ name ____" header), first 30 lines.
    name=${test##*::}; name=${name%%[*}
    awk -v n="$name" 'BEGIN{p=0} /^_{3,} .* _{3,}$/{p=index($0," " n)>0 || index($0," " n "[")>0} p&&c<30{print; c++}' "$report" \
      | while IFS= read -r line; do echo "::warning title=${name}::${line}"; done
  done <<< "$new"
  exit 1
fi
echo "No new Python failures (baseline $(wc -l < "$report.baseline") known)."
