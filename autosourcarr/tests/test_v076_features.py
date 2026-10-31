from __future__ import annotations

import ast
import uuid
from pathlib import Path

from app.db import db
from app.service import AutoSourcarrService


def root() -> Path:
    return Path(__file__).resolve().parents[2]


def source_row(source_id: str, name: str, priority: int, *, official: bool = False):
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_language,enabled_search,enabled_chapters,priority,last_seen,available,official)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
            (source_id, name, name, "en", f"https://{source_id}.invalid", "", 1, 1, 1, 1, priority, "now", 1, int(official)),
        )


def manga(mid: int, title: str):
    return {"id": mid, "title": title, "url": f"/m/{mid}", "thumbnailUrl": "", "description": "", "author": "A", "artist": "A", "genre": [], "status": 1, "initialized": True, "realUrl": ""}


def chapter(cid: int, number: float, pages: int):
    return {"id": cid, "url": f"/c/{cid}", "name": f"Chapter {number:g}", "uploadDate": 0, "chapterNumber": number, "scanlator": "", "mangaId": 0, "sourceOrder": cid, "realUrl": "", "pageCount": pages}


def test_v076_release_history_and_ui_markers():
    r = root()
    current = tuple(int(x) for x in (r / "VERSION").read_text().strip().split("."))
    assert current >= (0, 2, 76)
    js = (r / "autosourcarr/app/static/app.js").read_text()
    assert "Source display groups" in (r / "autosourcarr/app/static/index.html").read_text()
    assert "Reject episode" in js
    assert "WILL SERVE" in js
    assert "source_entry_title" in js
    assert "0.2.76 → 1.4.2076" in (r / "CHANGELOG.md").read_text()


def test_source_groups_persist_and_are_returned_with_sources():
    s = AutoSourcarrService()
    suffix = uuid.uuid4().hex[:8]
    sid = f"group-src-{suffix}"
    source_row(sid, f"Drake Scans {suffix}", 40)
    group = s.create_source_group(f"Scans group {suffix}")
    s.update_source(sid, group_id=group["id"])
    row = s.get_source(sid)
    assert row["group_id"] == group["id"]
    assert row["group_name"] == group["name"]
    s.delete_source_group(group["id"])
    assert s.get_source(sid)["group_id"] is None


def test_official_candidate_is_preferred_over_community_when_both_are_valid():
    s = AutoSourcarrService()
    suffix = uuid.uuid4().hex[:8]
    community = f"community-{suffix}"
    official = f"official-{suffix}"
    source_row(community, "Community Scans", 1, official=False)
    source_row(official, "MANGA Plus", 500, official=True)
    eid, _, _ = s.ingest_manga(community, manga(710001, f"Official Preference {suffix}"))
    s._upsert_match(eid, official, manga(710002, f"Official Preference {suffix}"), 1.0)
    s._ingest_chapters(eid, {"source_id": community, "source_name": "Community Scans", "source_manga_id": 710001}, s._match_id(eid, community), [chapter(711001, 1, 30)])
    s._ingest_chapters(eid, {"source_id": official, "source_name": "MANGA Plus", "source_manga_id": 710002}, s._match_id(eid, official), [chapter(711002, 1, 30)])
    row = s.list_chapters(eid)[0]
    assert row["serving_source_id"] == official
    assert row["serving_official"] is True


def test_rejected_candidate_stays_inactive_after_reingest_and_can_be_restored():
    s = AutoSourcarrService()
    suffix = uuid.uuid4().hex[:8]
    sid = f"reject-{suffix}"
    source_row(sid, "Reject Source", 10)
    eid, _, _ = s.ingest_manga(sid, manga(720001, f"Reject Chapter {suffix}"))
    payload = [chapter(721001, 3, 25)]
    s._ingest_chapters(eid, {"source_id": sid, "source_name": "Reject Source", "source_manga_id": 720001}, s._match_id(eid, sid), payload)
    vid = s.list_chapters(eid)[0]["id"]
    candidate_id = s.chapter_details(vid)["candidates"][0]["id"]
    s.reject_chapter_candidate(candidate_id)
    assert s.chapter_details(vid)["candidates"] == []
    s._ingest_chapters(eid, {"source_id": sid, "source_name": "Reject Source", "source_manga_id": 720001}, s._match_id(eid, sid), payload)
    assert s.chapter_details(vid)["candidates"] == []
    s.restore_chapter_candidate(candidate_id)
    assert s.chapter_details(vid)["candidates"][0]["id"] == candidate_id


def test_v076_python_files_parse():
    r = root()
    for rel in ["autosourcarr/app/db.py", "autosourcarr/app/models.py", "autosourcarr/app/service.py", "autosourcarr/app/main.py"]:
        ast.parse((r / rel).read_text())
