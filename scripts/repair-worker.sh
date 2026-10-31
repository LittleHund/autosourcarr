#!/usr/bin/env bash
set -euo pipefail
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPAIR_ENV="$PROJECT_DIR/config/repair-worker.env"
ACTION="${1:-help}"

if [[ "$ACTION" == setup ]]; then
  [[ $# == 2 ]] || { echo "Usage: sudo bash scripts/repair-worker.sh setup /absolute/separate/repair-dataset"; exit 2; }
  python3 - "$PROJECT_DIR" "$2" <<'PY'
from pathlib import Path
import os, secrets, sys
project = Path(sys.argv[1]).resolve()
root = Path(sys.argv[2])
if not root.is_absolute() or root.is_symlink() or any(c in str(root) for c in '\n\r$,:# '):
    raise SystemExit('Use a new absolute repair dataset path without spaces or Compose special characters')
root = root.resolve()
if root == Path('/') or root == project or root in project.parents or project in root.parents:
    raise SystemExit('Repair data must be separate from the project directory')
env = project/'config/repair-worker.env'
if env.exists():
    raise SystemExit('Repair setup already exists. Existing token/data retained; use up or login.')
if root.exists() and any(root.iterdir()):
    raise SystemExit('Choose an empty repair directory, never your existing source-host or application data')
root.mkdir(parents=True, exist_ok=True, mode=0o700)
for name in ('auth', 'locks'):
    path = root/name
    path.mkdir(mode=0o700)
    os.chown(path, 1000, 1000)
lock = root/'locks/account.lock'
lock.touch(mode=0o600)
os.chown(lock, 1000, 1000)
env.parent.mkdir(parents=True, exist_ok=True)
with os.fdopen(os.open(env, os.O_WRONLY|os.O_CREAT|os.O_EXCL, 0o600), 'w') as stream:
    stream.write(f'AUTOSOURCARR_PROJECT_ROOT={project}\nREPAIR_ROOT={root}\nREPAIR_TOKEN={secrets.token_hex(32)}\nREPAIR_NETWORK=autosourcarr-repair\n')
print('Private worker configuration created. Existing app/source configuration is unchanged.')
PY
  exit
fi

if [[ "$ACTION" == help ]]; then
  echo 'Commands: setup /absolute/repair-dataset | build | login | login-status | up | logs'
  echo 'Set AUTOSOURCARR_BASE_COMPOSE to your actual Compose file (default docker-compose.truenas.yml).'
  echo 'up builds and redeploys autosourcarr plus its optional repair worker; run only when ready to deploy.'
  exit
fi
[[ -f "$REPAIR_ENV" ]] || { echo 'Run setup first; see README → Private source repair worker.'; exit 2; }
BASE_COMPOSE="${AUTOSOURCARR_BASE_COMPOSE:-$PROJECT_DIR/docker-compose.truenas.yml}"
COMPOSE=(docker compose --env-file "$REPAIR_ENV" -f "$BASE_COMPOSE" -f "$PROJECT_DIR/docker-compose.repair.yml")
case "$ACTION" in
  build)
    "${COMPOSE[@]}" --profile tools build autosourcarr-repair repair-login
    docker pull ghcr.io/cirruslabs/android-sdk:35
    docker pull ghcr.io/suwayomi/suwayomi-server:v2.3.2243
    ;;
  login)
    "${COMPOSE[@]}" --profile tools run --rm --no-deps repair-login
    ;;
  login-status)
    "${COMPOSE[@]}" --profile tools run --rm --no-deps repair-login login status
    ;;
  up)
    [[ -n "${AUTOSOURCARR_BASE_COMPOSE:-}" ]] || { echo 'For managed TrueNAS Apps use configure-repair-worker.py --apply. Standalone Compose users must explicitly set AUTOSOURCARR_BASE_COMPOSE.'; exit 2; }
    "${COMPOSE[@]}" up -d --build --no-deps autosourcarr autosourcarr-repair autosourcarr-repair-egress
    ;;
  logs)
    "${COMPOSE[@]}" logs --tail=100 autosourcarr-repair
    ;;
  *) echo 'Unknown command. Use help.'; exit 2 ;;
esac
