"""Bounded per-instance troubleshooting logs, sanitized before memory or disk."""
from __future__ import annotations

import asyncio
from collections import deque
from datetime import datetime, timezone
import hashlib
import hmac
import ipaddress
import json
import logging
import os
from pathlib import Path
import re
import secrets
import stat
from threading import RLock
from urllib.parse import quote, quote_plus, urlsplit
import uuid

from .async_utils import run_blocking
from .diagnostics import APP_VERSION, INSTANCE_ID, exception_context, redact_text
from .repair_evidence import safe_job, safe_isolation, safe_operation

_PRIVATE_KEY = re.compile(r'^(?:authorization|proxy[-_]authorization|(?:set[-_])?cookie|(?:.*[-_])?(?:password|passwd|passphrase|secret|token|api[-_]?key|access[-_]?key|credential|session(?:[-_]?id)?|csrf(?:[-_]?token)?|device[-_]?code|user[-_]?code|user[-_]?name|email|account[-_]?id|workspace[-_]?id|private[-_]?key))$',re.I)
_URL = re.compile(r'\b(?:https?|wss?)://[^\s<>"\']+', re.I)
_EMAIL = re.compile(r'\b[A-Z0-9._%+-]{1,64}@[A-Z0-9.-]{1,253}\.[A-Z]{2,63}\b', re.I)
_IPV4 = re.compile(r'(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])')
_PERSONAL_KEY = re.compile(r'^(?:user|host(?:name)?|ip|client_ip|remote_addr|title|preferred_title|backup_title|source_title|query|search_query)$',re.I)
_ASSIGNMENT = re.compile(r'([A-Za-z_][A-Za-z0-9_-]*)(["\']?\s*[:=]\s*)(?:"[^"\r\n]*"|\'[^\'\r\n]*\'|[^\s,;&{}\[\]]+)')
_CONTROL_REASONS = frozenset(('unclassified','device-login-disabled','device-endpoint-unavailable','rate-limited',
    'request-denied','service-unavailable','http-error','tls-error','dns-error','network-error','invalid-response',
    'tool-startup','resource-limit','configuration-error','timed-out','process-signal','no-code-timeout','runtime-error','credential-save'))
_RESOURCE_LIMIT_FIELDS = frozenset(('nproc_soft','nproc_hard','address_space_soft_bytes','address_space_hard_bytes',
    'open_files_soft','open_files_hard','cgroup_pids_max','cgroup_memory_max_bytes'))
_RESOURCE_CURRENT_FIELDS = frozenset(('cgroup_pids_current','cgroup_memory_current_bytes'))


def _safe_account_resources(value):
    """Independent export allowlist; private worker extras never enter downloads."""
    if type(value) is not dict:
        return None
    safe = {}
    for key in _RESOURCE_LIMIT_FIELDS | _RESOURCE_CURRENT_FIELDS:
        item = value.get(key)
        if type(item) is not str:
            continue
        numeric = re.fullmatch(r'(?:0|[1-9][0-9]{0,19})', item) and int(item) <= (1 << 64) - 1
        if numeric or item == 'unknown' or key in _RESOURCE_LIMIT_FIELDS and item == 'unlimited':
            safe[key] = item
    scope = value.get('cgroup_scope')
    if type(scope) is str and scope in {'unknown','visible-v2-only'}:
        safe['cgroup_scope'] = scope
    ancestor = value.get('ancestor_limits')
    if type(ancestor) is str and ancestor == 'unknown':
        safe['ancestor_limits'] = 'unknown'
    return safe or None


