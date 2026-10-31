"""A failed code request must report observed facts, never guess account policy."""
import asyncio
import json
import os
from pathlib import Path
import sys
import threading
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'repair-worker'))
from control import Control, DEVICE_URL, login_failure
from internal_runner import private_login_errors
from test_v0333_internal_account import native_account
from test_v0329_device_output_bridge import app_bridge
from test_v0328_login_backend import account_control, wait_prompt


@pytest.mark.parametrize('output', [
    'Unknown native startup failure',
    'Error logging in with device code: device code request failed with status 400 Bad Request',
    'Error logging in with device code: expected value at line 1 column 1',
])
def test_failed_initial_request_does_not_invent_an_account_settings_cause(output):
    message=login_failure(output)
    assert 'Enable device-code login' not in message
    assert 'ask your workspace admin' not in message


@pytest.mark.parametrize('output,exit_code,reason,http_status', [
    ('Unknown startup problem token=private-secret',1,'unclassified',None),
    ('device code request failed with status 400 Bad Request',1,'http-error',400),
    ('device code request failed with status 401 Unauthorized',1,'request-denied',401),
    ('device code request failed with status 403 Forbidden',1,'request-denied',403),
    ('device code login is not enabled for this Codex server',1,'device-endpoint-unavailable',404),
    ('device code request failed with status 429 Too Many Requests',1,'rate-limited',429),
    ('device code request failed with status 503 Service Unavailable',1,'service-unavailable',503),
    ('error sending request: invalid peer certificate: UnknownIssuer',1,'tls-error',None),
    ('dns error: failed to lookup address information',1,'dns-error',None),
    ('error sending request',1,'network-error',None),
    ('Error logging in with device code: expected value at line 1 column 1',1,'invalid-response',None),
    ('Error logging in with device code: missing field `device_auth_id`',1,'invalid-response',None),
    ('Error: Cannot find module /private/codex.js',1,'tool-startup',None),
    ('FATAL ERROR: Failed to reserve virtual memory for CodeRange',1,'resource-limit',None),
    ('Error loading configuration: /private/file',1,'configuration-error',None),
    ('',-6,'process-signal',None),
])
@pytest.mark.asyncio
async def test_failed_login_exports_only_safe_observed_diagnostics(tmp_path,output,exit_code,reason,http_status):
    root=tmp_path/'repair';(root/'auth').mkdir(parents=True);(root/'locks').mkdir()
    (root/'auth/auth.json').write_bytes(b'previous-private-account')
    class FailedControl(Control):
        async def account_command(self,action): return ['fixed-account-fixture']
        async def command(self,args,**kwargs): return exit_code,output+'\naccess_token=private-secret cookie=private-cookie'
    control=FailedControl(SimpleNamespace(root=root))
    control.start('login')
    async with asyncio.timeout(3): await control.task
    state=control.snapshot()
    assert state['state']=='failed' and not state['busy'] and not state['blocked']
    assert not state['device_code'] and not state['verification_url']
    info=state['diagnostics']
    assert info=={'reason':reason,'phase':'requesting-code','exit_code':exit_code,'http_status':http_status,'code_received':False}
    assert 'private-secret' not in json.dumps(state) and 'private-cookie' not in json.dumps(state)
    assert (root/'auth/auth.json').read_bytes()==b'previous-private-account'
    control.start('login-status');assert not control.snapshot().get('diagnostics')
    await control.close()


@pytest.mark.asyncio
async def test_failure_after_visible_code_reports_authorization_phase(tmp_path):
    root=tmp_path/'repair';(root/'auth').mkdir(parents=True);(root/'locks').mkdir()
    class FailedControl(Control):
        async def account_command(self,action): return ['fixed-account-fixture']
        async def command(self,args,**kwargs):
            self._capture_code(DEVICE_URL+'\nEnter this one-time code\nABCD-EFGH\n')
            return 1,'device auth failed with status 401 Unauthorized'
    control=FailedControl(SimpleNamespace(root=root));control.start('login')
    async with asyncio.timeout(3): await control.task
    state=control.snapshot()
    assert not state['device_code'] and not state['verification_url']
    assert state['diagnostics']['phase']=='authorizing' and state['diagnostics']['code_received'] is True
    assert state['diagnostics']['http_status']==401


def test_secret_numbers_and_unrelated_text_do_not_select_http_or_account_errors():
    message=login_failure('Unknown error access_token=private429secret account_id=403-private')
    assert 'Wait a few minutes' not in message and 'security settings' not in message


@pytest.mark.asyncio
async def test_native_failure_uses_private_log_without_exposing_its_text(native_account):
    import internal_runner
    control,runner=native_account
    Path(internal_runner.ACCOUNT_COMMAND[1]).write_text("""import os,sys
from pathlib import Path
log=Path(os.environ['CODEX_HOME'])/'log';log.mkdir()
(log/'codex-login.log').write_text('error sending request: invalid peer certificate: UnknownIssuer\\naccess_token=private-secret cookie=private-cookie')
print('Unknown native request failure',flush=True)
sys.exit(1)
""")
    previous=(runner.root/'auth/auth.json').read_bytes()
    control.start('login')
    async with asyncio.timeout(5): await control.task
    state=control.snapshot()
    assert state['diagnostics']['reason']=='tls-error' and state['diagnostics']['exit_code']==1
    assert 'private-secret' not in json.dumps(state) and 'private-cookie' not in json.dumps(state)
    assert (runner.root/'auth/auth.json').read_bytes()==previous
    assert not state['blocked'] and not list(runner.root.glob('account-run-*')) and not list(runner.root.glob('account-login-*'))
    runner.probe.assert_not_called()


