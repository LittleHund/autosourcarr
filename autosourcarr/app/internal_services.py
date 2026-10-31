"""Per-instance helper ownership. HTTP handlers only admit work or read snapshots.

Heavy work runs in child processes; no Docker socket, runtime package installation,
shared project mutation or unlimited thread pools. Only sanitized, bounded child
logs enter the authenticated diagnostic export.
"""
from __future__ import annotations
import asyncio
import contextlib
import json
import os
from pathlib import Path
import secrets
import shutil
import sys
import time

import httpx
from fastapi import APIRouter, Depends, HTTPException, Request
from urllib.parse import urlsplit
from fastapi.responses import JSONResponse

from .config import settings
from .async_utils import run_blocking
from .reader_build import live_reader_config, reader_config_message
from .support_logs import support_logs

TOOLS = Path('/opt/autosourcarr-tools')


async def drain_owned(awaitable):
    """A second cancel must not abandon a child spawn or cleanup already owned."""
    task=asyncio.ensure_future(awaitable)
    while not task.done():
        try: await asyncio.shield(task)
        except asyncio.CancelledError: continue
    return await task


class CleanupUncertain(RuntimeError):
    pass


async def stop_group(process, grace=8):
    if process is None:return
    if process.returncode is not None and (process.returncode < 0 or process.returncode == 75):raise CleanupUncertain("Supervisor exited without confirmed cleanup")
    if process.returncode is not None:return
    # The live subreaper owns its entire tree, including detached descendants.
    # Never kill a stale process-group ID after its leader has been reaped.
    with contextlib.suppress(ProcessLookupError): process.terminate()
    try:
        await asyncio.wait_for(process.wait(), grace)
        if process.returncode is not None and (process.returncode < 0 or process.returncode == 75):raise CleanupUncertain("Supervisor exited without confirmed cleanup")
    except asyncio.TimeoutError:
        with contextlib.suppress(ProcessLookupError): process.kill()
        # Process.wait may also wait for a surviving descendant to close stdout.
        # The supervisor's returncode is sufficient here; cleanup is uncertain.
        deadline=time.monotonic()+2
        while process.returncode is None and time.monotonic()<deadline:await asyncio.sleep(.05)
        raise CleanupUncertain('Helper teardown could not be confirmed')


