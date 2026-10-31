"""Persistent source-host repositories and bounded, read-only manifest previews."""
from __future__ import annotations

import asyncio
from concurrent.futures import ThreadPoolExecutor
import threading
import hashlib
import http.client
import ipaddress
import json
import socket
import time
import zlib
from pathlib import Path
from urllib.parse import urljoin, urlsplit, urlunsplit

from .env_settings import ENV_WRITE_LOCK, parse_env, upsert_env_value

KEIYOUSHI_URL = "https://github.com/keiyoushi/extensions/raw/repo/index.pb"
MAX_MANIFEST_BYTES = 3 * 1024 * 1024
_PREVIEW_EXECUTOR = ThreadPoolExecutor(max_workers=2, thread_name_prefix="repository-preview")
_PREVIEW_ADMISSION = threading.BoundedSemaphore(2)


class RepositoryConflict(ValueError):
    pass


def normalize_repository_url(value: str) -> str:
    if not isinstance(value, str) or len(value) > 2048 or any(ord(c) < 33 or ord(c) == 127 for c in value):
        raise ValueError("Enter a repository manifest URL without spaces or control characters")
    p = urlsplit(value)
    if p.scheme != "https" or not p.hostname or p.username or p.password or p.fragment:
        raise ValueError("Use an HTTPS manifest URL without credentials or a fragment")
    if p.port not in (None, 443):
        raise ValueError("Repository previews support the standard HTTPS port")
    return urlunsplit((p.scheme, p.netloc.lower(), p.path or "/", p.query, ""))


def repository_snapshot(path: Path) -> dict:
    with ENV_WRITE_LOCK:
        if not path.is_file():
            raise FileNotFoundError("Private source host settings file is not mounted")
        pairs, errors = parse_env(path)
        if errors:
            raise ValueError("Source host settings need correction: " + "; ".join(errors))
        raw = dict(pairs).get("EXTENSION_STORES", "[]").strip()
        if len(raw) >= 2 and raw[0] == raw[-1] and raw[0] in "'\"":
            raw = raw[1:-1]
        try:
            urls = json.loads(raw)
        except (ValueError, TypeError) as exc:
            raise ValueError("EXTENSION_STORES must be a JSON array of repository URLs") from exc
        if not isinstance(urls, list) or any(not isinstance(url, str) for url in urls):
            raise ValueError("EXTENSION_STORES must be a JSON array of repository URLs")
        return {"revision": hashlib.sha256(path.read_bytes()).hexdigest(), "repositories": [
            {"url": url, "name": "Keiyoushi" if urlsplit(url).hostname in {"github.com", "raw.githubusercontent.com"} and "/keiyoushi/" in urlsplit(url).path.lower() else "Extension repository", "state": "Saved configuration"}
            for url in dict.fromkeys(urls)
        ]}


def change_repository(path: Path, url: str, revision: str, *, remove: bool = False) -> dict:
    # Delete uses the exact persisted value, so even a legacy HTTP URL can be removed.
    if not remove:
        url = normalize_repository_url(url)
    with ENV_WRITE_LOCK:
        before = repository_snapshot(path)
        if not revision or before["revision"] != revision:
            raise RepositoryConflict("Repository configuration changed. Refresh before saving")
        urls = [r["url"] for r in before["repositories"]]
        if remove:
            urls = [item for item in urls if item != url]
        elif url not in urls:
            urls.append(url)
        upsert_env_value(path, "EXTENSION_STORES", json.dumps(urls, ensure_ascii=True, separators=(",", ":")))
        return {**repository_snapshot(path), "restart_required": True}


def _public_addresses(host: str, port: int) -> list[tuple]:
    addresses = socket.getaddrinfo(host, port, type=socket.SOCK_STREAM)
    if not addresses or any(not ipaddress.ip_address(a[4][0]).is_global for a in addresses):
        raise ValueError("Repository previews require a public HTTPS address")
    return addresses


