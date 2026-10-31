"""Actual federation and SQLite regressions for reader browsing and filters."""
import asyncio
from unittest.mock import AsyncMock

import httpx
import pytest

from app import main, service as module
from app.config import settings
from test_v087_recovery import env, entry, manga, source


@pytest.fixture
def quiet_reader(env, monkeypatch):
    service, database = env
    for name in ('schedule_source_metadata_hydration', 'schedule_entry_identity',
                 'schedule_duplicate_snapshot_refresh', 'schedule_translation_enrichment',
                 'schedule_missing_source_discovery', 'schedule_additional_record_discovery',
                 '_queue_feed_metadata_enrichment', '_schedule_federated_refresh'):
        monkeypatch.setattr(service, name, lambda *a, **kw: False)
    return service, database


@pytest.mark.asyncio
async def test_default_reader_budget_leaves_time_after_full_source_batch(quiet_reader, monkeypatch):
    service, database = quiet_reader
    for sid in range(5):
        source(database, str(sid))
    eid = entry(service, '0', 301201, 'Sunflower Adventure')
    monkeypatch.setattr(settings, 'max_source_concurrency', 2)
    monkeypatch.setattr(settings, 'interactive_source_timeout', 4.0)
    monkeypatch.setattr(settings, 'interactive_overall_timeout', 6.0)
    calls = []
    async def fetch(sid, mode, page, query):
        calls.append((sid, mode, page, query))
        if sid != '0':
            await asyncio.sleep(20)
        return {'mangas': [manga(301201, 'Sunflower Adventure')], 'hasNextPage': True}
    monkeypatch.setattr(module.source_host, 'fetch_source_manga', fetch)
    try:
        # Real bounded_map + ingestion + cache write. The 0.3.11 six-second
        # reader deadline rejects this before the six-second source batch ends.
        result = await asyncio.wait_for(service.reader_federated('POPULAR'), 9)
        assert [x['id'] for x in result['items']] == [eid]
        assert result['has_next_page'] is True
        assert len(calls) == 5
        cached = await asyncio.wait_for(service.reader_federated('POPULAR'), .5)
        assert cached['persistent_cache'] and [x['id'] for x in cached['items']] == [eid]
        assert len(calls) == 5
    finally:
        await service.cancel_background_tasks()


@pytest.mark.asyncio
async def test_pending_http_poll_reuses_real_federation(quiet_reader, monkeypatch):
    service, database = quiet_reader
    source(database, '1')
    entry(service, '1', 301202, 'Sunflower Journey')
    service._reader_response_budget = .01
    monkeypatch.setattr(main, 'service', service)
    calls = []
    async def fetch(*args):
        calls.append(args)
        await asyncio.sleep(.07)
        return {'mangas': [manga(301202, 'Sunflower Journey')], 'hasNextPage': False}
    monkeypatch.setattr(module.source_host, 'fetch_source_manga', fetch)
    try:
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url='http://test') as client:
            first = await client.get('/api/v1/browse/popular')
            assert first.status_code == 503
            assert first.headers['x-autosourcarr-reader-state'] == 'preparing'
            second = await client.get('/api/v1/browse/popular')
            assert second.status_code == 503
            tasks = [t for key, t in service._refresh_tasks.items() if key.startswith('__reader-feed__:')]
            await asyncio.gather(*tasks)
            third = await client.get('/api/v1/browse/popular')
            assert third.status_code == 200 and third.json()['items']
        assert len(calls) == 1
    finally:
        await service.cancel_background_tasks()


@pytest.mark.asyncio
@pytest.mark.parametrize('force_live', [False, True])
async def test_partial_title_search_cannot_leak_unselected_source(quiet_reader, monkeypatch, force_live):
    service, database = quiet_reader
    source(database, 'source-a'); source(database, 'source-b')
    entry(service, 'source-a', 301203, 'Alpha Centauri')
    selected = entry(service, 'source-b', 301204, 'Alpha Andromeda')
    monkeypatch.setattr(module.source_host, 'fetch_source_manga', AsyncMock(return_value={'mangas': [], 'hasNextPage': False}))
    try:
        result = await service.federated('SEARCH', query='Alpha', source_ids=['source-b'], min_matches=0, force_live=force_live)
        assert [row['id'] for row in result['items']] == [selected]
    finally:
        await service.cancel_background_tasks()


@pytest.mark.asyncio
async def test_unselected_local_title_is_not_returned_when_selected_source_has_no_hit(quiet_reader, monkeypatch):
    service, database = quiet_reader
    source(database, 'source-a'); source(database, 'source-b')
    entry(service, 'source-a', 301205, 'Alpha Centauri')
    fetch = AsyncMock(return_value={'mangas': [], 'hasNextPage': False})
    monkeypatch.setattr(module.source_host, 'fetch_source_manga', fetch)
    try:
        result = await service.federated('SEARCH', query='Alpha', source_ids=['source-b'], min_matches=0)
        assert result['items'] == []
        assert fetch.call_args.args[0] == 'source-b'
    finally:
        await service.cancel_background_tasks()


