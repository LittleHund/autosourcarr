#!/bin/sh
# The app starts this owned child after HTTP startup. No dependency installation.
set -eu
SOLVER_STATE="${AUTOSOURCARR_SOLVER_DIR:-${DATA_DIR:-/data}/tools/solver}"
case "$SOLVER_STATE" in /*) ;; *) echo 'Solver state must be an absolute path.' >&2; exit 2 ;; esac
umask 077
mkdir -p "$SOLVER_STATE" "$SOLVER_STATE/home" "$SOLVER_STATE/cache"
# UC patches its executable, so each instance needs a private writable copy.
# The manager serializes starts; never share this directory between instances.
DRIVER_TEMP=$(mktemp "$SOLVER_STATE/.chromedriver.XXXXXX")
trap 'rm -f "$DRIVER_TEMP"' EXIT HUP INT TERM
cp /opt/chromedriver-linux64/chromedriver "$DRIVER_TEMP"
chmod 700 "$DRIVER_TEMP"
mv "$DRIVER_TEMP" "$SOLVER_STATE/chromedriver"
export AUTOSOURCARR_CHROMEDRIVER="$SOLVER_STATE/chromedriver"
export HOME="$SOLVER_STATE/home" XDG_CACHE_HOME="$SOLVER_STATE/cache"
export HOST=127.0.0.1 PORT=8191 LOG_LEVEL=warning LOG_HTML=false
export PATH="/opt/chrome-linux64:/usr/local/bin:$PATH"
cd /opt/flaresolverr/src
exec /opt/solver-venv/bin/python -u /opt/flaresolverr/src/flaresolverr.py
