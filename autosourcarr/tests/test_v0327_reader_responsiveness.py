"""Actual image-route blocking and ownership races with disposable SQLite."""
import asyncio
import gzip
import threading
import time
from unittest.mock import AsyncMock

import httpx
import pytest
from fastapi import HTTPException

from app import main, service as module
from app.suwayomi import SourceWorkDeferred
from test_v087_recovery import env
from test_v091_pages import chapter_fixture


async def warm_page(env, monkeypatch, *, unresolved=False):
    service, database, vid = chapter_fixture(env, monkeypatch)
    monkeypatch.setattr(module.source_host, "fetch_chapter_pages", AsyncMock(
        return_value=["https://images.invalid/page.jpg"]))
    result = await service.resolve_pages(vid)
    candidate_id = result["candidate"]["id"]
    if unresolved:
        service.set_candidate_chapter_index(candidate_id, -1)
    monkeypatch.setattr(main, "service", service)
    opened = []

    async def open_image(*args):
        opened.append(args)
        return httpx.Response(200, content=b"image", headers={"content-type": "image/jpeg"})

    monkeypatch.setattr(main.source_host, "open_page_image_stream", open_image)
    monkeypatch.setattr(main.source_host, "open_binary_stream", open_image)
    return service, database, vid, candidate_id, opened


def detach(database):
    # Leave origin rows and the hot page-info cache in place, as a split/reset
    # interruption can do; current catalogue ownership is the governing fence.
    with database.catalog() as c:
        c.execute("UPDATE source_matches SET active=0 WHERE source_id='a'")


async def close_response(response):
    await response.upstream.aclose()


@pytest.mark.asyncio
@pytest.mark.parametrize("seam", ["cached-info", "index-write", "cached-map-audit"])
async def test_real_cached_reader_contention_keeps_loop_responsive(env, monkeypatch, seam):
    service, database, vid, candidate_id, _ = await warm_page(
        env, monkeypatch, unresolved=seam == "index-write")
    held = threading.Event()
    stop = threading.Event()

    def hold():
        if seam == "cached-info":
            # The real storage-cutover admission gate delays even hot-cache
            # membership reads. No files are moved in this disposable fixture.
            with database._relocation_condition:
                database._database_relocating = True
            held.set()
            try:
                stop.wait(.20)
            finally:
                with database._relocation_condition:
                    database._database_relocating = False
                    database._relocation_condition.notify_all()
        else:
            try:
                with database.origins() as c:
                    c.execute("BEGIN IMMEDIATE")
                    held.set()
                    stop.wait(.20)
            finally:
                database.close_current_thread()

    holder = threading.Thread(target=hold)
    holder.start()
    assert await asyncio.to_thread(held.wait, 1)
    started = time.perf_counter()
    request = asyncio.create_task(service.resolve_pages(vid) if seam == "cached-map-audit"
                                  else main.proxy_page(candidate_id, 0))
    try:
        await asyncio.sleep(.01)
        tick_delay = time.perf_counter() - started
        assert tick_delay < .10, f"Warm {seam} blocked the loop for {tick_delay:.3f}s"
        assert not request.done(), "Fixture did not exercise the blocked database seam"
    finally:
        stop.set()
        result = await request
        if seam != "cached-map-audit":
            await close_response(result)
        holder.join(1)
        assert not holder.is_alive()


