"""Concurrent review decisions must not be replaced by an older SQLite snapshot."""
from concurrent.futures import ThreadPoolExecutor

from app import tag_store
from test_v087_recovery import env


def test_old_reader_snapshot_cannot_poison_shared_tag_mapping(env, monkeypatch):
    _, database = env
    monkeypatch.setattr(tag_store, 'db', database)
    tag_store.invalidate()
    with database.catalog() as c:
        c.executemany('INSERT INTO tags(namespace,name,normalized) VALUES(?,?,?)', [
            ('tag', 'sci-fi', 'sci-fi'), ('tag', 'science fiction', 'science fiction'),
        ])
        ids = [r[0] for r in c.execute('SELECT id FROM tags ORDER BY id')]
    with ThreadPoolExecutor(max_workers=1) as worker:
        with database.catalog() as old_reader:
            old_reader.execute('BEGIN')
            old_reader.execute('SELECT * FROM tags').fetchall()  # Hold the pre-review snapshot.
            worker.submit(tag_store.review, *ids, 'different').result(timeout=5)
            # The committed decision already invalidated the cache. An older read
            # transaction must not refill it with its pre-review equivalence map.
            tag_store._current_mapping(old_reader)
    assert tag_store.canonical_keys(['sci-fi', 'science fiction']) == {'tag:sci-fi', 'tag:science fiction'}
