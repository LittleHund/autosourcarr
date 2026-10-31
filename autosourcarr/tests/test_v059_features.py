import inspect
import os
import tempfile
from pathlib import Path

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ["DATA_DIR"] = _TEST_DATA.name
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")

from app.config import settings
from app.db import db
from app.service import AutoSourcarrService


def source_row(source_id: str, name: str, priority: int = 10):
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_search,enabled_chapters,priority,last_seen,available)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,1)""",
            (source_id, name, name, "en", "https://example.invalid", "", 1, 1, 1, priority, "now"),
        )


def manga(mid: int, title: str, description: str = "", author: str = "A"):
    return {
        "id": mid, "title": title, "url": f"/manga/{mid}", "thumbnailUrl": "",
        "description": description, "author": author, "artist": author,
        "genre": ["Action"], "status": 1, "initialized": True, "realUrl": "",
    }


def test_exact_import_title_is_not_vetoed_by_creator_romanization_difference():
    s = AutoSourcarrService()
    source_row("v59-author", "Author source")
    record = manga(
        59001,
        "Latna Saga: Survival of a Sword King",
        "A sufficiently descriptive synopsis for the same fantasy work and its protagonist.",
        author="Lim Kyung-bae",
    )
    eid = s._create_entry(record, "v59-author")
    s._upsert_match(eid, "v59-author", record, 1.0)
    evidence = s._import_candidate_evidence({
        "title": "Latna Saga: Survival of a Sword King",
        "description": "A different backup synopsis can still be incomplete.",
        "author": "YKB",
    }, eid)
    assert evidence["eligible"] is True
    assert evidence["title_score"] >= 0.94


def test_metadata_identity_exact_title_is_not_vetoed_by_creator_name_difference():
    s = AutoSourcarrService()
    item = {
        "title": "Danwu Supreme",
        "description": "",
        "author": "Tang Jia San Shao",
    }
    candidate = {
        "title": "Danwu Supreme",
        "titles": [{"title": "Danwu Supreme"}],
        "description": "",
        "author": "Different Romanized Pen Name",
    }
    score, reason = s._import_metadata_candidate_score(item, candidate)
    assert score >= 0.94, reason


def test_live_shortlist_is_used_directly_by_strict_import_resolver():
    s = AutoSourcarrService()
    source_row("v59-shortlist-a", "Shortlist A", 10)
    source_row("v59-shortlist-b", "Shortlist B", 20)
    wanted = manga(59011, "Devil's Advent", "Exact target summary", "A")
    unrelated = manga(59012, "Completely Different Work", "Unrelated", "B")
    wanted_id = s._create_entry(wanted, "v59-shortlist-a"); s._upsert_match(wanted_id, "v59-shortlist-a", wanted, 1.0)
    other_id = s._create_entry(unrelated, "v59-shortlist-b"); s._upsert_match(other_id, "v59-shortlist-b", unrelated, 1.0)
    chosen, reason = s._import_auto_resolve_candidate(
        {"title": "Devil's Advent", "description": "", "author": "Different pen name"},
        candidate_ids=[wanted_id],
    )
    assert chosen == wanted_id, reason
    chosen_other, _ = s._import_auto_resolve_candidate(
        {"title": "Devil's Advent", "description": ""}, candidate_ids=[other_id]
    )
    assert chosen_other is None


def test_automatic_import_search_uses_more_than_two_metadata_aliases_and_does_not_stop_on_review_score():
    src = inspect.getsource(AutoSourcarrService.import_item_live_suggestions)
    assert "secondary = alias_queries[:10]" in src
    assert "same title which succeeds instantly in Manual Search" in src
    assert "Only stop when the actual auto-resolver" in src
    assert "auto_match_entry_id" in src


def test_automatic_duplicate_requests_are_coalesced_for_followup_sweep():
    s = AutoSourcarrService()
    class Running:
        def done(self):
            return False
    s._refresh_tasks["__automatic-duplicate-matching__"] = Running()
    s._automatic_duplicate_rescan_requested = False
    assert s.schedule_automatic_duplicate_matching(delay=0) is False
    assert s._automatic_duplicate_rescan_requested is True


def test_duplicate_automation_default_summary_threshold_is_seventy_percent():
    assert abs(float(settings.duplicate_auto_merge_summary_threshold) - 0.70) < 1e-9


def test_text_selection_blocks_periodic_surface_replacement():
    js = Path(__file__).resolve().parents[1] / "app" / "static" / "app.js"
    text = js.read_text(encoding="utf-8")
    assert "function textSelectionIntersectsSurface(surface)" in text
    assert "window.getSelection" in text
    assert "if(textSelectionIntersectsSurface(surface))return true;" in text
    assert "document.addEventListener('selectionchange'" in text
    assert "requestSurfaceRender('imports')" in text
    assert "requestSurfaceRender('tasks')" in text
    assert "if(structuralRenderBlocked('imports')){scheduleImportsPoll();return;}" in text


def test_v059_requeues_existing_unresolved_imports():
    src = inspect.getsource(AutoSourcarrService.schedule_import_candidate_pipeline_upgrade)
    assert 'version = "0.2.84"' in src


def test_automatic_duplicate_sweep_enriches_shortlisted_metadata_before_merge():
    src = inspect.getsource(AutoSourcarrService._hydrate_duplicate_identity_candidates)
    runner = inspect.getsource(AutoSourcarrService.schedule_automatic_duplicate_matching)
    assert "self._duplicate_shortlist()" in src
    assert "resolve_entry_identity" in src
    assert "Resolving metadata identities for duplicate candidates" in runner
    assert "_hydrate_duplicate_thumbnail_fingerprints" in runner
