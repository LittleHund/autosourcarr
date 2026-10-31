"""Bounded discovery and explicit installation of original Keiyoushi archives.

No source website is visited during discovery. A pinned repository manifest (and
release digest where present) is the authority; forks/mirrors are never substituted.
An installed archive remains unverified until the isolated repair worker validates it.
"""
from __future__ import annotations

import asyncio
from datetime import datetime, timedelta, timezone
import io
import json
from pathlib import Path
import re
import struct
import threading
import time
from urllib.parse import urlsplit, quote
import xml.etree.ElementTree as ET
import zipfile
import zlib

from fastapi import APIRouter, Depends, HTTPException
import httpx

from .async_utils import run_blocking
from .config import settings
from .diagnostics import scrub
from .env_settings import parse_env
from .source_maintenance import atomic, sha, inventory, worker, source_maintenance, ensure_repair_ready
from .task_registry import task_registry
from .source_repositories import _protobuf_fields, repository_snapshot
from .support_logs import support_logs

REPOSITORY = 'https://github.com/keiyoushi/extensions'
RAW = 'https://raw.githubusercontent.com/keiyoushi/extensions/'
API = 'https://api.github.com/repos/keiyoushi/extensions/'
SNAPSHOT_DAYS = (0, 7, 30, 60, 90, 180, 365, 730)
MAX_INDEX = 8 * 1024 * 1024
MAX_PACKAGE = 32 * 1024 * 1024
PKG = re.compile(r'eu\.kanade\.tachiyomi\.extension\.[a-z0-9]+\.[a-z0-9_]+')
LABEL_PACKAGE = re.compile(r'[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*){2,}')
COMMIT = re.compile(r'[0-9a-f]{40}')
DIGEST = re.compile(r'[0-9a-f]{64}')
NOTICE = ('Original Keiyoushi snapshots are sampled over two years; this is not the complete history. '
          'Missing from today’s catalog does not prove a website is retired. '
          'Archived installation does not prove the source works. Repair checks metadata, chapters and page images; '
          'CAPTCHA, login and rate limits require attention rather than repeated code repairs.')


def label_package(value):
    # Manual classification supports every namespace accepted by APK upload.
    # It grants no Keiyoushi archive provenance or repair eligibility.
    return len(value) <= 200 and LABEL_PACKAGE.fullmatch(value) is not None


def text_field(fields, key, default=''):
    value = next((v for k, v in fields if k == key), None)
    return value.decode('utf-8') if isinstance(value, bytes) else default


def expanded(data):
    if not data or len(data) > MAX_INDEX:
        raise ValueError('Repository index is empty or exceeds 8 MiB')
    if data.startswith(b'\x1f\x8b'):
        decoder = zlib.decompressobj(16 + zlib.MAX_WBITS)
        data = decoder.decompress(data, MAX_INDEX + 1)
        if len(data) > MAX_INDEX or decoder.unconsumed_tail or not decoder.eof or decoder.unused_data:
            raise ValueError('Repository index is oversized or incomplete')
    return data


def package_url(value, commit):
    """Only original repository files or releases, never manifest-supplied mirrors."""
    if not COMMIT.fullmatch(commit):
        raise ValueError('Archive requires a complete repository commit')
    p = urlsplit(str(value))
    if p.scheme != 'https' or p.username or p.password or p.query or p.fragment or p.port not in (None, 443):
        return ''
    if p.hostname == 'raw.githubusercontent.com':
        match = re.fullmatch(r'/keiyoushi/extensions/(?:repo|[0-9a-f]{40})/((?:apk|jar)/[A-Za-z0-9_.+\-]+\.(?:apk|jar))', p.path)
        if match:
            return RAW + commit + '/' + match[1]
    if p.hostname == 'github.com':
        if re.fullmatch(r'/keiyoushi/extensions/releases/download/[A-Za-z0-9_.\-]+/[A-Za-z0-9_.+\-]+\.(?:apk|jar)', p.path):
            return str(value)
        match = re.fullmatch(r'/keiyoushi/extensions/raw/(?:repo|[0-9a-f]{40})/((?:apk|jar)/[A-Za-z0-9_.+\-]+\.(?:apk|jar))', p.path)
        if match:
            return RAW + commit + '/' + match[1]
    return ''


