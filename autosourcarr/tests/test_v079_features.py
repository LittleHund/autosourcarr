from __future__ import annotations

import ast
import uuid
from pathlib import Path

from app.db import db
from app.service import AutoSourcarrService


def root() -> Path:
    return Path(__file__).resolve().parents[2]


def add_source(source_id: str, name: str, priority: int) -> None:
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_language,enabled_search,enabled_chapters,priority,last_seen,available)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)""",
            (source_id, name, name, "en", f"https://{source_id}.invalid", "", 1, 1, 1, 1, priority, "now", 1),
        )


def test_v079_release_and_three_column_chapter_layout():
    r = root()
    current = (r / "VERSION").read_text().strip()
    assert tuple(int(x) for x in current.split(".")) >= (0, 2, 79)
    html = (r / "autosourcarr/app/static/index.html").read_text()
    css = (r / "autosourcarr/app/static/style.css").read_text()
    js = (r / "autosourcarr/app/static/app.js").read_text()
    assert f"/static/app.js?v={current}" in html
    assert f"/static/style.css?v={current}" in html
    assert "grid-template-columns:repeat(3,minmax(0,1fr))" in css
    assert "<th>Group</th><th>Priority</th><th>Source</th>" in js
    assert "sourceGroupSharedCell" in js
    assert "0.2.79 → 1.4.2079" in (r / "CHANGELOG.md").read_text()


def test_group_priority_is_shared_and_member_assignment_inherits_it():
    s = AutoSourcarrService()
    suffix = uuid.uuid4().hex[:8]
    sources = [f"grp79-{suffix}-{i}" for i in range(3)]
    for i, sid in enumerate(sources):
        add_source(sid, f"Scan source {suffix} {i}", 10 + i * 50)
    group = s.create_source_group(f"Scans {suffix}", priority=77)
    for sid in sources:
        s.update_source(sid, group_id=group["id"])
        assert s.get_source(sid)["priority"] == 77
        assert s.get_source(sid)["group_priority"] == 77
    s.update_source_group(group["id"], priority=23)
    assert all(s.get_source(sid)["priority"] == 23 for sid in sources)
    assert next(g for g in s.list_source_groups() if g["id"] == group["id"])["priority"] == 23


def test_priority_change_on_one_group_member_updates_every_member():
    s = AutoSourcarrService()
    suffix = uuid.uuid4().hex[:8]
    a, b = f"grp79-a-{suffix}", f"grp79-b-{suffix}"
    add_source(a, f"A {suffix}", 100)
    add_source(b, f"B {suffix}", 200)
    group = s.create_source_group(f"Shared {suffix}", priority=90)
    s.update_source(a, group_id=group["id"])
    s.update_source(b, group_id=group["id"])
    s.update_source(a, priority=5)
    assert s.get_source(a)["priority"] == 5
    assert s.get_source(b)["priority"] == 5
    assert next(g for g in s.list_source_groups() if g["id"] == group["id"])["priority"] == 5


def test_v079_python_files_parse():
    r = root()
    for rel in ["autosourcarr/app/db.py", "autosourcarr/app/models.py", "autosourcarr/app/service.py", "autosourcarr/app/main.py"]:
        ast.parse((r / rel).read_text())


def test_upgrade_adds_group_priority_and_synchronizes_existing_members(tmp_path, monkeypatch):
    import sqlite3
    from app.config import settings
    from app.db import Database

    catalog = tmp_path / "catalog.sqlite"
    with sqlite3.connect(catalog) as conn:
        conn.executescript(
            """
            CREATE TABLE source_config (
                source_id TEXT PRIMARY KEY, name TEXT NOT NULL, display_name TEXT NOT NULL DEFAULT '',
                lang TEXT NOT NULL DEFAULT '', base_url TEXT NOT NULL DEFAULT '', icon_url TEXT NOT NULL DEFAULT '',
                supports_latest INTEGER NOT NULL DEFAULT 1, enabled_language INTEGER NOT NULL DEFAULT 1,
                enabled_search INTEGER NOT NULL DEFAULT 1, enabled_chapters INTEGER NOT NULL DEFAULT 1,
                priority INTEGER NOT NULL DEFAULT 1000, last_seen TEXT NOT NULL, available INTEGER NOT NULL DEFAULT 1,
                group_id INTEGER, official INTEGER NOT NULL DEFAULT 0, official_manual INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE source_groups (
                id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL
            );
            INSERT INTO source_groups(id,name,created_at,updated_at) VALUES(1,'Scans','now','now');
            INSERT INTO source_config(source_id,name,priority,last_seen,group_id) VALUES('a','A',80,'now',1);
            INSERT INTO source_config(source_id,name,priority,last_seen,group_id) VALUES('b','B',20,'now',1);
            INSERT INTO source_config(source_id,name,priority,last_seen,group_id) VALUES('c','C',60,'now',1);
            """
        )
    monkeypatch.setattr(settings, "data_dir", tmp_path)
    monkeypatch.setattr(settings, "database_dir", None)
    migrated = Database()
    migrated.close_current_thread()
    with sqlite3.connect(catalog) as conn:
        cols = {row[1] for row in conn.execute("PRAGMA table_info(source_groups)")}
        assert "priority" in cols
        assert conn.execute("SELECT priority FROM source_groups WHERE id=1").fetchone()[0] == 20
        assert {row[0] for row in conn.execute("SELECT priority FROM source_config WHERE group_id=1")} == {20}