@pytest.mark.asyncio
@pytest.mark.parametrize("seam", ["cached-info", "index-write"])
async def test_cancelled_reader_drains_owned_sqlite_work_before_finishing(env, monkeypatch, seam):
    service, database, _, candidate_id, opened = await warm_page(
        env, monkeypatch, unresolved=seam == "index-write")
    entered, release, finished = threading.Event(), threading.Event(), threading.Event()
    method = "page_fetch_info" if seam == "cached-info" else "set_candidate_chapter_index"
    original = getattr(service, method)

    def delayed(*args):
        entered.set()
        assert release.wait(2), "Cancellation abandoned the owned worker"
        try:
            return original(*args)
        finally:
            finished.set()
            database.close_current_thread()

    monkeypatch.setattr(service, method, delayed)
    request = asyncio.create_task(main.proxy_page(candidate_id, 0))
    try:
        assert await asyncio.to_thread(entered.wait, 1)
        request.cancel()
        await asyncio.sleep(.01)
        request.cancel()
        await asyncio.sleep(.01)
        assert not request.done(), "Request returned while its database thread was live"
        release.set()
        with pytest.raises(asyncio.CancelledError):
            await request
        assert finished.is_set()
        assert not opened, "Cancelled SQL work proceeded to open an image stream"
        if seam == "index-write":
            with database.origins() as c:
                assert c.execute("SELECT source_chapter_index FROM chapter_candidates WHERE id=?",
                                 (candidate_id,)).fetchone()[0] == 0
    finally:
        release.set()
        if not request.done():
            request.cancel()
        await asyncio.gather(request, return_exceptions=True)


@pytest.mark.asyncio
@pytest.mark.parametrize("lookup", ["mapped", "deferred", "unavailable"])
async def test_source_detached_during_index_lookup_cannot_write_or_open_image(env, monkeypatch, lookup):
    service, database, _, candidate_id, opened = await warm_page(env, monkeypatch, unresolved=True)
    entered, release = asyncio.Event(), asyncio.Event()

    async def delayed_index(*args):
        entered.set()
        await release.wait()
        if lookup == "deferred":
            raise SourceWorkDeferred("capacity busy")
        if lookup == "unavailable":
            raise RuntimeError("index endpoint unavailable")
        return 0

    monkeypatch.setattr(main.source_host, "chapter_index_for_id", delayed_index)
    request = asyncio.create_task(main.proxy_page(candidate_id, 0))
    try:
        await entered.wait()
        detach(database)
        release.set()
        with pytest.raises(HTTPException) as caught:
            await request
        assert caught.value.status_code == 404
        assert not opened
        with database.origins() as c:
            assert c.execute("SELECT source_chapter_index FROM chapter_candidates WHERE id=?",
                             (candidate_id,)).fetchone()[0] == -1
    finally:
        release.set()
        await asyncio.gather(request, return_exceptions=True)


class OwnedImageStream(httpx.AsyncByteStream):
    def __init__(self, close_entered=None, close_release=None, body=b"old-work-image"):
        self.close_entered = close_entered
        self.close_release = close_release
        self.body = body
        self.slots = 1
        self.yields = 0
        self.close_calls = 0
        self.close_cancelled = False

    async def __aiter__(self):
        self.yields += 1
        yield self.body

    async def aclose(self):
        self.close_calls += 1
        try:
            if self.close_entered:
                self.close_entered.set()
            if self.close_release:
                await self.close_release.wait()
            self.slots = 0
        except asyncio.CancelledError:
            self.close_cancelled = True
            raise


@pytest.mark.asyncio
@pytest.mark.parametrize("detach_while", ["headers", "publication"])
async def test_detached_response_is_closed_before_any_old_work_bytes_are_published(env, monkeypatch, detach_while):
    service, database, _, candidate_id, _ = await warm_page(env, monkeypatch)
    entered, release = asyncio.Event(), asyncio.Event()
    stream = OwnedImageStream()
    upstream = httpx.Response(200, stream=stream)

    async def delayed_headers(*args):
        entered.set()
        await release.wait()
        return upstream

    monkeypatch.setattr(main.source_host, "open_page_image_stream", delayed_headers)
    if detach_while == "publication":
        original = service.page_fetch_info
        reads = []

        def detach_at_publication(*args):
            reads.append(True)
            if len(reads) == 2:
                detach(database)
            return original(*args)

        monkeypatch.setattr(service, "page_fetch_info", detach_at_publication)
    request = asyncio.create_task(main.proxy_page(candidate_id, 0))
    try:
        await entered.wait()
        if detach_while == "headers":
            detach(database)
        release.set()
        with pytest.raises(HTTPException) as caught:
            await request
        assert caught.value.status_code == 404
        assert upstream.is_closed and stream.slots == 0
        assert stream.yields == 0
    finally:
        release.set()
        await asyncio.gather(request, return_exceptions=True)
        await upstream.aclose()


