"""Hermetic regression tests: never open a user's database or contact live sources.

MockTransport and ASGITransport remain usable for realistic HTTP/client tests.
Individual tests may override the real-transport guard explicitly when needed.
"""
from __future__ import annotations

import os
import tempfile

import httpx
import pytest

# This must happen before test-module collection imports app.config/app.db.
_TEST_ROOT = tempfile.TemporaryDirectory(prefix="autosourcarr-tests-")
os.environ["DATA_DIR"] = _TEST_ROOT.name
os.environ["DATABASE_DIR"] = _TEST_ROOT.name
os.environ["SETTINGS_CONFIG_DIR"] = _TEST_ROOT.name + "/config"
os.environ["MAIN_SUWAYOMI_URL"] = ""
os.environ["TRANSLATION_API_URL"] = ""
os.environ["SOURCE_ENGINE_MODE"] = "external"  # Hermetic fixtures explicitly choose their mock engine.
os.environ["SOURCE_SUWAYOMI_URL"] = "http://source-host.invalid:4567"
# Capture the safe paths before historical modules adjust their own environment.
from app.config import settings  # noqa: E402


def pytest_sessionfinish(session, exitstatus):
    # Windows cannot remove the session's disposable files while the main-thread
    # SQLite handles are still open. Do not import a database for tests that never
    # used one, and never touch any path outside this hermetic test session.
    import sys
    module = sys.modules.get('app.db')
    if module is not None:
        module.db.close_current_thread()


@pytest.fixture(autouse=True)
def no_live_network(monkeypatch):
    async def blocked_async(self, request):
        raise httpx.ConnectError("Live network is disabled in regression tests", request=request)

    def blocked_sync(self, request):
        raise httpx.ConnectError("Live network is disabled in regression tests", request=request)

    monkeypatch.setattr(httpx.AsyncHTTPTransport, "handle_async_request", blocked_async)
    monkeypatch.setattr(httpx.HTTPTransport, "handle_request", blocked_sync)
