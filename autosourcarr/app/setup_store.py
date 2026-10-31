"""First-run configuration. Drafts never reach disk; Finish is a recoverable transaction."""
from __future__ import annotations
import base64
import hashlib
import json
import math
import os
import re
import tempfile
import uuid
from pathlib import Path
from urllib.parse import urlsplit, unquote
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError
from dotenv import dotenv_values
from io import StringIO
from .env_settings import ENV_WRITE_LOCK
from .setup_schema import FIELDS, BY_KEY
from .reader_build import READER_KEYS, evaluate_reader_config

STATE = 'setup-state.json'
JOURNAL = '.setup-transaction.json'
FILES = ('autosourcarr.env', 'source-host.env', STATE)
LIMIT = 2 * 1024 * 1024

class SetupConflict(ValueError):
    pass


def read_file(path):
    if path.is_symlink():
        raise ValueError('Setup cannot replace symlinked configuration files.')
    if not path.exists():
        return None
    if not path.is_file() or path.stat().st_size > (8 * LIMIT if path.name == JOURNAL else LIMIT):
        raise ValueError('Configuration file is not a supported regular file.')
    return path.read_bytes()


def atomic_write(path, data):
    read_file(path)
    if data is not None and len(data) > (8 * LIMIT if path.name == JOURNAL else LIMIT):
        raise ValueError('Setup configuration exceeds the supported size.')
    if data is None:
        path.unlink(missing_ok=True)
    else:
        fd, temp = tempfile.mkstemp(prefix='.setup-write-', dir=path.parent)
        try:
            with os.fdopen(fd, 'wb') as out:
                out.write(data); out.flush(); os.fsync(out.fileno())
            os.replace(temp, path)
        finally:
            if os.path.exists(temp): os.unlink(temp)
    fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
    try: os.fsync(fd)
    finally: os.close(fd)


def state_at(root):
    raw = read_file(root / STATE)
    state = json.loads(raw) if raw else {}
    if not isinstance(state, dict): raise ValueError('Invalid setup state.')
    return state


def env_at(root, name):
    raw = read_file(root / name)
    return dict(dotenv_values(stream=StringIO((raw or b'').decode()), interpolate=False))


def recover(root):
    """Marker is the commit point. All paths are fixed; journal cannot select a target."""
    raw = read_file(root / JOURNAL)
    if not raw: return {}
    journal = json.loads(raw)
    if not isinstance(journal, dict) or set(journal.get('before', {})) != set(FILES):
        raise ValueError('Invalid setup recovery journal; configuration was not modified.')
    uuid.UUID(journal['transaction'])
    # Validate every recovery blob before writing the first file.
    before = {name: None if value is None else base64.b64decode(value, validate=True)
              for name, value in journal['before'].items()}
    if any(value is not None and len(value) > LIMIT for value in before.values()):
        raise ValueError('Setup recovery data exceeds its limit.')
    restored_state = json.loads(before[STATE]) if before[STATE] else {}
    if not isinstance(restored_state, dict): raise ValueError('Invalid setup recovery state.')
    effective = journal.get('effective_before', {})
    if not isinstance(effective, dict) or any(k not in BY_KEY or k == 'EXTENSION_STORES' for k in effective):
        raise ValueError('Invalid setup recovery settings.')
    if state_at(root).get('transaction') == journal['transaction']:
        atomic_write(root / JOURNAL, None)
        return {}
    # Pin pre-save effective values durably: Docker may already have injected a half-written env file.
    if effective:
        before['autosourcarr.env'] = patch_env(before['autosourcarr.env'], effective)
        restored_state['managed_keys'] = sorted(set(restored_state.get('managed_keys', [])) | set(effective))
        before[STATE] = json.dumps(restored_state).encode()
    for name in FILES:
        atomic_write(root / name, before[name])
    atomic_write(root / JOURNAL, None)
    return effective


def startup_overrides(root):
    """Only wizard-owned keys override stale Docker env values, from the canonical env file."""
    root = Path(root)
    with ENV_WRITE_LOCK:
        rolled_back = recover(root)
        state = state_at(root)
        saved = env_at(root, 'autosourcarr.env') if state.get('managed_keys') else {}
        values = {key.lower(): saved[key] for key in state.get('managed_keys', [])
                  if key in BY_KEY and key != 'EXTENSION_STORES' and saved.get(key) is not None}
        values.update({key.lower(): value for key, value in rolled_back.items()})
        _BOOT_REPOSITORIES[str(root)] = repository_value(root)
        return values


_BOOT_REPOSITORIES = {}

def repository_value(root):
    raw = env_at(Path(root), 'source-host.env').get('EXTENSION_STORES', os.environ.get('EXTENSION_STORES', ''))
    try: return json.loads(raw) if raw else None
    except (ValueError, TypeError): return raw


