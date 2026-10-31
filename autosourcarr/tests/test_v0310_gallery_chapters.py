from __future__ import annotations

import json
import uuid

import pytest

from app.db import db
from app.gallery_metadata import enrich_gallery_payload
from app.service import AutoSourcarrService


def source(sid, name):
    with db.catalog() as c:
        c.execute('''INSERT INTO source_config(source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_language,enabled_search,enabled_chapters,priority,last_seen,available)
        VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)''', (sid,name,name,'en','https://example.invalid','',1,1,1,1,50,'now',1))


@pytest.fixture
def catalog():
    token=uuid.uuid4().hex[:12]
    a,b,c='gallery-'+token, 'mirror-'+token, 'manga-'+token
    source(a,'IMHentai');source(b,'E-Hentai');source(c,'Example Manga')
    return AutoSourcarrService(),a,b,c,token


def payload(mid,title,sid,**kw):
    return dict(id=mid,sourceId=sid,title=title,genre=['artist:Sample Artist','fantasy','adventure'],initialized=True,url=f'/gallery/{mid}',**kw)


def attach(s,sid,mid,title,**kw):
    return s.ingest_manga(sid,payload(mid,title,sid,**kw))[0]


def ingest_ch(s,eid,sid,mid,cid,num=0,name='Chapter 0',chapters=None):
    s._ingest_chapters(eid,{'source_id':sid,'source_manga_id':mid,'source_name':'IMHentai'},s._match_id(eid,sid),chapters or [{'id':cid,'name':name,'chapterNumber':num,'sourceOrder':0,'url':f'/chapter/{cid}','pageCount':10}], effects={'invalidated':set()})


def rows(s,eid):
    return s.list_chapters(eid)


@pytest.mark.parametrize('title',['[Artist] Flowerxl','ARTIST Flowerxl','Artist ❤️❤️ Flowerxl','Artist - Flowerxl'])
def test_artist_collection_forms(title):
    m=enrich_gallery_payload({'title':title}, {'name':'IMHentai'})['_gallery_metadata']
    assert m['kind']=='collection'
    assert m['artist']=='Flowerxl'
    assert m['work_title']=='Flowerxl'


@pytest.mark.parametrize('marker',['Chapter 1','ch. 1','Chap. 1','Part 1','pt. 1','Episode 1','ep. 1','(Chapter 1)','[Part 1]'])
def test_explicit_gallery_part_is_work_metadata(marker):
    m=enrich_gallery_payload({'title':f'[Sample Artist] Quiet Garden {marker} [English]'}, {'name':'E-Hentai'})['_gallery_metadata']
    assert m['work_title']=='Quiet Garden'
    assert m['chapter_number']==1
    assert marker in m['raw_title']


def test_same_artist_collections_group_and_keep_every_gallery_chapter(catalog):
    s,a,_,_,token=catalog
    titles=[f'[Artist] Example {token}',f'ARTIST Example {token}',f'Artist ❤️❤️ Example {token}',f'Artist - Example {token}']
    ids=[attach(s,a,i+100,title) for i,title in enumerate(titles)]
    assert len(set(ids))==1
    eid=ids[0]
    for i in range(4): ingest_ch(s,eid,a,100+i,1000+i)
    out=rows(s,eid)
    assert len(out)==4
    assert {r['chapter_number'] for r in out}=={1,2,3,4}
    assert all(r['candidate_count']==1 for r in out)
    before={r['serving_source_entry_title']:r['id'] for r in out}
    # Reverse refresh order and restart do not renumber stable gallery slots.
    s2=AutoSourcarrService()
    for i in reversed(range(4)): ingest_ch(s2,eid,a,100+i,1000+i)
    assert {r['serving_source_entry_title']:r['id'] for r in rows(s2,eid)}==before
    with db.origins() as o:
        assert {r[0] for r in o.execute('SELECT source_manga_id FROM chapter_candidates WHERE virtual_chapter_id IN (SELECT id FROM virtual_chapters WHERE entry_id=?)',(eid,))}=={100,101,102,103}


