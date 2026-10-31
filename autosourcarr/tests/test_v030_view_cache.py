import asyncio
import json
from unittest.mock import AsyncMock

import pytest

from app import service as module
from app.service import AutoSourcarrService
from test_v087_recovery import env, source, entry


def feed(service, database, size=50, **overrides):
    options = dict(mode="POPULAR", page=1, query="", source_ids=None, min_matches=0, exact_match_count=None, sort="relevance", page_size=size)
    options.update(overrides)
    key = service._browse_cache_key(**options)
    return key, options


def save(service, key, options, ids, errors=None):
    service._save_persistent_browse_cache(key, **{k: v for k, v in options.items() if k != "page_size"},
        response={"items": [{"id": eid} for eid in ids], "has_next_page": True, "source_errors": errors or []})


def test_page_size_change_reuses_ranked_saved_feed_without_upstream(env, monkeypatch):
    service, database = env
    source(database, "a")
    ids = [entry(service, "a", 100 + n, title=f"The Archive Beyond Planet {n}") for n in range(3)]
    key, options = feed(service, database, 50)
    save(service, key, options, list(reversed(ids)))
    refreshes = []
    monkeypatch.setattr(service, "_schedule_federated_refresh", lambda key, options: refreshes.append(options) or True)
    monkeypatch.setattr(service, "schedule_metadata_enrichment", lambda *args: None)
    fetch = AsyncMock(side_effect=AssertionError("cached page must not contact sources"))
    monkeypatch.setattr(module.source_host, "fetch_source_manga", fetch)
    result = asyncio.run(service.federated("POPULAR", min_matches=0, page_size=500))
    assert [item["id"] for item in result["items"]] == list(reversed(ids))
    assert result["cache_size_mismatch"] and result["refreshing"] and result["has_next_page"]
    assert result["page_size"] == 500 and refreshes[0]["page_size"] == 500
    fetch.assert_not_awaited()


@pytest.mark.parametrize("changed", [{"page": 2}, {"mode": "LATEST"}, {"source_ids": ["other"]}, {"query": "different"}, {"sort": "title"}, {"min_matches": 2}])
def test_resize_does_not_mix_feed_identity(env, changed):
    service, database = env
    source(database, "a")
    eid = entry(service, "a", 400, title="An Archive Far Beyond The Stars")
    key, options = feed(service, database)
    save(service, key, options, [eid])
    wanted, _ = feed(service, database, 500, **changed)
    assert service._load_persistent_browse_cache(wanted) is None


def test_failed_refresh_retains_last_good_ids_timestamp_and_errors(env):
    service, database = env
    source(database, "a")
    eid = entry(service, "a", 500, title="The Archive Across Endless Galaxies")
    key, options = feed(service, database)
    save(service, key, options, [eid])
    before = service._load_persistent_browse_cache(key)
    save(service, key, options, [], [{"error": "CAPTCHA"}])
    after = service._load_persistent_browse_cache(key)
    assert after["items"] == before["items"]
    assert after["cache_updated_at"] == before["cache_updated_at"]
    assert after["source_errors"] == [{"error": "CAPTCHA"}]
    save(service, key, options, [])  # a genuinely successful empty feed is authoritative
    assert service._load_persistent_browse_cache(key)["items"] == []


def test_failed_exact_empty_cache_can_reuse_other_size(env):
    service, database = env
    source(database, "a")
    eid = entry(service, "a", 600, title="Beyond The Cosmic Archive Gates")
    small, small_options = feed(service, database)
    big, big_options = feed(service, database, 500)
    save(service, small, small_options, [eid])
    save(service, big, big_options, [], [{"error": "CAPTCHA"}])
    result = service._load_persistent_browse_cache(big)
    assert [row["id"] for row in result["items"]] == [eid]


def test_no_attempted_source_is_not_successful_empty_refresh(env, monkeypatch):
    service, _ = env
    monkeypatch.setattr(service, "list_sources", lambda: [])
    monkeypatch.setattr(service, "sync_sources", AsyncMock(return_value=[]))
    monkeypatch.setattr(service, "_schedule_federated_refresh", lambda *args: False)
    result = asyncio.run(service.federated("POPULAR", force_live=True))
    assert not result["items"] and "No source request was attempted" in result["source_errors"][0]["error"]


def test_restored_subbed_is_display_only_and_scoped_to_main_server(env, monkeypatch):
    service, database = env
    source(database, "a")
    eid = entry(service, "a", 700, title="The Last Archive Beyond The Moon")
    monkeypatch.setattr(module.settings, "main_suwayomi_url", "http://main.invalid")
    service._persist_subbed_display_snapshot({"entry_ids": [eid], "stats_by_entry": {str(eid): {"chapter_count": 12}}})
    restored = AutoSourcarrService()
    display = restored.cached_subbed_payload()
    assert display["entry_ids"] == [eid] and display["saved_snapshot"] and display["refreshing"]
    assert restored._subbed_cache is None  # no live membership claim
    main = type("Client", (), {"sources": AsyncMock(return_value=[{"id": "s", "name": "AutoSourcarr"}]), "library_mangas": AsyncMock(return_value=[])})()
    monkeypatch.setattr(module, "main_suwayomi", main)
    result = asyncio.run(restored.subbed_entry_ids(force=True))
    assert result["ok"] and result["entry_ids"] == []
    main.library_mangas.assert_awaited_once()
    assert database.setting("subbed_display_snapshot_v1")["payload"]["entry_ids"] == []
    monkeypatch.setattr(module.settings, "main_suwayomi_url", "http://different.invalid")
    assert AutoSourcarrService().cached_subbed_payload()["ok"] is None


def test_failed_library_refresh_keeps_saved_display_snapshot(env, monkeypatch):
    service, _ = env
    monkeypatch.setattr(module.settings, "main_suwayomi_url", "http://main.invalid")
    service._persist_subbed_display_snapshot({"entry_ids": [], "stats_by_entry": {}})
    restored = AutoSourcarrService()
    main = type("Client", (), {"sources": AsyncMock(side_effect=RuntimeError("temporarily unavailable"))})()
    monkeypatch.setattr(module, "main_suwayomi", main)
    assert asyncio.run(restored.subbed_entry_ids(force=True))["ok"] is False
    assert restored.cached_subbed_payload()["saved_snapshot"] is True
