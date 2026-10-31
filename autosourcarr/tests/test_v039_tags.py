import asyncio
import json
from unittest.mock import AsyncMock

import httpx
import pytest
from app import tag_store
from app.db import now_iso
from test_v087_recovery import env, source, manga


@pytest.fixture
def tags(env, monkeypatch):
    service, database = env
    monkeypatch.setattr(tag_store, 'db', database)
    tag_store.invalidate()
    return service, database


def save(database, sid, mid, values, **fields):
    with database.catalog() as c:
        tag_store.sync_source_record(c, sid, mid, {'title':'Quiet Garden', 'genre':values, 'initialized':True, **fields}, {})
    return {r['name']:r['id'] for r in tag_store.list_tags()['items']}


def keys(*names, ns='tag'):
    return tag_store.canonical_keys([{'namespace':ns,'name':n} for n in names])


def test_aliases_preserve_namespace_and_allow_explicit_separation(tags):
    _, d=tags
    ids=save(d,'a',1,['sci-fi','science fiction','artist: sci-fi','female: sci-fi'])
    assert len(keys('sci-fi','science fiction'))==1
    assert len(tag_store.canonical_keys([{'namespace':'tag','name':'sci-fi'}, {'namespace':'artist','name':'sci-fi'}, {'namespace':'female','name':'sci-fi'}]))==3
    raw=tag_store.list_tags()['items']
    genre={r['name']:r['id'] for r in raw if r['namespace']=='tag'}
    tag_store.review(genre['sci-fi'],genre['science fiction'],'different')
    assert len(keys('sci-fi','science fiction'))==2
    tag_store.review(genre['sci-fi'],genre['science fiction'],'clear')
    assert len(keys('sci-fi','science fiction'))==1


def test_manual_same_transitive_and_separate_constraints_are_authoritative(tags):
    _,d=tags
    ids=save(d,'a',1,['starships','spaceships','spacecraft'])
    tag_store.review(ids['starships'],ids['spaceships'],'same')
    tag_store.review(ids['spaceships'],ids['spacecraft'],'same')
    assert len(keys(*ids))==1
    with pytest.raises(ValueError,match='contradict'):
        tag_store.review(ids['starships'],ids['spacecraft'],'different')
    assert len(keys(*ids))==1  # failed transaction does not overwrite either review
    tag_store.review(ids['spaceships'],ids['spacecraft'],'clear')
    tag_store.review(ids['starships'],ids['spacecraft'],'different')
    with pytest.raises(ValueError,match='contradict'):
        tag_store.review(ids['spaceships'],ids['spacecraft'],'same')
    assert len(keys(*ids))==2


def test_namespace_combinations_rejected_and_normalization_idempotent(tags):
    _,d=tags
    save(d,'a',1,['artist: Sample Name','character: Sample Name','science_fiction','Science Fiction'])
    items=tag_store.list_tags()['items']
    assert len(items)==3
    with pytest.raises(ValueError,match='namespaces'):
        tag_store.review(items[0]['id'],items[1]['id'],'same')


def test_fuzzy_suggestions_never_change_matching_until_reviewed(tags):
    _,d=tags
    ids=save(d,'a',1,['spaceships','spaceship'])
    assert len(keys(*ids))==2
    result=tag_store.suggestions()
    assert len(result['items'])==1
    tag_store.review(ids['spaceships'],ids['spaceship'],'same')
    assert tag_store.suggestions()['items']==[]
    assert len(keys(*ids))==1


def test_record_tags_follow_active_bindings_and_keep_thin_metadata(tags):
    s,d=tags
    source(d,'a');source(d,'b')
    first=manga(41,'Quiet Garden');first['genre']=['science fiction','artist: Example Artist']
    second=manga(42,'Quiet Garden');second['genre']=['sci-fi']
    eid=s._create_entry(first,'a')
    s._upsert_match(eid,'a',first,1,'manual',True)
    s._upsert_match(eid,'b',second,1,'manual',True)
    with d.catalog() as c:
        tag_store.sync_source_record(c,'a',41,first)
        tag_store.sync_source_record(c,'b',42,second)
        tag_store.sync_source_record(c,'a',41,{'title':'Quiet Garden','initialized':False,'genre':[]})
    assert len(tag_store.entry_metadata(eid)['tags'])==4
    with d.catalog() as c:
        c.execute("UPDATE source_matches SET active=0 WHERE source_id='a'")
    result=tag_store.entry_metadata(eid)
    assert {r['name'] for r in result['tags']}=={'sci-fi','Example Writer'}
    assert next(r for r in tag_store.list_tags()['items'] if r['name']=='science fiction')['entry_count']==0


def test_normal_reset_keeps_reviews_full_reset_clears_them(tags):
    s,d=tags
    ids=save(d,'a',1,['spaceships','spaceship'])
    tag_store.review(ids['spaceships'],ids['spaceship'],'different')
    s.reset_database()
    assert len(tag_store.reviews()['items'])==1
    with d.catalog() as c:
        assert c.execute('SELECT count(*) FROM source_record_tags').fetchone()[0]==0
    s.full_reset_database()
    assert tag_store.reviews()['items']==[]
    assert tag_store.list_tags()['items']==[]


