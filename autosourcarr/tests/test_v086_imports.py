"""Reproduce import queue cancellation and synchronous work on the HTTP loop."""
import asyncio
import json
import threading
import time
from types import SimpleNamespace

import pytest

from app import service as mod
from app.async_utils import gather_owned
from app.config import settings
from app.db import Database
from app.error_registry import ErrorRegistry
from app.service import AutoSourcarrService
from app.task_registry import TaskRegistry


@pytest.fixture
def env(monkeypatch, tmp_path):
    monkeypatch.setattr(settings,"data_dir",tmp_path)
    monkeypatch.setattr(settings,"database_dir","")
    monkeypatch.setattr(settings,"exact_thumbnail_matching",False)
    monkeypatch.setattr(settings,"automatic_duplicate_matching",False)
    monkeypatch.setattr(settings,"metadata_identity_enabled",False)
    monkeypatch.setattr(settings,"metadata_identity_on_ingest",False)
    database = Database()
    monkeypatch.setattr(mod,"db",database)
    registry=TaskRegistry(); monkeypatch.setattr(mod,"task_registry",registry)
    monkeypatch.setattr(mod,"error_registry",ErrorRegistry())
    monkeypatch.setattr(mod,"translator",SimpleNamespace(enabled=False))
    s=AutoSourcarrService()
    for name in ("schedule_source_metadata_hydration","schedule_entry_identity","schedule_duplicate_snapshot_refresh"):
        monkeypatch.setattr(s,name,lambda *a,**kw:False)
    yield s,database,registry


def store(s,run="run-a",number=1):
    s._create_import_run(run,run+".tachibk")
    return s._store_import_item(run,number,{"title":"A long unique example for recovery","source":0},state="unresolved",reason="",
                               entry_id=None,translation_checked=True,suggestion_checked=False,generate_candidates=False)


async def eventually(predicate):
    async def wait():
        while not predicate():
            await asyncio.sleep(.002)
    await asyncio.wait_for(wait(),2)


def test_initial_strict_selection_runs_in_a_worker_thread(env,monkeypatch):
    s,_,_=env
    async def run():
        loop_thread=threading.get_ident()
        def select(*a,**kw):
            assert threading.get_ident()!=loop_thread
            time.sleep(.06)
            return 42,"strict evidence"
        monkeypatch.setattr(s,"_import_auto_resolve_candidate",select)
        ticks=[]
        task=asyncio.create_task(s._import_auto_resolve_candidate_async({"title":"Example"}))
        while not task.done():
            ticks.append(time.monotonic());await asyncio.sleep(.005)
        assert await task==(42,"strict evidence") and len(ticks)>=3
    asyncio.run(run())


def test_fallback_shortlist_and_thumbnail_evidence_are_also_off_loop(env,monkeypatch):
    s,_,_=env
    monkeypatch.setattr(settings,"exact_thumbnail_matching",True)
    async def run():
        loop_thread=threading.get_ident();seen=[]
        def local(name,result):
            def check(*a,**kw):
                assert threading.get_ident()!=loop_thread
                seen.append(name)
                return result
            return check
        monkeypatch.setattr(s,"_import_auto_resolve_candidate",local("strict",(None,"ambiguous")))
        monkeypatch.setattr(s,"_backup_candidate_ranked",local("rank",[(1.,10)]))
        monkeypatch.setattr(s,"_import_candidate_evidence",local("evidence",{"eligible":True}))
        async def image(*a): return [10]
        monkeypatch.setattr(s,"_import_exact_thumbnail_entry_ids",image)
        result=await s._import_auto_resolve_candidate_async({"title":"Example"})
        assert result==(10,"exact decoded thumbnail pixels") and seen==["strict","rank","evidence"]
    asyncio.run(run())


