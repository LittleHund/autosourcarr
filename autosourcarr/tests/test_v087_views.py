"""Read-only import history, bounded local read workers and source admission."""
import asyncio
from concurrent.futures import ThreadPoolExecutor
import json
import threading
import time

import anyio
import httpx
import pytest
from app.config import settings
from app.db import now_iso
from app.read_lane import ReadLane
from app.source_errors import source_error_kind
from app.suwayomi import SuwayomiClient, SourceWorkDeferred, source_work_budget, background_source_work
from test_v087_recovery import env, entry, source, manga


def run_rows(s, db, count=137, state="unresolved"):
    s._create_import_run("run", "fixture.tachibk")
    with db.catalog() as c:
        c.executemany("""INSERT INTO import_items(import_id,position,title,state,raw_json,updated_at,suggestion_checked,translation_checked)
                         VALUES('run',?,?,?,?,?,?,1)""",
                      [(n, f"Backup Work {n}", state, json.dumps({"description": "D"*5000}), now_iso(), 1) for n in range(count)])
        c.execute("UPDATE import_runs SET total=?,unresolved=? WHERE id='run'", (count, count if state=="unresolved" else 0))


def test_import_hydration_is_read_only_and_does_not_invent_history(env):
    s,db=env;source(db,"known");eid=entry(s,"known",1);run_rows(s,db,1,"existing")
    with db.catalog() as c:
        c.execute("UPDATE import_items SET entry_id=? WHERE import_id='run'",(eid,))
    statements=[]
    with db.catalog() as c:
        c.set_trace_callback(statements.append)
        try:
            many=s.import_run_items("run")
            one=s.import_run_item("run",many["items"][0]["id"])
        finally:c.set_trace_callback(None)
    assert not [q for q in statements if q.lstrip().upper().startswith(("UPDATE", "INSERT", "DELETE", "REPLACE"))]
    for row in [many["items"][0],one["item"]]:
        assert row["historical_snapshot_missing"] and row["applied_match"] is None
        assert row["matched_entry"]["id"]==eid


def test_stored_history_stays_frozen_after_catalogue_rename(env):
    s,db=env;source(db,"known");eid=entry(s,"known",1);run_rows(s,db,1,"added")
    stored={"id":eid,"preferred_title":"Original historical title","description":"Historical synopsis"}
    with db.catalog() as c:
        c.execute("UPDATE import_items SET entry_id=?,matched_snapshot_json=?",(eid,json.dumps(stored)))
        c.execute("UPDATE entries SET preferred_title='New current title' WHERE id=?",(eid,))
    item=s.import_run_items("run")["items"][0]
    assert item["applied_match"]==stored
    assert item["matched_entry"]["preferred_title"]=="New current title"


def test_import_page_read_does_not_wait_for_an_unrelated_writer(env):
    s,db=env;source(db,"known");eid=entry(s,"known",1);run_rows(s,db,1,"existing")
    with db.catalog() as c:c.execute("UPDATE import_items SET entry_id=?",(eid,))
    entered,release=threading.Event(),threading.Event()
    def writer():
        try:
            with db.catalog() as c:
                c.execute("UPDATE entries SET status=2 WHERE id=?",(eid,));entered.set();release.wait(2)
        finally:db.close_current_thread()
    thread=threading.Thread(target=writer);thread.start();assert entered.wait(1)
    try:
        start=time.monotonic();result=s.import_run_items("run",limit=50)
        assert time.monotonic()-start<.3 and len(result["items"])==1
    finally:release.set();thread.join(2)


def test_import_pagination_counts_full_state_and_clamps_removed_last_page(env, monkeypatch):
    s,db=env;run_rows(s,db)
    sizes=[];original=s._hydrate_import_rows
    def hydrate(rows):sizes.append(len(rows));return original(rows)
    monkeypatch.setattr(s,"_hydrate_import_rows",hydrate)
    page=s.import_run_items("run",limit=50,offset=50)
    assert page["total"]==137 and page["review_counts"]["all"]==137 and page["review_counts"]["no_candidates"]==137
    assert [x["position"] for x in page["items"]]==list(range(50,100)) and sizes==[50]
    with db.catalog() as c:c.execute("DELETE FROM import_items WHERE position>=100")
    page=s.import_run_items("run",limit=50,offset=100)
    assert page["offset"]==50 and len(page["items"])==50 and not page["has_more"]


@pytest.mark.parametrize("review",["has_candidates","no_candidates","has_reference","no_reference","has_english","no_english","searching"])
def test_server_review_filter_counts_and_results_agree(env,review):
    s,db=env;source(db,"known");eid=entry(s,"known",1);run_rows(s,db,10)
    with db.catalog() as c:
        c.execute("UPDATE import_items SET candidate_ids_json=?,reference_entry_id=?,english_title='English Hint' WHERE position%2=0",(json.dumps([eid]),eid))
        c.execute("UPDATE import_items SET suggestion_checked=0 WHERE position>=8")
        c.execute("UPDATE import_items SET suggestion_state='paused' WHERE position=9")
    data=s.import_run_items("run",limit=50,review=review)
    assert data["total"]==data["review_counts"][review]==len(data["items"])
    if review=="searching":assert [x["position"] for x in data["items"]]==[8]


