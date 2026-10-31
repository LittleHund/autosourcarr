import asyncio
import threading

import pytest

from app.async_utils import gather_partial, run_sync
from app.suwayomi import bounded_map
from app.service import AutoSourcarrService
from app.task_registry import TaskRegistry


def test_partial_batch_retains_success_in_input_order_and_drains_timeout():
    async def scenario():
        stopped = asyncio.Event()
        async def slow():
            try:
                await asyncio.sleep(10)
            finally:
                stopped.set()
        async def fast():
            return "success"
        values = await gather_partial((slow(), fast()), timeout=0.02)
        assert isinstance(values[0], TimeoutError)
        assert values[1] == "success"
        assert stopped.is_set()
    asyncio.run(scenario())


def test_batch_cancellation_does_not_leave_children_running():
    async def scenario():
        started = asyncio.Event()
        stopped = []
        async def work(i):
            started.set()
            try:
                await asyncio.sleep(10)
            finally:
                stopped.append(i)
        parent = asyncio.create_task(gather_partial(work(i) for i in range(4)))
        await started.wait()
        parent.cancel()
        with pytest.raises(asyncio.CancelledError):
            await parent
        assert sorted(stopped) == [0, 1, 2, 3]
    asyncio.run(scenario())


def test_bounded_map_timeout_without_per_item_deadline_is_not_typeerror():
    async def failure(_):
        raise TimeoutError()
    rows = asyncio.run(bounded_map([1], failure, limit=1))
    assert rows[0][:2] == (1, None)
    assert isinstance(rows[0][2], TimeoutError)
    assert str(rows[0][2]) == "source request timed out"


def test_bounded_map_preserves_order_and_honors_concurrency():
    async def scenario():
        active = peak = 0
        async def work(i):
            nonlocal active, peak
            active += 1
            peak = max(peak, active)
            try:
                await asyncio.sleep((4-i)*0.005)
                return i * 2
            finally:
                active -= 1
        rows = await bounded_map(range(4), work, limit=2)
        assert [x[0] for x in rows] == [0, 1, 2, 3]
        assert [x[1] for x in rows] == [0, 2, 4, 6]
        assert peak == 2
    asyncio.run(scenario())


def test_mutating_thread_is_joined_before_cancellation_finishes():
    async def scenario():
        entered, release, done = threading.Event(), threading.Event(), threading.Event()
        def writer():
            entered.set()
            release.wait(2)
            done.set()
        job = asyncio.create_task(run_sync(writer))
        while not entered.is_set():
            await asyncio.sleep(0.001)
        job.cancel()
        await asyncio.sleep(0.01)
        assert not job.done()
        job.cancel()  # Repeated UI/shutdown cancellation must not detach the writer.
        release.set()
        with pytest.raises(asyncio.CancelledError):
            await job
        assert done.is_set()
    asyncio.run(scenario())


def test_stop_includes_identity_color_and_auxiliary_task_families():
    async def scenario():
        service = AutoSourcarrService()
        stopped = []
        async def worker(name):
            try:
                await asyncio.sleep(10)
            finally:
                stopped.append(name)
        for i, bucket in enumerate(service.background_task_buckets()):
            bucket[i] = asyncio.create_task(worker(i))
        service._track_auxiliary(worker(5))
        await asyncio.sleep(0)
        await service.stop_background_tasks()
        assert sorted(stopped) == list(range(6))
        assert all(not bucket for bucket in service.background_task_buckets())
        assert not service._auxiliary_tasks
        assert service.schedule_entry_identity(1) is False
        assert service.schedule_continuous_catalog_discovery() is False
        service.resume_background_tasks()
        assert not service._background_stopping
    asyncio.run(scenario())


def test_duplicate_pause_same_reason_has_independent_owners():
    service = AutoSourcarrService()
    service.begin_automatic_duplicate_pause("reset")
    service.begin_automatic_duplicate_pause("reset")
    service.end_automatic_duplicate_pause("reset", schedule=False)
    assert service.automatic_duplicate_pause_reasons() == ["reset"]
    assert service._automatic_duplicate_pause_event.is_set()
    service.end_automatic_duplicate_pause("reset", schedule=False)
    assert service.automatic_duplicate_pause_reasons() == []


def test_cancelled_queued_factory_is_never_instantiated():
    async def scenario():
        service = AutoSourcarrService()
        service._background_task_semaphore = asyncio.Semaphore(0)
        called = []
        async def factory():
            called.append(True)
        parent = asyncio.create_task(service._background_task_runtime(factory))
        await asyncio.sleep(0)
        parent.cancel()
        with pytest.raises(asyncio.CancelledError):
            await parent
        assert called == []
    asyncio.run(scenario())


def test_registry_cancellation_before_first_execution_is_terminal():
    async def scenario():
        registry = TaskRegistry()
        async def never_started():
            await asyncio.sleep(20)
        job = asyncio.create_task(never_started())
        tid = registry.create("Queued job")
        registry.bind_runtime(tid, job)
        job.cancel()
        with pytest.raises(asyncio.CancelledError):
            await job
        await asyncio.sleep(0)
        assert not registry.is_running(tid)
    asyncio.run(scenario())
