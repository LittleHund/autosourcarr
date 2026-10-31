import asyncio
import threading

from app.cover_cache import CoverCache


def test_malformed_cover_sidecar_is_a_cache_miss(tmp_path):
    cache = CoverCache()
    cache.root = tmp_path
    cache.write("entry-1", b"data", "image/png")
    (tmp_path / "entry-1.json").write_text("[]")
    assert cache.read("entry-1") is None


def test_sync_invalidation_prevents_old_loader_from_recreating_cover(tmp_path):
    async def scenario():
        cache = CoverCache()
        cache.root = tmp_path
        entered, release = asyncio.Event(), asyncio.Event()
        async def loader():
            entered.set()
            await release.wait()
            return b"stale image", "image/png"
        await cache.get("entry-1", loader, wait=0)
        task = cache._tasks["entry-1"]
        await entered.wait()
        cache.clear_keys_sync(["entry-1"])
        release.set()
        assert await task is None
        assert cache.read("entry-1") is None
    asyncio.run(scenario())


def test_failed_covers_have_short_backoff_but_explicit_invalidation_retries(tmp_path):
    async def scenario():
        cache = CoverCache()
        cache.root = tmp_path
        calls = []
        async def loader():
            calls.append(1)
            raise RuntimeError("blocked upstream")
        assert await cache.get("entry-1", loader, wait=1) is None
        assert await cache.get("entry-1", loader, wait=1) is None
        assert calls == [1]
        await cache.clear_keys(["entry-1"])
        assert await cache.get("entry-1", loader, wait=1) is None
        assert calls == [1, 1]
    asyncio.run(scenario())


def test_clear_all_waits_for_thread_writer_without_freezing_event_loop(monkeypatch, tmp_path):
    async def scenario():
        cache = CoverCache()
        cache.root = tmp_path
        entered, release = threading.Event(), threading.Event()
        original = cache.write
        def slow_write(*args):
            entered.set()
            assert release.wait(3)
            original(*args)
        monkeypatch.setattr(cache, "write", slow_write)
        async def loader(): return b"cover image", "image/png"
        await cache.get("entry-1", loader, wait=0)
        for _ in range(100):
            if entered.is_set(): break
            await asyncio.sleep(.005)
        assert entered.is_set()
        clear = asyncio.create_task(cache.clear_all())
        try:
            # This timer must run while the writer remains blocked in its OS thread.
            await asyncio.sleep(.02)
            assert not clear.done()
        finally:
            release.set()
        await asyncio.wait_for(clear, 2)
        assert cache.read("entry-1") is None
        assert cache._tasks == {}
        assert cache._clearing is False
    asyncio.run(scenario())


def test_cover_writes_publish_complete_json_without_temp_files(tmp_path):
    cache = CoverCache()
    cache.root = tmp_path
    asyncio.run(cache.write_async("entry-1", b"image", "image/webp"))
    assert cache.read("entry-1") == (b"image", "image/webp")
    assert not list(tmp_path.glob("*.tmp"))