def test_live_lookup_reranks_and_persists_local_candidates_off_loop(env,monkeypatch):
    s,db,_=env; item=store(s)
    async def run():
        loop_thread=threading.get_ident();calls=[]
        def suggestions(*a):
            assert threading.get_ident()!=loop_thread
            calls.append(True)
            return {"suggestions":[{"id":5,"import_score":1}]}
        monkeypatch.setattr(s,"import_item_suggestions",suggestions)
        async def choose(*a,**kw):return 5,"strict local"
        monkeypatch.setattr(s,"_import_auto_resolve_candidate_async",choose)
        result=await s.import_item_live_suggestions("run-a",item,automatic=True)
        assert result["auto_match_entry_id"]==5 and calls==[True]
        row=s._read_import_item("run-a",item)
        assert json.loads(row["candidate_ids_json"])==[5] and row["suggestion_checked"]==1
    asyncio.run(run())


def test_ingestion_stays_serial_but_followup_scheduling_stays_on_loop(env,monkeypatch):
    s,_,_=env
    async def run():
        loop_thread=threading.get_ident();active=0;peak=0;follow=[]
        def ingest(*args):
            nonlocal active,peak
            assert threading.get_ident()!=loop_thread
            active+=1;peak=max(peak,active);time.sleep(.02);active-=1
            return 1,1.,"new-entry"
        def schedule(*a,**kw):
            assert threading.get_ident()==loop_thread
            follow.append(a)
        monkeypatch.setattr(s,"ingest_manga",ingest)
        monkeypatch.setattr(s,"schedule_source_metadata_hydration",schedule)
        await asyncio.gather(*(s._ingest_import_manga("1",{"id":i+1}) for i in range(4)))
        assert peak==1 and len(follow)==4
    asyncio.run(run())


def test_cancellation_before_first_coroutine_step_decrements_pending(env):
    s,_,r=env
    async def run():
        tid=s.schedule_import_translation_enrichment("run-a")
        runtime=s._refresh_tasks["import-enrich:run-a:active"]
        assert s._import_discovery_pending==1
        runtime.cancel()
        await asyncio.gather(runtime,return_exceptions=True)
        await asyncio.sleep(0)
        assert s._import_discovery_pending==0 and "import-enrich:run-a:active" not in s._refresh_tasks
        row=next(x for x in r.snapshot()["items"] if x["id"]==tid)
        assert row["state"]=="failed"
    asyncio.run(run())


def test_discovery_no_loop_does_not_leak_coroutine_or_pending_record(env):
    s,_,r=env
    assert s.schedule_import_translation_enrichment("run-a") is None
    assert s._import_discovery_pending==0 and r.snapshot()["items"]==[]


def test_same_run_coalesces_distinct_runs_queue_and_waiter_cancel_is_clean(env,monkeypatch):
    s,_,r=env;store(s,"run-a");store(s,"run-b")
    async def run():
        started=asyncio.Event();release=asyncio.Event()
        async def metadata(*a,**kw):started.set();await release.wait();return {}
        monkeypatch.setattr(s,"_resolve_import_metadata_identity",metadata)
        a=s.schedule_import_translation_enrichment("run-a")
        await asyncio.wait_for(started.wait(),1)
        b=s.schedule_import_translation_enrichment("run-b")
        assert s.schedule_import_translation_enrichment("run-a") is None
        await asyncio.sleep(.01)
        assert s._import_discovery_pending==2
        snap=r.snapshot();assert snap["active"]==1 and snap["queued"]==1
        second=next(x for x in snap["items"] if x["id"]==b)
        assert second["context"]=="Import run-b" and not second["started_at"]
        waiter=s._refresh_tasks["import-enrich:run-b:active"];waiter.cancel()
        await asyncio.gather(waiter,return_exceptions=True);await asyncio.sleep(0)
        assert s._import_discovery_pending==1 and not r.is_running(b)
        parent=s._refresh_tasks["import-enrich:run-a:active"];parent.cancel()
        await asyncio.gather(parent,return_exceptions=True);await asyncio.sleep(0)
        assert s._import_discovery_pending==0
    asyncio.run(run())


