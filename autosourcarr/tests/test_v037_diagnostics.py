"""Diagnostic exports retain useful failure context without leaking credentials."""
import asyncio
import json
from unittest.mock import AsyncMock

import httpx
import pytest

from app import diagnostics, main, service as module
from app.config import settings
from app.error_registry import ErrorRegistry
from app.suwayomi import SuwayomiClient, SourceWorkDeferred, BatchDeadlineExceeded, bounded_map
from test_v086_imports import env, store


@pytest.mark.asyncio
@pytest.mark.parametrize('fallback', ['unavailable', 'unauthorized', 'html', 'healthy'])
async def test_health_retains_both_probe_results_and_rejects_login_pages(fallback):
    calls = []
    def send(request):
        calls.append((request.method, request.url.path))
        if request.method == 'POST':
            raise httpx.ReadTimeout('', request=request)
        if fallback == 'healthy':
            return httpx.Response(200, json={'version': '2.3.2243'})
        if fallback == 'html':
            return httpx.Response(200, text='<html>Sign in</html>')
        return httpx.Response(401 if fallback == 'unauthorized' else 503)
    client = SuwayomiClient('http://source.invalid')
    await client.client.aclose()
    client.client = httpx.AsyncClient(transport=httpx.MockTransport(send))
    try:
        result = await client.health()
        assert result['ok'] == (fallback == 'healthy')
        assert calls == [('POST', '/api/graphql'), ('GET', '/api/v1/settings/about')]
        assert result['probes'][0]['error'] and result['probes'][0]['exception_chain']
        assert all('elapsed_ms' in p for p in result['probes'])
        if fallback == 'healthy':
            assert result['degraded'] and result['warning']
        else:
            assert result['error'] and result['probes'][1]['error']
        if fallback == 'unavailable':
            assert result['probes'][1]['http_status'] == 503
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_health_local_outage_and_cancellation_do_not_try_fallback(monkeypatch):
    client = SuwayomiClient('http://source.invalid')
    fallback = AsyncMock(side_effect=AssertionError('No fallback'))
    monkeypatch.setattr(client.client, 'get', fallback)
    query = AsyncMock(side_effect=SourceWorkDeferred('Engine starting'))
    monkeypatch.setattr(client, 'graphql', query)
    try:
        assert (await client.health())['deferred']
        query.side_effect = asyncio.CancelledError
        with pytest.raises(asyncio.CancelledError):
            await client.health()
        fallback.assert_not_called()
    finally:
        await client.close()


def test_registry_version_trace_redaction_and_snapshot_isolation():
    registry = ErrorRegistry()
    try:
        password_in_locals = 'never-collect-locals'
        raise RuntimeError('https://user:password@source.test/chapter?token=private-token&part=3')
    except RuntimeError as exc:
        registry.record('Import', 'Lookup failed', key='one', detail='x'*13000,
                        context={'token': 'do-not-export', 'operation': 'source_lookup'}, exception=exc)
    row = registry.snapshot()['items'][0]
    assert row['app_version'] == diagnostics.APP_VERSION
    assert row['instance_id'] == diagnostics.INSTANCE_ID
    assert row['detail_truncated'] and len(row['detail']) == 12000
    frame = row['exception_chain'][0]['frames'][-1]
    assert frame['function'] == 'test_registry_version_trace_redaction_and_snapshot_isolation'
    text = json.dumps(row)
    for secret in ('user:password', 'private-token', 'do-not-export', password_in_locals):
        assert secret not in text
    assert 'part=3' in text
    row['context']['operation'] = 'mutated'
    assert registry.snapshot()['items'][0]['context']['operation'] == 'source_lookup'
    registry.record('Import', 'Lookup failed again', key='one', context={'phase': 'execution'})
    assert registry.snapshot()['items'][0]['count'] == 2
    assert registry.snapshot()['items'][0]['context'] == {'phase': 'execution'}


