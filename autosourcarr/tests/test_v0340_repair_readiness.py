"""A known host denial must stop preparation before archive or live-source I/O."""
import asyncio
import json
import importlib.util
import io
from pathlib import Path
from types import SimpleNamespace
import sys
from unittest.mock import AsyncMock

from fastapi import FastAPI
import httpx
import pytest


@pytest.fixture
def sandbox_module(monkeypatch):
    worker = Path(__file__).resolve().parents[2] / 'repair-worker'
    monkeypatch.syspath_prepend(str(worker))
    # Only exercise diagnostic formatting here; no POSIX process/limit fixture.
    monkeypatch.setitem(sys.modules, 'resource_budget', SimpleNamespace(diagnostics=lambda: {}))
    spec = importlib.util.spec_from_file_location('sandbox_diagnostics_v0342', worker / 'internal_sandbox.py')
    module = importlib.util.module_from_spec(spec)
    monkeypatch.setitem(sys.modules, spec.name, module)
    spec.loader.exec_module(module)
    return module


def test_private_proc_mount_denial_gives_the_scoped_docker_option(sandbox_module):
    checks = {'apparmor': 'autosourcarr-internal (enforce)', 'NoNewPrivs': '1', 'Seccomp': '2',
              'max_user_namespaces': '245814', 'proc_masked_paths': '2', 'proc_readonly_paths': '5'}
    result = sandbox_module.isolation_failure("bwrap: Can't mount proc on /newroot/proc: Operation not permitted", 1, checks)
    assert result['available'] is False and result['reason'] == 'mount-denied'
    assert 'private /proc' in result['message'] and 'systempaths=unconfined' in ' '.join(result['steps'])
    assert 'load and select' not in result['message'], 'The supplied profile is already active'
    assert result['checks'] == checks and result['api_checks_available'] and result['account_setup_available']
    other = sandbox_module.isolation_failure('bwrap: Cannot mount tmpfs: Operation not permitted', 1, checks)
    assert 'systempaths=unconfined' not in ' '.join(other['steps']), 'Do not prescribe proc unmasking for unrelated mounts'


def test_proc_mask_diagnostics_report_only_known_path_counts(sandbox_module, monkeypatch):
    mounts = ('25 20 0:1 / /proc rw,nosuid - proc proc rw\n'
              '26 25 0:2 /null /proc/keys rw - tmpfs tmpfs rw\n'
              '27 25 0:2 /null /proc/kcore rw - tmpfs tmpfs rw\n'
              '28 25 0:1 /sys /proc/sys ro - proc proc ro\n'
              '29 20 0:3 /private/token=SECRET /data/PRIVATE_HOST_PATH rw - ext4 /dev/private rw\n'
              '30 25 0:4 / /proc/arbitrary-private-name rw - tmpfs SECRET rw\n')
    monkeypatch.setattr(sandbox_module.Path, 'open', lambda *a, **k: io.StringIO(mounts))
    assert sandbox_module.proc_mount_checks() == {'proc_masked_paths': '2', 'proc_readonly_paths': '1'}


@pytest.mark.parametrize('failure', ['missing', 'oversized', 'empty', 'malformed'])
def test_unreadable_or_truncated_mount_diagnostics_remain_unknown(sandbox_module, monkeypatch, failure):
    def stream(*args, **kwargs):
        if failure == 'missing':
            raise OSError('not exposed')
        return io.StringIO({'oversized': 'x' * 65537, 'empty': '', 'malformed': 'not valid mountinfo\n'}[failure])
    monkeypatch.setattr(sandbox_module.Path, 'open', stream)
    assert sandbox_module.proc_mount_checks() == {'proc_masked_paths': 'not reported', 'proc_readonly_paths': 'not reported'}


