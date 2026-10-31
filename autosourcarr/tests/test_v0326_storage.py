"""Durable user decisions and lossless merge recovery against real app databases."""
import asyncio
import json
import sqlite3

import pytest

from app import service as service_module, tag_store
from app.db import Database, now_iso
from app.service import AutoSourcarrService
from test_v0323_matching_review import env, entry, payload, TITLE, DESCRIPTION


@pytest.fixture
def storage_env(env, monkeypatch):
    service, database = env
    monkeypatch.setattr(tag_store, 'db', database)
    tag_store.invalidate()
    yield service, database
    tag_store.invalidate()


def reorder_legacy_variants(database):
    # db._migrate() has supported appending these columns since early variants.
    # Rebuild the pre-column table and run that real migration, retaining native
    # uniqueness/FKs instead of relying on a CREATE TABLE AS read-only stand-in.
    tail = ['match_type', 'locked', 'initialized']
    with database.catalog() as c:
        info = [dict(r) for r in c.execute('PRAGMA table_info(source_variants)')]
        columns = [r['name'] for r in info if r['name'] not in tail]
        declarations = []
        for row in info:
            if row['name'] in tail:
                continue
            if row['name'] == 'id':
                declaration = 'id INTEGER PRIMARY KEY AUTOINCREMENT'
            else:
                declaration = row['name'] + ' ' + row['type']
                if row['notnull']:
                    declaration += ' NOT NULL'
                if row['dflt_value'] is not None:
                    declaration += ' DEFAULT ' + row['dflt_value']
                if row['name'] == 'entry_id':
                    declaration += ' REFERENCES entries(id) ON DELETE CASCADE'
            declarations.append(declaration)
        declarations.append('UNIQUE(source_id,source_manga_id)')
        c.execute('ALTER TABLE source_variants RENAME TO old_variants')
        c.execute('CREATE TABLE source_variants(' + ','.join(declarations) + ')')
        c.execute('INSERT INTO source_variants(' + ','.join(columns) + ') SELECT ' + ','.join(columns) + ' FROM old_variants')
    database._migrate()
    with database.catalog() as c:
        for name in tail:
            c.execute(f'UPDATE source_variants SET {name}=(SELECT {name} FROM old_variants old WHERE old.id=source_variants.id)')
        c.execute('DROP TABLE old_variants')
        assert [r[1] for r in c.execute('PRAGMA table_info(source_variants)')][-3:] == tail


def manual_pair(service):
    eid = entry(service, 'a', 1)
    service._upsert_match(eid, 'b', payload('b', 2), 1, 'manual', True)
    with service_module.db.catalog() as c:
        c.execute("UPDATE source_matches SET match_type='manual',locked=1 WHERE entry_id=?", (eid,))
    service._persist_manual_binding(eid, 'a', payload('a', 1))
    service._persist_manual_binding(eid, 'b', payload('b', 2))
    return eid


def test_reset_groups_stays_reset_after_real_service_reconstruction_and_catalogue_reset(storage_env):
    service, database = storage_env
    manual_pair(service)
    assert service.manual_groups_payload()['group_count'] == 1
    assert service.reset_manual_groups()['groups_removed'] == 1
    restarted = AutoSourcarrService()
    state = restarted.manual_groups_payload()
    assert state['group_count'] == 0
    assert state['manual_match_count'] == 2
    restarted.reset_database()
    assert restarted.restore_persistent_manual_matches()['restored'] == 2
    state = AutoSourcarrService().manual_groups_payload()
    assert state['group_count'] == 0
    assert state['manual_match_count'] == 2
    with database.catalog() as c:
        assert [tuple(r) for r in c.execute('SELECT match_type,locked FROM source_matches ORDER BY source_id')] == [('manual', 1), ('manual', 1)]


