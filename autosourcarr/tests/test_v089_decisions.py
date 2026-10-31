import asyncio
import pytest
from types import SimpleNamespace
from unittest.mock import AsyncMock
from test_v086_imports import env, store
from app import service as module


@pytest.mark.parametrize('action', ['match', 'retry'])
def test_cancel_foreground_replacement_restores_settled_state(env, monkeypatch, action):
    s, _, _ = env
    iid = prepare(s, monkeypatch)
    monkeypatch.setattr(s, '_import_manual_autoresolve_entry', lambda item: (43, 'Remembered'))
    async def run():
        entered = asyncio.Event()
        async def bind(*args, **kwargs):
            entered.set()
            await asyncio.Event().wait()
        monkeypatch.setattr(s, '_bind_backup_source_to_entry', bind)
        for _ in range(s._import_resolve_capacity):
            await s._import_resolve_semaphore.acquire()
        try:
            s.schedule_import_item_match('run-a', iid, 42)
            queued = s._refresh_tasks[f'__import-match__:run-a:{iid}']
            call = s.match_import_item('run-a', iid, 43) if action == 'match' else s.retry_import_item('run-a', iid)
            foreground = asyncio.create_task(call)
            await asyncio.wait_for(entered.wait(), 2)
            foreground.cancel()
            await asyncio.gather(queued, foreground, return_exceptions=True)
            await asyncio.sleep(0)
            row = s._read_import_item('run-a', iid)
            assert row['state'] == 'unresolved'
            assert row['entry_id'] is None
        finally:
            for _ in range(s._import_resolve_capacity):
                s._import_resolve_semaphore.release()
    asyncio.run(run())


def prepare(s, monkeypatch):
    iid = store(s)
    monkeypatch.setattr(s, 'entry_preferred_title', lambda *a: 'Selected entry')
    monkeypatch.setattr(s, '_import_match_snapshot', lambda eid: {'id': eid})
    monkeypatch.setattr(s, '_remember_import_manual_autoresolve', lambda *a: None)
    monkeypatch.setattr(s, '_clear_import_rejection_for_entry', lambda *a: 0)
    monkeypatch.setattr(s, 'canonical_entry_id', lambda eid: eid)
    return iid


def test_cancel_replacement_restores_settled_state(env, monkeypatch):
    s, db, _ = env
    iid = prepare(s, monkeypatch)
    async def run():
        for _ in range(s._import_resolve_capacity):
            await s._import_resolve_semaphore.acquire()
        try:
            s.schedule_import_item_match('run-a', iid, 42)
            a = s._refresh_tasks[f'__import-match__:run-a:{iid}']
            s.schedule_import_item_match('run-a', iid, 43)
            b = s._refresh_tasks[f'__import-match__:run-a:{iid}']
            b.cancel()
            await asyncio.gather(a, b, return_exceptions=True)
            await asyncio.sleep(0)
            assert s._read_import_item('run-a', iid)['state'] == 'unresolved'
        finally:
            for _ in range(s._import_resolve_capacity):
                s._import_resolve_semaphore.release()
    asyncio.run(run())


def test_foreground_match_waiting_for_library_lock_cannot_add_after_skip(env, monkeypatch):
    s, db, _ = env
    iid = prepare(s, monkeypatch)
    monkeypatch.setattr(s, '_bind_backup_source_to_entry', AsyncMock(return_value={'entry_id':42}))
    monkeypatch.setattr(s, '_main_autosourcarr_source_id', AsyncMock(return_value='main'))
    monkeypatch.setattr(s, 'get_entry', lambda eid: {'id':eid, 'preferred_title':'Example', 'titles':[]})
    monkeypatch.setattr(s, '_main_manga_for_autosourcarr_entry', AsyncMock(return_value={'id':44, 'inLibrary':False}))
    mutation = AsyncMock(return_value={'id':44, 'inLibrary':True})
    monkeypatch.setattr(module, 'main_suwayomi', SimpleNamespace(set_manga_in_library=mutation))
    async def run():
        entered = asyncio.Event()
        original = s._add_import_entry_to_main_library
        async def adding(*a, **kw):
            entered.set()
            return await original(*a, **kw)
        monkeypatch.setattr(s, '_add_import_entry_to_main_library', adding)
        await s._import_library_lock.acquire()
        task = asyncio.create_task(s.match_import_item('run-a', iid, 42))
        await asyncio.wait_for(entered.wait(), 2)
        s.skip_import_item('run-a', iid)
        s._import_library_lock.release()
        await asyncio.wait_for(task, 2)
        assert s._read_import_item('run-a', iid)['state'] == 'skipped'
        mutation.assert_not_awaited()
    asyncio.run(run())


