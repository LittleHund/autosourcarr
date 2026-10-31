"""Candidate recall/manual exclusions and exact ingestion lookup plans."""
import pytest

from app.db import Database
from test_v087_recovery import env,source,entry


def test_manually_separated_anchor_does_not_hide_another_duplicate_pair(env):
    service,database = env
    ids=[]
    for index in range(3):
        sid='source-'+str(index)
        source(database,sid)
        ids.append(entry(service,sid,100+index,'An Unusual Chronicle of Forgotten Spirits'))
    a,b,c=ids
    with database.catalog() as connection:
        for left,right in ((a,b),(a,c)):
            connection.execute("INSERT INTO duplicate_reviews(left_entry_id,right_entry_id,decision,reviewed_at) VALUES(?,?,'not_duplicate','now')",(left,right))
    pairs,_,_=service._duplicate_shortlist()
    assert (b,c) in pairs
    assert (a,b) not in pairs and (a,c) not in pairs
    assert service._auto_merge_group_evidence(b,c)['eligible']
    assert not service._auto_merge_group_evidence(a,b)['eligible']


def test_alternative_duplicate_anchors_remain_linear_for_common_titles(env):
    service,database = env
    source(database,'shared')
    ids=[entry(service,'shared',1000+i,'One Distinctive Shared Archive Chronicle') for i in range(120)]
    with database.catalog() as connection:
        for other in ids[1:]:
            connection.execute("INSERT INTO duplicate_reviews(left_entry_id,right_entry_id,decision,reviewed_at) VALUES(?,?,'not_duplicate','now')",(ids[0],other))
    pairs,_,_=service._duplicate_shortlist()
    assert (ids[1],ids[2]) in pairs
    assert len(pairs) <= 5*len(ids)
    assert not any(ids[0] in pair for pair in pairs)


@pytest.mark.parametrize('query',[
    'SELECT id FROM entries WHERE lower(preferred_title)=lower(?) LIMIT 25',
    'SELECT DISTINCT entry_id FROM source_matches WHERE active=1 AND lower(source_title)=lower(?) LIMIT 25',
    'SELECT DISTINCT entry_id FROM source_variants WHERE active=1 AND lower(source_title)=lower(?) LIMIT 25',
])
def test_exact_candidate_title_queries_use_expression_searches(env,query):
    service,database = env
    with database.catalog() as connection:
        plan=[row['detail'] for row in connection.execute('EXPLAIN QUERY PLAN '+query,('Known Work',))]
    assert any('SEARCH' in step and '<expr>=?' in step for step in plan),plan


def test_ingestion_indexes_recreated_for_retained_catalog_without_changing_candidates(env):
    service,database = env
    source(database,'a');source(database,'b')
    ids=[entry(service,'a',21,'Known 東京 Work'),entry(service,'b',22,'Known 東京 Work')]
    queries=('Known 東京 Work','KNOWN 東京 WORK','東京','not present')
    before={q:(service._candidate_entries(q),service._candidate_entries_for_manga({'title':q})) for q in queries}
    with database.catalog() as connection:
        for name in ('idx_entries_lower_title','idx_matches_lower_title','idx_variants_lower_title'):
            connection.execute('DROP INDEX IF EXISTS '+name)
    migrated=Database()
    try:
        after={q:(service._candidate_entries(q),service._candidate_entries_for_manga({'title':q})) for q in queries}
        assert before==after
        assert set(before[queries[0]][0])==set(ids)
        with database.catalog() as connection:
            assert connection.execute('PRAGMA quick_check').fetchone()[0]=='ok'
            indexes={r['name'] for r in connection.execute("SELECT name FROM sqlite_master WHERE type='index'")}
            assert {'idx_entries_lower_title','idx_matches_lower_title','idx_variants_lower_title'} <= indexes
    finally:
        migrated.close_current_thread()


def test_expression_indexes_preserve_match_variant_case_identity_and_inactive_exclusions(env):
    service,database=env
    source(database,'a');source(database,'b');source(database,'c')
    ids=[entry(service,sid,40+i,'Other Work '+str(i)) for i,sid in enumerate(('a','b','c'))]
    query='One Exact 東京 Work'
    with database.catalog() as connection:
        connection.execute('DELETE FROM titles')
        connection.execute('UPDATE source_matches SET source_title=? WHERE entry_id=?',(query,ids[0]))
        connection.execute("INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,last_checked) VALUES(?,'variant',100,?,'now')",(ids[1],query.lower()))
        connection.execute("INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,active,last_checked) VALUES(?,'inactive',101,?,0,'now')",(ids[2],query))
        for name in ('idx_entries_lower_title','idx_matches_lower_title','idx_variants_lower_title'):
            connection.execute('DROP INDEX '+name)
    before=(service._candidate_entries(query),service._candidate_entries_for_manga({'title':query}))
    migrated=Database()
    try:
        after=(service._candidate_entries(query),service._candidate_entries_for_manga({'title':query}))
        assert before==after==([ids[0],ids[1]],[ids[0],ids[1]])
    finally:
        migrated.close_current_thread()
