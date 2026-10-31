"""User-requested recovery must progress even while automatic work waits for idle."""
import asyncio
from unittest.mock import AsyncMock
from types import SimpleNamespace

import pytest

from app import service as module
from app.config import settings
from app.suwayomi import background_source_work
from test_v087_recovery import env, source, entry, manga


def test_idle_feed_does_not_reserve_the_background_source_slot(env, monkeypatch):
    s, _ = env

    async def scenario():
        sleeping = asyncio.Event()
        idle = asyncio.Event()
        async def wait_idle(*args, **kwargs):
            sleeping.set()
            await idle.wait()
        monkeypatch.setattr(s, "_wait_for_background_idle", wait_idle)
        s._schedule_federated_refresh("test-feed", {})
        await asyncio.wait_for(sleeping.wait(), .5)
        try:
            # Automatic work is asleep and must not prevent an explicit job.
            await asyncio.wait_for(s._background_source_semaphore.acquire(), .1)
            s._background_source_semaphore.release()
        finally:
            await s.cancel_background_tasks()
    asyncio.run(scenario())


def test_user_repair_starts_while_generic_maintenance_slots_are_occupied(env, monkeypatch):
    s, database = env
    source(database, "one")
    eid = entry(s, "one", 90001, attach=False)

    async def scenario():
        began = asyncio.Event()
        async def discover(*args, **kwargs):
            began.set()
            return {"added": 0}
        monkeypatch.setattr(s, "discover_missing_source_matches", discover)
        s._background_task_semaphore = asyncio.Semaphore(0)
        s._background_source_semaphore = asyncio.Semaphore(0)
        response = s.schedule_subbed_search_all([eid], unmatched_only=True)
        try:
            await asyncio.wait_for(began.wait(), .2)
            await s._refresh_tasks["__subbed-search-all__"]
            record = next(x for x in module.task_registry.snapshot()["items"] if x["id"] == response["task_id"])
            assert record["state"] == "completed"
        finally:
            await s.cancel_background_tasks()
    asyncio.run(scenario())


def test_priority_recovery_searches_during_continuous_browsing(env, monkeypatch):
    s, database = env
    source(database, "one")
    eid = entry(s, "one", 90002, title="The Archive Beyond Tomorrow", attach=False)

    async def scenario():
        idle_calls = []
        async def never_idle(*args, **kwargs):
            idle_calls.append(True)
            await asyncio.Event().wait()
        monkeypatch.setattr(s, "_wait_for_background_idle", never_idle)
        monkeypatch.setattr(settings, "manual_search_overall_timeout", .04)
        fetch = AsyncMock(return_value={"mangas": [], "hasNextPage": False})
        monkeypatch.setattr(module.source_host, "fetch_source_manga", fetch)
        async with background_source_work():
            await s.discover_missing_source_matches(eid, priority_rebuild=True)
        assert fetch.await_count > 0, "Explicit repair never reached the source because it waited for browser idle"
        assert not idle_calls
    asyncio.run(scenario())


def test_retry_visits_unsearched_sources_before_repeating_previous_misses(env, monkeypatch):
    s, database = env
    for sid in ("a", "b", "c"):
        source(database, sid)
    eid = entry(s, "a", 90003, attach=False)
    s._save_source_discovery_outcome(eid, "a", "no_results", "Previously searched")
    seen = []
    async def search(eid, sid, *args, **kwargs):
        seen.append(sid)
        return {"mangas": [], "warnings": []}
    monkeypatch.setattr(s, "search_source_for_manual", search)
    asyncio.run(s.discover_missing_source_matches(eid, priority_rebuild=True))
    assert seen[0] == "b"
    assert set(seen) == {"a", "b", "c"}


def test_timed_out_source_does_not_hide_the_next_source_or_trigger_cooldown(env, monkeypatch):
    s, database = env
    for sid in ("a", "b"):
        source(database, sid)
    eid = entry(s, "a", 90004, attach=False)
    monkeypatch.setattr(module, "settings", SimpleNamespace(**{**settings.model_dump(), "source_recovery_timeout_seconds": .03}))
    seen = []
    async def search(eid, sid, *args, **kwargs):
        seen.append(sid)
        if sid == "a":
            await asyncio.Event().wait()
        return {"mangas": [], "warnings": []}
    monkeypatch.setattr(s, "search_source_for_manual", search)
    async def scenario():
        await asyncio.wait_for(s.discover_missing_source_matches(eid, priority_rebuild=True), .5)
    asyncio.run(scenario())
    assert "b" in seen
    assert not s._source_failures.get("a")
    outcomes = {r["source_id"]: r["state"] for r in s.source_discovery_outcomes([eid])}
    assert outcomes == {"a": "deferred", "b": "no_results"}


