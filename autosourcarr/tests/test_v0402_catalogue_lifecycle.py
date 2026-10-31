"""Independent instance fixtures exercise mappings, decisions and durable recovery."""
import copy
import json
import sqlite3
from contextlib import closing

import pytest

from test_v0402_catalogue_exchange import worlds
from app import catalog_exchange_format as wire, catalog_identity, gallery_policy, identity_review
from app.config import settings
from app.db import Database
from app.instance_backup import InstanceBackups


def encode(bundle):
    return wire.canonical(bundle).encode('utf-8')


def choice(kind, entries=(), records=(), **value):
    return dict(uid=wire.uid('decision'), kind=kind, entries=list(entries), records=list(records), value=value)


def test_native_manual_choices_export_and_map_to_receiver_record_numbers(worlds):
    a, b = worlds('a'), worlds('b')
    with a.active():
        one = a.native('First manual work', '/one', 112)
        two = a.native('Second manual work', '/two', 113)
        a.service.set_preferred_title(one, 'My saved title')
        a.service._persist_manual_binding(one, '17', dict(id=112, title='First manual work', url='/one'), ensure_group=False)
        a.service._remember_manual_separate_record(one, '17', 113, separate_entry_id=two, source_title='Second manual work')
        a.service.review_duplicate(one, two, 'not_duplicate')
        with a.db.catalog() as c:
            c.execute("INSERT INTO import_match_rejections VALUES('public-backup','source-title:rejected','Backup title','Public source','Rejected title','now')")
            c.execute("INSERT INTO import_manual_autoresolve(backup_key,backup_title,backup_source,target_source_id,target_source_manga_id,target_entry_id_hint,created_at,updated_at) VALUES('public-backup','Backup title','Public source','17',112,?,'now','now')", (one,))
        review = identity_review.read(a.db, a.service, one)
        review = identity_review.save(a.db, a.service, one, 'asc1:work:anilist:123', 'needs-review', review['revision'], 'Source publication')
        identity_review.save(a.db, a.service, one, 'asc1:work:anilist:123', 'confirmed', review['revision'], 'Confirmed by sender')
        raw = a.dump()
        kinds = {r['kind'] for r in json.loads(raw)['decisions']}
        assert {'title', 'source-match', 'entry-separate', 'separate', 'import-choice', 'import-rejection', 'identity-review'} <= kinds
    with b.active():
        local_one = b.native('First manual work', '/one', 901)
        local_two = b.native('Second manual work', '/two', 902)
        result = b.add(raw)
        assert result['added'] == 0 and result['decisions_applied'] >= 7
        assert b.service.get_entry(local_one)['preferred_title'] == 'My saved title'
        with b.db.catalog() as c:
            assert c.execute('SELECT source_manga_id FROM manual_match_bindings').fetchone()[0] == 901
            assert c.execute('SELECT target_source_manga_id FROM import_manual_autoresolve').fetchone()[0] == 901
            assert c.execute('SELECT source_manga_id FROM manual_separate_records').fetchone()[0] == 902
            assert c.execute('SELECT decision FROM portable_identity_reviews').fetchone()[0] == 'needs-review'
            assert c.execute('SELECT COUNT(*) FROM import_match_rejections').fetchone()[0] == 1
        assert b.service._duplicate_review(local_one, local_two) == 'not_duplicate'
        forwarded = json.loads(b.dump())['decisions']
        original = json.loads(raw)['decisions']
        assert {(r['uid'], wire.digest(r)) for r in original} <= {(r['uid'], wire.digest(r)) for r in forwarded}
        assert len(forwarded) == len(original)