def test_legacy_capture_retains_every_named_variant_field(storage_env):
    service, database = storage_env
    eid = entry(service, 'a', 1)
    p = payload('b', 2, initialized=True, artist='Artist One')
    service._upsert_source_variant(eid, 'b', p, 1, 'manual', True)
    reorder_legacy_variants(database)
    with database.catalog() as c:
        expected = dict(c.execute('SELECT * FROM source_variants WHERE source_id=\'b\'').fetchone())
    row = service._manual_record_row('b', 2)
    for key in ('description', 'author', 'artist', 'metadata_json', 'match_type', 'locked', 'initialized'):
        assert row[key] == expected[key], key
    assert service._capture_existing_manual_matches() == 1
    with database.catalog() as c:
        binding = json.loads(c.execute("SELECT record_json FROM manual_match_bindings WHERE source_id='b'").fetchone()[0])
    assert binding['description'] == DESCRIPTION
    assert binding['author'] == 'Author One'
    assert binding['artist'] == 'Artist One'
    assert binding['initialized'] is True


def test_legacy_manual_duplicate_and_import_anchors_use_correct_variant_fields(storage_env, monkeypatch):
    service, database = storage_env
    eid = entry(service, 'a', 1)
    service._upsert_source_variant(eid, 'b', payload('b', 2, initialized=True, artist='Artist One'), 1, 'manual', True)
    reorder_legacy_variants(database)
    records = service._duplicate_review_manual_records([eid])
    variant = next(r for r in records if r['source_id'] == 'b')
    assert variant['description'] == DESCRIPTION
    assert variant['author'] == 'Author One'
    assert variant['locked'] == 1
    monkeypatch.setattr(service, '_current_source_id_for_backup', lambda *a: 'b')
    anchor = service._import_autoresolve_anchor(eid, {'source': 'b'})
    assert (anchor['source_id'], anchor['source_manga_id']) == ('b', 2)
    assert anchor['record']['description'] == DESCRIPTION
    assert anchor['record']['author'] == 'Author One'
    assert anchor['record']['initialized'] is True


def test_explicit_duplicate_review_can_form_group_after_an_intentional_group_reset(storage_env):
    service, database = storage_env
    eid = manual_pair(service)
    service.reset_manual_groups()
    other = entry(service, 'c', 3)
    result = service.review_duplicate(eid, other, 'duplicate')
    assert result['merged']
    state = AutoSourcarrService().manual_groups_payload()
    assert state['group_count'] == 1
    assert state['groups'][0]['member_count'] == 3


def chapter_origin(service, database, eid, sid, mid, chapter_id):
    service._ingest_chapters(eid, {'source_id': sid, 'source_name': sid, 'source_manga_id': mid},
                            service._match_id(eid, sid),
                            [{'id': chapter_id, 'chapterNumber': 1, 'name': 'Chapter 1', 'pageCount': 2, 'url': '/1'}])
    with database.origins() as o:
        candidate = dict(o.execute('SELECT * FROM chapter_candidates WHERE source_chapter_id=?', (chapter_id,)).fetchone())
        o.execute('INSERT INTO served_origin(virtual_chapter_id,candidate_id,selected_at,reason) VALUES(?,?,?,?)',
                  (candidate['virtual_chapter_id'], candidate['id'], 'now', 'served'))
        o.execute('INSERT INTO origin_history(virtual_chapter_id,candidate_id,source_id,source_name,source_manga_id,source_chapter_id,served_at,reason) VALUES(?,?,?,?,?,?,?,?)',
                  (candidate['virtual_chapter_id'], candidate['id'], sid, sid, mid, chapter_id, 'now', 'served'))
        o.execute('INSERT INTO candidate_pages(candidate_id,page_index,private_url,updated_at) VALUES(?,?,?,?)',
                  (candidate['id'], 0, 'https://example.invalid/page', 'now'))
    return candidate


