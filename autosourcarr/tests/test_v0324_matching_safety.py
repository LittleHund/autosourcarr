"""Regression cases reproduced during the project knowledge review."""
import asyncio
import json
from unittest.mock import AsyncMock

import pytest
from app.config import settings
from app.db import now_iso
from app import service as module
from app import matching_review as review
from app.matcher import automatic_title_similarity
from test_v0323_matching_review import env, entry, payload, save, DESCRIPTION, TITLE


@pytest.mark.parametrize('threshold,candidate', [
    (.94, 'The Last Explorer of the Silver Kingdom Returns'),
    (.99, 'The Last Explorer of the Silent Kingdom'),
])
def test_summary_does_not_bypass_configured_title_floor(env, monkeypatch, threshold, candidate):
    service, _ = env
    monkeypatch.setattr(settings, 'auto_match_threshold', threshold)
    entry(service, 'a')
    assert .90 <= automatic_title_similarity(TITLE, candidate) < threshold
    assert not service._local_match_candidates(payload('b', 2, title=candidate), 'b')


def test_automatic_import_binding_respects_explicit_separate_entries(env, monkeypatch):
    service, database = env
    left, right = entry(service, 'a'), entry(service, 'b', 2)
    service.review_duplicate(left, right, 'not_duplicate')
    monkeypatch.setattr(service, '_current_source_id_for_backup', lambda *args: 'b')
    monkeypatch.setattr(module.source_host, 'fetch_source_manga', AsyncMock(return_value={'mangas': [payload('b', 2)]}))
    monkeypatch.setattr(module.source_host, 'fetch_manga_and_chapters', AsyncMock(return_value={'manga': payload('b', 2)}))
    # Stale source URL means only a title shortcut finds the already-owned record;
    # no exact source URL binding to the target can justify overriding Separate.
    item = {**payload('b', 2), 'source': 'b', 'url': '/old-backup-url'}
    assert service._import_candidate_evidence(item, left)['eligible']
    assert not service._auto_merge_group_evidence(left, right)['eligible']
    try:
        asyncio.run(service._bind_backup_source_to_entry(left, item))
    except ValueError:
        pass  # A policy/safety refusal may be translated to Unresolved by its caller.
    assert service.canonical_entry_id(left) != service.canonical_entry_id(right)
    assert review.history(database, service)['total'] == 0


def test_policy_variant_evidence_survives_retained_column_order(env):
    from test_v0323_matching_review import save
    service, database = env
    eid = entry(service, 'a')
    service._upsert_source_variant(eid, 'b', payload('b', 2), 1, 'auto')
    save(env, 'source', 'b', 'internal', {'enabled': True, 'checks': {'title': 1, 'summary': 1, 'author': 1}})
    before = service._entry_matching_policy({'eligible': True}, payload('b', 3), eid)
    snapshot_before = service._entry_duplicate_evidence_snapshot(eid)['policy_payloads']
    assert before['eligible']
    # db.py migration appends these three columns; this is an existing supported
    # upgrade layout, also exercised by test_v036_mixed_entries.py.
    with database.catalog() as c:
        columns = [r[1] for r in c.execute('PRAGMA table_info(source_variants)')]
        tail = ['match_type', 'locked', 'initialized']
        columns = [name for name in columns if name not in tail] + tail
        c.execute('ALTER TABLE source_variants RENAME TO old_variants')
        c.execute('CREATE TABLE source_variants AS SELECT ' + ','.join(columns) + ' FROM old_variants')
        c.execute('DROP TABLE old_variants')
    assert service._entry_matching_policy({'eligible': True}, payload('b', 3), eid) == before
    assert service._entry_duplicate_evidence_snapshot(eid)['policy_payloads'] == snapshot_before


def test_split_variant_survives_retained_column_order(env):
    service, database = env
    eid = entry(service, 'a')
    service._upsert_source_variant(eid, 'b', payload('b', 2), 1, 'auto')
    with database.catalog() as c:
        columns = [r[1] for r in c.execute('PRAGMA table_info(source_variants)')]
        tail = ['match_type', 'locked', 'initialized']
        columns = [name for name in columns if name not in tail] + tail
        c.execute('ALTER TABLE source_variants RENAME TO old_variants')
        c.execute('CREATE TABLE source_variants AS SELECT ' + ','.join(columns) + ' FROM old_variants')
        c.execute('DROP TABLE old_variants')
    split = service.unmatch_duplicate_records(eid, [{'source_id': 'b', 'source_manga_id': 2}])
    assert split['new_entry_id'] != eid
    assert service._find_existing_by_source('b', 2) == split['new_entry_id']
    assert service._find_existing_by_source('a', 1) == eid


