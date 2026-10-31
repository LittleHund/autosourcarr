"""Reader demand coalesces without losing cancellation, fallback or retry semantics."""
import asyncio
from unittest.mock import AsyncMock

import pytest

from app import service as module
from app.service import ChapterPagesBlocked
from test_v087_recovery import env, source, entry, manga


def chapter_fixture(env, monkeypatch, *, fallback=False):
    s, database = env
    source(database, "a")
    eid = entry(s, "a", 91001, title="Archive Beyond The Midnight Gate")
    s._ingest_chapters(eid, {"source_id": "a", "source_name": "Source A", "source_manga_id": 91001}, s._match_id(eid, "a"), [{"id": 91011, "chapterNumber": 1, "name": "Chapter 1", "pageCount": 2, "url": "/1"}])
    if fallback:
        source(database, "b")
        s._upsert_match(eid, "b", manga(91002, "Archive Beyond The Midnight Gate"), 1.0)
        s._ingest_chapters(eid, {"source_id": "b", "source_name": "Source B", "source_manga_id": 91002}, s._match_id(eid, "b"), [{"id": 91012, "chapterNumber": 1, "name": "Chapter 1", "pageCount": 2, "url": "/1"}])
    monkeypatch.setattr(module.source_host, "chapter_index_for_id", AsyncMock(return_value=0))
    return s, database, s.list_chapters(eid)[0]["id"]


def test_simultaneous_readers_share_one_lookup_but_not_response(env, monkeypatch):
    s, _, vid = chapter_fixture(env, monkeypatch)
    async def scenario():
        started, finish = asyncio.Event(), asyncio.Event()
        calls = []
        async def pages(cid):
            calls.append(cid)
            started.set()
            await finish.wait()
            return ["https://images.invalid/1.jpg"]
        monkeypatch.setattr(module.source_host, "fetch_chapter_pages", pages)
        pending = [asyncio.create_task(s.resolve_pages(vid)) for _ in range(8)]
        await started.wait()
        await asyncio.sleep(0)
        assert len(calls) == 1
        finish.set()
        results = await asyncio.gather(*pending)
        results[0]["pages"][0] = "https://reader-one.invalid/page"
        assert results[1]["pages"][0].startswith("/api/v1/pages/")
        assert results[0]["candidate"] is not results[1]["candidate"]
        assert not s._page_resolution_tasks
        assert not s._page_resolution_waiters
    asyncio.run(scenario())


def test_reader_cancellation_keeps_other_reader_alive(env, monkeypatch):
    s, _, vid = chapter_fixture(env, monkeypatch)
    async def scenario():
        started, finish, cancelled = asyncio.Event(), asyncio.Event(), asyncio.Event()
        async def pages(cid):
            started.set()
            try:
                await finish.wait()
                return ["https://images.invalid/1.jpg"]
            except asyncio.CancelledError:
                cancelled.set()
                raise
        monkeypatch.setattr(module.source_host, "fetch_chapter_pages", pages)
        first = asyncio.create_task(s.resolve_pages(vid))
        second = asyncio.create_task(s.resolve_pages(vid))
        await started.wait()
        first.cancel()
        with pytest.raises(asyncio.CancelledError):
            await first
        assert not cancelled.is_set()
        finish.set()
        assert (await second)["pages"]
        assert not s._page_resolution_waiters
    asyncio.run(scenario())


@pytest.mark.parametrize("shutdown", [False, True])
def test_last_reader_cancellation_and_shutdown_drain_upstream(env, monkeypatch, shutdown):
    s, _, vid = chapter_fixture(env, monkeypatch)
    async def scenario():
        started, drained = asyncio.Event(), asyncio.Event()
        async def pages(cid):
            started.set()
            try:
                await asyncio.Event().wait()
            finally:
                drained.set()
        monkeypatch.setattr(module.source_host, "fetch_chapter_pages", pages)
        request = asyncio.create_task(s.resolve_pages(vid))
        await started.wait()
        if shutdown:
            await s.cancel_background_tasks()
        else:
            request.cancel()
        with pytest.raises(asyncio.CancelledError):
            await request
        assert drained.is_set()
        assert not s._page_resolution_tasks
        assert not s._page_resolution_waiters
        assert not s._auxiliary_tasks
        assert not s._page_lookup_failures
    asyncio.run(scenario())