@pytest.mark.asyncio
async def test_cancellation_at_publication_drains_response_close_and_releases_slot(env, monkeypatch):
    service, database, _, candidate_id, _ = await warm_page(env, monkeypatch)
    close_entered, close_release = asyncio.Event(), asyncio.Event()
    read_entered, read_release = threading.Event(), threading.Event()
    stream = OwnedImageStream(close_entered, close_release)
    upstream = httpx.Response(200, stream=stream)
    monkeypatch.setattr(main.source_host, "open_page_image_stream", AsyncMock(return_value=upstream))
    original = service.page_fetch_info
    calls = []

    def delayed_publication(*args):
        calls.append(True)
        if len(calls) == 2:
            read_entered.set()
            assert read_release.wait(2)
        try:
            return original(*args)
        finally:
            database.close_current_thread()

    monkeypatch.setattr(service, "page_fetch_info", delayed_publication)
    request = asyncio.create_task(main.proxy_page(candidate_id, 0))
    try:
        assert await asyncio.to_thread(read_entered.wait, 1)
        request.cancel()
        await asyncio.sleep(.01)
        assert not request.done()
        read_release.set()
        await asyncio.wait_for(close_entered.wait(), 1)
        request.cancel()
        await asyncio.sleep(.01)
        request.cancel()
        await asyncio.sleep(.01)
        assert not request.done() and stream.slots == 1
        close_release.set()
        with pytest.raises(asyncio.CancelledError):
            await request
        assert upstream.is_closed and stream.slots == 0
        assert stream.yields == 0
    finally:
        read_release.set()
        close_release.set()
        if not request.done():
            request.cancel()
        await asyncio.gather(request, return_exceptions=True)
        await upstream.aclose()


@pytest.mark.asyncio
async def test_inactive_historical_candidate_keeps_current_owner_direct_page_access(env, monkeypatch):
    _, database, _, candidate_id, opened = await warm_page(env, monkeypatch)
    with database.origins() as c:
        c.execute("UPDATE chapter_candidates SET active=0 WHERE id=?", (candidate_id,))
    response = await main.proxy_page(candidate_id, 0)
    try:
        assert response.status_code == 200
        assert opened == [(91001, 0, 0)]
    finally:
        await close_response(response)


@pytest.mark.asyncio
@pytest.mark.parametrize("send_outcome", ["success", "headers-fail", "body-fail"])
async def test_actual_streaming_response_consumes_body_and_closes_on_send_outcome(env, monkeypatch, send_outcome):
    _, _, _, candidate_id, _ = await warm_page(env, monkeypatch)
    stream = OwnedImageStream()
    upstream = httpx.Response(200, stream=stream, headers={
        "content-type": "image/jpeg", "content-length": str(len(b"old-work-image"))})
    monkeypatch.setattr(main.source_host, "open_page_image_stream", AsyncMock(return_value=upstream))
    response = await main.proxy_page(candidate_id, 0)
    messages = []

    async def send(message):
        messages.append(message)
        if ((send_outcome == "headers-fail" and message["type"] == "http.response.start")
                or (send_outcome == "body-fail" and message["type"] == "http.response.body")):
            raise RuntimeError("reader disconnected while sending")

    async def receive():
        await asyncio.Event().wait()

    scope = {"type": "http", "asgi": {"spec_version": "2.4"}}
    if send_outcome == "success":
        await response(scope, receive, send)
        assert b"".join(message.get("body", b"") for message in messages) == b"old-work-image"
        assert messages[-1]["more_body"] is False
    else:
        with pytest.raises(RuntimeError, match="reader disconnected"):
            await response(scope, receive, send)
    assert messages[0]["status"] == 200
    headers = dict(messages[0]["headers"])
    assert headers[b"content-type"] == b"image/jpeg"
    assert headers[b"content-length"] == str(len(b"old-work-image")).encode()
    assert headers[b"cache-control"] == b"private, max-age=3600"
    assert stream.yields == (0 if send_outcome == "headers-fail" else 1)
    assert upstream.is_closed and stream.slots == 0


