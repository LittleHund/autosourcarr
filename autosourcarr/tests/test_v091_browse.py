"""Slow catalogue steps must not freeze independent interactive requests."""
import asyncio
import importlib
import threading

import pytest

from app.service import AutoSourcarrService

module = importlib.import_module('app.service')


def prepared_service(monkeypatch):
    service = AutoSourcarrService()
    item = {'id': 1, 'canonical_id': 1, 'preferred_title': 'Example', 'match_count': 1}
    source = {'source_id': 'test', 'name': 'Test', 'priority': 1, 'enabled_language': True,
              'enabled_search': True, 'supports_latest': True}
    monkeypatch.setattr(service, '_load_persistent_browse_cache', lambda *a: None)
    monkeypatch.setattr(service, '_exact_search_entry_ids', lambda *a: [])
    monkeypatch.setattr(service, '_catalog_fallback', lambda *a: {'items': []})
    monkeypatch.setattr(service, '_search_entry_ids', lambda *a: [])
    monkeypatch.setattr(service, '_entry_titles', lambda *a: ['Example'])
    monkeypatch.setattr(service, '_entry_summary', lambda *a: dict(item))
    monkeypatch.setattr(service, '_entry_summaries', lambda ids: [dict(item)] if ids else [])
    monkeypatch.setattr(service, 'list_sources', lambda: [source])
    monkeypatch.setattr(service, 'ingest_manga', lambda *a: (1, 1.0, 'known-source-id'))
    monkeypatch.setattr(service, '_manga_metadata_is_sparse', lambda *a: False)
    monkeypatch.setattr(service, '_identity_links', lambda *a: [{'id': 1}])
    monkeypatch.setattr(service, 'remember_cover_hint', lambda *a: None)
    monkeypatch.setattr(service, '_save_persistent_browse_cache', lambda *a: None)
    for name in ('schedule_metadata_enrichment', 'schedule_translation_enrichment',
                 'schedule_missing_source_discovery', 'schedule_additional_record_discovery',
                 'schedule_duplicate_snapshot_refresh', 'schedule_source_metadata_hydration',
                 'schedule_entry_identity', 'schedule_automatic_duplicate_matching', '_schedule_federated_refresh'):
        monkeypatch.setattr(service, name, lambda *a, **kw: False)
    async def fetch(*args):
        return {'mangas': [{'id': 7, 'title': 'Example'}], 'hasNextPage': False}
    monkeypatch.setattr(module.source_host, 'fetch_source_manga', fetch)
    return service, item


@pytest.mark.parametrize('phase', ['persistent', 'exact_ids', 'exact_cards', 'fallback', 'ingest', 'live_cards', 'save'])
def test_catalogue_steps_leave_loop_available(monkeypatch, phase):
    service, item = prepared_service(monkeypatch)
    started, release, finished = threading.Event(), threading.Event(), threading.Event()
    # A separate request can release this controlled slow dependency only while
    # the event loop is responsive. On the old code it resumes after the timeout.
    def slow(value):
        def call(*args, **kwargs):
            started.set()
            release.wait(.3)
            finished.set()
            return value
        return call
    mode, query = 'POPULAR', ''
    if phase == 'persistent':
        payload = {'items': [dict(item)], 'cache_age_seconds': 0}
        monkeypatch.setattr(service, '_load_persistent_browse_cache', slow(payload))
    elif phase in {'exact_ids', 'exact_cards'}:
        mode, query = 'SEARCH', 'Example'
        monkeypatch.setattr(service, '_exact_search_entry_ids', slow([1]) if phase == 'exact_ids' else lambda *a: [1])
        if phase == 'exact_cards': monkeypatch.setattr(service, '_entry_summaries', slow([dict(item)]))
    elif phase == 'fallback':
        mode, query = 'SEARCH', 'Example'
        monkeypatch.setattr(service, '_catalog_fallback', slow({'items': [dict(item)]}))
    elif phase == 'ingest':
        monkeypatch.setattr(service, 'ingest_manga', slow((1, 1.0, 'known-source-id')))
    elif phase == 'live_cards':
        monkeypatch.setattr(service, '_entry_summaries', slow([dict(item)]))
    elif phase == 'save':
        monkeypatch.setattr(service, '_save_persistent_browse_cache', slow(None))

    async def scenario():
        async def independent_request():
            while not started.is_set(): await asyncio.sleep(.001)
            loop_was_available = not finished.is_set()
            release.set()
            return loop_was_available
        independent = asyncio.create_task(independent_request())
        result = await service.federated(mode, query=query, page_size=500)
        assert result['items'][0]['canonical_id'] == 1
        assert await independent, f'{phase} blocked the event loop until its work finished'
    try: asyncio.run(scenario())
    finally: release.set()


def test_browse_ingestion_keeps_followups_on_loop_without_promoting_idle_work(monkeypatch):
    service, _ = prepared_service(monkeypatch)
    calls = []
    def record(name):
        def call(*args, **kwargs):
            asyncio.get_running_loop()  # fail if a follow-up is scheduled by a worker
            calls.append((name, args, kwargs))
        return call
    monkeypatch.setattr(service, 'ingest_manga', lambda *a: (1, .99, 'ambiguous-local-candidates:new-entry'))
    monkeypatch.setattr(service, '_identity_links', lambda *a: [])
    monkeypatch.setattr(module.settings, 'metadata_identity_on_ingest', True)
    monkeypatch.setattr(module.settings, 'automatic_duplicate_matching', True)
    for name in ('schedule_source_metadata_hydration', 'schedule_entry_identity', 'schedule_automatic_duplicate_matching'):
        monkeypatch.setattr(service, name, record(name))
    asyncio.run(service.federated('POPULAR'))
    assert {row[0] for row in calls} == {'schedule_source_metadata_hydration', 'schedule_entry_identity', 'schedule_automatic_duplicate_matching'}
    hydration = next(row for row in calls if row[0] == 'schedule_source_metadata_hydration')
    assert hydration[2]['eager'] is False


def test_cancelled_browse_drains_ingest_before_releasing_shared_lane(monkeypatch):
    service, _ = prepared_service(monkeypatch)
    started, release, finished = threading.Event(), threading.Event(), threading.Event()
    def ingest(*args):
        started.set()
        release.wait(2)
        finished.set()
        return 1, 1.0, 'known-source-id'
    monkeypatch.setattr(service, 'ingest_manga', ingest)
    async def scenario():
        request = asyncio.create_task(service.federated('POPULAR'))
        while not started.is_set(): await asyncio.sleep(.001)
        request.cancel()
        await asyncio.sleep(.01)
        assert not request.done(), 'A cancelled response must still own its actual database writer'
        assert service._import_ingest_lock.locked()
        release.set()
        with pytest.raises(asyncio.CancelledError): await request
        assert finished.is_set() and not service._import_ingest_lock.locked()
    try: asyncio.run(scenario())
    finally: release.set()
