"""Bounded, credential-scrubbed troubleshooting data; never collect settings or locals."""
from datetime import datetime, timezone
from functools import lru_cache
from importlib.metadata import version, PackageNotFoundError
from pathlib import Path
import platform
import re
import sqlite3
import time
import traceback
import uuid

APP_VERSION = (Path(__file__).resolve().parent.parent / 'VERSION').read_text().strip()
INSTANCE_ID = uuid.uuid4().hex[:12]
STARTED_AT = datetime.now(timezone.utc).isoformat()
_STARTED = time.monotonic()
_CODEX_VERSION_FILE = Path('/opt/autosourcarr-codex-version')
_SECRET_KEY = re.compile(r'^(?:authorization|proxy[-_]authorization|(?:set[-_])?cookie|(?:.*[-_])?(?:password|passwd|secret|token|api[-_]?key))$', re.I)


def redact_text(value):
    text = str(value)
    text = re.sub(r'(https?://)[^\s/\?#@]+@', r'\1[redacted]@', text, flags=re.I)
    text = re.sub(r'([?&](?:token|api[_-]?key|password|auth|secret|access_token|reader_api_token)=)[^\s&#"\']+', r'\1[redacted]', text, flags=re.I)
    text = re.sub(r'((?:authorization|proxy-authorization|cookie|set-cookie)["\']?\s*[:=]\s*)[^\r\n]+', r'\1[redacted]', text, flags=re.I)
    # Also cover quoted JSON/log keys without removing unrelated technical fields.
    text = re.sub(r'((?:[\w-]*(?:token|password|secret|api[_-]?key)|authorization|proxy-authorization|cookie|set-cookie)["\']?\s*[:=]\s*)(?:"[^"\r\n]*"|\'[^\'\r\n]*\'|[^\s,;&}]+)', r'\1[redacted]', text, flags=re.I)
    return text


def scrub(value, *, depth=0):
    if depth > 10:
        return '[depth limited]'
    if isinstance(value, dict):
        return {str(key): '[redacted]' if _SECRET_KEY.fullmatch(re.sub(r'([a-z0-9])([A-Z])', r'\1_\2', str(key))) else scrub(item, depth=depth+1) for key, item in list(value.items())[:200]}
    if isinstance(value, (list, tuple)):
        return [scrub(item, depth=depth+1) for item in value[:200]]
    if isinstance(value, str):
        text = redact_text(value)
        return text if len(text) <= 16000 else text[:16000] + '\n[truncated]'
    return value if isinstance(value, (int, float, bool)) or value is None else redact_text(value)


def exception_context(error):
    chain, seen = [], set()
    while error is not None and id(error) not in seen and len(chain) < 6:
        seen.add(id(error))
        frames = traceback.extract_tb(error.__traceback__)
        chain.append({'type': type(error).__name__, 'message': redact_text(str(error) or type(error).__name__),
                      'frames': [{'file': frame.filename, 'line': frame.lineno, 'function': frame.name} for frame in frames[-24:]],
                      'frames_omitted': max(0, len(frames)-24)})
        error = error.__cause__ if error.__cause__ is not None else (None if error.__suppress_context__ else error.__context__)
    return scrub(chain)


@lru_cache(maxsize=1)
def software_versions():
    packages = {}
    for name in ('fastapi', 'starlette', 'pydantic', 'httpx', 'httpcore', 'anyio', 'uvicorn'):
        try:
            packages[name] = version(name)
        except PackageNotFoundError:
            packages[name] = 'unavailable'
    try:
        with _CODEX_VERSION_FILE.open('r',encoding='utf-8') as stream:
            codex_version=stream.read(129).strip()
    except (OSError,UnicodeError):
        codex_version='unavailable'
    if len(codex_version)>128 or not re.fullmatch(r'codex(?:-cli)? \d+\.\d+\.\d+[A-Za-z0-9.+-]{0,32}',codex_version):
        codex_version='unavailable'
    return {'python': platform.python_version(), 'sqlite': sqlite3.sqlite_version,
            'bundled_codex':codex_version,
            'platform': platform.system(), 'architecture': platform.machine(), 'packages': packages}


def runtime_context():
    return {'app_version': APP_VERSION, 'instance_id': INSTANCE_ID, 'server_time': datetime.now(timezone.utc).isoformat(),
            'started_at': STARTED_AT, 'uptime_seconds': round(time.monotonic()-_STARTED, 1), **software_versions()}