def test_cancel_import_drains_translation_and_metadata_before_next_run(env,monkeypatch):
    s,_,_=env;store(s,"run-a");store(s,"run-b")
    async def run():
        entered=set();closed=set();second_started=[]
        async def translate(*a,**kw):
            entered.add("translation")
            try:await asyncio.Event().wait()
            finally:
                await asyncio.sleep(.015);closed.add("translation")
        async def metadata(*a,**kw):
            entered.add("metadata")
            try:await asyncio.Event().wait()
            finally:
                await asyncio.sleep(.015);closed.add("metadata")
        monkeypatch.setattr(mod,"translator",SimpleNamespace(enabled=True,cached=lambda *a:"",translate=translate))
        monkeypatch.setattr(s,"_resolve_import_metadata_identity",metadata)
        original=s._prepare_import_discovery
        def prepare(run_id):
            if run_id=="run-b":second_started.append(set(closed));return []
            return original(run_id)
        monkeypatch.setattr(s,"_prepare_import_discovery",prepare)
        s.schedule_import_translation_enrichment("run-a")
        await eventually(lambda:len(entered)==2)
        s.schedule_import_translation_enrichment("run-b")
        parent=s._refresh_tasks["import-enrich:run-a:active"]
        parent.cancel()
        await asyncio.gather(parent,return_exceptions=True)
        await eventually(lambda:s._import_discovery_pending==0)
        assert closed=={"translation","metadata"} and second_started==[closed]
    asyncio.run(run())


def test_worker_failure_is_visible_and_partial_candidates_are_retained(env,monkeypatch):
    s,_,r=env;item=store(s)
    s._write_import_discovery_fields("run-a",item,candidate_ids_json="[123]")
    async def run():
        async def metadata(*a,**kw):return {}
        def broken(*a,**kw):raise RuntimeError("synthetic catalogue failure")
        monkeypatch.setattr(s,"_resolve_import_metadata_identity",metadata)
        monkeypatch.setattr(s,"_backup_candidate_ranked",broken)
        tid=s.schedule_import_translation_enrichment("run-a")
        await s._refresh_tasks["import-enrich:run-a:active"]
        row=s._read_import_item("run-a",item)
        assert row["suggestion_state"]=="error" and "synthetic catalogue failure" in row["reason"]
        assert json.loads(row["candidate_ids_json"])==[123]
        error=mod.error_registry.snapshot()["items"][0]
        assert error["component"]=="Import discovery" and error["error_type"]=="RuntimeError"
        task=next(x for x in r.snapshot()["items"] if x["id"]==tid)
        assert "Processed 1" in task["message"] and "deferred/errors" in task["message"]
    asyncio.run(run())


def test_already_skipped_row_is_not_searched_from_stale_discovery_queue(env,monkeypatch):
    s,db,_=env;item=store(s);stale=s._read_import_item("run-a",item)
    with db.catalog() as c:c.execute("UPDATE import_items SET state='skipped' WHERE id=?",(item,))
    monkeypatch.setattr(s,"_prepare_import_discovery",lambda *a:[stale])
    async def forbidden(*a,**kw):raise AssertionError("must not search skipped row")
    monkeypatch.setattr(s,"_resolve_import_metadata_identity",forbidden)
    async def run():
        s.schedule_import_translation_enrichment("run-a")
        await s._refresh_tasks["import-enrich:run-a:active"]
        assert s._read_import_item("run-a",item)["state"]=="skipped"
        assert not mod.error_registry.snapshot()["items"]
    asyncio.run(run())