def test_captcha_repeated_reads_wait_briefly_then_retry(env, monkeypatch):
    s, _, vid = chapter_fixture(env, monkeypatch)
    fetch = AsyncMock(side_effect=RuntimeError("Shape-selecting captcha detected. at java.Trace(Unknown)"))
    monkeypatch.setattr(module.source_host, "fetch_chapter_pages", fetch)
    async def scenario():
        for _ in range(5):
            with pytest.raises(ChapterPagesBlocked) as caught:
                await s.resolve_pages(vid)
            assert 1 <= caught.value.retry_after <= 30
            assert "WebView" in str(caught.value)
            assert "java.Trace" not in str(caught.value)
            assert "java.Trace" in caught.value.detail
        assert fetch.await_count == 1
        key = ("a", 91011)
        expires, detail = s._page_lookup_failures[key]
        s._page_lookup_failures[key] = (expires - 31, detail)
        fetch.side_effect = None
        fetch.return_value = ["https://images.invalid/recovered.jpg"]
        assert (await s.resolve_pages(vid))["pages"]
        assert fetch.await_count == 2
        assert not s._page_lookup_failures
    asyncio.run(scenario())


def test_captcha_on_one_candidate_still_allows_other_source(env, monkeypatch):
    s, _, vid = chapter_fixture(env, monkeypatch, fallback=True)
    order = []
    async def fetch(cid):
        order.append(cid)
        if cid == 91011:
            raise RuntimeError("Shape-selecting captcha detected")
        return ["https://images.invalid/fallback.jpg"]
    monkeypatch.setattr(module.source_host, "fetch_chapter_pages", fetch)
    result = asyncio.run(s.resolve_pages(vid))
    assert result["candidate"]["source_id"] == "b"
    assert order == [91011, 91012]


def test_captcha_can_use_stale_page_map_without_retry_storm(env, monkeypatch):
    s, database, vid = chapter_fixture(env, monkeypatch)
    fetch = AsyncMock(return_value=["https://images.invalid/stale.jpg"])
    monkeypatch.setattr(module.source_host, "fetch_chapter_pages", fetch)
    async def scenario():
        original = await s.resolve_pages(vid)
        with database.origins() as o:
            o.execute("UPDATE candidate_pages SET updated_at='2000-01-01T00:00:00+00:00'")
        fetch.side_effect = RuntimeError("Shape-selecting captcha detected")
        for _ in range(3):
            result = await s.resolve_pages(vid)
            assert result["pages"] == original["pages"]
            assert result["page_cache_stale"]
            assert "CAPTCHA" in result["source_warning"]
        assert fetch.await_count == 2
    asyncio.run(scenario())


def test_transient_failure_is_not_cached_as_captcha(env, monkeypatch):
    s, _, vid = chapter_fixture(env, monkeypatch)
    fetch = AsyncMock(side_effect=[RuntimeError("temporary unavailable"), ["https://images.invalid/retry.jpg"]])
    monkeypatch.setattr(module.source_host, "fetch_chapter_pages", fetch)
    async def scenario():
        with pytest.raises(RuntimeError, match="temporary unavailable"):
            await s.resolve_pages(vid)
        assert (await s.resolve_pages(vid))["pages"]
        assert fetch.await_count == 2
        assert not s._page_lookup_failures
    asyncio.run(scenario())


@pytest.mark.parametrize("full", [False, True])
def test_database_reset_clears_chapter_captcha_memory(env, monkeypatch, full):
    s, _, vid = chapter_fixture(env, monkeypatch)
    s._page_lookup_failures[("a", 91011)] = (float("inf"), "CAPTCHA")
    (s.full_reset_database if full else s.reset_database)()
    assert not s._page_lookup_failures


