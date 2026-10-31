"""User-agent and browser-data controls for the owned integrated source process."""
from __future__ import annotations

import asyncio
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile

import httpx
from .async_utils import run_blocking
from .config import settings

DEFAULT_UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'
PRESETS = [
    {'id': 'default', 'label': 'Engine default (reset)', 'value': ''},
    {'id': 'chrome-windows', 'label': 'Chrome 120 · Windows', 'value': DEFAULT_UA},
    {'id': 'chrome-linux', 'label': 'Chrome 120 · Linux', 'value': DEFAULT_UA.replace('Windows NT 10.0; Win64; x64', 'X11; Linux x86_64')},
    {'id': 'firefox-windows', 'label': 'Firefox 128 · Windows', 'value': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0'},
    {'id': 'browser', 'label': 'Use this browser', 'value': ''},
    {'id': 'custom', 'label': 'Custom user agent', 'value': ''},
]


def read_preferences(config_dir=None):
    path = Path(config_dir or settings.settings_config_dir) / 'source-browser.json'
    if not path.exists():
        return {'preset': 'default', 'user_agent': ''}
    value = json.loads(path.read_text('utf-8'))
    return validate_preferences(value.get('preset', 'default'), value.get('user_agent', ''))


def validate_preferences(preset, value):
    preset = str(preset)
    known = next((p for p in PRESETS if p['id'] == preset), None)
    if known is None:
        raise ValueError('Select a known user-agent preset or Custom')
    ua = str(value).strip() if preset in {'custom', 'browser'} else known['value']
    if preset in {'custom', 'browser'} and not ua:
        raise ValueError('Enter a custom user agent or select Engine default')
    if len(ua) > 1024 or any(ord(char) < 32 or ord(char) > 126 for char in ua):
        raise ValueError('User agent must contain at most 1024 printable ASCII characters, without line breaks')
    # Reject CR/LF before trim too: never normalize a header-injection attempt.
    if preset in {'custom', 'browser'} and any(c in str(value) for c in '\r\n\t'):
        raise ValueError('User agent must not contain line breaks or tabs')
    return {'preset': preset, 'user_agent': ua}


def save_preferences(value, config_dir=None):
    directory = Path(config_dir or settings.settings_config_dir)
    directory.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix='.source-browser-', dir=directory)
    try:
        with os.fdopen(fd, 'w') as stream:
            json.dump(value, stream)
            stream.flush();os.fsync(stream.fileno())
        os.replace(temporary, directory / 'source-browser.json')
    finally:
        Path(temporary).unlink(missing_ok=True)


def user_agent_environment(config_dir, data_dir):
    ua = read_preferences(config_dir)['user_agent']
    if not ua:
        return {}
    return {'JAVA_TOOL_OPTIONS': '-javaagent:/app/source-browser-agent.jar',
            'AUTOSOURCARR_SOURCE_USER_AGENT': ua,
            'AUTOSOURCARR_SOURCE_UA_REVISION': hashlib.sha256(ua.encode()).hexdigest(),
            'AUTOSOURCARR_SOURCE_UA_STATUS': str(Path(data_dir) / 'cache/autosourcarr-user-agent.properties')}


def clear_local_browser_data(data_dir):
    """Called only after the owned JVM has stopped. Never traverse supplied paths."""
    root = Path(data_dir)
    targets = [root / 'settings/cookie_store.xml', root / 'cache/kcef']
    # Validate both before deleting either; protect against links into other data.
    for path in targets:
        for part in (path.parent, path):
            if part.is_symlink():
                raise ValueError('Browser reset refused a symbolic link; check the source data mount')
    cleared = []
    for path in targets:
        if path.is_dir():shutil.rmtree(path)
        else:path.unlink(missing_ok=True)
        cleared.append(path.relative_to(root).as_posix())
    return cleared