def test_group_needs_explicit_local_review_and_preserves_local_separate(worlds):
    a, b = worlds('a'), worlds('b')
    with a.active():
        a.native('First published heading', '/one', 111)
        a.native('Alternate published heading', '/two', 112)
        bundle = json.loads(a.dump())
        bundle['decisions'] = [choice('group', [r['uid'] for r in bundle['entries']], [r['sources'][0] for r in bundle['entries']], group_title='Same publication')]
        raw = encode(bundle)
    with b.active():
        one = b.native('First published heading', '/one', 901)
        two = b.native('Alternate published heading', '/two', 902)
        b.add(raw)
        pending = b.exchange.decisions()['items'][0]
        assert pending['state'] == 'pending' and pending['can_apply'] and b.count() == 2
        b.service.review_duplicate(one, two, 'not_duplicate')
        pending = b.exchange.decisions()['items'][0]
        with pytest.raises(ValueError, match='Separate'):
            b.exchange.apply_decision(pending['uid'], pending['digest'], pending['revision'])
        assert b.count() == 2
        b.service.review_duplicate(one, two, 'clear')
        pending = b.exchange.decisions()['items'][0]
        assert b.exchange.apply_decision(pending['uid'], pending['digest'], pending['revision'])['state'] == 'applied'
        assert b.count() == 1
        with b.db.catalog() as c:
            rows = [dict(r) for r in c.execute('SELECT * FROM manual_match_bindings')]
            assert {r['source_manga_id'] for r in rows} == {901, 902}
            assert len({r['group_id'] for r in rows}) == 1
            assert all(len(r['group_id']) == 32 for r in rows)


def test_reviewed_link_adds_real_local_source_without_foreign_numbers(worlds):
    a, b = worlds('a'), worlds('b')
    with a.active():
        a.native('Shared native work', '/one', 111)
        raw = a.dump()
    with b.active():
        b.add(raw)
        imported = 1
        native = b.native('Shared native work', '/one', 901)
        row = b.exchange.records()['items'][0]
        assert row['can_link'] and row['local_candidates'][0]['id'] == native
        prepared = b.exchange.prepare_link(row['uid'], row['digest'], row['revision'], native)
        assert b.exchange.apply_decision(prepared['uid'], prepared['digest'], prepared['revision'])['state'] == 'applied'
        assert b.count() == 1
        assert b.service.canonical_entry_id(imported) == native
        assert b.service.canonical_entry_id(native) == native
        with b.db.catalog() as c:
            assert c.execute('SELECT source_manga_id FROM source_matches').fetchone()[0] == 901
            assert c.execute('SELECT source_manga_id FROM manual_match_bindings').fetchone()[0] == 901


def test_conflicting_uid_separate_correction_survives_reexport_and_third_instance(worlds):
    a, b, c = worlds('a'), worlds('b'), worlds('c')
    with a.active():
        a.native('Original record', '/original', 111)
        original = json.loads(a.dump())
    damaged = copy.deepcopy(original)
    incoming = damaged['entries'][0]
    ref = 'asc1:record:tachiyomi:17:L2RpZmZlcmVudA'  # /different
    incoming['title'] = 'Different record'
    incoming['sources'][0].update(reference=ref, title='Different record', raw_title='Different record')
    incoming['references'] = [ref]
    for lineage in incoming['lineage']:
        for record in lineage['records']:
            record['reference'] = ref
    with b.active():
        b.add(encode(original))
        assert b.add(encode(damaged))['retained'] == 1 and b.count() == 1
        row = next(r for r in b.exchange.records()['items'] if r['state'] == 'review')
        result = b.exchange.review_record(row['uid'], row['digest'], row['revision'])
        assert result['added'] == 1 and b.count() == 2
        assert b.add(encode(damaged))['added'] == 0 and b.count() == 2
        forwarded = b.dump()
    with c.active():
        c.add(forwarded)
        assert c.count() == 2
        rows = c.exchange.export()['catalogue']['entries']
        assert len({r['uid'] for r in rows}) == 2
        assert c.add(forwarded)['added'] == 0 and c.count() == 2


