"""Search deadlines and cancellation through the real worker/client seams."""
import asyncio
from unittest.mock import AsyncMock

import pytest

from app import service as module
from app.config import settings
from app.suwayomi import bounded_map
from test_v087_recovery import env, entry, manga


def test_manual_alias_search_obeys_overall_budget_and_keeps_completed_hit(env, monkeypatch):
    s, db = env
    eid = entry(s, 'seed', 88801, 'A long unique example for recovery')
    names = ['A long unique example for recovery', 'Second Search Alias', 'Third Search Alias', 'Fourth Search Alias']
    monkeypatch.setattr(s, '_expanded_search_names', AsyncMock(return_value=(names, [
        {'query': n, 'kind': 'native', 'language': ''} for n in names])))
    monkeypatch.setattr(settings, 'manual_search_timeout', 1.0)
    monkeypatch.setattr(settings, 'manual_search_overall_timeout', .06)
    monkeypatch.setattr(settings, 'manual_search_live_query_limit', 4)
    monkeypatch.setattr(settings, 'manual_translate_candidate_limit', 0)
    calls = []

    async def fetch(sid, mode, page, query):
        calls.append(query)
        if query == names[0]:
            return {'mangas': [manga(88802, names[0])], 'hasNextPage': False}
        await asyncio.sleep(.25)
        return {'mangas': [], 'hasNextPage': False}

    monkeypatch.setattr(module.source_host, 'fetch_source_manga', fetch)

    async def run():
        result = await asyncio.wait_for(s.search_source_for_manual(eid, 'target'), .20)
        assert [m['id'] for m in result['mangas']] == [88802]
        assert result['partial'] and result['deferred_count'] > 0
        assert result['live_error_count'] == 0  # local batch cap is not a source failure
        assert names[-1] not in calls
    asyncio.run(run())


def test_bounded_map_repeated_cancel_does_not_interrupt_child_cleanup():
    async def run():
        started, cleaning, release, finished = (asyncio.Event() for _ in range(4))
        interrupted = []
        async def child(_):
            started.set()
            try:
                await asyncio.Future()
            finally:
                cleaning.set()
                try:
                    await release.wait()
                except asyncio.CancelledError:
                    interrupted.append(True)
                    raise
                finally:
                    finished.set()
        parent = asyncio.create_task(bounded_map([1], child))
        await started.wait()
        parent.cancel()
        await cleaning.wait()
        parent.cancel()
        await asyncio.sleep(0)
        await asyncio.sleep(0)
        was_finished = finished.is_set()
        release.set()
        with pytest.raises(asyncio.CancelledError):
            await parent
        assert not was_finished and not interrupted
        assert finished.is_set()
    asyncio.run(run())
