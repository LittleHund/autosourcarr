from __future__ import annotations

import ast
import sqlite3
from pathlib import Path

from app.config import settings
from app.db import Database


def root() -> Path:
    return Path(__file__).resolve().parents[2]


def test_v077_version_alignment():
    r = root()
    current = tuple(int(x) for x in (r / "VERSION").read_text().strip().split("."))
    assert current >= (0, 2, 77)
    assert tuple(int(x) for x in (r / "autosourcarr/VERSION").read_text().strip().split(".")) == current
    assert "0.2.77 → 1.4.2077" in (r / "CHANGELOG.md").read_text()


def test_upgrade_from_pre_source_group_database_migrates_before_group_index(tmp_path, monkeypatch):
    """Regression: v0.2.76 tried to index group_id before ALTER TABLE added it."""
    catalog = tmp_path / "catalog.sqlite"
    with sqlite3.connect(catalog) as conn:
        conn.executescript(
            """
            CREATE TABLE source_config (
                source_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                display_name TEXT NOT NULL DEFAULT '',
                lang TEXT NOT NULL DEFAULT '',
                base_url TEXT NOT NULL DEFAULT '',
                icon_url TEXT NOT NULL DEFAULT '',
                supports_latest INTEGER NOT NULL DEFAULT 1,
                enabled_search INTEGER NOT NULL DEFAULT 1,
                enabled_chapters INTEGER NOT NULL DEFAULT 1,
                priority INTEGER NOT NULL DEFAULT 1000,
                last_seen TEXT NOT NULL,
                available INTEGER NOT NULL DEFAULT 1
            );
            INSERT INTO source_config(source_id,name,last_seen) VALUES('legacy','Legacy Source','now');
            """
        )

    monkeypatch.setattr(settings, "data_dir", tmp_path)
    monkeypatch.setattr(settings, "database_dir", None)

    migrated = Database()
    migrated.close_current_thread()

    with sqlite3.connect(catalog) as conn:
        columns = {row[1] for row in conn.execute("PRAGMA table_info(source_config)")}
        assert {"group_id", "official", "official_manual", "enabled_language"} <= columns
        indexes = {row[1] for row in conn.execute("PRAGMA index_list(source_config)")}
        assert "idx_source_config_group" in indexes
        assert conn.execute("SELECT name FROM source_config WHERE source_id='legacy'").fetchone()[0] == "Legacy Source"


def test_group_index_is_deferred_out_of_bootstrap_schema():
    text = (root() / "autosourcarr/app/db.py").read_text()
    # The SQL schema must not reference group_id before _migrate() can add it to an
    # existing source_config table. The index is still created in _migrate().
    prefix = text.split("class Database:", 1)[0]
    assert "CREATE INDEX IF NOT EXISTS idx_source_config_group ON source_config(group_id)" not in prefix
    assert 'CREATE INDEX IF NOT EXISTS idx_source_config_group ON source_config(group_id)' in text


def test_v077_python_files_parse():
    r = root()
    for rel in ["autosourcarr/app/db.py", "autosourcarr/app/service.py", "autosourcarr/app/main.py"]:
        ast.parse((r / rel).read_text())
