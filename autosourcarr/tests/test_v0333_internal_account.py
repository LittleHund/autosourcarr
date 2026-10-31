"""Account setup is trusted fixed code, independent of source execution."""
import asyncio
import fcntl
import json
import os
import shutil
import subprocess
import uuid
from pathlib import Path
import sys
from unittest.mock import Mock

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'repair-worker'))
import internal_runner
from internal_runner import InternalControl, InternalRunner
from test_v0328_login_backend import auth_bytes, wait_prompt


def test_private_supervisor_receipt_reports_local_exec_failure_and_is_idempotent(tmp_path):
    lifetime=internal_runner.AccountLifetime()
    try:
        process=subprocess.Popen(lifetime.attach([str(tmp_path/'missing-executable')]),
            pass_fds=lifetime.pass_fds,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        lifetime.spawned();lifetime.ready()
        output=process.communicate(timeout=5)[0]
        assert process.returncode==70
        lifetime.wait_eof();assert lifetime.failure['stage']=='exec' and lifetime.failure['errno']==2
        lifetime.wait_eof()  # Credential-finalization reuses confirmed teardown.
        assert b'missing-executable' not in output
    finally:
        lifetime.close()


@pytest.fixture
def native_account(tmp_path, monkeypatch):
    runner = InternalRunner(tmp_path/'repair')
    runner.probe = Mock(side_effect=AssertionError('Account setup must not probe source isolation'))
    runner.sandbox = Mock()
    runner.sandbox.command.side_effect = AssertionError('Account setup must not launch source code')
    executable = tmp_path/'codex.js'
    executable.write_text('''import json,os,sys,time,resource
from pathlib import Path
root=Path(__file__).parent/'repair'
auth=Path(os.environ['CODEX_HOME'])/'auth.json'
(root/'observed.json').write_text(json.dumps({'env':dict(os.environ),'cwd':os.getcwd(),'argv':sys.argv[1:],'files':sorted(p.name for p in auth.parent.iterdir()),'limits':{'files':resource.getrlimit(resource.RLIMIT_NOFILE)[0],'bytes':resource.getrlimit(resource.RLIMIT_FSIZE)[0],'uid_tasks':list(resource.getrlimit(resource.RLIMIT_NPROC))},'no_new_privs':next(x.split(':')[1].strip() for x in Path('/proc/self/status').read_text().splitlines() if x.startswith('NoNewPrivs:'))}))
args=sys.argv[1:]
if 'status' in args:
 print('Logged in using ChatGPT' if auth.exists() else 'Not logged in',flush=True)
 sys.exit(0 if auth.exists() else 1)
if 'logout' in args:
 auth.unlink(missing_ok=True);sys.exit(0)
print('https://auth.openai.com/codex/device\\nEnter this one-time code\\nABCD-EFGH',flush=True)
while not (root/'complete-login').exists():time.sleep(.01)
auth.write_bytes((root/'candidate-auth').read_bytes())
print('Successfully logged in',flush=True)
''')
    monkeypatch.setattr(internal_runner, 'ACCOUNT_COMMAND', (sys.executable,str(executable)))
    # This executor exposes /proc from a different PID namespace (self resolves
    # to a host PID while getpid returns an inner PID). Keep real child/limits/
    # supervision and replace only the unusable sampled /proc accounting seam.
    class FixtureWatchdog:
        def __init__(self, *args, **kwargs): pass
        def check(self): pass
    monkeypatch.setattr(internal_runner, 'Watchdog', FixtureWatchdog)
    (runner.root/'auth/auth.json').write_bytes(auth_bytes('old'))
    (runner.root/'candidate-auth').write_bytes(auth_bytes('new'))
    control = InternalControl(runner)
    yield control, runner
    runner.socket_temp.cleanup()


@pytest.mark.asyncio
async def test_device_login_publishes_code_without_source_namespace_permissions(native_account):
    control, runner = native_account
    try:
        control.start('login')
        await wait_prompt(control)
        assert control.snapshot()['device_code'] == 'ABCD-EFGH'
        assert (runner.root/'auth/auth.json').read_bytes() == auth_bytes('old')
        (runner.root/'complete-login').touch()
        async with asyncio.timeout(5): await control.task
        assert control.snapshot()['state'] == 'completed', control.snapshot()
        assert (runner.root/'auth/auth.json').read_bytes() == auth_bytes('new')
        runner.probe.assert_not_called()
        runner.sandbox.command.assert_not_called()
    finally:
        await control.close()


@pytest.mark.asyncio
async def test_saved_login_uses_private_home_and_clean_environment(native_account, monkeypatch):
    import resource
    inherited_tasks = list(resource.getrlimit(resource.RLIMIT_NPROC))
    control, runner = native_account
    (runner.root/'auth/config.toml').write_text('malicious_mcp_configuration')
    (runner.root/'auth/AGENTS.md').write_text('private source instructions')
    for key in ('NODE_OPTIONS','LD_PRELOAD','OPENAI_API_KEY','HTTP_PROXY','CODEX_HOME','REPAIR_TOKEN'):
        monkeypatch.setenv(key, 'private-credential-or-hook')
    try:
        control.start('login-status')
        async with asyncio.timeout(5): await control.task
        assert control.snapshot()['state'] == 'completed', control.snapshot()
        observed = json.loads((runner.root/'observed.json').read_text())
        assert observed['files'] == ['auth.json']
        assert observed['cwd'] != str(runner.root/'auth')
        assert observed['env']['CODEX_HOME'] != str(runner.root/'auth')
        assert 'private-credential-or-hook' not in json.dumps(observed)
        assert observed['no_new_privs'] == '1'
        assert observed['limits']['files'] <= 1024
        assert observed['limits']['bytes'] <= 512*1024**2
        assert observed['limits']['uid_tasks'] == inherited_tasks
        assert observed['env']['TOKIO_WORKER_THREADS'] == '2'
        assert (runner.root/'auth/config.toml').read_text() == 'malicious_mcp_configuration'
        runner.probe.assert_not_called()
    finally:
        await control.close()


@pytest.mark.asyncio
async def test_sign_out_removes_only_saved_credentials_after_owned_completion(native_account):
    control, runner = native_account
    marker=runner.root/'auth/config.toml';marker.write_text('retain other settings')
    try:
        control.start('logout')
        async with asyncio.timeout(5): await control.task
        assert control.snapshot()['state']=='completed',control.snapshot()
        assert not (runner.root/'auth/auth.json').exists()
        assert marker.read_text()=='retain other settings'
        assert not list(runner.root.glob('account-run-*'))
        runner.probe.assert_not_called()
    finally:
        await control.close()


@pytest.mark.parametrize('invalid',['symlink','fifo','oversize','recursive','broken','api-key','missing-token'])
@pytest.mark.asyncio
async def test_unsafe_saved_auth_is_retained_without_starting_a_command(native_account, invalid):
    control,runner=native_account
    auth=runner.root/'auth/auth.json';auth.unlink()
    if invalid=='symlink': auth.symlink_to(runner.root/'candidate-auth')
    elif invalid=='fifo': os.mkfifo(auth)
    elif invalid=='oversize': auth.write_bytes(b'x'*1048577)
    elif invalid=='recursive': auth.write_bytes(b'['*15000+b'0'+b']'*15000)
    elif invalid=='broken': auth.write_bytes(b'{private-invalid-json')
    elif invalid=='api-key': auth.write_text(json.dumps({'auth_mode':'apikey','OPENAI_API_KEY':'private-key'}))
    else: auth.write_text(json.dumps({'tokens':{'id_token':'only'}}))
    try:
        control.start('login-status')
        async with asyncio.timeout(3): await control.task
        state=control.snapshot()
        assert state['state']=='failed' and not state['blocked'],state
        assert 'could not be read safely' in state['message']
        assert os.path.lexists(auth)
        assert not (runner.root/'observed.json').exists()
        assert 'private-key' not in json.dumps(state)
        assert not list(runner.root.glob('account-run-*'))
    finally:
        await control.close()


@pytest.mark.asyncio
async def test_no_saved_login_reports_signed_out_without_namespace_permissions(native_account):
    control,runner=native_account
    (runner.root/'auth/auth.json').unlink()
    try:
        control.start('login-status')
        async with asyncio.timeout(5): await control.task
        assert control.snapshot()['state']=='completed'
        assert control.snapshot()['account']=='signed-out'
        runner.probe.assert_not_called()
    finally:
        await control.close()


@pytest.mark.asyncio
async def test_cancel_during_native_supervisor_startup_drains_before_unlock(native_account,monkeypatch):
    control,runner=native_account
    original=internal_runner.AccountLifetime.attach
    def delayed(self,args):
        argv=original(self,args)
        return [sys.executable,'-c','import os,sys,time;time.sleep(.1);os.execv(sys.argv[1],sys.argv[1:])',*argv]
    monkeypatch.setattr(internal_runner.AccountLifetime,'attach',delayed)
    try:
        control.start('login')
        await asyncio.sleep(.03)
        await control.cancel_login(control.state['operation_id'])
        assert control.snapshot()['state']=='cancelled' and not control.snapshot()['blocked'],control.snapshot()
        assert (runner.root/'auth/auth.json').read_bytes()==auth_bytes('old')
        assert not list(runner.root.glob('account-login-*'))
        assert not list(runner.root.glob('account-run-*'))
        with (runner.root/'locks/account.lock').open('a') as lock:
            fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
    finally:
        await control.close()


@pytest.mark.asyncio
async def test_missing_supervisor_receipt_cannot_promote_or_unlock(native_account,monkeypatch):
    from account_process import READY
    control,runner=native_account
    def no_receipt(self,args):
        return [sys.executable,'-c',f'import os;os.write({self.write_fd},{READY!r})']
    monkeypatch.setattr(internal_runner.AccountLifetime,'attach',no_receipt)
    try:
        control.start('login')
        async with asyncio.timeout(5): await control.task
        state=control.snapshot()
        assert state['state']=='failed' and state['blocked'],state
        assert 'cleanup could not be confirmed' in state['message']
        assert (runner.root/'auth/auth.json').read_bytes()==auth_bytes('old')
        with (runner.root/'locks/account.lock').open('a') as lock:
            with pytest.raises(BlockingIOError): fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
    finally:
        await control.close()
        if getattr(control,'_retained_lock',None): control._retained_lock.close()


@pytest.mark.asyncio
async def test_arbitrary_command_cannot_use_the_trusted_account_execution_path(native_account):
    control,_=native_account
    with pytest.raises(ValueError,match='fixed bundled account'):
        await control.command(['/bin/sh','-c','echo forbidden'])


@pytest.mark.asyncio
async def test_sign_out_commit_survives_one_failed_unlink_and_cleanup_retry(native_account,monkeypatch):
    control,runner=native_account
    saved=runner.root/'auth/auth.json';original=Path.unlink;attempts=0
    def unlink(path,*args,**kwargs):
        nonlocal attempts
        if path==saved:
            attempts+=1
            if attempts==1: raise OSError('fixture transient disk error')
        return original(path,*args,**kwargs)
    monkeypatch.setattr(Path,'unlink',unlink)
    try:
        control.start('logout')
        async with asyncio.timeout(5): await control.task
        assert control.snapshot()['blocked'] and saved.exists()
        control.start('retry-cleanup')
        async with asyncio.timeout(5): await control.task
        assert control.snapshot()['state']=='completed' and not control.snapshot()['blocked']
        assert not saved.exists() and attempts==2
        assert not list(runner.root.glob('account-run-*'))
    finally:
        await control.close()
        if getattr(control,'_retained_lock',None): control._retained_lock.close()


@pytest.mark.asyncio
async def test_recursive_login_candidate_is_rejected_without_losing_saved_account(native_account):
    control,runner=native_account
    (runner.root/'candidate-auth').write_bytes(b'['*15000+b'0'+b']'*15000)
    try:
        control.start('login')
        await wait_prompt(control)
        (runner.root/'complete-login').touch()
        async with asyncio.timeout(5): await control.task
        state=control.snapshot()
        assert state['state']=='failed' and not state['blocked'],state
        assert 'did not save valid ChatGPT credentials' in state['message']
        assert (runner.root/'auth/auth.json').read_bytes()==auth_bytes('old')
    finally:
        await control.close()


@pytest.mark.asyncio
async def test_supervisor_drains_an_adopted_same_group_child_before_publication(native_account):
    control,runner=native_account
    executable=Path(internal_runner.ACCOUNT_COMMAND[1])
    source=executable.read_text()
    source=source.replace("print('Successfully logged in',flush=True)","""import subprocess
subprocess.Popen([sys.executable,'-c','import signal,time;signal.signal(signal.SIGTERM,signal.SIG_IGN);time.sleep(30)'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
print('Successfully logged in',flush=True)""")
    executable.write_text(source)
    try:
        control.start('login')
        await wait_prompt(control)
        (runner.root/'complete-login').touch()
        async with asyncio.timeout(5): await control.task
        assert control.snapshot()['state']=='completed' and not control.snapshot()['blocked'],control.snapshot()
        assert (runner.root/'auth/auth.json').read_bytes()==auth_bytes('new')
    finally:
        await control.close()


@pytest.mark.asyncio
async def test_repeated_cancel_waits_for_a_spawned_process_to_be_delivered(native_account,monkeypatch):
    control,runner=native_account
    control.state={'action':'login','operation_id':str(uuid.uuid4()),'state':'running'}
    args=await control.account_command('login')
    original=asyncio.create_subprocess_exec
    entered,release=asyncio.Event(),asyncio.Event()
    children=[]
    async def held(*argv,**kwargs):
        process=await original(*argv,**kwargs)
        children.append(process);entered.set()
        await release.wait()
        return process
    monkeypatch.setattr(asyncio,'create_subprocess_exec',held)
    task=asyncio.create_task(control.command(args,timeout=5,login=True))
    try:
        async with asyncio.timeout(3): await entered.wait()
        task.cancel();await asyncio.sleep(.01);task.cancel();await asyncio.sleep(.01)
        assert not task.done(),'A delivered process owner is required before cancellation can complete'
        release.set()
        with pytest.raises(asyncio.CancelledError):
            async with asyncio.timeout(5): await task
        await control._remove_container()
        assert children[0].returncode is not None
        assert (runner.root/'auth/auth.json').read_bytes()==auth_bytes('old')
    finally:
        release.set()
        if not task.done(): task.cancel()
        await asyncio.gather(task,return_exceptions=True)
        await control.close()
        if control._auth_stage and control._auth_stage.exists(): shutil.rmtree(control._auth_stage)
