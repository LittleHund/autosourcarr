"""Delayed imports must respect current separation and canonical ownership."""
import asyncio
from unittest.mock import AsyncMock

import pytest

from app import matching_policies as policies
from app import service as module
from test_v0323_matching_review import env, entry, payload, TITLE, DESCRIPTION


def test_automatic_import_cannot_override_retained_separation_without_record_owner(env, monkeypatch):
    service, _ = env
    target = entry(service, 'a')
    entry(service, 'b', 2)
    service._commit_separate_source_record(target, 'b', 2, payload('b', 2), {})
    service.reset_database()
    service.restore_subbed_reset_seeds([{'entry_id': target, 'canonical_entry_id': target,
        'title': TITLE, 'description': DESCRIPTION, 'author': 'Author One', 'status': 1}])
    target = service.ingest_manga('a', payload('a', 1))[0]
    assert service._is_manual_separate_record(target, 'b', 2)
    assert service._find_existing_by_source('b', 2) is None
    monkeypatch.setattr(service, '_current_source_id_for_backup', lambda *a: 'b')
    monkeypatch.setattr(module.source_host, 'fetch_source_manga', AsyncMock(return_value={'mangas': [payload('b', 2)]}))
    monkeypatch.setattr(module.source_host, 'fetch_manga_and_chapters', AsyncMock(return_value={'manga': payload('b', 2)}))
    item = {**payload('b', 2), 'source': 'b', 'url': '/stale'}
    with pytest.raises(policies.PolicyBlocked, match='different work'):
        asyncio.run(service._bind_backup_source_to_entry(target, item))
    assert service._find_existing_by_source('b', 2) is None
    assert asyncio.run(service._bind_backup_source_to_entry(target, item, manual=True))['bound']
    assert service._find_existing_by_source('b', 2) == target


@pytest.mark.parametrize('restrict_survivor', [False, True])
def test_delayed_import_follows_the_targets_new_canonical_owner(env, monkeypatch, restrict_survivor):
    service, _ = env
    target, other = entry(service, 'a'), entry(service, 'c', 3)
    monkeypatch.setattr(service, '_current_source_id_for_backup', lambda *a: 'b')
    monkeypatch.setattr(service, '_entry_reliability', lambda eid: (1 if eid == other else 0,))
    monkeypatch.setattr(module.source_host, 'fetch_manga_and_chapters', AsyncMock(return_value={'manga': payload('b', 2)}))

    async def run():
        started, resume = asyncio.Event(), asyncio.Event()
        async def fetch(*args, **kwargs):
            started.set()
            await resume.wait()
            return {'mangas': [payload('b', 2)]}
        monkeypatch.setattr(module.source_host, 'fetch_source_manga', fetch)
        pending = asyncio.create_task(service._bind_backup_source_to_entry(target, {**payload('b', 2), 'source': 'b', 'url': '/stale'}))
        await asyncio.wait_for(started.wait(), 2)
        survivor = service.merge_entries(target, other, 'manual-review')
        if restrict_survivor:
            from test_v0323_matching_review import off
            off(env, 'c', 'external')
        resume.set()
        if restrict_survivor:
            with pytest.raises(policies.PolicyBlocked):
                await pending
            assert service._find_existing_by_source('b', 2) is None
            return
        result = await pending
        assert result['bound']
        assert service._find_existing_by_source('b', 2) == survivor
        assert service.canonical_entry_id(target) == survivor
    asyncio.run(run())
