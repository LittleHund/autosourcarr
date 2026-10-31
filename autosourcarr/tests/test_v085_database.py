"""v0.2.85: transaction boundaries, reset configuration and relocation admission."""
import asyncio
import json
import threading
from pathlib import Path

import pytest
from app.config import settings
from app.db import Database
import app.service as service_module

@pytest.fixture
def database(monkeypatch, tmp_path):
    monkeypatch.setattr(settings, "data_dir", tmp_path)
    monkeypatch.setattr(settings, "database_dir", "")
    instance = Database()
    yield instance
    instance.close_current_thread()

@pytest.mark.parametrize("kind", ["catalog", "origins"])
def test_inner_success_remains_rollbackable_by_outer_scope(database, kind):
    context = getattr(database, kind)
    with context() as conn:
        conn.execute("CREATE TABLE audit_tx(value INTEGER)")
    with pytest.raises(RuntimeError):
        with context():
            # No outer write before SAVEPOINT: the old code committed this insert.
            with context() as inner:
                inner.execute("INSERT INTO audit_tx VALUES(1)")
            raise RuntimeError("outer failure")
    with context() as conn:
        assert conn.execute("SELECT count(*) FROM audit_tx").fetchone()[0] == 0

@pytest.mark.parametrize("exception", [asyncio.CancelledError, KeyboardInterrupt, SystemExit])
def test_base_exception_rolls_back_without_contaminating_next_transaction(database, exception):
    with database.catalog() as c:
        c.execute("CREATE TABLE audit_abort(value INTEGER)")
    with pytest.raises(exception):
        with database.catalog() as c:
            c.execute("INSERT INTO audit_abort VALUES(1)")
            raise exception()
    with database.catalog() as c:
        assert not c.in_transaction
        assert c.execute("SELECT count(*) FROM audit_abort").fetchone()[0] == 0


def test_nested_failure_can_be_caught_without_losing_outer_work(database):
    with database.catalog() as c:
        c.execute("CREATE TABLE audit_caught(value INTEGER)")
    with database.catalog() as c:
        c.execute("INSERT INTO audit_caught VALUES(1)")
        with pytest.raises(ValueError):
            with database.catalog() as inner:
                inner.execute("INSERT INTO audit_caught VALUES(2)")
                raise ValueError()
        c.execute("INSERT INTO audit_caught VALUES(3)")
    with database.catalog() as c:
        assert [r[0] for r in c.execute("SELECT value FROM audit_caught")] == [1, 3]


def test_existing_writer_can_finish_nested_work_during_relocation(database):
    admitted, start_nested, nested_done = threading.Event(), threading.Event(), threading.Event()
    failures = []
    def writer():
        try:
            with database.catalog():
                admitted.set()
                assert start_nested.wait(2)
                with database.origins() as c:
                    c.execute("SELECT 1")
                nested_done.set()
        except BaseException as exc:
            failures.append(exc)
        finally:
            database.close_current_thread()
    thread = threading.Thread(target=writer)
    thread.start()
    assert admitted.wait(2)
    with database._relocation_condition:
        database._database_relocating = True
    start_nested.set()
    try:
        assert nested_done.wait(.5), "relocation gate must not deadlock an admitted writer"
    finally:
        with database._relocation_condition:
            database._database_relocating = False
            database._relocation_condition.notify_all()
        thread.join(2)
    assert not thread.is_alive() and not failures
    assert database._active_db_contexts == 0


def test_normal_reset_preserves_all_source_preferences_but_clears_merge_lineage(database, monkeypatch):
    monkeypatch.setattr(service_module, "db", database)
    service = service_module.AutoSourcarrService()
    with database.catalog() as c:
        c.execute("INSERT INTO source_config(source_id,name,lang,priority,enabled_search,enabled_chapters,enabled_language,last_seen) VALUES('audit','Audit','en',73,0,0,0,'now')")
        before = dict(c.execute("SELECT * FROM source_config WHERE source_id='audit'").fetchone())
        c.execute("INSERT INTO automatic_merge_lineage(canonical_entry_id,member_entry_id,snapshot_json,merged_at) VALUES(1,2,'{}','now')")
    service.reset_database()
    with database.catalog() as c:
        assert dict(c.execute("SELECT * FROM source_config WHERE source_id='audit'").fetchone()) == before
        assert c.execute("SELECT count(*) FROM automatic_merge_lineage").fetchone()[0] == 0

@pytest.mark.parametrize("marker", ["not-json", "{}", '{"source_dir":"relative","target_dir":"/tmp/else"}'])
def test_invalid_relocation_marker_fails_closed(database, tmp_path, marker):
    config = tmp_path / "config"
    config.mkdir()
    (config / "database-relocation.json").write_text(marker)
    before = (tmp_path / "catalog.sqlite").read_bytes()
    with pytest.raises(RuntimeError):
        database.finalize_database_relocation(config)
    assert (tmp_path / "catalog.sqlite").read_bytes() == before
    assert (config / "database-relocation.json").exists()