def test_different_creators_never_group_same_work_heading(catalog):
    s,a,_,_,token=catalog
    one=attach(s,a,201,f'Artist - First {token}')
    two=attach(s,a,202,f'Artist - Second {token}')
    assert one!=two


def test_numbered_gallery_parts_share_work_and_native_manga_chapters(catalog):
    s,a,b,c,token=catalog
    base='Quiet Garden '+token
    eid=attach(s,a,301,base+' Chapter 1')
    assert attach(s,a,302,base+' ch. 2')==eid
    # Same numbered part on another gallery source is an alternative, not a new
    # chapter. A ordinary source's multi-chapter work also uses the same n: keys.
    assert attach(s,b,303,base+' Part 1')==eid
    assert attach(s,c,304,base)==eid
    ingest_ch(s,eid,a,301,3010)
    ingest_ch(s,eid,a,302,3020)
    ingest_ch(s,eid,b,303,3030)
    ingest_ch(s,eid,c,304,0,chapters=[{'id':3041,'name':'Chapter 1','chapterNumber':1}, {'id':3042,'name':'Chapter 2','chapterNumber':2}])
    out=rows(s,eid)
    assert {r['chapter_number']:r['candidate_count'] for r in out}=={1:3,2:2}


def test_native_multichapter_gallery_source_keeps_native_sequence(catalog):
    s,a,_,_,token=catalog
    eid=attach(s,a,401,'Quiet Garden '+token)
    ingest_ch(s,eid,a,401,0,chapters=[{'id':4011,'name':'Chapter 4','chapterNumber':4}, {'id':4012,'name':'Chapter 5','chapterNumber':5}])
    assert {r['chapter_number'] for r in rows(s,eid)}=={4,5}


def test_animated_and_static_entries_are_separate_even_with_same_tags(catalog):
    s,a,_,_,token=catalog
    name='Quiet Garden '+token
    static=attach(s,a,501,name)
    animated=attach(s,a,502,name+' (animated)')
    assert animated!=static
    assert s.get_entry(animated)['preferred_title'].endswith('(animated)')
    assert not s._auto_merge_evidence(static,animated)['eligible']
    # Structured tags have the same protection even without a title marker.
    p=payload(503,name,a);p['genre'].append('other:animated')
    same_animation=s.ingest_manga(a,p)[0]
    assert same_animation==animated


def test_manual_separation_wins_over_new_gallery_family(catalog):
    s,a,_,_,token=catalog
    base='Quiet Garden '+token
    eid=attach(s,a,601,base+' Chapter 1')
    # Explicit not-duplicate decision must prevent the retained entries being
    # merged by the upgrade/new family scoring.
    other=s._create_entry(payload(602,base+' Part 2',a),a)
    s._upsert_match(other,a,payload(602,base+' Part 2',a),1)
    s.review_duplicate(eid,other,'not_duplicate')
    assert not s._auto_merge_evidence(eid,other)['eligible']


def test_legacy_generic_gallery_collision_splits_preserving_served_id_and_pages(catalog,monkeypatch):
    s,a,_,_,token=catalog
    title='Artist - Sample '+token
    eid=attach(s,a,701,title)
    assert attach(s,a,702,title)==eid
    native=s._gallery_chapter_metadata
    monkeypatch.setattr(s,'_gallery_chapter_metadata',lambda *args:{})
    ingest_ch(s,eid,a,701,7010)
    ingest_ch(s,eid,a,702,7020)
    with db.origins() as o:
        old=o.execute('SELECT id FROM virtual_chapters WHERE entry_id=?',(eid,)).fetchone()[0]
        candidate=o.execute('SELECT id FROM chapter_candidates WHERE source_id=? AND source_manga_id=?',(a,702)).fetchone()[0]
        o.execute('INSERT INTO candidate_pages(candidate_id,page_index,private_url,updated_at) VALUES(?,?,?,?)',(candidate,0,'https://example.invalid/page','now'))
        o.execute('INSERT INTO served_origin(virtual_chapter_id,candidate_id,selected_at,reason) VALUES(?,?,?,?)',(old,candidate,'now','served'))
        o.execute('INSERT INTO origin_history(virtual_chapter_id,candidate_id,source_id,source_name,source_manga_id,source_chapter_id,served_at,reason) VALUES(?,?,?,?,?,?,?,?)',(old,candidate,a,'IMHentai',702,7020,'now','served'))
    monkeypatch.setattr(s,'_gallery_chapter_metadata',native)
    result=s._reconcile_gallery_entry(eid)
    assert result['records']==2
    assert len(rows(s,eid))==2
    with db.origins() as o:
        assert o.execute('SELECT virtual_chapter_id FROM chapter_candidates WHERE id=?',(candidate,)).fetchone()[0]==old
        assert o.execute('SELECT private_url FROM candidate_pages WHERE candidate_id=?',(candidate,)).fetchone()[0]=='https://example.invalid/page'
        assert o.execute('SELECT virtual_chapter_id FROM origin_history WHERE candidate_id=?',(candidate,)).fetchone()[0]==old
    stable=[(r['id'],r['chapter_number']) for r in rows(s,eid)]
    s._reconcile_gallery_entry(eid)
    assert [(r['id'],r['chapter_number']) for r in rows(s,eid)]==stable