@pytest.mark.parametrize('best_incoming', [False, True])
def test_rejected_replacement_variant_rolls_back_all_catalogue_ownership_and_audit(storage_env, monkeypatch, best_incoming):
    service, database = storage_env
    left = entry(service, 'a', 11, initialized=True, artist='Artist Left')
    right = entry(service, 'a', 12, initialized=True, artist='Artist Right')
    monkeypatch.setattr(service, '_entry_reliability', lambda eid: (int(eid == left),))
    with database.catalog() as c:
        if best_incoming:
            c.execute('UPDATE source_matches SET locked=1 WHERE entry_id=?', (right,))
        before = [dict(r) for r in c.execute('SELECT * FROM source_matches ORDER BY id')]
        c.execute("CREATE TRIGGER reject_merge_variant BEFORE INSERT ON source_variants BEGIN SELECT RAISE(ABORT,'variant rejected'); END")
    with pytest.raises(sqlite3.IntegrityError, match='variant rejected'):
        service._merge_entries_automatic(left, right, 'fault injection')
    with database.catalog() as c:
        assert c.execute('SELECT COUNT(*) FROM entries').fetchone()[0] == 2
        assert [dict(r) for r in c.execute('SELECT * FROM source_matches ORDER BY id')] == before
        assert c.execute('SELECT COUNT(*) FROM source_variants').fetchone()[0] == 0
        assert c.execute('SELECT COUNT(*) FROM automatic_merge_events').fetchone()[0] == 0
        assert c.execute('SELECT COUNT(*) FROM automatic_merge_lineage').fetchone()[0] == 0
        assert c.execute('SELECT COUNT(*) FROM entry_aliases').fetchone()[0] == 0
    assert service.canonical_entry_id(right) == right


def test_alias_failure_rolls_back_merge_and_keeps_cache_unpublished(storage_env):
    service, database = storage_env
    left, right = entry(service, 'a', 11), entry(service, 'b', 12)
    with database.catalog() as c:
        c.execute("CREATE TRIGGER reject_merge_alias BEFORE INSERT ON entry_aliases BEGIN SELECT RAISE(ABORT,'alias rejected'); END")
    with pytest.raises(sqlite3.IntegrityError, match='alias rejected'):
        service._merge_entries_automatic(left, right, 'fault injection')
    with database.catalog() as c:
        assert c.execute('SELECT COUNT(*) FROM entries').fetchone()[0] == 2
        assert c.execute('SELECT COUNT(*) FROM automatic_merge_events').fetchone()[0] == 0
    assert service.canonical_entry_id(right) == right
    assert service._find_existing_by_source('b', 12) == right


def test_origin_failure_keeps_sources_and_audit_then_real_database_restart_recovers(storage_env, monkeypatch):
    service, database = storage_env
    left, right = entry(service, 'a', 11), entry(service, 'a', 12)
    keep = chapter_origin(service, database, left, 'a', 11, 111)
    losing = chapter_origin(service, database, right, 'a', 12, 112)
    original = service._merge_entry_origins
    def fail_after_origin_move(*args):
        original(*args)
        raise RuntimeError('origin transaction failed')
    monkeypatch.setattr(service, '_merge_entry_origins', fail_after_origin_move)
    with pytest.raises(RuntimeError, match='origin transaction failed'):
        service._merge_entries_automatic(left, right, 'fault injection')
    with database.catalog() as c:
        assert c.execute('SELECT COUNT(*) FROM entries').fetchone()[0] == 1
        assert c.execute('SELECT COUNT(*) FROM automatic_merge_events').fetchone()[0] == 1
        variant = dict(c.execute('SELECT * FROM source_variants').fetchone())
        assert (variant['source_manga_id'], variant['description'], variant['author']) == (12, DESCRIPTION, 'Author One')
        assert c.execute('SELECT COUNT(*) FROM pending_entry_merges').fetchone()[0] == 1
    with database.origins() as o:
        assert [r[0] for r in o.execute('SELECT entry_id FROM virtual_chapters ORDER BY id')] == [left, right]
    database.close_current_thread()
    reopened = Database()
    monkeypatch.setattr(service_module, 'db', reopened)
    monkeypatch.setattr(tag_store, 'db', reopened)
    try:
        restarted = AutoSourcarrService()
        assert restarted.canonical_entry_id(right) == left
        assert restarted.reconcile_pending_entry_merges() == {'checked': 1, 'completed': 1, 'failed': 0, 'remaining': 0}
        assert restarted._find_existing_by_source('a', 12) == left
        with reopened.origins() as o:
            assert [(r['entry_id'], r['id']) for r in o.execute('SELECT * FROM virtual_chapters')] == [(left, keep['virtual_chapter_id'])]
            assert [r[0] for r in o.execute('SELECT virtual_chapter_id FROM origin_history ORDER BY id')] == [keep['virtual_chapter_id']] * 2
            assert o.execute('SELECT COUNT(*) FROM candidate_pages').fetchone()[0] == 2
            assert o.execute('SELECT COUNT(*) FROM chapter_candidates').fetchone()[0] == 2
            assert [r[0] for r in o.execute('SELECT entry_id FROM source_record_scans ORDER BY source_manga_id')] == [left, left]
        with reopened.catalog() as c:
            assert c.execute('SELECT COUNT(*) FROM automatic_merge_events').fetchone()[0] == 1
    finally:
        reopened.close_current_thread()


