"""Controlled acquisition/cancellation cases; never touch live data or sources."""
import asyncio
import sqlite3
import threading
from types import SimpleNamespace

import pytest

from app.async_utils import gather_owned
from app.read_lane import ReadLane


@pytest.mark.asyncio
async def test_owned_gather_drains_started_child_when_later_acquisition_fails():
    started = asyncio.Event()
    drained = []
    async def child():
        started.set()
        try:
            await asyncio.Event().wait()
        finally:
            await asyncio.sleep(0)
            drained.append(True)
    task = asyncio.create_task(child())
    try:
        await started.wait()
        with pytest.raises(TypeError):
            await gather_owned(task, None)
        assert task.done() and drained == [True]
    finally:
        if not task.done():
            task.cancel()
        await asyncio.gather(task, return_exceptions=True)


@pytest.mark.asyncio
async def test_cancelled_lane_shutdown_waits_for_worker_and_can_be_reused(monkeypatch):
    loop = asyncio.get_running_loop()
    lane = ReadLane(workers=1)
    started, shutting, drained = asyncio.Event(), asyncio.Event(), asyncio.Event()
    release = threading.Event()
    def worker():
        loop.call_soon_threadsafe(started.set)
        assert release.wait(3)
    operation = asyncio.create_task(lane.run(worker))
    closer = None
    try:
        await asyncio.wait_for(started.wait(), 2)
        shutdown = lane._executor.shutdown
        def observed_shutdown(*args, **kwargs):
            loop.call_soon_threadsafe(shutting.set)
            try:
                return shutdown(*args, **kwargs)
            finally:
                loop.call_soon_threadsafe(drained.set)
        monkeypatch.setattr(lane._executor, 'shutdown', observed_shutdown)
        closer = asyncio.create_task(lane.close())
        await asyncio.wait_for(shutting.wait(), 2)
        closer.cancel()
        await asyncio.sleep(0)
        await asyncio.sleep(0)
        assert not closer.done(), 'close returned while its owned worker was still blocked'
        closer.cancel()
        await asyncio.sleep(0)
        assert not closer.done()
        release.set()
        await asyncio.wait_for(operation, 2)
        with pytest.raises(asyncio.CancelledError):
            await closer
        assert drained.is_set()
        assert await lane.run(lambda: 'usable again') == 'usable again'
    finally:
        release.set()
        await asyncio.gather(operation, *([closer] if closer else []), return_exceptions=True)
        await asyncio.wait_for(drained.wait(), 2)
        await lane.close()


def test_failed_database_connection_setup_closes_the_acquired_connection(monkeypatch):
    # conftest has already fixed DATA_DIR/DATABASE_DIR/config to this test session.
    import app.db as module
    class TrackedConnection(sqlite3.Connection):
        close_calls = 0
        def close(self):
            self.close_calls += 1
            super().close()
    connection = sqlite3.connect(':memory:', factory=TrackedConnection)
    def failed_setup(conn):
        assert conn is connection
        raise RuntimeError('synthetic configuration failure')
    monkeypatch.setattr(module, 'sqlite3', SimpleNamespace(connect=lambda *a, **k: connection, Row=sqlite3.Row))
    monkeypatch.setattr(module.Database, '_configure_connection', staticmethod(failed_setup))
    try:
        with pytest.raises(RuntimeError, match='synthetic configuration failure'):
            module.Database._connect(':memory:')
        assert connection.close_calls == 1
        with pytest.raises(sqlite3.ProgrammingError):
            connection.execute('SELECT 1')
    finally:
        if not connection.close_calls:
            connection.close()