def _revision(root):
    parts = [read_file(root/name) or b'' for name in FILES[:2]]
    state = state_at(root)
    state.pop('cleanup', None)  # Background cleanup reporting is not a configuration edit.
    parts.append(json.dumps(state, sort_keys=True).encode())
    return hashlib.sha256(b'\0'.join(parts)).hexdigest()


def effective_values(root, settings):
    state = state_at(root)
    _BOOT_REPOSITORIES.setdefault(str(root), repository_value(root))
    saved = env_at(root, 'autosourcarr.env')
    source = env_at(root, 'source-host.env')
    result = {}
    for field in FIELDS:
        key = field['key']
        if key == 'EXTENSION_STORES':
            from .source_repositories import KEIYOUSHI_URL
            raw = source.get(key, os.environ.get(key, ''))
            try: value = json.loads(raw) if raw else [KEIYOUSHI_URL]
            except (ValueError, TypeError): raise ValueError('Existing extension repository list is invalid. Correct source-host.env before setup.')
            if not isinstance(value, list) or not all(isinstance(url, str) for url in value):
                raise ValueError('Existing extension repository list is invalid.')
        else:
            value = saved.get(key) if key in state.get('managed_keys', []) else None
            if value is None: value = getattr(settings, key.lower(), os.environ.get(key, field.get('default', '')))
            if field['kind'] == 'boolean': value = str(value).lower() in ('true','1','yes','on')
            elif field['kind'] in ('integer', 'number'): value = float(value) if field['kind'] == 'number' else int(value)
        result[key] = value
    return result


def snapshot(root, settings):
    root = Path(root)
    with ENV_WRITE_LOCK:
        recover(root)
        values = effective_values(root, settings)
        fields = []
        for field in FIELDS:
            item = dict(field)
            value = values[field['key']]
            if field['kind'] == 'secret': item.update(value='', configured=bool(value))
            else: item['value'] = value
            fields.append(item)
        state = state_at(root)
        return dict(completed=bool(state.get('completed')), revision=_revision(root), fields=fields,
                    cleanup=state.get('cleanup'), restart_required=restart_needed(values, settings),
                    integrated=settings.app_services_mode == 'integrated', reader_build=evaluate_reader_config(values))


def reader_preflight(root, settings, changes, revision):
    """Read a draft against the saved effective config; never save credentials."""
    if not isinstance(changes, dict) or any(key not in BY_KEY for key in changes):
        raise ValueError('Reader check contains an unsupported setting.')
    root = Path(root)
    with ENV_WRITE_LOCK:
        if revision != _revision(root):
            raise SetupConflict('Settings changed elsewhere. Reload setup before checking the reader draft.')
        values = effective_values(root, settings)
        for key in READER_KEYS:
            if key in changes:
                values[key] = normalize(BY_KEY[key], changes[key])
        return {'revision': revision, 'reader_build': evaluate_reader_config(values)}


def restart_needed(values, settings):
    root = Path(settings.settings_config_dir)
    if _BOOT_REPOSITORIES.get(str(root)) != repository_value(root): return True
    for key, value in values.items():
        if key == 'EXTENSION_STORES': continue
        running = getattr(settings, key.lower(), '')
        kind = BY_KEY[key]['kind']
        if kind == 'boolean':
            equal = (str(running).lower() in ('true','1','yes','on')) == (str(value).lower() in ('true','1','yes','on'))
        elif kind in ('integer', 'number'):
            equal = float(running) == float(value)
        else:
            equal = str(running) == str(value)
        if not equal: return True
    return False