def test_saved_gallery_reindex_uses_local_metadata_only(tags):
    s,d=tags
    source(d,'a')
    with d.catalog() as c:
        c.execute("UPDATE source_config SET name='E-Hentai',base_url='https://e-hentai.org' WHERE source_id='a'")
    data=manga(51,'[Example Artist] Quiet Garden [English]')
    data['genre']=['tag: science fiction','character: Star Captain']
    eid=s._create_entry(data,'a');s._upsert_match(eid,'a',data,1,'manual',True)
    after,count=tag_store.reindex_batch('source_matches',0)
    assert count==1
    result=tag_store.entry_metadata(eid)
    assert result['gallery'][0]['raw_title']==data['title']
    assert result['gallery'][0]['artist']=='Example Artist'
    assert result['gallery'][0]['title']=='Quiet Garden'
    assert result['metadata_basis']=='source'
    assert any(r['namespace']=='character' for r in result['tags'])


def test_authenticated_tag_api_roundtrip(tags, monkeypatch):
    from app import main
    s,d=tags
    monkeypatch.setattr(main,'service',s)
    monkeypatch.setattr(main.settings,'admin_token','tag-secret')
    ids=save(d,'a',1,['spaceships','spaceship'])
    async def run():
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app),base_url='http://test') as client:
            assert (await client.get('/api/admin/tags')).status_code==401
            headers={'X-Admin-Token':'tag-secret'}
            listed=await client.get('/api/admin/tags?search=spaceship',headers=headers)
            assert listed.json()['total']==2
            response=await client.post('/api/admin/tags/review',headers=headers,json={'left_id':ids['spaceships'],'right_id':ids['spaceship'],'decision':'same'})
            assert response.status_code==200
            assert (await client.get('/api/admin/tags/reviews',headers=headers)).json()['items'][0]['decision']=='same'
    asyncio.run(run())


def test_gallery_thin_feed_additions_do_not_erase_hydrated_tags(tags):
    _,d=tags
    source_info={'name':'E-Hentai','base_url':'https://e-hentai.org'}
    full={'title':'[Example Artist] Quiet Garden [English]','initialized':True,
          'genre':['character: Star Captain','science fiction']}
    with d.catalog() as c:
        tag_store.sync_source_record(c,'a',81,full,source_info)
        before=c.execute('SELECT count(*) FROM source_record_tags').fetchone()[0]
        tag_store.sync_source_record(c,'a',81,{'title':full['title'],'initialized':False},source_info)
        assert c.execute('SELECT count(*) FROM source_record_tags').fetchone()[0]==before
        record=json.loads(c.execute('SELECT metadata_json FROM gallery_records').fetchone()[0])
        assert any(t['name']=='Star Captain' for t in record['tags'])
        tag_store.sync_source_record(c,'a',81,{**full,'genre':[]},source_info)
        assert not c.execute("SELECT 1 FROM source_record_tags st JOIN tags t ON t.id=st.tag_id WHERE t.name='Star Captain'").fetchone()


def test_unchanged_metadata_does_not_rewrite_tag_links(tags):
    _,d=tags
    data={'title':'Quiet Garden','genre':['science fiction','character: Star Captain'],'initialized':True}
    with d.catalog() as c:
        tag_store.sync_source_record(c,'a',91,data)
        changed=c.total_changes
        tag_store.sync_source_record(c,'a',91,data)
        assert c.total_changes==changed


def test_canonical_review_snapshot_uses_one_joined_read(tags):
    _,d=tags
    ids=save(d,'a',1,['spaceships','spaceship'])
    tag_store.review(ids['spaceships'],ids['spaceship'],'same')
    statements=[]
    with d.catalog() as c:
        c.set_trace_callback(statements.append)
        try:
            assert len(keys(*ids))==1
        finally:
            c.set_trace_callback(None)
    reads=[sql for sql in statements if sql.lstrip().upper().startswith('SELECT')]
    assert len(reads)==1
    assert 'JOIN tags' in reads[0]


def test_manual_reindex_can_cancel_and_drains_admitted_batch(tags, monkeypatch):
    import threading
    from app import task_registry as registry_module
    from app.task_registry import TaskRegistry
    s,_=tags
    registry=TaskRegistry()
    monkeypatch.setattr(registry_module,'task_registry',registry)
    entered,release=threading.Event(),threading.Event()
    calls=[]
    def batch(*args):
        calls.append(args);entered.set();assert release.wait(3);return (1,1)
    monkeypatch.setattr(tag_store,'reindex_batch',batch)
    async def run():
        tag_store.queue_reindex(s)
        while not entered.is_set(): await asyncio.sleep(.01)
        task=s._refresh_tasks['__lifespan-tag-reindex__']
        record=registry.snapshot()['items'][0]
        assert record['origin']=='manual'
        assert record['id'] in registry._runtime_tasks
        task.cancel()
        await asyncio.sleep(.02)
        assert not task.done()  # thread still owns the admitted batch
        release.set()
        await asyncio.gather(task,return_exceptions=True)
        assert len(calls)==1
        assert registry.snapshot()['items'][0]['state']=='failed'
        assert registry.snapshot()['items'][0]['message']=='Tag indexing cancelled'
    asyncio.run(run())
