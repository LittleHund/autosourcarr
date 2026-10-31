import asyncio
import time

import httpx
import pytest

from app.config import settings
from app.db import db
from app.metadata_catalog import MetadataCatalogClient, metadata_catalog
from app.service import AutoSourcarrService


def candidate(provider="anilist", eid="85", title="Time Roulette"):
    return {"provider": provider, "external_id": eid, "title": title,
            "titles": [{"title": title, "language": "en", "type": "primary"}],
            "description": "A canonical synopsis about the adventures of a time traveler and a roulette wheel.",
            "cover_url": "https://example.invalid/cover.jpg", "author": "", "artist": "",
            "genres": [], "identity_scope": "work", "status": "completed", "links": {}}


def make_orphan(service, title="Time Roulette"):
    return service._create_entry({"id": 0, "title": title, "description": "", "author": "", "artist": "", "genre": [], "status": 0}, "missing")


def test_entry_metadata_retains_fast_provider_when_other_times_out(monkeypatch):
    service = AutoSourcarrService()
    entry_id = make_orphan(service)
    monkeypatch.setattr(settings, "metadata_identity_overall_timeout", 1)
    monkeypatch.setattr(settings, "metadata_identity_query_limit", 1)
    monkeypatch.setattr(metadata_catalog, "enabled_provider_keys", lambda: ["anilist", "kitsu"])
    stopped = []
    async def search(provider, query, force=False):
        if provider == "anilist":
            return [candidate()]
        try:
            await asyncio.sleep(20)
        finally:
            stopped.append(provider)
    monkeypatch.setattr(service, "_metadata_provider_search", search)
    result = asyncio.run(service.resolve_entry_identity(entry_id, force=True))
    assert result["matched"] == 1
    assert "kitsu" in result["provider_errors"]
    assert stopped == ["kitsu"]
    assert service._identity_links(entry_id)[0]["external_id"] == "85"


def test_import_metadata_errors_remain_retryable_and_visible(monkeypatch):
    service = AutoSourcarrService()
    monkeypatch.setattr(metadata_catalog, "enabled_provider_keys", lambda: ["anilist"])
    calls = []
    async def search(*args, **kwargs):
        calls.append(True)
        if len(calls) == 1:
            raise httpx.ReadTimeout("Temporary error")
        return [candidate()]
    monkeypatch.setattr(service, "_metadata_provider_search", search)
    item = {"title": "Time Roulette"}
    result = asyncio.run(service._resolve_import_metadata_identity(item))
    assert result["checked"] is False
    assert result["provider_errors"]["anilist"] == "Temporary error"
    recovered = asyncio.run(service._resolve_import_metadata_identity(item))
    assert recovered["checked"] is True
    assert len(recovered["matches"]) == 1


@pytest.mark.parametrize("provider,payload", [
    ("anilist", {"errors": [{"message": "rate limited"}]}),
    ("comicvine", {"status_code": 100, "error": "Invalid API key", "results": []}),
    ("kitsu", {"errors": [{"status": "500"}]}),
])
def test_http200_provider_errors_are_not_cached_as_no_results(monkeypatch, provider, payload):
    async def scenario():
        catalog = MetadataCatalogClient()
        monkeypatch.setattr(settings, "comicvine_api_key", "test-only-key")
        monkeypatch.setattr(catalog, "enabled_provider_keys", lambda: [provider])
        catalog._client = httpx.AsyncClient(transport=httpx.MockTransport(lambda request: httpx.Response(200, json=payload)))
        try:
            with pytest.raises(ValueError, match="Metadata provider"):
                await catalog.search_provider(provider, "Time Roulette")
        finally:
            await catalog.close()
    asyncio.run(scenario())


def test_metadata_client_reuses_connections_and_rate_limit_cooldown(monkeypatch):
    async def scenario():
        catalog = MetadataCatalogClient()
        monkeypatch.setattr(catalog, "enabled_provider_keys", lambda: ["anilist"])
        requests = []
        def handler(request):
            requests.append(request)
            return httpx.Response(429, headers={"Retry-After": "90"})
        catalog._client = httpx.AsyncClient(transport=httpx.MockTransport(handler))
        client = catalog._client
        try:
            with pytest.raises(httpx.HTTPStatusError):
                await catalog.search_provider("anilist", "one")
            with pytest.raises(RuntimeError, match="retry in"):
                await catalog.search_provider("anilist", "another alias")
            assert len(requests) == 1
            assert catalog._http_client() is client
            assert 85 < catalog._cooldowns["anilist"][0] - time.monotonic() <= 90
        finally:
            await catalog.close()
        assert client.is_closed
    asyncio.run(scenario())


