from __future__ import annotations

import ast
import uuid
from pathlib import Path

from app.db import db
from app.service import AutoSourcarrService, _chapter_release_is_official


def root() -> Path:
    return Path(__file__).resolve().parents[2]


def source_row(source_id: str, name: str, priority: int, *, base_url: str | None = None):
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_language,enabled_search,enabled_chapters,priority,last_seen,available,official,official_manual)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
            (source_id, name, name, "en", base_url or f"https://{source_id}.invalid", "", 1, 1, 1, 1, priority, "now", 1, 0, 0),
        )


def manga(mid: int, title: str):
    return {
        "id": mid,
        "title": title,
        "url": f"/m/{mid}",
        "thumbnailUrl": "",
        "description": "",
        "author": "A",
        "artist": "A",
        "genre": [],
        "status": 1,
        "initialized": True,
        "realUrl": "",
    }


def chapter(cid: int, number: float, name: str, scanlator: str = "", pages: int = 20):
    return {
        "id": cid,
        "url": f"/c/{cid}",
        "name": name,
        "uploadDate": 0,
        "chapterNumber": number,
        "scanlator": scanlator,
        "mangaId": 0,
        "sourceOrder": cid,
        "realUrl": "",
        "pageCount": pages,
    }


def test_v078_version_and_ui_contract():
    r = root()
    current = tuple(int(x) for x in (r / "VERSION").read_text().strip().split("."))
    assert current >= (0, 2, 78)
    assert tuple(int(x) for x in (r / "autosourcarr/VERSION").read_text().strip().split(".")) >= (0, 2, 78)
    assert 'orElse("' + ".".join(map(str, current)) + '")' in (r / "extension/module/build.gradle.kts").read_text()
    html = (r / "autosourcarr/app/static/index.html").read_text()
    js = (r / "autosourcarr/app/static/app.js").read_text()
    assert "/static/app.js?v=" + ".".join(map(str, current)) in html
    assert "/static/style.css?v=" + ".".join(map(str, current)) in html
    assert 'id="matrixTitleState"' in html and 'id="subbedTitleState"' in html
    assert "Manually renamed" in html
    assert "Remove manual titles" in js
    assert "chapterServingSource" in js and "chapterServingCandidates" in js and "chapterServingEntry" in js
    # Official/community state is source-derived; the source management table no longer exposes a user toggle.
    assert "<th>Official</th>" not in js
    assert "updateSourceGroup(${i},'official'" not in js
    assert "0.2.78 → 1.4.2078" in (r / "CHANGELOG.md").read_text()


def test_main_title_follows_largest_bound_source_title_until_manually_pinned():
    s = AutoSourcarrService()
    suffix = uuid.uuid4().hex[:8]
    s1, s2, s3 = (f"title-a-{suffix}", f"title-b-{suffix}", f"title-c-{suffix}")
    source_row(s1, f"Source A {suffix}", 10)
    source_row(s2, f"Source B {suffix}", 20)
    source_row(s3, f"Source C {suffix}", 30)
    title_a = f"Minority Title {suffix}"
    title_b = f"Consensus Title {suffix}"
    eid, _, _ = s.ingest_manga(s1, manga(780001, title_a))
    s._upsert_match(eid, s2, manga(780002, title_b), 1.0)
    s._upsert_match(eid, s3, manga(780003, title_b), 1.0)
    assert s.get_entry(eid)["preferred_title"] == title_b

    manual = f"My Manual Title {suffix}"
    s.set_preferred_title(eid, manual)
    s.add_title(eid, f"Manual Alias {suffix}", title_type="manual")
    # Bound-title refresh cannot overwrite a manual main title.
    assert s._sync_preferred_title_from_bound_records(eid) == manual
    assert s.get_entry(eid)["preferred_title"] == manual

    cleared = s.clear_manual_titles(eid)
    assert cleared["preferred_title"] == title_b
    assert not int(cleared["manual_preferred_title"] or 0)
    assert all(t["title_type"] != "manual" for t in cleared["titles"])


def test_official_is_decided_per_chapter_from_source_metadata():
    s = AutoSourcarrService()
    suffix = uuid.uuid4().hex[:8]
    sid = f"mixed-release-{suffix}"
    source_row(sid, f"Mixed Release Source {suffix}", 10)
    title = f"Mixed Release Work {suffix}"
    eid, _, _ = s.ingest_manga(sid, manga(781001, title))
    s._upsert_source_variant(eid, sid, manga(781002, title), 1.0, "auto-additional", False)
    match_id = s._match_id(eid, sid)
    s._ingest_chapters(
        eid,
        {"source_id": sid, "source_name": f"Mixed Release Source {suffix}", "source_manga_id": 781001},
        match_id,
        [chapter(782001, 1, "Chapter 1 - Unofficial", "Fan Translation")],
    )
    s._ingest_chapters(
        eid,
        {"source_id": sid, "source_name": f"Mixed Release Source {suffix}", "source_manga_id": 781002},
        match_id,
        [chapter(782002, 1, "Chapter 1 - Official", "Official Translation")],
    )
    row = s.list_chapters(eid)[0]
    assert row["candidate_count"] == 2
    assert row["serving_candidate_id"]
    assert row["serving_official"] is True
    detail = s.chapter_details(row["id"])
    by_id = {int(c["source_chapter_id"]): c for c in detail["candidates"]}
    assert by_id[782001]["official"] is False
    assert by_id[782002]["official"] is True
    assert int(detail["would_serve_candidate_id"]) == int(by_id[782002]["id"])


def test_source_record_title_can_distinguish_official_release():
    assert _chapter_release_is_official(
        {"source_id": "mixed", "name": "Chapter 1", "scanlator": "", "source_entry_title": "Example - Official Translation"},
        set(),
    )
    assert not _chapter_release_is_official(
        {"source_id": "mixed", "name": "Chapter 1", "scanlator": "", "source_entry_title": "Example - Unofficial"},
        set(),
    )


def test_explicit_unofficial_overrides_inherently_official_source():
    source_ids = {"official-source"}
    assert _chapter_release_is_official(
        {"source_id": "official-source", "name": "Chapter 1", "scanlator": ""}, source_ids
    ) is True
    assert _chapter_release_is_official(
        {"source_id": "official-source", "name": "Chapter 1 - Unofficial", "scanlator": "Fan TL"}, source_ids
    ) is False


def test_v078_python_files_parse():
    r = root()
    for rel in ["autosourcarr/app/db.py", "autosourcarr/app/models.py", "autosourcarr/app/service.py", "autosourcarr/app/main.py"]:
        ast.parse((r / rel).read_text())
