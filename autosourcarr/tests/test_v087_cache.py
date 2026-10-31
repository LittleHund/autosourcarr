"""Bounded cache retrieval must retain exact/native/variant candidates."""
from app.service import now_iso
from test_v087_recovery import env, source, entry


def records(service, db, n=500):
    source(db, 'catalogued')
    eid = entry(service, 'seed', 0, 'Index Seed', attach=False)
    with db.catalog() as c:
        c.executemany('''INSERT INTO source_variants
            (entry_id,source_id,source_manga_id,source_manga_url,source_title,thumbnail_url,metadata_json,active,last_checked)
            VALUES(?,?,?,?,?,?,?,1,?)''',
            [(eid, 'catalogued', i+10000, f'/work/{i}', f'Monster Archive Record {i}', '', '{}', now_iso()) for i in range(n)])
    return eid


def test_shortlist_is_bounded_but_late_exact_title_is_kept(env):
    service, db = env; records(service, db)
    rows = service._cached_source_shortlist('catalogued', ['Monster Archive Record 499'])
    assert len(rows) <= 240
    assert any(r['source_manga_id'] == 10499 for r in rows)


def test_normalized_native_alias_can_retrieve_real_record(env):
    service, db = env; source(db, 'catalogued')
    eid = entry(service, 'catalogued', 777, 'Different Original Title')
    service.add_title(eid, '타임룰렛', title_type='source-alt')
    rows = service._cached_source_shortlist('catalogued', ['타임룰렛'])
    assert [x['source_manga_id'] for x in rows] == [777]


def test_primary_and_variant_shortlist_are_active_source_scoped(env):
    service, db = env; source(db, 'catalogued'); source(db, 'other')
    eid = entry(service, 'catalogued', 777, 'Monster List')
    entry(service, 'other', 999, 'Monster List')
    with db.catalog() as c:
        c.execute('UPDATE source_matches SET active=0 WHERE source_manga_id=777')
        c.execute('''INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_manga_url,source_title,active,last_checked)
                     VALUES(?,?,?,?,?,1,?)''', (eid, 'catalogued', 888, '/888', 'Monster List', now_iso()))
    assert [x['source_manga_id'] for x in service._cached_source_shortlist('catalogued', ['Monster List'])] == [888]


def test_cached_candidate_scoring_decodes_no_more_than_shortlist(env, monkeypatch):
    service, db = env; eid = records(service, db)
    calls = []; original = service._variant_payload
    def decode(row):
        calls.append(row['source_manga_id'])
        return original(row)
    monkeypatch.setattr(service, '_variant_payload', decode)
    result = service._cached_manual_source_candidates(eid, 'catalogued', ['Monster Archive Record 499'])
    assert 10499 in result
    assert 1 <= len(calls) <= 240 and len(result) <= 80
