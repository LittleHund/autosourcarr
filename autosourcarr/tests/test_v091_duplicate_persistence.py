"""Review completion must not restore resolved pairs from persisted startup cache."""
import threading
from concurrent.futures import ThreadPoolExecutor
from app.service import AutoSourcarrService
from test_v087_recovery import env, source, entry


def make_duplicates(s, database):
    source(database, 'a'); source(database, 'b')
    left = entry(s, 'a', 91101)
    right = entry(s, 'b', 91102)
    groups = s.duplicate_groups()
    assert any({left, right} == set(g['entry_ids']) for g in groups)
    return left, right


def unresolved_pairs(s):
    return {(p['left_id'], p['right_id']) for g in (s.duplicate_groups_cached() or []) for p in g['pairs']}


def test_manual_review_remains_removed_from_warm_snapshot_after_restart(env):
    s, database = env
    left, right = make_duplicates(s, database)
    result = s.review_duplicate(left, right, 'not_duplicate')
    assert result['groups'] == []
    restarted = AutoSourcarrService()
    assert restarted._duplicate_review(left, right) == 'not_duplicate'
    assert (left, right) not in unresolved_pairs(restarted), 'Restart restores reviewed pair from old persisted snapshot'


def test_scan_persisting_after_manual_review_cannot_resurrect_resolved_pair(env, monkeypatch):
    s, database = env
    left, right = make_duplicates(s, database)
    s._invalidate_duplicate_snapshot()
    at_persist = threading.Event()
    allow_persist = threading.Event()
    persist = s._persist_duplicate_snapshot
    def delayed_persist(groups):
        at_persist.set()
        assert allow_persist.wait(1)
        persist(groups)
    monkeypatch.setattr(s, '_persist_duplicate_snapshot', delayed_persist)
    with ThreadPoolExecutor(max_workers=1) as worker:
        scan = worker.submit(s.duplicate_groups)
        try:
            assert at_persist.wait(1)
            s.review_duplicate(left, right, 'not_duplicate')
        finally:
            allow_persist.set()
        scan.result(1)
    assert (left, right) not in unresolved_pairs(s)
    restarted = AutoSourcarrService()
    assert (left, right) not in unresolved_pairs(restarted), 'Old scanner persisted its completed but superseded snapshot'


def test_manual_merge_does_not_rewrite_every_persistent_feed(env, monkeypatch):
    s, database = env
    left, right = make_duplicates(s, database)
    key = s._browse_cache_key('POPULAR', 1, '', None, 0, None, 'relevance', 50)
    s._save_persistent_browse_cache(key, 'POPULAR', 1, '', None, 0, None, 'relevance', {'items': [{'id': right}]})
    global_rewrites = []
    monkeypatch.setattr(s, '_repair_cached_entry_ids', lambda: global_rewrites.append(True))
    result = s.review_duplicate(left, right, 'duplicate')
    assert not global_rewrites, 'A single manual merge scans every cached feed under its mutation lock'
    payload = s._load_persistent_browse_cache(key)
    assert payload['items'][0]['canonical_id'] == result['canonical_entry_id']