class SupportPrivacy:
    """Keep useful errors/routes while masking secrets and private identities."""
    def __init__(self):
        self._salt = secrets.token_bytes(32)
        self._secrets = set()
        self._secret_bytes = 0
        self._secret_capacity = False
        self._lock = RLock()

    def register(self, values):
        with self._lock:
            for key, value in values.items():
                if not isinstance(value, str) or not value:
                    continue
                candidates = [value] if _PRIVATE_KEY.fullmatch(re.sub(r'([a-z0-9])([A-Z])',r'\1_\2',str(key))) else []
                if '://' in value:
                    try:
                        url = urlsplit(value)
                        candidates += [url.username, url.password]
                    except ValueError:
                        pass
                for candidate in candidates:
                    if candidate:
                        if len(candidate) > 16384:
                            self._secret_capacity = True
                            continue
                        for variant in (candidate, quote(candidate,safe=''),quote_plus(candidate),json.dumps(candidate,ensure_ascii=True)[1:-1]):
                            if variant in self._secrets:
                                continue
                            if self._secret_bytes+len(variant) > 262144:
                                self._secret_capacity = True
                                break
                            self._secrets.add(variant)
                            self._secret_bytes += len(variant)

    def _alias(self, kind, value):
        digest = hmac.new(self._salt, str(value).encode('utf-8',errors='replace'), hashlib.sha256).hexdigest()[:10]
        return '['+kind+'-'+digest+']'

    def text(self, value):
        text = str(value)
        if len(text) > 65536:
            return '[oversize diagnostic omitted]'
        with self._lock:
            if self._secret_capacity:
                return '[diagnostic omitted: secret registry capacity]'
            private = sorted(self._secrets, key=len, reverse=True)
        for secret in private:
            text = text.replace(secret, '[redacted]')
        text = re.sub(r'\x1b\[[0-?]*[ -/]*[@-~]', '', text)
        text = re.sub(r'-----BEGIN [^-]*PRIVATE KEY-----.*?(?:-----END [^-]*PRIVATE KEY-----|$)', '[private key omitted]', text, flags=re.S)
        text = redact_text(text)
        text = re.sub(r'\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+', r'\1 [redacted]', text, flags=re.I)
        text = re.sub(r'\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+', '[redacted]', text)
        text = re.sub(r'\bsk-(?:proj-|ant-)?[A-Za-z0-9_-]{12,}', '[redacted]', text)
        text = re.sub(r'((?:--?(?:storepass|keypass|ks-pass|key-pass|password|passwd|token|api-key|api_key|client-secret|credential))(?:=|\s+))(?:"[^"\r\n]*"|\'[^\'\r\n]*\'|[^\s]+)',r'\1[redacted]',text,flags=re.I)
        text = re.sub(r'\bpass:[^\s,"\']+', 'pass:[redacted]',text,flags=re.I)
        text = re.sub(r'((?:user[ _-]?code|device[ _-]?code|enter (?:the )?code|passphrase|credential|access[ _-]?key|user(?:name)?|email|account[ _-]?id|workspace[ _-]?id)["\']?\s*[:=]\s*)(?:"[^"\r\n]*"|\'[^\'\r\n]*\'|[^\s,;}]+)', r'\1[redacted]', text, flags=re.I)

        def url(match):
            try:
                if re.match(r'\w+://\[host-[0-9a-f]{10}\]',match[0]):
                    return match[0]
                parsed = urlsplit(match[0].replace('[redacted]','redacted'))
                path = parsed.path if parsed.path.startswith(('/api/', '/static/')) or parsed.path in ('/api','/graphql','/status','/health') else ('/[path]' if parsed.path else '')
                return parsed.scheme+'://'+self._alias('host', parsed.hostname or 'invalid')+path+('?'+re.sub(r'[^&=]+(?=&|$)', '[redacted]', parsed.query) if parsed.query else '')
            except ValueError:
                return '[invalid URL omitted]'
        text = _URL.sub(url, text)
        text = re.sub(r'([?&][^\s&=]+)=[^\s&#"\']*', r'\1=[redacted]', text)
        def assignment(match):
            key=re.sub(r'([a-z0-9])([A-Z])',r'\1_\2',match[1])
            return match[1]+match[2]+'[redacted]' if _PRIVATE_KEY.fullmatch(key) or _PERSONAL_KEY.fullmatch(key) else match[0]
        text = _ASSIGNMENT.sub(assignment,text)
        text = _EMAIL.sub(lambda m: self._alias('email', m[0]), text)
        def address(match):
            try:
                return self._alias('ip', ipaddress.ip_address(match[0]))
            except ValueError:
                return match[0]
        text = _IPV4.sub(address, text)
        text = re.sub(r'(?<![\w:])(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:.]*(?![\w:])',address,text)
        text = re.sub(r'[A-Za-z]:\\[^\r\n"<>|]*', lambda m: '[path]/'+m[0].rstrip(' .').rsplit('\\',1)[-1], text)
        text = re.sub(r'(?<![\w:/])/(?:home|Users|mnt|config|data|tmp)/[^\s"\']+', lambda m: '[path]/'+m[0].rstrip('/').rsplit('/',1)[-1], text)
        return text

    def scrub(self, value, depth=0):
        if depth > 12:
            return '[depth limited]'
        if isinstance(value, dict):
            return {str(k): '[redacted]' if _PRIVATE_KEY.search(re.sub(r'([a-z0-9])([A-Z])', r'\1_\2', str(k))) or _PERSONAL_KEY.fullmatch(str(k)) else self.scrub(v,depth+1)
                    for k,v in list(value.items())[:200]}
        if isinstance(value, (list,tuple)):
            return [self.scrub(v,depth+1) for v in value[:200]]
        if isinstance(value,str):
            return self.text(value)
        return value if value is None or isinstance(value,(int,float,bool)) else self.text(value)


