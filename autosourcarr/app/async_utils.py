"""Cancellation-safe offloading for database/file mutation workers."""
import asyncio
from collections.abc import Callable, Awaitable, Iterable
from typing import Any, TypeVar

T = TypeVar("T")

async def run_blocking(func: Callable[..., T], /, *args: Any, **kwargs: Any) -> T:
    """Offload work, but never report cancellation while its thread is still writing.

    Python cannot forcibly stop a running thread. Callers set cooperative stop flags
    where available; this helper drains even repeated cancellation before returning.
    """
    task = asyncio.create_task(asyncio.to_thread(func, *args, **kwargs))
    try:
        return await asyncio.shield(task)
    except asyncio.CancelledError:
        while not task.done():
            try:
                await asyncio.shield(task)
            except asyncio.CancelledError:
                continue
            except Exception:
                break
        # Retrieve a late exception so it does not become an unhandled task error;
        # the caller's cancellation remains the primary outcome.
        if task.done() and not task.cancelled():
            task.exception()
        raise


async def gather_owned(*awaitables, return_exceptions: bool = False):
    """Join child work and drain every child before a parent releases its lane.

    Ordinary gather propagates a child exception without stopping its siblings.
    These workers own mutable import evidence/DB writes, so that is not safe here.
    """
    tasks = []
    try:
        for work in awaitables:
            tasks.append(asyncio.ensure_future(work))
        return await asyncio.gather(*tasks, return_exceptions=return_exceptions)
    finally:
        for task in tasks:
            if not task.done():
                task.cancel()
        drain = asyncio.gather(*tasks, return_exceptions=True)
        cancelled_again = False
        while not drain.done():
            try:
                await asyncio.shield(drain)
            except asyncio.CancelledError:
                cancelled_again = True
        drain.result()  # consume any late child exception
        if cancelled_again:
            raise asyncio.CancelledError()


async def gather_partial(awaitables: Iterable[Awaitable[T]], *, timeout: float | None = None) -> list[T | Exception]:
    """Collect in input order, retaining successes when another job hits the deadline.

    The caller owns every child: on timeout *or caller cancellation*, cancel and
    join unfinished children before returning. Unlike wait_for(gather(...)), one
    slow provider does not erase already completed provider results.
    """
    tasks = []
    try:
        for work in awaitables:
            tasks.append(asyncio.ensure_future(work))
        if not tasks:
            return []
        done, pending = await asyncio.wait(tasks, timeout=timeout)
        results: list[T | Exception] = []
        for task in tasks:
            if task in pending:
                results.append(TimeoutError(f"batch deadline reached after {timeout:g}s"))
            elif task.cancelled():
                # A child explicitly cancelled itself; do not present cancellation
                # as a successful empty search to callers.
                raise asyncio.CancelledError()
            else:
                error = task.exception()
                if error is not None:
                    if not isinstance(error, Exception):
                        raise error
                    results.append(error)
                else:
                    results.append(task.result())
        return results
    finally:
        for task in tasks:
            if not task.done():
                task.cancel()
        drain = asyncio.gather(*tasks, return_exceptions=True)
        cancelled_during_cleanup = False
        while not drain.done():
            try:
                await asyncio.shield(drain)
            except asyncio.CancelledError:
                cancelled_during_cleanup = True
        drain.result()
        if cancelled_during_cleanup:
            raise asyncio.CancelledError()


# Both audited branches used this same cancellation-safe mutation contract.
run_sync = run_blocking
