"""v0.2.85: partial provider results, retry freshness, ambiguity, and query cost."""
import asyncio
from datetime import datetime, timedelta, timezone

import pytest
from app.config import settings
from app.db import Database
from app.metadata_catalog import metadata_catalog
from app.service import AutoSourcarrService
import app.service as module
from test_v083_features import identity, orphan

@pytest.fixture
def service(monkeypatch, tmp_path):
    monkeypatch.setattr(settings, "data_dir", tmp_path)
    monkeypatch.setattr(settings, "database_dir", "")
    monkeypatch.setattr(settings, "exact_thumbnail_matching", False)
    monkeypatch.setattr(settings, "automatic_duplicate_matching", False)
    monkeypatch.setattr(settings, "metadata_identity_query_limit", 1)
    monkeypatch.setattr(settings, "metadata_identity_enabled", True)
    database = Database()
    monkeypatch.setattr(module, "db", database)
    instance = AutoSourcarrService()
    monkeypatch.setattr(metadata_catalog, "enabled_provider_keys", lambda: ["anilist"])
    monkeypatch.setattr(instance, "schedule_duplicate_snapshot_refresh", lambda **kw: False)
    yield instance
    database.close_current_thread()


def test_normal_resolver_keeps_fast_identity_when_another_provider_times_out(service, monkeypatch):
    monkeypatch.setattr(settings, "metadata_identity_overall_timeout", 1)
    monkeypatch.setattr(metadata_catalog, "enabled_provider_keys", lambda: ["anilist", "kitsu"])
    title = "Audit Fast Metadata"
    eid = orphan(service, title)
    stopped = []
    async def search(provider, query, **kw):
        if provider == "anilist":
            return [identity("85", title)]
        try:
            await asyncio.sleep(60)
        finally:
            stopped.append(provider)
    monkeypatch.setattr(service, "_metadata_provider_search", search)
    result = asyncio.run(service.resolve_entry_identity(eid))
    assert result["matched"] == 1
    assert "kitsu" in result["provider_errors"] and stopped == ["kitsu"]
    assert result["links"][0]["external_id"] == "85"


def test_import_resolver_keeps_partial_success_and_provider_diagnostics(service, monkeypatch):
    monkeypatch.setattr(settings, "metadata_identity_overall_timeout", 1)
    monkeypatch.setattr(metadata_catalog, "enabled_provider_keys", lambda: ["anilist", "kitsu"])
    title = "Audit Import Partial"
    async def search(provider, query, **kw):
        if provider == "anilist":
            return [identity("86", title)]
        await asyncio.sleep(60)
    monkeypatch.setattr(service, "_metadata_provider_search", search)
    result = asyncio.run(service._resolve_import_metadata_identity({"title": title}))
    assert result["identities"][0]["external_id"] == "86"
    assert "kitsu" in result["provider_errors"]


def test_candidate_batch_reads_one_native_evidence_snapshot(service, monkeypatch):
    title = "Audit Ambiguous Cost"
    eid = orphan(service, title)
    calls = []
    original = service._entry_duplicate_evidence_snapshot
    def count(entry_id):
        calls.append(entry_id)
        return original(entry_id)
    async def search(*args, **kwargs):
        return [identity(str(i), title) for i in range(30)]
    monkeypatch.setattr(service, "_entry_duplicate_evidence_snapshot", count)
    monkeypatch.setattr(service, "_metadata_provider_search", search)
    result = asyncio.run(service.resolve_entry_identity(eid))
    assert result["matched"] == 0  # Optimization must not guess between exact IDs.
    assert calls == [eid]


def test_checked_but_empty_legacy_import_retries_automatically(service, monkeypatch):
    item = {"title": "Audit Retry Legacy", "_autosourcarr_metadata_identity": {"checked": True}}
    calls = []
    async def search(*args, **kwargs):
        calls.append(args)
        return [identity("87", item["title"])]
    monkeypatch.setattr(service, "_metadata_provider_search", search)
    result = asyncio.run(service._resolve_import_metadata_identity(item))
    assert calls and result["matches"][0]["external_id"] == "87"
    assert result["checked_at"]


def test_accepted_legacy_bundle_is_not_erased_for_lacking_records(service, monkeypatch):
    old = {"checked": True, "matches": [{"provider": "anilist", "external_id": "88"}], "titles": ["Native"]}
    item = {"title": "Audit Legacy Accepted", "_autosourcarr_metadata_identity": old}
    async def unexpected(*args, **kwargs):
        raise AssertionError("legacy accepted metadata must remain usable")
    monkeypatch.setattr(service, "_metadata_provider_search", unexpected)
    assert asyncio.run(service._resolve_import_metadata_identity(item)) == old

@pytest.mark.parametrize("age, should_search", [(30, False), (301, True)])
def test_provider_error_bundle_has_short_retry_window(service, monkeypatch, age, should_search):
    old = {"checked": True, "checked_at": (datetime.now(timezone.utc) - timedelta(seconds=age)).isoformat(),
           "provider_errors": {"anilist": "timeout"}, "records": []}
    item = {"title": "Audit Provider Retry", "_autosourcarr_metadata_identity": old}
    calls = []
    async def search(*args, **kwargs):
        calls.append(args)
        return []
    monkeypatch.setattr(service, "_metadata_provider_search", search)
    asyncio.run(service._resolve_import_metadata_identity(item))
    assert bool(calls) == should_search


def test_distinct_tied_import_identities_remain_ambiguous(service, monkeypatch):
    title = "Audit Tied Import"
    async def search(*args, **kwargs):
        return [identity("89a", title), identity("89b", title)]
    monkeypatch.setattr(service, "_metadata_provider_search", search)
    result = asyncio.run(service._resolve_import_metadata_identity({"title": title}))
    assert not result["identities"] and result["ambiguous_providers"] == ["anilist"]


def test_cached_query_preserves_native_spelling_and_configuration(service, monkeypatch):
    from app.matcher import normalize_title
    # Both map to 'shi' under transliteration; they are not the same provider query.
    assert normalize_title("時") == normalize_title("詩")
    service._metadata_cache_write("anilist", "時", [{"external_id": "90"}])
    assert service._metadata_cache_read("anilist", "時")[0]["external_id"] == "90"
    assert service._metadata_cache_read("anilist", "詩") is None
    old_adult = settings.metadata_identity_include_adult
    monkeypatch.setattr(settings, "metadata_identity_include_adult", not old_adult)
    assert service._metadata_cache_read("anilist", "時") is None
    monkeypatch.setattr(settings, "metadata_identity_include_adult", old_adult)
    monkeypatch.setattr(settings, "metadata_identity_results_per_provider", settings.metadata_identity_results_per_provider + 1)
    assert service._metadata_cache_read("anilist", "時") is None


def test_temporary_error_preserves_previously_accepted_provider_record(service, monkeypatch):
    title = "Audit Preserve Accepted"
    async def succeed(*args, **kwargs):
        return [identity("91", title)]
    monkeypatch.setattr(service, "_metadata_provider_search", succeed)
    item = {"title": title}
    asyncio.run(service._resolve_import_metadata_identity(item))
    async def fail(*args, **kwargs):
        raise RuntimeError("temporary provider outage")
    monkeypatch.setattr(service, "_metadata_provider_search", fail)
    result = asyncio.run(service._resolve_import_metadata_identity(item, force=True))
    assert result["identities"][0]["external_id"] == "91"
    assert "anilist" in result["provider_errors"]
