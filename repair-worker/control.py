"""Owned, bounded worker preparation and official device login. No raw output API."""
import asyncio
import fcntl
import json
import os
import re
import shutil
import stat
import tempfile
import time
import uuid
from pathlib import Path

from runner import CODEX_IMAGE, BUILD_IMAGE, ENGINE_IMAGE

DEVICE_URL = 'https://auth.openai.com/codex/device'
LOGIN_CODE_TIMEOUT = 45
from account_failures import ANSI, AccountFailure, account_failure


def device_code(output):
    clean = ANSI.sub('', output).replace('\r', '')
    # Expose only the official URL and the isolated code following its prompt.
    if not re.search(r'(?m)^\s*'+re.escape(DEVICE_URL)+r'\s*$', clean):
        return ''
    # A pipe chunk can stop midway through the code. Only publish a complete
    # prompt line, never a syntactically valid prefix of the real code.
    match = re.search(r'Enter this one-time code[^\n]*\n[ \t]*([A-Z0-9]{4,8}-[A-Z0-9]{4,8})[ \t]*\n', clean)
    return match[1] if match else ''


def login_failure(output):
    return str(account_failure(output))


class Control:
    def __init__(self, runner):
        self.runner = runner
        self.task = None
        self.state = {'action': '', 'state': 'idle', 'message': 'Choose Prepare repair tools to check this worker.'}
        self.account = 'unchecked'
        self.images_ready = False
        self.code = ''
        self._code_ready = asyncio.Event()
        self.expires_at = 0
        self.container_name = None
        self._auth_stage = None
        self._commit_login = False
        self._login_committing = False
        self._code_received = False

    def account_auth_path(self, action):
        if action != 'login': return self.runner.root/'auth'
        # Codex clears/revokes credentials before login. An empty, separate home
        # keeps a cancelled/failed attempt from touching the saved account.
        stage = self.runner.root/('account-login-'+self.state['operation_id'])
        stage.mkdir(mode=0o700)
        self._auth_stage = stage
        owner = (self.runner.root/'auth').stat()
        if (stage.stat().st_uid, stage.stat().st_gid) != (owner.st_uid, owner.st_gid):
            os.chown(stage, owner.st_uid, owner.st_gid)
        return stage

    def _promote_login(self):
        path = self._auth_stage/'auth.json'
        try:
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
            with os.fdopen(fd, 'rb') as stream:
                info = os.fstat(stream.fileno())
                if not stat.S_ISREG(info.st_mode) or not 0 < info.st_size <= 1048576: raise ValueError()
                data = stream.read(1048577)
            value = json.loads(data)
            tokens = value.get('tokens') if isinstance(value, dict) else None
            if not isinstance(tokens, dict) or value.get('auth_mode', 'chatgpt') != 'chatgpt' or value.get('OPENAI_API_KEY'):
                raise ValueError()
            if not all(isinstance(tokens.get(key), str) and tokens[key].strip() for key in ('id_token', 'access_token', 'refresh_token')):
                raise ValueError()
        except (OSError, ValueError, TypeError, RecursionError):
            raise RuntimeError('OpenAI sign-in did not save valid ChatGPT credentials. Your previous saved login is retained. Try again.') from None
        target = self.runner.root/'auth'
        temporary = None
        try:
            fd, temporary = tempfile.mkstemp(prefix='.login-', dir=target)
            with os.fdopen(fd, 'wb') as stream:
                os.fchmod(stream.fileno(), 0o600)
                owner = target.stat()
                if (os.fstat(stream.fileno()).st_uid, os.fstat(stream.fileno()).st_gid) != (owner.st_uid, owner.st_gid):
                    os.fchown(stream.fileno(), owner.st_uid, owner.st_gid)
                stream.write(data); stream.flush(); os.fsync(stream.fileno())
            os.replace(temporary, target/'auth.json')
            temporary = None
        except OSError:
            raise RuntimeError('The new login could not be saved. Your previous saved login is retained. Check this instance’s data directory and disk space, then retry.') from None
        finally:
            if temporary: Path(temporary).unlink(missing_ok=True)
        try:
            directory = os.open(target, os.O_RDONLY | os.O_DIRECTORY)
            try: os.fsync(directory)
            finally: os.close(directory)
        except OSError:
            return 'Signed in and saved. The directory sync could not finish; check disk health before restarting this instance.'
        return 'Signed in and saved. Check repair tools separately before running source code.'

    @property
    def busy(self):
        return bool(self.task and not self.task.done())

    def snapshot(self):
        remaining = max(0, self.expires_at-time.time())
        active_code = self.code if (self.busy and self.state['action'] == 'login'
                                    and self.state['state'] == 'running' and remaining > 0) else ''
        return {**self.state, 'busy': self.busy, 'images_ready': self.images_ready,
                'isolation': getattr(self.runner,'isolation',None),
                'account': self.account, 'auth_saved': (self.runner.root/'auth/auth.json').is_file(),
                'blocked': bool(getattr(self, '_retained_lock', None)), 'device_code': active_code, 'verification_url': DEVICE_URL if active_code else '',
                'expires_at': self.expires_at if active_code else 0,
                'expires_in': remaining if active_code else 0}

    def start(self, action, operation_id=None):
        operation_id=str(uuid.UUID(operation_id)) if operation_id else str(uuid.uuid4())
        if action not in {'prepare', 'login', 'login-status', 'logout', 'retry-cleanup'}:
            raise ValueError('Unknown worker action')
        if operation_id==self.state.get('operation_id') and action==self.state.get('action'): return self.snapshot()
        if self.busy:
            raise RuntimeError('A worker setup or account action is already running')
        handle = getattr(self, '_retained_lock', None)
        if handle:
            if action != 'retry-cleanup':
                raise RuntimeError('Select Retry account cleanup before another account action')
        else:
            if action == 'retry-cleanup': raise ValueError('No account cleanup is pending')
            handle = (self.runner.root/'locks/account.lock').open('a')
            try:
                fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                handle.close()
                raise RuntimeError('The shared account is in use; wait for its current action')
        self.code = ''; self.expires_at = 0
        self._code_ready.clear()
        self._commit_login = False
        self._code_received = False
        self._cancel_requested = False
        self._started = False
        self.state = {'action': action, 'operation_id':operation_id, 'state': 'running', 'message': 'Starting '+action.replace('-', ' ')}
        self.task = asyncio.create_task(self._run(action, handle))
        self.task.add_done_callback(lambda _: handle.close() if not self._started and getattr(self, '_retained_lock', None) is not handle else None)
        return self.snapshot()

    async def command(self, args, *, timeout=60, login=False):
        spawn = asyncio.create_task(asyncio.create_subprocess_exec(*args, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.STDOUT, start_new_session=True))
        try:
            process = await asyncio.shield(spawn)
        except asyncio.CancelledError:
            process = await spawn
            if process.returncode is None:
                process.kill()
                await process.wait()
            raise
        output = bytearray()
        async def read():
            while chunk := await process.stdout.read(4096):
                output.extend(chunk)
                if login:
                    # Retain the captured code until this owned login terminates;
                    # later diagnostics may rotate the bounded output buffer.
                    self._capture_code(output.decode(errors='replace'))
                if len(output) > 65536:
                    del output[:-32768]
            return await process.wait()
        try:
            code = await asyncio.wait_for(read(), timeout)
            return code, output.decode(errors='replace')
        finally:
            if process.returncode is None:
                process.terminate()
                try: await asyncio.wait_for(process.wait(), 5)
                except asyncio.TimeoutError:
                    process.kill(); await process.wait()

    async def _remove_container(self):
        if self.container_name:
            code, _ = await self.command(['docker', 'rm', '-f', self.container_name], timeout=30)
            if code:
                # Confirm absence before releasing the account lock.
                listed, rows = await self.command(['docker', 'ps', '-a', '--format', '{{.Names}}'], timeout=10)
                if listed or self.container_name in rows.splitlines(): raise RuntimeError('Account container could not be confirmed stopped')
            self.container_name = None

    async def prepare_tools(self):
        self.images_ready = False
        for image, label in ((CODEX_IMAGE, 'Codex'), (BUILD_IMAGE, 'Android build tools'), (ENGINE_IMAGE, 'source test engine')):
            self.state['message'] = 'Checking '+label
            code, _ = await self.command(['docker', 'image', 'inspect', image])
            if code:
                self.state['message'] = 'Preparing '+label+'; the first download can take several minutes'
                args = ['docker', 'build', '-t', image, '/worker/toolchain'] if image == CODEX_IMAGE else ['docker', 'pull', image]
                code, _ = await self.command(args, timeout=1800)
                if code: raise RuntimeError('Could not prepare '+label+'. Check Docker registry access and disk space, then retry Prepare repair tools.')

    def _capture_code(self, output):
        if not self.code:
            self.code = device_code(output)
        if self.code:
            self._code_received = True
            self._code_ready.set()
            self.state['message'] = 'Open the official sign-in page and enter the code. Waiting for you to finish.'

    async def login_command(self, args):
        """Bound code generation separately from the user's authorization wait."""
        command = asyncio.create_task(self.command(args, timeout=900, login=True))
        ready = asyncio.create_task(self._code_ready.wait())
        try:
            done, _ = await asyncio.wait({command, ready}, timeout=LOGIN_CODE_TIMEOUT,
                                         return_when=asyncio.FIRST_COMPLETED)
            if not done:
                raise AccountFailure('OpenAI did not return a sign-in code in time. Check this instance’s Internet access and retry. Your previous saved login is retained.', 'no-code-timeout')
            return await command
        finally:
            ready.cancel()
            if not command.done(): command.cancel()
            drained = asyncio.gather(command, ready, return_exceptions=True)
            while not drained.done():
                try: await asyncio.shield(drained)
                except asyncio.CancelledError: continue
            await drained

    async def account_command(self, action):
        code, _ = await self.command(['docker', 'image', 'inspect', CODEX_IMAGE])
        if code: raise RuntimeError('Codex tools are missing. Select Prepare repair tools first.')
        self.container_name = 'asr-'+self.runner.namespace+'-web-account'
        args = self.runner.base_args(self.container_name, memory='512m') + [
            '--log-driver', 'none', '--user', '1000:1000', '--mount',
            'type=bind,src='+self.runner.host(self.account_auth_path(action))+',dst=/home/node/.codex',
            CODEX_IMAGE, '-c', 'cli_auth_credentials_store="file"']
        args += ['login', '--device-auth'] if action == 'login' else ['login', 'status'] if action == 'login-status' else ['logout']
        return args

    async def failure_output(self, output):
        return output

    async def _run(self, action, handle):
        self._started = True
        try:
            if action == 'retry-cleanup':
                self.state.update(state='completed', message='Account cleanup completed. Worker actions are available again.')
                return
            if action == 'prepare':
                await self.prepare_tools()
                self.images_ready = True
                self.state.update(state='completed', message='Repair tools are ready. Connect your account in setup or check the saved AI connection.')
                return
            args = await self.account_command(action)
            if action == 'login':
                self.expires_at = time.time()+900
                self.state['message'] = 'Requesting a one-time code from OpenAI. Keep this page open; the sign-in page opens from your browser’s Sign in button.'
            code, output = await self.login_command(args) if action == 'login' else await self.command(args, timeout=60)
            if action == 'login-status':
                if code == 0:
                    self.account = 'saved-login'
                    self.state.update(state='completed', message='Codex recognizes a saved login. Usage and account availability are checked when a repair runs.')
                elif 'not logged in' in output.lower():
                    self.account = 'signed-out'
                    self.state.update(state='completed', message='Not signed in. Select Sign in with ChatGPT.')
                else:
                    raise account_failure(await self.failure_output(output), code, action=action,launcher_failure=getattr(self,'_launcher_failure',None))
            elif code:
                if action == 'login':
                    raise account_failure(await self.failure_output(output), code, code_received=self._code_received,launcher_failure=getattr(self,'_launcher_failure',None))
                raise account_failure(await self.failure_output(output), code, action=action,launcher_failure=getattr(self,'_launcher_failure',None))
            else:
                if action == 'login':
                    self._commit_login = True
                    self.state['message'] = 'Finishing sign-in and saving the account…'
                else:
                    self.account = 'signed-out'
                    self.state.update(state='completed', message='Signed out of this repair account. Existing repairs and source backups are retained.')
        except asyncio.CancelledError:
            self.state.update(state='cancelled', message='Account action cancelled. Any previously saved login is retained.')
        except asyncio.TimeoutError:
            self.state.update(state='failed', message='OpenAI sign-in timed out. Select Sign in with ChatGPT for a new code. Your previous saved login is retained.' if action == 'login' else 'This worker action timed out. Start it again when ready.')
            if action in {'login','login-status','logout'}:
                self.state['diagnostics'] = AccountFailure('', 'timed-out', phase='authorizing' if self._code_received else 'requesting-code' if action=='login' else 'checking-account' if action=='login-status' else 'signing-out', code_received=self._code_received).diagnostics
        except AccountFailure as exc:
            self.state.update(state='failed', message=str(exc), diagnostics=exc.diagnostics)
        except Exception as exc:
            # Only our own messages are public, never raw subprocess output/errors.
            message = str(exc) if isinstance(exc, RuntimeError) else self.runtime_failure_message()
            self.state.update(state='failed', message=message)
            if action in {'login','login-status','logout'}:
                self.state['diagnostics'] = AccountFailure('', 'runtime-error', phase='authorizing' if self._code_received else 'starting', code_received=self._code_received).diagnostics
        finally:
            self.code = ''; self.expires_at = 0
            cleanup = asyncio.create_task(self._finish(handle))
            while not cleanup.done():
                try: await asyncio.shield(cleanup)
                except asyncio.CancelledError: continue
            await cleanup

    async def _finish(self, handle):
        try:
            await self._remove_container()
        except Exception:
            self._commit_login = False
            self.state.update(state='failed', message=self.cleanup_failure_message())
            # Fail closed: retain the lock until the controller exits.
            self._retained_lock = handle
        else:
            try:
                if self._commit_login:
                    try:
                        self._login_committing = True
                        message = await asyncio.to_thread(self._promote_login)
                        self.account = 'saved-login'
                        self.state.update(state='completed', message=message)
                    except RuntimeError as exc:
                        self.state.update(state='failed', message=str(exc))
                        self.state['diagnostics'] = AccountFailure('', 'credential-save', phase='saving-account', code_received=self._code_received).diagnostics
                    finally: self._login_committing = False
                if self._auth_stage:
                    await asyncio.to_thread(shutil.rmtree, self._auth_stage)
                    self._auth_stage = None
            except Exception:
                self.state.update(state='failed', message=('The new login was saved, but account cleanup needs attention. ' if self.account == 'saved-login' and self.state['state'] == 'completed' else 'Account cleanup needs attention. ')+'Select Retry account cleanup before another sign-in.')
                self._retained_lock = handle
            else:
                handle.close()
                if getattr(self, '_retained_lock', None) is handle: del self._retained_lock
            finally: self._commit_login = False

    def cleanup_failure_message(self):
        return 'Account cleanup needs attention. Select Retry account cleanup when Docker is available again.'

    def runtime_failure_message(self):
        return 'Worker action could not run. Check its Docker access and retry.'

    async def _cancel(self):
        if self.busy:
            if not self._cancel_requested:
                self._cancel_requested = True
                self.task.cancel()
            try:
                await asyncio.shield(self.task)
            except asyncio.CancelledError:
                if not self.task.cancelled(): raise
                self.state.update(state='cancelled', message='Account action cancelled. Any previously saved login is retained.')

    async def cancel_login(self, operation_id=None):
        if operation_id is not None and operation_id!=self.state.get('operation_id'): return self.snapshot()
        if self.busy and self.state['action'] == 'login':
            if self._login_committing or self.state['state'] == 'completed': return self.snapshot()
            self._commit_login = False
            self.state.update(state='cancelled', message='Account action cancelled. Any previously saved login is retained.')
            await self._cancel()
        return self.snapshot()

    async def close(self):
        await self._cancel()
