"""Live-shaped source recovery fixtures; no external source or production DB."""
import asyncio
import json
from unittest.mock import AsyncMock

import pytest
from app import service as module
from app.config import settings
from app.db import Database, now_iso
from app.metadata_catalog import metadata_catalog
from app.service import AutoSourcarrService
from app.task_registry import TaskRegistry


@pytest.fixture
def env(monkeypatch, tmp_path):
    monkeypatch.setattr(settings, "data_dir", tmp_path)
    monkeypatch.setattr(settings, "database_dir", "")
    for key, value in {"metadata_identity_enabled": False, "metadata_identity_on_ingest": False,
                       "exact_thumbnail_matching": False, "automatic_duplicate_matching": False,
                       "auto_missing_source_discovery": True, "color_detection_enabled": False}.items():
        if hasattr(settings, key):
            monkeypatch.setattr(settings, key, value)
    database = Database()
    monkeypatch.setattr(module, "db", database)
    monkeypatch.setattr(module, "task_registry", TaskRegistry())
    service = AutoSourcarrService()
    for name in ("schedule_source_metadata_hydration", "schedule_entry_identity", "schedule_duplicate_snapshot_refresh"):
        monkeypatch.setattr(service, name, lambda *a, **k: False)
    monkeypatch.setattr(service, "_wait_for_background_idle", AsyncMock())
    monkeypatch.setattr(service, "_automatic_color_gate", AsyncMock(return_value=(True, "unknown", {})))
    yield service, database
    database.close_current_thread()


def source(db, sid):
    with db.catalog() as c:
        c.execute("INSERT INTO source_config(source_id,name,last_seen,priority) VALUES(?,?,?,?)", (sid, sid, now_iso(), 10))


def manga(mid, title="Monster List", *, author="Example Writer", description=None):
    return {"id": mid, "title": title, "url": f"/work/{mid}", "thumbnailUrl": "", "author": author,
            "artist": "", "description": description if description is not None else "An unusual archive chronicles spirits sharing a city with humans while a student uncovers their forgotten history.",
            "genre": [], "status": 1, "initialized": True, "realUrl": ""}


def entry(service, sid, mid, title="Monster List", attach=True):
    data = manga(mid, title)
    eid = service._create_entry(data if attach else manga(0, title, author="", description=""), sid)
    if attach:
        service._upsert_match(eid, sid, data, 1.0, "known")
    return eid


def setup_hit(env, monkeypatch, *, details=None, cached=False, owner=False):
    s, db = env
    source(db, "anchor"); source(db, "candidate")
    eid = entry(s, "anchor", 8701)
    if owner:
        owner_id = entry(s, "candidate", 8702)
    else:
        owner_id = None
    thin = manga(8702, author="", description="")
    card = {**thin, "auto_match_eligible": False, "autosourcarr_match_score": 1.0,
            "title_match_score": 1.0, "cached_result": cached,
            "_live_evidence_manga": {} if cached else thin}
    monkeypatch.setattr(s, "search_source_for_manual", AsyncMock(return_value={
        "mangas": [card], "warnings": [], "live_result_count": 0 if cached else 1}))
    calls = []
    async def fetch(mid, fetch_manga=True, fetch_chapters=True):
        calls.append((mid, fetch_manga, fetch_chapters))
        if details == "error":
            raise RuntimeError("temporary details failure")
        return {"manga": details or manga(mid), "chapters": [{"id": 87991, "chapterNumber": 1, "name": "Chapter 1", "url": "/1"}] if fetch_chapters else []}
    monkeypatch.setattr(module.source_host, "fetch_manga_and_chapters", fetch)
    return s, db, eid, calls, owner_id


def test_thin_generic_hit_is_hydrated_before_author_gate_and_chapters(env, monkeypatch):
    s, db, eid, calls, _ = setup_hit(env, monkeypatch)
    result = asyncio.run(s.discover_missing_source_matches(eid, "candidate"))
    assert result["added"] == 1
    assert calls == [(8702, True, False), (8702, False, True)]
    assert s.get_entry(eid)["entry_match_count"] == 2
    assert s.source_discovery_outcomes([eid])[0]["state"] == "matched"


def test_hydrated_author_conflict_rejects_without_chapter_fetch(env, monkeypatch):
    s, _, eid, calls, _ = setup_hit(env, monkeypatch, details=manga(8702, author="Yamamoto Qwxyz"))
    result = asyncio.run(s.discover_missing_source_matches(eid, "candidate"))
    assert result["added"] == 0 and calls == [(8702, True, False)]
    assert s.source_discovery_outcomes([eid])[0]["state"] == "verification"


def test_cached_card_cannot_authorize_after_failed_live_details(env, monkeypatch):
    s, _, eid, calls, _ = setup_hit(env, monkeypatch, cached=True, details="error")
    result = asyncio.run(s.discover_missing_source_matches(eid, "candidate"))
    assert result["added"] == 0
    assert s.source_discovery_outcomes([eid])[0]["state"] == "unavailable"


