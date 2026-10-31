"""Local evidence review must not guess from absent metadata or mutate bindings."""
import pytest
from test_v087_recovery import env, source, manga


def attach(env, *, title_a='Dragon Castle (Manga)', title_b='Dragon Castle (Webtoon)', summaries=None, lang='en', variant=False):
    service, db = env
    source(db, 'a');source(db, 'b')
    with db.catalog() as c:
        c.execute('UPDATE source_config SET lang=?', (lang,))
    left = manga(31, title_a, description=summaries[0] if summaries else None)
    right = manga(32, title_b, description=summaries[1] if summaries else None)
    eid = service._create_entry(left, 'a')
    service._upsert_match(eid, 'a', left, 1, 'manual', True)
    if variant:
        service._upsert_source_variant(eid, 'a', right, 1, 'manual', True)
    else:
        service._upsert_match(eid, 'b', right, 1, 'manual', True)
    return service, db, eid


@pytest.mark.parametrize('variant', [False, True])
def test_detects_conflict_without_merge_history_including_locked_records(env, variant):
    s, db, eid = attach(env, variant=variant)
    with db.catalog() as c:
        assert c.execute('SELECT count(*) FROM entry_aliases').fetchone()[0] == 0
    result = s.mixed_entry_candidates()
    assert result['counts']['needs_review'] == 1
    detail = s.mixed_entry_detail(eid)
    assert detail['evidence'][0]['reasons'] == ['Different publication formats: manga / webtoon']
    assert all(record['locked'] for record in detail['records'])
    assert detail['records'][0]['source_name'] == 'a'


@pytest.mark.parametrize('titles,summaries', [
    (('Dragon Castle', 'Dragon Castle'), None),
    (('A Castle', 'Wholly Different'), ('', '')),
    (('Dragon Castle (Colored)', 'Dragon Castle'), None),
    (('One Translation', 'A Different Translation'), None),
])
def test_missing_or_compatible_evidence_is_not_a_conflict(env, titles, summaries):
    s, db, eid = attach(env, title_a=titles[0], title_b=titles[1], summaries=summaries)
    assert s.mixed_entry_candidates()['total'] == 0
    assert s.mixed_entry_candidates(state='all')['total'] == 1


def test_two_unrelated_works_are_flagged_but_not_cross_language(env):
    s, db, eid = attach(env, title_a='Interstellar Fleet', title_b='Rose Garden', summaries=(
        'A starship captain navigates distant galaxies with her alien crew. Galactic war threatens the federation as smugglers steal a planet destroying weapon.',
        'A lonely florist opens a tiny shop in a quiet village. She grows beautiful roses and falls in love with a childhood friend while preparing wedding bouquets.'))
    assert s.mixed_entry_candidates()['total'] == 1
    with db.catalog() as c:
        c.execute("UPDATE source_config SET lang='ja' WHERE source_id='b'")
    assert s.mixed_entry_candidates()['total'] == 0


def test_review_tracks_exact_evidence_not_covers_and_can_be_undone(env):
    s, db, eid = attach(env)
    detail = s.mixed_entry_detail(eid)
    s.review_mixed_entry(eid, detail['fingerprint'], 'keep')
    assert s.mixed_entry_candidates()['total'] == 0
    assert s.mixed_entry_candidates(state='reviewed')['total'] == 1
    with db.catalog() as c:
        c.execute("UPDATE source_matches SET thumbnail_url='new-cover',last_checked='later' WHERE entry_id=?", (eid,))
    assert s.mixed_entry_detail(eid)['fingerprint'] == detail['fingerprint']
    s.review_mixed_entry(eid, detail['fingerprint'], 'clear')
    assert s.mixed_entry_candidates()['total'] == 1
    s.review_mixed_entry(eid, detail['fingerprint'], 'keep')
    with db.catalog() as c:
        c.execute("UPDATE source_matches SET source_title='Different Castle (Webtoon)' WHERE source_id='b'")
    assert s.mixed_entry_candidates()['total'] == 1
    with pytest.raises(ValueError, match='evidence changed'):
        s.review_mixed_entry(eid, detail['fingerprint'], 'keep')
    with db.catalog() as c:
        assert c.execute('SELECT count(*) FROM source_matches WHERE entry_id=? AND active=1', (eid,)).fetchone()[0] == 2


def test_pagination_search_and_no_remote_work(env, monkeypatch):
    s, db, eid = attach(env)
    monkeypatch.setattr(s, 'get_entry', lambda *a: pytest.fail('No per-entry hydrations'))
    monkeypatch.setattr(s, '_entry_color_profile', lambda *a: pytest.fail('No writes or page sampling'))
    result = s.mixed_entry_candidates(query='webtoon', offset=50, limit=1)
    assert result['offset'] == 0
    assert result['items'][0]['entry_id'] == eid
    assert 'records' not in result['items'][0]
    assert s.mixed_entry_candidates(query='absent')['total'] == 0


def test_review_survives_catalog_reset_and_full_reset_clears_it(env):
    s, db, eid = attach(env)
    detail = s.mixed_entry_detail(eid)
    s.review_mixed_entry(eid, detail['fingerprint'], 'keep')
    s.reset_database()
    with db.catalog() as c:
        assert c.execute('SELECT count(*) FROM mixed_entry_reviews').fetchone()[0] == 1
    s.full_reset_database()
    with db.catalog() as c:
        assert c.execute('SELECT count(*) FROM mixed_entry_reviews').fetchone()[0] == 0


def test_retained_variant_table_column_order_is_not_assumed(env):
    s, db, eid = attach(env, variant=True)
    expected = s.mixed_entry_detail(eid)
    with db.catalog() as c:
        columns = [row[1] for row in c.execute('PRAGMA table_info(source_variants)')]
        tail = ['match_type', 'locked', 'initialized']
        columns = [name for name in columns if name not in tail] + tail
        c.execute('ALTER TABLE source_variants RENAME TO old_variants')
        c.execute('CREATE TABLE source_variants AS SELECT ' + ','.join(columns) + ' FROM old_variants')
        c.execute('DROP TABLE old_variants')
    assert s.mixed_entry_detail(eid) == expected


def test_unchanged_evidence_reuses_small_assessment_and_changes_invalidate(env, monkeypatch):
    from app import mixed_entries
    s, db, eid = attach(env)
    mixed_entries._assessments.clear()
    original = mixed_entries._assess
    calls = []
    def counted(records):
        calls.append(True)
        return original(records)
    monkeypatch.setattr(mixed_entries, '_assess', counted)
    s.mixed_entry_candidates();s.mixed_entry_candidates(state='all');s.mixed_entry_detail(eid)
    assert len(calls) == 1
    with db.catalog() as c:
        c.execute("UPDATE source_matches SET source_title='Other Castle (Webtoon)' WHERE source_id='b'")
    s.mixed_entry_candidates()
    assert len(calls) == 2
