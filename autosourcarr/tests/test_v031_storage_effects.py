import asyncio, threading
import pytest
from app import service as module
from test_v087_recovery import env, source, entry, manga

def test_post_commit_effects_keep_owner_loop_and_drain(env,monkeypatch):
    s,d=env;source(d,'a');eid=entry(s,'a',41001)
    match={'source_id':'a','source_name':'a','source_manga_id':41001}
    chapters=[{'id':41111,'chapterNumber':1,'name':'One','sourceOrder':0}]
    monkeypatch.setattr(s,'schedule_source_color_analysis',lambda *a:None)
    asyncio.run(s._store_fetched_chapters(eid,match,chapters))
    with d.origins() as o:cid=o.execute('SELECT id FROM chapter_candidates WHERE source_chapter_id=41111').fetchone()[0]
    s._remember_page_info(cid,0,{'private_url':'old'})
    admitted,release=threading.Event(),threading.Event(); original=s._reconcile_split_chapters;calls=[]
    def pause(*args,**kwargs):admitted.set();assert release.wait(3);return original(*args,**kwargs)
    monkeypatch.setattr(s,'_reconcile_split_chapters',pause)
    monkeypatch.setattr(s,'schedule_source_color_analysis',lambda *a:calls.append((threading.current_thread().name,asyncio.get_running_loop())))
    async def run():
        loop=asyncio.get_running_loop();task=asyncio.create_task(s._store_fetched_chapters(eid,match,[{**chapters[0],'sourceOrder':1}]))
        try:
            for _ in range(1000):
                if admitted.is_set():break
                await asyncio.sleep(.001)
            assert admitted.is_set()
            task.cancel();await asyncio.sleep(.005);assert not task.done()
            release.set()
            with pytest.raises(asyncio.CancelledError):await task
            assert calls==[(threading.current_thread().name,loop)]
            assert (cid,0) not in s._page_info_cache
        finally:release.set();await asyncio.gather(task,return_exceptions=True)
    asyncio.run(run())

def test_media_label_late_identity_worker_cannot_replace_manual_title(env,monkeypatch):
    s,d=env;source(d,'a');eid=entry(s,'a',42001);peer=entry(s,'a',42002)
    with d.catalog() as c:
        c.execute("UPDATE entries SET media_mode='manga' WHERE id=?",(eid,))
        c.execute("UPDATE entries SET media_mode='webtoon' WHERE id=?",(peer,))
    read,release=threading.Event(),threading.Event();errors=[]
    monkeypatch.setattr(s,'_entry_media_mode',lambda _: 'manga')
    def bases(value):
        if value==peer and threading.current_thread().name=='metadata-snapshot':read.set();assert release.wait(3)
        return {'same-work'}
    monkeypatch.setattr(s,'_media_pair_title_sets',bases)
    def worker():
        try:s._sync_media_title_labels(eid)
        except BaseException as exc:errors.append(exc)
        finally:d.close_current_thread()
    thread=threading.Thread(target=worker,name='metadata-snapshot');thread.start()
    try:
        assert read.wait(2)
        s.set_preferred_title(eid,'My newer manual title')
    finally:release.set();thread.join(3)
    assert not errors and not thread.is_alive()
    row=s.get_entry(eid)
    assert row['preferred_title']=='My newer manual title',row['preferred_title']

def test_color_label_late_identity_worker_cannot_replace_manual_title(env,monkeypatch):
    s,d=env;source(d,'a');eid=entry(s,'a',43001)
    with d.catalog() as c:c.execute("UPDATE entries SET preferred_title='Monster List (Colored)' WHERE id=?",(eid,))
    read,release=threading.Event(),threading.Event();errors=[];original=module.title_color_variant
    def pause(value):
        result=original(value)
        if threading.current_thread().name=='metadata-snapshot':read.set();assert release.wait(3)
        return result
    monkeypatch.setattr(module,'title_color_variant',pause)
    def worker():
        try:s._sync_colored_title_label(eid)
        except BaseException as exc:errors.append(exc)
        finally:d.close_current_thread()
    thread=threading.Thread(target=worker,name='metadata-snapshot');thread.start()
    try:
        assert read.wait(2)
        s.set_preferred_title(eid,'My newer manual title')
    finally:release.set();thread.join(3)
    assert not errors and not thread.is_alive()
    row=s.get_entry(eid)
    assert row['preferred_title']=='My newer manual title',row['preferred_title']

def test_bound_title_consensus_worker_cannot_replace_manual_title(env,monkeypatch):
    s,d=env;source(d,'a');eid=entry(s,'a',44001)
    with d.catalog() as c:c.execute("UPDATE source_matches SET source_title='Current source consensus' WHERE entry_id=?",(eid,))
    read,release=threading.Event(),threading.Event();errors=[];original=s._most_bound_title_from_records
    def pause(*args):
        result=original(*args)
        if threading.current_thread().name=='metadata-snapshot':read.set();assert release.wait(3)
        return result
    monkeypatch.setattr(s,'_most_bound_title_from_records',pause)
    def worker():
        try:s._sync_preferred_title_from_bound_records(eid)
        except BaseException as exc:errors.append(exc)
        finally:d.close_current_thread()
    thread=threading.Thread(target=worker,name='metadata-snapshot');thread.start()
    try:
        assert read.wait(2)
        s.set_preferred_title(eid,'My newer manual title')
    finally:release.set();thread.join(3)
    assert not errors and not thread.is_alive()
    row=s.get_entry(eid)
    assert row['preferred_title']=='My newer manual title',row['preferred_title']
    assert row['manual_preferred_title']==1