def normalize(field, value):
    key, kind = field['key'], field['kind']
    if kind == 'boolean':
        if not isinstance(value, bool): raise ValueError(f'{field["label"]}: choose on or off.')
        return value
    if kind in ('number', 'integer'):
        try: number = float(value)
        except (ValueError, TypeError): raise ValueError(f'{field["label"]}: enter a number.')
        if isinstance(value, bool) or not math.isfinite(number) or not field['minimum'] <= number <= field['maximum'] or (kind == 'integer' and number != int(number)):
            raise ValueError(f'{field["label"]}: use {field["minimum"]}–{field["maximum"]}.')
        return int(number) if kind == 'integer' else number
    if kind == 'repositories':
        from .source_repositories import normalize_repository_url
        if not isinstance(value, list) or len(value) > 20: raise ValueError('Use at most 20 repository URLs.')
        return list(dict.fromkeys(normalize_repository_url(url) for url in value))
    if not isinstance(value, str) or len(value) > 4096 or any(ord(c) < 32 for c in value):
        raise ValueError(f'{field["label"]}: invalid value.')
    value = value if key == 'MAIN_SUWAYOMI_PASSWORD' else value.strip()
    if not value and field.get('allow_empty'): return ''
    if kind == 'secret':
        if key == 'AUTOSOURCARR_READER_API_TOKEN' and not re.fullmatch(r'[A-Za-z0-9._~-]{32,256}', value): raise ValueError('Reader API key must contain 32–256 letters, digits, dots, tildes, dashes or underscores.')
        if key == 'REPAIR_AI_API_KEY' and any(ord(c)>126 for c in value): raise ValueError('AI API key must contain printable ASCII characters.')
    elif kind == 'choice':
        if value not in dict(field['options']): raise ValueError(f'{field["label"]}: choose one of the listed options.')
    elif kind in ('url', 'translation'):
        if kind == 'translation' and not value: return ''
        try:
            url = urlsplit(value)
            port = url.port
            valid = url.scheme in ('http', 'https') and url.hostname and not url.username and not url.password and not url.query and not url.fragment and not any(c.isspace() for c in value)
        except ValueError: valid = False
        if not valid: raise ValueError(f'{field["label"]}: use a complete HTTP(S) address without credentials, query or fragment.')
        if key == 'REPAIR_AI_BASE_URL' and any(p in {'.','..'} for p in unquote(url.path).split('/')): raise ValueError('AI base address cannot contain path traversal.')
        value = value.rstrip('/')
    elif key == 'TZ':
        try: ZoneInfo(value)
        except (ZoneInfoNotFoundError, ValueError): raise ValueError('Time zone must be an IANA name such as Europe/Amsterdam.')
    elif key == 'AUTHELIA_USER_HEADER':
        if not re.fullmatch(r'[A-Za-z0-9-]{1,100}', value): raise ValueError('Identity header must contain only letters, numbers and hyphens.')
    elif key == 'MAIN_SUWAYOMI_USERNAME' and ':' in value:
        raise ValueError('HTTP Basic usernames cannot contain a colon.')
    elif key == 'REPAIR_AI_MODEL' and len(value)>256:
        raise ValueError('AI model identifier must be at most 256 characters.')
    elif key == 'REPAIR_AI_WORKSPACE_ID' and (len(value)>128 or any(not(c.isascii() and (c.isalnum() or c in '_-')) for c in value)):
        raise ValueError('Claude workspace ID must contain at most 128 ASCII letters, numbers, underscores or hyphens.')
    elif kind == 'path':
        if not value.startswith('/'): raise ValueError('Downloads folder must be an absolute container path.')
    elif kind in ('languages', 'translation_languages', 'providers'):
        parts = list(dict.fromkeys(p.strip().lower() for p in value.split(',') if p.strip()))
        allowed = {'ja','ko','zh'} if kind == 'translation_languages' else {'mangadex','anilist','mangaupdates','kitsu','myanimelist','comicvine'} if kind == 'providers' else None
        if not parts or any(not re.fullmatch(r'[a-z][a-z0-9-]{1,24}', p) or (allowed is not None and p not in allowed) for p in parts):
            raise ValueError(f'{field["label"]}: use valid comma-separated identifiers.')
        value = ','.join(parts)
    return value


def patch_env(raw, changes):
    # Values cannot introduce lines or interpolation; quote all strings safely for dotenv and Compose.
    lines = (raw or b'').decode().splitlines()
    for key, value in changes.items():
        if isinstance(value, list): value = json.dumps(value, separators=(',', ':'))
        elif isinstance(value, bool): value = str(value).lower()
        else: value = str(value)
        encoded = "'" + value.replace('\\', '\\\\').replace("'", "\\'") + "'"
        if re.fullmatch(r'[A-Za-z0-9_.,:/@%+~=-]*', value): encoded = value
        indices = [i for i, line in enumerate(lines) if re.match(r'^\s*(?:export\s+)?'+re.escape(key)+r'\s*=', line)]
        if len(indices) > 1: raise ValueError(f'Duplicate configuration key: {key}. Resolve it in settings first.')
        if indices: lines[indices[0]] = key + '=' + encoded
        else: lines.append(key + '=' + encoded)
    return ('\n'.join(lines)+'\n').encode()