class InternalServices:
    def __init__(self):
        self.tasks = {}
        self.processes = {}
        self.states = {}
        self.stopping = False
        self.cleanup_failed = set()
        self.builder_lock = asyncio.Lock()

    @property
    def enabled(self): return settings.app_services_mode == 'integrated'

    @property
    def root(self): return settings.data_dir/'tools'

    def state(self, name, state, message):
        previous = self.states.get(name)
        self.states[name] = {'state': state, 'message': message, 'updated_at': time.time()}
        if previous is None or previous.get('state') != state or previous.get('message') != message:
            support_logs.record(name, state+': '+message)

    def configure(self):
        if not self.enabled: return
        self.root.mkdir(parents=True, exist_ok=True)
        os.chmod(self.root, 0o700)
        token_file = self.root/'repair-token'
        try:
            fd = os.open(token_file, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        except FileExistsError: pass
        else:
            with os.fdopen(fd, 'w') as stream: stream.write(secrets.token_hex(32))
        token = token_file.read_text().strip()
        if not (32 <= len(token) <= 256) or not all(c in '0123456789abcdef' for c in token):
            raise ValueError('Internal repair credential is invalid; restore tools/repair-token from this instance backup')
        settings.repair_worker_url = 'http://127.0.0.1:8766'
        settings.repair_worker_token = token
        # A derived, private subset: helper jobs never receive the app env file.
        from .setup_store import atomic_write
        repair_root = self.root/'repair'
        repair_root.mkdir(mode=0o700, parents=True, exist_ok=True)
        atomic_write(repair_root/'ai-provider.json', json.dumps({
            'provider': settings.repair_ai_provider, 'protocol': settings.repair_ai_protocol, 'base_url': settings.repair_ai_base_url,
            'model': settings.repair_ai_model, 'api_key': settings.repair_ai_api_key, 'workspace_id': settings.repair_ai_workspace_id,
        }).encode())

    def environment(self, name):
        # Helper processes do not inherit app credentials or arbitrary Python/JVM options.
        env = {key:os.environ[key] for key in ('PATH', 'LANG', 'TZ', 'JAVA_HOME', 'SSL_CERT_FILE', 'SSL_CERT_DIR') if key in os.environ}
        home = self.root/name
        home.mkdir(parents=True, exist_ok=True)
        env.update(HOME=str(home), XDG_CACHE_HOME=str(home/'cache'), XDG_DATA_HOME=str(home/'data'),
                   XDG_CONFIG_HOME=str(home/'config'), PYTHONUNBUFFERED='1',
                   OMP_NUM_THREADS='2', OPENBLAS_NUM_THREADS='2', MKL_NUM_THREADS='2', TOKENIZERS_PARALLELISM='false')
        return env

    def command(self, name, env):
        if name == 'repair':
            env.update(REPAIR_RUNTIME='integrated', REPAIR_ROOT=str(self.root/'repair'),
                       REPAIR_TOKEN=settings.repair_worker_token, PYTHONPATH=str(TOOLS/'repair-worker'))
            return ['/opt/autosourcarr-venv/bin/python', '-m', 'uvicorn', 'server:app', '--host', '127.0.0.1', '--port', '8766', '--no-access-log'], 'http://127.0.0.1:8766/status'
        if name == 'translation':
            return ['/opt/translate-venv/bin/libretranslate', '--host', '127.0.0.1', '--port', '5000', '--load-only', 'en,ja,ko,zh', '--threads', '2', '--disable-web-ui', '--disable-files-translation'], 'http://127.0.0.1:5000/languages'
        if name == 'solver':
            env.update(HOST='127.0.0.1', PORT='8191', LOG_LEVEL='warning', LOG_HTML='false', HEADLESS='true',
                       BROWSER_TIMEOUT='40000', AUTOSOURCARR_SOLVER_DIR=str(self.root/'solver'))
            return [str(TOOLS/'start-solver.sh')], 'http://127.0.0.1:8191/'
        raise ValueError('Unknown internal service')

    def start(self):
        if not self.enabled: return
        self.stopping = False
        # No tools, models, registries or upstream hosts are awaited by lifespan.
        for name in ('repair', 'solver'): self.ensure(name)
        self.tasks['builder-auto']=asyncio.create_task(self._auto_build())

    async def _auto_build(self):
        # Upgrade existing reader installations automatically, after HTTP startup.
        # First setup remains explicit so endpoint/signing choices can be reviewed.
        await asyncio.sleep(30)
        from .extension_download import apk_status, jar_status
        APP_VERSION=(Path(__file__).resolve().parent.parent/"VERSION").read_text().strip()
        directory=settings.settings_config_dir/'extension-downloads'
        if not any((directory/name).is_file() for name in ('current-build.json', 'current.json')): return
        apk,jar=await asyncio.gather(run_blocking(apk_status,settings.settings_config_dir,APP_VERSION),
                                    run_blocking(jar_status,settings.settings_config_dir,APP_VERSION))
        if not apk['available'] or not jar['available']:
            try: self.build()
            except ValueError: pass  # Config/cleanup admission is already visible in Status.

    def ensure(self, name):
        if not self.enabled or self.stopping: return False
        if name in self.cleanup_failed: return False
        current = self.tasks.get(name)
        if current and not current.done(): return self.states.get(name, {}).get('state') == 'ready'
        if name not in {'repair','solver','translation'}: raise ValueError('Unknown internal service')
        self.state(name, 'starting', 'Starting internal '+name)
        self.tasks[name] = asyncio.create_task(self._serve(name), name='internal-'+name)
        return False

    async def _serve(self, name):
        # Bounded restart backoff, never an immediate spawn/retry storm.
        failures = 0
        while not self.stopping and name not in self.cleanup_failed:
            process = None
            drain = None
            try:
                env = await run_blocking(self.environment, name)
                command, url = self.command(name, env)
                process = await self._spawn(name, command, env)
                drain = asyncio.create_task(self._drain(process))
                started = time.monotonic()
                headers = {'Authorization':'Bearer '+settings.repair_worker_token} if name == 'repair' else {}
                async with httpx.AsyncClient(timeout=2, trust_env=False) as client:
                    while process.returncode is None and not self.stopping:
                        try:
                            response = await client.get(url, headers=headers)
                            if response.is_success:
                                self.state(name, 'ready', 'Running inside this AutoSourcarr instance')
                                if name == 'repair':
                                    from .repair_tasks import repair_tasks
                                    try:
                                        result = response.json()
                                        repair_tasks.observe(result)
                                        support_logs.observe_repair(result)
                                    except (ValueError, TypeError):
                                        repair_tasks.unavailable()
                                failures = 0
                            elif self.states.get(name,{}).get('state') == 'ready':
                                if name == 'repair':
                                    from .repair_tasks import repair_tasks
                                    repair_tasks.unavailable()
                                self.state(name, 'starting', 'Internal service is recovering')
                        except httpx.HTTPError:
                            if name == 'repair':
                                from .repair_tasks import repair_tasks
                                repair_tasks.unavailable()
                            if time.monotonic()-started > 15:
                                message = 'Preparing language models; cached titles remain available' if name == 'translation' else 'Waiting for the internal service to become ready'
                                self.state(name, 'starting', message)
                        try: await asyncio.wait_for(process.wait(), timeout=5)
                        except asyncio.TimeoutError: pass
                await self._stop(name, process)
                try: await asyncio.wait_for(asyncio.shield(drain),2)
                except asyncio.TimeoutError:
                    drain.cancel();await asyncio.gather(drain,return_exceptions=True)
                if not self.stopping: raise RuntimeError('Internal process exited')
            except asyncio.CancelledError: raise
            except Exception:
                failures += 1
                if name not in self.cleanup_failed:
                    self.state(name, 'unavailable', 'Internal '+name+' could not start. Check image build and data-directory permissions; retry from Services.')
                    self.states[name]['exit_code']=process.returncode if process else None
                    self.states[name]['stage']='process-start' if process is None else 'process-exit'
            finally:
                async def cleanup():
                    if process is not None: await self._stop(name, process)
                    if drain is not None:
                        try: await asyncio.wait_for(asyncio.shield(drain),2)
                        except asyncio.TimeoutError:
                            drain.cancel();await asyncio.gather(drain,return_exceptions=True)
                    self.processes.pop(name,None)
                await drain_owned(cleanup())
            if not self.stopping and name not in self.cleanup_failed: await asyncio.sleep(min(300, 10*2**min(failures,5)))

    async def _stop(self, name, process):
        if name in self.cleanup_failed:return False
        try:await stop_group(process)
        except CleanupUncertain:
            self.cleanup_failed.add(name)
            self.state(name,'unavailable','Previous process cleanup could not be confirmed. Restart this AutoSourcarr container before using this tool again.')
            self.states[name]['stage']='cleanup'
            return False
        return True

    async def _spawn(self, name, command, env, cwd=None):
        support_logs.privacy.register(env)
        spawn = asyncio.create_task(asyncio.create_subprocess_exec(sys.executable,str(Path(__file__).with_name("child_process.py")),*command, env=env, cwd=cwd,
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.STDOUT, start_new_session=True))
        try: process = await asyncio.shield(spawn)
        except asyncio.CancelledError:
            process = await drain_owned(spawn)
            await drain_owned(self._stop(name, process))
            raise
        self.processes[name] = process
        return process

    async def _drain(self, process):
        name = next((key for key, value in self.processes.items() if value is process), 'helper')
        await support_logs.capture(process.stdout, name)
        return await process.wait()

    def snapshot(self):
        return {'mode': settings.app_services_mode, 'reader_build': live_reader_config(settings) if self.enabled else None, 'services': {
            name: self.states.get(name, {'state':'idle','message':'Starts when needed'})
            for name in ('repair','builder','translation','solver')}}

    def _prepare_workspace(self):
        target = self.root/'extension-workspace'
        source = TOOLS/'build'
        if not source.is_dir(): raise RuntimeError('Integrated builder is missing from the image')
        target.mkdir(parents=True, exist_ok=True)
        # Copy immutable templates only, preserving each instance's checkout/caches/dist.
        for path in source.rglob('*'):
            if path.is_file():
                dest = target/path.relative_to(source)
                dest.parent.mkdir(parents=True, exist_ok=True)
                if path.name == "keiyoushi-commit.lock" and dest.exists(): continue
                shutil.copyfile(path, dest)
                os.chmod(dest, path.stat().st_mode & 0o777)
        return target

    def build(self):
        if not self.enabled: raise ValueError('Internal tools require the integrated image')
        if 'builder' in self.cleanup_failed: raise ValueError('Restart this AutoSourcarr container before another build; previous cleanup was not confirmed')
        task = self.tasks.get('builder')
        if task and not task.done(): return self.snapshot() # coalesce double-clicks
        preflight = live_reader_config(settings)
        if not preflight['ready']:
            message = reader_config_message(preflight)
            self.state('builder', 'blocked', message)
            raise ValueError(message)
        self.state('builder','queued','Reader extension build queued')
        self.tasks['builder'] = asyncio.create_task(self._build(), name='internal-builder')
        return self.snapshot()

    async def _build(self):
        process = None
        try:
            async with self.builder_lock:
                preflight = live_reader_config(settings)
                if not preflight['ready']:
                    self.state('builder', 'blocked', reader_config_message(preflight))
                    return
                target = await run_blocking(self._prepare_workspace)
                env = await run_blocking(self.environment, 'builder')
                env.update(AUTOSOURCARR_BUILD_ROOT=str(target), AUTOSOURCARR_CONFIG_DIR=str(settings.settings_config_dir),
                           AUTOSOURCARR_BUILD_MODE='single', AUTOSOURCARR_PUBLISH_UID=str(os.getuid()),
                           AUTOSOURCARR_PUBLISH_GID=str(os.getgid()), JAVA_HOME='/opt/build-jdk',
                           ANDROID_HOME='/opt/android-sdk-linux', ANDROID_SDK_ROOT='/opt/android-sdk-linux',
                           GRADLE_USER_HOME=str(self.root/'gradle-cache'),
                           PATH='/opt/build-jdk/bin:/opt/android-sdk-linux/cmdline-tools/latest/bin:'+os.environ.get('PATH','/usr/bin:/bin'))
                env.update(AUTOSOURCARR_INTERNAL_URL=settings.autosourcarr_internal_url,
                           AUTOSOURCARR_PUBLIC_URL=settings.autosourcarr_public_url,
                           AUTOSOURCARR_READER_API_TOKEN=settings.autosourcarr_reader_api_token)
                for key in ('AUTOSOURCARR_ALLOW_INSECURE_READER_API','AUTOSOURCARR_ALLOW_UNSAFE_BUILD'):
                    if key in os.environ: env[key]=os.environ[key]
                self.state('builder','running','Building and validating this instance’s JAR, then APK; browsing stays available')
                process = await self._spawn('builder',['nice','-n','5','bash',str(target/'extension/build-in-container.sh'),'--single'],env,cwd=str(target))
                code = await asyncio.wait_for(self._drain(process), 3600)
                if code: raise RuntimeError('Build failed')
                self.state('builder','completed','Verified JAR and APK are ready in Status')
        except asyncio.CancelledError:
            if 'builder' not in self.cleanup_failed:
                self.state('builder','cancelled','Build cancelled; previously published extensions are retained')
            raise
        except Exception:
            if 'builder' not in self.cleanup_failed:
                self.state('builder','failed','Build did not complete. Check reader URLs/token and signing files in Application settings, SDK network access and free disk space. Previous downloads are retained.')
                self.states['builder']['exit_code']=process.returncode if process else None
                self.states['builder']['stage']='preparing' if process is None else 'compiling-and-validating'
        finally:
            async def cleanup():
                if process is not None: await self._stop('builder', process)
                self.processes.pop('builder',None)
            await drain_owned(cleanup())

    async def stop(self):
        self.stopping = True
        tasks = list(self.tasks.values())
        for task in tasks:
            if not task.cancelling(): task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        self.tasks.clear()

    async def action(self, name, action):
        if not self.enabled: raise ValueError('This image is using external compatibility mode')
        if name not in {'repair','builder','translation','solver'}: raise ValueError('Unknown service')
        if action == 'start':
            if name == 'builder': return self.build()
            self.ensure(name)
        elif action == 'cancel' and name == 'builder':
            task = self.tasks.get(name)
            if task and not task.done():
                if not task.cancelling(): task.cancel()
                await asyncio.shield(asyncio.gather(task,return_exceptions=True))
        else: raise ValueError('Unsupported service action')
        return self.snapshot()


internal_services = InternalServices()


def services_router(admin_guard):
    router = APIRouter(prefix='/api/admin/services', dependencies=[Depends(admin_guard)])
    @router.get('')
    async def status(): return JSONResponse(internal_services.snapshot(),headers={'Cache-Control':'no-store'})
    @router.post('/{name}/{action}', status_code=202)
    async def action(name: str, action: str, request: Request):
        origin=request.headers.get('origin')
        hosts={request.headers.get('host',''),request.headers.get('x-forwarded-host','').split(',')[0].strip()}-{''}
        if request.headers.get('sec-fetch-site')=='cross-site' or (origin and urlsplit(origin).netloc not in hosts):
            raise HTTPException(403,'Open service actions from AutoSourcarr itself')
        try: result = await internal_services.action(name,action)
        except ValueError as exc: raise HTTPException(400,str(exc))
        return JSONResponse(result,status_code=202,headers={'Cache-Control':'no-store'})
    return router