def test_origin_commit_before_ack_is_idempotent_and_follows_later_alias_chain(storage_env, monkeypatch):
    service, database = storage_env
    left, right = entry(service, 'a', 11), entry(service, 'b', 12)
    chapter_origin(service, database, left, 'a', 11, 111)
    chapter_origin(service, database, right, 'b', 12, 112)
    complete = service._complete_pending_entry_merge
    monkeypatch.setattr(service, '_complete_pending_entry_merge', lambda *_: (_ for _ in ()).throw(RuntimeError('ack failed')))
    with pytest.raises(RuntimeError, match='ack failed'):
        service._merge_entries_automatic(left, right, 'first merge')
    monkeypatch.setattr(service, '_complete_pending_entry_merge', complete)
    third = entry(service, 'c', 13)
    monkeypatch.setattr(service, '_entry_reliability', lambda eid: (int(eid == third),))
    assert service.merge_entries(left, third, 'later manual merge') == third
    assert service.reconcile_pending_entry_merges() == {'checked': 1, 'completed': 1, 'failed': 0, 'remaining': 0}
    assert service.reconcile_pending_entry_merges() == {'checked': 0, 'completed': 0, 'failed': 0, 'remaining': 0}
    with database.origins() as o:
        assert [r[0] for r in o.execute('SELECT entry_id FROM virtual_chapters')] == [third]
        assert o.execute('SELECT COUNT(*) FROM chapter_candidates').fetchone()[0] == 2
        assert o.execute('SELECT COUNT(*) FROM origin_history').fetchone()[0] == 2
        assert o.execute('SELECT COUNT(*) FROM candidate_pages').fetchone()[0] == 2


def test_failed_manual_duplicate_commits_manual_bindings_and_review_once(storage_env, monkeypatch):
    service, database = storage_env
    left, right = entry(service, 'a', 11), entry(service, 'b', 12)
    monkeypatch.setattr(service, '_merge_entry_origins', lambda *_: (_ for _ in ()).throw(RuntimeError('origins failed')))
    with pytest.raises(RuntimeError, match='origins failed'):
        service.review_duplicate(left, right, 'duplicate')
    state = AutoSourcarrService().manual_groups_payload()
    assert state['manual_match_count'] == 2
    assert state['group_count'] == 1
    with database.catalog() as c:
        assert c.execute('SELECT COUNT(*) FROM duplicate_review_history').fetchone()[0] == 1
        assert [tuple(r) for r in c.execute('SELECT match_type,locked FROM source_matches ORDER BY source_id')] == [('manual', 1)] * 2


@pytest.mark.parametrize('full', [False, True])
def test_catalogue_reset_discards_pending_origin_journal(storage_env, monkeypatch, full):
    service, database = storage_env
    left, right = entry(service, 'a', 11), entry(service, 'b', 12)
    monkeypatch.setattr(service, '_merge_entry_origins', lambda *_: (_ for _ in ()).throw(RuntimeError('origins failed')))
    with pytest.raises(RuntimeError):
        service.merge_entries(left, right)
    assert service.pending_entry_merge_count() == 1
    (service.full_reset_database if full else service.reset_database)()
    assert service.pending_entry_merge_count() == 0
    assert AutoSourcarrService().reconcile_pending_entry_merges()['checked'] == 0


def test_committed_merge_invalidates_duplicate_snapshot_even_when_origins_fail(storage_env, monkeypatch):
    service, database = storage_env
    left, right = entry(service, 'a', 11), entry(service, 'b', 12)
    service._duplicate_snapshot = (1, [])
    service._duplicate_snapshot_stale = False
    monkeypatch.setattr(service, '_merge_entry_origins', lambda *_: (_ for _ in ()).throw(RuntimeError('origins failed')))
    with pytest.raises(RuntimeError):
        service._merge_entries_automatic(left, right, 'fault injection')
    assert service._duplicate_snapshot_stale
    assert service.canonical_entry_id(right) == left


