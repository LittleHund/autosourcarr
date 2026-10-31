"""Public configuration read must not publish pre-decision source membership."""
import asyncio
import threading

import httpx
from fastapi import FastAPI

from app import matching_review as review
from app.read_lane import ReadLane
from test_v0323_matching_review import env, entry, payload, save


def test_slow_public_configuration_read_cannot_overwrite_source_group_decision(env, monkeypatch):
    service, database = env
    group = service.create_source_group('Require a human join')['id']
    current = entry(service, 'a')
    save(env, 'group', group, 'external', {'enabled': False, 'checks': {}})
    captured, release = threading.Event(), threading.Event()
    original = database.rows

    def paused_rows(rows):
        result = original(rows)
        if (threading.current_thread().name.startswith(('catalogue-read', 'asyncio_'))
                and result and 'group_priority' in result[0] and not captured.is_set()):
            captured.set()
            assert release.wait(4)
        return result

    monkeypatch.setattr(database, 'rows', paused_rows)
    lane = ReadLane(workers=1)
    monkeypatch.setattr(review, 'read_lane', lane, raising=False)
    application = FastAPI()
    application.include_router(review.router(database, service, lambda: True))

    async def scenario():
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=application), base_url='http://review') as client:
            request = asyncio.create_task(client.get('/api/admin/matching-policies'))
            try:
                assert await asyncio.to_thread(captured.wait, 2)
                await asyncio.to_thread(service.update_source, 'a', group_id=group)
                assert service._source_cache['a']['group_id'] == group
            finally:
                release.set()
            response = await request
            assert response.status_code == 200
        await lane.close()

    asyncio.run(scenario())
    with database.catalog() as connection:
        assert connection.execute("SELECT group_id FROM source_config WHERE source_id='a'").fetchone()[0] == group
    # A read has finished after the saved group assignment. The subsequent real
    # ingest must use the saved group's disabled external matching policy.
    assert service.ingest_manga('b', payload('b', 2))[0] != current
"""A later pending read must not suppress a completed decision's fresh cache."""
import asyncio
import threading

import httpx
from fastapi import FastAPI

from app import matching_review as review
from app.read_lane import ReadLane
from test_v0323_matching_review import env, entry, payload, save


def test_later_pending_public_read_cannot_suppress_writer_cache_refresh(env, monkeypatch):
    service, database = env
    group = service.create_source_group('No automatic external joins')['id']
    current = entry(service, 'a')
    save(env, 'group', group, 'external', {'enabled': False, 'checks': {}})
    writer_captured, release_writer = threading.Event(), threading.Event()
    reader_captured, release_reader = threading.Event(), threading.Event()
    original = database.rows

    def paused_rows(rows):
        result = original(rows)
        if result and 'group_priority' in result[0]:
            name = threading.current_thread().name
            if name.startswith('asyncio_') and not writer_captured.is_set():
                writer_captured.set()
                assert release_writer.wait(4)
            elif name.startswith('catalogue-read') and not reader_captured.is_set():
                reader_captured.set()
                assert release_reader.wait(4)
        return result

    monkeypatch.setattr(database, 'rows', paused_rows)
    lane = ReadLane(workers=1)
    monkeypatch.setattr(review, 'read_lane', lane)
    application = FastAPI()
    application.include_router(review.router(database, service, lambda: True))

    async def scenario():
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=application), base_url='http://review') as client:
            writer = asyncio.create_task(asyncio.to_thread(service.update_source, 'a', group_id=group))
            request = None
            try:
                assert await asyncio.to_thread(writer_captured.wait, 2)
                request = asyncio.create_task(client.get('/api/admin/matching-policies'))
                assert await asyncio.to_thread(reader_captured.wait, 2)
                release_writer.set()
                await writer
                assert not request.done()
                # The write has completed and its SQL is current. A pending read
                # must not make the next real source commit ignore that decision.
                matched = await asyncio.to_thread(service.ingest_manga, 'b', payload('b', 2))
            finally:
                release_writer.set()
                release_reader.set()
                await writer
                if request is not None:
                    response = await request
                    assert response.status_code == 200
        await lane.close()
        return matched

    matched = asyncio.run(scenario())
    with database.catalog() as connection:
        assert connection.execute("SELECT group_id FROM source_config WHERE source_id='a'").fetchone()[0] == group
    assert matched[0] != current
