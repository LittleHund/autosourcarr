"""v0.2.85: bounded uploads/decoding, credentials and external links."""
import asyncio
import gzip
import io
from pathlib import Path

import pytest
from fastapi import HTTPException
from PIL import Image
import app.backup_import as backup
import app.image_safety as images
from app.config import settings


def varint(i):
    out = bytearray()
    while i > 127:
        out.append((i & 127) | 128); i >>= 7
    out.append(i)
    return bytes(out)


def minimal_backup():
    title = b"Audit Backup"
    manga = b"\x1a" + varint(len(title)) + title
    return b"\x0a" + varint(len(manga)) + manga


def test_valid_backup_still_decodes():
    result = backup.decode_backup_bytes(gzip.compress(minimal_backup()))
    assert result["manga"][0]["title"] == "Audit Backup"
    assert result["favorite_count"] == 1


def test_compressed_backup_bomb_stops_at_decoded_limit(monkeypatch):
    monkeypatch.setattr(backup, "MAX_DECODED_BYTES", 128)
    with pytest.raises(backup.BackupDecodeError, match="Decoded backup exceeds"):
        backup.decode_backup_bytes(gzip.compress(b"\x0a" * 129))


def test_direct_decoder_checks_transfer_limit(monkeypatch):
    monkeypatch.setattr(backup, "MAX_BACKUP_BYTES", 10)
    with pytest.raises(backup.BackupDecodeError, match="larger than"):
        backup.decode_backup_bytes(b"x" * 11)


def test_truncated_gzip_is_a_clear_backup_error():
    with pytest.raises(backup.BackupDecodeError, match="Invalid gzip backup"):
        backup.decode_backup_bytes(gzip.compress(minimal_backup())[:-4])

@pytest.mark.parametrize("payload", [b"\x00\x01", b"\x80" * 9 + b"\x02"])
def test_malformed_protobuf_is_rejected(payload):
    with pytest.raises(backup.BackupDecodeError):
        backup.decode_backup_bytes(payload)


def test_signed_source_id_varint_remains_supported():
    value, pos = backup._read_varint(b"\xff" * 9 + b"\x01", 0)
    assert value == (1 << 64) - 1 and pos == 10
    assert backup._signed_int64(value) == -1


def test_image_pixel_limit_is_checked_before_large_decode(monkeypatch):
    data = io.BytesIO()
    Image.new("RGB", (16, 16)).save(data, "PNG")
    monkeypatch.setattr(images, "MAX_IMAGE_PIXELS", 255)
    with pytest.raises(ValueError, match="megapixel"):
        with images.open_raster(data.getvalue()):
            pytest.fail("must not expose oversized image")

@pytest.mark.parametrize("content", [b"%!PS-Adobe-3.0 EPSF-3.0\n", b"8BPS\x00\x01" + b"\0" * 60, b"<html>challenge</html>"])
def test_non_raster_formats_never_reach_image_decoders(content):
    with pytest.raises((ValueError, OSError)):
        with images.open_raster(content):
            pytest.fail("unexpected decoder")


def test_normal_raster_dimensions_are_unchanged():
    data = io.BytesIO()
    Image.new("RGB", (128, 256)).save(data, "PNG")
    with images.open_raster(data.getvalue()) as image:
        assert image.size == (128, 256)


class Upload:
    def __init__(self, chunks, headers=None, error=None):
        self.chunks, self.headers, self.error = chunks, headers or {}, error
    async def stream(self):
        for chunk in self.chunks:
            yield chunk
        if self.error:
            raise self.error()

@pytest.mark.parametrize("case", ["header", "stream", "empty", "cancel"])
def test_failed_uploads_do_not_leave_partial_backup_files(monkeypatch, tmp_path, case):
    import app.main as main
    monkeypatch.setattr(settings, "data_dir", tmp_path)
    monkeypatch.setattr(main, "MAX_BACKUP_BYTES", 10)
    requests = {
        "header": Upload([], {"content-length": "11"}),
        "stream": Upload([b"a" * 8, b"b" * 3]),
        "empty": Upload([]),
        "cancel": Upload([b"a"], error=asyncio.CancelledError),
    }
    with pytest.raises(asyncio.CancelledError if case == "cancel" else HTTPException):
        asyncio.run(main.admin_import_mihon_backup(requests[case], "audit.tachibk"))
    assert not list(tmp_path.rglob("*.tachibk"))
    assert not list(tmp_path.rglob("decoded-library.json"))


def test_successful_upload_is_streamed_and_queued_once(monkeypatch, tmp_path):
    import app.main as main
    monkeypatch.setattr(settings, "data_dir", tmp_path)
    calls = []
    def queued(raw, decoded, **kw):
        calls.append(raw.read_bytes())
        return {"queued": True}
    monkeypatch.setattr(main.service, "schedule_backup_import", queued)
    result = asyncio.run(main.admin_import_mihon_backup(Upload([b"one", b"two"]), "../audit.tachibk"))
    assert calls == [b"onetwo"] and result == {"queued": True}
    assert len(list(tmp_path.rglob("audit.tachibk"))) == 1


def test_non_ascii_reader_token_is_rejected_not_server_error(monkeypatch):
    import app.main as main
    monkeypatch.setattr(settings, "autosourcarr_reader_api_token", "ascii-token")
    sent = []
    async def downstream(*args):
        pytest.fail("unauthorized request reached downstream")
    async def send(message):
        sent.append(message)
    scope = {"type": "http", "method": "GET", "path": "/api/reader/v1/live", "headers": [(b"x-autosourcarr-api-key", b"\xff")]}
    asyncio.run(main.ReaderApiAuthMiddleware(downstream)(scope, None, send))
    assert sent[0]["status"] == 401

@pytest.mark.parametrize("host", [b"bad/host", b"bad?host", b"bad#host", b"bad\\host", b"user@host", b"bad host", b"\xff"])
def test_host_header_rejects_url_boundary_poisoning(host):
    import app.main as main
    sent = []
    async def downstream(*args):
        pytest.fail("bad host reached app")
    async def send(message):
        sent.append(message)
    asyncio.run(main.HttpSafetyMiddleware(downstream)(
        {"type": "http", "path": "/", "headers": [(b"host", host)]}, None, send))
    assert sent[0]["status"] == 400


def test_untrusted_asset_responses_are_sandboxed_and_static_ranges_are_ignored():
    import app.main as main
    async def scenario(path):
        observed, sent = [], []
        async def downstream(scope, receive, send):
            observed.extend(scope["headers"])
            await send({"type": "http.response.start", "status": 200, "headers": []})
            await send({"type": "http.response.body", "body": b""})
        async def send(message):
            sent.append(message)
        await main.HttpSafetyMiddleware(downstream)(
            {"type": "http", "path": path, "headers": [(b"host", b"[::1]:8282"), (b"range", b"bytes=0-1,0-1")]}, None, send)
        return observed, dict(sent[0]["headers"])
    headers, response = asyncio.run(scenario("/static/app.js"))
    assert all(key != b"range" for key, _ in headers)
    assert response[b"x-content-type-options"] == b"nosniff"
    _, response = asyncio.run(scenario("/api/reader/v1/pages/1/0"))
    assert b"sandbox" in response[b"content-security-policy"]
