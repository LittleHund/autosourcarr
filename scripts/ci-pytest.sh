#!/usr/bin/env bash
# Runs the Python regression suite and fails only on failures that are not in the
# recorded baseline (tests/python-baseline-failures.txt: stale legacy-UI/version/line-count
# tests and subprocess tests that need an installed venv, unchanged since 0.4.3).
# A test that leaves the baseline is reported so the list can be trimmed.
set -uo pipefail
cd "$(dirname "$0")/.."
baseline=tests/python-baseline-failures.txt
report=$(mktemp)
PYTHONPATH=autosourcarr python -m pytest -q -p no:cacheprovider autosourcarr/tests 2>&1 | tee "$report" | tail -3
grep '^FAILED' "$report" | sed -e 's/^FAILED //' -e 's/ - .*//' | sort -u > "$report.failed"
sort -u "$baseline" > "$report.baseline"
new=$(comm -13 "$report.baseline" "$report.failed")
fixed=$(comm -23 "$report.baseline" "$report.failed")
if [[ -n "$fixed" ]]; then printf '\nBaseline failures that now pass (trim %s):\n%s\n' "$baseline" "$fixed"; fi
if [[ -n "$new" ]]; then printf '\nNEW FAILURES (not in baseline):\n%s\n' "$new"; exit 1; fi
echo "No new Python failures (baseline $(wc -l < "$report.baseline") known)."
