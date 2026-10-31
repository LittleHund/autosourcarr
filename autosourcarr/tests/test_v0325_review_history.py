"""Retained merge provenance and local read responsiveness."""
import asyncio
import json
import threading
from concurrent.futures import ThreadPoolExecutor

import httpx
import pytest
from fastapi import FastAPI

from app import matching_review as review
from app.db import Database
from app.read_lane import ReadLane
from app import service as module
from test_v0323_matching_review import env, entry


def test_reason_only_automatic_merge_visible_after_real_upgrade(env, monkeypatch):
    service, database = env
    left, right = entry(service, 'a'), entry(service, 'b', 2)
    survivor = service.merge_entries(left, right, 'automatic-consolidation')
    database.close_current_thread()
    upgraded = Database()
    monkeypatch.setattr(module, 'db', upgraded)
    service = module.AutoSourcarrService()
    page = review.history(upgraded, service)
    assert page['total'] == 1
    row = page['items'][0]
    assert row['id'] < 0 and row['legacy']
    assert row['current_entry_id'] == survivor
    assert 'reason only' in row['state']
    detail = review.detail(upgraded, service, row['id'])
    assert detail['legacy'] and not any(s['available'] for s in detail['before'])
    assert {s['entry']['id'] for s in detail['before']} == {left, right}
    assert 'not stored' in detail['evidence']['provenance']
    with upgraded.catalog() as c:
        assert c.execute('SELECT COUNT(*) FROM automatic_merge_events').fetchone()[0] == 0
    upgraded.close_current_thread()


@pytest.mark.parametrize('reason', ['manual-review', 'manual-match', 'unknown reason', 'import-source-bind', 'automatic-looking-unverified'])
def test_unknown_or_manual_markers_not_presented_as_automatic(env, reason):
    service, database = env
    service.merge_entries(entry(service, 'a'), entry(service, 'b', 2), reason)
    with database.catalog() as c:
        history_id = c.execute('SELECT id FROM match_history WHERE source_id=?', ('*',)).fetchone()[0]
    assert review.history(database, service)['total'] == 0
    with pytest.raises(KeyError):
        review.detail(database, service, -history_id)


def test_snapshot_merge_deduplicates_its_old_marker_and_keeps_original_ids(env):
    service, database = env
    left, right = entry(service, 'a'), entry(service, 'b', 2)
    survivor = service._merge_entries_automatic(left, right, 'automatic-consolidation')
    with database.catalog() as c:
        old_id = c.execute('SELECT id FROM match_history WHERE source_id=?', ('*',)).fetchone()[0]
    later = service.merge_entries(survivor, entry(service, 'c', 3), 'manual-review')
    page = review.history(database, service)
    assert page['total'] == 1 and not page['items'][0]['legacy']
    assert page['items'][0]['current_entry_id'] == later
    with pytest.raises(KeyError):
        review.detail(database, service, -old_id)
    original = review.detail(database, service, page['items'][0]['id'])
    assert {s['entry']['id'] for s in original['before']} == {left, right}


def test_reason_only_legacy_history_follows_later_alias_without_inventing_titles(env):
    service, database = env
    left, right = entry(service, 'a'), entry(service, 'b', 2)
    survivor = service.merge_entries(left, right, 'automatic-safe:exact native title')
    later = service.merge_entries(survivor, entry(service, 'c', 3), 'manual-review')
    page = review.history(database, service, query=str(right))
    assert page['total'] == 1
    row = page['items'][0]
    assert {row['target_entry_id'], row['duplicate_entry_id']} == {left, right}
    assert row['current_entry_id'] == later
    assert row['left_title'] == f"Entry {row['target_entry_id']}"
    assert review.history(database, service, offset=1)['items'] == []


@pytest.mark.parametrize('invalid_id', ['not-an-entry', None])
def test_malformed_legacy_identifiers_do_not_break_or_claim_automatic_history(env, invalid_id):
    service, database = env
    target = entry(service, 'a')
    with database.catalog() as c:
        old_id = c.execute("INSERT INTO match_history(entry_id,source_id,old_source_manga_id,new_source_manga_id,change_type,changed_at) VALUES(?,'*',?,?,'merge:automatic-consolidation','now')", (target, invalid_id, target)).lastrowid
    assert review.history(database, service)['total'] == 0
    with pytest.raises(KeyError):
        review.detail(database, service, -old_id)


def test_history_index_upgrade_preserves_original_snapshots_and_tolerates_invalid_json(env, monkeypatch):
    service, database = env
    left, right = entry(service, 'a'), entry(service, 'b', 2)
    original = json.dumps({'entry': {'id': left, 'preferred_title': 'A 100% Original_Title'}})
    with database.catalog() as c:
        c.execute('DROP INDEX idx_auto_merge_events_compact_history')
        event = c.execute("INSERT INTO automatic_merge_events(target_entry_id,duplicate_entry_id,target_snapshot_json,duplicate_snapshot_json,reason,merged_at) VALUES(?,?,?,'not JSON','legacy snapshot','2026-10-05')", (left, right, original)).lastrowid
    database.close_current_thread()
    upgraded = Database()
    monkeypatch.setattr(module, 'db', upgraded)
    with upgraded.catalog() as c:
        assert c.execute('SELECT target_snapshot_json FROM automatic_merge_events WHERE id=?', (event,)).fetchone()[0] == original
        assert c.execute("SELECT 1 FROM sqlite_master WHERE name='idx_auto_merge_events_compact_history'").fetchone()
    assert review.history(upgraded, service, query='100% Original_')['total'] == 1
    assert review.history(upgraded, service, query='not present')['total'] == 0
    assert review.detail(upgraded, service, event)['before'][0]['entry']['preferred_title'] == 'A 100% Original_Title'
    upgraded.close_current_thread()


@pytest.mark.parametrize('path', ['/matching-policies', '/duplicates/history', '/duplicates/history/1'])
def test_local_review_reads_are_admitted_when_source_executor_is_saturated(env, monkeypatch, path):
    service, database = env
    service._merge_entries_automatic(entry(service, 'a'), entry(service, 'b', 2), 'direct evidence')
    lane = ReadLane(workers=1)
    monkeypatch.setattr(review, 'read_lane', lane)
    app = FastAPI()
    app.include_router(review.router(database, service, lambda: True))
    entered, release = threading.Event(), threading.Event()

    def source_work():
        entered.set()
        assert release.wait(3)

    async def run():
        loop = asyncio.get_running_loop()
        loop.set_default_executor(ThreadPoolExecutor(max_workers=1))
        blocked = loop.run_in_executor(None, source_work)
        assert entered.wait(1)
        try:
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://app') as client:
                response = await asyncio.wait_for(client.get('/api/admin' + path), .75)
                assert response.status_code == 200
                assert not blocked.done(), 'Read waited for unrelated source work'
        finally:
            release.set()
            await blocked
            await lane.close()
    asyncio.run(run())
