"""Manual decisions must not wait for catalogue-wide duplicate scoring."""
import threading
import asyncio
from concurrent.futures import ThreadPoolExecutor

import pytest

from test_v087_recovery import env, source, entry
from app.config import settings


def entries(s, database):
    source(database, 'a'); source(database, 'b')
    return entry(s, 'a', 91001), entry(s, 'b', 91002)


def test_review_does_not_rescore_catalogue_or_rewrite_unrelated_feeds(env, monkeypatch):
    s, database = env
    left, right = entries(s, database)
    def forbidden(*args, **kwargs):
        pytest.fail('A manual decision performed catalogue-wide follow-up work')
    monkeypatch.setattr(s, '_duplicate_groups_locked', forbidden)
    monkeypatch.setattr(s, '_repair_cached_entry_ids', forbidden)
    result = s.review_duplicate(left, right, 'not_duplicate')
    assert result['decision'] == 'not_duplicate'
    assert result['pending'] is True
    assert s._duplicate_review(left, right) == 'not_duplicate'


@pytest.mark.parametrize('operation', ['shortlist', 'snapshot'])
def test_manual_review_finishes_while_automatic_scan_is_still_running(env, monkeypatch, operation):
    s, database = env
    left, right = entries(s, database)
    scanning = threading.Event()
    release = threading.Event()
    original = s._duplicate_shortlist if operation == 'shortlist' else s.duplicate_candidates
    def slow_scan(*args, **kwargs):
        scanning.set()
        assert release.wait(3), 'Test failed to release scan'
        return original(*args, **kwargs)
    monkeypatch.setattr(s, '_duplicate_shortlist' if operation == 'shortlist' else 'duplicate_candidates', slow_scan)
    def work(fn, *args):
        try:
            return fn(*args)
        finally:
            database.close_current_thread()
    with ThreadPoolExecutor(2) as pool:
        scan = pool.submit(work, s.repair_catalog_duplicates if operation == 'shortlist' else s.duplicate_groups)
        assert scanning.wait(1)
        review = pool.submit(work, s.review_duplicate, left, right, 'not_duplicate')
        try:
            assert review.result(timeout=.5)['decision'] == 'not_duplicate'
            assert not scan.done(), 'Manual work only finished after automation'
        finally:
            release.set()
        scan.result(timeout=3)
    assert s._duplicate_review(left, right) == 'not_duplicate'


def test_review_filters_only_affected_pair_and_retains_other_candidates(env):
    s, database = env
    left, right = entries(s, database)
    third = entry(s, 'a', 91003)
    s._duplicate_snapshot = (0, [{
        'group_id': 'old', 'entry_ids': [left, right, third],
        'entries': [{'id': n} for n in (left, right, third)],
        'pairs': [{'left_id': left, 'right_id': right}, {'left_id': left, 'right_id': third}],
        'best_score': 1,
    }])
    result = s.review_duplicate(left, right, 'not_duplicate')
    pairs = [p for g in result['groups'] for p in g['pairs']]
    assert {(p['left_id'], p['right_id']) for p in pairs} == {(left, third)}
    assert s.duplicate_groups_cached() == result['groups']


def test_old_snapshot_cannot_restore_a_reviewed_pair(env, monkeypatch):
    s, database = env
    left, right = entries(s, database)
    s._duplicate_snapshot = (0, [])
    s._duplicate_snapshot_stale = True
    def old_candidates():
        s.review_duplicate(left, right, 'not_duplicate')
        return {left: {'entry_ids': [right], 'candidates': [{'entry_id': right, 'title_score': 1}]}}
    monkeypatch.setattr(s, 'duplicate_candidates', old_candidates)
    assert s.duplicate_groups() == []
    assert s.duplicate_groups_cached() == []
    assert s.duplicate_snapshot_summary()['stale'] is True


def test_automatic_identity_shortlist_keeps_http_loop_available_and_stops_at_budget(env, monkeypatch):
    s, database = env
    left, right = entries(s, database)
    third = entry(s, 'a', 91004)
    monkeypatch.setattr(settings, 'metadata_identity_enabled', True)
    started, release, finished = threading.Event(), threading.Event(), threading.Event()
    def shortlist():
        started.set(); release.wait(.3); finished.set()
        return {(left, right), (right, third)}, {}, {}
    monkeypatch.setattr(s, '_duplicate_shortlist', shortlist)
    checked = []
    monkeypatch.setattr(s, '_identity_links', lambda eid: checked.append(eid) or [])
    async def resolve(*args, **kwargs):
        return {'matched': 1}
    monkeypatch.setattr(s, 'resolve_entry_identity', resolve)
    async def scenario():
        async def independent_request():
            while not started.is_set():
                await asyncio.sleep(.001)
            responsive = not finished.is_set()
            release.set()
            return responsive
        request = asyncio.create_task(independent_request())
        result = await s._hydrate_duplicate_identity_candidates(limit=1)
        assert await request, 'Automatic preparation froze HTTP until the full scan finished'
        assert result == {'checked': 1, 'resolved': 1}
        assert len(checked) == 1, 'Preparation inspected entries beyond this pass budget'
    try:
        asyncio.run(scenario())
    finally:
        release.set()