def test_upgrade_splits_automatic_animated_record_and_retains_cached_chapters(catalog):
    s,a,_,_,token=catalog
    title='Quiet Garden '+token
    eid=attach(s,a,801,title)
    s._upsert_source_variant(eid,a,payload(802,title+' (animated)',a),1,'auto')
    ingest_ch(s,eid,a,801,8010)
    ingest_ch(s,eid,a,802,8020)
    result=s._reconcile_gallery_entry(eid)
    assert result['splits']==1
    owner=s._find_existing_by_source(a,802)
    assert owner!=eid
    assert len(rows(s,eid))==len(rows(s,owner))==1
    assert s.get_entry(owner)['preferred_title'].endswith('(animated)')


def test_upgrade_retains_explicit_manual_animation_grouping(catalog):
    s,a,_,_,token=catalog
    title='Quiet Garden '+token
    eid=attach(s,a,901,title)
    s._upsert_source_variant(eid,a,payload(902,title+' (animated)',a),1,'manual',True)
    assert s._reconcile_gallery_entry(eid)['splits']==0
    assert s._find_existing_by_source(a,902)==eid


def test_animation_discovered_by_hydration_splits_without_restart(catalog):
    s,a,_,_,token=catalog
    title='Quiet Garden '+token
    eid=attach(s,a,1001,title)
    assert attach(s,a,1002,title)==eid
    animated=payload(1002,title,a);animated['genre'].append('other:animated')
    s._apply_source_detail_metadata(eid,a,1002,animated,True)
    assert s._find_existing_by_source(a,1002)!=eid


def test_gallery_upgrade_does_not_claim_fresh_network_scan_or_clear_error(catalog):
    s,a,_,_,token=catalog
    eid=attach(s,a,1101,'Artist - Sample '+token)
    ingest_ch(s,eid,a,1101,11010)
    with db.origins() as o:
        o.execute('UPDATE source_record_scans SET checked_at=?,error=? WHERE entry_id=?',('2020-01-01','Source needs verification',eid))
    s._reconcile_gallery_entry(eid)
    with db.origins() as o:
        scan=o.execute('SELECT checked_at,error FROM source_record_scans WHERE entry_id=?',(eid,)).fetchone()
    assert tuple(scan)==('2020-01-01','Source needs verification')


def test_each_gallery_reader_page_uses_its_own_original_source_chapter(catalog,monkeypatch):
    import asyncio
    from app.service import source_host
    s,a,_,_,token=catalog
    eid=attach(s,a,1201,'Artist - Sample '+token)
    assert attach(s,a,1202,'ARTIST Sample '+token)==eid
    ingest_ch(s,eid,a,1201,12010);ingest_ch(s,eid,a,1202,12020)
    called=[]
    async def fetch(cid):
        called.append(cid)
        return [f'https://example.invalid/{cid}/page-1']
    async def index(mid,cid):
        assert (mid,cid) in {(1201,12010),(1202,12020)}
        return 0
    monkeypatch.setattr(source_host,'fetch_chapter_pages',fetch)
    monkeypatch.setattr(source_host,'chapter_index_for_id',index)
    async def run():
        for chapter in rows(s,eid):
            response=await s.resolve_pages(chapter['id'])
            candidate=response['candidate']
            info=s.page_fetch_info(candidate['id'],0)
            assert info['private_url']==f'https://example.invalid/{candidate["source_chapter_id"]}/page-1'
    asyncio.run(run())
    assert set(called)=={12010,12020}


