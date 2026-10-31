"""Lifecycle of the bundled, loopback-only Mihon-compatible source engine.

The upstream JVM/Chromium runtime is supplied by the pinned container image. No
runtime download/install is performed during application startup.
"""
from __future__ import annotations

import asyncio
import os
from pathlib import Path
import signal
import time

import httpx
from dotenv import dotenv_values

from .config import settings
from .error_registry import error_registry
from .source_challenges import configure_cloudflare, configure_source_captcha, solver_timeout
from .support_logs import support_logs

ENGINE_DATA = Path('/home/suwayomi/.local/share/Tachidesk')
ENGINE_COMMAND = '/home/suwayomi/startup_script.sh'
IMAGE_VERSION = Path('/opt/autosourcarr-image-version')
# Source settings are isolated from AutoSourcarr's credentials and Python process.
SYSTEM_KEYS = ('HOME', 'PATH', 'JAVA_HOME', 'LANG', 'LC_ALL', 'TZ', 'SSL_CERT_FILE', 'SSL_CERT_DIR')
BLOCKED_KEYS = {'HOME', 'PATH', 'JAVA_HOME', 'LD_PRELOAD', 'LD_LIBRARY_PATH',
                'JAVA_TOOL_OPTIONS', '_JAVA_OPTIONS', 'JDK_JAVA_OPTIONS', 'CLASSPATH',
                'PYTHONPATH', 'PYTHONHOME', 'BASH_ENV', 'ENV', 'SHELLOPTS'}


def child_environment(config_dir: Path) -> dict[str, str]:
    env = {key: os.environ[key] for key in SYSTEM_KEYS if key in os.environ}
    path = Path(config_dir) / 'source-host.env'
    if path.exists():
        values = dotenv_values(path, interpolate=False)
        env.update({key: str(value) for key, value in values.items()
                    if value is not None and key not in BLOCKED_KEYS and not key.startswith(('AUTOSOURCARR_', 'PYTHON'))})
    env.update(BIND_IP='127.0.0.1', BIND_PORT='4567', AUTH_MODE='none',
               BASIC_AUTH_ENABLED='false', WEB_UI_ENABLED='false',
               INITIAL_OPEN_IN_BROWSER_ENABLED='false', KCEF_ENABLED='true')
    from .source_browser import user_agent_environment
    env.update(user_agent_environment(config_dir, ENGINE_DATA))
    return env


def child_identity(data: Path = ENGINE_DATA) -> dict:
    """Reuse the source dataset's owner (e.g. TrueNAS 568); never chown its files."""
    data.mkdir(parents=True, exist_ok=True)
    owner = data.stat()
    if os.getuid() != 0:
        return {}
    uid = int(settings.source_engine_uid or owner.st_uid or 1000)
    gid = int(settings.source_engine_gid or owner.st_gid or uid)
    if uid <= 0 or gid <= 0:
        raise RuntimeError('The source WebView requires a non-root source engine UID/GID.')
    if owner.st_uid == 0 and not any(data.iterdir()):
        os.chown(data, uid, gid)  # Only a newly created, empty mount root.
    return {'user': uid, 'group': gid, 'extra_groups': []}


