import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from app import service as module
from app.db import Database
from test_v087_recovery import env, entry


@pytest.mark.parametrize('embedded', [False, True])
@pytest.mark.parametrize('already_subscribed', [False, True])
def test_import_membership_uses_live_evidence_without_cache(env, monkeypatch, embedded, already_subscribed):
    s, db = env
    eid = entry(s, 'seed', 88901)
    manga = {'id': 88902}
    if embedded:
        manga['inLibrary'] = already_subscribed
    monkeypatch.setattr(s, '_main_autosourcarr_source_id', AsyncMock(return_value='main'))
    monkeypatch.setattr(s, '_main_manga_for_autosourcarr_entry', AsyncMock(return_value=manga))
    library = AsyncMock(return_value=[{'id': 88902, 'inLibrary': True}] if already_subscribed else [])
    mutation = AsyncMock(return_value={'id': 88902, 'inLibrary': True})
    monkeypatch.setattr(module, 'main_suwayomi', SimpleNamespace(library_mangas=library, set_manga_in_library=mutation))
    result = asyncio.run(s._add_import_entry_to_main_library(eid, 'Backup title'))
    assert result[0] == ('existing' if already_subscribed else 'added')
    assert mutation.await_count == (0 if already_subscribed else 1)
    assert library.await_count == (0 if embedded else 1)


def test_concurrent_imports_cannot_both_claim_new_membership(env, monkeypatch):
    s, db = env
    eid = entry(s, 'seed', 88901)
    monkeypatch.setattr(s, '_main_autosourcarr_source_id', AsyncMock(return_value='main'))
    state = {'member': False}
    async def materialize(*a):
        result = {'id': 88902, 'inLibrary': state['member']}
        await asyncio.sleep(.01)
        return result
    async def add(*a, **kwargs):
        await asyncio.sleep(.01)
        state['member'] = True
        return {'id': 88902, 'inLibrary': True}
    monkeypatch.setattr(s, '_main_manga_for_autosourcarr_entry', materialize)
    monkeypatch.setattr(module, 'main_suwayomi', SimpleNamespace(set_manga_in_library=add))
    async def run():
        results = await asyncio.gather(*(s._add_import_entry_to_main_library(eid, 'Backup') for _ in range(2)))
        assert sorted(r[0] for r in results) == ['added', 'existing']
    asyncio.run(run())


def test_unknown_preimport_membership_never_authorizes_add(env, monkeypatch):
    s, db = env
    eid = entry(s, 'seed', 88901)
    monkeypatch.setattr(s, '_main_autosourcarr_source_id', AsyncMock(return_value='main'))
    monkeypatch.setattr(s, '_main_manga_for_autosourcarr_entry', AsyncMock(return_value={'id': 88902}))
    mutation = AsyncMock(return_value={'inLibrary': True})
    monkeypatch.setattr(module, 'main_suwayomi', SimpleNamespace(
        library_mangas=AsyncMock(side_effect=RuntimeError('Membership unavailable')),
        set_manga_in_library=mutation))
    with pytest.raises(RuntimeError, match='Membership unavailable'):
        asyncio.run(s._add_import_entry_to_main_library(eid, 'Backup'))
    mutation.assert_not_awaited()


@pytest.mark.parametrize('verified', [False, True])
def test_reject_removes_only_verified_new_import_additions(env, monkeypatch, verified):
    s, db = env
    eid = entry(s, 'seed', 88901)
    s._create_import_run('ownership', 'ownership.tachibk')
    iid = s._store_import_item('ownership', 1, {'title': 'Monster List', 'source': 0},
                              state='added', reason='Historical add', entry_id=eid,
                              generate_candidates=False)
    with db.catalog() as c:
        c.execute('UPDATE import_items SET library_add_verified=? WHERE id=?', (int(verified), iid))
    removal = AsyncMock(return_value={'removed': True, 'reason': 'Removed new addition'})
    monkeypatch.setattr(s, '_remove_import_added_entry_from_main_library', removal)
    monkeypatch.setattr(s, 'import_item_live_suggestions', AsyncMock(return_value={'suggestions': []}))
    async def run():
        result = s.schedule_import_item_reject('ownership', iid)
        assert result['queued']
        await asyncio.wait_for(s._refresh_tasks[f'__import-reject__:ownership:{iid}'], 2)
        assert s._read_import_item('ownership', iid)['state'] == 'unresolved'
    asyncio.run(run())
    assert removal.await_count == int(verified)


def test_old_database_add_labels_do_not_gain_removal_authority_on_upgrade(env):
    s, db = env
    s._create_import_run('old', 'old.tachibk')
    iid = s._store_import_item('old', 1, {'title': 'Historical work'}, state='added',
                              reason='Added by old release', entry_id=None, generate_candidates=False)
    with db.catalog() as c:
        c.execute('ALTER TABLE import_items DROP COLUMN library_add_verified')
    db.close_current_thread()
    migrated = Database()
    with migrated.catalog() as c:
        row = c.execute('SELECT * FROM import_items WHERE id=?', (iid,)).fetchone()
        assert row['state'] == 'added'
        assert row['library_add_verified'] == 0
    migrated.close_current_thread()