def test_relayout_preserves_rejected_gallery_chapter(catalog):
    s,a,_,_,token=catalog
    eid=attach(s,a,1301,'Artist - Sample '+token)
    ingest_ch(s,eid,a,1301,13010)
    chapter=rows(s,eid)[0]
    s.reject_chapter_candidate(chapter['serving_candidate_id'])
    s._reconcile_gallery_entry(eid)
    assert not rows(s,eid)
    with db.origins() as o:
        assert o.execute('SELECT 1 FROM chapter_candidate_rejections WHERE source_id=? AND source_chapter_id=?',(a,13010)).fetchone()


def test_part_before_franchise_suffix_is_detected():
    m=enrich_gallery_payload({'title':'[Sample Artist] Quiet Garden Ch. 2 (Sky Kingdom) [English]'}, {'name':'E-Hentai'})['_gallery_metadata']
    assert m['work_title']=='Quiet Garden' and m['chapter_number']==2
    assert m['parody']=='Sky Kingdom'


@pytest.mark.parametrize('suffix',['Volume 1','Vol. 1','Season 2','Chapter 1-2','2000'])
def test_nonchapter_numbers_and_ranges_are_not_stripped(suffix):
    m=enrich_gallery_payload({'title':'[Sample Artist] Quiet Garden '+suffix}, {'name':'E-Hentai'})['_gallery_metadata']
    assert m['work_title'].endswith(suffix)
    assert m['chapter_number'] is None


def test_manual_merge_of_cached_gallery_entries_preserves_ids_and_unique_slots(catalog):
    s,a,_,_,token=catalog
    one=attach(s,a,1401,'Artist - First '+token)
    two=attach(s,a,1402,'Artist - Second '+token)
    ingest_ch(s,one,a,1401,14010);ingest_ch(s,two,a,1402,14020)
    before={r['id'] for eid in (one,two) for r in rows(s,eid)}
    eid=s.merge_entries(one,two,'manual')
    after=rows(s,eid)
    assert {r['id'] for r in after}==before
    assert {r['chapter_number'] for r in after}=={1,2}


def test_volume_editions_do_not_enter_numbered_gallery_family(catalog):
    s,a,_,_,token=catalog
    one=attach(s,a,1501,'Quiet Garden '+token+' Volume 1')
    two=attach(s,a,1502,'Quiet Garden '+token+' Volume 2')
    assert one!=two


def test_reparsed_animation_metadata_is_stable():
    source={'name':'IMHentai'}
    first=enrich_gallery_payload({'title':'Artist - Sample [Animated]','genre':['other:animated']},source)
    assert enrich_gallery_payload(first,source)==first


def test_native_gallery_chapters_with_special_preserve_numbers_and_titles(catalog):
    s,a,_,_,token=catalog
    eid=attach(s,a,1601,'Quiet Garden '+token)
    native=[{'id':16010,'name':'Chapter 5','chapterNumber':5}, {'id':16020,'name':'Chapter 6','chapterNumber':6}, {'id':16030,'name':'Special','chapterNumber':-1}]
    ingest_ch(s,eid,a,1601,0,chapters=native)
    out=rows(s,eid)
    assert {r['chapter_number'] for r in out}=={5,6,-1}
    assert any(r['display_name']=='Special' for r in out)
    before={r['id'] for r in out}
    s._reconcile_gallery_entry(eid)
    assert {r['chapter_number'] for r in rows(s,eid)}=={5,6,-1}
    assert {r['id'] for r in rows(s,eid)}==before


