import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from app import service as module
from test_v086_imports import env, store

@pytest.mark.parametrize('action', ['match', 'retry'])
def test_manual_skip_is_not_overwritten(env, monkeypatch, action):
    s, db, registry = env
    iid = store(s)
    monkeypatch.setattr(s, 'entry_preferred_title', lambda *a: 'Selected entry')
    monkeypatch.setattr(s, '_import_match_snapshot', lambda *a: {'id': 42})
    monkeypatch.setattr(s, '_remember_import_manual_autoresolve', lambda *a: None)
    monkeypatch.setattr(s, '_import_manual_autoresolve_entry', lambda *a: (42, 'Known selection'))
    monkeypatch.setattr(s, '_clear_import_rejection_for_entry', lambda *a: 0)
    monkeypatch.setattr(s, 'canonical_entry_id', lambda eid: eid)
    monkeypatch.setattr(s, '_add_import_entry_to_main_library', AsyncMock(return_value=('added', 'Added')))
    async def run():
        entered, release = asyncio.Event(), asyncio.Event()
        async def binding(*a, **kwargs):
            entered.set()
            await release.wait()
            return {'entry_id': 42}
        monkeypatch.setattr(s, '_bind_backup_source_to_entry', binding)
        if action == 'match':
            s.schedule_import_item_match('run-a', iid, 42)
        else:
            s.schedule_import_item_retry('run-a', iid)
        runtime = s._refresh_tasks[f'__import-{action}__:run-a:{iid}']
        await asyncio.wait_for(entered.wait(), 2)
        s.skip_import_item('run-a', iid)
        assert s._read_import_item('run-a', iid)['state'] == 'skipped'
        release.set()
        await asyncio.wait_for(asyncio.gather(runtime, return_exceptions=True), 2)
        assert s._read_import_item('run-a', iid)['state'] == 'skipped'
    asyncio.run(run())

def test_reject_cannot_remove_membership_of_later_accepted_import(env, monkeypatch):
    s, db, registry = env
    rejected_iid = store(s, 'reject-run')
    later_iid = store(s, 'accept-run')
    monkeypatch.setattr(s, 'canonical_entry_id', lambda eid: eid)
    monkeypatch.setattr(s, '_main_autosourcarr_source_id', AsyncMock(return_value='main'))
    monkeypatch.setattr(s, 'get_entry', lambda eid: {'id': eid, 'preferred_title': 'Example', 'titles': []})
    monkeypatch.setattr(s, '_import_match_snapshot', lambda eid: {'id': eid})
    async def run():
        lookup_entered, release_lookup = asyncio.Event(), asyncio.Event()
        membership = {'present': True}
        async def materialize(*a):
            if asyncio.current_task().get_name() == 'removal':
                lookup_entered.set()
                await release_lookup.wait()
            return {'id': 44, 'inLibrary': membership['present']}
        async def set_member(mid, present):
            membership['present'] = present
            return {'id': mid, 'inLibrary': present}
        monkeypatch.setattr(s, '_main_manga_for_autosourcarr_entry', materialize)
        monkeypatch.setattr(module, 'main_suwayomi', SimpleNamespace(set_manga_in_library=set_member))
        removal = asyncio.create_task(s._remove_import_added_entry_from_main_library(42, rejected_iid), name='removal')
        await asyncio.wait_for(lookup_entered.wait(), 2)
        # A manual decision persists before its queued network reconciliation.
        s._update_import_item_state('accept-run', later_iid, 'existing', 'Accepted', 42)
        assert s._read_import_item('accept-run', later_iid)['state'] == 'existing'
        release_lookup.set()
        await asyncio.wait_for(removal, 2)
        assert membership['present'], 'Accepted later import lost its main library membership'
    asyncio.run(run())

