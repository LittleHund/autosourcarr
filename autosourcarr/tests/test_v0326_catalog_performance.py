"""Indexed exact-title parity, selected policy work and bounded tag responses."""
import asyncio
import json
import sqlite3
import threading
from collections import defaultdict
from difflib import SequenceMatcher

import pytest

from app import matcher, matching_policies as policies, tag_store
from app.config import settings
from app.db import Database
from test_v087_recovery import env, entry, source, manga


@pytest.fixture
def tags(env,monkeypatch):
    monkeypatch.setattr(tag_store,'db',env[1])
    tag_store.invalidate()
    return env


def insert_titles(database,titles,*,updated='2026-01-01'):
    with database.catalog() as c:
        ids=[]
        for title in titles:
            ids.append(c.execute('INSERT INTO entries(preferred_title,created_at,updated_at) VALUES(?,?,?)',
                (title,'2025-01-01',updated)).lastrowid)
    return ids


def test_exact_title_index_preserves_unicode_alias_order_limits_and_ties(env,monkeypatch):
    service,database=env
    texts=['Fullwidth Ａ & B','fullwidth A and B','Fullwidth A—B','東京の猫','Tōkyō no neko','Café ½']
    ids=insert_titles(database,texts)
    assert service._exact_search_entry_ids('ＦＵＬＬＷＩＤＴＨ a & b')==[ids[1],ids[0]]
    assert service._exact_search_entry_ids('Fullwidth A—B')==[ids[2]]
    assert service._exact_search_entry_ids('東京の猫')==[ids[3]]
    assert service._exact_search_entry_ids('Cafe 1/2')==[ids[5]]
    with database.catalog() as c:
        c.execute("INSERT INTO titles(entry_id,title,normalized) VALUES(?,?,?)",
            (ids[0],texts[0],matcher.normalize_title(texts[0])))
    assert service._exact_search_entry_ids(texts[0],limit=2)==[ids[0],ids[1]]
    monkeypatch.setattr(service,'canonical_entry_id',lambda eid:ids[0] if eid==ids[1] else eid)
    # The historical limit is applied before canonical dedupe.
    assert service._exact_search_entry_ids(texts[0],limit=2)==[ids[0]]
    assert service._exact_search_entry_ids(texts[0],limit=0)==[]


def test_exact_title_index_keeps_source_filter_after_limit(env):
    service,database=env
    source(database,'chosen');source(database,'other')
    chosen=entry(service,'chosen',101,'One Known Work')
    other=entry(service,'other',102,'One Known Work')
    with database.catalog() as c:
        c.execute('DELETE FROM titles')
        c.execute("UPDATE entries SET updated_at='same'")
    assert service._exact_search_entry_ids('One Known Work',limit=1,source_ids=['chosen'])==[]
    assert service._exact_search_entry_ids('One Known Work',limit=2,source_ids=['chosen'])==[chosen]
    assert service._exact_search_entry_ids('One Known Work',limit=2)==[other,chosen]


def test_exact_title_clean_lookup_normalizes_only_query_and_uses_indexes(env,monkeypatch):
    service,database=env
    ids=insert_titles(database,[f'Unrelated title {i}' for i in range(2000)]+['東京の猫'])
    assert service._exact_search_entry_ids('東京の猫')==[ids[-1]]
    calls=[]
    import app.service as module
    normalize=module.normalize_title
    monkeypatch.setattr(module,'normalize_title',lambda value:(calls.append(value),normalize(value))[1])
    monkeypatch.setattr(matcher,'normalize_title',lambda value:(_ for _ in ()).throw(AssertionError('Clean title index recomputed')))
    assert service._exact_search_entry_ids('東京の猫')==[ids[-1]]
    assert calls==['東京の猫']
    with database.catalog() as c:
        plan=[r['detail'] for r in c.execute('''EXPLAIN QUERY PLAN SELECT e.id,p.raw_title,p.normalized
            FROM entry_preferred_titles p JOIN entries e ON e.id=p.entry_id
            WHERE p.normalized=? OR p.normalized IS NULL ORDER BY e.updated_at DESC,e.id DESC''',
            (normalize('東京の猫'),))]
    assert any('SEARCH p USING INDEX idx_entry_preferred_titles_normalized' in part for part in plan),plan
    assert not any('SCAN e' in part for part in plan),plan