def test_upgrade_separates_animated_gallery_from_ordinary_static_work(catalog):
    s,a,_,c,token=catalog
    title='Quiet Garden '+token
    eid=attach(s,c,1701,title)
    s._upsert_match(eid,a,payload(1702,title+' (animated)',a),1,'auto')
    ingest_ch(s,eid,c,1701,17010,num=1)
    ingest_ch(s,eid,a,1702,17020)
    result=s._reconcile_gallery_entry(eid)
    assert result['splits']==1
    owner=s._find_existing_by_source(a,1702)
    assert owner!=eid
    assert s._find_existing_by_source(c,1701)==eid
    assert len(rows(s,eid))==len(rows(s,owner))==1


@pytest.mark.parametrize('label',['Artist Example','[Artist] Example'])
def test_artist_collection_part_number_is_not_part_of_creator(label,catalog):
    s,a,_,_,token=catalog
    title=label+' '+token
    one=attach(s,a,1801,title+' Chapter 1')
    two=attach(s,a,1802,title+' Part 2 [English]')
    assert one==two
    m=s._gallery_chapter_metadata(a,1802)
    assert m['artist']=='Sample Artist' # explicit source field wins
    assert not any('Chapter' in t['name'] or 'Part' in t['name'] for t in m['tags'] if t['namespace']=='artist')
    assert m['chapter_number']==2


def test_single_known_native_chapter_number_is_not_erased(catalog):
    s,a,_,_,token=catalog
    eid=attach(s,a,1901,'Quiet Garden '+token)
    ingest_ch(s,eid,a,1901,19010,num=5,name='Chapter 5')
    out=rows(s,eid)
    assert out[0]['chapter_number']==5
    assert out[0]['display_name']=='Chapter 5'


def test_migration_does_not_overwrite_newer_authoritative_hydration(catalog,monkeypatch):
    from app import tag_store
    s,a,_,_,token=catalog
    title='Quiet Garden '+token
    eid=attach(s,a,2001,title)
    original=s._gallery_record_payload
    injected=False
    def race(row):
        nonlocal injected
        if row['source_manga_id']==2001 and not injected:
            injected=True
            fresh=payload(2001,title,a)
            fresh['genre'].append('fresh specific tag')
            fresh['description']='Fresh authoritative details'
            s._apply_source_detail_metadata(eid,a,2001,fresh,False)
        return original(row)
    monkeypatch.setattr(s,'_gallery_record_payload',race)
    assert s._reconcile_gallery_entry(eid)['deferred']==1
    with db.catalog() as c:
        row=c.execute('SELECT metadata_json FROM source_matches WHERE entry_id=?',(eid,)).fetchone()
    assert 'fresh specific tag' in json.loads(row['metadata_json'])['genre']
    assert any(t['name']=='fresh specific tag' for t in tag_store.entry_metadata(eid)['tags'])
    assert s._reconcile_gallery_entry(eid)['deferred']==0


def test_migration_obeys_new_manual_decision_before_animation_split(catalog,monkeypatch):
    s,a,_,_,token=catalog
    title='Quiet Garden '+token
    eid=attach(s,a,2101,title)
    s._upsert_source_variant(eid,a,payload(2102,title+' (animated)',a),1,'auto')
    original=s._gallery_record_payload
    injected=False
    def race(row):
        nonlocal injected
        if row['source_manga_id']==2102 and not injected:
            injected=True
            s._upsert_source_variant(eid,a,payload(2102,title+' (animated)',a),1,'manual',True)
        return original(row)
    monkeypatch.setattr(s,'_gallery_record_payload',race)
    assert s._reconcile_gallery_entry(eid)['splits']==0
    assert s._find_existing_by_source(a,2102)==eid
    assert s._reconcile_gallery_entry(eid)['splits']==0
    assert s._find_existing_by_source(a,2102)==eid


def test_known_gallery_refresh_cannot_downgrade_manual_variant(catalog):
    s,a,_,_,token=catalog
    title='Quiet Garden '+token
    eid=attach(s,a,2201,title)
    animated=payload(2202,title+' (animated)',a)
    s._upsert_source_variant(eid,a,animated,1,'manual',True)
    assert s.ingest_manga(a,animated)[0]==eid
    with db.catalog() as c:
        row=c.execute('SELECT match_type,locked FROM source_variants WHERE source_id=? AND source_manga_id=?',(a,2202)).fetchone()
    assert tuple(row)==('manual',1)
    assert s._reconcile_gallery_entry(eid)['splits']==0


