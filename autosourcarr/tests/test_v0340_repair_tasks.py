"""Persistent repair jobs share Tasks without acquiring execution or live data."""
import json
import asyncio
from unittest.mock import AsyncMock

from fastapi import FastAPI, HTTPException
import httpx
import pytest

from app.support_logs import SupportPrivacy


PKG = 'eu.kanade.tachiyomi.extension.en.example'


def job(identifier='a', state='blocked', **values):
    return {'id': identifier * 32, 'pkg_name': PKG, 'state': state,
            'message': 'The host denied namespace creation', 'created_at': 1791670592,
            'updated_at': 1791670613, **values}


def store():
    from app.repair_tasks import RepairTasks
    return RepairTasks(SupportPrivacy())


def test_jobs_join_existing_task_sections_and_keep_the_actual_repair_stage():
    tasks = store()
    tasks.observe({'version': '0.3.40', 'active': 'b' * 32,
                   'jobs': [job(version='0.3.40'), job('b', 'checking-baseline'), job('c', 'ready'), job('d', 'queued', origin='automatic')]})
    result = tasks.merge({'items': [{'id': 'existing', 'state': 'running', 'activity': 'waiting'}]})
    assert (result['running'], result['active'], result['queued'], result['waiting']) == (3, 1, 1, 1)
    assert (result['failed'], result['completed']) == (1, 1)
    blocked = next(row for row in result['items'] if row['id'] == 'source-repair:' + 'a' * 32)
    assert blocked['state'] == 'failed' and blocked['repair_state'] == 'blocked'
    assert blocked['repair_version'] == '0.3.40'
    assert blocked['created_at'].startswith('2026-10-10T')
    assert not blocked['cancellable']
    automatic = next(row for row in result['items'] if row.get('repair_id') == 'd' * 32)
    assert automatic['origin'] == 'automatic' and automatic['activity'] == 'queued'


def test_old_records_are_not_relabelled_as_new_release_attempts():
    tasks = store()
    tasks.observe({'version': '0.3.40', 'jobs': [job(), job('b', version='0.3.39')]})
    items = tasks.merge({'items': []})['items']
    assert items[0]['repair_version'] == 'unknown'
    assert items[1]['repair_version'] == '0.3.39'


def test_package_promotion_suppresses_review_and_follows_actual_install_outcome():
    tasks = store(); tasks.observe({'jobs': [job(state='ready')]})
    operation = {'id': 'a' * 32, 'state': 'installing', 'message': 'Importing checked binary'}
    installing = tasks.merge({'items': []}, operation)['items'][0]
    assert installing['state'] == 'running' and installing['repair_state'] == 'installing'
    assert not installing['cancellable']
    operation.update(state='installed')
    installed = tasks.merge({'items': []}, operation)['items'][0]
    assert installed['state'] == 'completed' and installed['repair_state'] == 'installed'


def test_failed_or_rolled_back_promotion_keeps_a_still_ready_candidate_reviewable():
    tasks = store(); tasks.observe({'jobs': [job(state='ready')]})
    for state in ('failed', 'rolled-back'):
        operation = {'id': 'a' * 32, 'state': state, 'message': 'Previous binary retained'}
        item = tasks.merge({'items': []}, operation)['items'][0]
        assert item['repair_state'] == state and item['repair_reviewable']
        assert not item['cancellable']
        assert tasks.clear_history(operation)['kept_for_review'] == 1
        assert len(tasks.merge({'items': []}, operation)['items']) == 1


def test_unavailable_helper_retains_history_but_does_not_offer_stale_actions():
    tasks = store(); tasks.observe({'jobs': [job(state='validating')]})
    tasks.unavailable()
    item = tasks.merge({'items': []})['items'][0]
    assert item['stale'] is True and not item['cancellable']
    assert item['activity'] == 'waiting'
    assert item['repair_state'] == 'validating'
    tasks.observe({'jobs': [job(state='ready')]})
    assert tasks.merge({'items': []})['items'][0]['stale'] is False


def test_clear_history_hides_finished_rows_but_preserves_ready_review_and_changed_states():
    tasks = store(); tasks.observe({'jobs': [job(), job('b', 'ready'), job('c', 'repairing')]})
    cleared = tasks.clear_history()
    assert cleared['cleared'] == 1 and cleared['kept_for_review'] == 1
    assert len(tasks.merge({'items': []})['items']) == 2
    tasks.observe({'jobs': [job(), job('b', 'ready'), job('c', 'repairing')]})
    assert len(tasks.merge({'items': []})['items']) == 2
    tasks.observe({'jobs': [job(state='ready', updated_at=1791670614)]})
    assert len(tasks.merge({'items': []})['items']) == 1


def test_projection_is_bounded_and_never_includes_private_worker_fields():
    tasks = store()
    tasks.privacy.register({'token': 'SYNTHETIC_SECRET_0340'})
    tasks.observe({'jobs': [job(message='Denied SYNTHETIC_SECRET_0340', issue='PRIVATE_ISSUE',
                              conversation=['PRIVATE_CHAT'], patch='PRIVATE_PATCH', baseline='PRIVATE_BINARY')],
                   'isolation': {'available': False, 'reason': 'namespace-denied'}, 'auth': 'PRIVATE_AUTH'})
    encoded = json.dumps(tasks.merge({'items': []}))
    for text in ('SYNTHETIC_SECRET_0340', 'PRIVATE_ISSUE', 'PRIVATE_CHAT', 'PRIVATE_PATCH', 'PRIVATE_BINARY', 'PRIVATE_AUTH'):
        assert text not in encoded
    tasks.observe({'jobs': [job(id=f'{number:032x}', created_at=number) for number in range(200)]})
    assert len(tasks.merge({'items': []})['items']) == 100