def test_owned_gather_drains_siblings_when_one_child_fails():
    async def run():
        closed=asyncio.Event();started=asyncio.Event()
        async def wait():
            started.set()
            try:await asyncio.Event().wait()
            finally:await asyncio.sleep(.01);closed.set()
        async def fail():
            await started.wait();raise RuntimeError("child failed")
        with pytest.raises(RuntimeError,match="child failed"):
            await gather_owned(wait(),fail())
        assert closed.is_set()
    asyncio.run(run())


def test_owned_gather_double_cancel_still_drains_mutation_thread():
    from app.async_utils import run_blocking
    async def run():
        started=threading.Event();release=threading.Event();finished=threading.Event()
        def mutate():started.set();assert release.wait(2);finished.set()
        parent=asyncio.create_task(gather_owned(run_blocking(mutate)))
        await eventually(started.is_set)
        parent.cancel();await asyncio.sleep(.01);parent.cancel();await asyncio.sleep(.01)
        assert not parent.done()
        release.set()
        with pytest.raises(asyncio.CancelledError):await parent
        assert finished.is_set()
    asyncio.run(run())


def test_restart_resume_preserves_completed_paused_and_resolved_rows(env):
    s,db,_=env
    ids=[store(s,number=i) for i in range(1,5)]
    with db.catalog() as c:
        c.execute("UPDATE import_items SET suggestion_state='searching',suggestion_checked=0 WHERE id=?",(ids[0],))
        c.execute("UPDATE import_items SET suggestion_state='done',suggestion_checked=1,candidate_ids_json='[55]' WHERE id=?",(ids[1],))
        c.execute("UPDATE import_items SET suggestion_state='paused',suggestion_checked=0 WHERE id=?",(ids[2],))
        c.execute("UPDATE import_items SET state='added',suggestion_state='searching' WHERE id=?",(ids[3],))
    rows=s._prepare_import_discovery("run-a",resume_only=True)
    assert [r["id"] for r in rows]==[ids[0]]
    assert s._read_import_item("run-a",ids[1])["candidate_ids_json"]=="[55]"
    assert s._read_import_item("run-a",ids[2])["suggestion_state"]=="paused"
    assert s._read_import_item("run-a",ids[3])["state"]=="added"
    assert len(s._prepare_import_discovery("run-a"))==3  # explicit retry includes paused rows


def test_restart_recovery_queues_only_unfinished_runs(env,monkeypatch):
    s,_,r=env
    a=store(s,"a");b=store(s,"b");c=store(s,"c")
    s._write_import_discovery_fields("a",a,suggestion_checked=0,suggestion_state="searching")
    s._write_import_discovery_fields("b",b,suggestion_checked=1,suggestion_state="done")
    s._write_import_discovery_fields("c",c,suggestion_checked=0,suggestion_state="paused")
    calls=[]
    def queue(run,**kw):calls.append((run,kw));return "queued-id"
    monkeypatch.setattr(s,"schedule_import_translation_enrichment",queue)
    async def run():
        assert s.schedule_import_discovery_resume(delay=0)
        assert not s.schedule_import_discovery_resume(delay=0)
        await s._refresh_tasks["__import-discovery-resume__"]
        assert calls==[("a",{"resume_only":True})]
        assert r.snapshot()["completed"]==1
    asyncio.run(run())


def test_explicit_cancel_pauses_remaining_rows_for_next_restart(env,monkeypatch):
    s,_,r=env;item=store(s)
    async def run():
        entered=asyncio.Event()
        async def metadata(*a,**kw):entered.set();await asyncio.Event().wait()
        monkeypatch.setattr(s,"_resolve_import_metadata_identity",metadata)
        tid=s.schedule_import_translation_enrichment("run-a")
        parent=s._refresh_tasks["import-enrich:run-a:active"]
        await asyncio.wait_for(entered.wait(),1)
        assert r.request_cancel(tid)
        await asyncio.gather(parent,return_exceptions=True)
        assert s._read_import_item("run-a",item)["suggestion_state"]=="paused"
        assert not s._prepare_import_discovery("run-a",resume_only=True)
    asyncio.run(run())
