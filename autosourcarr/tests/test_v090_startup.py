"""Restart recovery preserves identities and avoids rewriting existing user intent."""
import importlib
import random

import pytest

from app.config import settings
from app.db import Database, now_iso
from app.service import AutoSourcarrService

service_module = importlib.import_module('app.service')

@pytest.fixture
def startup_catalog(monkeypatch, tmp_path):
    monkeypatch.setattr(settings, 'data_dir', tmp_path)
    monkeypatch.setattr(settings, 'database_dir', tmp_path)
    monkeypatch.setattr(settings, 'settings_config_dir', tmp_path / 'config')
    database = Database()
    monkeypatch.setattr(service_module, 'db', database)
    yield database
    database.close_current_thread()


def add_entry(c, entry_id, title='Survivor'):
    c.execute('INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(?,?,?,?)',
              (entry_id, title, now_iso(), now_iso()))


def test_startup_recovers_chains_and_discards_only_broken_aliases(startup_catalog):
    db = startup_catalog
    with db.catalog() as c:
        add_entry(c, 10); add_entry(c, 20)
        # A live entry shadows its old alias; cycles/dangling ids cannot resolve.
        for old, target in [(1, 2), (2, 3), (4, 5), (5, 4), (6, 777), (10, 20), (8, 10)]:
            c.execute('INSERT INTO entry_aliases VALUES(?,?,?,?)', (old, target, 'test', now_iso()))
        c.execute('INSERT INTO match_history(entry_id,source_id,old_source_manga_id,new_source_manga_id,change_type,changed_at) VALUES(?,?,?,?,?,?)',
                  (20, '*', 3, 20, 'merge:interrupted', now_iso()))
    service = AutoSourcarrService()
    assert {i: service.canonical_entry_id(i) for i in (1, 2, 3, 8, 10, 20)} == {1:20, 2:20, 3:20, 8:10, 10:10, 20:20}
    with db.catalog() as c:
        assert [r[0] for r in c.execute('SELECT old_entry_id FROM entry_aliases ORDER BY old_entry_id')] == [1, 2, 3, 8]
    assert service._repair_entry_alias_integrity() == {'repaired':0, 'removed':0}


def test_startup_random_alias_graph_matches_identity_resolution_contract(startup_catalog):
    db = startup_catalog
    randomizer = random.Random(90)
    live = {901, 902, 903}
    links = {i: randomizer.randrange(1, 906) for i in range(1, 904)}
    expected = {}
    for start in links:
        if start in live:
            continue
        current = start
        seen = set()
        while current not in seen and current not in live and current in links:
            seen.add(current)
            current = links[current]
        if current in live:
            expected[start] = current
    with db.catalog() as c:
        for eid in live: add_entry(c, eid)
        c.executemany('INSERT INTO entry_aliases VALUES(?,?,?,?)', ((k,v,'test',now_iso()) for k,v in links.items()))
    AutoSourcarrService()
    with db.catalog() as c:
        actual = dict(c.execute('SELECT old_entry_id,canonical_entry_id FROM entry_aliases'))
    assert actual == expected


def test_restart_capture_preserves_existing_manual_binding_payload_and_timestamps(startup_catalog):
    db = startup_catalog
    with db.catalog() as c:
        add_entry(c, 100)
        c.execute('INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,match_type,locked,last_checked) VALUES(?,?,?,?,?,?,?)',
                  (100, 'manual-source', 1001, 'Manual Work', 'manual', 1, now_iso()))
    service = AutoSourcarrService()
    with db.catalog() as c:
        c.execute("UPDATE manual_match_bindings SET record_json=?, updated_at=?", ('{"id":1001,"user_note":"preserve extra metadata"}', '2020-01-01'))
        before = tuple(c.execute('SELECT * FROM manual_match_bindings').fetchone())
    assert service._capture_existing_manual_matches() == 0
    with db.catalog() as c:
        assert tuple(c.execute('SELECT * FROM manual_match_bindings').fetchone()) == before


def test_restart_capture_repairs_reassigned_manual_identity_and_forms_group(startup_catalog):
    db = startup_catalog
    with db.catalog() as c:
        add_entry(c, 100); add_entry(c, 101)
        c.execute('INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,match_type,locked,last_checked) VALUES(?,?,?,?,?,?,?)',
                  (100, 'manual-source', 1001, 'Manual Work', 'manual', 1, now_iso()))
    service = AutoSourcarrService()
    with db.catalog() as c:
        c.execute("UPDATE source_matches SET entry_id=101")
        c.execute('INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,match_type,locked,last_checked) VALUES(?,?,?,?,?,?,?)',
                  (101, 'second-source', 2001, 'Manual Work', 'MANUAL', 1, now_iso()))
    assert service._capture_existing_manual_matches() == 2
    with db.catalog() as c:
        bindings = [dict(r) for r in c.execute('SELECT * FROM manual_match_bindings')]
        assert {r['entry_id_hint'] for r in bindings} == {101}
        assert len({r['group_id'] for r in bindings}) == 1
        assert all(r['group_id'] for r in bindings)


def test_title_restore_applies_missing_title_but_does_not_rewrite_clean_entry(startup_catalog):
    db = startup_catalog
    with db.catalog() as c:
        add_entry(c, 100, 'Reset title')
        c.execute('INSERT INTO preferred_title_overrides VALUES(?,?,?)', (100, 'My manual title', now_iso()))
    service = AutoSourcarrService()
    with db.catalog() as c:
        assert tuple(c.execute('SELECT preferred_title,manual_preferred_title FROM entries').fetchone()) == ('My manual title', 1)
        before = c.total_changes
        assert service._apply_persistent_title_overrides() == 0
        assert c.total_changes == before


def test_restart_recovers_group_after_interrupted_binding_capture(startup_catalog):
    db = startup_catalog
    with db.catalog() as c:
        add_entry(c, 100)
        for sid, mid in [('first-source', 1001), ('second-source', 2001)]:
            c.execute('INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,match_type,locked,last_checked) VALUES(?,?,?,?,?,?,?)',
                      (100, sid, mid, 'Manual Work', 'manual', 1, now_iso()))
    service = AutoSourcarrService()
    with db.catalog() as c:
        c.execute('UPDATE manual_match_bindings SET group_id=NULL')
        c.execute('DELETE FROM manual_match_groups')
    assert service._capture_existing_manual_matches() == 0
    with db.catalog() as c:
        groups = [r[0] for r in c.execute('SELECT group_id FROM manual_match_bindings')]
        assert groups[0] and groups[0] == groups[1]