@pytest.mark.asyncio
async def test_known_denial_rejects_manual_admission_before_reading_installed_packages(monkeypatch):
    from app import source_maintenance as module
    from app.config import settings
    monkeypatch.setattr(settings, 'source_engine_mode', 'integrated')
    worker = AsyncMock(return_value={'isolation': {'available': False, 'reason': 'namespace-denied',
                                                  'message': 'Host denied namespaces'}})
    inventory = AsyncMock(side_effect=AssertionError('Blocked repair must not inspect packages'))
    monkeypatch.setattr(module, 'worker', worker); monkeypatch.setattr(module, 'inventory', inventory)
    app = FastAPI(); app.include_router(module.maintenance_router(lambda: None, asyncio.Lock()))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://test') as client:
        response = await client.post('/api/admin/source-maintenance/jobs', json={'pkg_name': 'example'})
    assert response.status_code == 409 and 'Host denied namespaces' in response.text
    inventory.assert_not_awaited()
    worker.assert_awaited_once_with('GET', '/status')


def test_diagnostic_export_preserves_denial_checks_and_redacts_exact_error():
    from app.support_logs import SupportLogStore
    store = SupportLogStore(); store.privacy.register({'token': 'SYNTHETIC_SECRET_0340'})
    store.observe_repair({'jobs': [], 'isolation': {'available': False, 'reason': 'namespace-denied',
        'exit_code': 1, 'checks': {'NoNewPrivs': '1', 'Seccomp': '2', 'apparmor': 'docker-default (enforce)',
                                 'max_user_namespaces': '1234', 'proc_masked_paths': '3',
                                 'proc_readonly_paths': '5', 'private': 'PRIVATE_FIELD'},
        'details': 'bwrap: No permissions to create new namespace SYNTHETIC_SECRET_0340',
        'private_path': 'PRIVATE_PATH'}})
    isolation = store.snapshot()['source_repairs']['status']['isolation']
    assert isolation['checks']['apparmor'] == 'docker-default (enforce)'
    assert isolation['checks']['NoNewPrivs'] == '1' and isolation['exit_code'] == 1
    assert isolation['checks']['proc_masked_paths'] == '3' and isolation['checks']['proc_readonly_paths'] == '5'
    assert 'No permissions' in isolation['details']
    encoded = json.dumps(isolation)
    assert 'SYNTHETIC_SECRET_0340' not in encoded and 'PRIVATE_FIELD' not in encoded and 'PRIVATE_PATH' not in encoded


def test_proc_mount_diagnostic_fields_reject_arbitrary_strings():
    from app.repair_evidence import safe_isolation
    from app.support_logs import SupportLogStore
    result = safe_isolation({'available': False, 'checks': {'proc_masked_paths': 'PRIVATE_PATH',
                                                          'proc_readonly_paths': {'SECRET': 'hidden'}}}, SupportLogStore().privacy)
    assert result['checks'] == {}


def test_ui_host_setup_download_contains_current_proc_mount_instructions():
    from app.repair_host_setup import export_host_setup
    from zipfile import ZipFile
    with ZipFile(io.BytesIO(export_host_setup())) as archive:
        assert set(archive.namelist()) == {'README.md', 'security/repair-seccomp.json',
                                          'security/autosourcarr-internal.apparmor', 'security/LICENSE-moby'}
        guide = archive.read('README.md')
    assert guide == (Path(__file__).resolve().parents[2] / 'security/repair-host-setup.md').read_bytes()
    assert b'systempaths=unconfined' in guide and b'.HostConfig.MaskedPaths' in guide
    assert b'private PID namespace' in guide


def test_immediate_control_check_is_exported_without_waiting_for_helper_heartbeat():
    from app.support_logs import SupportLogStore
    store = SupportLogStore()
    store.observe_control({'state': 'failed', 'action': 'prepare', 'isolation': {'available': False,
        'reason': 'namespace-denied', 'details': 'bwrap: No permissions to create new namespace',
        'checks': {'Seccomp': '2', 'NoNewPrivs': '1'}}})
    status = store.snapshot()['source_repairs']['status']
    assert status['active'] == 'unknown' and status['captured_at'] is None
    assert status['isolation_captured_at']
    assert status['isolation']['checks']['Seccomp'] == '2'


