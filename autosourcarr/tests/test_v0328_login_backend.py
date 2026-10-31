"""Independent device-login contract fixtures; no real login or namespaces.

Run from a project with AUTOSOURCARR_TEST_PROJECT=/absolute/project.  The owned
subprocess models the pinned CLI's clear-before-request behavior and prompt.  The
integrated branch uses the production trusted supervisor with a controlled CLI,
and does not claim Docker, actual Codex or source namespace verification.
"""
import asyncio
import fcntl
import json
import os
from pathlib import Path
import stat
import sys
import threading
import time
from types import SimpleNamespace
import uuid

import pytest

PROJECT = Path(os.environ.get('AUTOSOURCARR_TEST_PROJECT', Path(__file__).resolve().parents[2]))
sys.path.insert(0, str(PROJECT/'repair-worker'))
from control import Control, DEVICE_URL, device_code
from control import login_failure
from internal_runner import InternalControl
import control as control_module
import internal_runner as internal_module


def auth_bytes(label):
    return json.dumps({'auth_mode': 'chatgpt', 'OPENAI_API_KEY': None,
                      'tokens': {'id_token': label+'-id-private',
                                 'access_token': label+'-access-private',
                                 'refresh_token': label+'-refresh-private',
                                 'account_id': 'fixture'},
                      'last_refresh': '2026-10-06T00:00:00Z'}).encode()


FAKE_CLI = r'''
import json, os, sys, time
from pathlib import Path
args=sys.argv[1:]
if args[:1]==['--sync-fd']:
 sync_fd=int(args[1]);args=args[2:]
if args[:1]==['--auth']:
 auth=Path(args[1]);root=Path(args[2]);action=args[3]
elif os.environ.get('CODEX_HOME') and args[:1]==['-c']:
 auth=Path(os.environ['CODEX_HOME']);root=Path(__file__).parent/'repair'
 action='login-status' if 'status' in args else 'logout' if 'logout' in args else 'login'
else:
 root=Path(os.environ['LOGIN_FIXTURE_ROOT'])
 if args[:2]==['image','inspect']:sys.exit(0)
 if args[:1]==['rm']:sys.exit(0)
 if args[:1]==['ps']:sys.exit(0)
 if args[:1]!=['run']:sys.exit(9)
 mount=args[args.index('--mount')+1]
 auth=Path(next(part[4:] for part in mount.split(',') if part.startswith('src=')))
 action='login-status' if 'status' in args else 'logout' if 'logout' in args else 'login'
(root/'bound-auth-path').write_text(str(auth))
if action=='login-status':
 print('Logged in using ChatGPT' if (auth/'auth.json').exists() else 'Not logged in',flush=True)
 sys.exit(0 if (auth/'auth.json').exists() else 1)
if action=='logout':
 (auth/'auth.json').unlink(missing_ok=True);sys.exit(0)
# Pinned Codex 0.157.0 calls logout_with_revoke before requesting the code.
if (auth/'auth.json').exists():
 (root/'old-credential-was-revoked').touch()
 (auth/'auth.json').unlink()
prompt='\nWelcome to Codex [v\x1b[90m0.157.0\x1b[0m]\n\n1. Open this link in your browser and sign in to your account\n   \x1b[94mhttps://auth.openai.com/codex/device\x1b[0m\n\n2. Enter this one-time code \x1b[90m(expires in 15 minutes)\x1b[0m\n   \x1b[94mABCD-EFGH\x1b[0m\n'
for at in range(0,len(prompt),17):
 os.write(1,prompt[at:at+17].encode());time.sleep(.001)
while not (root/'complete-login').exists() and not (root/'fail-login').exists():time.sleep(.005)
if (root/'fail-login').exists():
 print('Error logging in with device code: device code request failed with status 503 Service Unavailable\naccess_token=DO-NOT-EXPOSE cookie=DO-NOT-EXPOSE',flush=True);sys.exit(1)
case=(root/'candidate-case').read_text() if (root/'candidate-case').exists() else 'valid'
if case=='invalid':(auth/'auth.json').write_text('{bad-json private-secret')
elif case=='missing':pass
elif case=='oversize':(auth/'auth.json').write_bytes(b'private-secret'+b'x'*1048576)
elif case=='symlink':(auth/'auth.json').symlink_to(root/'outside-secret')
else:(auth/'auth.json').write_bytes((root/'candidate-auth').read_bytes())
print('Successfully logged in',flush=True)
'''