def test_source_scope_is_applied_before_shortlist_limit_and_keeps_variants(quiet_reader):
    service, database = quiet_reader
    source(database, 'source-a'); source(database, '6084907896154116083')
    for mid in range(5): entry(service, 'source-a', 301210+mid, f'Alpha unrelated {mid}')
    selected = entry(service, 'source-a', 301220, 'Alpha selected')
    service._upsert_source_variant(selected, '6084907896154116083', manga(301221, 'Alpha selected'), 1.0, 'manual')
    with database.catalog() as c:
        c.execute("UPDATE entries SET updated_at='2000-01-01' WHERE id=?", (selected,))
    assert service._search_entry_ids('Alpha', 1, source_ids=['6084907896154116083']) == [selected]
    with database.catalog() as c:
        c.execute('UPDATE source_variants SET active=0 WHERE entry_id=?', (selected,))
    assert service._search_entry_ids('Alpha', 1, source_ids=['6084907896154116083']) == []


def test_previously_polluted_saved_feed_is_scoped_before_page_limit(quiet_reader):
    service, database = quiet_reader
    source(database, 'source-a'); source(database, 'source-b')
    wrong = entry(service, 'source-a', 301222, 'Alpha wrong')
    right = entry(service, 'source-b', 301223, 'Alpha right')
    key = service._browse_cache_key('SEARCH', 1, 'Alpha', ['source-b'], 0, None, 'title', 1)
    service._save_persistent_browse_cache(key, 'SEARCH', 1, 'Alpha', ['source-b'], 0, None, 'title',
                                        {'items': [{'id': wrong}, {'id': right}], 'has_next_page': False})
    result = service._load_persistent_browse_cache(key)
    assert [row['id'] for row in result['items']] == [right]


def test_filtered_local_search_honors_title_sort(quiet_reader):
    service, database = quiet_reader
    source(database, '1'); source(database, '2')
    first = entry(service, '1', 301224, 'Alpha Aardvark')
    last = entry(service, '1', 301225, 'Alpha Zebra')
    service._upsert_match(last, '2', manga(301226, 'Alpha Zebra'), 1.0)
    result = service._catalog_fallback('SEARCH', 'Alpha', 0, None, 'title', 50, ['1'])
    assert [row['id'] for row in result['items']] == [first, last]
    assert [row['id'] for row in service._catalog_fallback('SEARCH', 'Alpha', 0, None, 'sources', 50, ['1'])['items']] == [last, first]


@pytest.mark.parametrize('min_count,exact_count', [(0, 1), (2, None)])
def test_saved_larger_page_applies_changed_match_counts_before_truncation(quiet_reader, min_count, exact_count):
    service, database = quiet_reader
    source(database, 'source-a'); source(database, 'source-b')
    first = entry(service, 'source-a', 301228, 'First cached work')
    second = entry(service, 'source-a', 301229, 'Second cached work')
    if exact_count is None:
        service._upsert_match(first, 'source-b', manga(301230, 'First cached work'), 1.0)
        service._upsert_match(second, 'source-b', manga(301231, 'Second cached work'), 1.0)
    large_key = service._browse_cache_key('POPULAR', 1, '', None, min_count, exact_count, 'relevance', 2)
    small_key = service._browse_cache_key('POPULAR', 1, '', None, min_count, exact_count, 'relevance', 1)
    service._save_persistent_browse_cache(large_key, 'POPULAR', 1, '', None, min_count, exact_count, 'relevance',
                                        {'items': [{'id': first}, {'id': second}], 'has_next_page': True})
    # Both entries qualified when saved. The first now gains a source (exact
    # count) or loses one (minimum); the second must still fill the smaller page.
    if exact_count is not None:
        service._upsert_match(first, 'source-b', manga(301230, 'First cached work'), 1.0)
    else:
        with database.catalog() as c:
            c.execute("UPDATE source_matches SET active=0 WHERE entry_id=? AND source_id='source-b'", (first,))
    result = service._load_persistent_browse_cache(small_key)
    assert [row['id'] for row in result['items']] == [second]
    assert result['cache_size_mismatch'] and result['has_next_page']


@pytest.mark.asyncio
async def test_blank_filter_search_uses_popular_and_preserves_filters(quiet_reader, monkeypatch):
    service, database = quiet_reader
    source(database, '6084907896154116083')
    eid = entry(service, '6084907896154116083', 301227, 'Filtered work')
    fetch = AsyncMock(return_value={'mangas': [manga(301227, 'Filtered work')], 'hasNextPage': True})
    monkeypatch.setattr(module.source_host, 'fetch_source_manga', fetch)
    try:
        result = await service.reader_federated('SEARCH', query='  ', page=2, source_ids=['6084907896154116083'],
                                                min_matches=0, exact_match_count=1, sort='title')
        assert [row['id'] for row in result['items']] == [eid]
        assert fetch.call_args.args == ('6084907896154116083', 'POPULAR', 2, '')
        assert result['has_next_page'] is True
    finally:
        await service.cancel_background_tasks()


@pytest.mark.asyncio
@pytest.mark.parametrize('query', ['__autosourcarr_no_enabled_language__', '__autosourcarr_no_matching_source__'])
async def test_empty_language_selection_never_queries_websites(quiet_reader, monkeypatch, query):
    service, _ = quiet_reader
    monkeypatch.setattr(module.source_host, 'fetch_source_manga', AsyncMock(side_effect=AssertionError('No source selected')))
    assert (await service.reader_federated('SEARCH', query=query, exact_match_count=999999))['items'] == []
    assert not service._refresh_tasks