def fetch_manifest(url: str, *, deadline: float | None = None, max_bytes: int = MAX_MANIFEST_BYTES, accept: str | None = None) -> tuple[bytes, str]:
    """Pin the validated public address while retaining TLS hostname verification.

    No application cookies, proxy environment, source-host credentials or redirects
    to private addresses are used. The decoded response and total time are bounded.
    """
    if type(max_bytes) is not int or not 1 <= max_bytes <= 128 * 1024 * 1024:
        raise ValueError('Public source download budget must be between1 byte and128MiB')
    if accept is not None and (not isinstance(accept,str) or accept!='application/vnd.github.sha'):raise ValueError('Unsupported public source response type')
    deadline = deadline or time.monotonic() + 8.0
    for _ in range(4):
        url = normalize_repository_url(url)
        p = urlsplit(url)
        addresses = _public_addresses(p.hostname, 443)
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("Repository preview exceeded its time budget")
        connection = http.client.HTTPSConnection(p.hostname, timeout=min(remaining, 4))
        # HTTPSConnection wraps this pinned socket with SNI and validates the
        # original hostname; DNS cannot change between validation and connection.
        def connect_pinned(_address, timeout=4, source_address=None):
            last_error = None
            for family, kind, protocol, _, address in addresses:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("Repository preview exceeded its time budget")
                sock = socket.socket(family, kind, protocol)
                try:
                    sock.settimeout(min(float(timeout), remaining))
                    sock.connect(address)
                    return sock
                except OSError as exc:
                    sock.close()
                    last_error = exc
            raise last_error or OSError("No public repository address could be connected")
        connection._create_connection = connect_pinned
        socket_ref = []
        def stop_network():
            sock = socket_ref[0] if socket_ref else connection.sock
            if sock:
                try:
                    sock.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                sock.close()
        deadline_timer = threading.Timer(max(.01, deadline - time.monotonic()), stop_network)
        deadline_timer.daemon = True
        deadline_timer.start()
        try:
            headers={"Accept-Encoding":"identity","User-Agent":"AutoSourcarr-repository-preview"}
            if accept:headers['Accept']=accept
            connection.request("GET", urlunsplit(("", "", p.path or "/", p.query, "")), headers=headers)
            if connection.sock:
                socket_ref.append(connection.sock)
            response = connection.getresponse()
            if response.status in {301, 302, 303, 307, 308}:
                location = response.getheader("Location")
                if not location:
                    raise ValueError("Repository redirect has no destination")
                url = urljoin(url, location)
                continue
            if response.status != 200:
                raise ValueError(f"Repository returned HTTP {response.status}")
            if response.getheader("Content-Encoding", "identity").lower() not in {"", "identity"}:
                raise ValueError("Repository returned unsupported compressed content")
            if int(response.getheader("Content-Length", "0")) > max_bytes:
                raise ValueError(f"Repository response exceeds its {max_bytes}-byte limit")
            chunks, size = [], 0
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("Repository preview exceeded its time budget")
                sock = socket_ref[0] if socket_ref else connection.sock
                if sock:
                    sock.settimeout(min(remaining, 2))
                chunk = response.read1(min(65536, max_bytes + 1 - size))
                if not chunk:
                    break
                size += len(chunk)
                if size > max_bytes:
                    raise ValueError(f"Repository response exceeds its {max_bytes}-byte limit")
                chunks.append(chunk)
            return b"".join(chunks), url
        finally:
            deadline_timer.cancel()
            connection.close()
    raise ValueError("Repository has too many redirects")


def _protobuf_fields(data: bytes) -> list[tuple[int, object]]:
    fields = []
    offset = 0
    def varint():
        nonlocal offset
        result = 0
        for shift in range(0, 70, 7):
            if offset >= len(data):
                raise ValueError("Incomplete protobuf manifest")
            byte = data[offset]; offset += 1
            result |= (byte & 127) << shift
            if not byte & 128:
                return result
        raise ValueError("Invalid protobuf integer")
    while offset < len(data):
        key = varint(); field, wire = key >> 3, key & 7
        if not field:
            raise ValueError("Invalid protobuf field")
        if wire == 0:
            value = varint()
        elif wire in {1, 2, 5}:
            length = varint() if wire == 2 else 8 if wire == 1 else 4
            if offset + length > len(data):
                raise ValueError("Truncated protobuf manifest")
            value = data[offset:offset + length]; offset += length
        else:
            raise ValueError("Unsupported protobuf wire type")
        fields.append((field, value))
        if len(fields) > 100000:
            raise ValueError("Repository manifest contains too many fields")
    return fields