@pytest.mark.parametrize('state', ['added', 'existing', 'skipped', 'unresolved', 'failed'])
def test_completed_import_resolves_only_its_own_discovery_warning(env, state):
    s, _, _ = env
    iid = store(s)
    key = f'import-discovery:run-a:{iid}'
    registry = module.error_registry
    registry.record('Import discovery', 'Timed out', key=key)
    registry.record('Import discovery', 'Other timeout', key='import-discovery:other:2')
    s._update_import_item_state('run-a', iid, state, 'manual action')
    rows = {item['key']: item for item in registry.snapshot()['items']}
    assert rows[key]['resolved'] == (state in {'added', 'existing', 'skipped'})
    assert not rows['import-discovery:other:2']['resolved']


@pytest.mark.asyncio
async def test_export_is_authenticated_local_and_contains_affected_item_state(env, monkeypatch):
    s, _, tasks = env
    iid = store(s)
    registry = module.error_registry
    registry.record('Import discovery', 'Timed out', key=f'import-discovery:run-a:{iid}')
    monkeypatch.setattr(main, 'service', s)
    monkeypatch.setattr(main, 'error_registry', registry)
    monkeypatch.setattr(main, 'task_registry', tasks)
    monkeypatch.setattr(main, '_last_dependency_health', {'captured_at': 'previous-check', 'source_host': {'ok': False}})
    monkeypatch.setattr(settings, 'admin_token', 'fixture-secret')
    monkeypatch.setattr(main.source_host, 'health', AsyncMock(side_effect=AssertionError('No network')))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url='http://test') as client:
        assert (await client.get('/api/admin/diagnostics')).status_code == 401
        response = await client.get('/api/admin/diagnostics', headers={'X-Admin-Token': 'fixture-secret'})
        assert response.status_code == 200, response.text
        body = response.json()
        assert response.headers['x-autosourcarr-version'] == main.APP_VERSION
        assert body['runtime']['app_version'] == main.APP_VERSION
        assert body['health']['captured_at'] == 'previous-check'
        assert body['imports']['items'][0]['state'] == 'unresolved'
        assert 'raw_json' not in body['imports']['items'][0]
        assert body['errors']['items'][0]['app_version'] == main.APP_VERSION
        assert 'fixture-secret' not in response.text
        monkeypatch.setattr(s, 'import_diagnostic_context', lambda _: (_ for _ in ()).throw(RuntimeError('Database busy')))
        response = await client.get('/api/admin/diagnostics', headers={'X-Admin-Token': 'fixture-secret'})
        assert response.status_code == 200
        assert response.json()['partial_error']['section'] == 'imports'
        assert response.json()['errors']['items']


def test_grouped_latest_message_retains_its_matching_trace_and_context():
    from app.diagnostic_groups import group_diagnostics
    old = {'id': 'old', 'message': 'Failed', 'last_seen': '2026-09-22', 'context': {'item_id': 1}, 'exception_chain': [{'type': 'Old'}], 'app_version': '0.3.6', 'detail_truncated': True}
    new = {**old, 'id': 'new', 'last_seen': '2026-09-23', 'context': {'item_id': 2}, 'exception_chain': [{'type': 'New'}], 'app_version': '0.3.7', 'detail_truncated': False}
    result = group_diagnostics([old, new])[0]
    assert result['id'] == 'new' and result['context'] == {'item_id': 2}
    assert result['exception_chain'] == [{'type': 'New'}] and result['app_version'] == '0.3.7'
    assert not result['detail_truncated']


def test_userinfo_and_camelcase_credentials_are_scrubbed():
    result = diagnostics.scrub({'accessToken': 'hidden-value', 'url': 'https://private-user@host/path?part=3'})
    assert result['accessToken'] == '[redacted]'
    assert 'private-user' not in result['url'] and 'part=3' in result['url']


def test_superseded_import_action_does_not_clear_its_warning(env):
    s, _, _ = env
    iid = store(s)
    key = f'import-discovery:run-a:{iid}'
    module.error_registry.record('Import discovery', 'Timed out', key=key)
    result = s._update_import_item_state('run-a', iid, 'skipped', '', expected_revision=900)
    assert result['superseded']
    assert not module.error_registry.snapshot()['items'][0]['resolved']
