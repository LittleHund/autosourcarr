"""The public tag review and source commits must use one decision writer gate."""
import asyncio
import threading
from concurrent.futures import ThreadPoolExecutor

import httpx

from app import tag_store
from test_v0323_matching_review import env


def test_public_tag_review_waits_for_existing_catalog_writer(env, monkeypatch):
    from app import main
    service, database = env
    monkeypatch.setattr(main, 'service', service)
    monkeypatch.setattr(tag_store, 'db', database)
    monkeypatch.setattr(main.settings, 'admin_token', 'isolated-test-token')
    tag_store.invalidate()
    with database.catalog() as c:
        tag_store.sync_source_record(c, 'a', 1, {'genre': ['sci-fi', 'science fiction']})
    ids = {x['name']: x['id'] for x in tag_store.list_tags()['items']}
    entered, release = threading.Event(), threading.Event()
    def hold_writer():
        with service._catalog_maintenance_lock:
            entered.set()
            assert release.wait(4)
    async def run():
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url='http://test') as client:
            request = asyncio.create_task(client.post('/api/admin/tags/review',
                headers={'X-Admin-Token': 'isolated-test-token'},
                json={'left_id': ids['sci-fi'], 'right_id': ids['science fiction'], 'decision': 'different'}))
            try:
                await asyncio.wait_for(asyncio.shield(request), .25)
            except asyncio.TimeoutError:
                blocked = True
            else:
                blocked = False
            finally:
                release.set()
            response = await request
            assert response.status_code == 200
            assert blocked, 'Public tag decision committed while the source commit writer gate was held'
    with ThreadPoolExecutor(max_workers=1) as worker:
        held = worker.submit(hold_writer)
        assert entered.wait(2)
        try:
            asyncio.run(run())
        finally:
            release.set()
            held.result(timeout=4)
    assert tag_store.canonical_keys(['sci-fi', 'science fiction']) == {'tag:sci-fi', 'tag:science fiction'}