def test_preferred_index_legacy_migration_and_external_sql_insert_rename_delete(env):
    service,database=env
    with database.catalog() as c:
        for name in ('insert','update','delete'):c.execute('DROP TRIGGER entry_preferred_titles_'+name)
        c.execute('DROP TABLE entry_preferred_titles')
    ids=insert_titles(database,['Older Ａ & B','東京の猫'])
    migrated=Database()
    try:
        assert service._exact_search_entry_ids('Older A and B')==[ids[0]]
        with sqlite3.connect(settings.catalog_path) as external:
            external.execute("UPDATE entries SET preferred_title='Renamed Café' WHERE id=?",(ids[0],))
            external.execute('DELETE FROM entries WHERE id=?',(ids[1],))
            new=external.execute("INSERT INTO entries(preferred_title,created_at,updated_at) VALUES('Brand New Work','now','now')").lastrowid
            assert external.execute('PRAGMA quick_check').fetchone()[0]=='ok'
        assert service._exact_search_entry_ids('Older A and B')==[]
        assert service._exact_search_entry_ids('Renamed Cafe')==[ids[0]]
        assert service._exact_search_entry_ids('東京の猫')==[]
        assert service._exact_search_entry_ids('Brand New Work')==[new]
        with database.catalog() as c:
            assert c.execute('SELECT count(*) FROM entry_preferred_titles').fetchone()[0]==2
    finally:migrated.close_current_thread()


@pytest.mark.parametrize('workers',[1,2])
def test_preferred_index_cas_does_not_publish_a_stale_rename_snapshot(env,monkeypatch,workers):
    service,database=env
    (eid,)=insert_titles(database,['Old Work'])
    captured,release=threading.Event(),threading.Event()
    normalize=matcher.normalize_title
    count=0
    count_lock=threading.Lock()
    def delayed(value):
        nonlocal count
        if value=='Old Work':
            with count_lock:
                count+=1
                if count==workers:captured.set()
            assert release.wait(2)
        return normalize(value)
    monkeypatch.setattr(matcher,'normalize_title',delayed)
    errors=[]
    def backfill():
        try:
            with database.catalog() as c:database.refresh_preferred_title_index(c)
        except BaseException as exc:errors.append(exc)
        finally:database.close_current_thread()
    pool=[threading.Thread(target=backfill) for _ in range(workers)]
    for worker in pool:worker.start()
    try:
        assert captured.wait(1)
        with sqlite3.connect(settings.catalog_path) as external:
            external.execute("UPDATE entries SET preferred_title='New Work' WHERE id=?",(eid,))
        release.set()
        for worker in pool:worker.join(2)
        assert all(not worker.is_alive() for worker in pool) and not errors
        with database.catalog() as c:
            row=c.execute('SELECT raw_title,normalized FROM entry_preferred_titles WHERE entry_id=?',(eid,)).fetchone()
            assert row['raw_title']=='New Work' and row['normalized'] is None
        assert service._exact_search_entry_ids('Old Work')==[]
        assert service._exact_search_entry_ids('New Work')==[eid]
    finally:
        release.set()
        for worker in pool:worker.join(2)


def test_exact_lookup_covers_dirty_rows_created_after_its_refresh_snapshot(env,monkeypatch):
    service,database=env
    (eid,)=insert_titles(database,['Old Work'])
    assert service._exact_search_entry_ids('Old Work')==[eid]
    refresh=database.refresh_preferred_title_index
    def rename_after_refresh(c):
        refresh(c)
        with sqlite3.connect(settings.catalog_path) as external:
            external.execute("UPDATE entries SET preferred_title='New Work' WHERE id=?",(eid,))
    monkeypatch.setattr(database,'refresh_preferred_title_index',rename_after_refresh)
    assert service._exact_search_entry_ids('New Work')==[eid]
    monkeypatch.setattr(database,'refresh_preferred_title_index',refresh)
    assert service._exact_search_entry_ids('Old Work')==[]


