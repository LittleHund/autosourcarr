"""Actual worker/app routes with owned executable fixtures, never real login.

The existing pinned-login fixture supplies real Control/InternalControl process
and SyncPipe paths. Its Docker/bwrap executable and resource watchdog are mock
boundaries; this does not establish native namespace or real Codex coverage.
"""
import asyncio
import os
from pathlib import Path
import sys
import uuid

import httpx
from fastapi import FastAPI, HTTPException, Request
import pytest

PROJECT = Path(os.environ.get('AUTOSOURCARR_TEST_PROJECT', Path(__file__).resolve().parents[2]))
sys.path.insert(0, str(PROJECT/'repair-worker'))
sys.path.insert(0, str(PROJECT/'autosourcarr/tests'))
from test_v0328_login_backend import account_control, wait_prompt, auth_bytes
from control import DEVICE_URL, device_code
import server as worker_server
from app import source_maintenance as maintenance


@pytest.fixture
def app_bridge(account_control, monkeypatch):
    control, root = account_control
    monkeypatch.setattr(worker_server, 'control', control)
    monkeypatch.setattr(worker_server, 'runner', control.runner)
    monkeypatch.setattr(worker_server, 'pending', set())
    monkeypatch.setenv('REPAIR_TOKEN', 'x'*64)
    monkeypatch.setattr(maintenance.settings, 'repair_worker_url', 'http://worker-fixture')
    monkeypatch.setattr(maintenance.settings, 'repair_worker_token', 'x'*64)
    native_client = httpx.AsyncClient
    def worker_client(**kwargs):
        return native_client(transport=httpx.ASGITransport(app=worker_server.app), **kwargs)
    monkeypatch.setattr(maintenance.httpx, 'AsyncClient', worker_client)
    async def fixture_admin_guard(request: Request):
        if request.headers.get('X-Admin-Token') != 'fixture-admin':
            raise HTTPException(401, 'fixture admin required')
    app = FastAPI()
    app.include_router(maintenance.maintenance_router(fixture_admin_guard, asyncio.Lock()))
    client = native_client(transport=httpx.ASGITransport(app=app), base_url='http://app-fixture',
                           headers={'X-Admin-Token': 'fixture-admin', 'Origin': 'http://app-fixture'})
    return client, control, root


@pytest.mark.asyncio
async def test_pinned_prompt_survives_owned_child_worker_and_app_api(app_bridge):
    client, control, root = app_bridge
    operation = str(uuid.uuid4())
    try:
        async with client:
            started = await client.post('/api/admin/source-maintenance/control/login', json={'operation_id': operation})
            assert started.status_code==202, started.text
            await wait_prompt(control)
            raw_worker = await maintenance.worker('GET', '/control')
            response = await client.get('/api/admin/source-maintenance/control')
            assert response.status_code==200, response.text
            exposed = response.json()
            assert response.headers['cache-control']=='no-store'
            for key in ('device_code', 'verification_url', 'operation_id', 'expires_at', 'busy', 'state'):
                assert exposed[key]==raw_worker[key]
            assert exposed['device_code']=='ABCD-EFGH'
            assert exposed['verification_url']==DEVICE_URL
            assert exposed['operation_id']==operation and exposed['busy']
            cancelled = await client.post('/api/admin/source-maintenance/control/cancel-login', json={'operation_id': operation})
            assert cancelled.status_code==202 and cancelled.json()['state']=='cancelled'
            assert not cancelled.json()['device_code']
            assert (root/'auth/auth.json').read_bytes()==auth_bytes('old')
    finally: await control.close()


@pytest.mark.asyncio
async def test_later_bounded_output_cannot_erase_captured_device_code(app_bridge):
    client, control, root = app_bridge
    # Add harmless late output to the executable fixture while it still waits for
    # the user's authorization. The prompt and its code are unchanged.
    executable = next(root.parent.glob('bwrap'), None) or next(root.parent.glob('docker'))
    script = executable.read_text()
    original = "while not (root/'complete-login').exists() and not (root/'fail-login').exists():time.sleep(.005)"
    replacement = """while not (root/'complete-login').exists() and not (root/'fail-login').exists():
 if (root/'emit-later-output').exists() and not (root/'later-output-written').exists():
  os.write(1,b'harmless diagnostic\\n'*4096)
  (root/'later-output-written').touch()
 time.sleep(.005)"""
    assert original in script
    executable.write_text(script.replace(original, replacement))
    operation = str(uuid.uuid4())
    try:
        async with client:
            started = await client.post('/api/admin/source-maintenance/control/login', json={'operation_id': operation})
            assert started.status_code==202, started.text
            await wait_prompt(control)
            initial = (await client.get('/api/admin/source-maintenance/control')).json()
            assert initial['device_code']=='ABCD-EFGH'
            (root/'emit-later-output').touch()
            async with asyncio.timeout(3):
                while not (root/'later-output-written').exists(): await asyncio.sleep(.005)
            await asyncio.sleep(.05)
            refreshed = (await client.get('/api/admin/source-maintenance/control')).json()
            assert refreshed['busy'] and refreshed['operation_id']==operation
            assert refreshed['device_code']=='ABCD-EFGH'
            assert refreshed['verification_url']==DEVICE_URL
    finally: await control.close()


def test_stream_prefix_is_not_a_complete_device_code():
    # The pinned CLI test uses CODE-12345. A pipe read can stop after its first
    # four digits; that prefix must not be published as another valid code.
    prefix = DEVICE_URL+'\n2. Enter this one-time code (expires in 15 minutes)\n CODE-1234'
    assert device_code(prefix)==''
    assert device_code(prefix+'5\n')=='CODE-12345'


def test_device_code_remaining_lifetime_controls_ui_even_with_clock_skew():
    import subprocess
    result = subprocess.run(["node", str(PROJECT/"autosourcarr/tests/ui_v0329_device_ttl.js")], cwd=PROJECT, capture_output=True, text=True, timeout=10)
    assert result.returncode == 0, result.stdout+result.stderr
