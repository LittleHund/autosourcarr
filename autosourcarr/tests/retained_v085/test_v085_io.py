import asyncio
import gzip
import io
from pathlib import Path

import httpx
import pytest
from PIL import Image, UnidentifiedImageError
from starlette.requests import Request
from fastapi import HTTPException

from app import backup_import
from app.config import settings
from app.image_safety import open_sample_image
from app.suwayomi import SuwayomiClient, SuwayomiError


class BytesStream(httpx.AsyncByteStream):
    def __init__(self, parts):
        self.parts, self.closed = parts, False
    async def __aiter__(self):
        for part in self.parts:
            yield part
    async def aclose(self):
        self.closed = True


@pytest.mark.parametrize("cursor", [None, "never-advances"])
def test_library_pagination_fails_closed_on_incomplete_snapshot(monkeypatch, cursor):
    async def scenario():
        client = SuwayomiClient("http://source.invalid")
        async def graphql(*args, **kwargs):
            return {"mangas": {"nodes": [{"id": 1}], "pageInfo": {"hasNextPage": True, "endCursor": cursor}}}
        monkeypatch.setattr(client, "graphql", graphql)
        try:
            with pytest.raises(SuwayomiError, match="refusing an incomplete snapshot"):
                await client.library_mangas()
        finally:
            await client.close()
    asyncio.run(scenario())


def test_library_pagination_collects_all_pages(monkeypatch):
    async def scenario():
        client = SuwayomiClient("http://source.invalid")
        cursors = []
        async def graphql(query, variables):
            cursors.append(variables["after"])
            first = len(cursors) == 1
            return {"mangas": {"nodes": [{"id": len(cursors)}], "pageInfo": {"hasNextPage": first, "endCursor": "next" if first else None}}}
        monkeypatch.setattr(client, "graphql", graphql)
        try:
            assert await client.library_mangas() == [{"id": 1}, {"id": 2}]
            assert cursors == [None, "next"]
        finally:
            await client.close()
    asyncio.run(scenario())


@pytest.mark.parametrize("headers,parts", [({}, [b"abcd", b"efghi"]), ({"content-length": "9"}, [b"123456789"]),
    ({"content-encoding": "gzip"}, [gzip.compress(b"x" * 64)])])
def test_limited_binary_download_rejects_oversize_and_closes_stream(headers, parts):
    async def scenario():
        client = SuwayomiClient("http://source.invalid")
        await client.client.aclose()
        stream = BytesStream(parts)
        client.client = httpx.AsyncClient(transport=httpx.MockTransport(lambda request: httpx.Response(200, headers=headers, stream=stream)))
        try:
            with pytest.raises(SuwayomiError, match="exceeds"):
                await client._fetch_limited_binary("/image", max_bytes=8)
            assert stream.closed
        finally:
            await client.close()
    asyncio.run(scenario())


def test_stream_timeout_override_is_applied():
    async def scenario():
        client = SuwayomiClient("http://source.invalid")
        await client.client.aclose()
        seen = []
        def respond(request):
            seen.append(request.extensions["timeout"])
            return httpx.Response(200, content=b"abc")
        client.client = httpx.AsyncClient(transport=httpx.MockTransport(respond))
        try:
            response = await client.open_binary_stream("/image", timeout=2.75)
            await response.aclose()
            assert all(value == 2.75 for value in seen[0].values())
        finally:
            await client.close()
    asyncio.run(scenario())


def test_proxy_closes_upstream_when_downstream_header_send_fails(monkeypatch):
    from app import main
    async def scenario():
        stream = BytesStream([b"unused"])
        upstream = httpx.Response(200, stream=stream)
        monkeypatch.setattr(main.service, "page_fetch_info", lambda *args: {"source_chapter_index": 0, "source_manga_id": 1})
        async def opened(*args): return upstream
        monkeypatch.setattr(main.source_host, "open_page_image_stream", opened)
        response = await main.proxy_page(1, 0)
        async def receive(): return {"type": "http.disconnect"}
        async def send(message): raise OSError("downstream disconnected")
        with pytest.raises(Exception):
            await response({"type": "http", "asgi": {"spec_version": "2.4"}, "method": "GET"}, receive, send)
        assert upstream.is_closed and stream.closed
    asyncio.run(scenario())


