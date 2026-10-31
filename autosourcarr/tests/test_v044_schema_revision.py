"""0.4.4: both SQLite files carry an AutoSourcarr schema stamp; newer schemas are refused."""
import sqlite3

import pytest
from app.config import settings
from app.db import Database, SCHEMA_APPLICATION_ID, SCHEMA_REVISION, SchemaRevisionError


def pragma(path, name):
    with sqlite3.connect(path) as conn:
        return conn.execute(f"PRAGMA {name}").fetchone()[0]


@pytest.fixture
def paths(monkeypatch, tmp_path):
    monkeypatch.setattr(settings, "data_dir", tmp_path)
    monkeypatch.setattr(settings, "database_dir", "")
    return settings.catalog_path, settings.origins_path


def test_new_and_unstamped_databases_receive_the_current_stamp(paths):
    Database().close_current_thread()
    for path in paths:
        assert pragma(path, "application_id") == SCHEMA_APPLICATION_ID
        assert pragma(path, "user_version") == SCHEMA_REVISION
    with sqlite3.connect(paths[0]) as conn:  # an unstamped 0.4.3 file upgrades in place
        conn.execute("PRAGMA application_id=0")
        conn.execute("PRAGMA user_version=0")
    Database().close_current_thread()
    assert pragma(paths[0], "user_version") == SCHEMA_REVISION


@pytest.mark.parametrize("index", [0, 1])
def test_newer_or_foreign_schema_is_refused_without_changes(paths, index):
    Database().close_current_thread()
    with sqlite3.connect(paths[index]) as conn:
        conn.execute(f"PRAGMA user_version={SCHEMA_REVISION + 1}")
    before = paths[index].read_bytes()
    with pytest.raises(SchemaRevisionError, match="schema revision"):
        Database()
    assert paths[index].read_bytes() == before
    with sqlite3.connect(paths[index]) as conn:
        conn.execute(f"PRAGMA user_version={SCHEMA_REVISION}")
        conn.execute("PRAGMA application_id=1234")
    with pytest.raises(SchemaRevisionError, match="not an AutoSourcarr database"):
        Database()