def test_delayed_status_does_not_undo_a_newer_job_response_or_drop_new_admission():
    tasks = store(); tasks.observe({'jobs': [job(state='repairing', updated_at=10)]})
    tasks.observe(job(state='ready', updated_at=12))
    tasks.observe(job('b', 'queued', created_at=11, updated_at=11))
    tasks.observe({'captured_at': 10, 'jobs': [job(state='repairing', updated_at=10)]})
    items = tasks.merge({'items': []})['items']
    assert len(items) == 2
    assert next(item for item in items if item['repair_id'] == 'a' * 32)['repair_state'] == 'ready'


@pytest.mark.asyncio
async def test_actual_tasks_route_includes_repairs_without_any_integrated_helper_request(monkeypatch):
    from app import repair_tasks as module
    from app import source_maintenance
    from app.config import settings
    from app.task_registry import TaskRegistry
    import app.task_registry as registry_module
    tasks = store(); tasks.observe({'jobs': [job(), job('b', 'ready')]})
    registry = TaskRegistry(); registry.create('Existing local action', origin='manual')
    monkeypatch.setattr(module, 'repair_tasks', tasks)
    monkeypatch.setattr(registry_module, 'task_registry', registry)
    monkeypatch.setattr(settings, 'app_services_mode', 'integrated')
    monkeypatch.setattr(source_maintenance.source_maintenance, 'operation', {})
    worker = AsyncMock(side_effect=AssertionError('Tasks must use the existing integrated health snapshot'))
    monkeypatch.setattr(source_maintenance, 'worker', worker)
    app = FastAPI(); app.include_router(module.tasks_router(lambda: None))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://test') as client:
        response = await client.get('/api/admin/tasks')
        assert response.status_code == 200
        result = response.json()
        assert (result['running'], result['failed'], result['completed']) == (1, 1, 1)
        assert result['source_repairs']['reachable'] is True
        cleared = (await client.post('/api/admin/tasks/clear-history')).json()
        assert cleared['cleared'] == 1 and cleared['kept_for_review'] == 1
        assert len((await client.get('/api/admin/tasks')).json()['items']) == 2
    worker.assert_not_awaited()


@pytest.mark.asyncio
async def test_tasks_projection_preserves_admin_authentication():
    from app.repair_tasks import tasks_router
    def guard():
        raise HTTPException(401, 'Authentication required')
    app = FastAPI(); app.include_router(tasks_router(guard))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://test') as client:
        assert (await client.get('/api/admin/tasks')).status_code == 401
        assert (await client.post('/api/admin/tasks/clear-history')).status_code == 401
        assert (await client.post('/api/admin/tasks/source-repair:' + 'a' * 32 + '/cancel')).status_code == 401


@pytest.mark.asyncio
async def test_tasks_cancel_proxies_worker_request_without_claiming_cleanup_finished(monkeypatch):
    from app.repair_tasks import tasks_router
    from app import source_maintenance
    worker = AsyncMock(return_value={'requested': True})
    monkeypatch.setattr(source_maintenance, 'worker', worker)
    app = FastAPI(); app.include_router(tasks_router(lambda: None))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://test') as client:
        result = await client.post('/api/admin/tasks/source-repair:' + 'a' * 32 + '/cancel')
        assert result.status_code == 200 and result.json()['cancel_requested'] is True
        assert 'state' not in result.json()
        worker.assert_awaited_once_with('POST', '/jobs/' + 'a' * 32 + '/cancel')
        assert (await client.post('/api/admin/tasks/source-repair:invalid/cancel')).status_code == 404


@pytest.mark.asyncio
async def test_owned_service_health_check_populates_tasks_without_opening_sources(monkeypatch):
    from app import internal_services as module, repair_tasks as tasks_module
    from app.config import settings
    tasks = store()
    monkeypatch.setattr(tasks_module, 'repair_tasks', tasks)
    monkeypatch.setattr(settings, 'app_services_mode', 'integrated')
    services = module.InternalServices()
    process = type('Process', (), {'returncode': None})()
    async def wait():
        process.returncode = 0
        return 0
    process.wait = wait
    monkeypatch.setattr(services, 'environment', lambda name: {})
    monkeypatch.setattr(services, 'command', lambda name, env: (['fixture'], 'http://127.0.0.1:8766/status'))
    monkeypatch.setattr(services, '_spawn', AsyncMock(return_value=process))
    monkeypatch.setattr(services, '_stop', AsyncMock(return_value=True))
    async def drain(_):
        return 0
    monkeypatch.setattr(services, '_drain', drain)
    calls = []
    def response(request):
        calls.append(request.url.path); services.stopping = True
        return httpx.Response(200, json={'active': None, 'jobs': [job()], 'isolation': {'available': False, 'reason': 'namespace-denied'}})
    client = httpx.AsyncClient
    monkeypatch.setattr(module.httpx, 'AsyncClient', lambda **kw: client(transport=httpx.MockTransport(response), **kw))
    await services._serve('repair')
    assert calls == ['/status']
    result = tasks.merge({'items': []})
    assert result['failed'] == 1 and result['source_repairs']['reachable'] is True
