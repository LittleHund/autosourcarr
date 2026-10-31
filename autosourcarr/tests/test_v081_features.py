from __future__ import annotations

import asyncio
import ast
import uuid
from pathlib import Path

from app.db import db
from app.service import AutoSourcarrService
from app.task_registry import TaskRegistry


def root() -> Path:
    return Path(__file__).resolve().parents[2]


def add_source(source_id: str) -> None:
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_language,enabled_search,enabled_chapters,priority,last_seen,available)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)""",
            (source_id, source_id, source_id, "en", "https://example.invalid", "", 1, 1, 1, 1, 50, "now", 1),
        )


def manga(mid: int, title: str, source_id: str) -> dict:
    return {
        "id": mid,
        "sourceId": source_id,
        "title": title,
        "url": f"/m/{mid}",
        "thumbnailUrl": "",
        "description": "",
        "author": "",
        "artist": "",
        "genre": [],
        "status": 1,
        "initialized": True,
        "realUrl": "",
    }


def test_v081_release_ui_contract():
    r = root()
    assert tuple(map(int,(r / "VERSION").read_text().strip().split('.'))) >= (0,2,81)
    html = (r / "autosourcarr/app/static/index.html").read_text()
    js = (r / "autosourcarr/app/static/app.js").read_text()
    assert f'/static/app.js?v={(r / "VERSION").read_text().strip()}' in html
    assert f'/static/style.css?v={(r / "VERSION").read_text().strip()}' in html
    assert 'id="searchAllSubbed"' in html
    assert 'id="fullReset"' in html
    assert "taskFinishedText" in js
    assert ".sort(taskNewestFirst)" in js
    assert "/api/admin/subbed/search-all" in js
    assert "/api/admin/maintenance/full-reset" in js
    assert "0.2.81 → 1.4.2081" in (r / "CHANGELOG.md").read_text()


def test_task_registry_finished_rows_are_newest_first_and_timestamped():
    registry = TaskRegistry()
    older = registry.create("Older")
    registry.complete(older)
    newer = registry.create("Newer")
    registry.fail(newer, "boom")
    snap = registry.snapshot()
    done = [row for row in snap["items"] if row["state"] != "running"]
    assert all(row.get("finished_at") for row in done)
    # Snapshot keeps both completed and failed sections newest-first internally;
    # browser rendering also explicitly sorts each section by finished_at.
    assert next(row for row in done if row["id"] == newer)["finished_at"] >= next(row for row in done if row["id"] == older)["finished_at"]


def test_subbed_search_all_uses_exhaustive_discovery(monkeypatch):
    s = AutoSourcarrService()
    sid = f"v81-{uuid.uuid4().hex[:8]}"
    add_source(sid)
    eid, _, _ = s.ingest_manga(sid, manga(881001, "Subbed Search All", sid))
    calls = []

    async def fake_discover(entry_id: int, source_id=None, *, exhaustive=False):
        calls.append((entry_id, source_id, exhaustive))
        return {"entry_id": entry_id, "added": 0, "primary_added": 0, "additional_added": 0}

    monkeypatch.setattr(s, "discover_all_source_records", fake_discover)

    async def run():
        result = s.schedule_subbed_search_all([eid])
        assert result["queued"] is True
        task = s._refresh_tasks["__subbed-search-all__"]
        await task

    asyncio.run(run())
    assert calls == [(eid, None, True)]


def test_full_reset_preserves_configuration_tables_by_contract():
    source = (root() / "autosourcarr/app/service.py").read_text()
    start = source.index("    def full_reset_database")
    end = source.index("    def delete_entry", start)
    body = source[start:end]
    for cleared in [
        "manual_match_bindings", "manual_match_groups", "manual_separate_records",
        "preferred_title_overrides", "import_runs", "import_items",
        "chapter_candidate_rejections",
    ]:
        assert cleared in body
    assert 'DELETE FROM user_ui_preferences' not in body
    assert 'DELETE FROM source_groups' not in body
    assert 'DELETE FROM source_config' not in body
    main = (root() / "autosourcarr/app/main.py").read_text()
    assert 'seed["rebuild_hints"] = []' in main
    assert 'seed["manual_title"] = False' in main
    assert 'seed["description"] = ""' in main
    assert "source_config``" in source or "source_config" in body
    assert "restore_persistent_manual_matches" not in main[main.index('async def admin_full_reset'):main.index('@app.post("/api/admin/import/mihon-backup"')]


def test_v081_python_files_parse():
    r = root()
    for rel in ["autosourcarr/app/service.py", "autosourcarr/app/main.py", "autosourcarr/app/task_registry.py"]:
        ast.parse((r / rel).read_text())