def test_same_file_source_overlap_reuses_one_entry_and_keeps_versions(worlds):
    a, b = worlds('a'), worlds('b')
    with a.active():
        a.native('Native publication', '/one')
        bundle = json.loads(a.dump())
    other = copy.deepcopy(bundle['entries'][0])
    other['uid'] = wire.uid()
    other['aliases'] = []
    other['lineage'] = [{'uid': other['uid'], 'records': [{'source_id': '17', 'reference': other['sources'][0]['reference']}]}]
    other['title'] = 'Other sender metadata'
    bundle['entries'].append(other)
    with b.active():
        result = b.add(encode(bundle))
        assert result['added'] == 1 and result['reused'] == 1 and b.count() == 1
        assert b.service.get_entry(1)['preferred_title'] == 'Native publication'
        assert b.exchange.records()['total'] == 2


def test_foreign_alias_cannot_attach_a_disjoint_record_to_local_entry(worlds):
    a, b = worlds('a'), worlds('b')
    with a.active():
        a.native('First native publication', '/one')
        original = json.loads(a.dump())
    with b.active():
        b.add(encode(original))
        incoming = copy.deepcopy(original)
        record = incoming['entries'][0]
        record['aliases'] = [record['uid']]
        record['uid'] = wire.uid()
        reference = 'asc1:record:tachiyomi:17:L3R3bw'  # /two
        record['sources'][0]['reference'] = reference
        record['references'] = [reference]
        record['lineage'] = [{'uid': record['uid'], 'records': [{'source_id': '17', 'reference': reference}]}]
        result = b.add(encode(incoming))
        assert result['retained'] == 1 and b.count() == 1
        with b.db.catalog() as c:
            assert not c.execute('SELECT 1 FROM catalogue_source_facts WHERE reference=?', (reference,)).fetchone()


def gallery_bundle(world):
    with world.db.catalog() as c:
        c.execute("INSERT INTO source_config(source_id,name,last_seen) VALUES('17','E-Hentai','now')")
    world.service._reload_source_cache()
    for mid in (11, 12):
        world.service.ingest_manga('17', dict(id=mid, title=f'[Fixture Artist] Quiet Garden Part {mid-10} [English]',
            url=f'/gallery/{mid}', genre=['artist:Fixture Artist', 'fantasy', 'adventure'], initialized=True))
    return world.dump()


def set_gallery(world, **changes):
    state = gallery_policy.read(world.db, '17')
    gallery_policy.save(world.db, world.service, '17', {**state['policy'], **changes}, state['revision'])
    return world.service.apply_gallery_layout('17')


def test_shared_only_gallery_toggle_splits_then_chapters_family_can_recombine(worlds):
    a, b = worlds('a'), worlds('b')
    with a.active():
        raw = gallery_bundle(a)
    with b.active():
        b.add(raw)
        assert b.count() == 1
        assert set_gallery(b, enabled=False)['splits'] == 1 and b.count() == 2
        assert set_gallery(b, enabled=True)['merged'] == 1 and b.count() == 1
        assert set_gallery(b, enabled=False)['splits'] == 1 and b.count() == 2
        with b.db.catalog() as c:
            assert c.execute('SELECT COUNT(*) FROM source_matches').fetchone()[0] == 0
        assert b.add(raw)['added'] == 0 and b.count() == 2


def test_manual_gallery_restore_cannot_recombine_when_policy_is_off(worlds):
    a = worlds('a')
    with a.active():
        gallery_bundle(a)
        with a.db.catalog() as c:
            c.execute("INSERT INTO manual_match_groups VALUES('fixture-group','Quiet Garden','now','now')")
            rows = [dict(r) for table in ('source_matches', 'source_variants') for r in c.execute('SELECT * FROM '+table+' WHERE active=1')]
        for row in rows:
            a.service._persist_manual_binding(row['entry_id'], '17', a.service._record_payload(row), group_id='fixture-group', ensure_group=False)
        set_gallery(a, enabled=False)
        assert a.count() == 2
        a.service.reset_database()
        result = a.service.restore_persistent_manual_matches()
        assert result['restored'] == 2 and a.count() == 2
        with a.db.catalog() as c:
            assert len({r[0] for r in c.execute('SELECT entry_id_hint FROM manual_match_bindings')}) == 2


