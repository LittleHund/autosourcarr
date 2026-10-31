"""Bounded first-code requests through the real owned child and app API.

The inherited fixtures replace Docker/bwrap executables and the resource
watchdog. They verify process drainage and credential retention, not host
namespace availability or a live OpenAI account.
"""
import asyncio
import fcntl
import uuid

import pytest

from test_v0329_device_output_bridge import app_bridge, account_control
from test_v0328_login_backend import auth_bytes, wait_prompt
import control as control_module


def hold_prompt(root, before=""):
    executable = next(root.parent.glob('bwrap'), None) or next(root.parent.glob('docker'))
    source = executable.read_text()
    marker = "prompt='\\nWelcome to Codex"
    assert marker in source
    held = before + "\n(root/'login-request-started').touch()\nwhile not (root/'release-code').exists():time.sleep(.005)\n"
    executable.write_text(source.replace(marker, held + marker))


@pytest.mark.asyncio
async def test_stalled_first_code_fails_promptly_drains_and_keeps_saved_login(app_bridge, monkeypatch):
    client, control, root = app_bridge
    hold_prompt(root)
    monkeypatch.setattr(control_module, 'LOGIN_CODE_TIMEOUT', .3, raising=False)
    operation = str(uuid.uuid4())
    try:
        async with client:
            response = await client.post('/api/admin/source-maintenance/control/login', json={'operation_id': operation})
            assert response.status_code == 202
            done, _ = await asyncio.wait({control.task}, timeout=1.2)
            assert done, 'A stalled code request must stop before the 15-minute authorization deadline'
            await control.task
            state = (await client.get('/api/admin/source-maintenance/control')).json()
            assert state['state'] == 'failed' and not state['busy']
            assert 'did not return a sign-in code' in state['message']
            assert state['operation_id'] == operation
            assert not state['device_code'] and not state['blocked']
            assert (root/'auth/auth.json').read_bytes() == auth_bytes('old')
            assert not list(root.glob('account-login-*'))
            with (root/'locks/account.lock').open('a') as lock:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    finally:
        await control.close()


@pytest.mark.asyncio
async def test_received_code_retains_the_full_authorization_wait(account_control, monkeypatch):
    control, root = account_control
    monkeypatch.setattr(control_module, 'LOGIN_CODE_TIMEOUT', .3, raising=False)
    try:
        control.start('login', str(uuid.uuid4()))
        await wait_prompt(control)
        await asyncio.sleep(.4)
        assert control.busy and control.snapshot()['device_code'] == 'ABCD-EFGH'
        assert (root/'auth/auth.json').read_bytes() == auth_bytes('old')
        (root/'complete-login').touch()
        async with asyncio.timeout(3): await control.task
        assert control.snapshot()['state'] == 'completed'
        assert (root/'auth/auth.json').read_bytes() == auth_bytes('new')
    finally:
        await control.close()


@pytest.mark.asyncio
async def test_failure_before_code_keeps_the_service_error(app_bridge, monkeypatch):
    client, control, root = app_bridge
    hold_prompt(root, "print('device code request failed with status 503 Service Unavailable private-secret',flush=True);sys.exit(1)")
    monkeypatch.setattr(control_module, 'LOGIN_CODE_TIMEOUT', .3, raising=False)
    try:
        async with client:
            await client.post('/api/admin/source-maintenance/control/login', json={'operation_id': str(uuid.uuid4())})
            async with asyncio.timeout(3): await control.task
            state = (await client.get('/api/admin/source-maintenance/control')).json()
            assert state['state'] == 'failed'
            assert 'temporarily unavailable' in state['message']
            assert 'private-secret' not in str(state)
            assert (root/'auth/auth.json').read_bytes() == auth_bytes('old')
    finally:
        await control.close()


@pytest.mark.asyncio
async def test_cancel_before_first_code_drains_only_the_owned_login(account_control, monkeypatch):
    control, root = account_control
    hold_prompt(root)
    monkeypatch.setattr(control_module, 'LOGIN_CODE_TIMEOUT', 2, raising=False)
    operation = str(uuid.uuid4())
    try:
        control.start('login', operation)
        async with asyncio.timeout(3):
            while not (root/'login-request-started').exists(): await asyncio.sleep(.005)
        await control.cancel_login(str(uuid.uuid4()))
        assert control.busy
        await control.cancel_login(operation)
        assert control.snapshot()['state'] == 'cancelled'
        assert not control.busy and not control.snapshot()['blocked']
        assert (root/'auth/auth.json').read_bytes() == auth_bytes('old')
        assert not list(root.glob('account-login-*'))
    finally:
        await control.close()


@pytest.mark.asyncio
async def test_new_login_cannot_reuse_previous_code_readiness(account_control, monkeypatch):
    control, root = account_control
    monkeypatch.setattr(control_module, 'LOGIN_CODE_TIMEOUT', .3, raising=False)
    try:
        first = str(uuid.uuid4())
        control.start('login', first)
        await wait_prompt(control)
        await control.cancel_login(first)
        hold_prompt(root)
        second = str(uuid.uuid4())
        control.start('login', second)
        done, _ = await asyncio.wait({control.task}, timeout=1.2)
        assert done, 'An earlier code must not grant the next request a 15-minute wait'
        assert control.snapshot()['state'] == 'failed'
        assert control.snapshot()['operation_id'] == second
        assert 'did not return a sign-in code' in control.snapshot()['message']
        assert (root/'auth/auth.json').read_bytes() == auth_bytes('old')
    finally:
        await control.close()


@pytest.mark.asyncio
async def test_first_code_timeout_holds_account_lock_until_owned_cleanup(account_control, monkeypatch):
    control, root = account_control
    hold_prompt(root)
    monkeypatch.setattr(control_module, 'LOGIN_CODE_TIMEOUT', .3, raising=False)
    entered, release = asyncio.Event(), asyncio.Event()
    original = control._remove_container
    async def held_cleanup():
        entered.set()
        await release.wait()
        await original()
    monkeypatch.setattr(control, '_remove_container', held_cleanup)
    try:
        control.start('login', str(uuid.uuid4()))
        async with asyncio.timeout(3): await entered.wait()
        assert control.busy and control.snapshot()['state'] == 'failed'
        assert not control.snapshot()['device_code']
        assert (root/'auth/auth.json').read_bytes() == auth_bytes('old')
        with (root/'locks/account.lock').open('a') as lock:
            with pytest.raises(BlockingIOError): fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        release.set()
        async with asyncio.timeout(3): await control.task
        assert not control.busy and not control.snapshot()['blocked']
        assert not list(root.glob('account-login-*'))
    finally:
        release.set()
        await control.close()
