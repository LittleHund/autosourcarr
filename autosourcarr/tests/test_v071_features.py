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


def manga(mid: int, title: str, description: str = "", author: str = ""):
    return {
        "id": mid,
        "title": title,
        "url": f"/manga/{mid}",
        "thumbnailUrl": "",
        "description": description,
        "author": author,
        "artist": "",
        "genre": ["Action"],
        "status": 1,
        "initialized": True,
        "realUrl": "",
    }


def attached_records(s: AutoSourcarrService, eid: int):
    e = s.get_entry(eid)
    return {
        (str(r["source_id"]), int(r["source_manga_id"]), str(r.get("match_type") or ""))
        for r in [*(e.get("matches") or []), *(e.get("source_variants") or [])]
    }


def test_redo_reconsiders_old_split_duplicate_rows_from_previous_redo():
    s = AutoSourcarrService()
    source_row("v71-anchor", 1)
    source_row("v71-old-redo", 2)
    anchor = manga(71001, "Disastrous Necromancer", "A necromancer awakens during a monster catastrophe.")
    wrong = manga(71002, "Martial Peak", "A cultivator climbs the martial path in a sect.")
    eid = s._create_entry(anchor, "v71-anchor")
    s._upsert_match(eid, "v71-anchor", anchor, 1.0, "auto", False)
    # v0.2.70 could leave a record with this internal type after a failed Redo.
    s._upsert_match(eid, "v71-old-redo", wrong, 1.0, "split-duplicate", False)

    out = s.prepare_entry_automation_redo(eid)

    assert out["split_records"] == 1
    assert {(sid, mid) for sid, mid, _mt in attached_records(s, eid)} == {("v71-anchor", 71001)}
    new_id = int(out["new_entry_ids"][0])
    moved = attached_records(s, new_id)
    assert {(sid, mid) for sid, mid, _mt in moved} == {("v71-old-redo", 71002)}
    assert {mt for _sid, _mid, mt in moved} == {"redo-anchor"}
    assert s._duplicate_review(eid, new_id) == "not_duplicate"


def test_redo_chooses_consensus_anchor_instead_of_oldest_wrong_primary():
    s = AutoSourcarrService()
    for idx, sid in enumerate(("v71-wrong-old", "v71-correct-a", "v71-correct-b", "v71-correct-c", "v71-wrong-two"), 1):
        source_row(sid, idx)

    wrong_old = manga(71101, "Infinite Necromancer", "A hunter gains an endless summoning skill.")
    correct_a = manga(71102, "Disastrous Necromancer", "A class-awakened necromancer fights monsters in a changed modern world.")
    correct_b = manga(71103, "Disastrous Necromancer", "A class-awakened necromancer fights monsters in a changed modern world.")
    correct_c = manga(71104, "Disastrous Necromancer", "A class-awakened necromancer fights monsters in a changed modern world.")
    wrong_two = manga(71105, "The Real Daughter", "A noble family's lost daughter returns home.")

    # Deliberately create the wrong record first. This reproduces the v0.2.70
    # oldest-primary failure mode.
    eid = s._create_entry(wrong_old, "v71-wrong-old")
    s._upsert_match(eid, "v71-wrong-old", wrong_old, 1.0, "auto", False)
    s._upsert_match(eid, "v71-correct-a", correct_a, 1.0, "auto", False)
    s._upsert_match(eid, "v71-correct-b", correct_b, 1.0, "auto", False)
    s._upsert_match(eid, "v71-correct-c", correct_c, 1.0, "auto", False)
    s._upsert_match(eid, "v71-wrong-two", wrong_two, 1.0, "auto", False)

    out = s.prepare_entry_automation_redo(eid)

    assert out["anchor_title"] == "Disastrous Necromancer"
    assert out["anchor_source_id"] in {"v71-correct-a", "v71-correct-b", "v71-correct-c"}
    remaining = {(sid, mid) for sid, mid, _mt in attached_records(s, eid)}
    assert len(remaining) == 1
    assert next(iter(remaining))[0] in {"v71-correct-a", "v71-correct-b", "v71-correct-c"}
    assert out["split_records"] == 4

    # The two clearly unrelated works are permanently blocked from immediately
    # snowballing back into the repaired canonical entry.
    by_source = {}
    for new_id in out["new_entry_ids"]:
        rec = next(iter(attached_records(s, int(new_id))))
        by_source[rec[0]] = int(new_id)
    assert s._duplicate_review(eid, by_source["v71-wrong-old"]) == "not_duplicate"
    assert s._duplicate_review(eid, by_source["v71-wrong-two"]) == "not_duplicate"
    # Correct same-work records remain eligible for the duplicate automation to
    # rejoin after the clean rebuild.
    for sid in {"v71-correct-a", "v71-correct-b", "v71-correct-c"} - {out["anchor_source_id"]}:
        assert s._duplicate_review(eid, by_source[sid]) != "not_duplicate"


def test_redo_scrubs_stale_generated_aliases_copied_by_old_merges():
    s = AutoSourcarrService()
    source_row("v71-clean-a", 1)
    source_row("v71-clean-b", 2)
    anchor = manga(71201, "Clean Anchor Work", "Anchor description.")
    wrong = manga(71202, "Foreign Work", "Foreign description.")
    eid = s._create_entry(anchor, "v71-clean-a")
    s._upsert_match(eid, "v71-clean-a", anchor, 1.0, "auto", False)
    s._upsert_match(eid, "v71-clean-b", wrong, 1.0, "auto", False)
    s.add_title(eid, "Stale Foreign Alias", title_type="matched-alias", source_id="legacy", source_manga_id=999999)
    s.add_title(eid, "Stale Translation", title_type="translated-literal", source_id="translate", source_manga_id=0)

    out = s.prepare_entry_automation_redo(eid)
    ids = [int(x) for x in out["entry_ids"]]
    marks = ",".join("?" for _ in ids)
    with db.catalog() as c:
        rows = c.execute(
            f"SELECT title,title_type FROM titles WHERE entry_id IN ({marks})",
            ids,
        ).fetchall()
    values = {(str(r["title"]), str(r["title_type"])) for r in rows}
    assert ("Stale Foreign Alias", "matched-alias") not in values
    assert ("Stale Translation", "translated-literal") not in values


def test_v071_version_and_changelog():
    r = root()
    # Later releases supersede the live version/cache busters; keep this regression
    # focused on the historical v0.2.71 behavior/mapping remaining documented.
    changelog = (r / "CHANGELOG.md").read_text()
    assert "## 0.2.71 - 2026-08-28" in changelog
    assert "0.2.71 → 1.4.2071" in changelog
    assert "consensus" in changelog.lower()
    assert "redo-anchor" in changelog
