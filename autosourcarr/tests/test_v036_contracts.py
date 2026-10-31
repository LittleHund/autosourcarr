import asyncio
import pytest
import httpx
from test_v086_imports import env
from test_v089_decisions import prepare


@pytest.mark.parametrize('action', ['match', 'retry'])
def test_queue_ack_contains_the_committed_decision_revision(env, monkeypatch, action):
    service, db, _ = env
    iid = prepare(service, monkeypatch)
    async def run():
        for _ in range(service._import_resolve_capacity):
            await service._import_resolve_semaphore.acquire()
        method = service.schedule_import_item_match if action == 'match' else service.schedule_import_item_retry
        args = ('run-a', iid, 42) if action == 'match' else ('run-a', iid)
        try:
            queued = method(*args)
            row = service._read_import_item('run-a', iid)
            assert queued['decision_revision'] == row['decision_revision'] > 0
            again = method(*args)
            assert again['already_running']
            assert again['decision_revision'] == queued['decision_revision']
            runtime = service._refresh_tasks[f'__import-{action}__:run-a:{iid}']
            runtime.cancel()
            await asyncio.gather(runtime, return_exceptions=True)
        finally:
            for _ in range(service._import_resolve_capacity):
                service._import_resolve_semaphore.release()
    asyncio.run(run())


@pytest.mark.asyncio
async def test_mixed_routes_require_admin_and_reject_invalid_requests(monkeypatch):
    from app import main
    monkeypatch.setattr(main.settings, 'admin_token', 'fixture-secret')
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url='http://test') as client:
        for path in ['/api/admin/mixed-entries', '/api/admin/mixed-entries/1']:
            assert (await client.get(path)).status_code == 401
        assert (await client.post('/api/admin/mixed-entries/1/review', json={'fingerprint':'f'*64,'decision':'keep'})).status_code == 401
        headers={'X-Admin-Token':'fixture-secret'}
        assert (await client.get('/api/admin/mixed-entries?state=invalid',headers=headers)).status_code == 422
        assert (await client.post('/api/admin/mixed-entries/1/review',headers=headers,json={'fingerprint':'f'*64,'decision':'split'})).status_code == 422
