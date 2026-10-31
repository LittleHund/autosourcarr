"""Explicit per-entry actions must progress independently of automatic scheduling."""
import asyncio
import os
import tempfile
from unittest.mock import AsyncMock

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ["DATA_DIR"] = _TEST_DATA.name
os.environ["DATABASE_DIR"] = _TEST_DATA.name

import pytest
from app import main
from app import service as module
from app.config import settings
from app.suwayomi import _BACKGROUND_SOURCE_WORK, background_source_work
from test_v087_recovery import env, source, entry


@pytest.mark.parametrize('kind', ['refresh', 'discovery', 'redo', 'hydration'])
def test_manual_entry_jobs_progress_while_automatic_capacity_is_occupied(env, monkeypatch, kind):
    s, database = env
    source(database, 'one')
    eid = entry(s, 'one', 91001, attach=False)
    async def scenario():
        called = asyncio.Event()
        foreground = []
        async def work(*args, **kwargs):
            foreground.append(not _BACKGROUND_SOURCE_WORK.get())
            called.set()
            return {'entry_id': eid, 'entry_ids': [eid], 'manga': {}}
        monkeypatch.setattr(s, 'refresh_entry', work)
        monkeypatch.setattr(s, 'discover_all_source_records', work)
        monkeypatch.setattr(module.source_host, 'fetch_manga_and_chapters', work)
        monkeypatch.setattr(s, 'prepare_entry_automation_redo', lambda *a, **k: {'entry_id': eid, 'entry_ids': [eid]})
        s._background_task_semaphore = asyncio.Semaphore(0)
        s._background_source_semaphore = asyncio.Semaphore(0)
        try:
            # A scheduler can create an explicit job from an existing task context.
            async with background_source_work():
                if kind == 'refresh':
                    response = s.schedule_entry_refresh_job(eid)
                    key = f'__user-refresh__:{eid}:1:1'
                elif kind == 'discovery':
                    response = s.schedule_entry_discovery_job(eid)
                    key = f'__user-discovery__:{eid}:all'
                elif kind == 'redo':
                    response = s.schedule_entry_automation_redo(eid)
                    key = f'__redo-automation__:{eid}'
                else:
                    response = s.schedule_manual_match_hydration(eid, 'one', 91001)
                    key = '__manual-hydrate__:one:91001'
                runtime = s._refresh_tasks[key]
            await asyncio.wait_for(called.wait(), .2)
            await asyncio.wait_for(runtime, .5)
            assert foreground and all(foreground)
            assert s._background_task_semaphore.locked()
            assert s._background_source_semaphore.locked()
            if isinstance(response, dict):
                task = next(x for x in module.task_registry.snapshot()['items'] if x['id'] == response['task_id'])
                assert task['state'] == 'completed'
        finally:
            await s.cancel_background_tasks()
    asyncio.run(scenario())


def test_direct_manual_discovery_does_not_wait_for_its_own_interactive_request(env, monkeypatch):
    s, database = env
    source(database, 'one')
    eid = entry(s, 'one', 91002, title='The Archive Beyond Tomorrow', attach=False)
    monkeypatch.setattr(main, 'service', s)
    monkeypatch.setattr(settings, 'manual_search_overall_timeout', .05)
    monkeypatch.setattr(settings, 'auto_variant_discovery', False)
    idle_calls = []
    async def wait_idle(*args, **kwargs):
        idle_calls.append(True)
        while s.interactive_request_active():
            await asyncio.sleep(.005)
    monkeypatch.setattr(s, '_wait_for_background_idle', wait_idle)
    fetch = AsyncMock(return_value={'mangas': [], 'hasNextPage': False})
    monkeypatch.setattr(module.source_host, 'fetch_source_manga', fetch)
    async def scenario():
        s.begin_interactive_request()
        try:
            result = await asyncio.wait_for(main.admin_discover_additional(eid, 'one', False), .5)
            assert result['entry_id'] == eid
        finally:
            s.end_interactive_request()
        assert fetch.await_count > 0, 'The manual request waited for its own middleware admission to end'
        assert idle_calls == []
    asyncio.run(scenario())


def test_explicit_discovery_preserves_automatic_search_scope_and_alias_budget(env, monkeypatch):
    s, database = env
    source(database, 'one')
    eid = entry(s, 'one', 91003, attach=False)
    observed = []
    async def discover(*args, **kwargs):
        observed.append(kwargs)
        return {'entry_id': eid, 'added': 0}
    monkeypatch.setattr(s, 'discover_missing_source_matches', discover)
    monkeypatch.setattr(s, 'discover_additional_source_records', AsyncMock(return_value={'entry_id': eid, 'added': 0}))
    async def scenario():
        s.schedule_entry_discovery_job(eid)
        await s._refresh_tasks[f'__user-discovery__:{eid}:all']
        assert observed and observed[0].get('user_requested') is True
        assert not observed[0].get('exhaustive')
        assert not observed[0].get('priority_rebuild')
    asyncio.run(scenario())


def test_manual_jobs_share_bounded_admission_with_manual_import_resolution(env, monkeypatch):
    s, database = env
    source(database, 'one')
    eid = entry(s, 'one', 91004, attach=False)
    async def scenario():
        called = asyncio.Event()
        async def work(*args, **kwargs):
            called.set()
        monkeypatch.setattr(s, 'refresh_entry', work)
        for _ in range(s._import_resolve_capacity):
            await s._import_resolve_semaphore.acquire()
        released = 0
        try:
            s.schedule_entry_refresh_job(eid)
            runtime = s._refresh_tasks[f'__user-refresh__:{eid}:1:1']
            await asyncio.sleep(.02)
            assert not called.is_set(), 'Explicit jobs exceeded the shared bounded manual capacity'
            s._import_resolve_semaphore.release()
            released += 1
            await asyncio.wait_for(called.wait(), .2)
            await runtime
        finally:
            for _ in range(s._import_resolve_capacity - released):
                s._import_resolve_semaphore.release()
            await s.cancel_background_tasks()
    asyncio.run(scenario())


def test_shutdown_drains_active_manual_work_and_releases_admission(env, monkeypatch):
    s, database = env
    source(database, 'one')
    eid = entry(s, 'one', 91005, attach=False)
    async def scenario():
        began = asyncio.Event()
        drained = asyncio.Event()
        async def work(*args, **kwargs):
            began.set()
            try:
                await asyncio.Event().wait()
            finally:
                await asyncio.sleep(.01)
                drained.set()
        monkeypatch.setattr(s, 'refresh_entry', work)
        s._background_task_semaphore = asyncio.Semaphore(0)
        result = s.schedule_entry_refresh_job(eid)
        try:
            await asyncio.wait_for(began.wait(), .2)
        finally:
            await s.cancel_background_tasks()
        assert drained.is_set()
        assert not s._refresh_tasks
        task = next(x for x in module.task_registry.snapshot()['items'] if x['id'] == result['task_id'])
        assert task['state'] == 'failed' and 'cancel' in task['message'].lower()
        for _ in range(s._import_resolve_capacity):
            await asyncio.wait_for(s._import_resolve_semaphore.acquire(), .1)
        for _ in range(s._import_resolve_capacity):
            s._import_resolve_semaphore.release()
    asyncio.run(scenario())
