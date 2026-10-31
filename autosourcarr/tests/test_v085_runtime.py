"""v0.2.85: cancellation, HTTP pool/backpressure and reader-stream regressions."""
import asyncio
import gzip
import threading
from pathlib import Path

import httpx
import pytest

from app.async_utils import run_blocking
from app.config import settings
from app.service import AutoSourcarrService
from app.suwayomi import SuwayomiClient, bounded_map
from app.task_registry import TaskRegistry


def test_parent_cancellation_drains_running_and_queued_source_work():
    async def scenario():
        live = set()
        ready = asyncio.Event()
        async def work(i):
            live.add(i)
            if len(live) == 2:
                ready.set()
            try:
                await asyncio.Event().wait()
            finally:
                live.remove(i)
        parent = asyncio.create_task(bounded_map(range(500), work, limit=2))
        await asyncio.wait_for(ready.wait(), 1)
        parent.cancel()
        with pytest.raises(asyncio.CancelledError):
            await parent
        assert not live, "children must stop before the parent reports cancellation"
    asyncio.run(scenario())


def test_mapper_worker_count_scales_with_concurrency_not_catalogue_size():
    async def scenario():
        before = set(asyncio.all_tasks())
        gate = asyncio.Event()
        async def work(i):
            await gate.wait()
            return i * 2
        parent = asyncio.create_task(bounded_map(range(1000), work, limit=3))
        await asyncio.sleep(.01)
        assert len(set(asyncio.all_tasks()) - before) <= 4
        gate.set()
        rows = await parent
        assert [r[1] for r in rows] == [i * 2 for i in range(1000)]
    asyncio.run(scenario())


def test_mapper_native_timeout_without_configured_timeout_is_not_typeerror():
    async def work(_):
        raise TimeoutError("upstream timeout")
    rows = asyncio.run(bounded_map([1], work))
    assert isinstance(rows[0][2], TimeoutError)
    assert "upstream timeout" in str(rows[0][2])


def test_mapper_retains_fast_results_and_drains_timed_out_worker():
    async def scenario():
        stopped = asyncio.Event()
        async def work(i):
            if i == 1:
                return "fast"
            try:
                await asyncio.Event().wait()
            finally:
                stopped.set()
        rows = await bounded_map([1, 2], work, limit=2, overall_timeout=.03)
        assert rows[0] == (1, "fast", None)
        assert isinstance(rows[1][2], TimeoutError)
        assert stopped.is_set()
    asyncio.run(scenario())


def test_mapper_self_cancel_does_not_abandon_later_items():
    async def work(i):
        if i == 1:
            raise asyncio.CancelledError()
        return i
    rows = asyncio.run(bounded_map([1, 2], work, limit=1))
    assert rows[0][2] is not None and rows[1] == (2, 2, None)


def test_thread_mutation_finishes_before_cancel_returns_even_when_cancelled_twice():
    async def scenario():
        started, release, finished = threading.Event(), threading.Event(), threading.Event()
        def write():
            started.set()
            assert release.wait(2)
            finished.set()
        task = asyncio.create_task(run_blocking(write))
        while not started.is_set():
            await asyncio.sleep(.001)
        task.cancel()
        await asyncio.sleep(.005)
        task.cancel()
        await asyncio.sleep(.005)
        assert not task.done()
        release.set()
        with pytest.raises(asyncio.CancelledError):
            await task
        assert finished.is_set()
    asyncio.run(scenario())


def test_http_transport_uses_configured_limits(monkeypatch):
    monkeypatch.setattr(settings, "http_max_connections", 17)
    monkeypatch.setattr(settings, "http_max_keepalive_connections", 7)
    client = SuwayomiClient("http://source.invalid")
    assert client.client._transport._pool._max_connections == 17
    assert client.client._transport._pool._max_keepalive_connections == 7
    asyncio.run(client.close())


def test_stream_respects_its_explicit_timeout():
    async def scenario():
        seen = []
        def request(req):
            seen.append(req.extensions["timeout"])
            return httpx.Response(200, content=b"page")
        client = SuwayomiClient("http://source.invalid")
        await client.client.aclose()
        client.client = httpx.AsyncClient(transport=httpx.MockTransport(request))
        response = await client.open_binary_stream("/page", timeout=2.5)
        assert seen[0]["read"] == 2.5
        await response.aclose()
        await client.close()
    asyncio.run(scenario())


