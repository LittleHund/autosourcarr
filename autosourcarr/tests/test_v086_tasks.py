"""Queue status and HTTP control-plane regressions (no live sources)."""
import asyncio
import threading
from datetime import datetime, timedelta, timezone

import anyio
import httpx
import pytest

from app.config import settings
from app.task_registry import TaskRegistry
from app import main


def test_waiting_activities_have_no_execution_clock_or_eta():
    r = TaskRegistry()
    queued = r.create("Import", total=10, activity="queued", context="Import run-a")
    r.update(queued, current=3)
    r._tasks[queued].waiting_since = (datetime.now(timezone.utc)-timedelta(seconds=120)).isoformat()
    row = r.snapshot()["items"][0]
    assert row["state"] == "running"  # compatible lifecycle/cancellation contract
    assert row["activity"] == "queued" and not row["started_at"]
    assert row["eta_seconds"] is None and row["rate_per_second"] is None
    assert row["elapsed_seconds"] == 0 and 119 <= row["waiting_seconds"] <= 121
    assert row["context"] == "Import run-a"


def test_mark_waiting_does_not_restart_timer_every_queue_poll():
    r = TaskRegistry()
    tid = r.create("Duplicates", activity="waiting")
    before = r._tasks[tid].waiting_since
    for i in range(10):
        r.mark_waiting(tid, message=f"Waiting for {i} jobs")
    assert r._tasks[tid].waiting_since == before
    r.mark_active(tid)
    row = r.snapshot()["items"][0]
    assert row["activity"] == "running" and row["started_at"]
    assert row["waiting_seconds"] == 0 and row["elapsed_seconds"] <= 1


def test_activity_counts_and_executing_jobs_sort_first():
    r = TaskRegistry()
    w = r.create("Deferred", activity="waiting")
    q = r.create("Queued", activity="queued")
    a = r.create("Active")
    d = r.create("Done"); r.complete(d)
    snap = r.snapshot()
    assert [x["id"] for x in snap["items"]] == [a,q,w,d]
    assert (snap["running"], snap["active"], snap["queued"], snap["waiting"]) == (3,1,1,1)
    assert r.clear_history()["cleared"] == 1
    assert r.snapshot()["running"] == 3


@pytest.mark.parametrize("activity", ["queued", "waiting"])
def test_waiting_tasks_keep_their_cancel_button_and_leave_no_active_record(activity):
    r = TaskRegistry()
    tid = r.create("Deferred", activity=activity)
    r.bind_canceller(tid, lambda: None)
    assert r.request_cancel(tid)
    assert r.snapshot()["running"] == 0
    assert r.snapshot()["items"][0]["state"] == "failed"
    r.mark_active(tid)
    assert not r.is_running(tid)


def test_invalid_task_activity_rejected():
    with pytest.raises(ValueError):
        TaskRegistry().create("test", activity="imaginary")


@pytest.mark.parametrize("path", ["/api/admin/duplicate-candidates", "/api/admin/entries/summaries", "/api/admin/duplicates/automatic"])
def test_batch_reads_and_self_tracking_automation_are_not_application_updates(path):
    assert main._mutation_task_name("POST", path) is None


def test_actual_mutations_remain_visible_and_fallback_names_are_specific():
    assert main._mutation_task_name("POST", "/api/admin/duplicates/review") == "Review duplicate"
    assert main._mutation_task_name("PUT", "/api/admin/sources/1") == "Update source"
    assert "POST" in main._mutation_task_name("POST", "/api/admin/something-new")
    assert "something-new" in main._mutation_task_name("POST", "/api/admin/something-new")


def test_duplicate_lookup_success_and_bad_input_never_create_update_tasks(monkeypatch):
    registry = TaskRegistry()
    monkeypatch.setattr(main, "task_registry", registry)
    monkeypatch.setattr(settings, "admin_token", "")
    monkeypatch.setattr(main.service, "duplicate_candidates_cached", lambda ids: {})
    monkeypatch.setattr(main.service, "duplicate_snapshot_summary", lambda: {"stale":False})
    async def run():
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url="http://testserver") as client:
            for _ in range(4):
                response = await client.post("/api/admin/duplicate-candidates", json={"ids":[1,2]})
                assert response.status_code == 200
            response = await client.post("/api/admin/duplicate-candidates", json={"ids":"bad"})
            assert response.status_code == 400 and "JSON array" in response.text
            assert registry.snapshot()["items"] == []
    asyncio.run(run())


def test_tasks_errors_and_cancel_work_even_when_sync_request_pool_is_full(monkeypatch):
    registry = TaskRegistry()
    tid = registry.create("Queued import", activity="queued")
    registry.bind_canceller(tid, lambda: None)
    monkeypatch.setattr(main, "task_registry", registry)
    monkeypatch.setattr(settings, "admin_token", "v86-secret")
    async def run():
        limiter = anyio.to_thread.current_default_thread_limiter()
        old = limiter.total_tokens
        limiter.total_tokens = 1
        entered, release = threading.Event(), threading.Event()
        def blocked_sync_route():
            entered.set()
            assert release.wait(4)
        blocker = asyncio.create_task(anyio.to_thread.run_sync(blocked_sync_route))
        try:
            while not entered.is_set():
                await asyncio.sleep(.001)
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app),base_url="http://testserver",headers={"X-Admin-Token":"v86-secret"}) as client:
                for path in ("/api/admin/tasks", "/api/admin/errors"):
                    response = await asyncio.wait_for(client.get(path), .5)
                    assert response.status_code == 200
                denied = await asyncio.wait_for(client.get("/api/admin/tasks",headers={"X-Admin-Token":"wrong"}), .5)
                assert denied.status_code == 401  # bypassing the threadpool is NOT an auth bypass
                response = await asyncio.wait_for(client.post(f"/api/admin/tasks/{tid}/cancel"), .5)
                assert response.status_code == 200 and not registry.is_running(tid)
        finally:
            release.set()
            await blocker
            limiter.total_tokens = old
    asyncio.run(run())