@pytest.fixture(params=['legacy', 'integrated'])
def account_control(request, tmp_path, monkeypatch):
    root = tmp_path/'repair'
    for name in ('auth', 'locks', 'jobs', 'sockets'):
        (root/name).mkdir(parents=True, exist_ok=True)
    (root/'auth/auth.json').write_bytes(auth_bytes('old'))
    (root/'auth/auth.json').chmod(0o600)
    (root/'auth').chmod(0o700)
    (root/'candidate-auth').write_bytes(auth_bytes('new'))
    (root/'outside-secret').write_bytes(auth_bytes('outside'))
    executable = tmp_path/('docker' if request.param=='legacy' else 'bwrap')
    executable.write_text('#!'+sys.executable+'\n'+FAKE_CLI)
    executable.chmod(0o755)
    class BindingSandbox:
        def command(self, work, command, **kwargs):
            action = 'login-status' if 'status' in command else 'logout' if 'logout' in command else 'login'
            return [str(executable), '--auth', str(kwargs['auth']), str(root), action]
    runner = SimpleNamespace(root=root, namespace='fixture', host=lambda path: str(path),
                             cleanup_failed=False, sockets=root/'sockets',
                             probe=lambda: {'available': True, 'message': 'fixture'},
                             sandbox=BindingSandbox(),
                             base_args=lambda name, **kwargs: ['docker', 'run', '--name', name])
    if request.param=='legacy':
        monkeypatch.setenv('PATH', str(tmp_path)+os.pathsep+os.environ['PATH'])
        monkeypatch.setenv('LOGIN_FIXTURE_ROOT', str(root))
    else:
        # This fixture has no namespace /proc; resource-budget tests exercise
        # the real watchdog. Keep process/output/SyncPipe/drain paths real here.
        class FixtureWatchdog:
            def __init__(self, *args, **kwargs): pass
            def check(self): pass
        monkeypatch.setattr(internal_module, 'Watchdog', FixtureWatchdog)
        monkeypatch.setattr(internal_module, 'ACCOUNT_COMMAND', (sys.executable,str(executable)))
    control = InternalControl(runner) if request.param=='integrated' else Control(runner)
    return control, root


async def wait_prompt(control):
    async with asyncio.timeout(3):
        while not control.snapshot()['device_code']:
            if control.task.done():
                raise AssertionError(control.snapshot())
            await asyncio.sleep(.005)


def assert_account_locked(root):
    with (root/'locks/account.lock').open('a') as lock:
        with pytest.raises(BlockingIOError):
            fcntl.flock(lock, fcntl.LOCK_EX|fcntl.LOCK_NB)


@pytest.mark.asyncio
async def test_pinned_clear_before_request_cannot_touch_previous_login(account_control):
    control, root = account_control
    previous = (root/'auth/auth.json').read_bytes()
    operation = str(uuid.uuid4())
    control.start('login', operation)
    try:
        await wait_prompt(control)
        assert control.snapshot()['verification_url']==DEVICE_URL
        assert control.snapshot()['device_code']=='ABCD-EFGH'
        assert (root/'auth/auth.json').read_bytes()==previous
        assert not (root/'old-credential-was-revoked').exists()
        bound = Path((root/'bound-auth-path').read_text())
        assert bound != root/'auth' and bound.is_relative_to(root)
        assert not (bound/'auth.json').exists()
        assert stat.S_IMODE(bound.stat().st_mode)==0o700
        assert_account_locked(root)
        await control.cancel_login(str(uuid.uuid4()))
        assert control.busy
        await control.cancel_login(operation)
        assert control.snapshot()['state']=='cancelled'
        assert (root/'auth/auth.json').read_bytes()==previous
        assert not control.snapshot()['device_code']
    finally:
        await control.close()


@pytest.mark.asyncio
async def test_success_waits_for_owned_cleanup_before_atomic_promotion(account_control, monkeypatch):
    control, root = account_control
    previous = (root/'auth/auth.json').read_bytes()
    entered, release = asyncio.Event(), asyncio.Event()
    original = control._remove_container
    async def held_cleanup():
        entered.set()
        await release.wait()
        await original()
    monkeypatch.setattr(control, '_remove_container', held_cleanup)
    control.start('login', str(uuid.uuid4()))
    try:
        await wait_prompt(control)
        (root/'complete-login').touch()
        async with asyncio.timeout(3): await entered.wait()
        assert control.busy
        assert (root/'auth/auth.json').read_bytes()==previous
        assert_account_locked(root)
        release.set()
        async with asyncio.timeout(3): await control.task
        assert control.snapshot()['state']=='completed'
        assert (root/'auth/auth.json').read_bytes()==auth_bytes('new')
        assert stat.S_IMODE((root/'auth/auth.json').stat().st_mode)==0o600
        assert 'new-access-private' not in json.dumps(control.snapshot())
    finally:
        release.set()
        await control.close()


