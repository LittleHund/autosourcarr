import asyncio
import json
import os
import tempfile
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ.setdefault("DATA_DIR", _TEST_DATA.name)
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")

from app.db import db, now_iso
from app.metadata_catalog import metadata_catalog
from app.service import AutoSourcarrService


def root() -> Path:
    return Path(__file__).resolve().parents[2]


def orphan(service: AutoSourcarrService, title: str) -> int:
    return service._create_entry(
        {
            "id": 0,
            "title": title,
            "url": "",
            "thumbnailUrl": "",
            "description": "",
            "author": "",
            "artist": "",
            "genre": [],
            "status": 0,
            "initialized": False,
            "realUrl": "",
        },
        "v83-orphan",
    )


def identity(external_id: str, title: str, *, aliases=None, description="", author=""):
    titles = [{"title": title, "language": "en", "type": "primary"}]
    for alias in aliases or []:
        titles.append({"title": alias, "language": "", "type": "alias"})
    return {
        "provider": "anilist",
        "provider_name": "AniList",
        "identity_scope": "work",
        "external_id": external_id,
        "url": f"https://anilist.invalid/{external_id}",
        "title": title,
        "titles": titles,
        "description": description,
        "cover_url": "https://images.invalid/time-roulette.jpg",
        "status": "completed",
        "author": author,
        "artist": author,
        "genres": ["Fantasy"],
        "media_type": "MANGA",
        "country": "KR",
        "year": 2021,
        "adult": False,
        "links": {},
        "raw": {},
    }


def test_release_version_and_startup_orphan_repair_hook_exist():
    r = root()
    version = (r / "VERSION").read_text().strip()
    assert (r / "autosourcarr/VERSION").read_text().strip() == version
    html = (r / "autosourcarr/app/static/index.html").read_text()
    main = (r / "autosourcarr/app/main.py").read_text()
    service = (r / "autosourcarr/app/service.py").read_text()
    assert f"/static/app.js?v={version}" in html
    assert f"/static/style.css?v={version}" in html
    assert "repair_orphan_identity_metadata_upgrade" in service
    assert "await service.repair_orphan_identity_metadata_upgrade()" in main


def test_title_only_orphan_accepts_unique_exact_metadata_identity(monkeypatch):
    service = AutoSourcarrService()
    entry_id = orphan(service, "Time Roulette")
    candidate = identity(
        "138576",
        "Time Roulette",
        aliases=["타임룰렛"],
        description="A detailed canonical synopsis for the Time Roulette work that is long enough to populate the sparse entry.",
        author="Author Example",
    )

    monkeypatch.setattr(metadata_catalog, "enabled_provider_keys", lambda: ["anilist"])

    async def fake_search(provider, query, force=False):
        assert provider == "anilist"
        assert query == "Time Roulette"
        assert force is True
        return [candidate]

    monkeypatch.setattr(service, "_metadata_provider_search", fake_search)
    result = asyncio.run(service.resolve_entry_identity(entry_id, force=True))

    assert result["matched"] == 1
    assert result["bootstrap_title"] == "Time Roulette"
    resolved = service.get_entry(entry_id)
    assert any(x["provider"] == "anilist" and x["external_id"] == "138576" for x in resolved["identities"])
    assert "detailed canonical synopsis" in resolved["description"].lower()
    assert resolved["thumbnail_url"] == "https://images.invalid/time-roulette.jpg"
    assert any(x["title"] == "타임룰렛" for x in resolved["titles"])


def test_title_only_orphan_rejects_fuzzy_and_ambiguous_bootstrap(monkeypatch):
    service = AutoSourcarrService()
    fuzzy_id = orphan(service, "Time Roulette V83 Fuzzy")
    ambiguous_id = orphan(service, "Time Roulette V83 Ambiguous")
    monkeypatch.setattr(metadata_catalog, "enabled_provider_keys", lambda: ["anilist"])

    async def fuzzy_search(provider, query, force=False):
        return [identity("fuzzy", "Time Roulette V83 Fuzz")]

    monkeypatch.setattr(service, "_metadata_provider_search", fuzzy_search)
    fuzzy = asyncio.run(service.resolve_entry_identity(fuzzy_id, force=True))
    assert fuzzy["matched"] == 0
    assert service.get_entry(fuzzy_id)["identities"] == []

    async def ambiguous_search(provider, query, force=False):
        return [
            identity("amb-a", "Time Roulette V83 Ambiguous"),
            identity("amb-b", "Time Roulette V83 Ambiguous"),
        ]

    monkeypatch.setattr(service, "_metadata_provider_search", ambiguous_search)
    ambiguous = asyncio.run(service.resolve_entry_identity(ambiguous_id, force=True))
    assert ambiguous["matched"] == 0
    assert ambiguous["ambiguous_bootstrap_providers"] == ["anilist"]
    assert service.get_entry(ambiguous_id)["identities"] == []


