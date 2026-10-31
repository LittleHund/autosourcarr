"""Chapter storage contention must not block unrelated HTTP/event-loop work."""
import asyncio
import threading
import time
from unittest.mock import AsyncMock

import pytest

from app import service as module
from test_v087_recovery import env
from test_v091_pages import chapter_fixture


def test_served_page_storage_does_not_block_event_loop(env, monkeypatch):
    service, _, chapter_id = chapter_fixture(env, monkeypatch)
    monkeypatch.setattr(module.source_host, "fetch_chapter_pages", AsyncMock(return_value=["https://images.invalid/1"]))
    original = service._record_served_candidate
    entered = threading.Event()

    def slow_record(*args):
        entered.set()
        time.sleep(0.20)  # Represents a busy retained SQLite writer.
        return original(*args)

    monkeypatch.setattr(service, "_record_served_candidate", slow_record)

    async def scenario():
        request = asyncio.create_task(service.resolve_pages(chapter_id))
        while not entered.is_set():
            await asyncio.sleep(0.001)
        assert not request.done(), "chapter storage blocked the HTTP event loop"
        started = time.perf_counter()
        await asyncio.sleep(0.01)
        assert time.perf_counter() - started < 0.10
        assert (await request)["pages"]

    asyncio.run(scenario())


def test_cancelled_page_storage_drains_before_return(env, monkeypatch):
    service, _, chapter_id = chapter_fixture(env, monkeypatch)
    monkeypatch.setattr(module.source_host, "fetch_chapter_pages", AsyncMock(return_value=["https://images.invalid/1"]))
    entered, completed = threading.Event(), threading.Event()
    original = service._record_served_candidate

    def slow_record(*args):
        entered.set()
        time.sleep(0.10)
        try:
            return original(*args)
        finally:
            completed.set()

    monkeypatch.setattr(service, "_record_served_candidate", slow_record)

    async def scenario():
        request = asyncio.create_task(service.resolve_pages(chapter_id))
        while not entered.is_set():
            await asyncio.sleep(0.001)
        request.cancel()
        with pytest.raises(asyncio.CancelledError):
            await request
        assert completed.is_set()
        assert not service._page_resolution_tasks

    asyncio.run(scenario())