def finish(root, settings, changes, revision, transaction):
    root = Path(root)
    if not isinstance(changes, dict) or len(changes) > len(FIELDS) or any(k not in BY_KEY for k in changes):
        raise ValueError('Setup contains an unsupported setting.')
    try: uuid.UUID(transaction)
    except (ValueError, TypeError, AttributeError): raise ValueError('Invalid setup request identifier.')
    with ENV_WRITE_LOCK:
        root.mkdir(parents=True, exist_ok=True)
        recover(root)
        oldstate = state_at(root)
        if oldstate.get('transaction') == transaction:
            return dict(completed=True, changed=oldstate.get('changed', []), restart_required=oldstate.get('restart_required', False), transaction=transaction,
                        reader_build=evaluate_reader_config(effective_values(root, settings)))
        if revision != _revision(root): raise SetupConflict('Settings changed elsewhere. Reload setup before saving; your draft has not been applied.')
        current = effective_values(root, settings)
        updates = {key: normalize(BY_KEY[key], value) for key, value in changes.items()}
        updates = {key: value for key, value in updates.items() if value != current[key]}
        merged = {**current, **updates}
        if merged['REPAIR_AI_PROVIDER'] != 'chatgpt' and settings.app_services_mode != 'integrated':
            raise ValueError('Custom repair AI requires the integrated AutoSourcarr image. External workers retain their own account configuration.')
        if merged['REPAIR_AI_PROVIDER'] != 'chatgpt' and (not merged['REPAIR_AI_BASE_URL'] or not merged['REPAIR_AI_MODEL']):
            raise ValueError('Custom repair AI needs an API base URL and model name.')
        if merged['REPAIR_AI_PROVIDER'] in {'openai','claude','gemini','openrouter'} and not merged['REPAIR_AI_API_KEY']:
            raise ValueError('This AI provider needs its API key. A consumer subscription is separate from API access.')
        if 'REPAIR_AI_PROVIDER' in updates and current['REPAIR_AI_API_KEY'] and 'REPAIR_AI_API_KEY' not in changes:
            raise ValueError('AI provider changed: explicitly replace or remove its saved credential.')
        if merged['TRANSLATION_MODE'] == 'external' and not merged['TRANSLATION_API_URL']:
            raise ValueError('External translation needs a LibreTranslate API address.')
        for address, secret in [('REPAIR_AI_BASE_URL', 'REPAIR_AI_API_KEY'), ('TRANSLATION_API_URL', 'TRANSLATION_API_KEY'), ('MAIN_SUWAYOMI_URL', 'MAIN_SUWAYOMI_PASSWORD')]:
            if address in updates and current[secret] and secret not in changes:
                raise ValueError(f'{BY_KEY[address]["label"]} changed: explicitly replace or remove its saved credential before sending it to another address.')
        if any(key in updates for key in ('BACKGROUND_SOURCE_CONCURRENCY', 'SOURCE_HOST_WORK_CONCURRENCY')) and merged['BACKGROUND_SOURCE_CONCURRENCY'] >= merged['SOURCE_HOST_WORK_CONCURRENCY']:
            raise ValueError('Background concurrency must be below source engine capacity to leave room for interactive work.')
        if any(key in updates for key in ('AUTO_MATCH_THRESHOLD', 'FUZZY_REVIEW_THRESHOLD')) and merged['FUZZY_REVIEW_THRESHOLD'] > merged['AUTO_MATCH_THRESHOLD']:
            raise ValueError('Candidate threshold must not exceed the automatic threshold.')
        before = {name: read_file(root / name) for name in FILES}
        app_updates = {k: v for k, v in updates.items() if k != 'EXTENSION_STORES'}
        after = dict(before)
        existing_app = env_at(root, 'autosourcarr.env')
        app_updates = {**{k: v for k, v in merged.items() if k != 'EXTENSION_STORES' and k not in existing_app}, **app_updates}
        if app_updates: after['autosourcarr.env'] = patch_env(before['autosourcarr.env'], app_updates)
        if 'EXTENSION_STORES' in updates or 'EXTENSION_STORES' not in env_at(root, 'source-host.env'):
            after['source-host.env'] = patch_env(before['source-host.env'], {'EXTENSION_STORES': merged['EXTENSION_STORES']})
        changed = list(updates)
        pending = bool(updates) or restart_needed(current, settings) or after['source-host.env'] != before['source-host.env']
        newstate = dict(completed=True, transaction=transaction,
                        managed_keys=sorted(set(oldstate.get('managed_keys', [])) | set(app_updates)),
                        changed=changed, restart_required=pending, cleanup={'status': 'pending'})
        after[STATE] = json.dumps(newstate).encode()
        if any(v is not None and len(v) > LIMIT for v in after.values()): raise ValueError('Setup configuration exceeds its supported size.')
        journal = dict(transaction=transaction, before={k: base64.b64encode(v).decode() if v is not None else None for k,v in before.items()},
                       effective_before={k: current[k] for k in app_updates})
        atomic_write(root / JOURNAL, json.dumps(journal).encode())
        try:
            for name in FILES:
                if after[name] != before[name]: atomic_write(root / name, after[name])
        except BaseException:
            recover(root)
            raise
        atomic_write(root / JOURNAL, None)
        return dict(completed=True, changed=changed, restart_required=pending, transaction=transaction,
                    reader_build=evaluate_reader_config(merged))


def record_cleanup(root, transaction, result):
    with ENV_WRITE_LOCK:
        state = state_at(root)
        if state.get('transaction') == transaction:
            state['cleanup'] = result
            atomic_write(root / STATE, json.dumps(state).encode())