class SourceBrowser:
    def __init__(self):
        self.task = None
        self.busy = False
        self.state = 'idle'
        self.error = ''
        self.result = None
        self.stopping = False

    async def snapshot(self):
        from .source_engine import source_engine, ENGINE_DATA
        prefs = await run_blocking(read_preferences)
        applied = {}
        status_file = ENGINE_DATA / 'cache/autosourcarr-user-agent.properties'
        if prefs['user_agent']:
            def status():
                try:
                    raw = status_file.read_text()
                except OSError:return {}
                return dict(line.split('=', 1) for line in raw.splitlines() if '=' in line and not line.startswith('#'))
            applied = await run_blocking(status)
            if applied.get('revision') != hashlib.sha256(prefs['user_agent'].encode()).hexdigest():applied = {}
        return {'preferences': prefs, 'presets': PRESETS, 'default_user_agent': DEFAULT_UA,
                'operation': self.state, 'busy': bool(self.task and not self.task.done()), 'error': self.error,
                'result': self.result, 'runtime': applied, 'engine_state': source_engine.state,
                'integrated': settings.source_engine_mode == 'integrated',
                'automatic_challenges': source_engine.challenges,
                'note': 'Changes restart the source process. Extensions with explicit headers and an external solver can use their own user agent.'}

    def queue(self, action, maintenance_lock, *, preferences=None):
        if settings.source_engine_mode != 'integrated':
            raise ValueError('These controls require the integrated source engine')
        if self.task and not self.task.done():
            raise ValueError('A source browser change is already running')
        if action not in {'user-agent', 'clear-data', 'restart'}:raise ValueError('Unknown source browser action')
        self.state, self.error, self.result = 'queued', '', None
        self.stopping = False
        self.task = asyncio.create_task(self._run(action, maintenance_lock, preferences))

    async def _solver_configuration(self, client):
        data = await client.graphql('query AutoSourcarrSolverSession { settings { flareSolverrUrl flareSolverrSessionName } }')
        return data.get('settings') or {}

    async def _clear_solver_session(self, values):
        # Only this source engine's named session, never other applications' sessions.
        url, session = str(values.get('flareSolverrUrl') or '').rstrip('/'), str(values.get('flareSolverrSessionName') or '')
        if not url or not session:return None
        async with httpx.AsyncClient(timeout=5, trust_env=False) as remote:
            response = await remote.post(url+'/v1', json={'cmd':'sessions.destroy','session':session})
            response.raise_for_status()
            body = response.json()
            if body.get('status') != 'ok':
                # Missing named session means there is already nothing to clear.
                if 'not found' not in str(body.get('message', '')).lower():
                    raise RuntimeError('Configured solver did not confirm session removal')
        return session

    async def _stop_engine(self, engine):
        # Cancellation of the settings action must not interrupt process-group
        # cleanup and leave a JVM writing into a profile we are about to reset.
        task = asyncio.create_task(engine.stop())
        try:
            await asyncio.shield(task)
        except asyncio.CancelledError:
            while not task.done():
                try:
                    await asyncio.shield(task)
                except asyncio.CancelledError:
                    continue
                except Exception:
                    break
            if not task.cancelled():task.exception()
            raise

    async def _assert_runtime_absent(self):
        # child=None is insufficient if startup refused a port already occupied
        # by an unowned JVM. Never clear a profile that process could be using.
        try:
            _, writer = await asyncio.wait_for(asyncio.open_connection('127.0.0.1', 4567), timeout=2)
        except ConnectionRefusedError:
            return
        except (OSError, TimeoutError) as exc:
            raise RuntimeError('Could not confirm the source runtime stopped; browser data was not cleared') from exc
        else:
            writer.close()
            await writer.wait_closed()
            raise RuntimeError('Another process is still listening on source port 4567; browser data was not cleared')

    async def _run(self, action, maintenance_lock, preferences):
        from .source_engine import source_engine, ENGINE_DATA, ENGINE_COMMAND
        from .suwayomi import source_host
        from .source_webview import sessions
        stopped = False
        try:
            async with maintenance_lock:
                if not Path(ENGINE_COMMAND).is_file():raise RuntimeError('Build/redeploy the integrated image before changing its browser')
                if preferences and preferences['user_agent'] and not Path('/app/source-browser-agent.jar').is_file():
                    raise RuntimeError('This image lacks user-agent support; Save/Redeploy version 0.3.2 first')
                warnings, solver_configuration = [], {}
                if action == 'clear-data':
                    try:
                        solver_configuration = await asyncio.wait_for(self._solver_configuration(source_host), timeout=5)
                    except Exception as exc:
                        warnings.append('Could not read configured solver session; external solver data was not cleared: '+type(exc).__name__)
                self.busy, self.state = True, 'restarting'
                sessions.sessions.clear()
                stopped = True
                await self._stop_engine(source_engine)
                if source_engine.child is not None:raise RuntimeError('Source process has not stopped; browser data was not cleared')
                await self._assert_runtime_absent()
                if action == 'user-agent':await run_blocking(save_preferences, preferences)
                elif action == 'clear-data':
                    try:
                        await asyncio.wait_for(self._clear_solver_session(solver_configuration), timeout=7)
                    except Exception as exc:
                        warnings.append('Configured solver session could not be cleared: '+type(exc).__name__)
                    cleared = await run_blocking(clear_local_browser_data, ENGINE_DATA)
                    self.result = {'cleared': cleared, 'warnings': warnings}
                # Agent status belongs to a process, not merely its saved preference.
                await run_blocking((ENGINE_DATA / 'cache/autosourcarr-user-agent.properties').unlink, missing_ok=True)
                source_engine.start();stopped = False
                self.busy = False
                for _ in range(60):
                    if source_engine.state == 'ready':break
                    if source_engine.state == 'setup-required':raise RuntimeError(source_engine.error)
                    await asyncio.sleep(1)
                else:raise RuntimeError('The source process is not ready yet; check Status and download diagnostic logs')
                self.state = 'completed'
        except asyncio.CancelledError:
            self.state = 'cancelled'
            raise
        except Exception as exc:
            self.state, self.error = 'failed', str(exc)
        finally:
            self.busy = False
            if stopped and not self.stopping:source_engine.start()

    async def stop(self):
        self.stopping = True
        if self.task and not self.task.done():
            self.task.cancel();await asyncio.gather(self.task, return_exceptions=True)


source_browser = SourceBrowser()
