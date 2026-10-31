import os
import tempfile
from pathlib import Path

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ["DATA_DIR"] = _TEST_DATA.name
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")

from app.db import db
from app.service import AutoSourcarrService


def root() -> Path:
    return Path(__file__).resolve().parents[2]


def source_row(source_id: str, priority: int):
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_language,enabled_search,enabled_chapters,priority,last_seen,available)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,1)""",
            (source_id, source_id, source_id, "en", "https://example.invalid", "", 1, 1, 1, 1, priority, "now"),
        )


def manga(mid: int, title: str, description: str = ""):
    return {
        "id": mid, "title": title, "url": f"/manga/{mid}", "thumbnailUrl": "",
        "description": description, "author": "", "artist": "", "genre": ["Drama"],
        "status": 1, "initialized": True, "realUrl": "",
    }


def test_redo_ui_replaces_two_manual_cleanup_buttons_and_refresh_is_single_action():
    r = root()
    js = (r / "autosourcarr/app/static/app.js").read_text()
    main = (r / "autosourcarr/app/main.py").read_text()
    assert '>Redo automation</button>' in js
    assert '>Unbind automatic matches</button>' not in js
    assert '>Split / unbind duplicates</button>' not in js
    assert 'onclick="refreshEntry(${id},\'${kind}\',this)">Refresh</button>' in js
    assert 'Refresh metadata + chapters</button><button onclick="refreshEntryCover' not in js
    assert "/redo-automation" in main
    assert "/refresh-all" in main


def test_redo_splits_unlocked_automation_but_preserves_manual_locked_relationships():
    s = AutoSourcarrService()
    for i, sid in enumerate(("v70-anchor", "v70-wrong-known", "v70-wrong-auto", "v70-manual"), 1):
        source_row(sid, i)
    anchor = manga(70001, "Original Work", "Original source summary")
    eid = s._create_entry(anchor, "v70-anchor")
    s._upsert_match(eid, "v70-anchor", anchor, 1.0, "auto", False)
    # `known` can be the result of later browsing over an old automatic attachment.
    s._upsert_match(eid, "v70-wrong-known", manga(70002, "Wrong Known", "foreign"), 1.0, "known", False)
    s._upsert_match(eid, "v70-wrong-auto", manga(70003, "Wrong Auto", "foreign"), 1.0, "auto", False)
    s._upsert_match(eid, "v70-manual", manga(70004, "Intentional Manual", "manual"), 1.0, "manual", True)

    out = s.prepare_entry_automation_redo(eid)
    assert out["split_records"] == 2
    original = s.get_entry(eid)
    attached = {(m["source_id"], int(m["source_manga_id"])) for m in original["matches"]}
    attached |= {(m["source_id"], int(m["source_manga_id"])) for m in original.get("source_variants", [])}
    assert attached == {("v70-anchor", 70001), ("v70-manual", 70004)}
    assert len(out["new_entry_ids"]) == 2


def test_redo_discards_unlocked_identity_and_stale_auto_lineage():
    s = AutoSourcarrService()
    source_row("v70-id-a", 1); source_row("v70-id-b", 2)
    a = manga(70101, "Identity Anchor")
    eid = s._create_entry(a, "v70-id-a")
    s._upsert_match(eid, "v70-id-a", a, 1.0, "auto", False)
    s._upsert_match(eid, "v70-id-b", manga(70102, "Foreign Identity"), 1.0, "auto", False)
    s.add_title(eid, "Wrong Provider Title", title_type="identity-primary", source_id="metadata:test")
    with db.catalog() as c:
        c.execute(
            """INSERT INTO identity_links(entry_id,provider,provider_name,external_id,confidence,match_type,locked,last_checked)
               VALUES(?,?,?,?,?,?,0,?)""",
            (eid, "test", "Test", "wrong-id", 0.9, "auto", "now"),
        )
        c.execute(
            "INSERT OR REPLACE INTO automatic_merge_lineage(canonical_entry_id,member_entry_id,snapshot_json,merged_at) VALUES(?,?,?,?)",
            (eid, eid, "{}", "now"),
        )
    out = s.prepare_entry_automation_redo(eid)
    ids = [int(x) for x in out["entry_ids"]]
    marks = ",".join("?" for _ in ids)
    with db.catalog() as c:
        assert c.execute(f"SELECT COUNT(*) n FROM identity_links WHERE entry_id IN ({marks}) AND locked=0", ids).fetchone()["n"] == 0
        assert c.execute(f"SELECT COUNT(*) n FROM automatic_merge_lineage WHERE canonical_entry_id IN ({marks})", ids).fetchone()["n"] == 0
        assert c.execute(f"SELECT COUNT(*) n FROM titles WHERE entry_id IN ({marks}) AND title_type LIKE 'identity-%'", ids).fetchone()["n"] == 0


def test_authelia_docs_use_valid_reader_regex():
    r = root()
    for name in ("README.md", "TRUENAS.md"):
        text = (r / name).read_text()
        assert "^/api/reader/v1([/?].*)?$" in text
        assert "^/api/reader/v1(?:/.*)?$" not in text


def test_v070_version_mapping_and_cache_busters():
    r = root()
    # Later releases supersede the live version/cache busters; keep this regression
    # focused on the historical v0.2.70 mapping remaining documented.
    changelog = (r / "CHANGELOG.md").read_text()
    assert "## 0.2.70 - 2026-08-28" in changelog
    assert "0.2.70 → 1.4.2070" in changelog
