"""Split journal and actual cached-reader membership regressions."""
import asyncio
import sqlite3
from unittest.mock import AsyncMock

import pytest

from app import service as service_module, tag_store
from app.db import Database
from app.service import AutoSourcarrService
from test_v0326_storage import storage_env, chapter_origin
from test_v0323_matching_review import env, entry, payload


def attached_chapter(service, database):
    original = entry(service, 'a', 11)
    service._upsert_match(original, 'b', payload('b', 12), 1)
    candidate = chapter_origin(service, database, original, 'b', 12, 112)
    return original, candidate


def fail_split_origin(database):
    with database.origins() as o:
        o.execute("CREATE TRIGGER fail_split_origin BEFORE UPDATE OF active ON chapter_candidates WHEN NEW.active=0 BEGIN SELECT RAISE(ABORT,'origin split failure'); END")


def test_failed_split_refuses_chapter_selection_and_hot_direct_page_cache(storage_env):
    service, database = storage_env
    original, candidate = attached_chapter(service, database)
    assert service.page_fetch_info(candidate['id'], 0)['source_manga_id'] == 12
    fail_split_origin(database)
    with pytest.raises(sqlite3.IntegrityError, match='origin split failure'):
        service.unmatch_duplicate_records(original, [{'source_id': 'b', 'source_manga_id': 12}])
    assert service._find_existing_by_source('b', 12) != original
    assert service.list_chapters(original) == []
    assert service.chapter_details(candidate['virtual_chapter_id'])['would_serve_candidate_id'] is None
    with pytest.raises(RuntimeError, match='No active source candidate'):
        service._page_resolution_snapshot(candidate['virtual_chapter_id'])
    with pytest.raises(KeyError):
        service.page_fetch_info(candidate['id'], 0)
    with database.origins() as o:
        assert o.execute('SELECT active FROM chapter_candidates WHERE id=?', (candidate['id'],)).fetchone()[0] == 1
        assert o.execute('SELECT COUNT(*) FROM origin_history').fetchone()[0] == 1
        assert o.execute('SELECT COUNT(*) FROM served_origin').fetchone()[0] == 1
        assert o.execute('SELECT COUNT(*) FROM candidate_pages').fetchone()[0] == 1


def test_failed_split_reopens_real_sqlite_and_recovers_without_losing_provenance(storage_env, monkeypatch):
    service, database = storage_env
    original, candidate = attached_chapter(service, database)
    fail_split_origin(database)
    with pytest.raises(sqlite3.IntegrityError):
        service.unmatch_duplicate_records(original, [{'source_id': 'b', 'source_manga_id': 12}])
    new = service._find_existing_by_source('b', 12)
    with database.catalog() as c:
        assert c.execute('SELECT COUNT(*) FROM pending_entry_splits').fetchone()[0] == 1
        assert c.execute('SELECT COUNT(*) FROM duplicate_split_history').fetchone()[0] == 1
    database.close_current_thread()
    reopened = Database()
    monkeypatch.setattr(service_module, 'db', reopened)
    monkeypatch.setattr(tag_store, 'db', reopened)
    try:
        restarted = AutoSourcarrService()
        assert restarted.chapter_details(candidate['virtual_chapter_id'])['would_serve_candidate_id'] is None
        with reopened.origins() as o:
            o.execute('DROP TRIGGER fail_split_origin')
        assert restarted.reconcile_pending_entry_origins() == {'checked': 1, 'completed': 1, 'failed': 0, 'remaining': 0}
        assert restarted.reconcile_pending_entry_origins()['checked'] == 0
        assert restarted._find_existing_by_source('b', 12) == new
        assert restarted._duplicate_review(original, new) == 'not_duplicate'
        with reopened.origins() as o:
            assert o.execute('SELECT active FROM chapter_candidates WHERE id=?', (candidate['id'],)).fetchone()[0] == 0
            assert o.execute("SELECT entry_id FROM source_record_scans WHERE source_id='b'").fetchone()[0] == new
            assert o.execute('SELECT COUNT(*) FROM origin_history').fetchone()[0] == 1
            assert o.execute('SELECT COUNT(*) FROM served_origin').fetchone()[0] == 1
            assert o.execute('SELECT COUNT(*) FROM candidate_pages').fetchone()[0] == 1
        with reopened.catalog() as c:
            assert c.execute('SELECT COUNT(*) FROM duplicate_split_history').fetchone()[0] == 1
    finally:
        reopened.close_current_thread()


def test_split_ack_retry_keeps_newer_scans_and_does_not_repeat_catalogue_audit(storage_env, monkeypatch):
    service, database = storage_env
    original, candidate = attached_chapter(service, database)
    complete = service._complete_pending_entry_split
    monkeypatch.setattr(service, '_complete_pending_entry_split', lambda *_: (_ for _ in ()).throw(RuntimeError('split ack failed')))
    with pytest.raises(RuntimeError, match='split ack failed'):
        service.unmatch_duplicate_records(original, [{'source_id': 'b', 'source_manga_id': 12}])
    new = service._find_existing_by_source('b', 12)
    with database.origins() as o:
        o.execute("UPDATE source_record_scans SET checked_at='2099-01-01',chapter_count=99,error='latest' WHERE source_id='b'")
        o.execute("INSERT INTO source_record_scans(entry_id,source_id,source_manga_id,checked_at,chapter_count,error) VALUES(?,'b',12,'2000-01-01',7,'old')", (original,))
    monkeypatch.setattr(service, '_complete_pending_entry_split', complete)
    assert service.reconcile_pending_entry_origins()['completed'] == 1
    with database.origins() as o:
        assert tuple(o.execute("SELECT entry_id,checked_at,chapter_count,error FROM source_record_scans WHERE source_id='b'").fetchone()) == (new, '2099-01-01', 99, 'latest')
    with database.catalog() as c:
        assert c.execute('SELECT COUNT(*) FROM duplicate_split_history').fetchone()[0] == 1
        assert c.execute('SELECT COUNT(*) FROM duplicate_review_history').fetchone()[0] == 1