def test_owned_record_is_not_merged_and_does_not_fetch_chapters(env, monkeypatch):
    s, _, eid, calls, owner = setup_hit(env, monkeypatch, owner=True)
    result = asyncio.run(s.discover_missing_source_matches(eid, "candidate"))
    assert result["added"] == 0 and result["blocked_candidates"] == 1
    assert s.canonical_entry_id(owner) == owner and s.canonical_entry_id(eid) == eid
    assert calls == [(8702, True, False)]
    outcome = s.source_discovery_outcomes([eid])[0]
    assert outcome["state"] == "owned" and outcome["candidate_entry_id"] == owner


def test_wrong_returned_source_id_is_not_attached(env, monkeypatch):
    s, _, eid, calls, _ = setup_hit(env, monkeypatch, details={**manga(8702), "sourceId": "other"})
    assert asyncio.run(s.discover_missing_source_matches(eid, "candidate"))["added"] == 0
    assert len(calls) == 1


def test_chapter_failure_does_not_masquerade_as_successfully_empty_scan(env, monkeypatch):
    s, db, eid, calls, _ = setup_hit(env, monkeypatch)
    async def fetch(mid, fetch_manga=True, fetch_chapters=True):
        if fetch_chapters:
            raise RuntimeError("chapter fetch unavailable")
        return {"manga": manga(mid), "chapters": []}
    monkeypatch.setattr(module.source_host, "fetch_manga_and_chapters", fetch)
    assert asyncio.run(s.discover_missing_source_matches(eid, "candidate"))["added"] == 1
    with db.origins() as c:
        row = c.execute("SELECT error FROM source_record_scans WHERE entry_id=? AND source_id='candidate'", (eid,)).fetchone()
    assert "chapter fetch unavailable" in row["error"]


def test_promising_detail_fetches_are_bounded(env, monkeypatch):
    s, db = env; source(db, "target")
    eid = entry(s, "anchor", 0, attach=False)
    cards = [{**manga(100+x), "autosourcarr_match_score": 1.0, "_live_evidence_manga": {}} for x in range(30)]
    monkeypatch.setattr(s, "search_source_for_manual", AsyncMock(return_value={"mangas": cards, "live_result_count": 30}))
    fetch = AsyncMock(return_value={"manga": manga(100, "Entirely Unrelated Work"), "chapters": []})
    monkeypatch.setattr(module.source_host, "fetch_manga_and_chapters", fetch)
    assert asyncio.run(s.discover_missing_source_matches(eid, "target"))["added"] == 0
    assert fetch.await_count == 3


def test_partial_live_results_do_not_trip_source_failure(env, monkeypatch):
    s, db = env; source(db, "partial")
    eid = entry(s, "anchor", 0, attach=False)
    monkeypatch.setattr(s, "search_source_for_manual", AsyncMock(return_value={"mangas": [], "live_result_count": 1, "warnings": ["one alias failed"]}))
    seen = []
    monkeypatch.setattr(s, "_record_source_result", lambda sid, error: seen.append((sid, error)))
    asyncio.run(s.discover_missing_source_matches(eid, "partial"))
    assert seen == [("partial", None)]


def test_capacity_deferred_does_not_record_upstream_failure(env, monkeypatch):
    s, db = env; source(db, "busy")
    eid = entry(s, "anchor", 0, attach=False)
    monkeypatch.setattr(s, "search_source_for_manual", AsyncMock(return_value={"mangas": [], "deferred_count": 1, "live_result_count": 0}))
    seen=[];monkeypatch.setattr(s,"_record_source_result",lambda *a:seen.append(a))
    asyncio.run(s.discover_missing_source_matches(eid,"busy"))
    assert seen == []
    assert s.source_discovery_outcomes([eid])[0]["state"] == "deferred"


def test_priority_repair_commits_first_wave_and_skips_remaining_sources(env, monkeypatch):
    s, db, eid, calls, _ = setup_hit(env, monkeypatch)
    for n in range(6): source(db, f"zextra-{n}")
    monkeypatch.setattr(settings, "source_host_work_concurrency", 2)
    result = asyncio.run(s.discover_missing_source_matches(eid, priority_rebuild=True))
    assert result["added"] == 1 and result["searched_sources"] == 1
    assert result["priority_rebuild_short_circuit"]


def identity(title, external_id="id", aliases=()):
    return {"provider": "anilist", "provider_name": "AniList", "identity_scope": "work", "external_id": external_id,
            "title": title, "titles": [{"title": title, "language": "en", "type": "primary"}]+[{"title": a, "language": "", "type": "alias"} for a in aliases],
            "author": "Example Writer", "description": manga(0)["description"], "genres": [], "status": "ongoing", "links": {}}


