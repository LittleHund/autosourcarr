"""Cold reader pages make bounded forward progress without repeating source work."""
import asyncio
from unittest.mock import AsyncMock

import pytest

from app import service as module
from app.service import ReaderBrowsePending
from test_v087_recovery import entry, manga, source
from test_v0312_reader import quiet_reader, env


def feed(items):
    return {'mangas': items, 'hasNextPage': True}


@pytest.mark.asyncio
async def test_reader_known_page_uses_current_identity_without_rewriting_every_record(quiet_reader, monkeypatch):
    service, database = quiet_reader
    source(database, 'a')
    eid = entry(service, 'a', 31401, 'Original work')
    with database.catalog() as c:
        c.execute("UPDATE entries SET preferred_title='My manual title',manual_preferred_title=1 WHERE id=?", (eid,))
        before = dict(c.execute('SELECT * FROM source_matches WHERE entry_id=?', (eid,)).fetchone())
    monkeypatch.setattr(module.source_host, 'fetch_source_manga', AsyncMock(return_value=feed([manga(31401, 'Original work')])) )
    ingest = AsyncMock(side_effect=AssertionError('Known reader records must not be rewritten'))
    monkeypatch.setattr(service, '_ingest_import_manga', ingest)
    refresh = []
    monkeypatch.setattr(service, '_schedule_federated_refresh', lambda *a: refresh.append(a) or True)
    try:
        result = await service.reader_federated('POPULAR')
        assert result['items'][0]['id'] == eid
        assert result['items'][0]['preferred_title'] == 'My manual title'
        assert result['has_next_page'] and refresh
        ingest.assert_not_called()
        with database.catalog() as c:
            assert dict(c.execute('SELECT * FROM source_matches WHERE entry_id=?', (eid,)).fetchone()) == before
    finally:
        await service.cancel_background_tasks()


@pytest.mark.asyncio
async def test_cold_work_slices_resume_without_refetch_or_restarting_completed_ingest(quiet_reader, monkeypatch):
    service, database = quiet_reader
    source(database, 'a')
    service._reader_work_budget = .065
    service._reader_response_budget = .3
    cards = [manga(31410+i, title) for i,title in enumerate(('Sunflowers', 'Volcanoes', 'Jupiter', 'Carpenters', 'Boats', 'Forests', 'Metropolis', 'Robotics'))]
    fetch = AsyncMock(return_value=feed(cards))
    monkeypatch.setattr(module.source_host, 'fetch_source_manga', fetch)
    original = service._ingest_import_manga
    completed = []
    original_sync = service.ingest_manga
    def committed(sid, item):
        result = original_sync(sid, item)
        # A deadline can cancel the awaiting coroutine after its owned DB thread
        # committed. Observe the commit itself, not the cancelled return callback.
        completed.append(item['id'])
        return result
    monkeypatch.setattr(service, 'ingest_manga', committed)
    async def slow(sid, item, **kwargs):
        await asyncio.sleep(.025)
        return await original(sid, item, **kwargs)
    monkeypatch.setattr(service, '_ingest_import_manga', slow)
    result = None
    try:
        for attempt in range(12):
            try:
                result = await service.reader_federated('POPULAR')
                break
            except ReaderBrowsePending:
                await asyncio.sleep(0)
        assert result is not None, 'A large feed must eventually complete rather than restart from the first title'
        assert len(result['items']) == len(cards) and result['has_next_page']
        assert fetch.await_count == 1
        assert len(completed) == len(set(completed)) == len(cards)
        await asyncio.sleep(0)
        assert not service._reader_feed_checkpoints
        assert (await service.reader_federated('POPULAR'))['persistent_cache']
        assert fetch.await_count == 1
    finally:
        await service.cancel_background_tasks()


@pytest.mark.asyncio
async def test_resumed_feed_rechecks_manual_ownership_and_deleted_records(quiet_reader, monkeypatch):
    service, database = quiet_reader
    source(database, 'a')
    first = entry(service, 'a', 31430, 'First')
    second = entry(service, 'a', 31431, 'Second')
    moved = entry(service, 'b', 31432, 'Moved target')
    cards = [manga(31430, 'First'), manga(31431, 'Second')]
    rows = [(dict(source_id='a', name='A', priority=10), feed(cards), None)]
    checkpoint = {}
    result = await service._reader_ingest_feed(rows, checkpoint)
    assert set(result[0]) == {first, second}
    with database.catalog() as c:
        c.execute("INSERT INTO deleted_source_records(source_id,source_manga_id,deleted_at) VALUES('a',31430,'now')")
        c.execute("UPDATE source_matches SET entry_id=? WHERE source_id='a' AND source_manga_id=31431", (moved,))
    ingest = AsyncMock(side_effect=AssertionError('Completed records must not reattach old ownership'))
    monkeypatch.setattr(service, '_ingest_import_manga', ingest)
    result = await service._reader_ingest_feed(rows, checkpoint)
    assert set(result[0]) == {moved}
    ingest.assert_not_called()