def test_provider_failures_are_not_cached_as_empty_results(monkeypatch):
    service = AutoSourcarrService()
    query = "V83 Provider Failure Cache Sentinel"
    with db.catalog() as c:
        c.execute(
            "DELETE FROM metadata_query_cache WHERE provider=? AND query_key=?",
            ("anilist", service._metadata_query_key(query)),
        )

    async def fail(*args, **kwargs):
        raise RuntimeError("temporary provider outage")

    monkeypatch.setattr(metadata_catalog, "search_provider", fail)
    with pytest.raises(RuntimeError, match="temporary provider outage"):
        asyncio.run(service._metadata_provider_search("anilist", query, force=True))
    assert service._metadata_cache_read("anilist", query) is None


def test_old_empty_metadata_cache_expires_quickly():
    service = AutoSourcarrService()
    query = "V83 Empty Cache TTL Sentinel"
    key = service._metadata_query_key(query)
    old = (datetime.now(timezone.utc) - timedelta(hours=1)).isoformat()
    with db.catalog() as c:
        c.execute(
            """INSERT INTO metadata_query_cache(provider,query_key,query_text,results_json,updated_at)
               VALUES(?,?,?,?,?)
               ON CONFLICT(provider,query_key) DO UPDATE SET results_json=excluded.results_json,updated_at=excluded.updated_at""",
            ("anilist", key, query, json.dumps([]), old),
        )
    assert service._metadata_cache_read("anilist", query) is None


def test_exact_bootstrap_metadata_can_only_corrobate_first_source_anchor():
    service = AutoSourcarrService()
    entry_id = orphan(service, "Time Roulette")
    summary = (
        "A detailed source-independent synopsis about a roulette that manipulates time, "
        "with enough specific shared text to corroborate the first source attachment safely."
    )
    candidate = identity("v83-bootstrap", "Time Roulette", aliases=["Chrono Wheel"], description=summary, author="V83 Author")
    service._upsert_identity_link(
        entry_id,
        candidate,
        1.0,
        "metadata:bootstrap-exact preferred title: Time Roulette ↔ Time Roulette",
    )
    service._choose_metadata(entry_id)
    entry = service.get_entry(entry_id)

    exact_source = {
        "title": "Time Roulette",
        "description": summary,
        "author": "V83 Author",
    }
    exact_evidence = service._source_match_evidence(entry, exact_source)
    assert exact_evidence["eligible"] is True
    assert exact_evidence["summary_similarity"] >= 0.82

    # Provider aliases are search hints only; they cannot become automatic source
    # authority even though the accepted identity also knows this alias and summary.
    alias_source = {
        "title": "Chrono Wheel",
        "description": summary,
        "author": "V83 Author",
    }
    alias_evidence = service._source_match_evidence(entry, alias_source)
    assert alias_evidence["eligible"] is False


def test_orphan_upgrade_targets_only_source_less_identity_less_rows(monkeypatch):
    service = AutoSourcarrService()
    orphan_id = orphan(service, "V83 Repair Target Unique")
    source_backed_id = service._create_entry(
        {
            "id": 983001,
            "title": "V83 Source Backed Unique",
            "url": "/v83/983001",
            "thumbnailUrl": "",
            "description": "source backed description",
            "author": "",
            "artist": "",
            "genre": [],
            "status": 1,
            "initialized": True,
            "realUrl": "",
        },
        "v83-source",
    )
    service._upsert_match(
        source_backed_id,
        "v83-source",
        {
            "id": 983001,
            "title": "V83 Source Backed Unique",
            "url": "/v83/983001",
            "thumbnailUrl": "",
            "description": "source backed description",
            "author": "",
            "artist": "",
            "genre": [],
            "status": 1,
            "initialized": True,
            "realUrl": "",
        },
        1.0,
        "auto",
    )

    called = []

    async def fake_resolve(eid, force=False):
        called.append((eid, force))
        return {"entry_id": eid, "matched": 1 if eid == orphan_id else 0, "provider_errors": {}}

    monkeypatch.setattr(service, "resolve_entry_identity", fake_resolve)
    result = asyncio.run(service.repair_orphan_identity_metadata_upgrade(force=True, limit=20000))
    assert (orphan_id, True) in called
    assert all(eid != source_backed_id for eid, _ in called)
    assert result["candidates"] >= 1