def test_durable_manual_binding_protects_legacy_downgraded_flags(catalog):
    s,a,_,_,token=catalog
    title='Quiet Garden '+token
    eid=attach(s,a,2301,title)
    animated=payload(2302,title+' (animated)',a)
    s._upsert_source_variant(eid,a,animated,1,'manual',True)
    s._persist_manual_binding(eid,a,animated)
    with db.catalog() as c:
        c.execute("UPDATE source_variants SET locked=0,match_type='known' WHERE source_id=? AND source_manga_id=?",(a,2302))
    assert s._reconcile_gallery_entry(eid)['splits']==0
    assert s._find_existing_by_source(a,2302)==eid


def test_served_gallery_split_keeps_native_manga_chapter_as_its_own_chapter(catalog,monkeypatch):
    s,a,_,c,token=catalog
    title='Quiet Garden '+token
    eid=attach(s,a,2401,title)
    s._upsert_match(eid,c,payload(2402,title,c),1,'auto')
    real=s._gallery_chapter_metadata
    monkeypatch.setattr(s,'_gallery_chapter_metadata',lambda *args:{})
    ingest_ch(s,eid,a,2401,24010,num=1,name='Chapter 1')
    ingest_ch(s,eid,c,2402,24020,num=1,name='Chapter 1')
    with db.origins() as o:
        chosen=o.execute('SELECT * FROM chapter_candidates WHERE source_id=? AND source_chapter_id=?',(a,24010)).fetchone()
        old=int(chosen['virtual_chapter_id'])
        o.execute('INSERT INTO served_origin(virtual_chapter_id,candidate_id,selected_at,reason) VALUES(?,?,?,?)',(old,chosen['id'],'now','served'))
    monkeypatch.setattr(s,'_gallery_chapter_metadata',real)
    s._reconcile_gallery_entry(eid)
    out=rows(s,eid)
    assert len(out)==2
    with db.origins() as o:
        native=o.execute('SELECT vc.chapter_key,vc.chapter_number FROM virtual_chapters vc JOIN chapter_candidates cc ON cc.virtual_chapter_id=vc.id WHERE cc.source_id=? AND cc.source_chapter_id=?',(c,24020)).fetchone()
        gallery=o.execute('SELECT virtual_chapter_id FROM chapter_candidates WHERE source_id=? AND source_chapter_id=?',(a,24010)).fetchone()
    assert tuple(native)==('n:1',1)
    assert gallery[0]==old
    assert all(ch['candidate_count']==1 for ch in out)


def test_manual_gallery_write_wait_does_not_block_ui_event_loop(catalog,monkeypatch):
    import asyncio
    import threading
    import time
    s,a,_,_,token=catalog
    title='Quiet Garden '+token
    eid=attach(s,a,2501,title)
    held=threading.Event();release=threading.Event()
    def writer():
        with s._catalog_maintenance_lock:
            held.set()
            release.wait(1)
    worker=threading.Thread(target=writer)
    worker.start();assert held.wait(1)
    monkeypatch.setattr(s,'schedule_manual_match_hydration',lambda *args:False)
    async def run():
        started=time.monotonic()
        pending=asyncio.create_task(s.manual_match(eid,a,2502,mode='add',candidate=payload(2502,title,a),defer_hydration=True))
        try:
            await asyncio.sleep(.05)
            assert time.monotonic()-started<.5
            assert not pending.done()
        finally:
            release.set()
        await pending
    try:
        asyncio.run(run())
    finally:
        release.set();worker.join(2)