@pytest.mark.asyncio
async def test_nonzero_exit_retains_old_credentials_and_public_error(account_control):
    control, root = account_control
    previous = (root/'auth/auth.json').read_bytes()
    control.start('login', str(uuid.uuid4()))
    try:
        await wait_prompt(control)
        (root/'fail-login').touch()
        async with asyncio.timeout(3): await control.task
        state = control.snapshot()
        assert state['state']=='failed' and not state['device_code']
        assert (root/'auth/auth.json').read_bytes()==previous
        assert 'DO-NOT-EXPOSE' not in json.dumps(state)
        assert state['message']
    finally: await control.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('case', ['missing', 'invalid', 'oversize', 'symlink'])
async def test_exit_zero_without_safe_candidate_retains_old_credentials(account_control, case):
    control, root = account_control
    previous = (root/'auth/auth.json').read_bytes()
    (root/'candidate-case').write_text(case)
    control.start('login', str(uuid.uuid4()))
    try:
        await wait_prompt(control)
        (root/'complete-login').touch()
        async with asyncio.timeout(3): await control.task
        state = control.snapshot()
        assert state['state']=='failed' and not state['device_code']
        assert (root/'auth/auth.json').read_bytes()==previous
        assert (root/'outside-secret').read_bytes()==auth_bytes('outside')
        assert 'private-secret' not in json.dumps(state)
        assert 'outside-access-private' not in json.dumps(state)
    finally: await control.close()


@pytest.mark.asyncio
async def test_cleanup_failure_never_promotes_or_unlocks_candidate(account_control, monkeypatch):
    control, root = account_control
    previous = (root/'auth/auth.json').read_bytes()
    original = control._remove_container
    async def uncertain_cleanup():
        await original()
        raise RuntimeError('controlled uncertain cleanup')
    monkeypatch.setattr(control, '_remove_container', uncertain_cleanup)
    control.start('login', str(uuid.uuid4()))
    try:
        await wait_prompt(control)
        (root/'complete-login').touch()
        async with asyncio.timeout(3): await control.task
        assert control.snapshot()['state']=='failed' and control.snapshot()['blocked']
        assert (root/'auth/auth.json').read_bytes()==previous
        assert_account_locked(root)
        with pytest.raises(RuntimeError): control.start('login', str(uuid.uuid4()))
    finally:
        monkeypatch.setattr(control, '_remove_container', original)
        control.start('retry-cleanup', str(uuid.uuid4()))
        async with asyncio.timeout(3): await control.task
        assert not control.snapshot()['blocked']
        assert (root/'auth/auth.json').read_bytes()==previous
        await control.close()


@pytest.mark.asyncio
async def test_owned_cancel_during_cleanup_discards_uncommitted_login(account_control, monkeypatch):
    control, root = account_control
    previous = (root/'auth/auth.json').read_bytes()
    entered, release = asyncio.Event(), asyncio.Event()
    original = control._remove_container
    async def held_cleanup():
        entered.set()
        await release.wait()
        await original()
    monkeypatch.setattr(control, '_remove_container', held_cleanup)
    operation = str(uuid.uuid4())
    control.start('login', operation)
    cancelled = None
    try:
        await wait_prompt(control)
        (root/'complete-login').touch()
        async with asyncio.timeout(3): await entered.wait()
        cancelled = asyncio.create_task(control.cancel_login(operation))
        await asyncio.sleep(.01)
        assert not cancelled.done()
        assert_account_locked(root)
        release.set()
        async with asyncio.timeout(3): await cancelled
        assert control.snapshot()['state']=='cancelled'
        assert (root/'auth/auth.json').read_bytes()==previous
        assert not control.snapshot()['device_code']
    finally:
        release.set()
        if cancelled: await asyncio.gather(cancelled, return_exceptions=True)
        await control.close()


@pytest.mark.asyncio
async def test_auth_publication_failure_before_commit_keeps_original(account_control, monkeypatch):
    control, root = account_control
    previous = (root/'auth/auth.json').read_bytes()
    replace = control_module.os.replace
    def fail_replace(source, target, *args, **kwargs):
        if Path(target)==root/'auth/auth.json': raise OSError('private failure detail')
        return replace(source, target, *args, **kwargs)
    monkeypatch.setattr(control_module.os, 'replace', fail_replace)
    control.start('login', str(uuid.uuid4()))
    try:
        await wait_prompt(control)
        (root/'complete-login').touch()
        async with asyncio.timeout(3): await control.task
        assert control.snapshot()['state']=='failed'
        assert (root/'auth/auth.json').read_bytes()==previous
        assert 'private failure detail' not in json.dumps(control.snapshot())
    finally: await control.close()


