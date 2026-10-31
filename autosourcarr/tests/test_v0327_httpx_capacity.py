"""Pinned HTTPX timeout types and narrowly recoverable empty capacity responses."""
import asyncio
from unittest.mock import AsyncMock, Mock

import httpx
import pytest
from fastapi import HTTPException
from starlette.requests import Request

from app import main, service as module
from app.config import settings
from app.source_errors import assistance_reason, source_error_kind, source_error_summary
from app.suwayomi import SourceWorkDeferred, HumanAssistanceRequired, SuwayomiError
from test_v087_recovery import env, source, entry, manga
from test_v091_pages import chapter_fixture
from test_v0327_reader_responsiveness import warm_page, close_response

# Captured before conftest's autouse network guard is applied. This test restores
# it only for a one-connection HTTPX pool connected to its own loopback server.
_REAL_HANDLE = httpx.AsyncHTTPTransport.handle_async_request


@pytest.mark.parametrize("error, expected", [
    (httpx.PoolTimeout(""), "deferred"),
    (httpx.PoolTimeout("captcha"), "deferred"),
    (httpx.ConnectTimeout(""), "timeout"),
    (httpx.ReadTimeout(""), "timeout"),
    (httpx.WriteTimeout(""), "timeout"),
    (TimeoutError(""), "timeout"),
    (SourceWorkDeferred("capacity busy"), "deferred"),
])
def test_typed_empty_message_timeouts_retain_their_phase(error, expected):
    assert source_error_kind(error) == expected
    assert assistance_reason(error) == ""
    message = source_error_summary(error, "Example source")
    assert "deferred" in message if expected == "deferred" else "timed out" in message


@pytest.mark.parametrize("error", [httpx.ConnectTimeout(""), httpx.ReadTimeout(""), httpx.WriteTimeout("")])
def test_upstream_httpx_timeout_still_counts_towards_website_cooldown(env, monkeypatch, error):
    service, _ = env
    monkeypatch.setattr(settings, "source_failure_threshold", 1)
    service._record_source_result("upstream", error)
    assert service._source_failures["upstream"][0] == 1
    assert service._source_in_cooldown("upstream")