def test_proxy_does_not_forward_compressed_content_length(monkeypatch):
    from app import main
    async def scenario():
        body = b"page data" * 100
        upstream = httpx.Response(200, headers={"content-encoding": "gzip", "content-length": str(len(gzip.compress(body)))},
                                 stream=BytesStream([gzip.compress(body)]))
        monkeypatch.setattr(main.service, "page_fetch_info", lambda *args: {"source_chapter_index": 0, "source_manga_id": 1})
        async def opened(*args): return upstream
        monkeypatch.setattr(main.source_host, "open_page_image_stream", opened)
        response = await main.proxy_page(1, 0)
        assert "content-length" not in response.headers
        actual = b"".join([part async for part in response.body_iterator])
        assert actual == body
        assert upstream.is_closed
    asyncio.run(scenario())


def upload_request(parts, headers=None):
    data = iter(parts)
    consumed = []
    async def receive():
        try:
            part = next(data)
        except StopIteration:
            return {"type": "http.request", "body": b"", "more_body": False}
        consumed.append(part)
        return {"type": "http.request", "body": part, "more_body": True}
    scope = {"type": "http", "method": "POST", "path": "/api/admin/import/mihon-backup", "headers": headers or []}
    return Request(scope, receive), consumed


def test_upload_limit_applies_while_streaming_and_removes_partial_file(monkeypatch):
    from app import main
    monkeypatch.setattr(main, "MAX_BACKUP_BYTES", 8)
    request, consumed = upload_request([b"1234", b"56789", b"never consumed"])
    with pytest.raises(HTTPException) as exc:
        asyncio.run(main.admin_import_mihon_backup(request, "library.tachibk"))
    assert exc.value.status_code == 413
    assert consumed == [b"1234", b"56789"]
    assert not list((settings.data_dir / "imports").glob("*"))


def test_declared_oversize_upload_is_rejected_before_body_read(monkeypatch):
    from app import main
    monkeypatch.setattr(main, "MAX_BACKUP_BYTES", 8)
    request, consumed = upload_request([b"never consumed"], [(b"content-length", b"9")])
    with pytest.raises(HTTPException) as exc:
        asyncio.run(main.admin_import_mihon_backup(request, "library.tachibk"))
    assert exc.value.status_code == 413 and not consumed


def test_upload_cleanup_when_scheduler_refuses_queue(monkeypatch):
    from app import main
    seen = []
    def schedule(raw, readable, **kwargs):
        seen.append(raw.read_bytes())
        return {"queued": False, "error": "stopping"}
    monkeypatch.setattr(main.service, "schedule_backup_import", schedule)
    request, _ = upload_request([b"abc", b"def"])
    result = asyncio.run(main.admin_import_mihon_backup(request, "../../library.tachibk"))
    assert result["queued"] is False
    assert seen == [b"abcdef"]
    assert not list((settings.data_dir / "imports").glob("*"))


def test_gzip_backup_expansion_is_bounded(monkeypatch):
    monkeypatch.setattr(backup_import, "MAX_DECODED_BACKUP_BYTES", 64)
    with pytest.raises(backup_import.BackupDecodeError, match="decompressed|decoded|larger|exceeds"):
        backup_import.decode_backup_bytes(gzip.compress(b"x" * 200))


@pytest.mark.parametrize("data", [b"\x00\x00", b"\x80" * 9 + b"\x02"])
def test_invalid_protobuf_field_or_varint_is_rejected(data):
    with pytest.raises(backup_import.BackupDecodeError):
        backup_import.decode_backup_bytes(data)


def sample_image(fmt="PNG"):
    output = io.BytesIO()
    Image.new("RGB", (100, 100)).save(output, format=fmt)
    return output.getvalue()


def test_image_pixel_bound_applies_before_full_decode():
    with pytest.raises(ValueError, match="decoded pixels"):
        with open_sample_image(sample_image(), max_pixels=9999):
            pytest.fail("oversized sample should be rejected")


def test_non_raster_sample_formats_are_not_opened():
    with pytest.raises(UnidentifiedImageError):
        with open_sample_image(sample_image("TIFF")):
            pytest.fail("unsupported parser should not be called")


def test_regular_raster_image_remains_supported():
    with open_sample_image(sample_image()) as image:
        assert image.size == (100, 100)
        assert image.convert("RGBA").size == (100, 100)
