import os
import tempfile
from pathlib import Path

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ["DATA_DIR"] = _TEST_DATA.name
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")

from app.db import db
from app.service import AutoSourcarrService


def source_row(source_id: str, name: str, priority: int):
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_language,enabled_search,enabled_chapters,priority,last_seen,available)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,1)""",
            (source_id, name, name, "en", "https://example.invalid", "", 1, 1, 1, 1, priority, "now"),
        )


def manga(mid: int, title: str, description: str = ""):
    return {
        "id": mid,
        "title": title,
        "url": f"/manga/{mid}",
        "thumbnailUrl": "",
        "description": description,
        "author": "",
        "artist": "",
        "genre": ["Action"],
        "status": 1,
        "initialized": True,
        "realUrl": "",
    }


def test_historical_snowball_repair_does_not_require_entry_alias_history():
    s = AutoSourcarrService()
    titles = [
        "Disastrous Necromancer",
        "God of Blackfield",
        "Infinite Leveling Murim",
        "The Greatest Estate Developer",
        "Hero Has Returned",
        "Doom Breaker",
        "Return of the Frozen Player",
        "Legend of the Northern Blade",
    ]
    ids = []
    for i, title in enumerate(titles):
        sid = f"v63-repair-{i}"
        source_row(sid, sid, i + 1)
        payload = manga(63000 + i, title, f"A unique synopsis for {title}; unrelated setting, cast, powers and events number {i}.")
        eid = s._create_entry(payload, sid)
        s._upsert_match(eid, sid, payload, 1.0)
        ids.append(eid)
    canonical = ids[0]
    for other in ids[1:]:
        canonical = s.merge_entries(canonical, other, "automatic duplicate test")

    # Simulate damage from a release/migration that no longer has usable alias
    # tombstones. v0.2.62 incorrectly depended on this history to even inspect it.
    with db.catalog() as c:
        c.execute("DELETE FROM entry_aliases")
    assert len(s.get_entry(canonical)["matches"]) == 8

    result = s._repair_unsafe_automatic_merges()
    assert result["scanned_candidates"] >= 1
    assert result["repaired_entries"] >= 1
    assert result["split_records"] >= 6
    assert len(s.get_entry(canonical)["matches"]) <= 2


def test_inspector_metadata_and_sources_are_independent_collapsible_sections():
    # 0.3.10 intentionally replaces the old two-column stack with the user's
    # requested order. The executable DOM regressions additionally cover loading.
    root = Path(__file__).resolve().parents[1]
    css = (root / "app/static/workbench.css").read_text(encoding="utf-8")
    js = (root / "app/static/app.js").read_text(encoding="utf-8")
    renderer = js.split("async function renderInlineEntry(", 1)[1].split("window.closeInlineEntry=", 1)[0]
    markers = ["entryDetailSection('titles'", "entryDetailSection('tags'",
               "entryDetailSection('metadata'", "entryDetailSection('matches'",
               "entryChapterSections(kind)"]
    positions = [renderer.index(marker) for marker in markers]
    assert positions == sorted(positions)
    assert '<details class="card spaced entryDetailSection"' in js
    assert ".entryDetailContent { min-width:0;" in css
    assert "data-identity-list" in renderer and "sourceInspectorList" in renderer


def test_import_live_search_keeps_discovered_entry_ids_and_avoids_alias_cross_product_batches():
    import inspect
    source = inspect.getsource(AutoSourcarrService.import_item_live_suggestions)
    assert "live_candidate_ids" in source
    assert "strict_candidate_ids" in source
    assert "for alias_query in secondary:" in source
    assert '{"source_id": sid, "query": alias_query}' in source
    assert "for q in secondary" not in source
    assert "secondary = alias_queries[:10]" in source


def test_v063_repair_marker_blocks_duplicate_automation_until_new_repair_runs():
    import inspect
    from app import main as main_module

    service_source = inspect.getsource(AutoSourcarrService.schedule_automatic_duplicate_matching)
    main_source = inspect.getsource(main_module.lifespan)
    assert '!= "0.2.69"' in service_source
    assert '"Repair unsafe duplicate merges"' in main_source
    assert 'automatic_merge_safety_repair_version' in main_source

def test_live_discovered_candidate_is_strictly_evaluated_even_if_local_reranker_omits_it(monkeypatch):
    import asyncio
    from app import service as service_module

    s = AutoSourcarrService()
    source_row("v63-live", "Live Source", 1)
    s._reload_source_cache()
    s._create_import_run("run-v63-live", "v63-live.tachibk")
    item = {
        "source": 999999,
        "sourceName": "Missing Original",
        "url": "/missing/original",
        "title": "Direct Live Candidate",
    }
    item_id = s._store_import_item(
        "run-v63-live", 1, item,
        state="unresolved", reason="not found", entry_id=None,
        translation_checked=True, suggestion_checked=False, generate_candidates=False,
    )
    decoy = s._create_entry(manga(63990, "Unrelated Local Decoy"), "v63-live")
    target = {"id": None}
    original_ingest = s.ingest_manga

    def track_ingest(source_id, payload):
        result = original_ingest(source_id, payload)
        if str(payload.get("title") or "") == "Direct Live Candidate":
            target["id"] = int(result[0])
        return result

    async def fake_fetch(source_id, mode, page, query):
        if query == "Direct Live Candidate":
            return {"mangas": [manga(63991, "Direct Live Candidate")]}
        return {"mangas": []}

    def fake_suggestions(import_id, current_item_id, query=""):
        row = s._entry_summary(decoy)
        row["import_score"] = 0.25
        return {"query": query or "Direct Live Candidate", "english_title": "", "suggestions": [row]}

    async def fake_strict(item, *, english_title="", reference_entry_id=None, limit=16, candidate_ids=None):
        ids = [int(x) for x in (candidate_ids or [])]
        if target["id"] is not None and int(target["id"]) in ids:
            return int(target["id"]), "direct live shortlist"
        return None, ""

    monkeypatch.setattr(service_module.source_host, "fetch_source_manga", fake_fetch)
    monkeypatch.setattr(s, "ingest_manga", track_ingest)
    monkeypatch.setattr(s, "import_item_suggestions", fake_suggestions)
    monkeypatch.setattr(s, "_import_auto_resolve_candidate_async", fake_strict)
    monkeypatch.setattr(s, "schedule_source_metadata_hydration", lambda *args, **kwargs: None)

    result = asyncio.run(s.import_item_live_suggestions(
        "run-v63-live", item_id, "Direct Live Candidate", automatic=True,
    ))
    assert target["id"] is not None
    assert int(result["auto_match_entry_id"]) == int(target["id"])
    with db.catalog() as c:
        stored = c.execute("SELECT candidate_ids_json FROM import_items WHERE id=?", (item_id,)).fetchone()[0]
    assert str(target["id"]) in str(stored)
