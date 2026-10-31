"""Reader response budgets include admission, catalogue work and disconnections."""
import asyncio
from pathlib import Path
import threading
from unittest.mock import AsyncMock

import httpx
import pytest

from app import main, service as module
from app.config import settings
from app.service import ReaderBrowsePending
from test_v087_recovery import env, entry, source
from test_v039_builder import sdk
from test_v0310_builder import command


def reply(identity=1):
    return {"items": [{"id": identity}], "has_next_page": False, "source_errors": []}


@pytest.mark.asyncio
async def test_cold_reader_deadline_keeps_one_owned_request_for_retries(env, monkeypatch):
    service, _ = env
    service._reader_response_budget = .01
    release = asyncio.Event()
    called = []
    async def federated(mode, **kw):
        if kw.get('cache_only'):
            return None
        called.append((mode, kw))
        await release.wait()
        return reply()
    monkeypatch.setattr(service, 'federated', federated)
    try:
        for _ in range(3):
            with pytest.raises(ReaderBrowsePending):
                await asyncio.wait_for(service.reader_federated('POPULAR', page=1), .2)
        assert len(called) == 1
        tasks = [job for key, job in service._refresh_tasks.items() if key.startswith('__reader-feed__:')]
        assert len(tasks) == 1 and not tasks[0].done()
        release.set()
        assert (await service.reader_federated('POPULAR', page=1))['items'] == [{'id': 1}]
        assert len(called) == 1
    finally:
        release.set()
        await service.cancel_background_tasks()


@pytest.mark.asyncio
async def test_reader_live_capacity_does_not_delay_cached_feed(env, monkeypatch):
    service, _ = env
    service._reader_response_budget = .02
    release = asyncio.Event()
    active = 0
    maximum = 0
    async def federated(mode, **kw):
        nonlocal active, maximum
        if mode == 'LATEST':
            return reply(7)
        if kw.get('cache_only'):
            return None
        active += 1
        maximum = max(maximum, active)
        try:
            await release.wait()
            return reply()
        finally:
            active -= 1
    monkeypatch.setattr(service, 'federated', federated)
    try:
        results = await asyncio.gather(*(service.reader_federated('SEARCH', query=str(i)) for i in range(8)), return_exceptions=True)
        assert all(isinstance(result, ReaderBrowsePending) for result in results)
        assert maximum == 2
        with pytest.raises(ReaderBrowsePending):
            await service.reader_federated('SEARCH', query='over-capacity')
        assert len([key for key in service._refresh_tasks if key.startswith('__reader-feed__:')]) == 8
        # Even a completely full live queue must not block a saved feed.
        assert (await asyncio.wait_for(service.reader_federated('LATEST'), .1))['items'] == [{'id': 7}]
        assert active == 2
    finally:
        release.set()
        await service.cancel_background_tasks()


@pytest.mark.asyncio
async def test_reader_disconnect_does_not_cancel_shared_work_but_reset_drains(env, monkeypatch):
    service, _ = env
    service._reader_response_budget = 10
    entered, cancelled = asyncio.Event(), asyncio.Event()
    async def federated(mode, **kw):
        if kw.get('cache_only'):
            return None
        entered.set()
        try:
            await asyncio.Event().wait()
        finally:
            await asyncio.sleep(.01)
            cancelled.set()
    monkeypatch.setattr(service, 'federated', federated)
    caller = asyncio.create_task(service.reader_federated('POPULAR'))
    await entered.wait()
    caller.cancel()
    with pytest.raises(asyncio.CancelledError):
        await caller
    assert not cancelled.is_set()
    await service.cancel_background_tasks()
    assert cancelled.is_set() and not service._refresh_tasks
    with pytest.raises(ReaderBrowsePending):
        await service.reader_federated('POPULAR')


@pytest.mark.asyncio
async def test_reader_task_work_budget_is_bounded(env, monkeypatch):
    service, _ = env
    service._reader_response_budget = .2
    service._reader_work_budget = .01
    async def federated(*a, **kw):
        await asyncio.Event().wait()
    monkeypatch.setattr(service, 'federated', federated)
    with pytest.raises(ReaderBrowsePending):
        await service.reader_federated('POPULAR')
    await asyncio.sleep(0)
    assert not service._refresh_tasks


@pytest.mark.asyncio
async def test_saved_feed_returns_even_while_metadata_planning_is_blocked(env, monkeypatch):
    service, database = env
    source(database, 'source-a')
    eid = entry(service, 'source-a', 1)
    key = service._browse_cache_key('POPULAR', 1, '', None, 1, None, 'relevance')
    service._save_persistent_browse_cache(key, 'POPULAR', 1, '', None, 1, None, 'relevance', reply(eid))
    planning, release = threading.Event(), threading.Event()
    def plan(*a):
        planning.set()
        assert release.wait(3)
        return [], []
    monkeypatch.setattr(service, '_plan_metadata_enrichment', plan)
    monkeypatch.setattr(module.source_host, 'fetch_source_manga', AsyncMock(side_effect=AssertionError('cached feed must not fetch sources')))
    try:
        response = await asyncio.wait_for(service.reader_federated('POPULAR'), .3)
        assert [item['id'] for item in response['items']] == [eid]
        assert response['persistent_cache']
        while not planning.is_set():
            await asyncio.sleep(.001)
        assert (await asyncio.wait_for(service.reader_federated('POPULAR'), .3))['items'][0]['id'] == eid
        assert not release.is_set()
    finally:
        release.set()
        await service.cancel_background_tasks()


