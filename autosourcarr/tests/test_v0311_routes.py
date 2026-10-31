"""Boundary tests for source controls and reader preparing responses."""
import asyncio
import json

import httpx
import pytest
from starlette.requests import Request
from app import main
from app.service import ReaderBrowsePending


@pytest.fixture
def app_state(monkeypatch):
    monkeypatch.setattr(main.settings, 'admin_token', 'admin-secret')
    monkeypatch.setattr(main, '_database_admin_lock', asyncio.Lock())
    monkeypatch.setattr(main, '_catalog_reset_active', False)
    monkeypatch.setattr(main, '_catalog_http_requests', 0)
    monkeypatch.setattr(main.service, 'schedule_source_reconciliation', lambda **kw: True)


def web():
    return httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url='http://test')


@pytest.mark.asyncio
async def test_control_routes_require_admin_before_reading_body(app_state, monkeypatch):
    async def unexpected(*args):
        raise AssertionError('unauthorized operation')
    monkeypatch.setattr(main.source_controls, 'preferences_snapshot', unexpected)
    monkeypatch.setattr(main.source_controls, 'update_preference', unexpected)
    monkeypatch.setattr(main.source_controls, 'install_uploaded_apk', unexpected)
    async with web() as client:
        for method, path in [('GET', '/api/admin/sources/123/preferences'),
                             ('PUT', '/api/admin/sources/123/preferences'),
                             ('POST', '/api/admin/source-extensions/upload')]:
            response = await client.request(method, path, content=b'not valid')
            assert response.status_code == 401


@pytest.mark.asyncio
async def test_preference_response_no_store_and_large_source_id_exact(app_state, monkeypatch):
    source = '9223372036854775806'
    seen = []
    async def snapshot(client, sid):
        seen.append(sid)
        return {'source_id': sid, 'preferences': [], 'revision': 'one'}
    monkeypatch.setattr(main.source_controls, 'preferences_snapshot', snapshot)
    async with web() as client:
        response = await client.get(f'/api/admin/sources/{source}/preferences', headers={'X-Admin-Token':'admin-secret'})
    assert response.status_code == 200
    assert response.headers['cache-control'] == 'no-store'
    assert seen == [source]
    assert response.json()['source_id'] == source


@pytest.mark.asyncio
async def test_preferences_parse_errors_do_not_echo_input(app_state, monkeypatch):
    secret = 'secret-source-password'
    async with web() as client:
        for payload in [secret.encode(), json.dumps([secret]).encode(), b'x' * 65537]:
            response = await client.put('/api/admin/sources/123/preferences', content=payload,
                                         headers={'X-Admin-Token':'admin-secret'})
            assert response.status_code in {400, 413}
            assert secret not in response.text


@pytest.mark.asyncio
async def test_unexpected_upstream_error_is_sanitized(app_state, monkeypatch):
    async def broken(*args):
        raise RuntimeError('password=secret-source-password')
    monkeypatch.setattr(main.source_controls, 'update_preference', broken)
    async with web() as client:
        response = await client.put('/api/admin/sources/123/preferences', json={}, headers={'X-Admin-Token':'admin-secret'})
    assert response.status_code == 503
    assert 'secret-source-password' not in response.text
    assert 'Refresh' in response.text


@pytest.mark.asyncio
async def test_changed_settings_return_conflict(app_state, monkeypatch):
    async def changed(*args):
        raise main.source_controls.SourcePreferencesConflict('Reload source settings')
    monkeypatch.setattr(main.source_controls, 'update_preference', changed)
    async with web() as client:
        response = await client.put('/api/admin/sources/123/preferences', json={}, headers={'X-Admin-Token':'admin-secret'})
    assert response.status_code == 409


@pytest.mark.asyncio
async def test_upload_raw_bytes_filename_and_lock(app_state, monkeypatch):
    seen = []
    async def install(client, data, filename):
        assert main._database_admin_lock.locked()
        seen.append((data, filename))
        return {'extension': {'pkgName': 'test.source'}, 'backup_saved': False}
    monkeypatch.setattr(main.source_controls, 'install_uploaded_apk', install)
    async with web() as client:
        response = await client.post('/api/admin/source-extensions/upload', content=b'APK bytes',
            headers={'X-Admin-Token':'admin-secret','Content-Type':'application/vnd.android.package-archive','X-Filename':'my%20source.apk'})
    assert response.status_code == 200
    assert seen == [(b'APK bytes', 'my source.apk')]
    assert response.json()['source_sync_pending'] is True
    assert response.headers['cache-control'] == 'no-store'


@pytest.mark.asyncio
async def test_upload_wrong_content_type_rejected(app_state):
    async with web() as client:
        response = await client.post('/api/admin/source-extensions/upload', json={}, headers={'X-Admin-Token':'admin-secret'})
    assert response.status_code == 415


@pytest.mark.asyncio
async def test_streaming_upload_limit_without_content_length():
    messages = iter([{'type':'http.request','body':b'1234','more_body':True},
                     {'type':'http.request','body':b'5678','more_body':False}])
    async def receive():
        return next(messages)
    request = Request({'type':'http','method':'POST','path':'/','headers':[]}, receive)
    with pytest.raises(main.HTTPException) as caught:
        await main._source_control_body(request, 5)
    assert caught.value.status_code == 413


@pytest.mark.asyncio
async def test_waiting_source_mutation_does_not_deadlock_reset(app_state, monkeypatch):
    called = asyncio.Event()
    reconciliations = []
    monkeypatch.setattr(main.service, 'schedule_source_reconciliation',
                        lambda **kw: reconciliations.append(kw) or True)
    async def save(*args):
        called.set()
        assert main._database_admin_lock.locked()
        return {'preferences': [], 'revision': 'next'}
    monkeypatch.setattr(main.source_controls, 'update_preference', save)
    async with web() as client:
        async with main._database_admin_lock:
            async with main._exclusive_catalog_reset(timeout=.15):
                request = asyncio.create_task(client.put('/api/admin/sources/123/preferences', json={}, headers={'X-Admin-Token':'admin-secret'}))
                await asyncio.sleep(.03)
                assert not called.is_set()
                assert main._catalog_http_requests == 0
        response = await asyncio.wait_for(request, 1)
    assert response.status_code == 200
    assert called.is_set()
    assert reconciliations == [{'delay': 0.25}]


@pytest.mark.asyncio
@pytest.mark.parametrize('path,mode', [('/api/v1/browse/popular','POPULAR'),('/api/v1/browse/latest','LATEST'),('/api/v1/search?q=hero','SEARCH')])
async def test_reader_pending_is_bounded_retryable_not_empty_feed(app_state, monkeypatch, path, mode):
    async def pending(actual_mode, **kw):
        assert actual_mode == mode
        raise ReaderBrowsePending()
    monkeypatch.setattr(main.service, 'reader_federated', pending)
    async with web() as client:
        response = await client.get(path)
    assert response.status_code == 503
    assert response.headers['x-autosourcarr-reader-state'] == 'preparing'
    assert int(response.headers['retry-after']) > 0
    assert response.json()['preparing'] is True
    assert 'items' not in response.json()
