"""Repair failures must reach downloaded evidence without private job bodies."""
import json
from unittest.mock import AsyncMock

import httpx
import pytest

from test_v0339_baseline_manifest import PKG


def test_worker_job_state_and_proof_are_retained_with_strict_field_and_privacy_limits():
    from app.support_logs import SupportLogStore
    store = SupportLogStore()
    store.privacy.register({'token': 'SYNTHETIC_CONFIGURED_SECRET'})
    job = {'id': 'a' * 32, 'pkg_name': PKG, 'state': 'blocked', 'message': 'Original manifest missing SYNTHETIC_CONFIGURED_SECRET',
           'created_at': 1, 'updated_at': 2, 'baseline_issue': 'converted-jar-without-manifest',
           'baseline_contract_basis': 'user-confirmed-original',
           'baseline_contract': {'package': PKG, 'entrypoint': PKG + '.Example', 'library': '1.4', 'private_data': 'PRIVATE_METADATA'},
           'history': [{'state': 'checking-backup', 'message': 'Read rollback bytes', 'at': 1}],
           'conversation': [{'text': 'PRIVATE_CONVERSATION'}], 'patch': 'PRIVATE_SOURCE_PATCH',
           'issue': 'PRIVATE_USER_ISSUE', 'source_apk': 'PRIVATE_APK_BODY', 'auth': 'PRIVATE_AUTH'}
    store.observe_repair(job)
    snapshot = store.snapshot()
    observed = snapshot['source_repairs']['jobs'][0]
    assert observed['state'] == 'blocked' and observed['baseline_issue'] == 'converted-jar-without-manifest'
    assert observed['history'][0]['state'] == 'checking-backup'
    assert observed['baseline_contract']['entrypoint'] == PKG + '.Example'
    assert observed['baseline_contract_basis'] == 'user-confirmed-original'
    encoded = json.dumps(snapshot)
    for value in ('SYNTHETIC_CONFIGURED_SECRET', 'PRIVATE_CONVERSATION', 'PRIVATE_SOURCE_PATCH', 'PRIVATE_USER_ISSUE', 'PRIVATE_APK_BODY', 'PRIVATE_AUTH', 'PRIVATE_METADATA'):
        assert value not in encoded


@pytest.mark.asyncio
async def test_existing_worker_responses_feed_diagnostic_cache_without_new_probes(monkeypatch):
    from app import source_maintenance as module
    from app.support_logs import SupportLogStore
    from app.config import settings
    store = SupportLogStore()
    monkeypatch.setattr(module, 'support_logs', store)
    monkeypatch.setattr(settings, 'repair_worker_url', 'http://127.0.0.1:8766')
    monkeypatch.setattr(settings, 'repair_worker_token', 'SYNTHETIC_PRIVATE_WORKER_TOKEN')
    requests = []
    def response(request):
        requests.append(request.url.path)
        return httpx.Response(200, json={'active': None, 'jobs': [{'id': 'a' * 32, 'pkg_name': PKG,
            'state': 'blocked', 'message': 'Installed manifest missing', 'updated_at': 2}],
            'isolation': {'available': False, 'reason': 'not-checked', 'message': 'Check tools', 'private_path': 'PRIVATE_PATH'}})
    client = httpx.AsyncClient
    monkeypatch.setattr(module.httpx, 'AsyncClient', lambda **kw: client(transport=httpx.MockTransport(response), **kw))
    await module.worker('GET', '/status')
    snapshot = store.snapshot()
    assert requests == ['/status']
    assert snapshot['source_repairs']['jobs'][0]['message'] == 'Installed manifest missing'
    assert 'PRIVATE_PATH' not in json.dumps(snapshot)


@pytest.mark.asyncio
async def test_orphan_preparation_failure_is_logged_even_before_a_job_exists(tmp_path, monkeypatch):
    import asyncio
    from fastapi import HTTPException
    from app import source_orphans as module
    from app.support_logs import SupportLogStore
    from app.config import settings
    store = SupportLogStore()
    monkeypatch.setattr(module, 'support_logs', store)
    monkeypatch.setattr(settings, 'data_dir', tmp_path)
    manager = module.SourceOrphans()
    monkeypatch.setattr(manager, 'repair', AsyncMock(side_effect=HTTPException(409, 'Matching original APK is unavailable')))
    monkeypatch.setattr(manager, 'save', lambda: None)
    await manager.prepare_repair(PKG, asyncio.Lock())
    evidence = store.snapshot()['source_repairs']['orphan_operation']
    assert evidence['state'] == 'failed' and 'Matching original APK is unavailable' in evidence['message']
    assert any('Matching original APK' in row['message'] for row in store.snapshot()['entries'])


@pytest.mark.asyncio
async def test_immediate_worker_block_is_not_presented_as_a_successful_queue(tmp_path, monkeypatch):
    import asyncio
    from app import source_orphans as module
    from app.support_logs import SupportLogStore
    store = SupportLogStore()
    monkeypatch.setattr(module, 'support_logs', store)
    manager = module.SourceOrphans()
    monkeypatch.setattr(manager, 'repair', AsyncMock(return_value={'id': 'a' * 32, 'state': 'blocked', 'message': 'Original APK mismatch'}))
    monkeypatch.setattr(manager, 'save', lambda: None)
    await manager.prepare_repair(PKG, asyncio.Lock())
    assert manager.operation['state'] == 'failed'
    assert manager.operation['repair_job_id'] == 'a' * 32
    assert 'Original APK mismatch' in manager.operation['message']