@pytest.mark.asyncio
async def test_source_sync_commit_cannot_block_reader_deadline_and_reset_waits(env, monkeypatch):
    service, _ = env
    service._reader_response_budget = .01
    entered, release = threading.Event(), threading.Event()
    monkeypatch.setattr(module.source_host, 'sources', AsyncMock(return_value=[]))
    def commit(sources):
        entered.set()
        assert release.wait(3)
        return []
    monkeypatch.setattr(service, '_persist_source_snapshot', commit)
    async def federated(*a, **kw):
        if kw.get('cache_only'):
            return None
        await service.sync_sources()
        return reply()
    monkeypatch.setattr(service, 'federated', federated)
    try:
        with pytest.raises(ReaderBrowsePending):
            await asyncio.wait_for(service.reader_federated('POPULAR'), .3)
        assert entered.is_set()
        drain = asyncio.create_task(service.cancel_background_tasks())
        await asyncio.sleep(.02)
        assert not drain.done()
        release.set()
        await drain
        assert not service._refresh_tasks
    finally:
        release.set()
        await service.cancel_background_tasks()


@pytest.mark.asyncio
async def test_reader_http_reports_retryable_preparation_instead_of_empty_page(env, monkeypatch):
    service, _ = env
    monkeypatch.setattr(main, 'service', service)
    monkeypatch.setattr(service, 'reader_federated', AsyncMock(side_effect=ReaderBrowsePending()))
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url='http://test') as client:
        for path in ('/api/v1/browse/popular', '/api/v1/browse/latest', '/api/v1/search?q=some-title'):
            response = await client.get(path)
            assert response.status_code == 503
            assert response.headers['retry-after'] == '3'
            assert response.headers['x-autosourcarr-reader-state'] == 'preparing'
            assert 'preparing' in response.json()['detail']


def test_builder_warns_on_published_port_without_rewriting_custom_listener(sdk):
    _, configs, _ = sdk
    path = configs[1] / 'autosourcarr.env'
    path.write_text(path.read_text().replace('private-autosourcarr:8282', 'private-autosourcarr:8283'))
    result = command(sdk, '--both', '--check')
    assert result.returncode == 0, result.stdout + result.stderr
    assert 'port other than 8282' in result.stderr
    assert 'private-autosourcarr:8283' in result.stdout
    assert 'b' * 64 not in result.stdout + result.stderr


@pytest.mark.asyncio
async def test_reset_notifies_connected_reader_without_aborting_its_http_request(env, monkeypatch):
    service, _ = env
    service._reader_response_budget = 10
    entered = asyncio.Event()
    async def federated(*a, **kw):
        entered.set()
        await asyncio.Event().wait()
    monkeypatch.setattr(service, 'federated', federated)
    caller = asyncio.create_task(service.reader_federated('POPULAR'))
    await entered.wait()
    await service.cancel_background_tasks()
    with pytest.raises(ReaderBrowsePending):
        await caller


@pytest.mark.asyncio
async def test_reader_cache_admission_is_bounded_coalesces_and_resets(env, monkeypatch):
    service, _ = env
    service._reader_response_budget = .01
    service._reader_admission_limit = 4
    entered, finished = [], []
    release = asyncio.Event()
    async def federated(mode, **kw):
        assert kw.get('cache_only'), 'No live source call is allowed before cache lookup finishes'
        entered.append(kw['query'])
        try:
            await release.wait()
            return reply()
        finally:
            finished.append(kw['query'])
    monkeypatch.setattr(service, 'federated', federated)
    results = await asyncio.gather(*(service.reader_federated('SEARCH', query=str(i)) for i in range(12)), return_exceptions=True)
    assert all(isinstance(result, ReaderBrowsePending) for result in results)
    assert len(entered) == 4
    assert len([key for key in service._refresh_tasks if key.startswith('__reader-feed__:')]) == 4
    # Same-key callers reuse an admitted cache job even when all slots are full.
    with pytest.raises(ReaderBrowsePending):
        await service.reader_federated('SEARCH', query=entered[0])
    assert len(entered) == 4
    await service.cancel_background_tasks()
    assert sorted(entered) == sorted(finished)
    assert not service._refresh_tasks and not service._reader_live_keys
    service.resume_background_tasks()
    release.set()
    assert (await service.reader_federated('SEARCH', query='after-reset'))['items'] == [{'id': 1}]
    await service.cancel_background_tasks()
