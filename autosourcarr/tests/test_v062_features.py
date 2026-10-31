import os
import tempfile

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ["DATA_DIR"] = _TEST_DATA.name
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")

from app.db import db, now_iso
from app.service import AutoSourcarrService


def source_row(source_id: str, name: str, priority: int):
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_search,enabled_chapters,priority,last_seen)
            VALUES(?,?,?,?,?,?,?,?,?,?,?)""",
            (source_id, name, name, "en", "https://example.invalid", "", 1, 1, 1, priority, "now"),
        )


def manga(mid: int, title: str, description: str = ""):
    return {
        "id": mid, "title": title, "url": f"/manga/{mid}", "thumbnailUrl": "",
        "description": description, "author": "", "artist": "", "genre": ["Action"],
        "status": 1, "initialized": True, "realUrl": "",
    }


def test_automatic_duplicate_merging_is_not_transitive():
    s = AutoSourcarrService()
    for i, sid in enumerate(("v62-a", "v62-b", "v62-c"), 1):
        source_row(sid, sid, i)
    a = s._create_entry(manga(62001, "Alpha Work"), "v62-a"); s._upsert_match(a, "v62-a", manga(62001, "Alpha Work"), 1.0)
    b = s._create_entry(manga(62002, "Bridge Work"), "v62-b"); s._upsert_match(b, "v62-b", manga(62002, "Bridge Work"), 1.0)
    c = s._create_entry(manga(62003, "Charlie Work"), "v62-c"); s._upsert_match(c, "v62-c", manga(62003, "Charlie Work"), 1.0)
    for eid, names in ((a, ("AB Alias One", "AB Alias Two")), (b, ("AB Alias One", "AB Alias Two", "BC Alias One", "BC Alias Two")), (c, ("BC Alias One", "BC Alias Two"))):
        for name in names:
            s.add_title(eid, name, title_type="manual")
    remap = s.consolidate_entries([a, b, c])
    assert remap[a] == remap[b]
    assert remap[c] != remap[a]
    with db.catalog() as conn:
        lineage = list(conn.execute("SELECT member_entry_id FROM automatic_merge_lineage WHERE canonical_entry_id=? ORDER BY member_entry_id", (remap[a],)))
    assert {int(row["member_entry_id"]) for row in lineage} == {a, b}


def test_summary_alone_is_not_automatic_merge_authority():
    s = AutoSourcarrService(); source_row("v62-sa", "SA", 10); source_row("v62-sb", "SB", 20)
    shared = "A long synopsis about a city, a wandering hunter, a ruined tower, and several companions whose journey continues through many dangerous regions."
    a = s._create_entry(manga(62101, "Alpha City Hunter", shared), "v62-sa"); s._upsert_match(a, "v62-sa", manga(62101, "Alpha City Hunter", shared), 1.0)
    b = s._create_entry(manga(62102, "Quantum Bakery", shared), "v62-sb"); s._upsert_match(b, "v62-sb", manga(62102, "Quantum Bakery", shared), 1.0)
    evidence = s._auto_merge_evidence(a, b)
    assert evidence["summary_similarity"] >= 0.99
    assert evidence["eligible"] is False


def test_entry_match_count_counts_concrete_records_not_only_sources():
    s = AutoSourcarrService(); source_row("v62-count-a", "Count A", 1); source_row("v62-count-b", "Count B", 2)
    e = s._create_entry(manga(62201, "Counted Work"), "v62-count-a")
    s._upsert_match(e, "v62-count-a", manga(62201, "Counted Work"), 1.0)
    s._upsert_source_variant(e, "v62-count-a", manga(62202, "Counted Work"), 1.0, "additional")
    s._upsert_match(e, "v62-count-b", manga(62203, "Counted Work"), 1.0)
    row = s._entry_summaries([e])[0]
    assert row["match_count"] == 2
    assert row["entry_match_count"] == 3


def test_legacy_automatic_snowball_repair_splits_clearly_foreign_records():
    s = AutoSourcarrService()
    titles = [
        "Disastrous Necromancer", "God of Blackfield", "Infinite Leveling Murim",
        "The Greatest Estate Developer", "Hero Has Returned", "Doom Breaker",
        "Return of the Frozen Player", "Legend of the Northern Blade",
    ]
    ids = []
    for i, title in enumerate(titles):
        sid = f"v62-repair-{i}"; source_row(sid, sid, i + 1)
        payload = manga(62300 + i, title, f"Unique synopsis for {title} with deliberately unrelated characters, setting, history and events number {i}.")
        eid = s._create_entry(payload, sid); s._upsert_match(eid, sid, payload, 1.0); ids.append(eid)
    canonical = ids[0]
    for other in ids[1:]:
        canonical = s.merge_entries(canonical, other, "description>=90%; summary 70%")
    assert len(s.get_entry(canonical)["matches"]) == 8
    result = s._repair_unsafe_automatic_merges()
    assert result["repaired_entries"] >= 1
    assert result["split_records"] >= 6
    assert len(s.get_entry(canonical)["matches"]) <= 2


def test_match_matrix_has_available_entry_match_count_filter():
    html = open("autosourcarr/app/static/index.html", encoding="utf-8").read()
    js = open("autosourcarr/app/static/app.js", encoding="utf-8").read()
    assert 'id="matrixEntryMatches"' in html
    assert "ensureAvailableEntryMatchOptions" in js
    assert "entry_match_count" in js
    assert "new Set((entries||[]).map" in js
