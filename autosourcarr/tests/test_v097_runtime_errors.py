"""Shared-runtime deferrals preserve caches, source health, and useful diagnostics."""
import asyncio
from unittest.mock import AsyncMock, Mock

import httpx
import pytest
from fastapi import HTTPException

from app import main, service as module
from app.config import settings
from app.error_registry import ErrorRegistry
from app.source_engine import source_engine
from app.suwayomi import SourceEngineUnavailable, SuwayomiClient
from test_v087_recovery import env
from test_v091_pages import chapter_fixture


def test_root_diagnostic_survives_flood_and_reopens_without_stale_resolution():
    registry = ErrorRegistry()
    root = registry.record('Engine', 'Missing runtime', key='engine', pinned=True)
    for chapter in range(146):
        for _ in range(3):
            registry.record('Pages', 'Failed', key=str(chapter))
    assert registry.snapshot(limit=1)['items'][0]['id'] == root
    assert registry.snapshot(limit=100)['history_count'] == 100
    registry.resolve(key='engine')
    registry.record('Engine', 'Missing runtime again', key='engine')
    item = registry.snapshot(limit=1)['items'][0]
    assert item['id'] == root and not item['resolved'] and 'resolved_at' not in item


@pytest.mark.parametrize('state', ['setup-required', 'starting', 'restarting', 'unresponsive'])
def test_engine_guard_skips_network_but_allows_external_images_and_recovers(monkeypatch, state):
    monkeypatch.setattr(settings, 'source_engine_mode', 'integrated')
    monkeypatch.setattr(source_engine, 'state', state)
    monkeypatch.setattr(source_engine, 'error', 'Runtime unavailable')
    recorder = Mock()
    monkeypatch.setattr(source_engine, 'record_unavailable', recorder)
    async def scenario():
        client = SuwayomiClient('http://127.0.0.1:4567', managed_engine=True)
        calls = []
        async def send(request):
            calls.append(str(request.url))
            return httpx.Response(200, json={'data': {'ok': True}})
        await client.client.aclose()
        client.client = httpx.AsyncClient(transport=httpx.MockTransport(send), event_hooks={'request': [client._check_engine_request]})
        try:
            for _ in range(10):
                with pytest.raises(SourceEngineUnavailable) as caught:
                    await client.graphql('query { health }')
                assert caught.value.local_source_deferral
            assert calls == [] and recorder.call_count == 10
            assert (await client.client.get('https://images.example/cover.jpg')).is_success
            monkeypatch.setattr(source_engine, 'state', 'ready')
            assert await client.graphql('query { health }') == {'ok': True}
            assert len(calls) == 2
        finally:
            await client.close()
    asyncio.run(scenario())


def test_local_deferral_is_preserved_and_stale_pages_are_served(env, monkeypatch):
    s, database, vid = chapter_fixture(env, monkeypatch, fallback=True)
    fetch = AsyncMock(return_value=['https://images.example/1.jpg'])
    monkeypatch.setattr(module.source_host, 'fetch_chapter_pages', fetch)
    async def scenario():
        result = await s.resolve_pages(vid)
        assert result['pages']
        fetch.side_effect = SourceEngineUnavailable('Runtime missing', retry_after=30)
        # Fresh cached maps bypass the unavailable runtime completely.
        assert (await s.resolve_pages(vid))['page_cache_hit']
        assert fetch.await_count == 1
        with database.origins() as db:
            db.execute("UPDATE candidate_pages SET updated_at='2000-01-01T00:00:00+00:00'")
        result = await s.resolve_pages(vid)
        assert result['page_cache_stale'] and result['source_warning_deferred']
        assert fetch.await_count == 2, 'Do not try each candidate against the same missing runtime'
        with database.origins() as db:
            db.execute('DELETE FROM candidate_pages')
        with pytest.raises(SourceEngineUnavailable):
            await s.resolve_pages(vid)
        assert not s._page_lookup_failures, 'No website cooldown from local runtime absence'
    asyncio.run(scenario())


def test_reader_503_has_retry_after_and_does_not_add_a_chapter_error(monkeypatch):
    monkeypatch.setattr(main.service, 'resolve_pages', AsyncMock(side_effect=SourceEngineUnavailable('Runtime missing', retry_after=30)))
    registry = ErrorRegistry()
    monkeypatch.setattr(main, 'error_registry', registry)
    async def scenario():
        with pytest.raises(HTTPException) as caught:
            await main.chapter_pages(1, None)
        assert caught.value.status_code == 503 and caught.value.headers == {'Retry-After': '30'}
        assert registry.snapshot()['history_count'] == 0
    asyncio.run(scenario())


def test_external_page_fallback_does_not_persist_failed_mapping(monkeypatch):
    monkeypatch.setattr(main.service, 'page_fetch_info', lambda *args: {'source_chapter_index': -1, 'source_manga_id': 1, 'source_chapter_id': 2, 'private_url': 'https://images.example/1.jpg'})
    save = Mock()
    monkeypatch.setattr(main.service, 'set_candidate_chapter_index', save)
    monkeypatch.setattr(main.source_host, 'chapter_index_for_id', AsyncMock(side_effect=SourceEngineUnavailable('Missing')))
    upstream = httpx.Response(200, content=b'image', headers={'content-type': 'image/jpeg'})
    binary = AsyncMock(return_value=upstream)
    monkeypatch.setattr(main.source_host, 'open_binary_stream', binary)
    async def scenario():
        result = await main.proxy_page(1, 0)
        assert result.status_code == 200
        save.assert_not_called()
        binary.assert_awaited_once_with('https://images.example/1.jpg')
        await upstream.aclose()
    asyncio.run(scenario())


def test_known_runtime_outage_does_not_wait_behind_busy_source_lane(monkeypatch):
    monkeypatch.setattr(settings, 'source_engine_mode', 'integrated')
    monkeypatch.setattr(source_engine, 'state', 'setup-required')
    monkeypatch.setattr(source_engine, 'error', 'Missing runtime')
    monkeypatch.setattr(source_engine, 'record_unavailable', Mock())
    async def scenario():
        client = SuwayomiClient('http://127.0.0.1:4567', work_limit=1, managed_engine=True)
        total, _ = client._work_semaphores()
        await total.acquire()
        try:
            with pytest.raises(SourceEngineUnavailable):
                await asyncio.wait_for(client.fetch_source_manga('one', 'SEARCH'), .2)
        finally:
            total.release()
            await client.close()
    asyncio.run(scenario())


def test_source_inventory_preserves_local_deferral_without_schema_retry(monkeypatch):
    async def scenario():
        client = SuwayomiClient('http://source.invalid')
        query = AsyncMock(side_effect=SourceEngineUnavailable('Missing'))
        monkeypatch.setattr(client, 'graphql', query)
        try:
            with pytest.raises(SourceEngineUnavailable):
                await client.sources()
            assert query.await_count == 1
        finally:
            await client.close()
    asyncio.run(scenario())