class _SupportHandler(logging.Handler):
    def __init__(self, store):
        super().__init__(logging.WARNING)
        self.store = store

    def emit(self, record):
        if record.name == 'uvicorn.access' or getattr(record,'_autosourcarr_support_saved',False):
            return
        try:
            message = record.getMessage()
            if record.exc_info and record.exc_info[1]:
                message += '\n'+json.dumps(exception_context(record.exc_info[1]))
            self.store.record('app', record.levelname+' '+record.name+': '+message)
            record._autosourcarr_support_saved = True
        except Exception:
            # A broken log formatter must never break the operation being logged.
            pass


class SupportLogStore:
    def __init__(self, *, max_file_bytes=512*1024, file_count=3, pending_limit=512):
        self.max_file_bytes = max(8192,int(max_file_bytes))
        self.file_count = max(1,min(5,int(file_count)))
        self.privacy = SupportPrivacy()
        self._pending = deque(maxlen=pending_limit)
        self._memory = deque(maxlen=pending_limit)
        self._lock = RLock()
        self._io_lock = RLock()
        self._root = None
        self._task = None
        self._handler = None
        self._dropped = 0
        self._storage_error = ''
        self._control = None
        self._repair_jobs = {}
        self._repair_status = None
        self._orphan_operation = None

    def observe_repair(self, value):
        if not isinstance(value, dict):
            return
        stamp = datetime.now(timezone.utc).isoformat()
        jobs = value.get('jobs')
        rows = list(reversed(jobs[:50])) if isinstance(jobs, list) else [value]
        changed = []
        for row in rows:
            safe = safe_job(row, self.privacy)
            if safe is None:
                continue
            with self._lock:
                previous = self._repair_jobs.pop(safe['id'], None)
                self._repair_jobs[safe['id']] = safe
                while len(self._repair_jobs) > 50:
                    self._repair_jobs.pop(next(iter(self._repair_jobs)))
            if previous != safe:
                changed.append(safe)
        if isinstance(jobs, list):
            active = value.get('active') if 'active' in value else 'unknown'
            safe = {'captured_at': stamp, 'active': active if isinstance(active, str) and re.fullmatch(r'[0-9a-f]{32}', active) else None if active is None else 'unknown',
                    'jobs_omitted_from_response': max(0, len(jobs) - 50),
                    'isolation': safe_isolation(value.get('isolation'), self.privacy)}
            with self._lock:
                self._repair_status = safe
        for safe in changed:
            self.record('repair-job', json.dumps({key: safe[key] for key in ('id', 'pkg_name', 'state', 'message', 'baseline_issue') if key in safe}))

    def observe_orphan_operation(self, value):
        safe = safe_operation(value, self.privacy)
        if safe is None:
            return
        with self._lock:
            changed = self._orphan_operation != safe
            self._orphan_operation = safe
        if changed:
            self.record('orphan-repair', json.dumps(safe))

    def observe_control(self, value):
        if not isinstance(value,dict):
            return
        self.privacy.register(value)
        isolation = safe_isolation(value.get('isolation'), self.privacy)
        if isolation is not None:
            with self._lock:
                if self._repair_status is None:
                    self._repair_status = {'captured_at': None, 'active': 'unknown', 'jobs_omitted_from_response': 0}
                self._repair_status.update(isolation=isolation, isolation_captured_at=datetime.now(timezone.utc).isoformat())
        def choice(item,allowed,default='unknown'):
            return item if isinstance(item,str) and item in allowed else default
        safe = {'state': choice(value.get('state'),{'idle','running','ready','failed','completed','cancelled','cancelling'}),
                'action': choice(value.get('action'),{'prepare','login','login-status','logout','cancel-login','retry-cleanup'}),
                'busy':value.get('busy') is True}
        diagnostics = value.get('diagnostics')
        if isinstance(diagnostics,dict):
            safe['diagnostics'] = {'reason':choice(diagnostics.get('reason'),_CONTROL_REASONS,'unclassified'),
                'phase':choice(diagnostics.get('phase'),{'starting','requesting-code','authorizing','checking-account','signing-out','saving-account'})}
            for key in ('exit_code','http_status'):
                item=diagnostics.get(key)
                if item is None or type(item) is int and -255 <= item <= 599:
                    safe['diagnostics'][key]=item
            if type(diagnostics.get('code_received')) is bool:
                safe['diagnostics']['code_received']=diagnostics['code_received']
            stage=diagnostics.get('launcher_stage')
            if isinstance(stage,str) and stage in {'exec','parent'}:
                safe['diagnostics']['launcher_stage']=stage
            number=diagnostics.get('os_error')
            if type(number) is int and 0 <= number <= 4095:
                safe['diagnostics']['os_error']=number
            resources = _safe_account_resources(diagnostics.get('resources'))
            if resources is not None:
                safe['diagnostics']['resources'] = resources
        with self._lock:
            changed = safe != self._control
            self._control = safe
        if changed:
            self.record('repair-account',json.dumps(safe))

    def configure(self, data_dir):
        self._root = Path(data_dir)/'diagnostics'

    def record(self, source, message):
        text = self.privacy.text(message).encode('utf-8',errors='replace')[:4096].decode('utf-8',errors='ignore')
        row = {'id':uuid.uuid4().hex[:12], 'time':datetime.now(timezone.utc).isoformat(),
               'app_version':APP_VERSION,'instance_id':INSTANCE_ID,
               'source':str(source)[:80].encode('utf-8',errors='replace').decode('utf-8'), 'message':text}
        encoded = json.dumps(row,ensure_ascii=False)+'\n'
        with self._lock:
            if len(self._pending) == self._pending.maxlen:
                self._dropped += 1
            self._pending.append(encoded)
            self._memory.append(encoded)

    def _paths(self):
        return [self._root/('support.jsonl'+('.'+str(i) if i else '')) for i in range(self.file_count)]

    @staticmethod
    def _regular(path):
        try:
            observed = path.lstat()
        except FileNotFoundError:
            return
        if not stat.S_ISREG(observed.st_mode) or observed.st_nlink != 1:
            raise OSError('Unsafe diagnostic log path')
        return observed

    def _write(self, lines):
        if self._root.is_symlink() or getattr(self._root,'is_junction',lambda:False)():
            raise OSError('Unsafe diagnostic directory')
        self._root.mkdir(mode=0o700, parents=True, exist_ok=True)
        if self._root.is_symlink() or getattr(self._root,'is_junction',lambda:False)():
            raise OSError('Unsafe diagnostic directory')
        os.chmod(self._root,0o700)
        paths = self._paths()
        for path in paths:
            self._regular(path)
        for line in lines:
            raw = line.encode('utf-8')
            if len(raw) > self.max_file_bytes:
                row = json.loads(line)
                row['message'] = '[oversize diagnostic omitted]'
                raw = (json.dumps(row)+'\n').encode('utf-8')
            if paths[0].exists() and paths[0].stat().st_size+len(raw) > self.max_file_bytes:
                for i in range(len(paths)-1,0,-1):
                    if paths[i-1].exists():
                        os.replace(paths[i-1], paths[i])
                if len(paths) == 1:
                    paths[0].unlink()
            before = self._regular(paths[0])
            flags = os.O_WRONLY|os.O_APPEND|os.O_CREAT|getattr(os,'O_NOFOLLOW',0)
            if before is None:
                flags |= os.O_EXCL
            fd = os.open(paths[0],flags,0o600)
            with os.fdopen(fd,'ab') as stream:
                actual = os.fstat(stream.fileno())
                if not stat.S_ISREG(actual.st_mode) or actual.st_nlink != 1 or before is not None and (before.st_dev,before.st_ino) != (actual.st_dev,actual.st_ino):
                    raise OSError('Unsafe diagnostic log file')
                stream.write(raw)

    def flush(self):
        if self._root is None:
            return
        with self._io_lock:
            with self._lock:
                lines = list(self._pending)
                self._pending.clear()
            if not lines:
                return
            try:
                self._write(lines)
                self._storage_error = ''
            except OSError as exc:
                # The in-memory tail remains downloadable when the disk is unavailable.
                self._storage_error = type(exc).__name__

    def snapshot(self):
        with self._io_lock:
            self.flush()
            lines = []
            if self._root is not None:
                try:
                    if self._root.is_symlink() or getattr(self._root,'is_junction',lambda:False)():
                        raise OSError('Unsafe diagnostic directory')
                    for path in reversed(self._paths()):
                        before = self._regular(path)
                        if before is not None:
                            fd = os.open(path,os.O_RDONLY|getattr(os,'O_NOFOLLOW',0)|getattr(os,'O_NONBLOCK',0))
                            with os.fdopen(fd,'rb') as stream:
                                actual = os.fstat(stream.fileno())
                                if not stat.S_ISREG(actual.st_mode) or actual.st_nlink != 1 or (before.st_dev,before.st_ino) != (actual.st_dev,actual.st_ino):
                                    raise OSError('Unsafe diagnostic log file')
                                lines += stream.read(self.max_file_bytes).decode('utf-8',errors='replace').splitlines()
                except OSError as exc:
                    self._storage_error = type(exc).__name__
            with self._lock:
                lines += list(self._memory)
            rows = {}
            for line in lines:
                try:
                    row = json.loads(line)
                    if isinstance(row,dict) and isinstance(row.get('id'),str):
                        rows[row['id']] = self.privacy.scrub({k:row[k] for k in ('id','time','app_version','instance_id','source','message') if k in row})
                except (ValueError,TypeError):
                    continue
            with self._lock:
                repairs = json.loads(json.dumps({'status': self._repair_status, 'jobs': list(reversed(self._repair_jobs.values())),
                                                 'orphan_operation': self._orphan_operation, 'job_limit': 50,
                                                 'scope': 'Cached observed helper responses and local preparation only; no export-time probes. Private prompts, patches, conversations and package bodies are excluded.'}))
            return {'entries':list(rows.values()), 'storage_error':self._storage_error,
                    'source_repairs': self.privacy.scrub(repairs),
                    'repair_account':self.privacy.scrub(self._control),
                    'retention':{'bounded':True,'max_disk_bytes':self.max_file_bytes*self.file_count,
                                 'files':self.file_count,'dropped_records':self._dropped,
                                 'memory_records':self._memory.maxlen,'record_utf8_bytes':4096,'process_line_bytes':65536,
                                 'persistent':self._root is not None and not self._storage_error},
                    'scope':'Retained app errors/warnings and supervised process output. Older rotated logs and host/proxy/other-container logs are unavailable.'}

    async def capture(self, stream, source):
        pending = bytearray()
        omitted = private_block = False
        marker_tail = b''
        private_end = False
        while chunk := await stream.read(16384):
            for part in chunk.splitlines(keepends=True):
                probe = marker_tail+part
                if not private_block and re.search(rb'-----BEGIN [^-\r\n]*PRIVATE KEY-----',probe):
                    private_block = True
                    self.record(source,'[private key omitted]')
                if private_block and re.search(rb'-----END [^-\r\n]*PRIVATE KEY-----',probe):
                    private_end = True
                marker_tail = probe[-80:]
                ended = part.endswith((b'\n',b'\r'))
                if not omitted:
                    if len(pending)+len(part) > 65536:
                        pending.clear()
                        omitted = True
                    else:
                        pending.extend(part)
                if ended:
                    if omitted:
                        self.record(source,'[oversize process line omitted]')
                    else:
                        text = pending.decode('utf-8',errors='replace').rstrip('\r\n')
                        if not private_block:
                            self.record(source,text)
                    if private_end:
                        private_block = False
                    pending.clear()
                    omitted = False
                    marker_tail = b''
                    private_end = False
            await asyncio.sleep(0)
        if pending and not private_block and not omitted:
            self.record(source,pending.decode('utf-8',errors='replace'))

    def start(self, data_dir, values):
        self.configure(data_dir)
        self.privacy.register(values)
        if self._handler is None:
            self._handler = _SupportHandler(self)
            logging.getLogger().addHandler(self._handler)
            logging.getLogger('uvicorn.error').addHandler(self._handler)
        if self._task is None or self._task.done():
            self._task = asyncio.create_task(self._run(),name='support-log-writer')
        self.record('app','AutoSourcarr startup')

    async def _run(self):
        try:
            while True:
                await asyncio.sleep(1)
                with self._lock:
                    pending = bool(self._pending)
                if pending:
                    await run_blocking(self.flush)
        finally:
            await run_blocking(self.flush)

    async def stop(self):
        if self._handler is not None:
            logging.getLogger().removeHandler(self._handler)
            logging.getLogger('uvicorn.error').removeHandler(self._handler)
            self._handler = None
        if self._task is not None:
            self.record('app','AutoSourcarr shutdown')
            self._task.cancel()
            await asyncio.gather(self._task,return_exceptions=True)
            self._task = None
        await run_blocking(self.flush)


support_logs = SupportLogStore()