def test_entry_deadline_advances_to_next_item_and_reports_partial_completion(env, monkeypatch):
    s, database = env
    source(database, "a")
    ids = [entry(s, "a", 90100 + i, title=f"Archive Volume {i}", attach=False) for i in range(2)]
    monkeypatch.setattr(module, "settings", SimpleNamespace(**{**settings.model_dump(), "subbed_search_entry_timeout_seconds": .04}))
    seen = []
    drained = []
    async def discover(eid, **kwargs):
        seen.append(eid)
        if eid == ids[0]:
            try:
                await asyncio.Event().wait()
            finally:
                drained.append(eid)
        return {"added": 0}
    monkeypatch.setattr(s, "discover_missing_source_matches", discover)
    async def scenario():
        result = s.schedule_subbed_search_all(ids, unmatched_only=True)
        runtime = s._refresh_tasks["__subbed-search-all__"]
        await asyncio.wait_for(runtime, .5)
        row = next(x for x in module.task_registry.snapshot()["items"] if x["id"] == result["task_id"])
        assert row["current"] == 2
        assert "partial" in row["message"].lower()
        assert "retry" in row["message"].lower()
    asyncio.run(scenario())
    assert seen == ids and drained == [ids[0]]


def test_item_timeout_preserves_committed_matches_and_reports_them(env, monkeypatch):
    s, database = env
    source(database, "a")
    eid = entry(s, "a", 0, attach=False)
    monkeypatch.setattr(settings, "subbed_search_entry_timeout_seconds", .03)
    async def discover(*args, **kwargs):
        s._upsert_match(eid, "a", manga(90777), 1.0, "manual", True)
        await asyncio.Event().wait()
    monkeypatch.setattr(s, "discover_missing_source_matches", discover)
    async def scenario():
        queued = s.schedule_subbed_search_all([eid], unmatched_only=True)
        await asyncio.wait_for(s._refresh_tasks["__subbed-search-all__"], .5)
        row = next(x for x in module.task_registry.snapshot()["items"] if x["id"] == queued["task_id"])
        assert "1 record(s) added" in row["message"]
        assert "partial" in row["message"]
    asyncio.run(scenario())
    assert s.get_entry(eid)["matches"][0]["source_manga_id"] == 90777


@pytest.mark.parametrize("before_start", [False, True])
def test_repair_cancellation_drains_children_and_allows_a_new_run(env, monkeypatch, before_start):
    s, database = env
    source(database, "a")
    eid = entry(s, "a", 0, attach=False)
    async def scenario():
        began = asyncio.Event()
        drained = []
        async def discover(*args, **kwargs):
            began.set()
            try:
                await asyncio.Event().wait()
            finally:
                drained.append(True)
        monkeypatch.setattr(s, "discover_missing_source_matches", discover)
        first = s.schedule_subbed_search_all([eid], unmatched_only=True)
        runtime = s._refresh_tasks["__subbed-search-all__"]
        if not before_start:
            await asyncio.wait_for(began.wait(), .5)
        runtime.cancel()
        await asyncio.gather(runtime, return_exceptions=True)
        assert drained == ([] if before_start else [True])
        record = next(x for x in module.task_registry.snapshot()["items"] if x["id"] == first["task_id"])
        assert record["state"] == "failed" and record["message"] == "Cancelled by user"
        monkeypatch.setattr(s, "discover_missing_source_matches", AsyncMock(return_value={"added": 0}))
        again = s.schedule_subbed_search_all([eid], unmatched_only=True)
        assert again["queued"] and not again.get("already_running")
        await s._refresh_tasks["__subbed-search-all__"]
    asyncio.run(scenario())


def test_interrupted_source_attempt_moves_behind_untouched_sources_on_retry(env,monkeypatch):
    service,db=env
    for sid in ['a','b']: source(db,sid)
    eid=entry(service,'a',90001,attach=False)
    monkeypatch.setattr(settings,'subbed_search_entry_timeout_seconds',.025)
    monkeypatch.setattr(settings,'source_recovery_timeout_seconds',.3)
    seen=[]
    async def search(eid,sid,*args,**kwargs):
        seen.append(sid)
        if sid=='a': await asyncio.Event().wait()
        return {'mangas':[], 'warnings':[]}
    monkeypatch.setattr(service,'search_source_for_manual',search)
    async def scenario():
        for _ in range(3):
            service.schedule_subbed_search_all([eid],unmatched_only=True)
            await service._refresh_tasks['__subbed-search-all__']
    asyncio.run(scenario())
    assert 'b' in seen, f'Three partial repairs repeated {seen}; untouched source never received a request'