def test_deleted_and_merged_candidates_are_counted_consistently(env):
    s,db=env;source(db,"known");eid=entry(s,"known",1);run_rows(s,db,1)
    with db.catalog() as c:
        c.execute("INSERT INTO entry_aliases(old_entry_id,canonical_entry_id,merged_at) VALUES(987654,?,?)",(eid,now_iso()))
        c.execute("UPDATE import_items SET candidate_ids_json='[987654,999999]',reference_entry_id=987654")
    result=s.import_run_items("run",review="has_candidates")
    assert result["total"]==1 and result["review_counts"]["has_reference"]==1
    assert result["items"][0]["candidate_ids"]==[eid] and result["items"][0]["reference_entry_id"]==eid


def test_invalid_review_filter_has_explicit_error(env):
    s,db=env;run_rows(s,db,1)
    with pytest.raises(ValueError):s.import_run_items("run",review="not-a-filter")


def test_api_read_lane_responds_with_saturated_default_executor(env,monkeypatch):
    from app import main
    s,db=env;run_rows(s,db,1)
    monkeypatch.setattr(main,"db",db);monkeypatch.setattr(main,"service",s)
    async def run():
        loop=asyncio.get_running_loop();pool=ThreadPoolExecutor(max_workers=1);loop.set_default_executor(pool)
        release=threading.Event();started=threading.Event()
        def block():started.set();release.wait(3)
        blocker=loop.run_in_executor(None,block)
        while not started.is_set():await asyncio.sleep(.001)
        try:
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app),base_url="http://test") as client:
                response=await asyncio.wait_for(client.get('/api/admin/imports/run/items?limit=50'),1)
                assert response.status_code==200 and len(response.json()["items"])==1
        finally:release.set();await blocker;await main.read_lane.close()
    asyncio.run(run())


def test_api_read_lane_does_not_require_anyio_request_worker(env,monkeypatch):
    from app import main
    s,db=env;run_rows(s,db,1)
    monkeypatch.setattr(main,"db",db);monkeypatch.setattr(main,"service",s)
    async def run():
        limiter=anyio.to_thread.current_default_thread_limiter();old=limiter.total_tokens;limiter.total_tokens=1
        gate,started=threading.Event(),threading.Event()
        def block():started.set();gate.wait(3)
        blocker=asyncio.create_task(anyio.to_thread.run_sync(block))
        while not started.is_set():await asyncio.sleep(.001)
        try:
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app),base_url="http://test") as client:
                response=await asyncio.wait_for(client.get('/api/admin/imports?limit=50'),1)
                assert response.status_code==200 and response.json()["runs"][0]["id"]=="run"
        finally:gate.set();await blocker;limiter.total_tokens=old;await main.read_lane.close()
    asyncio.run(run())


def test_read_lane_bounds_workers_and_drains_repeated_cancellation():
    async def run():
        lane=ReadLane(1);gate=threading.Event();entered=threading.Event();second_started=threading.Event()
        def first():entered.set();gate.wait(2);return 1
        task=asyncio.create_task(lane.run(first))
        while not entered.is_set():await asyncio.sleep(.001)
        second=asyncio.create_task(lane.run(lambda:second_started.set()))
        task.cancel();await asyncio.sleep(.01);task.cancel();await asyncio.sleep(.01)
        assert not task.done() and not second_started.is_set()
        gate.set()
        with pytest.raises(asyncio.CancelledError):await task
        await second;assert second_started.is_set();await lane.close()
    asyncio.run(run())


def test_source_execution_budget_starts_after_admission():
    async def run():
        client=SuwayomiClient("http://source.invalid",work_limit=1)
        total,_=client._work_semaphores();await total.acquire()
        async def release():await asyncio.sleep(.08);total.release()
        releaser=asyncio.create_task(release())
        async def operation():await asyncio.sleep(.025);return 42
        try:
            async with source_work_budget(.04,admission_timeout=.2):
                assert await client._run_source_work(operation)==42
        finally:await releaser;await client.close()
    asyncio.run(run())


def test_source_admission_timeout_is_deferred_and_releases_slots():
    async def run():
        client=SuwayomiClient("http://source.invalid",work_limit=1)
        total,background=client._work_semaphores();await total.acquire();called=[]
        async def operation():called.append(1);return 42
        try:
            with pytest.raises(SourceWorkDeferred) as result:
                async with background_source_work(),source_work_budget(.03,admission_timeout=.03):
                    await client._run_source_work(operation)
            assert source_error_kind(result.value)=="deferred" and called==[] and background._value==1
            total.release()
            async with source_work_budget(.1):assert await client._run_source_work(operation)==42
        finally:await client.close()
    asyncio.run(run())


def test_execution_timeout_is_not_reported_as_local_deferral():
    async def run():
        client=SuwayomiClient("http://source.invalid",work_limit=1)
        async def slow():await asyncio.sleep(1)
        try:
            with pytest.raises(TimeoutError):
                async with source_work_budget(.01):await client._run_source_work(slow)
            total,_=client._work_semaphores();assert total._value==1
        finally:await client.close()
    asyncio.run(run())
