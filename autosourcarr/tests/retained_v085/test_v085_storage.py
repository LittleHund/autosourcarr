import asyncio
import json
import sqlite3
import threading
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import pytest

from app.config import settings
from app.db import db, now_iso
from app.service import AutoSourcarrService


def virtual_chapter(entry_id=1):
    with db.origins() as c:
        return c.execute("INSERT INTO virtual_chapters(entry_id,chapter_key,display_name,created_at,updated_at) VALUES(?,?,?,?,?)",
                         (entry_id, "1", "Chapter 1", now_iso(), now_iso())).lastrowid


@pytest.mark.parametrize("nested", [False, True])
def test_cancelled_database_transaction_rolls_back(nested):
    with pytest.raises(asyncio.CancelledError):
        with db.catalog() as c:
            if nested:
                db.set_setting("cancelled-write", "must not commit")
            else:
                c.execute("INSERT INTO settings(key,value) VALUES(?,?)", ("cancelled-write", "must not commit"))
            raise asyncio.CancelledError()
    assert db.setting("cancelled-write", "absent") == "absent"
    assert db._local.catalog_depth == 0
    assert db._active_db_contexts == 0


def test_failed_savepoint_entry_restores_nesting_depth(monkeypatch):
    original = db._thread_connection
    class FailSavepoint:
        def __init__(self, conn): self.conn = conn
        def __getattr__(self, name): return getattr(self.conn, name)
        def execute(self, sql, *args):
            if sql.startswith("SAVEPOINT "):
                raise sqlite3.OperationalError("injected savepoint failure")
            return self.conn.execute(sql, *args)
    def connection(kind, path):
        conn, depth = original(kind, path)
        return (FailSavepoint(conn) if depth else conn), depth
    with db.catalog():
        monkeypatch.setattr(db, "_thread_connection", connection)
        with pytest.raises(sqlite3.OperationalError):
            with db.catalog():
                pytest.fail("Broken SAVEPOINT should not enter body")
        assert db._local.catalog_depth == 1
    assert db._local.catalog_depth == 0


def test_admitted_transaction_can_enter_other_database_during_relocation():
    started, enter_nested, finished = threading.Event(), threading.Event(), threading.Event()
    errors = []
    def worker():
        try:
            with db.catalog():
                started.set()
                assert enter_nested.wait(2)
                with db.origins() as o:
                    o.execute("SELECT 1").fetchone()
            finished.set()
        except BaseException as exc:
            errors.append(exc)
        finally:
            db.close_current_thread()
    thread = threading.Thread(target=worker, daemon=True)
    thread.start()
    try:
        assert started.wait(2)
        with db._relocation_condition:
            db._database_relocating = True
        enter_nested.set()
        assert finished.wait(2), "Relocation gate deadlocked an already admitted parent"
    finally:
        with db._relocation_condition:
            db._database_relocating = False
            db._relocation_condition.notify_all()
        enter_nested.set()
        thread.join(2)
    assert not errors


@pytest.mark.parametrize("full", [False, True])
def test_reset_does_not_recycle_reader_chapter_ids(full):
    service = AutoSourcarrService()
    old = virtual_chapter()
    service.full_reset_database() if full else service.reset_database()
    assert virtual_chapter() > old


def mounted_target(monkeypatch, tmp_path):
    original = Path.read_text
    def read(path, *args, **kwargs):
        if str(path) == "/proc/self/mountinfo":
            return f"1 0 0:1 / / rw - overlay overlay rw\n2 1 0:2 / {tmp_path} rw - tmpfs tmpfs rw\n"
        return original(path, *args, **kwargs)
    monkeypatch.setattr(Path, "read_text", read)
    return tmp_path / "target"


@pytest.mark.parametrize("existing", ["catalog.sqlite", "chapter-origins.sqlite", "catalog.sqlite-wal", "chapter-origins.sqlite-shm"])
def test_relocation_never_overwrites_target_database(monkeypatch, tmp_path, existing):
    target = mounted_target(monkeypatch, tmp_path)
    target.mkdir()
    sentinel = target / existing
    sentinel.write_bytes(b"existing user data")
    with pytest.raises(ValueError, match="already contains"):
        db.stage_database_relocation(target, settings.settings_config_dir)
    assert sentinel.read_bytes() == b"existing user data"


def test_backup_failure_does_not_create_missing_source_or_leave_temp_files(tmp_path):
    missing, target = tmp_path / "missing.sqlite", tmp_path / "target.sqlite"
    with pytest.raises(sqlite3.OperationalError):
        db._backup_database_file(missing, target)
    assert not missing.exists()
    assert not target.exists()
    assert not list(tmp_path.glob("*.moving"))


def test_relocation_failed_activation_marker_keeps_source_active(monkeypatch, tmp_path):
    import app.db as db_module
    target = mounted_target(monkeypatch, tmp_path)
    source = settings.database_base_dir.resolve()
    db.set_setting("retained", "yes")
    db.stage_database_relocation(target, settings.settings_config_dir)
    original = db_module.os.replace
    def replace(src, dst):
        if Path(dst).name == "database-relocation.json":
            raise OSError("injected marker failure")
        return original(src, dst)
    with monkeypatch.context() as patch:
        patch.setattr(db_module.os, "replace", replace)
        with pytest.raises(OSError, match="marker failure"):
            db.activate_database_relocation(settings.settings_config_dir)
    assert settings.database_base_dir.resolve() == source
    assert db.setting("retained") == "yes"
    assert db._database_relocating is False
    result = db.activate_database_relocation(settings.settings_config_dir)
    assert result["activated"] is True
    assert db.setting("retained") == "yes"


def test_concurrent_preference_merges_preserve_unrelated_keys(monkeypatch):
    # Old implementation read outside its write transaction: synchronize those
    # reads to deterministically expose the lost-update bug on the baseline.
    original = db.user_ui_preferences
    read_barrier = threading.Barrier(2)
    def racing_read(key):
        result = original(key)
        read_barrier.wait(timeout=3)
        return result
    monkeypatch.setattr(db, "user_ui_preferences", racing_read)
    def write(values):
        try:
            return db.set_user_ui_preferences("test-user", values)
        finally:
            db.close_current_thread()
    with ThreadPoolExecutor(max_workers=2) as workers:
        jobs = [workers.submit(write, {"theme": "blue"}), workers.submit(write, {"density": "compact"})]
        for job in jobs:
            job.result(timeout=5)
    assert original("test-user") == {"theme": "blue", "density": "compact"}


def test_added_indexes_are_used_for_hot_queries():
    with db.catalog() as c:
        plan = str([tuple(r) for r in c.execute("EXPLAIN QUERY PLAN SELECT id FROM entries ORDER BY updated_at,id LIMIT 20")])
    with db.origins() as o:
        origin_plan = str([tuple(r) for r in o.execute("EXPLAIN QUERY PLAN SELECT virtual_chapter_id FROM served_origin WHERE candidate_id=?", (1,))])
    assert "idx_entries_updated_id" in plan
    assert "idx_served_origin_candidate" in origin_plan