def test_preferred_index_survives_sqlite_backup_and_both_reset_types(env,tmp_path):
    service,database=env
    insert_titles(database,['Café'])
    assert service._exact_search_entry_ids('Cafe')
    target=tmp_path/'moved'/'catalog.sqlite3'
    assert Database._backup_database_file(settings.catalog_path,target)>0
    with sqlite3.connect(target) as copied:
        assert copied.execute('PRAGMA quick_check').fetchone()[0]=='ok'
        eid=copied.execute("INSERT INTO entries(preferred_title,created_at,updated_at) VALUES('New Work','now','now')").lastrowid
        assert copied.execute('SELECT raw_title,normalized FROM entry_preferred_titles WHERE entry_id=?',(eid,)).fetchone()==('New Work',None)
    service.reset_database()
    with database.catalog() as c:assert c.execute('SELECT count(*) FROM entry_preferred_titles').fetchone()[0]==0
    insert_titles(database,['Next Work'])
    service.full_reset_database()
    with database.catalog() as c:assert c.execute('SELECT count(*) FROM entry_preferred_titles').fetchone()[0]==0


def set_rule(database,sid,checks):
    with database.catalog() as c:
        c.execute("INSERT INTO matching_policies VALUES('source',?,'external',?,'now')",
            (sid,json.dumps({'enabled':True,'checks':checks})))
    policies.invalidate(database)


def test_title_only_policy_skips_unselected_summary_author_and_tag_work(tags,monkeypatch):
    _,database=tags
    set_rule(database,'left',{'title':.94})
    def unselected(*a,**kw):raise AssertionError('Unselected policy metric executed')
    monkeypatch.setattr(policies,'best_description_score',unselected)
    monkeypatch.setattr(policies,'name_similarity',unselected)
    monkeypatch.setattr(tag_store,'canonical_keys',unselected)
    left={'sourceId':'left','title':'A Wonderful Complete Title','description':'Left summary','author':'Writer','genre':['sci-fi']}
    right={**left,'sourceId':'right'}
    evidence={'eligible':True,'title_score':1}
    result=policies.apply(database,{},evidence,[left],[right])
    assert result['eligible'] and [check['check'] for check in result['policy_checks']]==['title']
    assert policies.CHECKS['title']==.94
    assert policies.apply(database,{},dict(evidence,eligible=False),[left],[right])['eligible'] is False


def test_two_sided_selected_policy_metrics_keep_missing_and_canonical_tag_evidence(tags):
    _,database=tags
    set_rule(database,'left',{'tags':1})
    set_rule(database,'right',{'author':1})
    left={'sourceId':'left','title':'Same Work','author':'Writer','genre':['sci-fi']}
    right={'sourceId':'right','title':'Same Work','author':'Writer','genre':['science fiction']}
    result=policies.apply(database,{}, {'eligible':True,'title_score':1},[left],[right])
    assert result['eligible'] and {check['check'] for check in result['policy_checks']}=={'tags','author'}
    with database.catalog() as c:
        c.execute("UPDATE matching_policies SET rule_json=? WHERE subject='left'",(json.dumps({'enabled':True,'checks':{'title':1}}),))
    policies.invalidate(database)
    result=policies.apply(database,{}, {'eligible':True,'title_score':1,'author_similarity':1},[left],[dict(right,author='')])
    assert not result['eligible'] and result['policy_checks'][-1]['score'] is None


def insert_tags(database,names):
    with database.catalog() as c:
        ids={}
        for namespace,name in names:
            ids[(namespace,name)]=c.execute('INSERT INTO tags(namespace,name,normalized) VALUES(?,?,?)',
                (namespace,name,tag_store.normalize(name))).lastrowid
    tag_store.invalidate()
    return ids


def eager_suggestions(database,limit=100):
    """Behavior oracle using the previous eager alias projection."""
    with database.catalog() as c:
        rows,_=tag_store._catalog(c)
        reviewed={(r['left_id'],r['right_id']) for r in c.execute('SELECT * FROM tag_reviews')}
    result=[]
    for i,left in enumerate(rows):
        for right in rows[i+1:i+13]:
            if left['namespace']!=right['namespace']:break
            if tuple(sorted((left['id'],right['id']))) in reviewed or left['canonical_id']==right['canonical_id']:continue
            score=SequenceMatcher(None,left['normalized'],right['normalized']).ratio()
            if score>=.84:result.append({'left':left,'right':right,'score':round(score,3),
                'reason':'Similar spelling; review before treating these tags as equivalent'})
    result.sort(key=lambda row:-row['score'])
    return {'items':result[:max(1,min(limit,200))]}


