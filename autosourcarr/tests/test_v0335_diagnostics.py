"""Owned startup failures remain observable without changing await semantics."""
import asyncio
import gc
import logging
from types import SimpleNamespace

import pytest

from app.service import AutoSourcarrService
from app.task_registry import TaskRegistry


@pytest.mark.asyncio
async def test_unawaited_startup_failure_is_logged_and_released(caplog):
    owner = SimpleNamespace(_refresh_tasks={})
    loop = asyncio.get_running_loop()
    unhandled = []
    previous = loop.get_exception_handler()
    loop.set_exception_handler(lambda _loop, context: unhandled.append(context))
    async def failing():
        raise OSError('startup fixture failure')
    try:
        with caplog.at_level(logging.ERROR, logger='autosourcarr'):
            AutoSourcarrService.track_startup_task(owner, 'fixture', failing())
            for _ in range(3):
                await asyncio.sleep(0)
            gc.collect()
        assert not owner._refresh_tasks
        assert not unhandled
        assert 'fixture' in caplog.text and 'startup fixture failure' in caplog.text
    finally:
        loop.set_exception_handler(previous)


@pytest.mark.asyncio
async def test_startup_failure_can_still_be_awaited(caplog):
    owner = SimpleNamespace(_refresh_tasks={})
    async def failing():
        raise TimeoutError()
    task = AutoSourcarrService.track_startup_task(owner, 'awaited', failing())
    with pytest.raises(TimeoutError):
        await task
    await asyncio.sleep(0)
    assert not owner._refresh_tasks
    assert 'TimeoutError' in caplog.text


@pytest.mark.asyncio
async def test_cancelled_startup_is_released_without_error(caplog):
    owner = SimpleNamespace(_refresh_tasks={})
    task = AutoSourcarrService.track_startup_task(owner, 'cancelled', asyncio.sleep(60))
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task
    await asyncio.sleep(0)
    assert not owner._refresh_tasks
    assert not caplog.records


@pytest.mark.parametrize('error,expected', [
    (TimeoutError(), 'TimeoutError'), (OSError('disk unavailable'), 'disk unavailable'),
    ('Explicit failure', 'Explicit failure'),
])
def test_failed_task_has_meaningful_error(error, expected):
    registry = TaskRegistry()
    task_id = registry.create('fixture')
    registry.fail(task_id, error)
    row = registry.snapshot()['items'][0]
    assert row['state'] == 'failed' and row['error'] == expected