def test_foreground_match_cannot_overwrite_reject(env, monkeypatch):
    s, db, _ = env
    iid = prepare(s, monkeypatch)
    s._update_import_item_state('run-a', iid, 'existing', 'Existing subscription', 42)
    monkeypatch.setattr(s, '_remember_import_rejection', lambda *a: {'rejected_title': 'Selected entry'})
    monkeypatch.setattr(s, '_forget_import_manual_autoresolve', lambda *a: True)
    monkeypatch.setattr(s, 'import_item_live_suggestions', AsyncMock(return_value={'suggestions': []}))
    add = AsyncMock(return_value=('added', 'Added'))
    monkeypatch.setattr(s, '_add_import_entry_to_main_library', add)
    async def run():
        entered, release = asyncio.Event(), asyncio.Event()
        async def bind(*a, **kwargs):
            entered.set()
            await release.wait()
            return {'entry_id': 42}
        monkeypatch.setattr(s, '_bind_backup_source_to_entry', bind)
        action = asyncio.create_task(s.match_import_item('run-a', iid, 42))
        await asyncio.wait_for(entered.wait(), 2)
        s.schedule_import_item_reject('run-a', iid)
        reject = s._refresh_tasks[f'__import-reject__:run-a:{iid}']
        release.set()
        await asyncio.wait_for(asyncio.gather(action, reject), 2)
        row = s._read_import_item('run-a', iid)
        assert row['state'] == 'unresolved' and row['entry_id'] is None
        add.assert_not_awaited()
    asyncio.run(run())


def test_late_completion_and_cancel_cannot_restore_same_state_new_decision(env, monkeypatch):
    s, _, _ = env
    iid = prepare(s, monkeypatch)
    old = s._read_import_item('run-a', iid)
    first = s._claim_import_decision('run-a', iid)
    s._update_import_item_state('run-a', iid, 'resolving', 'First', 42)
    second = s._claim_import_decision('run-a', iid)
    s._update_import_item_state('run-a', iid, 'resolving', 'Newer', 43)
    s._restore_cancelled_import_resolution('run-a', iid, old, 'resolving', first)
    result = s._update_import_item_state('run-a', iid, 'added', 'Obsolete result', 42, expected_revision=first)
    assert result['superseded']
    row = s._read_import_item('run-a', iid)
    assert (row['state'], row['entry_id'], row['decision_revision']) == ('resolving', 43, second)


def test_add_and_remove_library_reconciliation_do_not_overlap(env, monkeypatch):
    s, _, _ = env
    rejected = store(s, 'old')
    monkeypatch.setattr(s, 'canonical_entry_id', lambda eid: eid)
    monkeypatch.setattr(s, '_main_autosourcarr_source_id', AsyncMock(return_value='main'))
    monkeypatch.setattr(s, 'get_entry', lambda eid: {'id': eid, 'preferred_title': 'Example', 'titles': []})
    async def run():
        entered, release = asyncio.Event(), asyncio.Event()
        membership = {'present': True}
        add_lookup = []
        async def materialize(*a):
            if asyncio.current_task().get_name() == 'removal':
                entered.set()
                await release.wait()
            else:
                add_lookup.append(True)
            return {'id': 44, 'inLibrary': membership['present']}
        async def set_member(mid, present):
            membership['present'] = present
            return {'id': mid, 'inLibrary': present}
        monkeypatch.setattr(s, '_main_manga_for_autosourcarr_entry', materialize)
        monkeypatch.setattr(module, 'main_suwayomi', SimpleNamespace(set_manga_in_library=set_member))
        remove = asyncio.create_task(s._remove_import_added_entry_from_main_library(42, rejected), name='removal')
        await asyncio.wait_for(entered.wait(), 2)
        add = asyncio.create_task(s._add_import_entry_to_main_library(42, 'Example'))
        await asyncio.sleep(.02)
        assert not add_lookup, 'add must wait until rejection reconciliation finishes'
        release.set()
        await asyncio.wait_for(asyncio.gather(remove, add), 2)
        assert membership['present']
    asyncio.run(run())