def parse_index(raw, commit, *, assets=None):
    """Official index.proto Resources.jarUrl=501; legacy JSON arrays remain valid."""
    data = expanded(raw)
    assets = assets or {}
    if data.lstrip().startswith(b'['):
        rows = json.loads(data)
        if not isinstance(rows, list) or len(rows) > 10000:
            raise ValueError('Invalid legacy extension index')
        candidates = []
        for row in rows:
            if not isinstance(row, dict): continue
            candidates.append({'name': row.get('name'), 'pkgName': row.get('pkg'),
                'versionName': row.get('version'), 'lang': row.get('lang'), 'nsfw': bool(row.get('nsfw')),
                'sources': row.get('sources', []),
                'apk': RAW+commit+'/apk/'+str(row.get('apk', '')), 'jar': ''})
    else:
        fields = _protobuf_fields(data)
        content = next((v for k, v in fields if k == 101 and isinstance(v, bytes)), None)
        if content is None:
            raise ValueError('Repository index does not contain its extension list')
        candidates = []
        for k, encoded in _protobuf_fields(content):
            if k != 1 or not isinstance(encoded, bytes): continue
            fields = _protobuf_fields(encoded)
            resources = next((v for k, v in fields if k == 3 and isinstance(v, bytes)), b'')
            resources = _protobuf_fields(resources)
            sources = []
            for k, encoded_source in fields:
                if k != 8 or not isinstance(encoded_source, bytes): continue
                source = _protobuf_fields(encoded_source)
                source_id = next((v for k, v in source if k == 1 and isinstance(v, int)), 0)
                if source_id >= 1 << 63: source_id -= 1 << 64
                sources.append({'id': str(source_id), 'name': text_field(source, 2),
                                'lang': text_field(source, 3), 'baseUrl': text_field(source, 4)})
            candidates.append({'name': text_field(fields, 1), 'pkgName': text_field(fields, 2),
                'versionName': text_field(fields, 6), 'lang': '', 'sources': sources,
                'nsfw': any(k == 7 and v in (2, 3) for k, v in fields),
                'apk': text_field(resources, 1), 'jar': text_field(resources, 501)})
    result = {}
    for row in candidates:
        pkg = str(row['pkgName'] or '')
        if not PKG.fullmatch(pkg) or pkg.endswith(('.keiyoushi', '.mihon')):
            continue
        sources = [s for s in row['sources'] if isinstance(s, dict) and re.fullmatch(r'-?[0-9]+', str(s.get('id', '')))][:200]
        langs = sorted(set(str(s.get('lang') or '') for s in sources) - {''})
        artifact = ''
        fingerprint = ''
        for kind in ('jar', 'apk'):
            url = package_url(row[kind], commit)
            if not url: continue
            digest_record = assets.get(pkg, {}).get(kind, {})
            checksum = str(digest_record.get('sha256') or '')
            same_name = digest_record.get('name') == url.rsplit('/', 1)[-1]
            # Release URLs may be replaced/removed. A pinned original digest is
            # mandatory there; old raw repository objects are already commit-pinned.
            if url.startswith(RAW) or (same_name and DIGEST.fullmatch(checksum)):
                artifact, fingerprint = url, checksum if same_name else ''
                break
        result[pkg] = {'pkgName': pkg, 'name': str(row['name'] or pkg)[:160],
            'versionName': str(row['versionName'] or '')[:60],
            'lang': str(row['lang'] or ('all' if len(langs) != 1 else langs[0])),
            'languages': langs, 'nsfw': row['nsfw'], 'sources': sources,
            'source_count': len(sources), 'origin': 'Keiyoushi', 'commit': commit,
            'commit_url': REPOSITORY+'/commit/'+commit, 'archive_url': artifact,
            'sha256': fingerprint, 'module': 'src/'+pkg.removeprefix('eu.kanade.tachiyomi.extension.').replace('.', '/')}
    if not result:
        raise ValueError('Repository index contains no real sources; an app-upgrade notice is not an empty catalog')
    return result


async def fetch(client, url, maximum=MAX_INDEX, *, optional=False):
    """Bound streamed bytes, redirects and time; no forwarded user credentials."""
    for redirect in range(4):
        p = urlsplit(url)
        if p.scheme != 'https' or p.username or p.password or p.port not in (None, 443):
            raise ValueError('Invalid original repository URL')
        if p.hostname not in {'api.github.com', 'raw.githubusercontent.com', 'github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com'}:
            raise ValueError('Original repository redirected outside supported GitHub hosts')
        async with client.stream('GET', url, headers={'User-Agent': 'AutoSourcarr-orphan-recovery', 'Accept-Encoding': 'identity'}) as response:
            if response.status_code in {301, 302, 303, 307, 308}:
                url = str(response.url.join(response.headers.get('Location', '')))
                continue
            if optional and response.status_code == 404:
                return None
            if response.status_code in {403, 429}:
                raise ValueError('GitHub rate limit or access restriction; archive discovery paused. Try later.')
            if response.status_code != 200:
                raise ValueError('Original repository returned HTTP '+str(response.status_code)+'; no alternate package was selected')
            data = bytearray()
            async for chunk in response.aiter_bytes():
                data.extend(chunk)
                if len(data) > maximum: raise ValueError('Repository response exceeds its size limit')
            return bytes(data)
    raise ValueError('Too many repository redirects')


def manifest_package(data):
    """Read package identity from plain JVM XML or the root Android binary XML."""
    if len(data) > 256 * 1024: raise ValueError('Extension manifest is too large')
    if data.lstrip().startswith(b'<'):
        if re.search(br'<!\s*(?:DOCTYPE|ENTITY)\b', data, re.I): raise ValueError('Unsupported XML declaration')
        return ET.fromstring(data).get('package', '')
    if len(data) < 8 or struct.unpack_from('<H', data)[0] != 3:
        raise ValueError('Unknown extension manifest format')
    strings = []
    def u16(offset): return struct.unpack_from('<H', data, offset)[0]
    def u32(offset): return struct.unpack_from('<I', data, offset)[0]
    def string_at(pos, utf8):
        def length(pos, byte):
            value = data[pos] if byte else u16(pos); pos += 1 if byte else 2
            bit = 0x80 if byte else 0x8000
            if value & bit:
                value = ((value & (bit-1)) << (8 if byte else 16)) | (data[pos] if byte else u16(pos)); pos += 1 if byte else 2
            return value, pos
        if utf8:
            _, pos = length(pos, True)
            size, pos = length(pos, True)
            return data[pos:pos+size].decode('utf-8')
        size, pos = length(pos, False)
        return data[pos:pos+size*2].decode('utf-16-le')
    end = u32(4)
    if end != len(data): raise ValueError('Incomplete binary extension manifest')
    offset = u16(2)
    while offset + 8 <= end:
        kind, header, size = struct.unpack_from('<HHI', data, offset)
        if header < 8 or size < header or offset + size > end: raise ValueError('Invalid manifest chunk')
        if kind == 1:
            count, flags, start = u32(offset+8), u32(offset+16), u32(offset+20)
            if count > 10000 or header+count*4 > size: raise ValueError('Invalid manifest strings')
            strings = [string_at(offset+start+u32(offset+header+4*i), bool(flags & 0x100)) for i in range(count)]
        elif kind == 0x102:
            ext = offset + header
            if strings[u32(ext+4)] == 'manifest':
                start, step, count = u16(ext+8), u16(ext+10), u16(ext+12)
                if step < 20 or ext+start+count*step > offset+size: raise ValueError('Invalid manifest attributes')
                for index in range(count):
                    pos = ext+start+index*step
                    if strings[u32(pos+4)] == 'package':
                        raw = u32(pos+8)
                        if raw != 0xffffffff: return strings[raw]
                        if data[pos+15] == 3: return strings[u32(pos+16)]
                break
        offset += size
    raise ValueError('Package identity is missing from the archive')