def parse_manifest(body: bytes) -> dict:
    if not body or len(body) > MAX_MANIFEST_BYTES:
        raise ValueError("Repository manifest is empty or too large")
    # Suwayomi repository files (including Keiyoushi index.pb) may themselves
    # be gzipped even when HTTP Content-Encoding is identity. Bound expansion.
    if body.startswith(b"\x1f\x8b"):
        try:
            decoder = zlib.decompressobj(16 + zlib.MAX_WBITS)
            expanded = decoder.decompress(body, MAX_MANIFEST_BYTES + 1)
        except zlib.error as exc:
            raise ValueError("Repository gzip manifest could not be decoded") from exc
        if len(expanded) > MAX_MANIFEST_BYTES or decoder.unconsumed_tail:
            raise ValueError("Expanded repository manifest exceeds 3 MiB")
        if not decoder.eof or decoder.unused_data:
            raise ValueError("Repository gzip manifest is truncated or has trailing data")
        body = expanded
    if body.lstrip().startswith((b"{", b"[")):
        data = json.loads(body)
        if not isinstance(data, dict):
            raise ValueError("Use the repository manifest (repo.json or index.pb), not an extension list")
        if isinstance(data.get("meta"), dict) and data["meta"].get("name"):
            return {"name": str(data["meta"]["name"])[:160], "format": "Legacy Mihon repository", "extension_count": None}
        if not isinstance(data.get("name"), str) or not any(k in data for k in ("signingKey", "extensionList", "extensionListUrl")):
            raise ValueError("This JSON is not a recognized extension repository")
        entries = data.get("extensionList", {}).get("extensions") if isinstance(data.get("extensionList"), dict) else None
        return {"name": data["name"][:160], "format": "Suwayomi repository JSON", "extension_count": len(entries) if isinstance(entries, list) else None}
    fields = _protobuf_fields(body)
    name = next((v for k, v in fields if k == 1 and isinstance(v, bytes)), b"").decode("utf-8")
    if not name.strip() or len(name) > 160 or not any(k in {3, 101, 102} for k, _ in fields):
        raise ValueError("This file is not a recognized extension repository manifest")
    embedded = next((v for k, v in fields if k == 101 and isinstance(v, bytes)), None)
    count = sum(k == 1 for k, _ in _protobuf_fields(embedded)) if embedded is not None else None
    return {"name": name, "format": "Suwayomi repository protobuf", "extension_count": count}


def preview_repository(url: str) -> dict:
    normalized = normalize_repository_url(url)
    try:
        body, resolved = fetch_manifest(normalized)
    except http.client.HTTPException as exc:
        raise ValueError("Repository returned an invalid HTTP response") from exc
    try:
        data = parse_manifest(body)
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise ValueError("Repository manifest could not be decoded") from exc
    from bundled.source_tools.discovery import declared_source
    try:source=declared_source(normalized,fetch_manifest,deadline=time.monotonic()+5)
    except (ValueError,OSError,http.client.HTTPException):source=None
    return {**data, "url": normalized, "resolved_url": resolved,
            'source_repository':source['repository'] if source else None,
            'source_code_status':'Declared by this repository; installed-code compatibility is checked separately.' if source else 'No unambiguous source-code declaration found. Original project input may be required.'}


async def preview_repository_async(url: str, *, timeout: float = 10.0) -> dict:
    """Keep DNS/network previews out of the application's shared worker pool.

    A caller's deadline/cancellation does not release admission until the actual
    worker finishes. Even an OS DNS stall can occupy at most these two workers.
    """
    if not _PREVIEW_ADMISSION.acquire(blocking=False):
        raise ValueError("Two repository previews are still running. Retry shortly")
    loop = asyncio.get_running_loop()
    try:
        future = loop.run_in_executor(_PREVIEW_EXECUTOR, preview_repository, url)
    except BaseException:
        _PREVIEW_ADMISSION.release()
        raise
    def finished(done):
        _PREVIEW_ADMISSION.release()
        if not done.cancelled():
            done.exception()  # consume a late failure after caller timeout
    future.add_done_callback(finished)
    try:
        return await asyncio.wait_for(asyncio.shield(future), timeout=timeout)
    except asyncio.TimeoutError as exc:
        raise TimeoutError("Repository preview timed out. No configuration was changed") from exc
