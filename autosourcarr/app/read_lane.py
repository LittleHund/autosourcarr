"""A bounded worker lane for local catalogue views, independent of source work."""
from __future__ import annotations

import asyncio
from concurrent.futures import ThreadPoolExecutor
from contextvars import copy_context
from functools import partial
from typing import Any, Callable, TypeVar
from weakref import WeakKeyDictionary

from .async_utils import run_blocking

T = TypeVar("T")


class ReadLane:
    def __init__(self, workers: int = 4) -> None:
        self.workers = max(1, int(workers))
        self._executor: ThreadPoolExecutor | None = None
        self._limits: WeakKeyDictionary = WeakKeyDictionary()

    async def run(self, function: Callable[..., T], /, *args: Any, **kwargs: Any) -> T:
        loop = asyncio.get_running_loop()
        if self._executor is None:
            self._executor = ThreadPoolExecutor(max_workers=self.workers, thread_name_prefix="catalogue-read")
        limit = self._limits.get(loop)
        if limit is None:
            limit = self._limits[loop] = asyncio.Semaphore(self.workers)
        # Waiting readers do not fill an unbounded executor queue. Cancellation
        # before admission submits nothing; admitted work is drained before reset.
        async with limit:
            context = copy_context()
            future = loop.run_in_executor(self._executor, context.run, partial(function, *args, **kwargs))
            try:
                return await asyncio.shield(future)
            except asyncio.CancelledError:
                while not future.done():
                    try:
                        await asyncio.shield(future)
                    except asyncio.CancelledError:
                        continue
                    except Exception:
                        break
                if future.done() and not future.cancelled():
                    future.exception()
                raise

    async def close(self) -> None:
        executor, self._executor = self._executor, None
        try:
            if executor is not None:
                await run_blocking(executor.shutdown, wait=True, cancel_futures=True)
        finally:
            self._limits.clear()


read_lane = ReadLane()


async def catalog_read(function, /, *args, **kwargs):
    """Cached views do not queue behind source/file workers."""
    from .db import db

    def build():
        try:
            return function(*args, **kwargs)
        finally:
            db.close_current_thread()

    return await read_lane.run(build)