def test_public_unmatch_is_durable_across_remerge_restart_and_normal_reset(storage_env):
    service, database = storage_env
    original = entry(service, 'a', 11)
    service._upsert_match(original, 'b', payload('b', 12), 1)
    split = service.unmatch_duplicate_records(original, [{'source_id': 'b', 'source_manga_id': 12}])
    new = split['new_entry_id']
    assert service._is_manual_separate_record(original, 'b', 12)
    assert service._duplicate_review(original, new) == 'not_duplicate'
    assert not service._auto_merge_group_evidence(original, new)['eligible']
    assert len(set(service.consolidate_entries([original, new]).values())) == 2
    restarted = AutoSourcarrService()
    assert not restarted._auto_merge_group_evidence(original, new)['eligible']
    restarted.reset_database()
    assert restarted.restore_persistent_manual_matches()['restored'] == 2
    restored_original = restarted._find_existing_by_source('a', 11)
    restored_new = restarted._find_existing_by_source('b', 12)
    assert restored_original != restored_new
    assert restarted.canonical_entry_id(original) == restored_original
    assert restarted.canonical_entry_id(new) == restored_new
    assert restarted._is_manual_separate_record(restored_original, 'b', 12)
    assert restarted._duplicate_review(restored_original, restored_new) == 'not_duplicate'
    assert not restarted._auto_merge_group_evidence(restored_original, restored_new)['eligible']
    assert len(set(restarted.consolidate_entries([restored_original, restored_new]).values())) == 2


def test_public_unmatch_detaches_existing_manual_group_without_forgetting_remaining_members(storage_env):
    service, database = storage_env
    original = manual_pair(service)
    service._upsert_match(original, 'c', payload('c', 3), 1, 'manual', True)
    service._persist_manual_binding(original, 'c', payload('c', 3))
    before = service.manual_groups_payload()['groups'][0]['id']
    new = service.unmatch_duplicate_records(original, [{'source_id': 'b', 'source_manga_id': 2}])['new_entry_id']
    with database.catalog() as c:
        member = dict(c.execute("SELECT * FROM manual_match_bindings WHERE source_id='b'").fetchone())
        assert member['entry_id_hint'] == new
        assert member['group_id'] is None
        remaining = [r[0] for r in c.execute('SELECT source_id FROM manual_match_bindings WHERE group_id=? ORDER BY source_id', (before,))]
        assert remaining == ['a', 'c']
    assert AutoSourcarrService().manual_groups_payload()['groups'][0]['member_count'] == 2
    service.reset_database()
    service.restore_persistent_manual_matches()
    assert service._find_existing_by_source('a', 1) == service._find_existing_by_source('c', 3)
    assert service._find_existing_by_source('a', 1) != service._find_existing_by_source('b', 2)


def test_public_unmatch_source_insert_failure_rolls_back_entry_binding_and_review(storage_env):
    service, database = storage_env
    original = manual_pair(service)
    state = service.manual_groups_payload()
    with database.catalog() as c:
        c.execute("CREATE TRIGGER reject_split_match BEFORE INSERT ON source_matches WHEN NEW.entry_id<>" + str(original) + " BEGIN SELECT RAISE(ABORT,'split rejected'); END")
    with pytest.raises(sqlite3.IntegrityError, match='split rejected'):
        service.unmatch_duplicate_records(original, [{'source_id': 'b', 'source_manga_id': 2}])
    with database.catalog() as c:
        assert c.execute('SELECT COUNT(*) FROM entries').fetchone()[0] == 1
        assert c.execute('SELECT COUNT(*) FROM duplicate_split_history').fetchone()[0] == 0
        assert c.execute('SELECT COUNT(*) FROM duplicate_review_history').fetchone()[0] == 0
        assert c.execute('SELECT COUNT(*) FROM manual_separate_records').fetchone()[0] == 0
    assert service._find_existing_by_source('b', 2) == original
    assert service.manual_groups_payload() == state