@pytest.mark.asyncio
async def test_real_one_connection_pool_timeout_keeps_source_health_and_diagnostic(env, monkeypatch):
    service, _ = env
    monkeypatch.setattr(settings, "source_failure_threshold", 2)
    entered, release = asyncio.Event(), asyncio.Event()
    connections = []
    handlers = set()

    async def handle(reader, writer):
        task = asyncio.current_task()
        handlers.add(task)
        try:
            await reader.readuntil(b"\r\n\r\n")
            connections.append(True)
            entered.set()
            await release.wait()
            writer.write(b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok")
            await writer.drain()
        finally:
            writer.close()
            await writer.wait_closed()
            handlers.discard(task)

    server = await asyncio.start_server(handle, "127.0.0.1", 0)
    port = server.sockets[0].getsockname()[1]
    monkeypatch.setattr(httpx.AsyncHTTPTransport, "handle_async_request", _REAL_HANDLE)
    client = httpx.AsyncClient(transport=httpx.AsyncHTTPTransport(
        limits=httpx.Limits(max_connections=1, max_keepalive_connections=1)),
        timeout=httpx.Timeout(2, pool=.03), trust_env=False)
    first = asyncio.create_task(client.get(f"http://127.0.0.1:{port}/occupied"))
    try:
        await asyncio.wait_for(entered.wait(), 1)
        with pytest.raises(httpx.PoolTimeout) as caught:
            await client.get(f"http://127.0.0.1:{port}/deferred")
        assert str(caught.value) == "", "Pinned pool did not produce the audited empty-message exception"
        assert connections == [True], "Deferred request unexpectedly reached the server"
        previous = (1, 0.0, "previous real upstream failure")
        service._source_failures["website"] = previous
        service._record_source_result("website", caught.value)
        assert service._source_failures["website"] == previous
        assert not service._source_in_cooldown("website")
        record = Mock()
        monkeypatch.setattr(module.error_registry, "record", record)
        item = {"title": "Deferred archive"}
        diagnostic = service._import_source_failure(item, "website", caught.value, query="Deferred archive")
        assert diagnostic["kind"] == "deferred" and diagnostic["detail"] == "PoolTimeout"
        assert diagnostic["query"] == "Deferred archive"
        assert item["_autosourcarr_source_diagnostics"]["website"] == diagnostic
        record.assert_not_called()
        release.set()
        assert (await first).text == "ok"
        assert (await client.get(f"http://127.0.0.1:{port}/recovered")).text == "ok"
    finally:
        release.set()
        if not first.done():
            first.cancel()
        await asyncio.gather(first, return_exceptions=True)
        await client.aclose()
        server.close()
        await server.wait_closed()
        if handlers:
            await asyncio.gather(*handlers, return_exceptions=True)


@pytest.mark.asyncio
@pytest.mark.parametrize("capacity", [httpx.PoolTimeout(""), SourceWorkDeferred("capacity busy")])
@pytest.mark.parametrize("checkpoint", [False, True])
async def test_empty_capacity_feed_is_not_cached_and_next_request_recovers(env, monkeypatch, capacity, checkpoint):
    service, database = env
    source(database, "a")
    monkeypatch.setattr(service, "_schedule_federated_refresh", lambda *a, **k: False)
    fetch = AsyncMock(side_effect=[capacity, {"mangas": [], "hasNextPage": False}])
    monkeypatch.setattr(module.source_host, "fetch_source_manga", fetch)
    kwargs = {"source_ids": ["a"], "query": "Absent but healthy archive"}
    first = await service.federated("SEARCH", force_live=True,
                                    reader_checkpoint={} if checkpoint else None, **kwargs)
    assert not first["items"] and first["source_errors"]
    if isinstance(capacity, httpx.PoolTimeout):
        assert "deferred" in first["source_errors"][0]["error"]
    assert not service._search_cache, "Local deferred empty response entered the memory cache"
    with database.catalog() as c:
        assert c.execute("SELECT COUNT(*) FROM browse_cache").fetchone()[0] == 0
    assert not service._source_failures
    second = await service.federated("SEARCH", **kwargs)
    assert not second["items"] and not second["source_errors"]
    assert not second["cache_hit"] and fetch.await_count == 2
    with database.catalog() as c:
        assert c.execute("SELECT COUNT(*) FROM browse_cache").fetchone()[0] == 1
    third = await service.federated("SEARCH", **kwargs)
    assert third["cache_hit"] and not third["items"]
    assert fetch.await_count == 2, "Healthy empty result lost its existing cache semantics"


@pytest.mark.asyncio
async def test_nonempty_partial_capacity_result_keeps_existing_cache(env, monkeypatch):
    service, database = env
    source(database, "a")
    source(database, "b")
    known = entry(service, "a", 9901, title="Archive Beyond The Midnight Gate")
    for method in ("schedule_translation_enrichment", "schedule_missing_source_discovery",
                   "schedule_additional_record_discovery", "_queue_feed_metadata_enrichment"):
        monkeypatch.setattr(service, method, lambda *a, **k: False)

    async def fetch(sid, *args):
        if sid == "b":
            raise httpx.PoolTimeout("")
        return {"mangas": [manga(9901, "Archive Beyond The Midnight Gate")], "hasNextPage": False}

    monkeypatch.setattr(module.source_host, "fetch_source_manga", fetch)
    monkeypatch.setattr(service, "_ingest_import_manga", AsyncMock(return_value=(known, 1.0, "known")))
    response = await service.federated("POPULAR", force_live=True, source_ids=["a", "b"])
    assert [item["canonical_id"] for item in response["items"]] == [known]
    assert response["source_errors"]
    assert len(service._search_cache) == 1
    with database.catalog() as c:
        assert c.execute("SELECT COUNT(*) FROM browse_cache").fetchone()[0] == 1
    assert "b" not in service._source_failures


@pytest.mark.asyncio
@pytest.mark.parametrize("error", [
    httpx.ReadTimeout(""), RuntimeError("site unavailable"),
    HumanAssistanceRequired({"id": 1, "name": "Source A", "reason": "CAPTCHA"}, 5),
])
async def test_empty_website_failure_cache_behavior_is_preserved(env, monkeypatch, error):
    service, database = env
    source(database, "a")
    monkeypatch.setattr(module.source_host, "fetch_source_manga", AsyncMock(side_effect=error))
    response = await service.federated("POPULAR", source_ids=["a"], force_live=True)
    assert not response["items"] and response["source_errors"]
    assert len(service._search_cache) == 1
    with database.catalog() as c:
        assert c.execute("SELECT COUNT(*) FROM browse_cache").fetchone()[0] == 1


@pytest.mark.asyncio
async def test_image_chapter_mapping_pool_deferral_preserves_retry_and_direct_fallback(env, monkeypatch):
    _, database, _, candidate_id, opened = await warm_page(env, monkeypatch, unresolved=True)
    mapping = AsyncMock(side_effect=[httpx.PoolTimeout(""), 0])
    monkeypatch.setattr(main.source_host, "chapter_index_for_id", mapping)
    first = await main.proxy_page(candidate_id, 0)
    await close_response(first)
    with database.origins() as c:
        assert c.execute("SELECT source_chapter_index FROM chapter_candidates WHERE id=?",
                         (candidate_id,)).fetchone()[0] == -1
    assert opened == [("https://images.invalid/page.jpg",)]
    second = await main.proxy_page(candidate_id, 0)
    await close_response(second)
    assert mapping.await_count == 2
    assert opened[-1] == (91001, 0, 0)
    with database.origins() as c:
        assert c.execute("SELECT source_chapter_index FROM chapter_candidates WHERE id=?",
                         (candidate_id,)).fetchone()[0] == 0


@pytest.mark.asyncio
async def test_page_resolution_pool_deferral_keeps_retrievable_pages_and_unresolved_mapping(env, monkeypatch):
    service, database, vid = chapter_fixture(env, monkeypatch)
    monkeypatch.setattr(module.source_host, "fetch_chapter_pages", AsyncMock(
        return_value=["https://images.invalid/page.jpg"]))
    mapping = AsyncMock(side_effect=[httpx.PoolTimeout(""), 0])
    monkeypatch.setattr(main.source_host, "chapter_index_for_id", mapping)
    result = await service.resolve_pages(vid)
    candidate_id = result["candidate"]["id"]
    info = service.page_fetch_info(candidate_id, 0)
    assert info["source_chapter_index"] == -1
    assert result["pages"] == [f"/api/v1/pages/{candidate_id}/0"]
    assert info["private_url"] == "https://images.invalid/page.jpg"
    monkeypatch.setattr(main, "service", service)
    opened = AsyncMock(return_value=httpx.Response(200, content=b"recovered-image"))
    monkeypatch.setattr(main.source_host, "open_page_image_stream", opened)
    response = await main.proxy_page(candidate_id, 0)
    await close_response(response)
    assert mapping.await_count == 2
    opened.assert_awaited_once_with(91001, 0, 0)
    with database.origins() as c:
        assert c.execute("SELECT source_chapter_index FROM chapter_candidates WHERE id=?",
                         (candidate_id,)).fetchone()[0] == 0


@pytest.mark.asyncio
@pytest.mark.parametrize("cached", [False, True])
async def test_shared_pool_saturation_stops_candidate_fanout_without_negative_cache(env, monkeypatch, cached):
    service, database, vid = chapter_fixture(env, monkeypatch, fallback=True)
    fetch = AsyncMock(return_value=["https://images.invalid/page.jpg"])
    monkeypatch.setattr(module.source_host, "fetch_chapter_pages", fetch)
    original = None
    if cached:
        original = await service.resolve_pages(vid)
        fetch.reset_mock()
        with database.origins() as c:
            c.execute("UPDATE candidate_pages SET updated_at='2000-01-01T00:00:00+00:00'")
    fetch.side_effect = httpx.PoolTimeout("")
    if cached:
        result = await service.resolve_pages(vid)
        assert result["pages"] == original["pages"]
        assert result["page_cache_stale"] and result["source_warning_deferred"]
    else:
        with pytest.raises(httpx.PoolTimeout):
            await service.resolve_pages(vid)
        with database.origins() as c:
            assert c.execute("SELECT COUNT(*) FROM candidate_pages").fetchone()[0] == 0
    assert fetch.await_count == 1, "Shared local pool pressure fanned out to every candidate"
    assert not service._page_lookup_failures and not service._source_failures
    fetch.side_effect = None
    assert (await service.resolve_pages(vid))["pages"]
    assert fetch.await_count == 2


@pytest.mark.asyncio
@pytest.mark.parametrize("route", ["pages", "image"])
@pytest.mark.parametrize("error", [httpx.PoolTimeout(""), httpx.ReadTimeout("")])
async def test_public_reader_pool_only_retry_does_not_report_website_failure(env, monkeypatch, route, error):
    service, _, vid, candidate, _ = await warm_page(env, monkeypatch)
    record = Mock(return_value="test-error")
    monkeypatch.setattr(main.error_registry, "record", record)
    if route == "pages":
        monkeypatch.setattr(service, "resolve_pages", AsyncMock(side_effect=error))
        request = main.chapter_pages(vid, Request({"type": "http"}))
    else:
        monkeypatch.setattr(main.source_host, "open_page_image_stream", AsyncMock(side_effect=error))
        request = main.proxy_page(candidate, 0)
    with pytest.raises(HTTPException) as caught:
        await request
    if isinstance(error, httpx.PoolTimeout):
        assert caught.value.status_code == 503
        assert caught.value.headers == {"Retry-After": "1"}
        assert "capacity" in caught.value.detail.lower()
        record.assert_not_called()
    else:
        assert caught.value.status_code == (503 if route == "pages" else 502)
        assert caught.value.headers is None
        assert record.call_count == (1 if route == "pages" else 0)


@pytest.mark.asyncio
@pytest.mark.parametrize("error", [httpx.PoolTimeout(""), SourceWorkDeferred("capacity busy")])
@pytest.mark.parametrize("checkpoint", [False, True])
async def test_native_cold_source_sync_capacity_is_not_retried_as_schema_or_cached_empty(env, monkeypatch, error, checkpoint):
    service, database = env
    monkeypatch.setattr(service, "_schedule_federated_refresh", lambda *a, **k: False)
    graphql = AsyncMock(side_effect=error)
    monkeypatch.setattr(module.source_host, "graphql", graphql)
    saved = {} if checkpoint else None
    kwargs = {"query": "Archive Beyond The Midnight Gate"}
    first = await service.federated("SEARCH", force_live=True, reader_checkpoint=saved, **kwargs)
    assert not first["items"] and first["source_errors"]
    assert graphql.await_count == 1, "Local saturation was retried as an older schema"
    assert "capacity" in first["source_errors"][0]["error"].lower()
    assert not service._search_cache
    with database.catalog() as c:
        assert c.execute("SELECT COUNT(*) FROM browse_cache").fetchone()[0] == 0
        assert c.execute("SELECT COUNT(*) FROM source_config").fetchone()[0] == 0
    if checkpoint:
        # A checkpoint retry retains the exact deferred outcome even though no
        # source result tuple exists; it must not turn into a publishable empty.
        await service.federated("SEARCH", force_live=True, reader_checkpoint=saved, **kwargs)
        assert graphql.await_count == 1 and not service._search_cache
    graphql.side_effect = None
    graphql.return_value = {"sources": {"nodes": []}}
    second = await service.federated("SEARCH", **kwargs)
    assert not second["cache_hit"] and not second["items"]
    assert graphql.await_count == 2
    assert len(service._search_cache) == 1
    with database.catalog() as c:
        assert c.execute("SELECT COUNT(*) FROM browse_cache").fetchone()[0] == 1
    third = await service.federated("SEARCH", **kwargs)
    assert third["cache_hit"] and graphql.await_count == 2


@pytest.mark.asyncio
async def test_native_source_schema_fallback_remains_available_and_empty_success_cacheable(env, monkeypatch):
    service, database = env
    graphql = AsyncMock(side_effect=[SuwayomiError("Cannot query field nodes on type Source"),
        {"sources": [{"id": "a", "name": "Source A", "lang": "en", "supportsLatest": True}]}])
    monkeypatch.setattr(module.source_host, "graphql", graphql)
    monkeypatch.setattr(module.source_host, "fetch_source_manga", AsyncMock(
        return_value={"mangas": [], "hasNextPage": False}))
    response = await service.federated("POPULAR", force_live=True)
    assert not response["items"] and not response["source_errors"]
    assert graphql.await_count == 2 and len(service._search_cache) == 1
    with database.catalog() as c:
        assert c.execute("SELECT COUNT(*) FROM source_config").fetchone()[0] == 1
        assert c.execute("SELECT COUNT(*) FROM browse_cache").fetchone()[0] == 1


@pytest.mark.asyncio
@pytest.mark.parametrize("error, attempts", [
    (RuntimeError("website error"), 2),
    (HumanAssistanceRequired({"id": 1, "name": "Source A", "reason": "CAPTCHA"}, 5), 1),
])
async def test_cold_source_sync_website_error_cache_semantics_are_preserved(env, monkeypatch, error, attempts):
    service, database = env
    monkeypatch.setattr(module.source_host, "graphql", AsyncMock(side_effect=error))
    response = await service.federated("POPULAR", force_live=True)
    assert not response["items"] and response["source_errors"]
    assert module.source_host.graphql.await_count == attempts
    assert len(service._search_cache) == 1
    with database.catalog() as c:
        assert c.execute("SELECT COUNT(*) FROM browse_cache").fetchone()[0] == 1
