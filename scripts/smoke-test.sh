#!/usr/bin/env bash
set -euo pipefail
BASE="${1:-http://127.0.0.1:8282}"

echo "Liveness:"
curl -fsS "$BASE/api/v1/live"
echo

echo "Dependency/status health:"
curl -fsS "$BASE/api/v1/health"
echo

echo "Sources:"
curl -fsS "$BASE/api/v1/sources"
echo
