"""Manual import decisions must outlive cancellation and concurrent discovery."""
import asyncio
from unittest.mock import AsyncMock

import pytest

from test_v086_imports import env, store


@pytest.mark.parametrize('action', ['match', 'retry'])
@pytest.mark.parametrize('started', [False, True])
def test_cancel_queued_resolution_restores_row_and_releases_lane(env, monkeypatch, action, started):
    s, db, registry = env
    iid = store(s)
    monkeypatch.setattr(s, 'entry_preferred_title', lambda *a: 'Selected entry')
    monkeypatch.setattr(s, '_import_match_snapshot', lambda *a: {'id': 42})
    monkeypatch.setattr(s, '_remember_import_manual_autoresolve', lambda *a: None)
    async def run():
        for _ in range(s._import_resolve_capacity):
            await s._import_resolve_semaphore.acquire()
        try:
            result = (s.schedule_import_item_match('run-a', iid, 42) if action == 'match'
                      else s.schedule_import_item_retry('run-a', iid))
            key = f'__import-{action}__:run-a:{iid}'
            runtime = s._refresh_tasks[key]
            if started:
                await asyncio.sleep(0)
            runtime.cancel()
            await asyncio.gather(runtime, return_exceptions=True)
            await asyncio.sleep(0)
            assert s._import_resolve_pending == 0
            assert s._import_resolve_active == 0
            assert key not in s._refresh_tasks
            assert s._read_import_item('run-a', iid)['state'] == 'unresolved'
        finally:
            for _ in range(s._import_resolve_capacity):
                s._import_resolve_semaphore.release()
    asyncio.run(run())


def test_late_automatic_state_write_cannot_overwrite_skip(env, monkeypatch):
    s, db, registry = env
    iid = store(s)
    monkeypatch.setattr(s, '_import_match_snapshot', lambda *a: {'id': 42})
    s.skip_import_item('run-a', iid)
    result = s._update_import_item_state('run-a', iid, 'added', 'Stale completion', 42,
                                         expected_states=('unresolved', 'failed'))
    assert result['superseded']
    row = s._read_import_item('run-a', iid)
    assert row['state'] == 'skipped' and row['entry_id'] is None
    assert row['library_add_verified'] == 0


@pytest.mark.parametrize('phase', ['metadata', 'binding'])
@pytest.mark.parametrize('action', ['skip', 'match'])
def test_manual_decision_cancels_inflight_automatic_import(env, monkeypatch, phase, action):
    s, db, registry = env
    iid = store(s)
    monkeypatch.setattr(s, '_backup_candidate_ranked', lambda *a, **kw: [])
    monkeypatch.setattr(s, '_import_auto_resolve_candidate_async', AsyncMock(return_value=(42, 'verified')))
    monkeypatch.setattr(s, '_import_match_snapshot', lambda eid: {'id': eid})
    monkeypatch.setattr(s, 'entry_preferred_title', lambda *a: 'Manual choice')
    monkeypatch.setattr(s, '_remember_import_manual_autoresolve', lambda *a: None)
    adds = []
    async def run():
        entered, release = asyncio.Event(), asyncio.Event()
        async def wait_phase(which, result):
            if which == phase:
                entered.set()
                await release.wait()
            return result
        monkeypatch.setattr(s, '_resolve_import_metadata_identity', lambda *a, **kw: wait_phase('metadata', {}))
        monkeypatch.setattr(s, '_bind_backup_source_to_entry', lambda *a, **kw: wait_phase('binding', {'entry_id': 42}))
        async def add(eid, *a, **kwargs):
            adds.append(eid)
            return 'added', 'Added'
        monkeypatch.setattr(s, '_add_import_entry_to_main_library', add)
        s.schedule_import_translation_enrichment('run-a')
        runtime = s._refresh_tasks['import-enrich:run-a:active']
        await asyncio.wait_for(entered.wait(), 2)
        manual_runtime = None
        if action == 'skip':
            # Skip is a synchronous FastAPI handler; exercise cross-thread cancel.
            await asyncio.to_thread(s.skip_import_item, 'run-a', iid)
        else:
            for _ in range(s._import_resolve_capacity):
                await s._import_resolve_semaphore.acquire()
            s.schedule_import_item_match('run-a', iid, 99)
            manual_runtime = s._refresh_tasks[f'__import-match__:run-a:{iid}']
        release.set()
        await asyncio.wait_for(runtime, 2)
        assert not adds  # obsolete automatic match never reaches main-library add
        row = s._read_import_item('run-a', iid)
        assert row['state'] == ('skipped' if action == 'skip' else 'resolving')
        if manual_runtime:
            manual_runtime.cancel()
            await asyncio.gather(manual_runtime, return_exceptions=True)
            for _ in range(s._import_resolve_capacity):
                s._import_resolve_semaphore.release()
    asyncio.run(run())