@pytest.mark.parametrize('reported_sid', [None, 'other'])
def test_actual_source_identity_cannot_bypass_its_disabled_matching_policy(env, reported_sid):
    from test_v0323_matching_review import off
    service, _ = env
    eid = entry(service, 'a')
    off(env, 'b', 'external')
    new_id, _, _ = service.ingest_manga('b', payload('b', 2, sourceId=reported_sid))
    assert new_id != eid
    assert service._find_existing_by_source('b', 2) == new_id


def upstream(monkeypatch, service):
    monkeypatch.setattr(service, '_current_source_id_for_backup', lambda *args: 'b')
    monkeypatch.setattr(module.source_host, 'fetch_source_manga', AsyncMock(return_value={'mangas': [payload('b', 2)]}))
    monkeypatch.setattr(module.source_host, 'fetch_manga_and_chapters', AsyncMock(return_value={'manga': payload('b', 2)}))
    return {**payload('b', 2), 'source': 'b', 'url': '/old-backup-url'}


def test_manual_import_choice_still_can_combine_previously_separate_entries(env, monkeypatch):
    service, database = env
    left, right = entry(service, 'a'), entry(service, 'b', 2)
    service.review_duplicate(left, right, 'not_duplicate')
    item = upstream(monkeypatch, service)
    result = asyncio.run(service._bind_backup_source_to_entry(left, item, manual=True))
    assert result['bound']
    assert service.canonical_entry_id(left) == service.canonical_entry_id(right)
    assert review.history(database, service)['total'] == 0  # explicit, not automated


def test_safe_automatic_import_owner_merge_saves_actual_full_proof(env, monkeypatch):
    service, database = env
    left, right = entry(service, 'a'), entry(service, 'b', 2)
    save(env, 'source', 'b', 'external', {'enabled': True, 'checks': {'title': 1, 'summary': 1}})
    item = upstream(monkeypatch, service)
    result = asyncio.run(service._bind_backup_source_to_entry(left, item))
    assert result['bound']
    assert service.canonical_entry_id(left) == service.canonical_entry_id(right)
    page = review.history(database, service)
    assert page['total'] == 1
    audit = review.detail(database, service, page['items'][0]['id'])['evidence']
    assert audit['eligible']
    assert audit['direct_pairs'] == 1
    assert audit['pair_evidence'][0]['eligible']
    assert audit['pair_evidence'][0]['policy_checks']
    assert audit['recorded_reason'] == 'import-source-bind'


def test_retry_translates_unsafe_owner_to_unresolved_and_never_adds_library(env, monkeypatch):
    service, database = env
    left, right = entry(service, 'a'), entry(service, 'b', 2)
    service.review_duplicate(left, right, 'not_duplicate')
    item = upstream(monkeypatch, service)
    # Exercise the real retry+binding+state boundary; isolate initial candidate
    # discovery since this regression concerns committing its later result.
    monkeypatch.setattr(service, '_import_manual_autoresolve_entry', lambda *args: (None, ''))
    monkeypatch.setattr(service, 'resolve_backup_manga_entry_details', AsyncMock(return_value={'entry_id': left, 'reason': 'matching candidate'}))
    add = AsyncMock(return_value=('added', 'Added'))
    monkeypatch.setattr(service, '_add_import_entry_to_main_library', add)
    with database.catalog() as c:
        c.execute("INSERT INTO import_runs(id,state,total,unresolved,created_at) VALUES('run','completed',1,1,?)", (now_iso(),))
        iid = c.execute("INSERT INTO import_items(import_id,position,title,state,raw_json,updated_at) VALUES('run',0,?,'unresolved',?,?)", (item['title'], json.dumps(item), now_iso())).lastrowid
    result = asyncio.run(service.retry_import_item('run', iid))
    assert result['state'] == 'unresolved'
    assert result['entry_id'] is None
    assert 'reviewed-not-duplicate' in result['reason']
    with database.catalog() as c:
        state = c.execute('SELECT state,entry_id FROM import_items WHERE id=?', (iid,)).fetchone()
    assert tuple(state) == ('unresolved', None)
    assert service.canonical_entry_id(left) != service.canonical_entry_id(right)
    assert review.history(database, service)['total'] == 0
    add.assert_not_awaited()
