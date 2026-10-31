"""Off-loop chapter writes retain cancellation, manual ownership and rollback."""
import asyncio
import threading
import pytest
from app import service as module
from test_v087_recovery import env, source, entry, manga


def setup_record(env, monkeypatch):
    service,database=env;source(database,'a');eid=entry(service,'a',31001)
    monkeypatch.setattr(service,'schedule_source_color_analysis',lambda *args:False)
    monkeypatch.setattr(service,'schedule_additional_record_discovery',lambda *args:False)
    record={'source_id':'a','source_name':'a','source_manga_id':31001}
    chapters=[{'id':31111,'chapterNumber':1,'name':'Chapter 1','sourceOrder':0}]
    return service,database,eid,record,chapters


@pytest.mark.parametrize('cancel',[False,True])
def test_owned_storage_yields_and_drains_cancellation(env, monkeypatch, cancel):
    s,d,eid,record,chapters=setup_record(env,monkeypatch)
    admitted,release,completed=threading.Event(),threading.Event(),threading.Event()
    original=s._ingest_chapters
    def hold(*args,**kwargs):
        admitted.set();assert release.wait(3)
        try:return original(*args,**kwargs)
        finally:completed.set()
    monkeypatch.setattr(s,'_ingest_chapters',hold)
    async def run():
        task=asyncio.create_task(s._store_fetched_chapters(eid,record,chapters))
        try:
            for _ in range(1000):
                if admitted.is_set():break
                await asyncio.sleep(.001)
            assert admitted.is_set() and not task.done()
            if cancel:
                task.cancel();await asyncio.sleep(.01);assert not task.done()
            release.set()
            if cancel:
                with pytest.raises(asyncio.CancelledError):await task
            else:assert await task
            assert completed.is_set()
        finally:release.set();await asyncio.gather(task,return_exceptions=True)
    asyncio.run(run())
    assert s.source_record_chapter_stats([eid])[(eid,'a',31001)]['chapter_count']==1


def test_stale_clear_and_deleted_entry_do_not_recreate_chapters(env, monkeypatch):
    s,d,eid,record,chapters=setup_record(env,monkeypatch)
    s.clear_match(eid,'a')
    assert asyncio.run(s._store_fetched_chapters(eid,record,chapters)) is False
    with d.origins() as c:assert c.execute('SELECT COUNT(*) FROM source_record_scans').fetchone()[0]==0
    s._upsert_match(eid,'a',manga(31001),1.0,'manual',True)
    s.delete_entry(eid)
    assert asyncio.run(s._store_fetched_chapters(eid,record,chapters)) is False


def test_reconcile_failure_rolls_back_ingest_and_post_commit_effects(env, monkeypatch):
    s,d,eid,record,chapters=setup_record(env,monkeypatch)
    scheduled=[];monkeypatch.setattr(s,'schedule_source_color_analysis',lambda *args:scheduled.append(args))
    def fail(*args,**kwargs):raise RuntimeError('reconciliation failed')
    monkeypatch.setattr(s,'_reconcile_split_chapters',fail)
    with pytest.raises(RuntimeError,match='reconciliation failed'):asyncio.run(s._store_fetched_chapters(eid,record,chapters))
    with d.origins() as c:
        assert c.execute('SELECT COUNT(*) FROM chapter_candidates').fetchone()[0]==0
        assert c.execute('SELECT COUNT(*) FROM source_record_scans').fetchone()[0]==0
    assert scheduled==[]


@pytest.mark.parametrize('action',['clear','title','manual_lock'])
def test_refresh_preserves_decisions_made_during_upstream_fetch(env, monkeypatch, action):
    s,d,eid,record,chapters=setup_record(env,monkeypatch)
    async def fetch(*args):
        if action=='clear':s.clear_match(eid,'a')
        elif action=='title':s.set_preferred_title(eid,'My saved manual title')
        else:s._upsert_match(eid,'a',manga(31001),1.0,'manual',True)
        return {'manga':manga(31001,'Updated source title'),'chapters':chapters}
    monkeypatch.setattr(module.source_host,'fetch_manga_and_chapters',fetch)
    result=asyncio.run(s.refresh_entry(eid))
    if action=='clear':assert result['matches']==[] and s.list_chapters(eid)==[]
    elif action=='title':assert result['preferred_title']=='My saved manual title' and result['manual_preferred_title']
    else:assert result['matches'][0]['locked'] and result['matches'][0]['match_type']=='manual'


def test_source_failure_after_detach_does_not_recreate_scan(env, monkeypatch):
    s,d,eid,record,chapters=setup_record(env,monkeypatch)
    async def fetch(*args):s.clear_match(eid,'a');raise RuntimeError('source unavailable')
    monkeypatch.setattr(module.source_host,'fetch_manga_and_chapters',fetch)
    with pytest.raises(RuntimeError,match='source unavailable'):asyncio.run(s.refresh_source_record_chapters(eid,'a',31001))
    with d.origins() as c:assert c.execute('SELECT COUNT(*) FROM source_record_scans').fetchone()[0]==0
    assert module.task_registry.snapshot()['running']==0


def test_identity_metadata_worker_cannot_overwrite_concurrent_manual_title(env, monkeypatch):
    s,d,eid,record,chapters=setup_record(env,monkeypatch)
    read,release=threading.Event(),threading.Event();errors=[]
    original=s._most_bound_title_from_records
    def pause(*args):
        value=original(*args)
        if threading.current_thread().name=='metadata-snapshot':read.set();assert release.wait(3)
        return value
    monkeypatch.setattr(s,'_most_bound_title_from_records',pause)
    def worker():
        try:s._choose_metadata(eid)
        except BaseException as exc:errors.append(exc)
        finally:d.close_current_thread()
    thread=threading.Thread(target=worker,name='metadata-snapshot');thread.start()
    try:
        assert read.wait(2)
        s.set_preferred_title(eid,'Saved while metadata was running')
    finally:release.set();thread.join(3)
    assert not thread.is_alive() and not errors
    result=s.get_entry(eid)
    assert result['preferred_title']=='Saved while metadata was running' and result['manual_preferred_title']