def test_native_evidence_snapshot_is_reused_for_candidate_scoring(monkeypatch):
    service = AutoSourcarrService()
    entry_id = make_orphan(service)
    original = service._entry_duplicate_evidence_snapshot
    calls = []
    def snapshot(eid):
        calls.append(eid)
        return original(eid)
    monkeypatch.setattr(service, "_entry_duplicate_evidence_snapshot", snapshot)
    monkeypatch.setattr(metadata_catalog, "enabled_provider_keys", lambda: ["anilist"])
    async def search(*args, **kwargs):
        return [candidate(eid=str(i), title=f"Wrong Roulette Title {i}") for i in range(15)]
    monkeypatch.setattr(service, "_metadata_provider_search", search)
    result = asyncio.run(service.resolve_entry_identity(entry_id))
    assert result["matched"] == 0
    assert calls == [entry_id]


def test_partial_import_metadata_survives_another_provider_outage(monkeypatch):
    service = AutoSourcarrService()
    monkeypatch.setattr(metadata_catalog, "enabled_provider_keys", lambda: ["anilist", "kitsu"])
    calls = []
    async def search(provider, *args, **kwargs):
        calls.append(provider)
        if provider == "kitsu":
            raise httpx.ReadTimeout("unavailable")
        return [candidate()]
    monkeypatch.setattr(service, "_metadata_provider_search", search)
    item = {"title": "Time Roulette"}
    first = asyncio.run(service._resolve_import_metadata_identity(item))
    second = asyncio.run(service._resolve_import_metadata_identity(item))
    assert first["checked"] is False and second["checked"] is False
    assert first["identities"] == second["identities"]
    assert second["identities"][0]["external_id"] == "85"
    assert calls.count("anilist") == 1


def test_orphan_repair_errors_are_not_marked_complete_and_resume(monkeypatch):
    service = AutoSourcarrService()
    eid = make_orphan(service)
    monkeypatch.setattr(metadata_catalog, "enabled_provider_keys", lambda: ["anilist"])
    calls = []
    async def resolve(entry_id, **kwargs):
        calls.append(entry_id)
        return {"matched": 0, "provider_errors": {"anilist": "offline"}} if len(calls) == 1 else {"matched": 0, "provider_errors": {}}
    monkeypatch.setattr(service, "resolve_entry_identity", resolve)
    first = asyncio.run(service.repair_orphan_identity_metadata_upgrade())
    assert first["pending"] is True and first["failed_ids"] == [eid]
    assert db.setting("orphan_identity_bootstrap_repair_version", "") != "0.2.85"
    second = asyncio.run(service.repair_orphan_identity_metadata_upgrade())
    assert second["pending"] is False
    assert calls == [eid, eid]
    assert db.setting("orphan_identity_bootstrap_repair_version") == "0.2.85"


def test_orphan_repair_limit_uses_cursor_instead_of_repeating_first_rows(monkeypatch):
    service = AutoSourcarrService()
    ids = [make_orphan(service, f"Distinct Orphan {i}") for i in range(3)]
    monkeypatch.setattr(metadata_catalog, "enabled_provider_keys", lambda: ["anilist"])
    calls = []
    async def resolve(entry_id, **kwargs):
        calls.append(entry_id)
        return {"matched": 0, "provider_errors": {}}
    monkeypatch.setattr(service, "resolve_entry_identity", resolve)
    results = [asyncio.run(service.repair_orphan_identity_metadata_upgrade(limit=1)) for _ in ids]
    assert calls == ids
    assert [r["pending"] for r in results] == [True, True, False]


def test_orphan_repair_disabled_providers_do_not_complete_upgrade(monkeypatch):
    service = AutoSourcarrService()
    make_orphan(service)
    monkeypatch.setattr(metadata_catalog, "enabled_provider_keys", lambda: [])
    result = asyncio.run(service.repair_orphan_identity_metadata_upgrade())
    assert result["skipped"] is True
    assert db.setting("orphan_identity_bootstrap_repair_version", "") != "0.2.85"