def test_buffered_image_limit_applies_after_gzip_decompression(monkeypatch):
    import app.suwayomi as module
    monkeypatch.setattr(module, "MAX_IMAGE_BYTES", 100)
    async def scenario():
        client = SuwayomiClient("http://source.invalid")
        await client.client.aclose()
        client.client = httpx.AsyncClient(transport=httpx.MockTransport(
            lambda req: httpx.Response(200, content=gzip.compress(b"x" * 101),
                                      headers={"content-encoding": "gzip"})))
        try:
            with pytest.raises(Exception, match="buffered-analysis"):
                await client.fetch_binary("/sample")
        finally:
            await client.close()
    asyncio.run(scenario())


def test_page_proxy_does_not_forward_compressed_content_length(monkeypatch):
    import app.main as main
    raw = b"decoded-image" * 100
    compressed = gzip.compress(raw)
    upstream = httpx.Response(200, content=compressed,
        headers={"content-encoding": "gzip", "content-length": str(len(compressed)), "content-type": "image/png"})
    monkeypatch.setattr(main.service, "page_fetch_info", lambda *args: {"source_chapter_index": 0, "source_manga_id": 1})
    async def fetch(*args):
        return upstream
    monkeypatch.setattr(main.source_host, "open_page_image_stream", fetch)
    async def scenario():
        response = await main.proxy_page(1, 0)
        assert "content-length" not in response.headers
        assert b"".join([part async for part in response.body_iterator]) == raw
        assert upstream.is_closed
    asyncio.run(scenario())


def test_all_scheduler_families_and_retry_handle_are_drained():
    async def scenario():
        service = AutoSourcarrService()
        tasks = []
        for bucket in service.background_task_buckets():
            task = asyncio.create_task(asyncio.sleep(60))
            bucket["test"] = task
            tasks.append(task)
        handle = asyncio.get_running_loop().call_later(60, lambda: None)
        service._catalog_retry_handle = handle
        await service.cancel_background_tasks()
        assert all(task.done() for task in tasks)
        assert all(not bucket for bucket in service.background_task_buckets())
        assert handle.cancelled()
    asyncio.run(scenario())


def test_task_cancel_from_worker_thread_uses_owning_loop():
    async def scenario():
        loop = asyncio.get_running_loop()
        loop.set_debug(True)
        registry = TaskRegistry()
        task_id = registry.create("cancel test")
        task = asyncio.create_task(asyncio.sleep(60))
        await asyncio.sleep(0)
        registry.bind_runtime(task_id, task)
        assert await asyncio.to_thread(registry.request_cancel, task_id)
        with pytest.raises(asyncio.CancelledError):
            await task
    asyncio.run(scenario())


def test_docker_context_includes_version_file():
    root = Path(__file__).resolve().parents[2]
    lines = (root / "autosourcarr/Dockerfile").read_text().splitlines()
    assert any(line.startswith("COPY ") and "VERSION" in line for line in lines)

@pytest.mark.parametrize("cursor", [None, "repeated"])
def test_library_pagination_fails_closed_instead_of_returning_incomplete_snapshot(cursor):
    async def scenario():
        client = SuwayomiClient("http://source.invalid")
        count=0
        async def graphql(*args, **kwargs):
            nonlocal count
            count+=1
            return {"mangas": {"nodes": [{"id": count,'sourceId':'9223372036854775807','url':str(count),'inLibrary':True}], "pageInfo": {"hasNextPage": True, "endCursor": cursor}}}
        client.graphql = graphql
        try:
            with pytest.raises(Exception, match="pagination"):
                await client.library_mangas()
        finally:
            await client.close()
    asyncio.run(scenario())


def test_reset_owns_startup_jobs_and_prevents_delayed_followup():
    async def scenario():
        service = AutoSourcarrService()
        followups = []
        async def startup():
            await asyncio.sleep(60)
            followups.append("started after reset")
        task = service.track_startup_task("test-startup", startup())
        await asyncio.sleep(0)
        await service.cancel_background_tasks()
        assert task.cancelled()
        assert not followups
        assert all(not bucket for bucket in service.background_task_buckets())
    asyncio.run(scenario())


def test_completed_startup_job_releases_ownership():
    async def scenario():
        service = AutoSourcarrService()
        async def startup():
            return 42
        task = service.track_startup_task("test-startup", startup())
        assert await task == 42
        await asyncio.sleep(0)
        assert "__lifespan-test-startup__" not in service._refresh_tasks
    asyncio.run(scenario())