def test_known_lookup_includes_active_variants_but_not_tombstones_or_inactive(quiet_reader):
    service, database = quiet_reader
    source(database, 'a')
    eid = entry(service, 'a', 31440, 'Work')
    service._upsert_source_variant(eid, 'a', manga(31441, 'Work'), 1, 'manual')
    service._upsert_source_variant(eid, 'a', manga(31442, 'Work'), 1, 'manual')
    with database.catalog() as c:
        c.execute('UPDATE source_variants SET active=0 WHERE source_manga_id=31442')
        c.execute("INSERT INTO deleted_source_records(source_id,source_manga_id,deleted_at) VALUES('a',31440,'now')")
    result = service._known_feed_records([(dict(source_id='a'), 0, manga(mid)) for mid in (31440,31441,31442)])
    assert result == {('a',31440): None, ('a',31441): eid}


@pytest.mark.asyncio
async def test_revalidation_still_updates_known_metadata(quiet_reader, monkeypatch):
    service, database = quiet_reader
    source(database, 'a')
    eid = entry(service, 'a', 31450, 'Stable work')
    fetched = manga(31450, 'Stable work', description='A wandering cartographer reaches an isolated mountain village and discovers that its maps change overnight. Together with the local librarian, she follows the shifting paths to uncover the history of the families who vanished from the valley.')
    monkeypatch.setattr(module.source_host, 'fetch_source_manga', AsyncMock(return_value=feed([fetched])))
    try:
        result = await service.federated('POPULAR', force_live=True)
        with database.catalog() as c:
            assert c.execute('SELECT description FROM source_matches WHERE entry_id=?', (eid,)).fetchone()['description'] == fetched['description']
        assert result['items'][0]['id'] == eid
    finally:
        await service.cancel_background_tasks()


@pytest.mark.asyncio
async def test_checkpoint_retention_is_bounded_and_reset_clears_it(quiet_reader):
    service, _ = quiet_reader
    service._reader_pending_limit = 2
    first = service._reader_feed_checkpoint('first')
    service._reader_live_keys.add('first')
    service._reader_feed_checkpoint('second')
    service._reader_feed_checkpoint('third')
    assert set(service._reader_feed_checkpoints) == {'first','third'}
    assert service._reader_feed_checkpoints['first'] is first
    service._reader_live_keys.add('third')
    with pytest.raises(ReaderBrowsePending):
        service._reader_feed_checkpoint('fourth')
    await service.cancel_background_tasks()
    assert not service._reader_feed_checkpoints


@pytest.mark.asyncio
async def test_capacity_deferral_does_not_discard_saved_reader_progress(quiet_reader, monkeypatch):
    service, _ = quiet_reader
    key = '__reader-feed__:' + service._browse_cache_key('POPULAR', 1, '', None, 1, None, 'relevance')
    checkpoint = service._reader_feed_checkpoint(key)
    checkpoint['next_record'] = 17
    service._reader_live_keys.update(str(i) for i in range(service._reader_pending_limit))
    monkeypatch.setattr(service, 'federated', AsyncMock(return_value=None))
    with pytest.raises(ReaderBrowsePending):
        await service.reader_federated('POPULAR')
    await asyncio.sleep(0)
    assert service._reader_feed_checkpoints[key] is checkpoint
    assert checkpoint['next_record'] == 17
    await service.cancel_background_tasks()


def test_expired_idle_checkpoints_are_removed_but_running_ones_survive(quiet_reader):
    service, _ = quiet_reader
    service._reader_feed_checkpoint('idle')['last_used'] -= 901
    service._reader_feed_checkpoint('running')['last_used'] -= 901
    service._reader_live_keys.add('running')
    service._reader_feed_checkpoint('new')
    assert set(service._reader_feed_checkpoints) == {'running', 'new'}