@pytest.mark.parametrize("ambiguous", [False, True])
def test_library_only_identity_bootstrap_requires_unique_exact_hit(env, monkeypatch, ambiguous):
    s, db = env
    monkeypatch.setattr(settings, "metadata_identity_enabled", True)
    eid = entry(s, "anchor", 0, attach=False)
    s.add_title(eid, "Monster List", title_type="main-library")
    monkeypatch.setattr(metadata_catalog, "enabled_provider_keys", lambda: ["anilist"])
    candidates = [identity("Monster List")]
    if ambiguous: candidates.append(identity("Monster List", "other"))
    monkeypatch.setattr(s, "_metadata_provider_search", AsyncMock(return_value=candidates))
    result = asyncio.run(s.resolve_entry_identity(eid, force=True))
    assert result["matched"] == (0 if ambiguous else 1)
    if not ambiguous:
        assert s._source_match_evidence(s.get_entry(eid), manga(1))["eligible"]


def test_legacy_identity_is_revalidated_for_library_first_source(env, monkeypatch):
    s, db = env; monkeypatch.setattr(settings, "metadata_identity_enabled", True)
    eid = entry(s, "anchor", 0, attach=False)
    s.add_title(eid, "Monster List", title_type="main-library")
    s._upsert_identity_link(eid, identity("Monster List"), 1.0, "metadata:title 100%")
    monkeypatch.setattr(metadata_catalog, "enabled_provider_keys", lambda: ["anilist"])
    search = AsyncMock(return_value=[identity("Monster List")]);monkeypatch.setattr(s, "_metadata_provider_search", search)
    asyncio.run(s.discover_missing_source_matches(eid))
    assert search.await_count == 1
    assert s._identity_links(eid)[0]["match_type"].startswith("metadata:bootstrap-exact preferred title:")


def test_fuzzy_library_only_identity_is_not_bootstrapped(env, monkeypatch):
    s, _=env;monkeypatch.setattr(settings,"metadata_identity_enabled",True)
    eid=entry(s,"a",0,"The Long Example Work",attach=False);s.add_title(eid,"The Long Example Work",title_type="main-library")
    monkeypatch.setattr(metadata_catalog,"enabled_provider_keys",lambda:["anilist"])
    monkeypatch.setattr(s,"_metadata_provider_search",AsyncMock(return_value=[identity("The Long Example Works")]))
    assert asyncio.run(s.resolve_entry_identity(eid,force=True))["matched"]==0


def test_repair_is_only_for_source_less_rows_and_coalesces(env, monkeypatch):
    s, db=env;source(db,"a")
    covered=entry(s,"a",1);empty=entry(s,"a",0,"An Empty Subscribed Work",attach=False)
    async def run():
        gate=asyncio.Event();seen=[]
        async def discover(eid,**kw):
            seen.append((eid,kw));await gate.wait();return {"added":1}
        monkeypatch.setattr(s,"discover_missing_source_matches",discover)
        first=s.schedule_subbed_search_all([covered,empty],unmatched_only=True)
        second=s.schedule_subbed_search_all([covered,empty],unmatched_only=True)
        assert first["entries"]==1 and second["already_running"]
        gate.set();await asyncio.gather(*list(s._refresh_tasks.values()))
        assert len(seen)==1 and seen[0][0]==empty and seen[0][1]["priority_rebuild"]
    asyncio.run(run())


def test_renamed_orphan_cannot_use_stale_bootstrap_author(env):
    s, db = env
    eid = entry(s, "a", 0, "Old Work", attach=False)
    s._upsert_identity_link(eid, identity("Old Work"), 1.0, "metadata:bootstrap-exact preferred title: Old Work")
    with db.catalog() as c:
        c.execute("UPDATE entries SET preferred_title='New Work' WHERE id=?", (eid,))
    evidence=s._trusted_automatic_entry_evidence(eid)
    assert evidence["author"] == "" and evidence["descriptions"] == []
    assert not s._source_match_evidence(s.get_entry(eid), manga(10, "New Work"))["eligible"]


def test_detail_admission_is_reported_as_deferred_not_source_outage(env, monkeypatch):
    from app.suwayomi import SourceWorkDeferred
    s, db, eid, calls, _ = setup_hit(env, monkeypatch)
    monkeypatch.setattr(module.source_host, "fetch_manga_and_chapters", AsyncMock(side_effect=SourceWorkDeferred("private source-host capacity busy")))
    seen=[]; monkeypatch.setattr(s,"_record_source_result",lambda *a:seen.append(a))
    assert asyncio.run(s.discover_missing_source_matches(eid,"candidate"))["added"] == 0
    assert seen == []
    assert s.source_discovery_outcomes([eid])[0]["state"] == "deferred"