def test_chapter_api_advertises_retry_delay_and_preserves_full_diagnostics(env, monkeypatch):
    from app import main
    from app.error_registry import ErrorRegistry
    from fastapi import HTTPException
    from starlette.requests import Request
    s, _, vid = chapter_fixture(env, monkeypatch)
    registry = ErrorRegistry()
    monkeypatch.setattr(main, "service", s)
    monkeypatch.setattr(main, "error_registry", registry)
    fetch = AsyncMock(side_effect=RuntimeError("Shape-selecting captcha detected at java.Trace(Unknown)"))
    monkeypatch.setattr(module.source_host, "fetch_chapter_pages", fetch)
    request = Request({"type": "http", "scheme": "https", "server": ("reader.invalid", 443), "path": "/", "headers": []})
    async def scenario():
        with pytest.raises(HTTPException) as caught:
            await main.chapter_pages(vid, request)
        assert caught.value.status_code == 503
        assert caught.value.headers == {"Retry-After": "30"}
        assert "WebView" in caught.value.detail
        assert "java.Trace" not in caught.value.detail
        row = registry.snapshot()["items"][0]
        assert "java.Trace" in row["detail"]
        assert row["key"] == f"source-host-pages:{vid}"
        # Another chapter recovering cannot hide this chapter's challenge.
        registry.record("Private source host", "Another source challenge", key="source-host-pages:99999")
        key = ("a", 91011)
        expires, detail = s._page_lookup_failures[key]
        s._page_lookup_failures[key] = (expires - 31, detail)
        fetch.side_effect = None
        fetch.return_value = ["https://images.invalid/recovered.jpg"]
        result = await main.chapter_pages(vid, request)
        assert result["pages"][0].startswith("https://reader.invalid/")
        active = registry.snapshot(include_resolved=False)["items"]
        assert [row["key"] for row in active] == ["source-host-pages:99999"]
    asyncio.run(scenario())


def test_source_host_health_cannot_resolve_individual_chapter_captcha(env, monkeypatch):
    from app import main
    from app.error_registry import ErrorRegistry
    s, _ = env
    registry = ErrorRegistry()
    monkeypatch.setattr(main, "service", s)
    monkeypatch.setattr(main, "error_registry", registry)
    monkeypatch.setattr(main, "main_suwayomi", None)
    monkeypatch.setattr(s, "schedule_status_aux_refresh", lambda **kwargs: False)
    monkeypatch.setattr(s, "status_stats_snapshot", AsyncMock(return_value={}))
    monkeypatch.setattr(main.source_host, "health", AsyncMock(return_value={"ok": True}))
    monkeypatch.setattr(main.translator, "health", AsyncMock(return_value={"ok": True}))
    for key in ("source-host-pages", "source-host-pages:357942"):
        registry.record("Private source host", "CAPTCHA unresolved", key=key)
    assert asyncio.run(main.health())["ok"] is True
    assert registry.snapshot()["active_count"] == 2


def test_repeated_final_reader_cancellation_cannot_interrupt_source_cleanup(env, monkeypatch):
    s, _, vid = chapter_fixture(env, monkeypatch)
    async def scenario():
        started, cleaning, release_cleanup, drained = (asyncio.Event() for _ in range(4))
        async def pages(cid):
            started.set()
            try:
                await asyncio.Event().wait()
            finally:
                cleaning.set()
                await release_cleanup.wait()
                drained.set()
        monkeypatch.setattr(module.source_host, "fetch_chapter_pages", pages)
        request = asyncio.create_task(s.resolve_pages(vid))
        await started.wait()
        request.cancel()
        await asyncio.wait_for(cleaning.wait(), .5)
        request.cancel()
        await asyncio.sleep(0)
        await asyncio.sleep(0)
        try:
            assert not request.done(), "Repeated cancellation escaped while source cleanup was still active"
            assert not drained.is_set()
        finally:
            release_cleanup.set()
            with pytest.raises(asyncio.CancelledError):
                await request
        assert drained.is_set()
        assert not s._page_resolution_tasks
        assert not s._page_resolution_waiters
        assert not s._auxiliary_tasks
    asyncio.run(scenario())