def test_tag_page_and_suggestions_preserve_unicode_review_canonical_aliases_and_order(tags):
    _,database=tags
    ids=insert_tags(database,[('tag','science fiction'),('tag','sci-fi'),('artist','science fiction'),
        ('tag','spaceship'),('tag','spaceships'),('tag','spacecraft'),('tag','starcraft'),
        ('tag','ＴＯＫＹＯ_猫'),('female','spaceships')])
    tag_store.review(ids[('tag','spaceship')],ids[('tag','spacecraft')],'same')
    tag_store.review(ids[('tag','sci-fi')],ids[('tag','science fiction')],'different')
    with database.catalog() as c:rows,_=tag_store._catalog(c)
    expected=[r for r in rows if 'tag space' in tag_store.normalize(r['namespace']+' '+r['name'])][1:3]
    page=tag_store.list_tags('tag space',limit=2,offset=1)
    assert page['items']==[dict(row,source_count=0,entry_count=0) for row in expected]
    assert tag_store.list_tags('tokyo 猫')['total']==1
    assert tag_store.suggestions(limit=2)==eager_suggestions(database,limit=2)
    tag_store.review(ids[('tag','spaceship')],ids[('tag','spacecraft')],'clear')
    assert tag_store.suggestions(limit=5)==eager_suggestions(database,limit=5)


def test_tag_page_alias_allocations_are_limited_to_displayed_rows(tags,monkeypatch):
    _,database=tags
    ids=insert_tags(database,[('tag',f'Equivalent spelling {i:03}') for i in range(100)])
    values=list(ids.values())
    with database.catalog() as c:
        c.executemany("INSERT INTO tag_reviews VALUES(?,?,'same','now')",[(values[0],right) for right in values[1:]])
    tag_store.invalidate()
    hydrate=tag_store._hydrate_aliases
    observed=[]
    def capture(rows,selected):
        selected=list(selected)
        observed.append((len(rows),len(selected),sum('aliases' in row for row in rows)))
        hydrate(rows,selected)
        assert sum(len(row.get('aliases',[])) for row in rows)==len(selected)*99
    monkeypatch.setattr(tag_store,'_hydrate_aliases',capture)
    page=tag_store.list_tags(limit=3,offset=4)
    assert observed==[(100,3,0)]
    assert len(page['items'])==3 and all(len(row['aliases'])==99 for row in page['items'])
    assert all(row['canonical_id']==values[0] for row in page['items'])
    assert tag_store.suggestions()['items']==[]


@pytest.mark.asyncio
async def test_saturated_tag_suggestion_lane_keeps_ordinary_tag_reads_available(tags,monkeypatch):
    from app import main
    from app.read_lane import ReadLane
    _,database=tags
    insert_tags(database,[('tag','spaceship')])
    ordinary=ReadLane(workers=1);suggestions=ReadLane(workers=1)
    monkeypatch.setattr(main,'tag_suggestion_lane',suggestions)
    response=main.catalogue_response
    async def local_response(function,*args,lane=None,**kwargs):
        return await response(function,*args,lane=ordinary if lane is None else lane,**kwargs)
    monkeypatch.setattr(main,'catalogue_response',local_response)
    entered,release=threading.Event(),threading.Event()
    def slow_suggestions():
        entered.set()
        assert release.wait(2)
        return {'items':[]}
    monkeypatch.setattr(tag_store,'suggestions',slow_suggestions)
    jobs=[asyncio.create_task(main.admin_tag_suggestions()) for _ in range(3)]
    try:
        assert await asyncio.to_thread(entered.wait,1)
        page=await asyncio.wait_for(main.admin_tags('',1,0),.2)
        assert json.loads(page.body)['total']==1
        for job in jobs:job.cancel()
        await asyncio.sleep(0)
        assert not jobs[0].done(), 'Admitted suggestion work must drain after cancellation'
    finally:
        release.set()
        await asyncio.gather(*jobs,return_exceptions=True)
        await asyncio.gather(ordinary.close(),suggestions.close())