@pytest.mark.parametrize('match_type', ['redo-anchor', 'auto-animation-split', 'split-duplicate'])
def test_internal_splits_preserve_redo_edition_semantics_without_manual_side_effects(storage_env, match_type):
    service, database = storage_env
    original = entry(service, 'a', 11)
    service._upsert_match(original, 'b', payload('b', 12), 1)
    new = service._split_duplicate_records(original, [{'source_id': 'b', 'source_manga_id': 12}], split_match_type=match_type)['new_entry_id']
    with database.catalog() as c:
        assert tuple(c.execute('SELECT match_type,locked FROM source_matches WHERE entry_id=?', (new,)).fetchone()) == (match_type, 0)
        assert c.execute('SELECT COUNT(*) FROM manual_match_bindings').fetchone()[0] == 0
        assert c.execute('SELECT COUNT(*) FROM manual_separate_records').fetchone()[0] == 0
        assert c.execute('SELECT COUNT(*) FROM duplicate_reviews').fetchone()[0] == 0


def test_pending_merge_recovery_does_not_reactivate_a_later_manual_split(storage_env, monkeypatch):
    service, database = storage_env
    left, right = entry(service, 'a', 11), entry(service, 'b', 12)
    chapter_origin(service, database, left, 'a', 11, 111)
    losing = chapter_origin(service, database, right, 'b', 12, 112)
    original_merge = service._merge_entry_origins
    monkeypatch.setattr(service, '_merge_entry_origins', lambda *_: (_ for _ in ()).throw(RuntimeError('origins failed')))
    with pytest.raises(RuntimeError):
        service._merge_entries_automatic(left, right, 'first merge')
    new = service.unmatch_duplicate_records(left, [{'source_id': 'b', 'source_manga_id': 12}])['new_entry_id']
    monkeypatch.setattr(service, '_merge_entry_origins', original_merge)
    assert service.reconcile_pending_entry_merges()['completed'] == 1
    assert service._find_existing_by_source('b', 12) == new
    assert service._duplicate_review(left, new) == 'not_duplicate'
    with database.origins() as o:
        assert o.execute('SELECT active FROM chapter_candidates WHERE id=?', (losing['id'],)).fetchone()[0] == 0
        assert o.execute("SELECT entry_id FROM source_record_scans WHERE source_id='b'").fetchone()[0] == new
        assert o.execute('SELECT COUNT(*) FROM origin_history').fetchone()[0] == 2


def set_source_colors(database):
    with database.catalog() as c:
        for sid, mid, mode in [('a', 11, 'monochrome'), ('b', 12, 'colored')]:
            c.execute('INSERT INTO source_color_profiles(source_id,source_manga_id,color_mode,confidence,analyzed_at) VALUES(?,?,?,?,?)',
                      (sid, mid, mode, .99, 'now'))


def test_public_split_recomputes_persisted_edition_consensus(storage_env):
    service, database = storage_env
    original = entry(service, 'a', 11)
    service._upsert_match(original, 'b', payload('b', 12), 1)
    set_source_colors(database)
    assert service._recompute_entry_color_profile(original)['color_mode'] == 'mixed'
    new = service.unmatch_duplicate_records(original, [{'source_id': 'b', 'source_manga_id': 12}])['new_entry_id']
    assert service._entry_color_profile(original)['color_mode'] == 'monochrome'
    assert service._entry_color_profile(new)['color_mode'] == 'colored'


def test_committed_manual_merge_recomputes_edition_consensus_before_origin_failure(storage_env, monkeypatch):
    service, database = storage_env
    left, right = entry(service, 'a', 11), entry(service, 'b', 12)
    set_source_colors(database)
    assert service._recompute_entry_color_profile(left)['color_mode'] == 'monochrome'
    assert service._recompute_entry_color_profile(right)['color_mode'] == 'colored'
    monkeypatch.setattr(service, '_merge_entry_origins', lambda *_: (_ for _ in ()).throw(RuntimeError('origins failed')))
    with pytest.raises(RuntimeError):
        service.review_duplicate(left, right, 'duplicate')
    assert service._entry_color_profile(left)['color_mode'] == 'mixed'


