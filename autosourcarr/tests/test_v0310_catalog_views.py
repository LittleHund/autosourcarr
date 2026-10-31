"""Source membership must be applied before page limits, with no upstream work."""
import asyncio
import json
import threading

import httpx
import pytest

from app import catalog_views, main
from app.read_lane import ReadLane
from test_v087_recovery import env, entry, manga, source


def setup(env, monkeypatch):
    service, database = env
    monkeypatch.setattr(catalog_views, "db", database)
    monkeypatch.setattr(main, "db", database)
    monkeypatch.setattr(main, "service", service)
    monkeypatch.setattr(service, "schedule_status_aux_refresh", lambda **kw: False)
    monkeypatch.setattr(service, "federated", lambda *a, **k: pytest.fail("Filter must not query sources"))
    for sid in ("9223372036854775801", "other"):
        source(database, sid)
    first = entry(service, "9223372036854775801", 10, "Alpha")
    second = entry(service, "other", 20, "Beta")
    service._upsert_source_variant(second, "9223372036854775801", manga(21, "Variant alias"), 1.0, "manual")
    third = entry(service, "other", 30, "Gamma")
    for i, eid in enumerate((first, second, third)):
        with database.catalog() as c:
            c.execute("UPDATE entries SET updated_at=? WHERE id=?", (f"2026-09-27T00:00:0{i}Z", eid))
    return service, database, first, second, third


def test_source_filter_before_paging_includes_additional_matches(env, monkeypatch):
    service, database, first, second, third = setup(env, monkeypatch)
    args = {"source_id": "9223372036854775801", "limit": 1}
    assert catalog_views.source_entry_ids(**args) == [second]
    assert catalog_views.source_entry_ids(**args, offset=1) == [first]
    assert catalog_views.source_entry_ids(**args, offset=2) == []
    assert catalog_views.source_entry_ids(**args, search="Variant alias") == [second]
    assert catalog_views.source_entry_ids("missing") == []
    assert catalog_views.source_entry_ids(**args, search="!!!") == []
    with database.catalog() as c:
        c.execute("UPDATE source_variants SET active=0 WHERE entry_id=?", (second,))
    assert catalog_views.source_entry_ids(**args) == [first]


def test_subbed_source_filter_preserves_library_order_and_empty_means_empty(env, monkeypatch):
    _, _, first, second, third = setup(env, monkeypatch)
    assert catalog_views.source_entry_ids("9223372036854775801", restrict_ids=[]) == []
    assert catalog_views.source_entry_ids("9223372036854775801", restrict_ids=[first, third, second], limit=1) == [first]
    assert catalog_views.source_entry_ids("9223372036854775801", restrict_ids=[first, third, second], offset=1, limit=1) == [second]


@pytest.mark.asyncio
async def test_browser_source_filter_serves_saved_entries_without_live_calls(env, monkeypatch):
    _, _, first, second, third = setup(env, monkeypatch)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url="http://test") as client:
        result = await client.get("/api/admin/browse", params={"source_id": "9223372036854775801", "page_size": 1})
        assert result.status_code == 200, result.text
        payload = result.json()
        assert [item["id"] for item in payload["items"]] == [second]
        assert payload["has_next_page"] and not payload["refreshing"]
        assert payload["source_id"] == "9223372036854775801"
        result = await client.get("/api/admin/browse", params={"source_id": "9223372036854775801", "page_size": 1, "page": 2})
        assert [item["id"] for item in result.json()["items"]] == [first]
        assert not result.json()["has_next_page"]


@pytest.mark.asyncio
async def test_matrix_source_filter_includes_variant_before_limit(env, monkeypatch):
    service, _, first, second, third = setup(env, monkeypatch)
    monkeypatch.setattr(service, "cached_subbed_payload", lambda: {"ok": True, "entry_ids": [first, third, second]})
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url="http://test") as client:
        result = await client.get("/api/admin/matrix", params={"source_id": "9223372036854775801", "limit": 1, "include_chapter_stats": "false"})
        assert result.status_code == 200, result.text
        assert [item["id"] for item in result.json()["entries"]] == [second]
        result = await client.get("/api/admin/matrix", params={"source_id": "9223372036854775801", "limit": 1, "subbed": "true", "include_chapter_stats": "false"})
        assert [item["id"] for item in result.json()["entries"]] == [first]


def test_source_match_count_uses_source_families_before_paging(env, monkeypatch):
    service, _, first, second, _ = setup(env, monkeypatch)
    service._upsert_match(first, "other", manga(40, "Alpha"), 1.0, "manual")
    assert catalog_views.source_entry_ids("9223372036854775801", exact_match_count=2) == [first]
    assert catalog_views.source_entry_ids("9223372036854775801", exact_match_count=1) == [second]
    assert catalog_views.source_entry_ids("9223372036854775801", sort="sources", limit=1) == [first]


@pytest.mark.asyncio
async def test_entry_details_use_catalogue_lane_even_with_default_pool_busy(monkeypatch):
    import anyio.to_thread
    limiter = anyio.to_thread.current_default_thread_limiter()
    previous = limiter.total_tokens
    limiter.total_tokens = 1
    entered, release = threading.Event(), threading.Event()
    task = asyncio.create_task(anyio.to_thread.run_sync(lambda: (entered.set(), release.wait(3))))
    monkeypatch.setattr(main.service, "get_entry", lambda eid: {"id": eid, "preferred_title": "Ready"})
    try:
        while not entered.is_set():
            await asyncio.sleep(.001)
        response = await asyncio.wait_for(main.admin_entry(12), timeout=.5)
        assert json.loads(response.body)["id"] == 12
        assert not task.done()
    finally:
        release.set()
        await task
        limiter.total_tokens = previous
