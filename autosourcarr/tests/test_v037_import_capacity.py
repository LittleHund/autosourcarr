"""An import's local admission budget must not become a website timeout."""
import asyncio
from unittest.mock import AsyncMock
import pytest
from app import service as module
from app.config import settings
from app.suwayomi import SuwayomiClient, SourceWorkDeferred, BatchDeadlineExceeded, bounded_map
from test_v086_imports import env


@pytest.mark.asyncio
async def test_saturated_import_admission_defers_without_upstream_or_cooldown(env, monkeypatch):
    s, _, _ = env
    monkeypatch.setattr(settings, 'source_failure_threshold', 1)
    s._import_source_semaphore = asyncio.Semaphore(0)
    work = AsyncMock()
    with pytest.raises(SourceWorkDeferred) as caught:
        await s._run_import_source_request(work, timeout=.01, automatic=True)
    work.assert_not_called()
    assert caught.value.diagnostic_context['upstream_started'] is False
    s._record_source_result('one', caught.value)
    assert not s._source_in_cooldown('one')
    before = module.error_registry.snapshot()['history_count']
    assert s._import_source_failure({}, 'one', caught.value)['kind'] == 'deferred'
    assert module.error_registry.snapshot()['history_count'] == before


@pytest.mark.asyncio
async def test_wait_for_client_slot_does_not_consume_execution_budget(env):
    s, _, _ = env
    client = SuwayomiClient('http://source.invalid', work_limit=1)
    total, _ = client._work_semaphores()
    await total.acquire()
    called = []
    async def work():
        called.append(True)
        return 'done'
    async def release():
        await asyncio.sleep(.05)
        total.release()
    released = asyncio.create_task(release())
    try:
        result = await s._run_import_source_request(lambda: client._run_source_work(work), timeout=.01, automatic=True)
        assert result == 'done' and called == [True]
    finally:
        await released
        await client.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('aggregate', [False, True])
async def test_execution_timeout_vs_collective_reference_budget(env, monkeypatch, aggregate):
    s, _, _ = env
    monkeypatch.setattr(settings, 'source_failure_threshold', 1)
    s._import_source_semaphore = asyncio.Semaphore(1)
    async def stuck():
        await asyncio.Event().wait()
    async def fetch(_):
        return await s._run_import_source_request(stuck, timeout=.01, automatic=True, aggregate=aggregate)
    results = await bounded_map(['one'], fetch, limit=1, per_item_timeout=None)
    error = results[0][2]
    assert isinstance(error, TimeoutError)
    assert error.diagnostic_context['phase'] == ('reference_budget' if aggregate else 'source_execution')
    s._record_source_result('one', error)
    assert s._source_in_cooldown('one') == (not aggregate)
    assert not s._import_source_semaphore.locked()


@pytest.mark.asyncio
async def test_cancelled_import_releases_admission_and_manual_request_bypasses_it(env):
    s, _, _ = env
    s._import_source_semaphore = asyncio.Semaphore(1)
    started = asyncio.Event()
    async def stuck():
        started.set()
        await asyncio.Event().wait()
    task = asyncio.create_task(s._run_import_source_request(stuck, timeout=10, automatic=True))
    await started.wait()
    assert s._import_source_semaphore.locked()
    assert await s._run_import_source_request(AsyncMock(return_value='manual'), timeout=1, automatic=False) == 'manual'
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task
    assert not s._import_source_semaphore.locked()


@pytest.mark.asyncio
async def test_slow_local_verification_preflight_is_not_a_website_timeout(env, monkeypatch):
    from types import SimpleNamespace
    from app.suwayomi import source_work_budget
    s, _, _ = env
    client = SuwayomiClient('http://source.invalid', work_limit=1)
    async def busy(*args):
        await asyncio.Event().wait()
    client.attention = SimpleNamespace(context=busy, check=AsyncMock(), failed=AsyncMock())
    work = AsyncMock()
    try:
        with pytest.raises(SourceWorkDeferred) as caught:
            async with source_work_budget(.01, admission_timeout=.02):
                await client._run_source_work(work, operation={'kind': 'browse'})
        assert caught.value.diagnostic_context['phase'] == 'source_admission'
        assert caught.value.diagnostic_context['upstream_started'] is False
        work.assert_not_called()
        client.attention.failed.assert_not_called()
        assert not client._work_semaphores()[0].locked()
    finally:
        await client.close()