class SourceEngine:
    def __init__(self):
        self.task = None
        self.child = None
        self.state = 'external' if settings.source_engine_mode == 'external' else 'stopped'
        self.error = ''
        self.restarts = 0
        self.challenge_task = None
        self.challenge_env = {}
        self.challenge_retry_at = 0.0
        self.challenge_sources_checked = False
        self.challenge_sources_revision = 0
        self.challenges = {'state': 'waiting', 'sources': [], 'cloudflare': {'state': 'waiting'}}

    def snapshot(self):
        try:
            image_version = IMAGE_VERSION.read_text().strip()
        except OSError:
            image_version = 'unmarked/legacy image'
        return {'mode': settings.source_engine_mode, 'state': self.state,
                'engine': 'Suwayomi v2.3.2243 (Mihon-compatible)',
                'restarts': self.restarts, 'error': self.error, 'automatic_challenges': self.challenges,
                'runtime_available': Path(ENGINE_COMMAND).is_file(), 'image_version': image_version,
                'effective_url': settings.effective_source_url,
                'webview': True, 'separate_container_required': settings.source_engine_mode != 'integrated'}

    def start(self):
        if settings.source_engine_mode == 'integrated' and self.task is None:
            if not Path(ENGINE_COMMAND).is_file():
                self.state = 'setup-required'
                self.error = 'This running container has no integrated source runtime. Rebuild and redeploy the current integrated AutoSourcarr image using the existing deployment. Retain its configuration, databases and source data. The UI and saved catalog remain accessible.'
                self.record_unavailable()
                return
            self.state = 'starting'
            self.task = asyncio.create_task(self._run(), name='integrated-source-engine')

    def record_unavailable(self, path: str = ''):
        return error_registry.record(
            'Integrated source engine', self.error or 'Integrated source engine is starting; retry shortly.',
            key='integrated-source-engine', pinned=True, status_code=503,
            error_type='MissingIntegratedRuntime' if self.state == 'setup-required' else 'SourceEngineUnavailable',
            path=path, detail='Source requests are deferred locally. Cached data remains available. '
            'This shared runtime problem does not indicate a failure of individual source websites.')

    def schedule_challenge_setup(self, *, force=False, refresh_sources=False):
        if refresh_sources:
            self.challenge_sources_revision += 1
            self.challenge_sources_checked = False
        if self.state != 'ready' or settings.source_engine_mode != 'integrated':
            return False
        if self.challenge_task is not None and not self.challenge_task.done():
            return False
        if not settings.source_auto_challenges:
            self.challenges = {'state': 'disabled', 'sources': [], 'cloudflare': {'state': 'manual'}}
            return False
        self.challenge_retry_at = time.monotonic() + 60.0
        self.challenge_task = asyncio.create_task(self._setup_challenges(force=force))
        return True

    def challenge_setup_needs_retry(self):
        if self.challenges.get('state') == 'disabled':
            return False
        return (
            not self.challenge_sources_checked
            or self.challenges.get('state') == 'unavailable'
            or self.challenges.get('cloudflare', {}).get('state') == 'unavailable'
            or any(row.get('state') == 'unavailable' for row in self.challenges.get('sources', []))
        )

    async def _setup_challenges(self, *, force=False):
        from .suwayomi import source_host
        previous_cloudflare = self.challenges.get('cloudflare', {}).get('state')
        previous_sources = self.challenges.get('sources', [])
        self.challenges = {'state': 'configuring', 'sources': previous_sources, 'cloudflare': {'state': 'checking'}}
        try:
            try:
                self.challenges['cloudflare'] = await configure_cloudflare(source_host, self.challenge_env, force=force)
            except Exception as exc:
                self.challenges['cloudflare'] = {'state': 'unavailable', 'message': 'Unable to configure the Cloudflare handler: ' + str(exc)[:250]}
            if force or not self.challenge_sources_checked or any(row.get('state') == 'unavailable' for row in previous_sources):
                revision = self.challenge_sources_revision
                self.challenge_sources_checked = False
                sources = await asyncio.wait_for(source_host.sources(), timeout=6)
                self.challenges['sources'] = await configure_source_captcha(source_host, sources)
                self.challenge_sources_checked = revision == self.challenge_sources_revision
            else:
                # A solver-only retry must not reread every healthy source or
                # repeatedly clear source cooldowns for an older preference edit.
                self.challenges['sources'] = [{**row, 'changed': False} for row in previous_sources]
            from .service import service
            configured_sources = {row['source_id'] for row in self.challenges['sources'] if row.get('state') == 'enabled' and row.get('changed')}
            cloudflare_recovered = previous_cloudflare != 'ready' and self.challenges['cloudflare'].get('state') == 'ready'
            for sid, failure in list(service._source_failures.items()):
                detail = str(failure[2]).casefold()
                if (sid in configured_sources and 'captcha' in detail) or (cloudflare_recovered and 'cloudflare' in detail):
                    service._source_failures.pop(sid, None)
            for key in list(service._page_lookup_failures):
                if key[0] in configured_sources and 'captcha' in service._page_lookup_failures[key][1].casefold():
                    service._page_lookup_failures.pop(key, None)
            self.challenges['state'] = 'partial' if self.challenges['cloudflare'].get('state') == 'unavailable' or any(row.get('state') != 'enabled' for row in self.challenges['sources']) else 'configured'
        except Exception as exc:
            self.challenges['state'] = 'unavailable'
            self.challenges['message'] = str(exc)[:250]
        finally:
            delay = 60.0
            if self.challenge_sources_checked and self.challenges['state'] != 'unavailable' and not any(row.get('state') == 'unavailable' for row in self.challenges['sources']):
                delay = max(delay, min(3600.0, self.challenges['cloudflare'].get('next_probe_at', 0) - time.time()))
            self.challenge_retry_at = time.monotonic() + delay

    async def _terminate(self):
        if self.challenge_task is not None:
            self.challenge_task.cancel()
            await asyncio.gather(self.challenge_task, return_exceptions=True)
            self.challenge_task = None

        child = self.child
        if child is None:
            return
        # startup_script/xvfb-run may have exited while JVM/Xvfb descendants remain.
        try:
            os.killpg(child.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            try:
                os.killpg(child.pid, 0)
            except ProcessLookupError:
                break
            await asyncio.sleep(.1)
        try:
            os.killpg(child.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        await child.wait()
        self.child = None
        task = getattr(self, 'log_task', None)
        self.log_task = None
        if task is not None:
            from .internal_services import drain_owned
            async def finish():
                try:
                    await asyncio.wait_for(asyncio.shield(task), 2)
                except asyncio.TimeoutError:
                    task.cancel()
                except Exception:
                    support_logs.record('source-engine', 'Process log collection could not finish')
                finally:
                    await asyncio.gather(task, return_exceptions=True)
            await drain_owned(finish())

    async def _run(self):
        backoff = 2
        try:
            while True:
                started = time.monotonic()
                try:
                    if not Path(ENGINE_COMMAND).is_file():
                        raise RuntimeError('Bundled source engine missing. Build and use the AutoSourcarr integrated image.')
                    # Refuse an already running loopback engine: two processes must
                    # never share the same H2 data directory.
                    async with httpx.AsyncClient(timeout=1, trust_env=False) as probe:
                        try:
                            await probe.get('http://127.0.0.1:4567/api/v1/settings/about')
                        except httpx.RequestError:
                            pass
                        else:
                            raise RuntimeError('Port 4567 is already in use; refusing to start a second source engine.')
                    identity = await asyncio.to_thread(child_identity)
                    env = await asyncio.to_thread(child_environment, settings.settings_config_dir)
                    if settings.app_services_mode == "integrated":
                        env['FLARESOLVERR_URL'] = "http://127.0.0.1:8191"
                        env.setdefault('FLARESOLVERR_ENABLED', 'true')
                    if not settings.source_auto_challenges:
                        env['FLARESOLVERR_ENABLED'] = 'false'
                    self.challenge_env = dict(env)
                    self.challenge_sources_checked = False
                    if settings.source_auto_challenges and 'FLARESOLVERR_ENABLED' in env:
                        # HTTP starts immediately; enable only after readiness.
                        env['FLARESOLVERR_ENABLED'] = 'false'
                    env['FLARESOLVERR_TIMEOUT'] = str(solver_timeout(env.get('FLARESOLVERR_TIMEOUT', '30')))
                    self.state, self.error = 'starting', ''
                    support_logs.privacy.register(env)
                    self.child = await asyncio.create_subprocess_exec(
                        ENGINE_COMMAND, cwd=str(ENGINE_DATA), env=env,
                        start_new_session=True, stdout=asyncio.subprocess.PIPE,
                        stderr=asyncio.subprocess.STDOUT, **identity)
                    self.log_task = asyncio.create_task(support_logs.capture(self.child.stdout, 'source-engine'),name='source-engine-logs')
                    announced_ready = False
                    async with httpx.AsyncClient(timeout=2, trust_env=False) as client:
                        while self.child.returncode is None:
                            try:
                                response = await client.get('http://127.0.0.1:4567/api/v1/settings/about')
                                if response.is_success:
                                    self.state = 'ready'
                                    error_registry.resolve(key='integrated-source-engine')
                                    if announced_ready and self.challenge_setup_needs_retry() and time.monotonic() >= self.challenge_retry_at:
                                        self.schedule_challenge_setup()
                                    if not announced_ready:
                                        from .service import service
                                        service.schedule_source_reconciliation()
                                        self.schedule_challenge_setup()
                                        announced_ready = True
                                else:
                                    self.state = 'starting' if self.state != 'ready' else 'unresponsive'
                            except httpx.RequestError:
                                if self.state == 'ready':
                                    self.state = 'unresponsive'
                            await asyncio.sleep(3)
                    raise RuntimeError(f'Source engine exited with code {self.child.returncode}. Download diagnostic logs and check source directory permissions.')
                except asyncio.CancelledError:
                    raise
                except Exception as exc:
                    self.error, self.state = str(exc), 'restarting'
                    error_registry.record('Integrated source engine', self.error,
                                          key='integrated-source-engine', pinned=True, detail='The UI remains available. Existing source data is retained.')
                finally:
                    await self._terminate()
                if time.monotonic() - started > 120:
                    backoff = 2
                await asyncio.sleep(backoff)
                self.restarts += 1
                backoff = min(backoff * 2, 60)
        finally:
            await self._terminate()
            self.state = 'stopped'

    async def stop(self):
        if self.task is not None:
            self.task.cancel()
            await asyncio.gather(self.task, return_exceptions=True)
            self.task = None


source_engine = SourceEngine()