def test_later_explicit_rejoin_wins_over_an_older_split_journal(storage_env):
    service, database = storage_env
    original, candidate = attached_chapter(service, database)
    fail_split_origin(database)
    with pytest.raises(sqlite3.IntegrityError):
        service.unmatch_duplicate_records(original, [{'source_id': 'b', 'source_manga_id': 12}])
    with database.origins() as o:
        o.execute('DROP TRIGGER fail_split_origin')
    service._commit_manual_match(original, 'b', 12, 'add', payload('b', 12), {'manga': payload('b', 12), 'chapters': []}, True)
    assert service._find_existing_by_source('b', 12) == original
    assert service.reconcile_pending_entry_origins()['completed'] == 1
    assert service.chapter_details(candidate['virtual_chapter_id'])['would_serve_candidate_id'] == candidate['id']
    assert service.page_fetch_info(candidate['id'], 0)['source_manga_id'] == 12
    assert not service._is_manual_separate_record(original, 'b', 12)


def test_later_second_split_wins_and_durable_exclusion_follows_the_actual_owner(storage_env):
    service, database = storage_env
    original, candidate = attached_chapter(service, database)
    fail_split_origin(database)
    with pytest.raises(sqlite3.IntegrityError):
        service.unmatch_duplicate_records(original, [{'source_id': 'b', 'source_manga_id': 12}])
    first = service._find_existing_by_source('b', 12)
    with database.origins() as o:
        o.execute('DROP TRIGGER fail_split_origin')
    service._upsert_match(first, 'c', payload('c', 13), 1)
    second = service.unmatch_duplicate_records(first, [{'source_id': 'b', 'source_manga_id': 12}])['new_entry_id']
    assert service.reconcile_pending_entry_origins()['completed'] == 1
    assert service._find_existing_by_source('b', 12) == second
    assert service._duplicate_review(original, second) == 'not_duplicate'
    with database.catalog() as c:
        assert c.execute("SELECT separate_entry_id_hint FROM manual_separate_records WHERE target_entry_id=? AND source_id='b'", (original,)).fetchone()[0] == second
    with database.origins() as o:
        assert o.execute('SELECT active FROM chapter_candidates WHERE id=?', (candidate['id'],)).fetchone()[0] == 0
        assert o.execute("SELECT entry_id FROM source_record_scans WHERE source_id='b'").fetchone()[0] == second


def test_inactive_owned_candidate_remains_available_to_existing_direct_page_urls(storage_env):
    service, database = storage_env
    original, candidate = attached_chapter(service, database)
    with database.origins() as o:
        o.execute('UPDATE chapter_candidates SET active=0 WHERE id=?', (candidate['id'],))
    assert service.page_fetch_info(candidate['id'], 0)['source_manga_id'] == 12
    assert service.chapter_details(candidate['virtual_chapter_id'])['would_serve_candidate_id'] is None


def test_async_page_fetch_rechecks_membership_before_publishing_pages_or_served_history(storage_env, monkeypatch):
    service, database = storage_env
    original, candidate = attached_chapter(service, database)
    monkeypatch.setattr(service_module.source_host, 'chapter_index_for_id', AsyncMock(return_value=0))
    async def run():
        started, release = asyncio.Event(), asyncio.Event()
        async def fetch(*_):
            started.set()
            await release.wait()
            return ['https://example.invalid/new-page']
        monkeypatch.setattr(service_module.source_host, 'fetch_chapter_pages', fetch)
        task = asyncio.create_task(service._resolve_pages_once(candidate['virtual_chapter_id']))
        await started.wait()
        service.unmatch_duplicate_records(original, [{'source_id': 'b', 'source_manga_id': 12}])
        release.set()
        with pytest.raises(KeyError):
            await task
    asyncio.run(run())
    with database.origins() as o:
        assert o.execute('SELECT private_url FROM candidate_pages WHERE candidate_id=?', (candidate['id'],)).fetchone()[0] == 'https://example.invalid/page'
        assert o.execute('SELECT COUNT(*) FROM origin_history').fetchone()[0] == 1


@pytest.mark.parametrize('full', [False, True])
def test_catalogue_reset_discards_obsolete_pending_splits(storage_env, full):
    service, database = storage_env
    original, _ = attached_chapter(service, database)
    fail_split_origin(database)
    with pytest.raises(sqlite3.IntegrityError):
        service.unmatch_duplicate_records(original, [{'source_id': 'b', 'source_manga_id': 12}])
    with database.catalog() as c:
        assert c.execute('SELECT COUNT(*) FROM pending_entry_splits').fetchone()[0] == 1
    (service.full_reset_database if full else service.reset_database)()
    assert service.pending_entry_origin_count() == 0