@pytest.mark.parametrize('unsafe',['file-symlink','folder-symlink','fifo','directory'])
def test_private_login_log_never_follows_special_files(tmp_path,unsafe):
    home=tmp_path/'home';home.mkdir();outside=tmp_path/'outside';outside.mkdir()
    (outside/'codex-login.log').write_text('private-cookie')
    folder=home/'log'
    if unsafe=='folder-symlink': folder.symlink_to(outside,target_is_directory=True)
    else:
        folder.mkdir();path=folder/'codex-login.log'
        if unsafe=='file-symlink': path.symlink_to(outside/'codex-login.log')
        elif unsafe=='fifo': os.mkfifo(path)
        else: path.mkdir()
    assert private_login_errors(home)==''


def test_private_login_log_reads_only_a_bounded_tail(tmp_path):
    home=tmp_path/'home';(home/'log').mkdir(parents=True)
    path=home/'log/codex-login.log'
    with path.open('wb') as stream:
        stream.write(b'private-prefix')
        stream.seek(32*1024*1024)
        stream.write(b'error sending request: invalid peer certificate')
    text=private_login_errors(home)
    assert len(text)<=8192 and 'private-prefix' not in text and 'invalid peer certificate' in text


@pytest.mark.asyncio
async def test_safe_failure_details_cross_authenticated_worker_and_app_routes(app_bridge):
    client,control,root=app_bridge
    try:
        async with client:
            assert (await client.post('/api/admin/source-maintenance/control/login')).status_code==202
            await wait_prompt(control);(root/'fail-login').touch()
            async with asyncio.timeout(5): await control.task
            response=await client.get('/api/admin/source-maintenance/control')
            assert response.status_code==200 and response.headers['cache-control']=='no-store'
            state=response.json()
            assert state['diagnostics']=={'reason':'service-unavailable','phase':'authorizing','exit_code':1,'http_status':503,'code_received':True}
            assert not state['device_code'] and not state['verification_url']
            assert 'DO-NOT-EXPOSE' not in response.text
    finally: await control.close()


@pytest.mark.asyncio
async def test_code_deadline_has_observed_phase_and_no_invented_settings_cause(tmp_path,monkeypatch):
    import control as module
    monkeypatch.setattr(module,'LOGIN_CODE_TIMEOUT',.03)
    root=tmp_path/'repair';(root/'auth').mkdir(parents=True);(root/'locks').mkdir()
    class HeldControl(Control):
        async def account_command(self,action): return ['fixed-fixture']
        async def command(self,args,**kwargs): await asyncio.Event().wait()
    control=HeldControl(SimpleNamespace(root=root));control.start('login')
    async with asyncio.timeout(3): await control.task
    state=control.snapshot()
    assert state['diagnostics']=={'reason':'no-code-timeout','phase':'requesting-code','exit_code':None,'http_status':None,'code_received':False}
    assert 'security settings' not in state['message']


@pytest.mark.asyncio
async def test_local_startup_exception_has_safe_details_without_raw_error(tmp_path):
    root=tmp_path/'repair';(root/'auth').mkdir(parents=True);(root/'locks').mkdir()
    class BrokenControl(Control):
        async def account_command(self,action): raise PermissionError('private-cookie /private/path')
    control=BrokenControl(SimpleNamespace(root=root));control.start('login')
    async with asyncio.timeout(3): await control.task
    state=control.snapshot()
    assert state['diagnostics']=={'reason':'runtime-error','phase':'starting','exit_code':None,'http_status':None,'code_received':False}
    assert 'private-cookie' not in json.dumps(state)


@pytest.mark.asyncio
async def test_cancel_drains_private_log_reader_before_account_unlock(native_account,monkeypatch):
    import fcntl
    import internal_runner
    control,runner=native_account
    Path(internal_runner.ACCOUNT_COMMAND[1]).write_text('import sys\nprint("Unknown native failure",flush=True)\nsys.exit(1)\n')
    entered=threading.Event();release=threading.Event();original=internal_runner.private_login_errors
    def held(home):
        entered.set();assert release.wait(3)
        return original(home)
    monkeypatch.setattr(internal_runner,'private_login_errors',held)
    cancellation=None
    try:
        control.start('login');assert await asyncio.to_thread(entered.wait,3)
        cancellation=asyncio.create_task(control.cancel_login(control.state['operation_id']))
        await asyncio.sleep(.03)
        assert control.busy and not cancellation.done()
        with (runner.root/'locks/account.lock').open('a') as lock:
            with pytest.raises(BlockingIOError): fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
        release.set()
        async with asyncio.timeout(3): await cancellation
        assert control.snapshot()['state']=='cancelled' and not control.snapshot()['blocked']
        assert not list(runner.root.glob('account-run-*')) and not list(runner.root.glob('account-login-*'))
    finally:
        release.set()
        if cancellation: await cancellation
        await control.close()

