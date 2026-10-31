#!/bin/sh
set -eu
APP_DIR="${AUTOSOURCARR_APP_DIR:-/app}"
DEPS_DIR="${AUTOSOURCARR_DEPS_DIR:-/deps}"
REQ_FILE="${AUTOSOURCARR_REQUIREMENTS:-$APP_DIR/requirements.txt}"
# Old TrueNAS YAML may still call this wrapper after selecting the integrated image.
# Use its baked environment directly. On a legacy Python image keep diagnostics
# accessible instead of crash-looping; integrated mode never falls back to a source host.
if [ "${SOURCE_ENGINE_MODE:-integrated}" = integrated ]; then
  if [ -x /opt/autosourcarr-venv/bin/python ] && [ -f /home/suwayomi/startup_script.sh ]; then
    export PYTHONPATH="$APP_DIR"
    # Own this irreversible restriction internally, before Python creates any
    # app threads or supervised children. setpriv execs in place and fails closed
    # if the kernel refuses the flag; existing Compose protection is compatible.
    /usr/bin/setpriv --no-new-privs -- /opt/autosourcarr-venv/bin/python -m app.instance_backup --apply-pending
    exec /usr/bin/setpriv --no-new-privs -- /opt/autosourcarr-venv/bin/python -m uvicorn app.main:app --workers 1 --host 0.0.0.0 --port "${AUTOSOURCARR_PORT_INTERNAL:-8282}" --proxy-headers
  fi
  echo 'AutoSourcarr: integrated runtime missing in this container; starting the UI in setup-required mode.' >&2
  echo 'Apply the release Compose service with build.context and pull_policy: build, then Save/Redeploy the TrueNAS app. Deployment builds and selects the integrated image automatically.' >&2
  echo 'Source execution stays unavailable until that deployment completes. Keep both databases and the existing source data mount.' >&2
fi
# Validate paths before loading the helper or creating/installing anything.
python - "$APP_DIR" "$DEPS_DIR" <<'PYGUARD'
import os, sys
from pathlib import Path
app, deps = (Path(value).resolve() for value in sys.argv[1:])
protected = [app, Path(os.environ.get("DATA_DIR", "/data")).resolve(),
             Path(os.environ.get("SETTINGS_CONFIG_DIR", "/config")).resolve()]
if deps == Path(deps.anchor) or any(deps == p or deps in p.parents for p in protected):
    raise SystemExit(f"Refusing unsafe dependency cache directory: {deps}; use a dedicated directory or /deps mount")
PYGUARD
# The helper installs/validates in a new directory and atomically selects it only
# on success. A failed download cannot destroy the previously working /deps cache.
ACTIVE_DEPS="$(python "$APP_DIR/app/dependency_cache.py" "$APP_DIR" "$DEPS_DIR" "$REQ_FILE")"
export PYTHONPATH="$ACTIVE_DEPS:$APP_DIR"
python -m app.instance_backup --apply-pending
exec python -m uvicorn app.main:app \
  --workers 1 \
  --host 0.0.0.0 \
  --port "${AUTOSOURCARR_PORT_INTERNAL:-8282}" \
  --proxy-headers
