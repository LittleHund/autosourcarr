"""Device lifetime at the real owned-child and worker/app API boundaries.

The inherited fixture replaces executable namespace/Docker/resource boundaries;
this checks actual cancellation, output, drain and API paths, not host isolation.
"""
import asyncio
from types import SimpleNamespace
import uuid

import pytest

from test_v0329_device_output_bridge import app_bridge, account_control
import control as control_module


@pytest.mark.asyncio
async def test_worker_remaining_ttl_expires_code_at_both_api_boundaries(app_bridge, monkeypatch):
    client, control, root = app_bridge
    operation = str(uuid.uuid4())
    try:
        async with client:
            started = await client.post('/api/admin/source-maintenance/control/login', json={'operation_id': operation})
            assert started.status_code == 202
            async with asyncio.timeout(3):
                while not control.code: await asyncio.sleep(.005)
            clock = SimpleNamespace(now=1800000000)
            monkeypatch.setattr(control_module, 'time', SimpleNamespace(time=lambda: clock.now))
            control.expires_at = clock.now+45
            fresh = (await client.get('/api/admin/source-maintenance/control')).json()
            assert fresh['device_code'] == 'ABCD-EFGH'
            assert fresh['expires_in'] == 45
            clock.now += 45
            expired = (await client.get('/api/admin/source-maintenance/control')).json()
            assert expired['busy'] and expired['state'] == 'running'
            assert expired['operation_id'] == operation
            assert control.code == 'ABCD-EFGH'  # TTL, rather than accidental buffer loss, hides it.
            assert expired['device_code'] == expired['verification_url'] == ''
            assert expired['expires_in'] == expired['expires_at'] == 0
    finally: await control.close()


@pytest.mark.asyncio
async def test_cancelled_login_hides_code_while_its_owned_child_drains(app_bridge):
    client, control, root = app_bridge
    executable = next(root.parent.glob('bwrap'), None) or next(root.parent.glob('docker'))
    script = executable.read_text()
    marker = "prompt='\\nWelcome to Codex"
    assert marker in script
    held_stop = """import signal
def held_terminate(signum, frame):
 (root/'cancel-received').touch()
 while not (root/'release-cancel').exists():time.sleep(.005)
 sys.exit(143)
signal.signal(signal.SIGTERM, held_terminate)
"""
    executable.write_text(script.replace(marker, held_stop+marker))
    operation = str(uuid.uuid4())
    cancel_task = None
    try:
        async with client:
            started = await client.post('/api/admin/source-maintenance/control/login', json={'operation_id': operation})
            assert started.status_code == 202
            async with asyncio.timeout(3):
                while not control.code: await asyncio.sleep(.005)
            cancel_task = asyncio.create_task(client.post('/api/admin/source-maintenance/control/cancel-login', json={'operation_id': operation}))
            async with asyncio.timeout(3):
                while not (root/'cancel-received').exists(): await asyncio.sleep(.005)
            assert not cancel_task.done()
            draining = (await client.get('/api/admin/source-maintenance/control')).json()
            assert draining['busy'] and draining['state'] == 'cancelled'
            assert draining['device_code'] == draining['verification_url'] == ''
            assert draining['expires_in'] == draining['expires_at'] == 0
            (root/'release-cancel').touch()
            async with asyncio.timeout(3): cancelled = await cancel_task
            assert cancelled.status_code == 202 and cancelled.json()['state'] == 'cancelled'
    finally:
        (root/'release-cancel').touch()
        if cancel_task: await asyncio.gather(cancel_task, return_exceptions=True)
        await control.close()
