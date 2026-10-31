"""Slow metrics work is coalesced and remains owned after health timeouts."""
import asyncio
import importlib
import threading
import time

import pytest

from app.service import AutoSourcarrService


def test_timed_out_health_polls_share_one_metrics_worker(monkeypatch):
    from app import main
    service = AutoSourcarrService()
    monkeypatch.setattr(main, 'service', service)
    monkeypatch.setattr(main, 'main_suwayomi', None)
    monkeypatch.setattr(service, 'schedule_status_aux_refresh', lambda **kwargs:False)
    async def healthy(): return {'ok':True}
    monkeypatch.setattr(main.source_host, 'health', healthy)
    monkeypatch.setattr(main.translator, 'health', healthy)
    original_wait = asyncio.wait_for
    async def quick_wait(aw, timeout):
        return await original_wait(aw, timeout=.03 if timeout == 4.0 else timeout)
    monkeypatch.setattr(asyncio, 'wait_for', quick_wait)
    release = threading.Event()
    counter_lock = threading.Lock()
    calls = 0
    def metrics():
        nonlocal calls
        with counter_lock: calls += 1
        release.wait(3)
        return {'catalog':{'entries':10}}
    monkeypatch.setattr(service,'status_stats',metrics)
    async def scenario():
        try:
            responses = await asyncio.gather(*(main.health() for _ in range(8)))
            assert all('warning' in row['stats'] for row in responses)
            assert calls == 1, 'Timed-out health requests must not stack filesystem scans'
        finally:
            release.set()
            await service.cancel_background_tasks()
    asyncio.run(scenario())


def test_cancelled_metrics_request_does_not_cancel_shared_work_and_shutdown_drains(monkeypatch):
    service=AutoSourcarrService()
    started,release,finished=threading.Event(),threading.Event(),threading.Event()
    def metrics():
        started.set()
        release.wait(3)
        finished.set()
        return {'catalog':{'entries':10}}
    monkeypatch.setattr(service,'status_stats',metrics)
    async def scenario():
        request=asyncio.create_task(service.status_stats_snapshot())
        while not started.is_set(): await asyncio.sleep(.001)
        request.cancel()
        with pytest.raises(asyncio.CancelledError): await request
        assert service._status_stats_task in service._auxiliary_tasks
        stopping=asyncio.create_task(service.cancel_background_tasks())
        await asyncio.sleep(.01)
        assert not stopping.done(), 'Shutdown must wait for the actual filesystem worker'
        release.set()
        await asyncio.wait_for(stopping,1)
        assert finished.is_set() and not service._auxiliary_tasks
    try: asyncio.run(scenario())
    finally: release.set()


def test_status_snapshot_serves_fresh_and_stale_metrics_without_redundant_work(monkeypatch):
    service=AutoSourcarrService()
    release=threading.Event()
    calls=0
    def metrics():
        nonlocal calls
        calls+=1
        if calls>1: release.wait(3)
        return {'catalog':{'entries':calls}}
    monkeypatch.setattr(service,'status_stats',metrics)
    async def scenario():
        first=await service.status_stats_snapshot()
        assert first['catalog']['entries']==1
        again=await service.status_stats_snapshot()
        assert again['catalog']['entries']==1 and not again['refreshing'] and calls==1
        service._status_stats_cache=(time.monotonic()-20,service._status_stats_cache[1])
        stale=await asyncio.wait_for(service.status_stats_snapshot(),.1)
        assert stale['catalog']['entries']==1 and stale['refreshing']
        shared_task=service._status_stats_task
        await service.status_stats_snapshot()
        assert service._status_stats_task is shared_task
        release.set()
        await shared_task
        assert calls==2
        assert (await service.status_stats_snapshot())['catalog']['entries']==2
        await service.cancel_background_tasks()
        service.resume_background_tasks()
        assert service._status_stats_cache is None
    try: asyncio.run(scenario())
    finally: release.set()
