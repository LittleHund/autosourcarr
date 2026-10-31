import asyncio
import os
import tempfile
import threading
import time

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ.setdefault("DATA_DIR", _TEST_DATA.name)
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")

from app.db import db
from app.suwayomi import SuwayomiClient, background_source_work


def test_catalog_reads_are_not_serialized_by_python_lock():
    entered = threading.Event()
    release = threading.Event()

    def background_reader():
        with db.catalog() as c:
            c.execute("SELECT 1").fetchone()
            entered.set()
            release.wait(timeout=1.0)

    t = threading.Thread(target=background_reader, daemon=True)
    t.start()
    assert entered.wait(timeout=1.0)
    started = time.monotonic()
    with db.catalog() as c:
        assert c.execute("SELECT 1").fetchone()[0] == 1
    elapsed = time.monotonic() - started
    release.set()
    t.join(timeout=1.0)
    # The old global RLock held this read for the full background wait (~1s).
    assert elapsed < 0.20


def test_background_source_work_reserves_foreground_capacity():
    async def scenario():
        client = SuwayomiClient("http://example.invalid", work_limit=2)
        active_bg = 0
        active_fg = 0
        max_bg = 0
        foreground_started = asyncio.Event()
        release = asyncio.Event()

        async def fake_graphql(query, variables=None):
            nonlocal active_bg, active_fg, max_bg
            if str(query).startswith("bg"):
                active_bg += 1
                max_bg = max(max_bg, active_bg)
                try:
                    await release.wait()
                finally:
                    active_bg -= 1
            else:
                active_fg += 1
                foreground_started.set()
                active_fg -= 1
            return {"ok": True}

        client.graphql = fake_graphql

        async def bg_call(i):
            async with background_source_work():
                return await client._run_source_work(lambda: client.graphql(f"bg-{i}"))

        bg_tasks = [asyncio.create_task(bg_call(i)) for i in range(3)]
        await asyncio.sleep(0.05)
        fg = asyncio.create_task(client._run_source_work(lambda: client.graphql("foreground")))
        # Foreground must start even though several background requests are queued.
        await asyncio.wait_for(foreground_started.wait(), timeout=0.25)
        release.set()
        await asyncio.gather(*bg_tasks, fg)
        await client.close()
        assert max_bg == 1

    asyncio.run(scenario())
