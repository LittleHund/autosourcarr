"""Independent, hermetic integration checks for the preset persistence contract."""
import sqlite3
import threading
from concurrent.futures import ThreadPoolExecutor

import pytest
from fastapi import HTTPException

from app import manual_reset, matching_policies, matching_presets, matching_review, tag_store
from app.config import settings
from app.db import Database
from app.service import AutoSourcarrService
from test_v0323_matching_review import env, entry, save


def create(env, name='Saved template', rule=None):
    service, database = env
    state = matching_presets.configuration(database)
    result = matching_presets.save(database, service, matching_review.PresetUpdate(
        revision=state['preset_revision'], name=name, description='Reusable local draft.',
        rule=rule or {'enabled': True, 'checks': {'title': .97}}))
    preset = next(p for p in result['presets'] if p['name'] == name)
    return result, preset


@pytest.mark.parametrize('reset_kind', ['catalog', 'full', 'manual'])
def test_all_resets_preserve_configuration_presets_and_rule_revision(env, monkeypatch, reset_kind):
    service, database = env
    monkeypatch.setattr(tag_store, 'db', database)
    entry(service, 'a')
    state, preset = create(env)
    save(env, 'source', 'a', 'external', preset['rule'])
    applied = matching_policies.rules(database)
    if reset_kind == 'catalog':
        service.reset_database()
    elif reset_kind == 'full':
        service.full_reset_database()
    else:
        manual_reset.reset(service, database, manual_reset.preview(service, database)['revision'])
    assert matching_presets.configuration(database) == state
    assert matching_policies.rules(database) == applied
    database.close_current_thread()
    restarted = Database()
    try:
        assert matching_presets.configuration(restarted) == state
        assert matching_policies.rules(restarted) == applied
    finally:
        restarted.close_current_thread()


def test_restore_old_source_snapshot_does_not_revert_newer_preset_edit(env):
    service, database = env
    state, preset = create(env)
    group = service.create_source_group('Old group')['id']
    service.update_source('a', group_id=group)
    save(env, 'group', group, 'internal', preset['rule'])
    snapshot = service.capture_source_preferences()
    newer = matching_presets.save(database, service, matching_review.PresetUpdate(
        revision=state['preset_revision'], name='Edited template', description='Newer edit.',
        rule={'enabled': True, 'checks': {'title': .99}}), preset['id'])
    with database.catalog() as c:
        c.execute('DELETE FROM matching_policies')
        c.execute('DELETE FROM source_groups')
        c.execute("UPDATE source_config SET source_id='new-a',group_id=NULL WHERE source_id='a'")
    matching_policies.invalidate(database)
    service.restore_source_preferences(snapshot)
    new_group = service.get_source('new-a')['group_id']
    assert matching_presets.configuration(database) == newer
    assert matching_policies.rules(database)[('group', str(new_group), 'internal')] == preset['rule']
    service.delete_source_group(new_group)
    assert matching_presets.configuration(database) == newer


def test_actual_constructor_adds_presets_to_earlier_catalog_without_changing_rules(env):
    service, database = env
    eid = entry(service, 'a')
    save(env, 'source', 'a', 'external', {'enabled': False, 'checks': {}})
    rules_before = matching_policies.rules(database)
    with database.catalog() as c:
        c.execute('DROP TABLE matching_presets')
    database.close_current_thread()
    restarted = Database()
    try:
        state = matching_presets.configuration(restarted)
        assert len(state['presets']) == 5 and all(p['builtin'] for p in state['presets'])
        assert matching_policies.rules(restarted) == rules_before
        with restarted.catalog() as c:
            assert c.execute('SELECT id FROM entries WHERE id=?', (eid,)).fetchone()
    finally:
        restarted.close_current_thread()


@pytest.mark.parametrize('operation', ['create', 'edit', 'delete'])
def test_sql_failure_rolls_back_preset_data_and_revision(env, operation):
    service, database = env
    state, preset = create(env)
    save(env, 'source', 'a', 'external', preset['rule'])
    rule_revision = matching_review.configuration(database, service)['revision']
    with database.catalog() as c:
        for event in ['INSERT', 'UPDATE', 'DELETE']:
            c.execute(f"CREATE TRIGGER fail_preset_{event.lower()} BEFORE {event} ON matching_presets "
                      "BEGIN SELECT RAISE(ABORT,'preset write failure'); END")
    with pytest.raises(sqlite3.IntegrityError, match='preset write failure'):
        if operation == 'delete':
            matching_presets.delete(database, service, preset['id'], state['preset_revision'])
        else:
            matching_presets.save(database, service, matching_review.PresetUpdate(
                revision=state['preset_revision'], name='Attempted mutation', description='',
                rule={'enabled': True, 'checks': {'title': .99}}),
                preset['id'] if operation == 'edit' else None)
    assert matching_presets.configuration(database) == state
    assert matching_review.configuration(database, service)['revision'] == rule_revision
    database.close_current_thread()
    restarted = Database()
    try:
        assert matching_presets.configuration(restarted) == state
    finally:
        restarted.close_current_thread()


def test_two_service_writers_use_one_sqlite_revision_transaction(env, monkeypatch):
    first, database = env
    second = AutoSourcarrService()
    state, preset = create(env)
    pending = [threading.Event(), threading.Event()]
    local = threading.local()
    original_connect = Database._connect

    class ContendedConnection(sqlite3.Connection):
        def execute(self, sql, *args, **kwargs):
            normalized = ' '.join(str(sql).split()).upper()
            # Observe admission before SQLite blocks on the external writer.
            # Correct code blocks at BEGIN before reading the revision; old code
            # blocks at INSERT after both writers accepted the same revision.
            if normalized == 'BEGIN IMMEDIATE' or normalized.startswith('INSERT INTO MATCHING_PRESETS'):
                if hasattr(local, 'index'):
                    pending[local.index].set()
            return super().execute(sql, *args, **kwargs)

    def connect(cls, path):
        if str(path) != str(settings.catalog_path):
            return original_connect(path)
        connection = sqlite3.connect(path, timeout=30, factory=ContendedConnection)
        connection.row_factory = sqlite3.Row
        return cls._configure_connection(connection)

    monkeypatch.setattr(Database, '_connect', classmethod(connect))
    blocker = sqlite3.connect(settings.catalog_path, timeout=30)
    blocker.execute('BEGIN IMMEDIATE')

    def write(index, service):
        local.index = index
        try:
            matching_presets.save(database, service, matching_review.PresetUpdate(
                revision=state['preset_revision'], name=f'Writer {index}', description='',
                rule={'enabled': True, 'checks': {'title': .98}}), preset['id'])
            return 200
        except HTTPException as error:
            return error.status_code
        finally:
            database.close_current_thread()

    with ThreadPoolExecutor(max_workers=2) as executor:
        futures = [executor.submit(write, i, service) for i, service in enumerate([first, second])]
        try:
            assert all(event.wait(3) for event in pending), 'Both writers must reach SQLite admission'
        finally:
            blocker.rollback()
            blocker.close()
        assert sorted(future.result(timeout=5) for future in futures) == [200, 409]

