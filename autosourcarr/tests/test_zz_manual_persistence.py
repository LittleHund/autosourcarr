from __future__ import annotations

import os
import tempfile

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ.setdefault("DATA_DIR", _TEST_DATA.name)
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")

from app.db import db
from app.service import AutoSourcarrService


def source_row(source_id: str, name: str, priority: int = 10):
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_search,enabled_chapters,priority,last_seen)
            VALUES(?,?,?,?,?,?,?,?,?,?,?)""",
            (source_id, name, name, "en", "https://example.invalid", "", 1, 1, 1, priority, "now"),
        )


def manga(mid: int, title: str):
    return {
        "id": mid,
        "title": title,
        "url": f"/manga/{mid}",
        "thumbnailUrl": "",
        "description": f"Description for {title}",
        "author": "Manual Author",
        "artist": "Manual Author",
        "genre": ["Action"],
        "status": 1,
        "initialized": True,
        "realUrl": "",
    }


def _binding(source_id: str, manga_id: int):
    with db.catalog() as c:
        row = c.execute(
            "SELECT * FROM manual_match_bindings WHERE source_id=? AND source_manga_id=?",
            (source_id, manga_id),
        ).fetchone()
    return dict(row) if row else None


def test_manual_match_binding_survives_normal_database_reset_and_restores():
    s = AutoSourcarrService()
    source_row("persist-a", "Persistent Source A", 11)
    entry_id, _, _ = s.ingest_manga("persist-a", manga(91001, "Persistent Manual Work"))
    chosen = manga(91002, "Persistent Manual Work")
    s._upsert_match(entry_id, "persist-a", chosen, 1.0, "manual", True)
    s._persist_manual_binding(entry_id, "persist-a", chosen, source_name="Persistent Source A")
    assert _binding("persist-a", 91002) is not None

    s.reset_database()
    assert _binding("persist-a", 91002) is not None, "normal database reset must preserve manual intent"
    restored = s.restore_persistent_manual_matches()
    assert restored["restored"] >= 1
    owner = s._find_existing_by_source("persist-a", 91002)
    assert owner is not None
    entry = s.get_entry(owner)
    row = next(x for x in entry["matches"] + entry["source_variants"] if str(x["source_id"]) == "persist-a" and int(x["source_manga_id"]) == 91002)
    assert row["match_type"] == "manual"
    assert int(row["locked"]) == 1


def test_two_manual_matches_create_persistent_group_and_group_reset_keeps_matches():
    s = AutoSourcarrService()
    source_row("group-a", "Manual Group A", 12)
    source_row("group-b", "Manual Group B", 13)
    entry_id, _, _ = s.ingest_manga("group-a", manga(92001, "Grouped Manual Work"))
    second = manga(92002, "Grouped Manual Work")
    s._upsert_match(entry_id, "group-b", second, 1.0, "manual", True)
    s._persist_manual_binding(entry_id, "group-a", manga(92001, "Grouped Manual Work"), source_name="Manual Group A")
    s._persist_manual_binding(entry_id, "group-b", second, source_name="Manual Group B")

    payload = s.manual_groups_payload()
    group = next(g for g in payload["groups"] if {m["source_id"] for m in g["members"]} == {"group-a", "group-b"})
    assert group["member_count"] == 2
    assert _binding("group-a", 92001)["group_id"] == group["id"]
    assert _binding("group-b", 92002)["group_id"] == group["id"]

    reset = s.reset_manual_groups()
    assert reset["groups_removed"] >= 1
    assert _binding("group-a", 92001) is not None
    assert _binding("group-b", 92002) is not None
    assert _binding("group-a", 92001)["group_id"] is None
    assert _binding("group-b", 92002)["group_id"] is None


def test_reset_manual_matches_removes_persistence_and_unlocks_current_rows():
    s = AutoSourcarrService()
    source_row("reset-manual", "Reset Manual Source", 14)
    entry_id, _, _ = s.ingest_manga("reset-manual", manga(93001, "Reset Manual Work"))
    selected = manga(93002, "Reset Manual Work")
    s._upsert_match(entry_id, "reset-manual", selected, 1.0, "manual", True)
    s._persist_manual_binding(entry_id, "reset-manual", selected, source_name="Reset Manual Source")
    assert _binding("reset-manual", 93002) is not None

    result = s.reset_manual_matches()
    assert result["manual_matches_removed"] >= 1
    assert _binding("reset-manual", 93002) is None
    with db.catalog() as c:
        row = c.execute(
            "SELECT match_type,locked FROM source_matches WHERE source_id='reset-manual' AND source_manga_id=93002"
        ).fetchone()
    assert row is not None
    assert row["match_type"] == "auto"
    assert int(row["locked"]) == 0
