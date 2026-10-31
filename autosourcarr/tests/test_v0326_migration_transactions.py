"""Actual constructor retries after faults inside owned SQLite migrations."""
from pathlib import Path
import sqlite3

import pytest

from app.config import settings
from app.db import Database
from test_v0323_matching_review import env


def legacy_language(database):
    with database.catalog() as c:
        c.execute("UPDATE source_config SET enabled_search=0,enabled_chapters=0 WHERE source_id='a'")
        c.execute("UPDATE source_config SET enabled_search=1,enabled_chapters=0 WHERE source_id='b'")
        c.execute('ALTER TABLE source_config DROP COLUMN enabled_language')


def interrupt_connection(monkeypatch, fault, scope='catalog'):
    connect = Database._connect
    target = settings.catalog_path if scope == 'catalog' else settings.origins_path
    class InterruptedConnection(sqlite3.Connection):
        def execute(self, sql, *args, **kwargs):
            normalized = ' '.join(str(sql).split()).upper()
            if fault == 'before_language_backfill' and normalized.startswith('UPDATE SOURCE_CONFIG SET ENABLED_LANGUAGE=CASE'):
                raise RuntimeError(fault)
            result = super().execute(sql, *args, **kwargs)
            if fault == 'after_language_alter' and normalized.startswith('ALTER TABLE SOURCE_CONFIG ADD COLUMN ENABLED_LANGUAGE'):
                raise RuntimeError(fault)
            if fault == 'after_origin_alter' and normalized.startswith('ALTER TABLE CHAPTER_CANDIDATES ADD COLUMN SOURCE_CHAPTER_INDEX'):
                raise RuntimeError(fault)
            return result
    def replacement(cls, path):
        if Path(path) != target:
            return connect(path)
        c = sqlite3.connect(path, timeout=30, factory=InterruptedConnection)
        c.row_factory = sqlite3.Row
        return cls._configure_connection(c)
    monkeypatch.setattr(Database, '_connect', classmethod(replacement))


@pytest.mark.parametrize('fault', ['before_language_backfill', 'after_language_alter'])
def test_constructor_language_alter_and_backfill_roll_back_together_then_retry(env, monkeypatch, fault):
    _, database = env
    legacy_language(database)
    with monkeypatch.context() as m:
        interrupt_connection(m, fault)
        with pytest.raises(RuntimeError, match=fault):
            Database()
    with database.catalog() as c:
        assert 'enabled_language' not in {r[1] for r in c.execute('PRAGMA table_info(source_config)')}
    restarted = Database()
    try:
        with restarted.catalog() as c:
            assert dict(c.execute('SELECT source_id,enabled_language FROM source_config')) == {'a': 0, 'b': 1, 'c': 1}
            # A later explicit language selection must survive subsequent retries.
            c.execute("UPDATE source_config SET enabled_language=1 WHERE source_id='a'")
            c.execute("UPDATE source_config SET enabled_language=0 WHERE source_id='b'")
        retried = Database()
        try:
            with retried.catalog() as c:
                assert dict(c.execute('SELECT source_id,enabled_language FROM source_config')) == {'a': 1, 'b': 0, 'c': 1}
        finally:
            retried.close_current_thread()
    finally:
        restarted.close_current_thread()


def test_origins_migration_rolls_back_independently_without_rewriting_committed_language_intent(env, monkeypatch):
    _, database = env
    legacy_language(database)
    with database.origins() as o:
        o.execute('ALTER TABLE chapter_candidates DROP COLUMN source_chapter_index')
    with monkeypatch.context() as m:
        interrupt_connection(m, 'after_origin_alter', scope='origins')
        with pytest.raises(RuntimeError, match='after_origin_alter'):
            Database()
    # Catalogue migration committed independently before the origins failure.
    with database.catalog() as c:
        assert c.execute("SELECT enabled_language FROM source_config WHERE source_id='a'").fetchone()[0] == 0
        c.execute("UPDATE source_config SET enabled_language=1 WHERE source_id='a'")
    with database.origins() as o:
        assert 'source_chapter_index' not in {r[1] for r in o.execute('PRAGMA table_info(chapter_candidates)')}
    retried = Database()
    try:
        with retried.catalog() as c:
            assert c.execute("SELECT enabled_language FROM source_config WHERE source_id='a'").fetchone()[0] == 1
        with retried.origins() as o:
            assert 'source_chapter_index' in {r[1] for r in o.execute('PRAGMA table_info(chapter_candidates)')}
    finally:
        retried.close_current_thread()