@pytest.mark.parametrize('mode',['add','replace'])
def test_cancelled_manual_commit_keeps_complete_durable_decision(catalog,monkeypatch,mode):
    import asyncio
    import threading
    s,a,_,_,token=catalog
    title='Quiet Garden '+token
    eid=attach(s,a,2601,title)
    s._persist_manual_binding(eid,a,payload(2601,title,a))
    held=threading.Event();release=threading.Event();admitted=threading.Event()
    def writer():
        with s._catalog_maintenance_lock:
            held.set();release.wait(2)
    original=s._commit_manual_match
    def observe(*args):
        admitted.set()
        return original(*args)
    monkeypatch.setattr(s,'_commit_manual_match',observe)
    monkeypatch.setattr(s,'schedule_manual_match_hydration',lambda *args:False)
    worker=threading.Thread(target=writer);worker.start();assert held.wait(1)
    async def run():
        task=asyncio.create_task(s.manual_match(eid,a,2602,mode=mode,candidate=payload(2602,title,a),defer_hydration=True))
        while not admitted.is_set():await asyncio.sleep(.001)
        task.cancel();await asyncio.sleep(.01);release.set()
        with pytest.raises(asyncio.CancelledError):await task
    try:asyncio.run(run())
    finally:release.set();worker.join(2)
    table='source_variants' if mode=='add' else 'source_matches'
    with db.catalog() as c:
        row=c.execute(f'SELECT match_type,locked FROM {table} WHERE source_id=? AND source_manga_id=? AND active=1',(a,2602)).fetchone()
        durable=c.execute('SELECT entry_id_hint FROM manual_match_bindings WHERE source_id=? AND source_manga_id=?',(a,2602)).fetchone()
    assert tuple(row)==('manual',1)
    assert durable['entry_id_hint']==eid


def test_failed_manual_fetch_preserves_separate_decision(catalog,monkeypatch):
    import asyncio
    from app.service import source_host
    s,a,_,_,token=catalog
    eid=attach(s,a,2701,'Quiet Garden '+token)
    s._remember_manual_separate_record(eid,a,2702)
    async def fail(*args,**kwargs):raise RuntimeError('Source unavailable')
    monkeypatch.setattr(source_host,'fetch_manga_and_chapters',fail)
    with pytest.raises(RuntimeError,match='Source unavailable'):
        asyncio.run(s.manual_match(eid,a,2702))
    assert s._is_manual_separate_record(eid,a,2702)


def test_stale_hydration_cannot_reclaim_moved_source_record(catalog):
    s,a,_,c,token=catalog
    manga=payload(2801,'Quiet Garden '+token,a)
    old=attach(s,a,2801,manga['title'])
    new=attach(s,c,2802,'Another Work '+token)
    s._upsert_match(new,a,manga,1,'manual',True)
    assert not s._apply_source_detail_if_current(old,a,2801,manga)
    assert s._find_existing_by_source(a,2801)==new
    with db.catalog() as conn:
        assert conn.execute('SELECT locked FROM source_matches WHERE entry_id=? AND source_id=?',(new,a)).fetchone()[0]==1


def test_automatic_attach_checks_new_manual_separation_inside_writer(catalog):
    s,a,_,_,token=catalog
    eid=attach(s,a,2901,'Quiet Garden '+token)
    p=payload(2902,'Quiet Garden '+token,a)
    s._remember_manual_separate_record(eid,a,2902)
    assert not s._attach_automatic_record_if_current(eid,a,p,1,'auto-additional',variant_only=True)
    assert s._find_existing_by_source(a,2902) is None


def test_existing_gallery_and_native_work_merge_via_actual_catalog_repair(catalog):
    s,a,_,c,token=catalog
    base='Quiet Garden Chronicle '+token
    gallery=s._create_entry(payload(3001,base+' Part 1',a),a)
    s._upsert_match(gallery,a,payload(3001,base+' Part 1',a),1,'auto')
    native=s._create_entry(payload(3002,base,c),c)
    s._upsert_match(native,c,payload(3002,base,c),1,'auto')
    ingest_ch(s,gallery,a,3001,30010)
    ingest_ch(s,native,c,3002,0,chapters=[{'id':30020,'name':'Chapter 1','chapterNumber':1},{'id':30021,'name':'Chapter 2','chapterNumber':2}])
    assert s._auto_merge_evidence(gallery,native)['eligible']
    s.repair_catalog_duplicates()
    eid=s.canonical_entry_id(gallery)
    assert eid==s.canonical_entry_id(native)
    assert {x['chapter_number']:x['candidate_count'] for x in rows(s,eid)}=={1:2,2:1}