@pytest.mark.parametrize('repair', ['merges', 'attachments'])
def test_remaining_legacy_upgrade_repairs_consume_named_variant_metadata(storage_env, monkeypatch, repair):
    service, database = storage_env
    original = entry(service, 'a', 11)
    for mid in range(12, 19):
        service._upsert_source_variant(original, 'b', payload('b', mid, initialized=True, artist='Artist One'), 1)
    reorder_legacy_variants(database)
    captured = []
    if repair == 'merges':
        snapshot = service._record_repair_snapshot
        def record(row):
            captured.append(row)
            return snapshot(row)
        monkeypatch.setattr(service, '_record_repair_snapshot', record)
        monkeypatch.setattr(service, '_repair_records_directly_compatible', lambda *_: True)
        service._repair_unsafe_automatic_merges()
        variant = next(r for r in captured if r['source_id'] == 'b' and r['source_manga_id'] == 12)
        assert variant['description'] == DESCRIPTION
        assert variant['author'] == 'Author One'
        assert variant['artist'] == 'Artist One'
        assert variant['initialized'] == 1
    else:
        monkeypatch.setattr(service, '_trusted_automatic_entry_evidence', lambda *_: {
            'trusted_record_keys': {('a', 11)}, 'titles': [TITLE], 'descriptions': [DESCRIPTION],
            'author': 'Author One', 'gallery_payloads': [payload('a', 11)]})
        def evidence(*args, **kwargs):
            captured.append(kwargs['candidate_payload'])
            return {'eligible': True}
        monkeypatch.setattr(service, '_strict_automatic_title_evidence_base', evidence)
        service._repair_unsafe_automatic_source_attachments()
        variant = next(r for r in captured if r['id'] == 12)
        assert variant['description'] == DESCRIPTION
        assert variant['author'] == 'Author One'
        assert variant['artist'] == 'Artist One'
        assert variant['initialized'] is True


def test_direct_reset_manual_matches_forgets_public_split_locks_but_keeps_negative_decisions_and_internal_flags(storage_env):
    service, database = storage_env
    original = entry(service, 'a', 11)
    service._upsert_match(original, 'b', payload('b', 12), 1)
    new = service.unmatch_duplicate_records(original, [{'source_id': 'b', 'source_manga_id': 12}])['new_entry_id']
    internal_original = entry(service, 'c', 21)
    service._upsert_source_variant(internal_original, 'c', payload('c', 22), 1, 'auto-animation-split', True)
    internal_new = service._split_duplicate_records(internal_original, [{'source_id': 'c', 'source_manga_id': 22}], split_match_type='auto-animation-split')['new_entry_id']
    legacy = entry(service, 'c', 31)
    with database.catalog() as c:
        c.execute("UPDATE source_matches SET match_type='manual',locked=1 WHERE entry_id=?", (legacy,))
        assert c.execute('SELECT COUNT(*) FROM manual_match_bindings').fetchone()[0] == 2
    assert service.reset_manual_matches()['manual_matches_removed'] == 2
    with database.catalog() as c:
        assert c.execute('SELECT COUNT(*) FROM manual_match_bindings').fetchone()[0] == 0
        assert c.execute('SELECT COUNT(*) FROM manual_match_groups').fetchone()[0] == 0
        assert tuple(c.execute("SELECT match_type,locked FROM source_matches WHERE source_id='a' AND source_manga_id=11").fetchone()) == ('auto', 0)
        assert tuple(c.execute("SELECT match_type,locked FROM source_matches WHERE source_id='b' AND source_manga_id=12").fetchone()) == ('auto', 0)
        assert tuple(c.execute('SELECT match_type,locked FROM source_matches WHERE entry_id=?', (legacy,)).fetchone()) == ('auto', 0)
        assert tuple(c.execute('SELECT match_type,locked FROM source_matches WHERE entry_id=?', (internal_new,)).fetchone()) == ('auto-animation-split', 1)
    assert service._is_manual_separate_record(original, 'b', 12)
    assert service._duplicate_review(original, new) == 'not_duplicate'
    assert not service._auto_merge_group_evidence(original, new)['eligible']