def validate_package(data, expected):
    if not 100 < len(data) <= MAX_PACKAGE: raise ValueError('Invalid archived package size')
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        if sum(x.file_size for x in archive.infolist()) > 160*1024*1024: raise ValueError('Expanded package is too large')
        if archive.getinfo('AndroidManifest.xml').file_size > 256*1024: raise ValueError('Manifest is too large')
        package = manifest_package(archive.read('AndroidManifest.xml'))
        if package != expected: raise ValueError('Archived binary package identity does not match its original catalog entry')
        if not any(n.endswith(('.class', '.dex')) for n in archive.namelist()): raise ValueError('Archive has no executable extension code')


class SourceOrphans:
    def __init__(self):
        self.loaded_path = None
        self.file_lock = threading.RLock()
        self.records = {}
        self.labels = {}
        self.current = set()
        self.refresh = {'state': 'idle', 'message': 'Refresh to discover original archived sources', 'snapshots': 0, 'requests': 0, 'coverage_days': 730}
        self.operation = {'state': 'idle'}
        self.task = None
        self.action = None
        self.guard = asyncio.Lock()
        self.inventory_cache = []
        self.inventory_at = 0
        self.inventory_known = False
        self.inventory_task = None
        self.inventory_revision = 0

    def path(self): return settings.data_dir/'source-maintenance'/'orphans.json'

    def load(self):
        with self.file_lock:
            self._load()

    def _load(self):
        path = self.path()
        if self.loaded_path == path: return
        self.records, self.current, self.labels = {}, set(), {}
        seed = Path(__file__).with_name('source_orphans_seed.json')
        saved = path if path.is_file() else seed
        if saved.is_file():
            value = json.loads(saved.read_text())
            self.records = value.get('records', {})
            self.current = set(value.get('current', []))
            self.labels = value.get('labels', {})
            self.refresh.update(value.get('refresh', {}))
            if self.refresh['state'] == 'running': self.refresh.update(state='interrupted', message='Previous discovery stopped; retained results remain available')
            self.operation = value.get('operation', {'state': 'idle'})
            if self.operation.get('state') in {'queued', 'running', 'installing'}:
                self.operation.update(state='interrupted', message='Previous source operation was interrupted. Refresh installed status before trying again.')
        # Older successful local uploads already have durable receipts. Recover
        # their classification without treating failed/uncertain installs as success.
        for receipt_path in (path.parent / 'uploads').glob('*/receipt.json'):
            try:
                if receipt_path.is_symlink() or receipt_path.stat().st_size > 65536:
                    continue
                receipt = json.loads(receipt_path.read_text())
                pkg = str(receipt.get('pkgName') or '')
                if receipt.get('state') == 'installed' and label_package(pkg):
                    self.labels.setdefault(pkg, {'orphaned': True, 'reason': 'manual-upload',
                                                'name': pkg, 'marked_at': receipt.get('completed_at')})
            except (OSError, ValueError, TypeError, AttributeError):
                continue
        self.loaded_path = path

    def save(self):
        with self.file_lock:
            atomic(self.path(), json.dumps({'records': self.records, 'current': sorted(self.current), 'labels': self.labels,
                                          'refresh': self.refresh, 'operation': self.operation}).encode())

    def annotate_extensions(self, extensions):
        """Classify local inventory only; never fetch repositories or alter provenance."""
        with self.file_lock:
            self._load()
            try:
                path = Path(settings.settings_config_dir) / 'source-host.env'
                pairs, errors = parse_env(path)
                configured = ({row['url'] for row in repository_snapshot(path)['repositories']}
                              if not errors and 'EXTENSION_STORES' in dict(pairs) else None)
            except (OSError, ValueError):
                configured = None  # Unknown configuration is not proof of absence.
            result = []
            for item in extensions:
                pkg = str(item.get('pkgName') or '')
                installed = bool(item.get('isInstalled'))
                label = self.labels.get(pkg, {})
                store = item.get('storeIndexUrl')
                local_only = (installed and 'storeIndexUrl' in item and 'apkUrl' in item
                              and not store and not item.get('apkUrl'))
                missing = bool(item.get('isObsolete') is True or local_only or
                               (configured is not None and store and store not in configured))
                archived = pkg in self.records and pkg not in self.current
                automatic = bool(local_only or (installed and item.get('isObsolete')) or archived)
                orphaned = label.get('orphaned', automatic) is True
                # An uninstalled package advertised by a configured repo keeps
                # the ordinary Install path. Retain its label for a future install.
                listed = bool(item.get('apkUrl') and not item.get('isObsolete') and store
                              and (configured is None or store in configured))
                if not installed and listed: orphaned = False
                reason = label.get('reason') if label.get('orphaned') else (
                    'manual-upload' if local_only else 'archived' if archived else 'catalog-missing')
                result.append({**item, 'is_orphaned': orphaned,
                    'orphan_reason': reason if orphaned else '',
                    'can_mark_orphan': bool(installed and not orphaned and missing and label_package(pkg)),
                    'can_unmark_orphan': bool(installed and orphaned and label_package(pkg)),
                    'repository_missing': missing})
            return result

    def set_label(self, extension, orphaned, reason='manual'):
        pkg = str(extension.get('pkgName') or '')
        if not label_package(pkg):
            raise ValueError('Unsupported extension package name')
        with self.file_lock:
            self._load()
            previous = self.labels.get(pkg)
            self.labels[pkg] = {'orphaned': bool(orphaned), 'reason': reason,
                'name': str(extension.get('name') or pkg), 'lang': str(extension.get('lang') or ''),
                'versionName': str(extension.get('versionName') or ''), 'marked_at': time.time()}
            try:
                self.save()
            except Exception:
                if previous is None: self.labels.pop(pkg, None)
                else: self.labels[pkg] = previous
                raise

    def classification_snapshot(self, rows):
        with self.file_lock:
            return self.annotate_extensions(rows), dict(self.labels)

    def accept_inventory(self, rows):
        self.inventory_revision += 1
        self.inventory_cache = rows
        self.inventory_known = True
        self.inventory_at = time.monotonic()

    def update_inventory_package(self, pkg, extension):
        self.inventory_revision += 1
        self.inventory_cache = [row for row in self.inventory_cache if row.get('pkgName') != pkg]
        if extension: self.inventory_cache.append(extension)
        self.inventory_at = 0  # A one-package result is not a complete inventory.

    async def snapshot(self):
        await run_blocking(self.load)
        if time.monotonic()-self.inventory_at > 20 and (not self.inventory_task or self.inventory_task.done()):
            self.inventory_at = time.monotonic()
            self.inventory_task = asyncio.create_task(self.refresh_inventory())
        annotated, labels = await run_blocking(self.classification_snapshot, self.inventory_cache)
        installed = {x['pkgName']: x for x in annotated if x.get('isInstalled')}
        available = {x['pkgName'] for x in annotated if not x.get('isInstalled') and not x['is_orphaned']}
        items = []
        # Local/manual labels do not manufacture an original archive, commit or
        # working backup. Uninstalled local packages require another explicit upload.
        rows = {**{pkg: {'pkgName': pkg, **label, 'origin': 'Local package',
                        'message': 'Not installed. Upload its APK again to reinstall.'}
                   for pkg, label in labels.items() if label.get('orphaned')}, **self.records}
        for pkg, row in rows.items():
            is_installed = pkg in installed
            label = labels.get(pkg, {})
            if pkg in available: continue
            if label.get('orphaned') is False: continue
            if is_installed and not installed[pkg]['is_orphaned']: continue
            if not is_installed and pkg in self.current and not label.get('orphaned'): continue
            state = row.get('state', 'available')
            if is_installed and state == 'available': state = 'installed-unverified'
            if not is_installed: state = 'available' if row.get('archive_url') and row.get('archive_available') is not False else 'unavailable'
            if is_installed and source_maintenance.operation.get('state') == 'installed' and source_maintenance.operation.get('extension', {}).get('pkgName') == pkg:
                state = 'repaired'
            live = installed.get(pkg, {})
            message = row.get('message', 'Original archived package; functionality has not been verified.')
            if is_installed and live.get('orphan_reason') in {'manual', 'manual-upload'}:
                message = ('Manually added source. ' if live['orphan_reason'] == 'manual-upload' else 'Marked as orphaned. ') + 'Its installed package and source settings are unchanged.'
            items.append({**{k:v for k,v in row.items() if k not in {'sources'}},
                **{k: live[k] for k in ('name', 'lang', 'versionName', 'hasUpdate', 'isObsolete', 'orphan_reason', 'can_unmark_orphan') if k in live}, 'installed': is_installed,
                'state': state, 'can_install': bool(row.get('archive_url') and row.get('archive_available') is not False and not is_installed and self.inventory_known),
                'can_repair': bool(settings.repair_worker_url and PKG.fullmatch(pkg) and (is_installed or row.get('sources'))),
                'message': message})
        # Installed orphans remain visible even if older than the sampled history.
        for pkg, item in installed.items():
            if pkg in rows or not item['is_orphaned']: continue
            items.append({'pkgName': pkg, 'name': item.get('name', pkg), 'lang': item.get('lang', ''), 'versionName': item.get('versionName',''),
                'orphan_reason': item['orphan_reason'], 'can_unmark_orphan': item['can_unmark_orphan'],
                'hasUpdate': item.get('hasUpdate', False), 'isObsolete': item.get('isObsolete', False),
                'installed': True, 'state': 'installed-unverified', 'origin': 'Installed package; archive provenance not yet located',
                'can_install': False, 'can_repair': bool(settings.repair_worker_url and PKG.fullmatch(pkg)),
                'message': ('Manually added source; no repository download is recorded.' if item['orphan_reason'] == 'manual-upload' else
                            'Missing from the configured catalog. Refresh archives to locate the original history.')})
        return {'items': sorted(items, key=lambda x:x['name'].casefold()), 'refresh': dict(self.refresh),
                'operation': dict(self.operation), 'worker_configured': bool(settings.repair_worker_url),
                'inventory_known': self.inventory_known, 'inventory_refreshing': bool(self.inventory_task and not self.inventory_task.done()), 'notice': NOTICE}

    async def refresh_inventory(self):
        revision = self.inventory_revision
        try:
            rows = await asyncio.wait_for(inventory(), 5)
            if revision != self.inventory_revision: return
            self.accept_inventory(rows)
        except Exception:
            if revision == self.inventory_revision: self.inventory_known = False
        self.inventory_at = time.monotonic()

    async def check_current(self, pkg, client):
        commits = json.loads(await fetch(client, API+'commits?sha=repo&per_page=1'))
        head = str(commits[0].get('sha', '')) if commits else ''
        if not COMMIT.fullmatch(head): raise ValueError('Current original catalog could not be confirmed')
        current_index = await fetch(client, RAW+head+'/index.pb', optional=True)
        if current_index is None: current_index = await fetch(client, RAW+head+'/index.min.json')
        active = parse_index(current_index, head)
        async with self.guard:
            self.current = set(active)
            await run_blocking(self.save)
        if pkg in active:
            raise ValueError('The original maintained package is available again; use Manage sources instead of its archive')

    def merge_found(self, found):
        for pkg, row in found.items():
            existing = self.records.get(pkg, {})
            if existing.get('installed_at'):
                continue
            if (existing.get('archive_url'), existing.get('sha256')) == (row.get('archive_url'), row.get('sha256')):
                row = {**row, **{k:existing[k] for k in ('archive_available', 'archive_verified_at', 'message', 'repair_job_id') if k in existing}}
            self.records[pkg] = row

    async def discover(self):
        found, active, seen, failures = {}, None, set(), []
        try:
            self.refresh.update(state='running', message='Reading original Keiyoushi snapshots', snapshots=0, requests=0)
            await run_blocking(self.save)
            async with httpx.AsyncClient(timeout=httpx.Timeout(20, connect=5), trust_env=False) as client:
                async def request(url, optional=False):
                    self.refresh['requests'] += 1
                    return await fetch(client, url, optional=optional)
                async with asyncio.timeout(240):
                    for days in SNAPSHOT_DAYS:
                        stamp = (datetime.now(timezone.utc)-timedelta(days=days)).strftime('%Y-%m-%dT%H:%M:%SZ')
                        commits = json.loads(await request(API+'commits?sha=repo&per_page=1&until='+stamp))
                        if not commits: continue
                        commit = str(commits[0].get('sha', ''))
                        if not COMMIT.fullmatch(commit): raise ValueError('GitHub returned an invalid commit')
                        commit_date = str(commits[0].get('commit', {}).get('committer', {}).get('date') or stamp)
                        if commit in seen: continue
                        seen.add(commit)
                        raw = await request(RAW+commit+'/index.pb', optional=True)
                        modern = raw is not None
                        if raw is None: raw = await request(RAW+commit+'/index.min.json')
                        raw_assets = await request(RAW+commit+'/release-assets.json', optional=True) if modern else None
                        assets = json.loads(raw_assets) if raw_assets else {}
                        try:
                            rows = parse_index(raw, commit, assets=assets)
                        except ValueError as exc:
                            if days == 0: raise
                            failures.append(str(exc)); continue
                        self.refresh['snapshots'] += 1
                        self.refresh['oldest_snapshot'] = commit_date
                        if self.refresh['snapshots'] == 1: self.refresh['newest_snapshot'] = commit_date
                        if active is None:
                            active = set(rows)
                        else:
                            for pkg, row in rows.items():
                                if pkg not in active and pkg not in found:
                                    found[pkg] = {**row, 'snapshot_date': commit_date, 'sampled_cutoff': stamp, 'state': 'available'}
                        self.refresh['message'] = f"Read {self.refresh['snapshots']} original snapshots; found {len(found)} missing packages"
                        await asyncio.sleep(.15)
            if active is None: raise ValueError('No current official catalog could be read')
            async with self.guard:
                self.merge_found(found)
                self.current = active
                self.refresh.update(state='complete', checked_at=time.time(),
                    message=f"Read {self.refresh['snapshots']} snapshots; {len(found)} archived packages found" + (f"; {len(failures)} incomplete snapshots skipped" if failures else ''))
                await run_blocking(self.save)
        except asyncio.CancelledError:
            self.refresh.update(state='interrupted', message='Discovery stopped; previously saved results retained')
            await run_blocking(self.save)
            raise
        except Exception as exc:
            async with self.guard:
                # Successfully read historical snapshots remain useful if a later
                # GitHub rate limit interrupts this bounded scan.
                if active is not None:
                    self.current = active
                    self.merge_found(found)
                self.refresh.update(state='partial' if found else 'failed', message='Archive discovery paused: '+str(scrub(str(exc))))
                await run_blocking(self.save)

    async def install(self, pkg, lock):
        from .suwayomi import source_host
        from .service import service
        try:
            async with lock:
                await run_blocking(self.load)
                if any(x['pkgName'] == pkg and x.get('isInstalled') for x in await inventory()):
                    raise ValueError('Source is already installed; an archive will not overwrite it')
                row = dict(self.records[pkg])
                if pkg in self.current: raise ValueError('A maintained package is available; install it through Manage sources')
                url = package_url(row.get('archive_url', ''), row.get('commit', ''))
                if not url or url != row['archive_url']: raise ValueError('No verified original download is available')
                self.operation = {'state': 'installing', 'pkgName': pkg, 'message': 'Downloading the original archived package'}
                await run_blocking(self.save)
                async with httpx.AsyncClient(timeout=httpx.Timeout(45, connect=5), trust_env=False) as client:
                    async with asyncio.timeout(90):
                        await self.check_current(pkg, client)
                        data = await fetch(client, url, MAX_PACKAGE)
                if row.get('sha256') and sha(data) != row['sha256']: raise ValueError('Archived download does not match its original release SHA256')
                await run_blocking(validate_package, data, pkg)
                suffix = '.jar' if url.endswith('.jar') else '.apk'
                archive_path = settings.data_dir/'source-maintenance'/'archives'/sha(data)/('original'+suffix)
                await run_blocking(atomic, archive_path, data)
                self.operation['message'] = 'Installing original archive; functionality is not yet verified'
                # Retain recovery provenance before mutation; do not automatically
                # replay an install after an uncertain response or application restart.
                async with self.guard:
                    self.records[pkg].update(download_sha256=sha(data), archive_saved=True)
                    await run_blocking(self.save)
                response = await source_host.client.post(source_host.base_url+'/api/v1/extension/install',
                    files={'file': ('archive'+suffix, data, 'application/java-archive' if suffix == '.jar' else 'application/vnd.android.package-archive')}, timeout=90)
                response.raise_for_status()
                current = next((x for x in await inventory() if x['pkgName'] == pkg and x.get('isInstalled')), None)
                if current is None: raise ValueError('Source engine did not confirm installation; refresh installed sources before retrying')
                expected_ids = sorted(str(x['id']) for x in row.get('sources', []))
                actual_ids = sorted(str(x['id']) for x in current.get('source', {}).get('nodes', []))
                message = 'Archived source installed. Metadata, chapters and pages still need functional verification.'
                if expected_ids and expected_ids != actual_ids:
                    message = 'Archived source installed, but source IDs differ from its historical index. Review before using or repairing.'
                async with self.guard:
                    self.records[pkg].update(state='installed-unverified', installed_at=time.time(), message=message,
                        identity_matches=not expected_ids or expected_ids == actual_ids)
                    self.operation.update(state='installed', message=message)
                    await run_blocking(self.save)
                self.inventory_cache = [e for e in self.inventory_cache if e.get('pkgName') != pkg] + [current]
                self.inventory_known = True
                self.inventory_at = time.monotonic()
                if not expected_ids or expected_ids == actual_ids:
                    service.schedule_source_reconciliation(delay=.25)
                from .source_raws import source_raws
                source_raws.schedule({**current,'storeIndexUrl':'https://github.com/keiyoushi/extensions/raw/repo/index.pb'},manual=False)
        except Exception as exc:
            message = str(scrub(str(exc))) or type(exc).__name__
            if 'Original repository returned HTTP 404' in message and pkg in self.records:
                self.records[pkg].update(archive_available=False,
                    message='Original archived binary was removed. Use Build and check archive to recover the original source code with the configured repair worker.')
            self.operation.update(state='failed', message=message)
            await run_blocking(self.save)

    async def repair(self, pkg, lock):
        async with lock:
            row = self.records.get(pkg, {})
            if row.get('identity_matches') is False: raise HTTPException(409, 'Historical source identity differs; manual review is required')
            extension = next((e for e in await inventory() if e['pkgName'] == pkg and e.get('isInstalled')), None)
            if extension:
                from .repair_workflow import repair_workflow
                from .source_raws import source_raws
                request={'issue':'Repair this installed orphan using its original ecosystem and retained raw code. Check website/API addresses and matching source identity; do not substitute another site or bypass login/CAPTCHA.'}
                saved=source_raws.catalog.status(pkg) if source_raws.catalog else None
                if row.get('module') and row.get('snapshot_date') and not (saved and saved.get('state')=='available' and saved.get('version')==extension.get('versionName')):
                    try:
                        revision=await self.repair_revision(row,require_pin=True)
                        request['code_origin']={'repository':'https://github.com/keiyoushi/extensions-source',
                            'module':row['module'],'revision':revision,'ecosystem':'keiyoushi'}
                    except (ValueError,httpx.HTTPError):
                        support_logs.record('source-raws','Original archive source-history lookup was inconclusive; normal raw input/reconstruction remains available.')
                result=await repair_workflow.create(extension,request)
                if pkg in self.records:
                    self.records[pkg].update(repair_job_id=result['id'],message='Repair admitted; raw input, execution and confirmation are in Tasks → Repairs.')
                    await run_blocking(self.save)
                return result
            if not PKG.fullmatch(pkg): raise HTTPException(400, 'Unsupported original extension package name')
            if not extension and (not row.get('sources') or pkg in self.current):
                raise HTTPException(409, 'A historical original source identity is required for recovery')
            revision = await self.repair_revision(row, require_pin=extension is None)
            payload = {'pkg_name': pkg, 'origin': 'manual',
                'module': 'src/'+pkg.removeprefix('eu.kanade.tachiyomi.extension.').replace('.', '/'), 'revision': revision,
                'issue': 'Recover this original orphaned source. Check the same maintained Keiyoushi module first, then its historical implementation; preserve original website/source identity and verify metadata, chapters, covers and page images. Do not substitute another site or bypass CAPTCHA/login.'}
            payload.update(recovery=True, expected_sources=row['sources'])
            result = await worker('POST', '/jobs', payload=payload)
            if pkg in self.records:
                self.records[pkg].update(state='installed-unverified' if extension else 'available', repair_job_id=result.get('id'),
                    message='Isolated repair/recovery requested; see Tasks for progress and functional evidence before installation.')
                await run_blocking(self.save)
            return result

    async def install_recovered(self, job, artifact):
        try:
            return await self._install_recovered(job, artifact)
        except Exception as exc:
            uncertain = self.operation.get('state') == 'installing'
            message = str(scrub(str(exc))) or type(exc).__name__
            self.operation = {'state': 'failed', 'pkgName': job['pkg_name'], 'message':
                ('Installation was not confirmed. Refresh installed sources before retrying; no automatic replay. ' if uncertain else '') + message}
            self.inventory_at = 0
            if uncertain: self.inventory_known = False
            await run_blocking(self.save)
            raise

    async def _install_recovered(self, job, artifact):
        """Called with the application's admin lock, after worker proof checks."""
        from .suwayomi import source_host
        from .service import service
        pkg = job['pkg_name']
        await run_blocking(self.load)
        if pkg not in self.records: raise ValueError('Original archive provenance is no longer available; refresh and recover again')
        if any(e['pkgName'] == pkg and e.get('isInstalled') for e in await inventory()):
            raise ValueError('A source was installed while recovery ran; it will not be overwritten')
        async with httpx.AsyncClient(timeout=httpx.Timeout(15, connect=5), trust_env=False) as client:
            await self.check_current(pkg, client)
        await run_blocking(validate_package, artifact, pkg)
        # The tested artifact is preserved before mutation. An uncertain response
        # is never replayed automatically and an existing install is never replaced.
        archive = settings.data_dir/'source-maintenance'/'archives'/sha(artifact)/'recovered.jar'
        await run_blocking(atomic, archive, artifact)
        self.operation = {'state': 'installing', 'pkgName': pkg, 'message': 'Installing the functionally checked original recovery'}
        await run_blocking(self.save)
        response = await source_host.client.post(source_host.base_url+'/api/v1/extension/install',
            files={'file': ('recovered.jar', artifact, 'application/java-archive')}, timeout=90)
        response.raise_for_status()
        extension = next((e for e in await inventory() if e['pkgName'] == pkg and e.get('isInstalled')), None)
        if not extension: raise ValueError('Recovery install is not confirmed; refresh installed sources before retrying')
        ids = sorted(str(x['id']) for x in extension.get('source', {}).get('nodes', []))
        if ids != sorted(job['expected_ids']):
            self.records[pkg].update(state='installed-unverified', identity_matches=False,
                message='Recovered package is installed but live source IDs differ from validated identity; review required.')
            self.operation.update(state='failed', message=self.records[pkg]['message'])
            await run_blocking(self.save)
            raise ValueError(self.records[pkg]['message'])
        self.records[pkg].update(installed_at=time.time(), download_sha256=sha(artifact), identity_matches=True)
        await self.record_repair(extension, job['id'])
        self.operation.update(state='installed', message='Original orphan recovered and installed after functional checks')
        await run_blocking(self.save)
        self.inventory_cache = [e for e in self.inventory_cache if e.get('pkgName') != pkg] + [extension]
        self.inventory_known = True
        self.inventory_at = time.monotonic()
        service.schedule_source_reconciliation(delay=.25)
        return extension

    async def repair_revision(self, row, *, require_pin=False):
        # Current upstream has priority. A missing historical module gets a full
        # source-repository commit from the archive date, never the unrelated
        # binary repository commit or an unofficial replacement.
        if not row.get('module') or not row.get('snapshot_date'): return ''
        repo = 'https://api.github.com/repos/keiyoushi/extensions-source/'
        module = row['module']
        if not re.fullmatch(r'src/[a-z0-9]+/[a-z0-9_]+', module):
            raise ValueError('Invalid archived source module')
        async with httpx.AsyncClient(timeout=httpx.Timeout(15, connect=5), trust_env=False) as client:
            if await fetch(client, repo+'contents/'+module+'?ref=main', optional=True) is not None:
                if not require_pin: return ''
                head = json.loads(await fetch(client, repo+'commits/main'))
                commit = str(head.get('sha', ''))
                if not COMMIT.fullmatch(commit): raise ValueError('Current original source commit is unavailable')
                return commit
            commits = json.loads(await fetch(client, repo+'commits?path='+module+'&per_page=1&until='+quote(row['snapshot_date'], safe='')))
            if not commits: raise ValueError('Original source history was not found before the archive date')
            commit = str(commits[0].get('sha', ''))
            if not COMMIT.fullmatch(commit): raise ValueError('Invalid source-history commit')
            if await fetch(client, repo+'contents/'+module+'?ref='+commit, optional=True) is None:
                parents = commits[0].get('parents') or []
                commit = str(parents[0].get('sha', '')) if parents else ''
                if not COMMIT.fullmatch(commit): raise ValueError('Original source removal parent is unavailable')
            return commit

    async def prepare_repair(self, pkg, lock):
        self.operation = {'state': 'running', 'pkgName': pkg, 'message': 'Locating the original source revision for an isolated repair'}
        task_id = task_registry.create('Prepare source repair', message=self.operation['message'], context=pkg, origin='manual',kind='source-repair-preparation')
        support_logs.observe_orphan_operation(self.operation)
        try:
            result = await self.repair(pkg, lock)
            blocked = result.get('state') in {'blocked', 'failed', 'cancelled', 'interrupted'}
            self.operation = {'state': 'failed' if blocked else 'complete', 'pkgName': pkg, 'repair_job_id': result.get('id'),
                              'message': ('Repair '+str(result['state'])+': '+str(scrub(str(result.get('message') or 'Open its evidence for details')))) if blocked else
                              'Isolated recovery queued. Open Tasks → Repairs for progress and functional evidence.'}
        except asyncio.CancelledError:
            task_registry.cancel(task_id, message='Repair preparation cancelled during shutdown')
            raise
        except Exception as exc:
            detail = exc.detail if isinstance(exc, HTTPException) else str(exc) or type(exc).__name__
            self.operation = {'state': 'failed', 'pkgName': pkg, 'message':
                'Recovery preparation did not finish: '+str(scrub(str(detail)))+'. Check Tasks → Repairs → Check repair tools before retrying; requests are not replayed automatically.'}
        if self.operation['state'] == 'failed':
            task_registry.fail(task_id, self.operation['message'])
        else:
            task_registry.complete(task_id, message=self.operation['message'])
        support_logs.observe_orphan_operation(self.operation)
        await run_blocking(self.save)

    async def record_repair(self, extension, job_id):
        await run_blocking(self.load)
        pkg = extension.get('pkgName')
        async with self.guard:
            if pkg in self.records:
                self.records[pkg].update(state='repaired' if job_id else 'installed-unverified', repair_job_id=job_id,
                    repaired_at=time.time() if job_id else None, message='Repair installed after isolated functional checks. Original archive is retained.' if job_id else 'Repair rolled back; the original installed source is retained.')
                await run_blocking(self.save)

    async def stop(self):
        if self.inventory_task and not self.inventory_task.done():
            self.inventory_task.cancel(); await asyncio.gather(self.inventory_task, return_exceptions=True)
        if self.task and not self.task.done():
            self.task.cancel(); await asyncio.gather(self.task, return_exceptions=True)
        if self.action and not self.action.done(): await asyncio.shield(self.action)


