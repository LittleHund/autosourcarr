"""Protect title recall and alias ordering while consolidating exact lookups."""
from app.matcher import normalize_title
from test_v087_recovery import env,source,entry


def test_exact_lookup_keeps_raw_limits_and_canonical_manga_limits_distinct(env):
    service,database=env
    source(database,'fixture')
    ids=[entry(service,'fixture',i,'Distinct Work '+str(i)) for i in range(3)]
    title='One Known Archive Chronicle'
    with database.catalog() as c:
        c.execute('DELETE FROM titles')
        for eid in ids[:2]:
            c.execute("INSERT INTO titles(entry_id,title,normalized,title_type) VALUES(?,?,?,'alias')",(eid,title,normalize_title(title)))
        c.execute('UPDATE entries SET preferred_title=? WHERE id=?',(title,ids[2]))
    service._alias_cache[ids[1]]=ids[0]
    assert service._candidate_entries(title,limit=2)==ids[:2]
    assert service._candidate_entries_for_manga({'title':title},limit=2)==[ids[0],ids[2]]


def test_trimmed_title_lookup_preserves_active_source_and_variant_recall(env):
    service,database=env
    source(database,'fixture')
    ids=[entry(service,'fixture',i,'Different Work '+str(i)) for i in range(3)]
    title='One Exact 東京 Chronicle'
    with database.catalog() as c:
        c.execute('DELETE FROM titles')
        c.execute('UPDATE source_matches SET source_title=? WHERE entry_id=?',(title,ids[0]))
        c.execute("INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,last_checked) VALUES(?,'other',20,?,'now')",(ids[1],title))
        c.execute("INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,active,last_checked) VALUES(?,'inactive',21,?,0,'now')",(ids[2],title))
    assert service._candidate_entries('  '+title+'  ')==ids[:2]
    assert service._candidate_entries_for_manga({'title':'  '+title+'  '})==ids[:2]
