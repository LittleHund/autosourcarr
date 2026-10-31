"""A saved feed remains readable while another SQLite writer owns admission."""
import json
import sqlite3

from app.config import settings
from test_v087_recovery import env, entry
from test_v091_duplicate_persistence import make_duplicates


def test_cached_merged_ids_are_read_only_under_writer_contention(env):
    service, database = env
    left, right = make_duplicates(service, database)
    key = service._browse_cache_key('POPULAR', 1, '', None, 0, None, 'relevance', 50)
    service._save_persistent_browse_cache(key, 'POPULAR', 1, '', None, 0, None,
                                        'relevance', {'items': [{'id': left}, {'id': right}]})
    merged = service.review_duplicate(left, right, 'duplicate')['canonical_entry_id']
    with database.catalog() as reader:
        original = reader.execute('SELECT entry_ids_json FROM browse_cache WHERE cache_key=?', (key,)).fetchone()[0]
        timeout = reader.execute('PRAGMA busy_timeout').fetchone()[0]
        reader.execute('PRAGMA busy_timeout=0')
        try:
            with sqlite3.connect(settings.catalog_path) as writer:
                writer.execute('BEGIN IMMEDIATE')
                for _ in range(2):
                    payload = service._load_persistent_browse_cache(key)
                    assert [item['canonical_id'] for item in payload['items']] == [merged]
                    assert payload['cache_hit'] and payload['persistent_cache']
                # Reading must not compact stored IDs or wait for this writer.
                assert reader.execute('SELECT entry_ids_json FROM browse_cache WHERE cache_key=?', (key,)).fetchone()[0] == original
                assert json.loads(original) == [left, right]
        finally:
            reader.execute(f'PRAGMA busy_timeout={timeout}')


def test_aliased_cache_read_cannot_overwrite_a_newer_saved_feed(env, monkeypatch):
    service, database = env
    left, right = make_duplicates(service, database)
    fresh = entry(service, 'a', 99901, 'A Different Complete Work')
    key = service._browse_cache_key('POPULAR', 1, '', None, 0, None, 'relevance', 50)
    def save(ids):
        service._save_persistent_browse_cache(key, 'POPULAR', 1, '', None, 0, None,
                                            'relevance', {'items': [{'id': eid} for eid in ids]})
    save([left, right])
    merged = service.review_duplicate(left, right, 'duplicate')['canonical_entry_id']
    summaries = service._entry_summaries
    def refresh_after_read(ids):
        save([fresh])
        return summaries(ids)
    monkeypatch.setattr(service, '_entry_summaries', refresh_after_read)
    # This request may finish its captured old snapshot; it cannot undo a refresh.
    assert [row['canonical_id'] for row in service._load_persistent_browse_cache(key)['items']] == [merged]
    with database.catalog() as c:
        stored = c.execute('SELECT entry_ids_json FROM browse_cache WHERE cache_key=?', (key,)).fetchone()[0]
    assert json.loads(stored) == [fresh]