source_orphans = SourceOrphans()


def orphan_router(admin_guard, lock):
    router = APIRouter(prefix='/api/admin/source-orphans', dependencies=[Depends(admin_guard)])

    @router.get('')
    async def snapshot(): return await source_orphans.snapshot()

    async def classify(pkg_name, orphaned):
        if not label_package(pkg_name): raise HTTPException(400, 'Unsupported extension package name')
        async with lock:
            try:
                rows = await asyncio.wait_for(inventory(), 5)
            except Exception:
                raise HTTPException(503, 'Source inventory is unavailable. No orphan label was changed.') from None
            extension = next((row for row in rows if row.get('pkgName') == pkg_name and row.get('isInstalled')), None)
            if extension is None: raise HTTPException(404, 'This source extension is not installed')
            annotated = (await run_blocking(source_orphans.annotate_extensions, [extension]))[0]
            if orphaned and not (annotated['can_mark_orphan'] or annotated['is_orphaned']):
                raise HTTPException(409, 'This source is not known to be missing from the configured repositories. Refresh the catalog before marking it as orphaned.')
            await run_blocking(source_orphans.set_label, extension, orphaned)
            source_orphans.accept_inventory(rows)
            return {'extension': (await run_blocking(source_orphans.annotate_extensions, [extension]))[0],
                    'message': 'Source marked as orphaned.' if orphaned else 'Source returned to Installed.'}

    @router.post('/{pkg_name}/mark')
    async def mark(pkg_name: str): return await classify(pkg_name, True)

    @router.post('/{pkg_name}/unmark')
    async def unmark(pkg_name: str): return await classify(pkg_name, False)

    @router.post('/refresh', status_code=202)
    async def refresh():
        await run_blocking(source_orphans.load)
        if source_orphans.task and not source_orphans.task.done(): return {'queued': True, 'refresh': source_orphans.refresh}
        if time.time()-source_orphans.refresh.get('requested_at', 0) < 60:
            raise HTTPException(429, 'Wait one minute between repository history refreshes')
        source_orphans.refresh.update(state='running', requested_at=time.time(), message='Discovery queued')
        source_orphans.task = asyncio.create_task(source_orphans.discover())
        return {'queued': True, 'refresh': source_orphans.refresh}

    @router.post('/{pkg_name}/install', status_code=202)
    async def install(pkg_name: str):
        if settings.source_engine_mode != 'integrated': raise HTTPException(409, 'Integrated engine required')
        await run_blocking(source_orphans.load)
        if pkg_name not in source_orphans.records: raise HTTPException(404, 'Archived source not found; refresh original history first')
        if source_orphans.action and not source_orphans.action.done(): raise HTTPException(409, 'An archive installation is running')
        source_orphans.operation = {'state': 'queued', 'pkgName': pkg_name, 'message': 'Archive installation queued'}
        source_orphans.action = asyncio.create_task(source_orphans.install(pkg_name, lock))
        return {'queued': True}

    @router.post('/{pkg_name}/repair', status_code=202)
    async def repair(pkg_name: str):
        if settings.source_engine_mode != 'integrated': raise HTTPException(409, 'Integrated engine required')
        if not settings.repair_worker_url: raise HTTPException(409, 'Configure the private repair worker in Source maintenance first')
        await run_blocking(source_orphans.load)
        if source_orphans.action and not source_orphans.action.done(): raise HTTPException(409, 'An archive action is running')
        if not PKG.fullmatch(pkg_name): raise HTTPException(400, 'Unsupported original extension package name')
        await ensure_repair_ready()
        if source_orphans.action and not source_orphans.action.done(): raise HTTPException(409, 'An archive action is running')
        source_orphans.operation = {'state': 'queued', 'pkgName': pkg_name, 'message': 'Recovery preparation queued'}
        source_orphans.action = asyncio.create_task(source_orphans.prepare_repair(pkg_name, lock))
        return {'queued': True}

    return router