@pytest.mark.asyncio
async def test_orphan_route_rejects_known_denial_without_creating_preparation_job(monkeypatch):
    from app import source_orphans as module
    from app.config import settings
    monkeypatch.setattr(settings, 'source_engine_mode', 'integrated')
    monkeypatch.setattr(settings, 'repair_worker_url', 'http://127.0.0.1:8766')
    manager = module.SourceOrphans()
    monkeypatch.setattr(manager, 'load', lambda: None)
    monkeypatch.setattr(module, 'source_orphans', manager)
    async def blocked():
        from fastapi import HTTPException
        raise HTTPException(409, 'Host denied namespaces; no repair queued')
    monkeypatch.setattr(module, 'ensure_repair_ready', blocked)
    prepare = AsyncMock(side_effect=AssertionError('No preparation should start'))
    monkeypatch.setattr(manager, 'prepare_repair', prepare)
    app = FastAPI(); app.include_router(module.orphan_router(lambda: None, asyncio.Lock()))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://test') as client:
        response = await client.post('/api/admin/source-orphans/eu.kanade.tachiyomi.extension.en.example/repair')
    assert response.status_code == 409 and manager.action is None
    prepare.assert_not_awaited()


@pytest.mark.asyncio
async def test_private_worker_denial_is_rejected_before_any_input_file_or_process(tmp_path, monkeypatch):
    worker = Path(__file__).resolve().parents[2]/'repair-worker'
    monkeypatch.syspath_prepend(str(worker))
    monkeypatch.setitem(sys.modules, 'control', SimpleNamespace(Control=object))
    monkeypatch.setitem(sys.modules, 'runner', SimpleNamespace(Runner=object))
    spec = importlib.util.spec_from_file_location('fixture_server_v0340', worker/'server.py')
    server = importlib.util.module_from_spec(spec); spec.loader.exec_module(server)
    server.runner = SimpleNamespace(isolation={'available': False, 'reason': 'namespace-denied', 'message': 'Host denied namespaces'})
    monkeypatch.setenv('REPAIR_TOKEN', 'x' * 40)
    from test_v0339_repair_payload import request
    try:
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=server.app), base_url='http://test') as client:
            result = await client.post('/jobs', json=request(), headers={'Authorization': 'Bearer ' + 'x' * 40})
        assert result.status_code == 409 and 'Host denied namespaces' in result.text
        assert list(tmp_path.iterdir()) == [] and not server.pending
    finally:
        server.executor.shutdown(wait=True)


@pytest.mark.asyncio
async def test_known_host_denial_defers_daily_watch_before_source_io_and_day_reservation(monkeypatch):
    from app import source_maintenance as module
    from app.config import settings
    from app.source_engine import source_engine
    from app.suwayomi import source_host
    monkeypatch.setattr(settings, 'app_services_mode', 'integrated')
    monkeypatch.setattr(settings, 'source_engine_mode', 'integrated')
    monkeypatch.setattr(settings, 'repair_worker_url', 'http://127.0.0.1:8766')
    monkeypatch.setattr(source_engine, 'state', 'ready')
    manager = module.SourceMaintenance(); manager.watch_state['enabled'] = True
    monkeypatch.setattr(manager, 'save_watch', lambda: None)
    worker = AsyncMock(return_value={'active': None, 'jobs': [], 'isolation': {'available': False, 'reason': 'namespace-denied', 'message': 'Host denied namespaces'}})
    monkeypatch.setattr(module, 'worker', worker)
    catalog = AsyncMock(side_effect=AssertionError('No source I/O under a known host denial'))
    monkeypatch.setattr(source_host, 'extensions', catalog)
    await manager.watch_once()
    assert manager.watch_state['next_check'] == 0 and manager.watch_state['attempts'] == {}
    assert manager.watch_state['local_retry_after'] > 0
    assert 'Host denied namespaces' in manager.watch_state['message']
    worker.assert_awaited_once_with('GET', '/status'); catalog.assert_not_awaited()
