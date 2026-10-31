from __future__ import annotations

import asyncio
import ast
import uuid
from pathlib import Path

from app.db import db
from app.service import AutoSourcarrService
from app.suwayomi import source_host


def root() -> Path:
    return Path(__file__).resolve().parents[2]


def add_source(source_id: str, priority: int = 50) -> None:
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_language,enabled_search,enabled_chapters,priority,last_seen,available)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)""",
            (source_id, source_id, source_id, "en", "https://example.invalid", "", 1, 1, 1, 1, priority, "now", 1),
        )


def manga(mid: int, title: str, *, source_id: str = "", thumb: str = "") -> dict:
    return {
        "id": mid,
        "sourceId": source_id,
        "title": title,
        "url": f"/m/{mid}",
        "thumbnailUrl": thumb,
        "description": "",
        "author": "",
        "artist": "",
        "genre": [],
        "status": 1,
        "initialized": True,
        "realUrl": "",
    }


def unused_entry_id() -> int:
    with db.catalog() as c:
        return int(c.execute("SELECT COALESCE(MAX(id),0)+1000 n FROM entries").fetchone()["n"])


def test_v080_release_and_ui_contract():
    r = root()
    assert tuple(map(int, (r / "VERSION").read_text().strip().split("."))) >= (0, 2, 80)
    html = (r / "autosourcarr/app/static/index.html").read_text()
    js = (r / "autosourcarr/app/static/app.js").read_text()
    assert "/static/app.js?v=" in html
    assert "/static/style.css?v=" in html
    assert "Separate entry" in js
    assert "allowManualCandidate" in js
    assert "0.2.80 → 1.4.2080" in (r / "CHANGELOG.md").read_text()


def test_reset_anchor_keeps_aliases_and_main_library_title_evidence():
    s = AutoSourcarrService()
    canonical = unused_entry_id()
    seeds = [
        {"entry_id": canonical + 1, "canonical_entry_id": canonical, "title": "Reset Anchor Work", "manual_title": False,
         "description": "", "thumbnail_url": "", "status": 0, "author": "", "artist": "", "genre": ""},
        {"entry_id": canonical + 2, "canonical_entry_id": canonical, "title": "Reset Anchor Work", "manual_title": False,
         "description": "", "thumbnail_url": "", "status": 0, "author": "", "artist": "", "genre": ""},
    ]
    assert s.restore_subbed_reset_seeds(seeds) == 1
    assert s.canonical_entry_id(canonical + 1) == canonical
    assert s.canonical_entry_id(canonical + 2) == canonical
    trusted = s._trusted_automatic_entry_evidence(canonical)
    assert "Reset Anchor Work" in trusted["titles"]
    with db.catalog() as c:
        assert c.execute("SELECT 1 FROM entries WHERE id=?", (canonical + 1,)).fetchone() is None
        assert c.execute("SELECT 1 FROM entries WHERE id=?", (canonical + 2,)).fetchone() is None


def test_reset_revalidates_all_old_source_hints_independently(monkeypatch):
    s = AutoSourcarrService()
    suffix = uuid.uuid4().hex[:8]
    sa, sb = f"v80-a-{suffix}", f"v80-b-{suffix}"
    add_source(sa, 10); add_source(sb, 20)
    canonical = unused_entry_id()
    title = f"Rebuild All Hints {suffix}"
    s.restore_subbed_reset_seeds([{
        "entry_id": canonical, "canonical_entry_id": canonical, "title": title, "manual_title": False,
        "description": "", "thumbnail_url": "", "status": 0, "author": "", "artist": "", "genre": "",
    }])

    async def fake_details(mid: int):
        sid = sa if mid == 810001 else sb
        return manga(mid, title, source_id=sid)

    monkeypatch.setattr(source_host, "source_manga_details", fake_details)
    result = asyncio.run(s._revalidate_reset_hints_for_entry(canonical, [
        {"source_id": sa, "source_manga_id": 810001},
        {"source_id": sb, "source_manga_id": 810002},
    ]))
    assert result["accepted"] == 2
    assert set(result["accepted_sources"]) == {sa, sb}
    assert {m["source_id"] for m in s.get_entry(canonical)["matches"]} == {sa, sb}


def test_separate_source_candidate_creates_different_work_and_persists_guard():
    s = AutoSourcarrService()
    suffix = uuid.uuid4().hex[:8]
    sa, sb = f"v80-target-{suffix}", f"v80-candidate-{suffix}"
    add_source(sa, 10); add_source(sb, 20)
    target, _, _ = s.ingest_manga(sa, manga(820001, f"Heart Leads {suffix}", source_id=sa))
    candidate = manga(820002, f"Follow Your Heart {suffix}", source_id=sb)
    result = asyncio.run(s.separate_source_record(target, sb, 820002, candidate=candidate, defer_hydration=True))
    other = int(result["separate_entry_id"])
    assert other and other != target
    assert s._is_manual_separate_record(target, sb, 820002)
    assert s._duplicate_review(target, other) == "not_duplicate"
    assert s._find_existing_by_source(sb, 820002) == other


def test_duplicate_shortlist_ignores_source_less_placeholder():
    s = AutoSourcarrService()
    suffix = uuid.uuid4().hex[:8]
    sid = f"v80-real-{suffix}"; add_source(sid)
    title = f"Source Backed Only {suffix}"
    real, _, _ = s.ingest_manga(sid, manga(830001, title, source_id=sid))
    ghost = s._create_entry(manga(830002, title, thumb="https://same.invalid/c.jpg"), "ghost")
    candidates = s.duplicate_candidates([ghost])
    assert ghost not in candidates
    assert real != ghost


def test_orphan_subscription_clone_repair_requires_strong_metadata():
    s = AutoSourcarrService()
    suffix = uuid.uuid4().hex[:8]
    sid = f"v80-orphan-{suffix}"; add_source(sid)
    title = f"Clone Repair {suffix}"; thumb = f"https://img.invalid/{suffix}.jpg"
    real, _, _ = s.ingest_manga(sid, manga(840001, title, source_id=sid, thumb=thumb))
    ghost = s._create_entry(manga(840002, title, thumb=thumb), "ghost")
    result = s._repair_subbed_orphan_aliases([ghost])
    assert result["aliased"] == 1
    assert s.canonical_entry_id(ghost) == real


def test_v080_python_files_parse():
    r = root()
    for rel in ["autosourcarr/app/db.py", "autosourcarr/app/models.py", "autosourcarr/app/service.py", "autosourcarr/app/main.py"]:
        ast.parse((r / rel).read_text())