@pytest.mark.asyncio
async def test_post_commit_directory_sync_failure_reports_saved_login(account_control, monkeypatch):
    control, root = account_control
    sync = control_module.os.fsync
    def fail_after_commit(fd):
        if stat.S_ISDIR(os.fstat(fd).st_mode) and (root/'auth/auth.json').read_bytes()==auth_bytes('new'):
            raise OSError('private disk detail')
        return sync(fd)
    monkeypatch.setattr(control_module.os, 'fsync', fail_after_commit)
    control.start('login', str(uuid.uuid4()))
    try:
        await wait_prompt(control)
        (root/'complete-login').touch()
        async with asyncio.timeout(3): await control.task
        assert control.snapshot()['state']=='completed'
        assert control.snapshot()['account']=='saved-login'
        assert (root/'auth/auth.json').read_bytes()==auth_bytes('new')
        assert 'private disk detail' not in json.dumps(control.snapshot())
        assert 'sync' in control.snapshot()['message'].lower()
    finally: await control.close()


@pytest.mark.asyncio
async def test_cancel_after_commit_during_stage_cleanup_keeps_completed_result(account_control, monkeypatch):
    control, root = account_control
    entered, release = threading.Event(), threading.Event()
    original = control_module.shutil.rmtree
    def held_stage_cleanup(path, *args, **kwargs):
        if Path(path).name.startswith('account-login-'):
            entered.set()
            if not release.wait(5): raise RuntimeError('fixture cleanup deadline')
        return original(path, *args, **kwargs)
    monkeypatch.setattr(control_module.shutil, 'rmtree', held_stage_cleanup)
    operation = str(uuid.uuid4())
    cancelled = None
    control.start('login', operation)
    try:
        await wait_prompt(control)
        (root/'complete-login').touch()
        async with asyncio.timeout(3):
            while not entered.is_set(): await asyncio.sleep(.005)
        assert control.busy and control.snapshot()['state']=='completed'
        assert (root/'auth/auth.json').read_bytes()==auth_bytes('new')
        cancelled = asyncio.create_task(control.cancel_login(operation))
        await asyncio.sleep(.01)
        assert_account_locked(root)
        release.set()
        async with asyncio.timeout(3): result = await cancelled
        assert result['state']=='completed'
        assert 'previous' not in result['message'].lower()
    finally:
        release.set()
        if cancelled: await asyncio.gather(cancelled, return_exceptions=True)
        async with asyncio.timeout(3): await control.task
        await control.close()


def test_pinned_prompt_export_does_not_require_a_tty():
    prompt = '\n1. Open this link in your browser and sign in to your account\n   \x1b[94m'+DEVICE_URL+'\x1b[0m\n\n2. Enter this one-time code \x1b[90m(expires in 15 minutes)\x1b[0m\n   \x1b[94mABCD-EFGH\x1b[0m\n'
    assert device_code(prompt)=='ABCD-EFGH'
    assert device_code(prompt.replace(DEVICE_URL, DEVICE_URL+'?private=secret'))==''
    assert device_code(prompt.replace(DEVICE_URL, 'http://auth.openai.com/codex/device'))==''


@pytest.mark.parametrize('output,advice', [
    ('device code login is not enabled for this Codex server', 'endpoint'),
    ('ChatGPT login is disabled.', 'workspace administrator'),
    ('device code request failed with status 429 Too Many Requests', 'Wait a few minutes'),
    ('device auth failed with status 503 Service Unavailable', 'temporarily unavailable'),
    ('device auth timed out after 15 minutes', 'new code'),
    ('Error loading configuration:', 'configuration'),
    ('error sending request', 'Internet access'),
])
def test_public_login_failures_are_actionable_and_redact_output(output, advice):
    message = login_failure(output + '\naccess_token=private-secret cookie=private-cookie')
    assert advice in message
    assert 'private-secret' not in message and 'private-cookie' not in message


@pytest.mark.asyncio
async def test_internal_command_deadline_stops_a_stalled_fixed_account_child(tmp_path, monkeypatch):
    # Real supervisor/output/receipt ownership; controlled executable and /proc
    # watchdog because this executor exposes mismatched PID namespace views.
    root = tmp_path/'repair'
    work = root/'account-work';work.mkdir(parents=True)
    class FixtureWatchdog:
        def __init__(self, *args, **kwargs): pass
        def check(self): pass
    monkeypatch.setattr(internal_module, 'Watchdog', FixtureWatchdog)
    control = InternalControl(SimpleNamespace(root=root))
    args=[sys.executable,'-c','import time;time.sleep(30)']
    control._account_expected=tuple(args)
    control._account_action='login'
    control._account_run_dir=work
    control._account_env={'PATH':'/usr/bin:/bin','HOME':str(work),'CODEX_HOME':str(work)}
    started = time.monotonic()
    try:
        with pytest.raises(asyncio.TimeoutError):
            await control.command(args, timeout=.3)
        assert time.monotonic()-started < 1.0
    finally:
        await control._remove_container()