@pytest.mark.asyncio
@pytest.mark.parametrize("cancel_path", ["repeated-cancel", "disconnect"])
async def test_first_body_owned_httpx_close_is_drained_before_request_finishes(env, monkeypatch, cancel_path):
    _, _, _, candidate_id, _ = await warm_page(env, monkeypatch)
    entered, release, disconnect = asyncio.Event(), asyncio.Event(), asyncio.Event()
    stream = OwnedImageStream(entered, release, body=b"x" * 65536)
    upstream = httpx.Response(200, stream=stream)
    monkeypatch.setattr(main.source_host, "open_page_image_stream", AsyncMock(return_value=upstream))
    response = await main.proxy_page(candidate_id, 0)
    sent = []

    async def send(message):
        sent.append(message)

    async def receive():
        await disconnect.wait()
        return {"type": "http.disconnect"}

    before = set(asyncio.all_tasks())
    request = asyncio.create_task(response({"type": "http", "asgi": {
        "spec_version": "2.0" if cancel_path == "disconnect" else "2.4"}}, receive, send))
    try:
        await asyncio.wait_for(entered.wait(), 1)
        assert upstream.is_closed and stream.slots == 1
        if cancel_path == "repeated-cancel":
            request.cancel()
        else:
            disconnect.set()
        await asyncio.sleep(.01)
        assert not request.done(), "HTTPX marked closed before releasing the owned stream's slot"
        if cancel_path == "repeated-cancel":
            request.cancel()
            await asyncio.sleep(.01)
            request.cancel()
        await asyncio.sleep(.01)
        assert not request.done() and not stream.close_cancelled
        assert stream.close_calls == 1 and stream.slots == 1
        release.set()
        if cancel_path == "repeated-cancel":
            with pytest.raises(asyncio.CancelledError):
                await request
        else:
            await request
        assert stream.close_calls == 1 and stream.slots == 0
        assert b"".join(message.get("body", b"") for message in sent) == b"x" * 65536
        await asyncio.sleep(0)
        assert not [task for task in asyncio.all_tasks() if task not in before and not task.done()]
    finally:
        release.set()
        disconnect.set()
        if not request.done():
            request.cancel()
        await asyncio.gather(request, return_exceptions=True)


@pytest.mark.asyncio
async def test_stream_owner_wrapper_keeps_decoding_and_decoded_response_headers(env, monkeypatch):
    _, _, _, candidate_id, _ = await warm_page(env, monkeypatch)
    decoded = b"decoded-image" * 8000
    compressed = gzip.compress(decoded)
    stream = OwnedImageStream(body=compressed)
    upstream = httpx.Response(200, stream=stream, headers={
        "content-type": "image/jpeg", "content-encoding": "gzip", "content-length": str(len(compressed))})
    monkeypatch.setattr(main.source_host, "open_page_image_stream", AsyncMock(return_value=upstream))
    response = await main.proxy_page(candidate_id, 0)
    messages = []

    async def send(message):
        messages.append(message)

    async def receive():
        await asyncio.Event().wait()

    await response({"type": "http", "asgi": {"spec_version": "2.4"}}, receive, send)
    assert b"".join(message.get("body", b"") for message in messages) == decoded
    headers = dict(messages[0]["headers"])
    assert headers[b"content-type"] == b"image/jpeg"
    assert b"content-length" not in headers and b"content-encoding" not in headers
    assert upstream.num_bytes_downloaded == len(compressed)
    assert upstream.is_closed and stream.slots == 0 and stream.close_calls == 1