def test_private_values_and_local_profiles_never_enter_share_file(worlds):
    a = worlds('a')
    with a.active():
        eid = a.native('Public title', '/one')
        with a.db.catalog() as c:
            c.execute("UPDATE entries SET description='Bearer fixture-secret-password https://account.invalid/?token=fixture-hidden-token email@example.com' WHERE id=?", (eid,))
            c.execute("UPDATE source_matches SET metadata_json=? WHERE entry_id=?", (json.dumps({'cookie': 'fixture-cookie-secret', 'token': 'fixture-json-secret', 'id': 112}), eid))
            c.execute("UPDATE source_config SET base_url='https://user:fixture-password@private.invalid' WHERE source_id='17'")
        raw = a.dump()
        for secret in (b'fixture-secret-password', b'fixture-hidden-token', b'email@example.com', b'fixture-cookie-secret', b'fixture-json-secret', b'fixture-password'):
            assert secret not in raw
        for key in ('source_manga_id', 'entry_id', 'reading_progress', 'cookie', 'base_url'):
            assert ('"'+key+'"').encode() not in raw
    with pytest.raises(ValueError):
        wire.reference('asc1:record:tachiyomi:17:L2cvMTIzL2FiY2RlZmdoaWo')  # EH-style access token


def test_reopen_schema_backup_and_resets_preserve_the_correct_scopes(worlds, tmp_path):
    a, b = worlds('a'), worlds('b')
    with a.active():
        a.native('Shared work', '/one')
        raw = a.dump()
    with b.active():
        b.add(raw)
        bundle = b.exchange.export()['catalogue']
        origin = bundle['origin']
        ids = {r['uid'] for r in bundle['entries']}
        reopened = Database()
        try:
            with reopened.catalog() as c:
                assert not c.execute('PRAGMA foreign_key_check').fetchall()
                assert {r[0] for r in c.execute('SELECT uid FROM catalogue_entry_ids')} >= ids
                assert c.execute('PRAGMA integrity_check').fetchone()[0] == 'ok'
        finally:
            reopened.close_current_thread()
        roots = {name: tmp_path/'backup'/name for name in ('config','data','db')}
        for root in roots.values():
            root.mkdir(parents=True)
        (roots['config']/'autosourcarr.env').write_text('FIXTURE=local')
        for path in (settings.catalog_path, settings.origins_path):
            with closing(sqlite3.connect(path)) as src, closing(sqlite3.connect(roots['db']/path.name)) as dst:
                src.backup(dst)
        backup = InstanceBackups(roots, '0.4.2')
        saved = backup.create('catalogue fixture passphrase')
        staged = backup.stage(backup.export(saved['id']).read_bytes(), 'catalogue fixture passphrase')
        backup.arm(staged['id'], staged['revision'], 'RESTORE THIS INSTANCE')
        backup.apply_pending()
        with closing(sqlite3.connect(roots['db']/'catalog.sqlite')) as c:
            assert {r[0] for r in c.execute('SELECT uid FROM catalogue_entry_ids')} >= ids
        b.service.reset_database()
        assert b.count() == 0
        assert b.add(raw)['added'] == 1 and b.count() == 1
        assert {r['uid'] for r in b.exchange.export()['catalogue']['entries']} == ids
        b.service.full_reset_database()
        with b.db.catalog() as c:
            assert c.execute('SELECT COUNT(*) FROM catalogue_entry_ids').fetchone()[0] == 0
            assert c.execute('SELECT COUNT(*) FROM catalogue_decisions').fetchone()[0] == 0
            assert c.execute('SELECT uid FROM catalogue_origin').fetchone()[0] == origin


def test_invalid_decision_shapes_are_rejected_before_staging(worlds):
    a = worlds('a')
    with a.active():
        a.native('Work', '/one')
        bundle = json.loads(a.dump())
    for malformed in (choice('identity-review', [bundle['entries'][0]['uid']]), {**choice('title'), 'kind': {}}, choice('source-match', records=bundle['entries'][0]['sources']*2)):
        candidate = {**bundle, 'decisions': [malformed]}
        with pytest.raises(ValueError):
            wire.decode(encode(candidate))
