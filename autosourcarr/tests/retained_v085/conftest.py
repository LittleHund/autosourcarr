"""Offline regression harness: isolated state and no accidental provider traffic.

HTTPX MockTransport/ASGITransport remain available for contract tests. Real network
transports fail immediately unless AUTOSOURCARR_TEST_ALLOW_NETWORK=1 is explicit.
"""
import asyncio
import os
import tempfile
from pathlib import Path

import httpx
import pytest

# Importing app.db constructs its singleton; never use a developer's real DATA_DIR.
_COLLECTION_DATA = tempfile.TemporaryDirectory(prefix="autosourcarr-test-collection-")
os.environ["DATA_DIR"] = _COLLECTION_DATA.name
os.environ.pop("DATABASE_DIR", None)
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")


@pytest.fixture(autouse=True)
def isolated_runtime(monkeypatch, tmp_path):
    from app.config import settings
    from app.db import CATALOG_SCHEMA, ORIGINS_SCHEMA, db
    from app.cover_cache import cover_cache

    db.close_current_thread()
    runtime_dir = tmp_path / "isolated-runtime"
    runtime_dir.mkdir()
    monkeypatch.setattr(settings, "data_dir", runtime_dir)
    monkeypatch.setattr(settings, "database_dir", None)
    monkeypatch.setattr(settings, "settings_config_dir", runtime_dir / "config")
    (runtime_dir / "config").mkdir(exist_ok=True)
    monkeypatch.setattr(cover_cache, "root", tmp_path / "cover-cache")
    cover_cache.root.mkdir(exist_ok=True)
    db._init_db(settings.catalog_path, CATALOG_SCHEMA)
    db._init_db(settings.origins_path, ORIGINS_SCHEMA)
    db._migrate()

    if os.environ.get("AUTOSOURCARR_TEST_ALLOW_NETWORK") != "1":
        async def no_async_network(self, request):
            raise httpx.ConnectError("Network disabled by offline regression harness", request=request)
        def no_network(self, request):
            raise httpx.ConnectError("Network disabled by offline regression harness", request=request)
        monkeypatch.setattr(httpx.AsyncHTTPTransport, "handle_async_request", no_async_network)
        monkeypatch.setattr(httpx.HTTPTransport, "handle_request", no_network)
    yield
    db.close_current_thread()
