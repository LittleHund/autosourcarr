import asyncio
import os
import tempfile

import httpx

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ["DATA_DIR"] = _TEST_DATA.name
os.environ["SOURCE_LANGUAGES"] = "en,all"

from app.db import db, now_iso
from app.service import AutoSourcarrService
from app.matcher import normalize_title
from app.suwayomi import bounded_map, source_host


def source_row(source_id: str, name: str, priority: int):
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_search,enabled_chapters,priority,last_seen)
            VALUES(?,?,?,?,?,?,?,?,?,?,?)""",
            (source_id, name, name, "en", "https://example.invalid", "", 1, 1, 1, priority, "now"),
        )


def manga(mid: int, title: str, description: str = "", author: str = "A"):
    return {
        "id": mid,
        "title": title,
        "url": f"/manga/{mid}",
        "thumbnailUrl": "",
        "description": description,
        "author": author,
        "artist": author,
        "genre": ["Action"],
        "status": 1,
        "initialized": True,
        "realUrl": "",
    }


def test_ingest_deduplicates_two_sources_by_title():
    s = AutoSourcarrService()
    source_row("1", "Source A", 10)
    source_row("2", "Source B", 20)
    e1, _, _ = s.ingest_manga("1", manga(101, "One-Punch Man"))
    e2, confidence, _ = s.ingest_manga("2", manga(202, "One Punch Man"))
    assert e1 == e2
    assert confidence >= 0.94
    assert s.get_entry(e1)["match_count"] == 2


def test_manual_match_is_locked_against_automatic_overwrite():
    s = AutoSourcarrService()
    source_row("1", "Source A", 10)
    eid, _, _ = s.ingest_manga("1", manga(101, "Berserk"))
    s._upsert_match(eid, "1", manga(102, "Berserk Deluxe"), 1.0, "manual", True)
    s._upsert_match(eid, "1", manga(103, "Berserk Wrong"), 0.99, "auto", False)
    entry = s.get_entry(eid)
    assert entry["matches"][0]["source_manga_id"] == 102
    assert entry["matches"][0]["locked"] == 1


def test_graphql_status_enum_is_translated():
    s = AutoSourcarrService()
    source_row("3", "Source C", 30)
    m = manga(303, "Status Test")
    m["status"] = "ONGOING"
    eid, _, _ = s.ingest_manga("3", m)
    assert s.get_entry(eid)["status"] == 1


def chapter(cid: int, number: float, pages: int, name: str | None = None):
    return {
        "id": cid,
        "url": f"/chapter/{cid}",
        "name": name or f"Chapter {number:g}",
        "uploadDate": 0,
        "chapterNumber": number,
        "scanlator": "",
        "mangaId": 0,
        "sourceOrder": cid,
        "realUrl": "",
        "pageCount": pages,
    }


def test_whole_chapter_hides_fractional_parts_when_page_totals_match():
    s = AutoSourcarrService()
    source_row("split-a", "Whole Source", 10)
    source_row("split-b", "Parts Source", 20)
    eid, _, _ = s.ingest_manga("split-a", manga(4101, "Split Chapter Test"))
    s._upsert_match(eid, "split-b", manga(4201, "Split Chapter Test"), 1.0)
    s._ingest_chapters(
        eid,
        {"source_id": "split-a", "source_name": "Whole Source", "source_manga_id": 4101},
        s._match_id(eid, "split-a"),
        [chapter(4110, 5.0, 42)],
    )
    s._ingest_chapters(
        eid,
        {"source_id": "split-b", "source_name": "Parts Source", "source_manga_id": 4201},
        s._match_id(eid, "split-b"),
        [chapter(4211, 5.1, 20), chapter(4212, 5.2, 22)],
    )
    s._reconcile_split_chapters(eid)
    nums = [x["chapter_number"] for x in s.list_chapters(eid)]
    assert 5.0 in nums
    assert 5.1 not in nums
    assert 5.2 not in nums


def test_complete_chapter_beats_higher_priority_truncated_candidate():
    s = AutoSourcarrService()
    source_row("prio-a", "High Priority", 1)
    source_row("prio-b", "Low Priority", 50)
    eid, _, _ = s.ingest_manga("prio-a", manga(5101, "Priority Test"))
    s._upsert_match(eid, "prio-b", manga(5201, "Priority Test"), 1.0)
    s._ingest_chapters(
        eid,
        {"source_id": "prio-a", "source_name": "High Priority", "source_manga_id": 5101},
        s._match_id(eid, "prio-a"),
        [chapter(5110, 10.0, 20)],
    )
    s._ingest_chapters(
        eid,
        {"source_id": "prio-b", "source_name": "Low Priority", "source_manga_id": 5201},
        s._match_id(eid, "prio-b"),
        [chapter(5210, 10.0, 40)],
    )
    vid = s.list_chapters(eid)[0]["id"]
    detail = s.chapter_details(vid)
    assert detail["candidates"][0]["source_id"] == "prio-b"



def test_live_discovery_refresh_and_page_resolution(monkeypatch):
    s = AutoSourcarrService()
    source_row("flow-a", "Flow A", 1)
    source_row("flow-b", "Flow B", 20)

    async def fake_fetch_source_manga(source_id, mode, page, query=""):
        if source_id not in {"flow-a", "flow-b"}:
            return {"mangas": [], "hasNextPage": False}
        mid = 6101 if source_id == "flow-a" else 6201
        title = "Flow Test" if source_id == "flow-a" else "Flow-Test"
        return {"mangas": [manga(mid, title)], "hasNextPage": False}

    async def fake_fetch_manga_and_chapters(manga_id, fetch_manga=True, fetch_chapters=True):
        if manga_id == 6101:
            return {
                "manga": manga(6101, "Flow Test"),
                "chapters": [chapter(6110, 1.0, 3)],
            }
        return {
            "manga": manga(6201, "Flow-Test"),
            "chapters": [chapter(6210, 1.0, 3)],
        }

    async def fake_fetch_chapter_pages(chapter_id):
        return [f"https://images.invalid/{chapter_id}/{i}.jpg" for i in range(3)]

    async def fake_chapter_index_for_id(manga_id, chapter_id):
        return 0

    monkeypatch.setattr(source_host, "fetch_source_manga", fake_fetch_source_manga)
    monkeypatch.setattr(source_host, "fetch_manga_and_chapters", fake_fetch_manga_and_chapters)
    monkeypatch.setattr(source_host, "fetch_chapter_pages", fake_fetch_chapter_pages)
    monkeypatch.setattr(source_host, "chapter_index_for_id", fake_chapter_index_for_id)

    found = asyncio.run(s.federated("SEARCH", query="Flow Test"))
    assert len(found["items"]) == 1
    entry_id = found["items"][0]["id"]
    assert found["items"][0]["match_count"] == 2

    asyncio.run(s.refresh_entry(entry_id))
    chapters = s.list_chapters(entry_id)
    assert len(chapters) == 1
    assert chapters[0]["candidate_count"] == 2

    resolved = asyncio.run(s.resolve_pages(chapters[0]["id"]))
    assert resolved["candidate"]["source_id"] == "flow-a"
    assert len(resolved["pages"]) == 3
    assert "/api/v1/pages/" in resolved["pages"][0]

    history = s.entry_history(entry_id)
    assert history["history"][0]["source_id"] == "flow-a"


def test_source_family_counts_multiple_languages_once():
    s = AutoSourcarrService()
    source_row("family-en", "Comick (Unoriginal)", 10)
    source_row("family-ja", "Comick (Unoriginal)", 10)
    with db.catalog() as c:
        c.execute("UPDATE source_config SET lang='en' WHERE source_id='family-en'")
        c.execute("UPDATE source_config SET lang='ja' WHERE source_id='family-ja'")
    eid, _, _ = s.ingest_manga("family-en", manga(7101, "Language Family Test"))
    eid2, _, _ = s.ingest_manga("family-ja", manga(7201, "Language-Family Test"))
    assert eid == eid2
    summary = s.get_entry(eid)
    assert summary["match_count"] == 1
    assert any("Comick (Unoriginal)" in x and "en" in x and "ja" in x for x in summary["sources"])


def test_disabled_language_is_not_used_for_chapter_serving():
    s = AutoSourcarrService()
    source_row("lang-off", "Language Source", 1)
    source_row("lang-on", "Fallback Source", 20)
    with db.catalog() as c:
        c.execute("UPDATE source_config SET enabled_language=0 WHERE source_id='lang-off'")
    eid, _, _ = s.ingest_manga("lang-off", manga(8101, "Language Serving Test"))
    s._upsert_match(eid, "lang-on", manga(8201, "Language Serving Test"), 1.0)
    s._ingest_chapters(
        eid,
        {"source_id": "lang-off", "source_name": "Language Source", "source_manga_id": 8101},
        s._match_id(eid, "lang-off"),
        [chapter(8110, 1.0, 20)],
    )
    s._ingest_chapters(
        eid,
        {"source_id": "lang-on", "source_name": "Fallback Source", "source_manga_id": 8201},
        s._match_id(eid, "lang-on"),
        [chapter(8210, 1.0, 20)],
    )
    chapter_id = s.list_chapters(eid)[0]["id"]
    detail = s.chapter_details(chapter_id)
    assert [c["source_id"] for c in detail["candidates"]] == ["lang-on"]


def test_bounded_map_returns_fast_results_when_another_source_times_out():
    async def worker(item):
        if item == "slow":
            await asyncio.sleep(0.2)
        return {"value": item}

    results = asyncio.run(
        bounded_map(
            ["fast", "slow"],
            worker,
            limit=2,
            per_item_timeout=0.03,
            overall_timeout=0.08,
        )
    )
    by_item = {item: (payload, error) for item, payload, error in results}
    assert by_item["fast"][0] == {"value": "fast"}
    assert by_item["fast"][1] is None
    assert by_item["slow"][0] is None
    assert isinstance(by_item["slow"][1], TimeoutError)


def test_bounded_map_hard_deadline_cancels_queued_sources():
    async def worker(item):
        await asyncio.sleep(0.2)
        return item

    results = asyncio.run(
        bounded_map(
            [1, 2, 3],
            worker,
            limit=1,
            per_item_timeout=1.0,
            overall_timeout=0.03,
        )
    )
    assert len(results) == 3
    assert all(payload is None for _, payload, _ in results)
    assert all(isinstance(error, TimeoutError) for _, _, error in results)


def test_federated_search_returns_partial_results_when_a_source_is_slow(monkeypatch):
    from app.config import settings

    s = AutoSourcarrService()
    source_row("partial-fast", "Partial Fast", 1)
    source_row("partial-slow", "Partial Slow", 2)

    async def fake_fetch_source_manga(source_id, mode, page, query=""):
        if source_id == "partial-slow":
            await asyncio.sleep(0.2)
        return {
            "mangas": [manga(9101 if source_id == "partial-fast" else 9201, "Partial Result")],
            "hasNextPage": False,
        }

    monkeypatch.setattr(source_host, "fetch_source_manga", fake_fetch_source_manga)
    monkeypatch.setattr(settings, "federated_source_timeout", 0.03)
    monkeypatch.setattr(settings, "federated_overall_timeout", 0.08)

    result = asyncio.run(s.federated("SEARCH", query="Partial Result"))
    assert result["items"]
    assert result["items"][0]["preferred_title"] == "Partial Result"
    assert any(error["source_id"] == "partial-slow" for error in result["source_errors"])


def test_description_alone_cannot_deduplicate_differently_titled_sources():
    s = AutoSourcarrService()
    source_row("desc-a", "Description A", 10)
    source_row("desc-b", "Description B", 20)
    text = (
        "After being transported to a distant kingdom, a quiet librarian discovers an ancient "
        "archive and joins a group of explorers searching for the vanished royal city beneath the sea."
    )
    e1, _, _ = s.ingest_manga("desc-a", manga(10101, "The Lost Archive", text))
    e2, confidence, reason = s.ingest_manga("desc-b", manga(10201, "Library of the Sunken Kingdom", text))
    assert e1 != e2
    assert confidence == 1.0
    assert reason == "new-entry"


def test_subbed_entries_map_main_library_urls(monkeypatch):
    import app.service as service_module

    s = AutoSourcarrService()
    source_row("sub-src", "Subbed Source", 10)
    eid, _, _ = s.ingest_manga("sub-src", manga(11101, "Subscribed Test"))

    class FakeMain:
        async def sources(self):
            return [{"id": "999", "name": "AutoSourcarr", "displayName": "AutoSourcarr"}]

        async def library_mangas(self):
            return [
                {
                    "id": 77, "sourceId": "999", "url": str(eid), "title": "Subscribed Test",
                    "inLibrary": True, "downloadCount": 12, "chapters": {"totalCount": 34},
                },
                {"id": 78, "sourceId": "123", "url": "555", "title": "Other extension", "inLibrary": True},
            ]

    monkeypatch.setattr(service_module, "main_suwayomi", FakeMain())
    result = asyncio.run(s.subbed_entry_ids())
    assert result["ok"] is True
    assert result["entry_ids"] == [eid]
    assert result["stats_by_entry"][str(eid)]["chapter_count"] == 34
    assert result["stats_by_entry"][str(eid)]["downloaded_chapter_count"] == 12
    assert result["stats_by_entry"][str(eid)]["main_manga_id"] == 77
    assert s.entries_by_ids(result["entry_ids"])[0]["preferred_title"] == "Subscribed Test"


def test_consolidate_existing_duplicate_entries_keeps_all_source_matches():
    s = AutoSourcarrService()
    source_row("merge-a", "Merge A", 10)
    source_row("merge-b", "Merge B", 20)
    a = manga(12101, "Duplicate Work")
    b = manga(12201, "Duplicate-Work")
    e1 = s._create_entry(a, "merge-a")
    s._upsert_match(e1, "merge-a", a, 1.0)
    e2 = s._create_entry(b, "merge-b")
    s._upsert_match(e2, "merge-b", b, 1.0)
    s.add_title(e1, "Duplicate Work Alt", title_type="manual")
    s.add_title(e2, "Duplicate Work Alt", title_type="manual")
    assert e1 != e2

    remap = s.consolidate_entries([e1, e2])
    merged = remap[e1]
    assert remap[e2] == merged
    entry = s.get_entry(merged)
    assert len(entry["matches"]) == 2
    assert {m["source_id"] for m in entry["matches"]} == {"merge-a", "merge-b"}


def test_equal_completeness_uses_source_priority():
    s = AutoSourcarrService()
    source_row("equal-a", "Preferred", 1)
    source_row("equal-b", "Fallback", 50)
    eid, _, _ = s.ingest_manga("equal-a", manga(13101, "Equal Completeness"))
    s._upsert_match(eid, "equal-b", manga(13201, "Equal Completeness"), 1.0)
    s._ingest_chapters(eid, {"source_id":"equal-a","source_name":"Preferred","source_manga_id":13101}, s._match_id(eid,"equal-a"), [chapter(13110, 1.0, 30)])
    s._ingest_chapters(eid, {"source_id":"equal-b","source_name":"Fallback","source_manga_id":13201}, s._match_id(eid,"equal-b"), [chapter(13210, 1.0, 30)])
    vid = s.list_chapters(eid)[0]["id"]
    assert s.chapter_details(vid)["candidates"][0]["source_id"] == "equal-a"


def test_consolidation_preserves_combined_chapter_coverage():
    s = AutoSourcarrService()
    source_row("coverage-a", "Coverage A", 10)
    source_row("coverage-b", "Coverage B", 20)
    a = manga(14101, "Coverage Work")
    b = manga(14201, "Coverage-Work")
    e1 = s._create_entry(a, "coverage-a")
    s._upsert_match(e1, "coverage-a", a, 1.0)
    e2 = s._create_entry(b, "coverage-b")
    s._upsert_match(e2, "coverage-b", b, 1.0)
    s.add_title(e1, "Coverage Work Alt", title_type="manual")
    s.add_title(e2, "Coverage Work Alt", title_type="manual")
    s._ingest_chapters(e1, {"source_id":"coverage-a","source_name":"Coverage A","source_manga_id":14101}, s._match_id(e1,"coverage-a"), [chapter(14110, 1.0, 20)])
    s._ingest_chapters(e2, {"source_id":"coverage-b","source_name":"Coverage B","source_manga_id":14201}, s._match_id(e2,"coverage-b"), [chapter(14220, 2.0, 20)])

    remap = s.consolidate_entries([e1, e2])
    merged = remap[e1]
    assert remap[e2] == merged
    numbers = {row["chapter_number"] for row in s.list_chapters(merged)}
    assert numbers == {1.0, 2.0}


def test_persistent_browse_cache_survives_service_instance_and_skips_live_fetch(monkeypatch):
    s = AutoSourcarrService()
    source_row("cache-src", "Cache Source", 1)
    calls = {"count": 0}

    async def fake_fetch_source_manga(source_id, mode, page, query=""):
        calls["count"] += 1
        return {"mangas": [manga(15101, "Persistent Cache Work")], "hasNextPage": False}

    monkeypatch.setattr(source_host, "fetch_source_manga", fake_fetch_source_manga)
    live = asyncio.run(s.federated("SEARCH", query="Persistent Cache Work", source_ids=["cache-src"], force_live=True))
    assert live["items"]
    assert calls["count"] == 1

    async def should_not_be_called(*args, **kwargs):
        raise AssertionError("fresh persistent cache should be returned before a live source call")

    monkeypatch.setattr(source_host, "fetch_source_manga", should_not_be_called)
    s2 = AutoSourcarrService()
    cached = asyncio.run(s2.federated("SEARCH", query="Persistent Cache Work", source_ids=["cache-src"]))
    assert cached["persistent_cache"] is True
    assert cached["cache_hit"] is True
    assert cached["items"][0]["preferred_title"] == "Persistent Cache Work"


def test_exact_match_count_filter_uses_source_families(monkeypatch):
    s = AutoSourcarrService()
    source_row("exact-a", "Exact A", 1)
    source_row("exact-b", "Exact B", 2)

    async def fake_fetch_source_manga(source_id, mode, page, query=""):
        mid = 16101 if source_id == "exact-a" else 16201
        return {"mangas": [manga(mid, "Exact Count Work")], "hasNextPage": False}

    monkeypatch.setattr(source_host, "fetch_source_manga", fake_fetch_source_manga)
    two = asyncio.run(s.federated("SEARCH", query="Exact Count Work", source_ids=["exact-a", "exact-b"], exact_match_count=2, force_live=True))
    assert len(two["items"]) == 1
    assert two["items"][0]["match_count"] == 2
    one = asyncio.run(s.federated("SEARCH", query="Exact Count Work", source_ids=["exact-a", "exact-b"], exact_match_count=1, force_live=True))
    assert one["items"] == []


def test_status_stats_reports_persistent_browse_cache():
    stats = AutoSourcarrService().status_stats()
    assert stats["catalog"]["entries"] >= 1
    assert stats["cache"]["persistent_browse_feeds"] >= 1
    assert "browse_cache_refresh_seconds" in stats["performance"]


def test_stale_persistent_feed_returns_before_background_refresh_finishes(monkeypatch):
    from app.config import settings

    s = AutoSourcarrService()
    source_row("swr-src", "SWR Source", 1)

    async def first_fetch(source_id, mode, page, query=""):
        return {"mangas": [manga(17101, "Stale While Revalidate")], "hasNextPage": False}

    monkeypatch.setattr(source_host, "fetch_source_manga", first_fetch)
    asyncio.run(s.federated("POPULAR", source_ids=["swr-src"], force_live=True))
    monkeypatch.setattr(settings, "browse_cache_refresh_seconds", 0)

    async def slow_refresh(source_id, mode, page, query=""):
        await asyncio.sleep(0.08)
        return {"mangas": [manga(17101, "Stale While Revalidate")], "hasNextPage": False}

    monkeypatch.setattr(source_host, "fetch_source_manga", slow_refresh)

    async def scenario():
        started = asyncio.get_running_loop().time()
        response = await s.federated("POPULAR", source_ids=["swr-src"])
        elapsed = asyncio.get_running_loop().time() - started
        assert elapsed < 0.04
        assert response["persistent_cache"] is True
        assert response["refreshing"] is True
        await asyncio.gather(*list(s._refresh_tasks.values()), return_exceptions=True)

    asyncio.run(scenario())


def test_same_source_reliable_duplicate_merges_and_keeps_variant():
    s = AutoSourcarrService()
    source_row("same-dup", "Same Duplicate Source", 10)
    desc = (
        "A veteran explorer repeats the same expedition ninety nine times and uses every failure "
        "to build the perfect strategy for reaching the bottom of the dungeon with his companions."
    )
    a = manga(18101, "The Bottom Rank Explorer", desc)
    b = manga(18102, "The Bottom-Rank Explorer", desc)
    e1 = s._create_entry(a, "same-dup")
    s._upsert_match(e1, "same-dup", a, 1.0)
    e2 = s._create_entry(b, "same-dup")
    s._upsert_match(e2, "same-dup", b, 1.0)

    remap = s.consolidate_entries([e1, e2])
    merged = remap[e1]
    assert remap[e2] == merged
    entry = s.get_entry(merged)
    assert len(entry["matches"]) == 1
    assert len(entry["source_variants"]) == 1
    ids = {entry["matches"][0]["source_manga_id"], entry["source_variants"][0]["source_manga_id"]}
    assert ids == {18101, 18102}


def test_same_source_ambiguous_records_do_not_merge_without_strong_evidence():
    s = AutoSourcarrService()
    source_row("same-ambiguous", "Ambiguous Source", 10)
    a = manga(18201, "Reborn Hero", "")
    b = manga(18202, "Reborn Hero", "")
    a["author"] = "Author One"
    b["author"] = "Author Two"
    e1 = s._create_entry(a, "same-ambiguous")
    s._upsert_match(e1, "same-ambiguous", a, 1.0)
    e2 = s._create_entry(b, "same-ambiguous")
    s._upsert_match(e2, "same-ambiguous", b, 1.0)

    remap = s.consolidate_entries([e1, e2])
    assert remap[e1] == e1
    assert remap[e2] == e2


def test_same_source_variant_adds_missing_chapters_on_refresh(monkeypatch):
    s = AutoSourcarrService()
    source_row("variant-cover", "Variant Coverage", 10)
    desc = (
        "A complete duplicate source record for the same serialized work is retained so chapters "
        "missing from one catalog record can still be served from its alternate record."
    )
    a = manga(18301, "Variant Coverage Work", desc)
    b = manga(18302, "Variant-Coverage Work", desc)
    e1 = s._create_entry(a, "variant-cover")
    s._upsert_match(e1, "variant-cover", a, 1.0)
    e2 = s._create_entry(b, "variant-cover")
    s._upsert_match(e2, "variant-cover", b, 1.0)
    merged = s.consolidate_entries([e1, e2])[e1]

    async def fake_fetch_manga_and_chapters(manga_id, fetch_manga=True, fetch_chapters=True):
        if int(manga_id) == 18301:
            return {"manga": a, "chapters": [chapter(18311, 1.0, 20)]}
        return {"manga": b, "chapters": [chapter(18322, 2.0, 20)]}

    monkeypatch.setattr(source_host, "fetch_manga_and_chapters", fake_fetch_manga_and_chapters)
    asyncio.run(s.refresh_entry(merged))
    assert {x["chapter_number"] for x in s.list_chapters(merged)} == {1.0, 2.0}


def test_merged_old_entry_id_still_resolves():
    s = AutoSourcarrService()
    source_row("alias-a", "Alias A", 10)
    source_row("alias-b", "Alias B", 20)
    a = manga(18401, "Stable Alias Work")
    b = manga(18402, "Stable-Alias Work")
    e1 = s._create_entry(a, "alias-a")
    s._upsert_match(e1, "alias-a", a, 1.0)
    e2 = s._create_entry(b, "alias-b")
    s._upsert_match(e2, "alias-b", b, 1.0)
    s.add_title(e1, "Stable Alias Alternate", title_type="manual")
    s.add_title(e2, "Stable Alias Alternate", title_type="manual")
    remap = s.consolidate_entries([e1, e2])
    target = remap[e1]
    old = e2 if target == e1 else e1
    resolved = s.get_entry(old)
    assert resolved["canonical_id"] == target
    assert resolved["url"] == str(old)


def test_manual_search_source_failure_returns_clean_payload(monkeypatch):
    s = AutoSourcarrService()
    source_row("broken-source", "Broken Source", 10)
    eid, _, _ = s.ingest_manga("broken-source", manga(18701, "Anything"))

    async def broken(*args, **kwargs):
        raise RuntimeError("HTTP error 404")

    monkeypatch.setattr(source_host, "fetch_source_manga", broken)
    result = asyncio.run(s.search_source_for_manual(eid, "broken-source", "Anything", 1))
    # The live source can fail while AutoSourcarr still exposes previously acquired
    # source records from SQLite. This is preferable to an empty/error-only matcher.
    assert result["mangas"]
    assert result["mangas"][0]["id"] == 18701
    assert result["hasNextPage"] is False
    assert result["error"] == ""
    assert any("404" in warning for warning in result["warnings"])


def test_sync_sources_marks_removed_source_unavailable(monkeypatch):
    s = AutoSourcarrService()
    source_row("stale-source", "Stale Source", 10)
    source_row("live-source", "Live Source", 20)

    async def fake_sources():
        return [{
            "id": "live-source", "name": "Live Source", "displayName": "Live Source", "lang": "en",
            "homeUrl": "https://example.invalid", "iconUrl": "", "supportsLatest": True,
        }]

    monkeypatch.setattr(source_host, "sources", fake_sources)
    rows = asyncio.run(s.sync_sources())
    assert {r["source_id"] for r in rows} == {"live-source"}
    with db.catalog() as c:
        stale = c.execute("SELECT available,enabled_search,enabled_chapters FROM source_config WHERE source_id='stale-source'").fetchone()
    assert int(stale["available"]) == 0
    # Availability gates work without discarding the user's retained choices.
    assert int(stale["enabled_search"]) == 1
    assert int(stale["enabled_chapters"]) == 1



def test_sync_sources_transient_empty_snapshot_keeps_existing_sources(monkeypatch):
    s = AutoSourcarrService()
    source_row("keep-source", "Keep Source", 10)

    async def fake_sources():
        return []

    monkeypatch.setattr(source_host, "sources", fake_sources)
    rows = asyncio.run(s.sync_sources())
    assert "keep-source" in {r["source_id"] for r in rows}
    with db.catalog() as c:
        row = c.execute("SELECT available,enabled_search,enabled_chapters FROM source_config WHERE source_id='keep-source'").fetchone()
    assert int(row["available"]) == 1
    assert int(row["enabled_search"]) == 1
    assert int(row["enabled_chapters"]) == 1

def test_uncached_popular_does_not_use_random_catalog_fallback():
    s = AutoSourcarrService()
    source_row("fallback-source", "Fallback Source", 10)
    s.ingest_manga("fallback-source", manga(18501, "Catalog Entry That Is Not A Feed"))
    fallback = s._catalog_fallback("POPULAR", "", 1, None, "relevance")
    assert fallback["items"] == []


def test_same_source_duplicate_same_underlying_url_merges_without_description():
    s = AutoSourcarrService()
    source_row("same-url", "Same URL Source", 10)
    a = manga(18601, "Same Page Work", "")
    b = manga(18602, "Same Page Work", "")
    a["url"] = "/title/same-page-work"
    b["url"] = "/title/same-page-work"
    a["author"] = ""
    b["author"] = ""
    e1 = s._create_entry(a, "same-url")
    s._upsert_match(e1, "same-url", a, 1.0)
    e2 = s._create_entry(b, "same-url")
    s._upsert_match(e2, "same-url", b, 1.0)
    remap = s.consolidate_entries([e1, e2])
    assert remap[e1] != remap[e2]


def test_same_source_second_ingest_becomes_additional_match_instead_of_duplicate_entry():
    s = AutoSourcarrService()
    source_row("multi-same", "Multi Same", 10)
    desc = "An explorer repeats the same journey and learns from every failure until the complete route is finally known to the team."
    first, _, _ = s.ingest_manga("multi-same", manga(18801, "Multiple Record Work", desc))
    second, score, reason = s.ingest_manga("multi-same", manga(18802, "Multiple-Record Work", desc))
    assert first == second
    assert score >= 0.97
    entry = s.get_entry(first)
    assert len(entry["matches"]) == 1
    assert len(entry["source_variants"]) == 1
    assert entry["source_variants"][0]["source_manga_id"] == 18802


def test_manual_search_uses_all_known_titles_and_ranks_closest(monkeypatch):
    s = AutoSourcarrService()
    source_row("alias-search", "Alias Search", 10)
    eid, _, _ = s.ingest_manga("alias-search", manga(18901, "English Display Name"))
    s.add_title(eid, "Sekai no Namae", title_type="manual")
    calls = []

    async def fake_fetch(source_id, mode, page, query=""):
        calls.append(query)
        if query == "Sekai no Namae":
            return {"mangas": [manga(18902, "Sekai no Namae")], "hasNextPage": False}
        return {"mangas": [manga(18903, "Different Thing")], "hasNextPage": False}

    monkeypatch.setattr(source_host, "fetch_source_manga", fake_fetch)
    result = asyncio.run(s.search_source_for_manual(eid, "alias-search", "", 1, True))
    assert "English Display Name" in calls
    assert "Sekai no Namae" in calls
    candidates = {x["id"]: x for x in result["mangas"]}
    assert 18902 in candidates
    assert candidates[18902]["autosourcarr_match_score"] >= 0.99


def test_duplicate_candidates_consider_alternate_titles_not_only_preferred_title():
    s = AutoSourcarrService()
    source_row("dup-all-a", "Dup All A", 10)
    source_row("dup-all-b", "Dup All B", 20)
    a, _, _ = s.ingest_manga("dup-all-a", manga(19001, "Displayed A"))
    b, _, _ = s.ingest_manga("dup-all-b", manga(19002, "Displayed B"))
    s.add_title(a, "Shared Hidden Alias", title_type="manual")
    s.add_title(b, "Shared Hidden Alias", title_type="manual")
    dup = s.duplicate_candidates([a, b])
    assert b in dup[a]["entry_ids"]
    assert a in dup[b]["entry_ids"]


def test_delete_entry_suppresses_exact_source_record_from_rediscovery():
    s = AutoSourcarrService()
    source_row("delete-src", "Delete Source", 10)
    eid, _, _ = s.ingest_manga("delete-src", manga(19101, "Delete Me"))
    result = s.delete_entry(eid)
    assert result["deleted"] is True
    rediscovered, score, reason = s.ingest_manga("delete-src", manga(19101, "Delete Me"))
    assert rediscovered is None
    assert reason == "manually-deleted"


def test_manual_match_existing_owned_source_record_is_forcibly_moved_not_merged(monkeypatch):
    s = AutoSourcarrService()
    source_row("manual-force-a", "Manual Force A", 10)
    source_row("manual-force-b", "Manual Force B", 20)
    left, _, _ = s.ingest_manga("manual-force-a", manga(19201, "Manual Force Left"))
    right, _, _ = s.ingest_manga("manual-force-b", manga(19202, "Completely Different Display"))

    async def fake_fetch_manga_and_chapters(manga_id, fetch_manga=True, fetch_chapters=True):
        return {"manga": manga(19202, "Completely Different Display"), "chapters": []}

    monkeypatch.setattr(source_host, "fetch_manga_and_chapters", fake_fetch_manga_and_chapters)
    result = asyncio.run(s.manual_match(left, "manual-force-b", 19202, "add"))
    assert s.canonical_entry_id(left) == left
    assert s.canonical_entry_id(right) == right
    assert right in result["forced_from_entry_ids"]
    with db.catalog() as c:
        old = c.execute(
            "SELECT active FROM source_matches WHERE entry_id=? AND source_id=? AND source_manga_id=?",
            (right, "manual-force-b", 19202),
        ).fetchone()
        moved = c.execute(
            "SELECT entry_id,active,locked,match_type FROM source_matches WHERE source_id=? AND source_manga_id=? AND active=1",
            ("manual-force-b", 19202),
        ).fetchone()
    assert old is not None and int(old["active"]) == 0
    assert moved is not None and int(moved["entry_id"]) == left and int(moved["active"]) == 1
    assert int(moved["locked"]) == 1 and moved["match_type"] == "manual"


def test_manual_add_forces_owned_record_into_target_variant():
    s = AutoSourcarrService()
    source_row("manual-add-base", "Manual Add Base", 10)
    source_row("manual-add-shared", "Manual Add Shared", 20)
    target = s._create_entry(manga(19211, "Manual Add Target"), "manual-add-base")
    s._upsert_match(target, "manual-add-base", manga(19211, "Manual Add Target"), 1.0)
    s._upsert_match(target, "manual-add-shared", manga(19212, "Manual Add Existing Primary"), 1.0)
    owner = s._create_entry(manga(19213, "Manual Add Other Owner"), "manual-add-shared")
    s._upsert_match(owner, "manual-add-shared", manga(19213, "Manual Add Other Owner"), 1.0)

    result = asyncio.run(s.manual_match(
        target, "manual-add-shared", 19213, "add",
        candidate=manga(19213, "Manual Add Other Owner"), defer_hydration=True,
    ))
    assert owner in result["forced_from_entry_ids"]
    with db.catalog() as c:
        old_owner = c.execute(
            "SELECT active FROM source_matches WHERE entry_id=? AND source_id=? AND source_manga_id=?",
            (owner, "manual-add-shared", 19213),
        ).fetchone()
        variant = c.execute(
            "SELECT entry_id,active,locked,match_type FROM source_variants WHERE source_id=? AND source_manga_id=?",
            ("manual-add-shared", 19213),
        ).fetchone()
    assert old_owner is not None and int(old_owner["active"]) == 0
    assert variant is not None and int(variant["entry_id"]) == target and int(variant["active"]) == 1
    assert int(variant["locked"]) == 1 and variant["match_type"] == "manual"


def test_forced_manual_move_leaves_previous_persistent_manual_group():
    s = AutoSourcarrService()
    source_row("manual-group-one", "Manual Group One", 10)
    source_row("manual-group-two", "Manual Group Two", 20)
    source_row("manual-group-target", "Manual Group Target", 30)
    owner = s._create_entry(manga(19221, "Old Manual Group"), "manual-group-one")
    one = manga(19221, "Old Manual Group")
    two = manga(19222, "Old Manual Group Mirror")
    s._upsert_match(owner, "manual-group-one", one, 1.0, "manual", True)
    s._upsert_match(owner, "manual-group-two", two, 1.0, "manual", True)
    s._persist_manual_binding(owner, "manual-group-one", one, source_name="Manual Group One")
    s._persist_manual_binding(owner, "manual-group-two", two, source_name="Manual Group Two")
    with db.catalog() as c:
        before = c.execute(
            "SELECT group_id FROM manual_match_bindings WHERE source_id=? AND source_manga_id=?",
            ("manual-group-two", 19222),
        ).fetchone()
    assert before is not None and str(before["group_id"] or "")

    target, _, _ = s.ingest_manga("manual-group-target", manga(19223, "Target Manual Group"))
    asyncio.run(s.manual_match(
        target, "manual-group-two", 19222, "replace", candidate=two, defer_hydration=True,
    ))
    with db.catalog() as c:
        moved = c.execute(
            "SELECT entry_id_hint,group_id FROM manual_match_bindings WHERE source_id=? AND source_manga_id=?",
            ("manual-group-two", 19222),
        ).fetchone()
        old_left = c.execute(
            "SELECT group_id FROM manual_match_bindings WHERE source_id=? AND source_manga_id=?",
            ("manual-group-one", 19221),
        ).fetchone()
    assert moved is not None and int(moved["entry_id_hint"]) == target
    assert not str(moved["group_id"] or "")
    assert old_left is not None and not str(old_left["group_id"] or "")


def test_manual_replace_forces_exact_primary_away_from_other_entry(monkeypatch):
    s = AutoSourcarrService()
    source_row("manual-steal-base", "Manual Steal Base", 10)
    source_row("manual-steal-src", "Manual Steal Source", 20)
    target, _, _ = s.ingest_manga("manual-steal-base", manga(19301, "Target Work"))
    owner, _, _ = s.ingest_manga("manual-steal-src", manga(19302, "Wrong Owner Work"))

    candidate = manga(19302, "Correct Manual Choice")
    result = asyncio.run(s.manual_match(
        target, "manual-steal-src", 19302, "replace", candidate=candidate, defer_hydration=True,
    ))
    assert owner in result["forced_from_entry_ids"]
    with db.catalog() as c:
        active = list(c.execute(
            "SELECT entry_id,locked,match_type FROM source_matches WHERE source_id=? AND source_manga_id=? AND active=1",
            ("manual-steal-src", 19302),
        ))
    assert len(active) == 1
    assert int(active[0]["entry_id"]) == target
    assert int(active[0]["locked"]) == 1
    assert active[0]["match_type"] == "manual"


def test_duplicate_merge_tolerates_inactive_same_source_tombstone():
    s = AutoSourcarrService()
    source_row("merge-tomb-a", "Merge Tomb A", 10)
    source_row("merge-tomb-shared", "Merge Tomb Shared", 20)
    left, _, _ = s.ingest_manga("merge-tomb-a", manga(19401, "Merge Tomb Left"))
    right, _, _ = s.ingest_manga("merge-tomb-shared", manga(19402, "Merge Tomb Right"))
    # Create the exact state that caused UNIQUE(entry_id, source_id): the survivor
    # has an inactive row for the source while the losing entry has an active row.
    s._upsert_match(left, "merge-tomb-shared", manga(19403, "Old Cleared Match"), 1.0)
    s.clear_match(left, "merge-tomb-shared")
    merged = s.merge_entries(left, right, "test-inactive-tombstone")
    with db.catalog() as c:
        rows = list(c.execute(
            "SELECT entry_id,source_manga_id,active FROM source_matches WHERE entry_id=? AND source_id=?",
            (merged, "merge-tomb-shared"),
        ))
    assert len(rows) == 1
    assert int(rows[0]["active"]) == 1
    assert int(rows[0]["source_manga_id"]) == 19402


def test_ingest_does_not_collapse_multiple_existing_duplicate_entries():
    s = AutoSourcarrService()
    desc = (
        "A single serialized work is mirrored by many catalog records. Every record describes the same hero, "
        "same expedition and same supporting cast so the rows are strong duplicate-review candidates."
    )
    existing = []
    for idx in range(5):
        sid = f"cluster-{idx}"
        source_row(sid, f"Cluster {idx}", 10 + idx)
        eid = s._create_entry(manga(20000 + idx, f"Unlimited Duplicate Work {idx if idx else ''}".strip(), desc), sid)
        s._upsert_match(eid, sid, manga(20000 + idx, "Unlimited Duplicate Work", desc), 1.0)
        s.add_title(eid, "Unlimited Duplicate Work", title_type="shared")
        existing.append(eid)
    source_row("cluster-new", "Cluster New", 50)
    eid, _, reason = s.ingest_manga("cluster-new", manga(20999, "Unlimited Duplicate Work", desc))
    assert reason == "ambiguous-local-candidates:new-entry"
    assert eid not in existing
    assert len({s.canonical_entry_id(old) for old in existing}) == len(existing)
    assert s.get_entry(eid)["match_count"] == 1

    # Explicit duplicate consolidation remains available and has no cluster-size cap.
    remap = s.consolidate_entries(existing + [eid])
    canonical_ids = {s.canonical_entry_id(remap.get(old, old)) for old in existing + [eid]}
    assert len(canonical_ids) == 1


def test_consolidation_has_no_twenty_entry_duplicate_cap():
    s = AutoSourcarrService()
    ids = []
    desc = "A long enough shared synopsis confirms every record belongs to the same unlimited duplicate cluster in this stress test."
    for idx in range(25):
        sid = f"many-{idx}"
        source_row(sid, f"Many {idx}", idx + 1)
        payload = manga(21000 + idx, "Twenty Five Copy Work", desc)
        eid = s._create_entry(payload, sid)
        s._upsert_match(eid, sid, payload, 1.0)
        ids.append(eid)
    remap = s.consolidate_entries(ids)
    canonical_ids = {s.canonical_entry_id(remap.get(eid, eid)) for eid in ids}
    assert len(canonical_ids) == 1
    assert s.get_entry(next(iter(canonical_ids)))["match_count"] == 25


def test_manual_search_generates_transliteration_and_origin_translation(monkeypatch):
    from app.config import settings
    from app.translation import translator

    s = AutoSourcarrService()
    source_row("translation-search", "Translation Search", 10)
    payload = manga(22001, "The Fastest Dungeon Explorer")
    payload["genre"] = ["Manga", "Action"]
    eid, _, _ = s.ingest_manga("translation-search", payload)
    s.add_title(eid, "最速ダンジョン探索者", language="ja", title_type="manual")

    async def fake_translate(text, target, source="auto"):
        if target == "en" and "探索者" in text:
            return "The Fastest Dungeon Explorer"
        if target == "ja" and text == "The Fastest Dungeon Explorer":
            return "最速ダンジョン探索者"
        return ""

    monkeypatch.setattr(translator, "translate", fake_translate)
    monkeypatch.setattr(settings, "manual_search_alias_limit", 30)
    names, meta = asyncio.run(s._expanded_search_names(eid, "translation-search"))
    assert "The Fastest Dungeon Explorer" in names
    assert "最速ダンジョン探索者" in names
    assert any(m["kind"] == "transliteration" for m in meta)


def test_manual_search_uses_literal_translation_of_native_candidate(monkeypatch):
    from app.translation import translator

    s = AutoSourcarrService()
    source_row("literal-search", "Literal Search", 10)
    eid, _, _ = s.ingest_manga("literal-search", manga(23001, "Return of the Frozen Player"))

    async def fake_fetch(source_id, mode, page, query=""):
        return {"mangas": [manga(23002, "얼어붙은 플레이어의 귀환")], "hasNextPage": False}

    async def fake_translate(text, target, source="auto"):
        if target == "en" and "플레이어" in text:
            return "Return of the Frozen Player"
        return ""

    monkeypatch.setattr(source_host, "fetch_source_manga", fake_fetch)
    monkeypatch.setattr(translator, "translate", fake_translate)
    result = asyncio.run(s.search_source_for_manual(eid, "literal-search"))
    assert result["mangas"]
    translated = next(x for x in result["mangas"] if x["id"] == 23002)
    assert translated["autosourcarr_match_score"] >= 0.99
    assert translated["translated_candidate_title"] == "Return of the Frozen Player"


def test_additional_matches_expose_their_chapter_contribution(monkeypatch):
    s = AutoSourcarrService()
    source_row("contribution-src", "Contribution Source", 10)
    desc = "Two mirror records of this exact same story each provide different missing chapters for one unified entry."
    first = manga(24001, "Contribution Work", desc)
    second = manga(24002, "Contribution Work", desc)
    eid, _, _ = s.ingest_manga("contribution-src", first)
    eid2, _, _ = s.ingest_manga("contribution-src", second)
    assert eid2 == eid

    async def fake_fetch(manga_id, fetch_manga=True, fetch_chapters=True):
        if int(manga_id) == 24001:
            return {"manga": first, "chapters": [chapter(24011, 1.0, 20)]}
        return {"manga": second, "chapters": [chapter(24022, 2.0, 20)]}

    monkeypatch.setattr(source_host, "fetch_manga_and_chapters", fake_fetch)
    asyncio.run(s.refresh_entry(eid))
    entry = s.get_entry(eid)
    assert entry["matches"][0]["chapter_count"] == 1
    assert entry["source_variants"][0]["chapter_count"] == 1
    assert {c["chapter_number"] for c in s.list_chapters(eid)} == {1.0, 2.0}


def test_reviewed_nonduplicate_pair_disappears_from_duplicate_candidates():
    s = AutoSourcarrService()
    source_row("review-a", "Review A", 10)
    source_row("review-b", "Review B", 20)
    a = manga(25001, "Distinct Display A", "")
    b = manga(25002, "Distinct Display B", "")
    a["author"] = "Author One"
    b["author"] = "Author Two"
    e1 = s._create_entry(a, "review-a"); s._upsert_match(e1, "review-a", a, 1.0)
    e2 = s._create_entry(b, "review-b"); s._upsert_match(e2, "review-b", b, 1.0)
    s.add_title(e1, "Shared Review Alias", title_type="manual")
    s.add_title(e2, "Shared Review Alias", title_type="manual")
    assert e2 in s.duplicate_candidates([e1, e2])[e1]["entry_ids"]
    result = s.review_duplicate(e1, e2, "not_duplicate")
    assert result["decision"] == "not_duplicate"
    assert s.duplicate_candidates([e1, e2]) == {}


def test_manual_duplicate_review_merges_and_preserves_source_records():
    s = AutoSourcarrService()
    source_row("review-merge-a", "Review Merge A", 10)
    source_row("review-merge-b", "Review Merge B", 20)
    a = manga(25101, "Manual Duplicate A")
    b = manga(25102, "Manual Duplicate B")
    e1 = s._create_entry(a, "review-merge-a"); s._upsert_match(e1, "review-merge-a", a, 1.0)
    e2 = s._create_entry(b, "review-merge-b"); s._upsert_match(e2, "review-merge-b", b, 1.0)
    result = s.review_duplicate(e1, e2, "duplicate")
    canonical = result["canonical_entry_id"]
    assert s.canonical_entry_id(e1) == canonical
    assert s.canonical_entry_id(e2) == canonical
    assert s.get_entry(canonical)["match_count"] == 2
    # A user-confirmed same-work decision is durable identity, not a transient
    # duplicate-row merge. Every source record from both sides becomes a locked
    # manual binding and the pair appears under Manual groups.
    assert result["manual_group_id"]
    assert result["manual_group_members"] == 2
    groups = s.manual_groups_payload()["groups"]
    group = next(g for g in groups if g["id"] == result["manual_group_id"])
    assert group["member_count"] == 2
    assert {(m["source_id"], m["source_manga_id"]) for m in group["members"]} == {
        ("review-merge-a", 25101), ("review-merge-b", 25102)
    }
    entry = s.get_entry(canonical)
    assert all(int(m["locked"]) == 1 and m["match_type"] == "manual" for m in entry["matches"])


def test_manual_search_returns_cached_candidates_when_live_source_times_out(monkeypatch):
    from app.config import settings
    s = AutoSourcarrService()
    source_row("manual-target", "Manual Target", 10)
    source_row("manual-cache", "Manual Cache", 20)
    target, _, _ = s.ingest_manga("manual-target", manga(25201, "Cached Manual Work"))
    cached, _, _ = s.ingest_manga("manual-cache", manga(25202, "Cached Manual Work Mirror"))
    s.add_title(cached, "Cached Manual Work", title_type="manual")

    async def slow_fetch(*args, **kwargs):
        await asyncio.sleep(0.1)
        return {"mangas": [], "hasNextPage": False}

    monkeypatch.setattr(source_host, "fetch_source_manga", slow_fetch)
    monkeypatch.setattr(settings, "manual_search_timeout", 0.01)
    monkeypatch.setattr(settings, "manual_search_overall_timeout", 0.02)
    result = asyncio.run(s.search_source_for_manual(target, "manual-cache", "", 1, True))
    assert result["mangas"]
    assert result["mangas"][0]["id"] == 25202
    assert result["error"] == ""
    assert result["partial"] is True


def test_auto_additional_record_discovery_keeps_every_strong_result_and_chapters(monkeypatch):
    from app.config import settings
    s = AutoSourcarrService()
    source_row("auto-multi", "Auto Multi", 10)
    desc = "The exact same serialized expedition is mirrored under several source records and every record contributes a different chapter to the same work."
    primary = manga(25300, "Automatic Multi Record Work", desc)
    eid, _, _ = s.ingest_manga("auto-multi", primary)
    results = [primary] + [manga(25300+i, "Automatic Multi Record Work", desc) for i in range(1, 5)]

    async def fake_search(source_id, mode, page, query=""):
        return {"mangas": results, "hasNextPage": False}

    async def fake_detail(mid, fetch_manga=True, fetch_chapters=True):
        item = next(x for x in results if int(x["id"]) == int(mid))
        number = int(mid) - 25300 + 1
        return {"manga": item, "chapters": [chapter(25400+number, float(number), 20)]}

    async def no_translate(*args, **kwargs):
        return ""

    monkeypatch.setattr(source_host, "fetch_source_manga", fake_search)
    monkeypatch.setattr(source_host, "fetch_manga_and_chapters", fake_detail)
    monkeypatch.setattr(settings, "auto_variant_query_limit", 6)
    result = asyncio.run(s.discover_additional_source_records(eid))
    assert result["added"] == 4
    entry = s.get_entry(eid)
    assert len(entry["source_variants"]) == 4
    assert {x["source_manga_id"] for x in entry["source_variants"]} == {25301,25302,25303,25304}
    assert {x["chapter_number"] for x in s.list_chapters(eid)} == {2.0,3.0,4.0,5.0}


def test_entry_cover_hint_falls_back_to_additional_record_thumbnail():
    s = AutoSourcarrService()
    source_row("cover-variant", "Cover Variant", 10)
    primary = manga(25501, "Variant Cover Work")
    primary["thumbnailUrl"] = ""
    eid, _, _ = s.ingest_manga("cover-variant", primary)
    variant = manga(25502, "Variant Cover Work")
    variant["thumbnailUrl"] = "https://images.invalid/variant-cover.jpg"
    s._upsert_source_variant(eid, "cover-variant", variant, 1.0, "auto-additional", False)
    hint = s.entry_cover_hint(eid)
    assert hint["source_manga_id"] == 25502
    assert hint["thumbnail_url"].endswith("variant-cover.jpg")


def test_duplicate_groups_show_explicit_counterpart_evidence():
    s = AutoSourcarrService()
    source_row("group-a", "Group A", 10)
    source_row("group-b", "Group B", 20)
    a = manga(25601, "Visible Candidate One", "")
    b = manga(25602, "Visible Candidate Two", "")
    a["author"] = "Different A"; b["author"] = "Different B"
    e1 = s._create_entry(a, "group-a"); s._upsert_match(e1, "group-a", a, 1.0)
    e2 = s._create_entry(b, "group-b"); s._upsert_match(e2, "group-b", b, 1.0)
    s.add_title(e1, "Shared Explicit Duplicate Alias", title_type="manual")
    s.add_title(e2, "Shared Explicit Duplicate Alias", title_type="manual")
    groups = s.duplicate_groups()
    group = next(g for g in groups if e1 in g["entry_ids"] and e2 in g["entry_ids"])
    assert group["pairs"]
    pair = group["pairs"][0]
    assert {pair["left_id"], pair["right_id"]} == {e1, e2}
    assert pair["matched_title"]
    assert pair["other_title"]


def test_source_cache_reflects_database_changes_after_service_construction():
    s = AutoSourcarrService()
    source_row("late-source-cache", "Late Source Cache", 9)
    rows = {x["source_id"]: x for x in s.list_sources()}
    assert "late-source-cache" in rows
    assert rows["late-source-cache"]["priority"] == 9


def test_resolve_pages_reuses_persistent_page_map(monkeypatch):
    s = AutoSourcarrService()
    source_row("page-cache-source", "Page Cache Source", 10)
    item = manga(26001, "Page Cache Work")
    eid, _, _ = s.ingest_manga("page-cache-source", item)
    match_id = s._match_id(eid, "page-cache-source")
    s._ingest_chapters(
        eid,
        {"source_id": "page-cache-source", "source_name": "Page Cache Source", "source_manga_id": 26001},
        match_id,
        [chapter(26002, 1.0, 3)],
    )
    virtual_id = s.list_chapters(eid)[0]["id"]
    calls = {"pages": 0, "index": 0}

    async def fake_pages(chapter_id):
        calls["pages"] += 1
        return [f"https://images.invalid/{chapter_id}/{i}.jpg" for i in range(3)]

    async def fake_index(manga_id, chapter_id):
        calls["index"] += 1
        return 0

    monkeypatch.setattr(source_host, "fetch_chapter_pages", fake_pages)
    monkeypatch.setattr(source_host, "chapter_index_for_id", fake_index)
    first = asyncio.run(s.resolve_pages(virtual_id))
    second = asyncio.run(s.resolve_pages(virtual_id))
    assert first["page_cache_hit"] is False
    assert second["page_cache_hit"] is True
    assert calls == {"pages": 1, "index": 1}
    assert first["pages"] == second["pages"]


def test_resolve_pages_uses_stale_cache_when_source_host_is_temporarily_unavailable(monkeypatch):
    s = AutoSourcarrService()
    source_row("stale-page-source", "Stale Page Source", 10)
    item = manga(26101, "Stale Page Work")
    eid, _, _ = s.ingest_manga("stale-page-source", item)
    match_id = s._match_id(eid, "stale-page-source")
    s._ingest_chapters(
        eid,
        {"source_id": "stale-page-source", "source_name": "Stale Page Source", "source_manga_id": 26101},
        match_id,
        [chapter(26102, 1.0, 2)],
    )
    virtual_id = s.list_chapters(eid)[0]["id"]

    async def first_pages(chapter_id):
        return [f"https://images.invalid/{chapter_id}/0.jpg", f"https://images.invalid/{chapter_id}/1.jpg"]

    async def fake_index(manga_id, chapter_id):
        return 0

    monkeypatch.setattr(source_host, "fetch_chapter_pages", first_pages)
    monkeypatch.setattr(source_host, "chapter_index_for_id", fake_index)
    first = asyncio.run(s.resolve_pages(virtual_id))
    assert first["page_cache_stale"] is False

    with db.origins() as o:
        o.execute("UPDATE candidate_pages SET updated_at='2000-01-01T00:00:00+00:00'")

    async def offline_pages(chapter_id):
        raise httpx.ConnectError("source host temporarily unavailable")

    monkeypatch.setattr(source_host, "fetch_chapter_pages", offline_pages)
    fallback = asyncio.run(s.resolve_pages(virtual_id))
    assert fallback["page_cache_hit"] is True
    assert fallback["page_cache_stale"] is True
    assert "source host temporarily unavailable" in fallback["source_warning"]
    assert fallback["pages"] == first["pages"]


def test_resolve_pages_falls_back_to_next_candidate_on_source_specific_failure(monkeypatch):
    s = AutoSourcarrService()
    source_row("fallback-a", "Fallback A", 1)
    source_row("fallback-b", "Fallback B", 20)
    eid, _, _ = s.ingest_manga("fallback-a", manga(26201, "Fallback Work"))
    s._upsert_match(eid, "fallback-b", manga(26301, "Fallback Work"), 1.0)
    s._ingest_chapters(
        eid,
        {"source_id": "fallback-a", "source_name": "Fallback A", "source_manga_id": 26201},
        s._match_id(eid, "fallback-a"),
        [chapter(26202, 1.0, 3)],
    )
    s._ingest_chapters(
        eid,
        {"source_id": "fallback-b", "source_name": "Fallback B", "source_manga_id": 26301},
        s._match_id(eid, "fallback-b"),
        [chapter(26302, 1.0, 3)],
    )
    virtual_id = s.list_chapters(eid)[0]["id"]

    async def pages(chapter_id):
        if int(chapter_id) == 26202:
            raise RuntimeError("source-specific chapter failure")
        return [f"https://images.invalid/{chapter_id}/{i}.jpg" for i in range(3)]

    async def fake_index(manga_id, chapter_id):
        return 0

    monkeypatch.setattr(source_host, "fetch_chapter_pages", pages)
    monkeypatch.setattr(source_host, "chapter_index_for_id", fake_index)
    resolved = asyncio.run(s.resolve_pages(virtual_id))
    assert resolved["candidate"]["source_id"] == "fallback-b"
    assert resolved["page_cache_hit"] is False
    assert len(resolved["pages"]) == 3


def test_common_title_words_do_not_create_quadratic_duplicate_noise():
    s = AutoSourcarrService()
    source_row("duplicate-scale-source", "Duplicate Scale Source", 10)
    ids = []
    for i in range(80):
        item = manga(27000 + i, f"Generic Adventure Chronicle UniqueToken{i}", author=f"Author {i}")
        eid = s._create_entry(item, "duplicate-scale-source")
        s._upsert_match(eid, "duplicate-scale-source", item, 1.0)
        ids.append(eid)
    candidates = s.duplicate_candidates(ids)
    assert not any(eid in candidates for eid in ids)


def test_refresh_entry_honors_detail_and_chapter_flags(monkeypatch):
    s = AutoSourcarrService()
    source_row("flag-src", "Flag Source", 10)
    work = manga(99001, "Flagged Refresh Work")
    eid, _, _ = s.ingest_manga("flag-src", work)
    calls = []

    async def fake_fetch(manga_id, fetch_manga=True, fetch_chapters=True):
        calls.append((int(manga_id), bool(fetch_manga), bool(fetch_chapters)))
        return {
            "manga": work if fetch_manga else {},
            "chapters": [chapter(99011, 1.0, 12)] if fetch_chapters else [],
        }

    monkeypatch.setattr(source_host, "fetch_manga_and_chapters", fake_fetch)

    asyncio.run(s.refresh_entry(eid, fetch_details=True, fetch_chapters=False))
    assert calls[-1] == (99001, True, False)
    assert s.list_chapters(eid) == []

    asyncio.run(s.refresh_entry(eid, fetch_details=False, fetch_chapters=True))
    assert calls[-1] == (99001, False, True)
    assert {row["chapter_number"] for row in s.list_chapters(eid)} == {1.0}


def test_chapter_order_change_invalidates_cached_page_index_and_map():
    s = AutoSourcarrService()
    source_row("order-src", "Order Source", 10)
    work = manga(99101, "Order Shift Work")
    eid, _, _ = s.ingest_manga("order-src", work)
    match_id = s._match_id(eid, "order-src")
    first = chapter(99111, 1.0, 3)
    first["sourceOrder"] = 10
    s._ingest_chapters(eid, {"source_id": "order-src", "source_name": "Order Source", "source_manga_id": 99101}, match_id, [first])
    with db.origins() as o:
        cand = o.execute("SELECT id FROM chapter_candidates WHERE source_id='order-src' AND source_chapter_id=99111").fetchone()
        cid = int(cand["id"])
        o.execute("UPDATE chapter_candidates SET source_chapter_index=7 WHERE id=?", (cid,))
        o.execute("INSERT INTO candidate_pages(candidate_id,page_index,private_url,updated_at) VALUES(?,?,?,?)", (cid, 0, "https://old.invalid/0.jpg", now_iso()))
    s._remember_page_info(cid, 0, {"private_url": "https://old.invalid/0.jpg"})

    shifted = dict(first)
    shifted["sourceOrder"] = 11
    s._ingest_chapters(eid, {"source_id": "order-src", "source_name": "Order Source", "source_manga_id": 99101}, match_id, [shifted])

    with db.origins() as o:
        row = o.execute("SELECT source_chapter_index FROM chapter_candidates WHERE id=?", (cid,)).fetchone()
        pages = o.execute("SELECT COUNT(*) n FROM candidate_pages WHERE candidate_id=?", (cid,)).fetchone()
    assert int(row["source_chapter_index"]) == -1
    assert int(pages["n"]) == 0
    assert (cid, 0) not in s._page_info_cache


def test_manual_search_hydrates_entry_only_once(monkeypatch):
    s = AutoSourcarrService()
    source_row("manual-hydrate", "Manual Hydrate", 10)
    source_row("manual-hydrate-target", "Manual Hydrate Target", 20)
    eid, _, _ = s.ingest_manga("manual-hydrate", manga(99201, "Single Hydration Work"))
    s.ingest_manga("manual-hydrate-target", manga(99202, "Single Hydration Work Mirror"))

    original_get_entry = s.get_entry
    calls = {"get_entry": 0}

    def counted_get_entry(entry_id):
        calls["get_entry"] += 1
        return original_get_entry(entry_id)

    async def empty_search(source_id, mode, page, query=""):
        return {"mangas": [], "hasNextPage": False}

    monkeypatch.setattr(s, "get_entry", counted_get_entry)
    monkeypatch.setattr(source_host, "fetch_source_manga", empty_search)
    asyncio.run(s.search_source_for_manual(eid, "manual-hydrate-target", "", 1, True))
    assert calls["get_entry"] == 1


def test_additional_record_scoring_reuses_preloaded_entry_context(monkeypatch):
    from app.config import settings

    s = AutoSourcarrService()
    source_row("variant-context", "Variant Context", 10)
    desc = "A sufficiently detailed shared synopsis used to prove that additional source record scoring can reuse one immutable entry context."
    primary = manga(99300, "Variant Context Work", desc)
    eid, _, _ = s.ingest_manga("variant-context", primary)
    candidates = [primary, manga(99301, "Variant Context Work", desc), manga(99302, "Variant Context Work", desc)]

    async def fake_search(source_id, mode, page, query=""):
        return {"mangas": candidates, "hasNextPage": False}

    async def fake_detail(mid, fetch_manga=True, fetch_chapters=True):
        item = next(x for x in candidates if int(x["id"]) == int(mid))
        return {"manga": item, "chapters": []}

    def unexpected_get_entry(*args, **kwargs):
        raise AssertionError("additional-record candidate scoring rehydrated the complete entry")

    monkeypatch.setattr(source_host, "fetch_source_manga", fake_search)
    monkeypatch.setattr(source_host, "fetch_manga_and_chapters", fake_detail)
    monkeypatch.setattr(settings, "auto_variant_query_limit", 2)
    monkeypatch.setattr(s, "get_entry", unexpected_get_entry)
    result = asyncio.run(s.discover_additional_source_records(eid))
    assert result["added"] == 2


def test_exact_title_duplicate_shortlist_has_no_group_size_ceiling():
    s = AutoSourcarrService()
    source_row("exact-cluster-scale", "Exact Cluster Scale", 10)
    ids = []
    for i in range(120):
        item = manga(99400 + i, "Exact Unlimited Duplicate Title", author=f"Author {i}")
        eid = s._create_entry(item, "exact-cluster-scale")
        s._upsert_match(eid, "exact-cluster-scale", item, 1.0)
        ids.append(eid)
    pairs, _, reasons = s._duplicate_shortlist(ids)
    exact_pairs = [pair for pair in pairs if "exact-title" in reasons.get(pair, set())]
    assert len(exact_pairs) == len(ids) - 1


def test_database_reuses_connection_and_savepoints_for_nested_contexts():
    key_outer = "nested-savepoint-outer"
    key_inner = "nested-savepoint-inner"
    with db.catalog() as first:
        first_id = id(first)
        first.execute("INSERT OR REPLACE INTO settings(key,value) VALUES(?,?)", (key_outer, "1"))
        with db.catalog() as nested:
            assert id(nested) == first_id
            nested.execute("INSERT OR REPLACE INTO settings(key,value) VALUES(?,?)", (key_inner, "2"))
    with db.catalog() as second:
        assert id(second) == first_id
        assert second.execute("SELECT value FROM settings WHERE key=?", (key_outer,)).fetchone()["value"] == "1"
        assert second.execute("SELECT value FROM settings WHERE key=?", (key_inner,)).fetchone()["value"] == "2"


def test_duplicate_reviews_continue_after_mixed_decisions():
    """One review decision must not poison the remaining duplicate queue."""
    s = AutoSourcarrService()
    source_row("review-seq-a", "Review Seq A", 10)
    source_row("review-seq-b", "Review Seq B", 20)
    source_row("review-seq-c", "Review Seq C", 30)
    desc = "A deliberately shared long description used to build a reliable duplicate review cluster with enough text for matching."
    a = manga(31001, "Sequential Review Work", desc, "Same Author")
    b = manga(31002, "Sequential Review Work", desc, "Different Author")
    c = manga(31003, "Sequential Review Work", desc, "Same Author")
    e1 = s._create_entry(a, "review-seq-a"); s._upsert_match(e1, "review-seq-a", a, 1.0)
    e2 = s._create_entry(b, "review-seq-b"); s._upsert_match(e2, "review-seq-b", b, 1.0)
    e3 = s._create_entry(c, "review-seq-c"); s._upsert_match(e3, "review-seq-c", c, 1.0)
    assert s.duplicate_groups()
    first = s.review_duplicate(e1, e2, "not_duplicate")
    assert first["decision"] == "not_duplicate"
    assert isinstance(first["groups"], list)
    # A subsequent merge in the same original group must still work.
    second = s.review_duplicate(e1, e3, "duplicate")
    assert second["merged"] is True
    assert isinstance(second["groups"], list)
    canonical = second["canonical_entry_id"]
    assert s.canonical_entry_id(e1) == canonical
    assert s.canonical_entry_id(e3) == canonical
    # The dismissed e1/e2 relation is remapped and remains dismissed.
    assert s._duplicate_review(canonical, e2) == "not_duplicate"


def test_unified_metadata_uses_rich_additional_source_record():
    """A matched variant can fill missing cover/summary/author fields on the unified entry."""
    s = AutoSourcarrService()
    source_row("meta-primary", "Metadata Primary", 10)
    source_row("meta-extra", "Metadata Extra", 20)
    primary = manga(32001, "Metadata Work", "")
    primary["thumbnailUrl"] = ""
    primary["author"] = ""
    primary["artist"] = ""
    eid, _, _ = s.ingest_manga("meta-primary", primary)

    extra = manga(
        32002,
        "Metadata Work",
        "This is a substantially richer summary from another matched record and should be retained on the unified entry.",
        "Rich Author",
    )
    extra["thumbnailUrl"] = "https://images.example.invalid/metadata-work.jpg"
    s._upsert_source_variant(eid, "meta-extra", extra, 0.99, "manual", True)
    s._choose_metadata(eid)
    entry = s.get_entry(eid)
    assert entry["thumbnail_url"] == extra["thumbnailUrl"]
    assert "substantially richer summary" in entry["description"]
    assert entry["author"] == "Rich Author"


def test_new_match_hydrates_full_source_metadata_even_when_list_card_looks_complete(monkeypatch):
    s = AutoSourcarrService()
    source_row("meta-full-new", "Metadata Full New", 10)
    listed = manga(32991, "Complete Looking List Card", "This browse result already has a long enough summary and author, but still omits other detail-only fields.", "List Author")
    listed["thumbnailUrl"] = "https://images.example.invalid/listed.jpg"
    listed["artist"] = ""
    listed["genre"] = []
    listed["status"] = 0

    detailed = dict(listed)
    detailed["artist"] = "Detail Artist"
    detailed["genre"] = ["Drama", "Mystery"]
    detailed["status"] = 2
    detailed["description"] = "This is the authoritative detailed summary returned by the source detail endpoint after matching."

    async def fake_fetch(manga_id, fetch_manga=True, fetch_chapters=True):
        assert int(manga_id) == 32991
        assert fetch_manga is True and fetch_chapters is False
        return {"manga": detailed, "chapters": []}

    monkeypatch.setattr(source_host, "fetch_manga_and_chapters", fake_fetch)

    async def scenario():
        eid, _, reason = s.ingest_manga("meta-full-new", listed)
        assert reason == "new-entry"
        tasks = list(s._metadata_tasks.values())
        assert tasks
        await asyncio.gather(*tasks)
        return eid

    eid = asyncio.run(scenario())
    entry = s.get_entry(eid)
    assert entry["artist"] == "Detail Artist"
    assert "Drama" in entry["genre"] and "Mystery" in entry["genre"]
    assert entry["status"] == 2
    assert "authoritative detailed summary" in entry["description"]


def test_background_metadata_enrichment_fetches_missing_cover_and_summary(monkeypatch):
    s = AutoSourcarrService()
    source_row("meta-bg", "Metadata Background", 10)
    sparse = manga(33001, "Qzxyv Background Metadata 33001", "")
    sparse["thumbnailUrl"] = ""
    sparse["author"] = ""
    eid, _, _ = s.ingest_manga("meta-bg", sparse)

    rich = manga(33001, "Qzxyv Background Metadata 33001", "A detailed summary loaded after browse so the initial feed remains fast while metadata becomes complete.", "Background Author")
    rich["thumbnailUrl"] = "https://images.example.invalid/background.jpg"

    async def fake_fetch(manga_id, fetch_manga=True, fetch_chapters=True):
        assert int(manga_id) == 33001
        assert fetch_manga is True
        assert fetch_chapters is False
        return {"manga": rich, "chapters": []}

    monkeypatch.setattr(source_host, "fetch_manga_and_chapters", fake_fetch)
    result = asyncio.run(s.enrich_entry_metadata(eid))
    assert result["thumbnail_url"] == rich["thumbnailUrl"]
    assert "initial feed remains fast" in result["description"]
    assert result["author"] == "Background Author"


def test_sparse_refresh_does_not_erase_rich_match_metadata():
    s = AutoSourcarrService()
    source_row("metadata-preserve", "Metadata Preserve", 10)
    rich = manga(99501, "Metadata Preserve Work", "A long and useful synopsis that should survive a later sparse source-search result without being erased from the catalog.")
    rich["thumbnailUrl"] = "https://images.invalid/cover-rich.jpg"
    rich["author"] = "Rich Author"
    eid, _, _ = s.ingest_manga("metadata-preserve", rich)

    sparse = manga(99501, "Metadata Preserve Work", "", author="")
    sparse["thumbnailUrl"] = ""
    sparse["initialized"] = False
    sparse["genre"] = []
    sparse["status"] = 0
    s.ingest_manga("metadata-preserve", sparse)

    entry = s.get_entry(eid)
    assert entry["thumbnail_url"] == "https://images.invalid/cover-rich.jpg"
    assert "long and useful synopsis" in entry["description"]
    assert entry["author"] == "Rich Author"


def test_entry_cover_hint_keeps_source_manga_id_without_thumbnail_url():
    s = AutoSourcarrService()
    source_row("cover-fallback", "Cover Fallback", 10)
    item = manga(99511, "Cover Fallback Work", "")
    item["initialized"] = False
    item["thumbnailUrl"] = ""
    eid, _, _ = s.ingest_manga("cover-fallback", item)
    hint = s.entry_cover_hint(eid)
    assert hint["source_id"] == "cover-fallback"
    assert int(hint["source_manga_id"]) == 99511


def test_entry_cover_hints_include_all_matched_sources_for_failover():
    s = AutoSourcarrService()
    source_row("cover-failover-a", "Cover Failover A", 1)
    source_row("cover-failover-b", "Cover Failover B", 20)

    first = manga(99711, "Cover Failover Work", "")
    first["thumbnailUrl"] = ""
    first["initialized"] = False
    second = manga(99712, "Cover Failover Work", "A richer record used as a reliable alternate cover source for the unified work.")
    second["thumbnailUrl"] = "https://images.invalid/failover-cover.jpg"
    second["initialized"] = True

    eid, _, _ = s.ingest_manga("cover-failover-a", first)
    eid2, _, _ = s.ingest_manga("cover-failover-b", second)
    assert eid2 == eid
    hints = s.entry_cover_hints(eid)
    ids = {int(h["source_manga_id"]) for h in hints if h.get("source_manga_id")}
    assert {99711, 99712} <= ids
    assert hints[0]["thumbnail_url"] == "https://images.invalid/failover-cover.jpg"




def test_sparse_metadata_repair_detects_detail_only_missing_fields(monkeypatch):
    s = AutoSourcarrService()
    source_row("repair-detail-only", "Repair Detail Only", 10)
    item = manga(99991, "Repair Detail Only Work", "This is a long complete-looking browse summary that deliberately leaves only detail fields missing.", "Known Author")
    item["thumbnailUrl"] = "https://images.invalid/repair-detail.jpg"
    item["artist"] = ""
    item["genre"] = []
    item["status"] = 0
    eid = s._create_entry(item, "repair-detail-only")
    s._upsert_match(eid, "repair-detail-only", item, 1.0)
    s._choose_metadata(eid)

    captured = {}
    def fake_schedule(ids, max_entries=None):
        captured["ids"] = list(ids)
        captured["max_entries"] = max_entries
        return len(ids)
    monkeypatch.setattr(s, "schedule_metadata_enrichment", fake_schedule)
    result = s.queue_sparse_metadata_enrichment(5000)
    assert eid in captured["ids"]
    assert result["queued"] >= 1


def test_entry_summaries_expose_metadata_completeness_for_extension_cards():
    s = AutoSourcarrService()
    source_row("metadata-card-state", "Metadata Card State", 10)
    sparse = manga(99992, "Sparse Card Metadata", "")
    sparse["thumbnailUrl"] = ""
    sparse["author"] = ""
    sparse["artist"] = ""
    sparse["genre"] = []
    sparse["status"] = 0
    eid = s._create_entry(sparse, "metadata-card-state")
    s._upsert_match(eid, "metadata-card-state", sparse, 1.0)
    s._choose_metadata(eid)

    card = s._entry_summaries([eid])[0]
    assert card["metadata_complete"] is False
    assert {"thumbnail", "description", "author", "artist", "genre", "status"} <= set(card["missing_metadata"])


def test_memory_browse_cache_still_schedules_sparse_metadata_repair(monkeypatch):
    from app.config import settings
    s = AutoSourcarrService()
    source_row("memory-metadata", "Memory Metadata", 10)
    sparse = manga(99993, "Cached Sparse Metadata", "")
    eid = s._create_entry(sparse, "memory-metadata")
    s._upsert_match(eid, "memory-metadata", sparse, 1.0)
    scheduled, repaired = [], []
    monkeypatch.setattr(settings, "metadata_identity_enabled", False)
    monkeypatch.setattr(s, "_load_persistent_browse_cache", lambda key: None)
    monkeypatch.setattr(s, "_catalog_fallback", lambda *args, **kwargs: {"items": []})
    original_schedule = s.schedule_metadata_enrichment
    def capture_schedule(ids, max_entries=None, **kwargs):
        scheduled.extend(ids)
        return original_schedule(ids, max_entries, **kwargs)
    monkeypatch.setattr(s, "schedule_metadata_enrichment", capture_schedule)

    key = ("POPULAR", 1, "", (), 1, None, "relevance", 50)
    import time
    s._search_cache[key] = (time.monotonic(), {
        "items": [{"id": eid, "canonical_id": eid, "preferred_title": "Cached sparse card"}],
        "has_next_page": False,
        "source_errors": [],
        "cache_hit": False,
        "persistent_cache": False,
        "refreshing": False,
    })

    async def scenario():
        idle, completed = asyncio.Event(), asyncio.Event()
        monkeypatch.setattr(s, "_wait_for_background_idle", idle.wait)
        async def repair(entry_id):
            repaired.append(entry_id)
            completed.set()
        monkeypatch.setattr(s, "enrich_entry_metadata", repair)
        try:
            result = await s.federated("POPULAR")
            assert result["cache_hit"] is True
            assert scheduled == []  # optional repair never gates the cache response
            planners = [task for key, task in s._metadata_tasks.items() if key[0] == "feed-plan"]
            assert len(planners) == 1
            idle.set()
            await asyncio.wait_for(asyncio.gather(*planners), timeout=1)
            await asyncio.wait_for(completed.wait(), timeout=1)
            assert scheduled == [eid]
            assert repaired == [eid]  # actual scheduler admitted the sparse-entry worker
        finally:
            idle.set()
            await s.cancel_background_tasks()
    asyncio.run(scenario())


def test_federated_browse_returns_source_error_instead_of_500_when_source_host_is_down(monkeypatch):
    s = AutoSourcarrService()
    monkeypatch.setattr(s, "list_sources", lambda: [])
    monkeypatch.setattr(s, "_load_persistent_browse_cache", lambda key: None)
    monkeypatch.setattr(s, "_catalog_fallback", lambda *args, **kwargs: {"items": []})

    async def broken_sync():
        raise RuntimeError("source host unavailable")

    monkeypatch.setattr(s, "sync_sources", broken_sync)
    result = asyncio.run(s.federated("POPULAR", force_live=True))
    assert result["items"] == []
    assert result["source_errors"]
    assert result["source_errors"][0]["source_id"] == "source-host"


def test_federated_browse_custom_page_size_is_request_scoped_and_cached_separately(monkeypatch):
    s = AutoSourcarrService()
    source_row("page-size-src", "Page Size Source", 1)

    async def fake_fetch_source_manga(source_id, mode, page, query=""):
        if source_id != "page-size-src":
            return {"mangas": [], "hasNextPage": False}
        return {
            "mangas": [manga(880000 + i, f"Page Size Work {i:02d}") for i in range(12)],
            "hasNextPage": True,
        }

    monkeypatch.setattr(source_host, "fetch_source_manga", fake_fetch_source_manga)

    small = asyncio.run(s.federated("POPULAR", source_ids=["page-size-src"], page_size=3, force_live=True))
    large = asyncio.run(s.federated("POPULAR", source_ids=["page-size-src"], page_size=7, force_live=True))
    assert small["page_size"] == 3
    assert large["page_size"] == 7
    assert len(small["items"]) == 3
    assert len(large["items"]) == 7
    assert s._browse_cache_key("POPULAR", 1, "", ["page-size-src"], 1, None, "relevance", 3) != s._browse_cache_key("POPULAR", 1, "", ["page-size-src"], 1, None, "relevance", 7)


def test_auto_duplicate_merge_requires_two_exact_titles_or_summary(monkeypatch):
    from app.config import settings
    s = AutoSourcarrService()
    source_row("strict-a", "Strict A", 10); source_row("strict-b", "Strict B", 20)
    a = manga(31001, "One Exact Name", "")
    b = manga(31002, "One Exact Name", "")
    e1=s._create_entry(a,"strict-a"); s._upsert_match(e1,"strict-a",a,1.0)
    e2=s._create_entry(b,"strict-b"); s._upsert_match(e2,"strict-b",b,1.0)
    assert s._auto_merge_evidence(e1,e2)["exact_title_count"] == 1
    assert s.consolidate_entries([e1,e2])[e1] != s.consolidate_entries([e1,e2])[e2]
    s.add_title(e1,"Second Exact Alias",title_type="manual")
    s.add_title(e2,"Second Exact Alias",title_type="manual")
    evidence=s._auto_merge_evidence(e1,e2)
    assert evidence["eligible"] is True and evidence["exact_title_count"] >= 2


def test_auto_duplicate_merge_summary_threshold_is_configurable(monkeypatch):
    from app.config import settings
    s=AutoSourcarrService(); source_row("sum-a","Summary A",10); source_row("sum-b","Summary B",20)
    text="A sufficiently long shared synopsis about a wandering archivist who searches a ruined city for records of a vanished kingdom and its people."
    text2=text + " The second catalog also mentions a winter expedition and several details that make the wording meaningfully different."
    # Summary is corroboration for one exact source-native title; it is never
    # allowed to connect unrelated display titles by itself.
    a=manga(31101,"Shared Native Name",text); b=manga(31102,"Shared Native Name",text2)
    e1=s._create_entry(a,"sum-a"); s._upsert_match(e1,"sum-a",a,1.0)
    e2=s._create_entry(b,"sum-b"); s._upsert_match(e2,"sum-b",b,1.0)
    monkeypatch.setattr(settings,"duplicate_auto_merge_summary_threshold",0.70)
    assert s._auto_merge_evidence(e1,e2)["eligible"] is True
    monkeypatch.setattr(settings,"duplicate_auto_merge_summary_threshold",0.99)
    assert s._auto_merge_evidence(e1,e2)["eligible"] is False


def test_unmatch_duplicate_records_splits_source_and_removes_its_names():
    s=AutoSourcarrService(); source_row("splitwrong-a","Split A",10); source_row("splitwrong-b","Split B",20)
    a=manga(31201,"Correct Main Work","A main work description long enough to remain as metadata after splitting the wrong source record away from it.")
    b=manga(31202,"Wrong Other Work","A completely unrelated source record with its own wrong alias and descriptive metadata for another title entirely.")
    e1=s._create_entry(a,"splitwrong-a"); s._upsert_match(e1,"splitwrong-a",a,1.0)
    e2=s._create_entry(b,"splitwrong-b"); s._upsert_match(e2,"splitwrong-b",b,1.0)
    merged=s.merge_entries(e1,e2,"test-wrong-merge")
    assert "wrong other work" in {r["normalized"] for r in s._entry_title_rows(merged)}
    out=s.unmatch_duplicate_records(merged,[{"source_id":"splitwrong-b","source_manga_id":31202}])
    assert out["new_entry_id"] != merged
    old=s.get_entry(merged); new=s.get_entry(out["new_entry_id"])
    assert {m["source_id"] for m in old["matches"]} == {"splitwrong-a"}
    assert {m["source_id"] for m in new["matches"]} == {"splitwrong-b"}
    assert "wrong other work" not in {r["normalized"] for r in s._entry_title_rows(merged)}


def test_reset_database_clears_catalog_and_origins():
    s=AutoSourcarrService(); source_row("reset-src","Reset",1)
    eid,_,_=s.ingest_manga("reset-src",manga(31301,"Reset Work"))
    s._ingest_chapters(eid,{"source_id":"reset-src","source_name":"Reset","source_manga_id":31301},s._match_id(eid,"reset-src"),[chapter(31311,1.0,10)])
    out=s.reset_database(); assert out["reset"] is True
    with db.catalog() as c:
        assert c.execute("SELECT COUNT(*) n FROM entries").fetchone()["n"] == 0
        assert c.execute("SELECT name FROM source_config WHERE source_id='reset-src'").fetchone()["name"] == "Reset"
    with db.origins() as o:
        assert o.execute("SELECT COUNT(*) n FROM virtual_chapters").fetchone()["n"] == 0


def test_generated_translation_titles_do_not_create_duplicate_shortlist_edges():
    s = AutoSourcarrService()
    source_row("translation-safe-a", "Translation Safe A", 10)
    source_row("translation-safe-b", "Translation Safe B", 20)
    a = s._create_entry(manga(31401, "Plus Ninety Nine Wooden Stick"), "translation-safe-a")
    s._upsert_match(a, "translation-safe-a", manga(31401, "Plus Ninety Nine Wooden Stick"), 1.0)
    b = s._create_entry(manga(31402, "Million Likes Tournament Romance"), "translation-safe-b")
    s._upsert_match(b, "translation-safe-b", manga(31402, "Million Likes Tournament Romance"), 1.0)
    s.add_title(a, "Bad Shared Machine Translation", language="en", title_type="translated-literal", source_id="translation")
    s.add_title(b, "Bad Shared Machine Translation", language="en", title_type="translated-literal", source_id="translation")
    pairs, _, _ = s._duplicate_shortlist([a, b])
    assert s._duplicate_pair(a, b) not in pairs


def test_translated_only_source_title_cannot_auto_match_without_summary_corroboration():
    s = AutoSourcarrService()
    source_row("translation-target", "Translation Target", 10)
    source_row("translation-candidate", "Translation Candidate", 20)
    eid = s._create_entry(manga(31501, "+99 Reinforced Wooden Stick", ""), "translation-target")
    s._upsert_match(eid, "translation-target", manga(31501, "+99 Reinforced Wooden Stick", ""), 1.0)
    s.add_title(eid, "100-mankai no Suki o Ageru", language="en", title_type="translated-literal", source_id="translation")
    candidate = manga(31502, "100-mankai no Suki o Ageru", "")
    matched, score, reason = s._best_local_match(candidate, "translation-candidate")
    assert matched is None
    assert score == 0.0


def test_metadata_prefers_explicit_english_source_title():
    s = AutoSourcarrService()
    with db.catalog() as c:
        for sid, name, lang, priority in (
            ("title-all", "All-language source", "all", 5),
            ("title-en", "English source", "en", 20),
        ):
            c.execute(
                """INSERT OR REPLACE INTO source_config
                (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_language,enabled_search,enabled_chapters,priority,last_seen,available)
                VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)""",
                (sid, name, name, lang, "https://example.invalid", "", 1, 1, 1, 1, priority, "now", 1),
            )
    wrong = manga(31601, "100-mankai no Suki o Ageru")
    english = manga(31602, "+99 Reinforced Wooden Stick")
    eid = s._create_entry(wrong, "title-all")
    s._upsert_match(eid, "title-all", wrong, 1.0)
    s._upsert_match(eid, "title-en", english, 1.0)
    s._choose_metadata(eid)
    assert s.get_entry(eid)["preferred_title"] == "+99 Reinforced Wooden Stick"


def test_reset_snapshot_restores_same_subscribed_id_and_rebrowses(monkeypatch):
    import app.service as service_module

    s = AutoSourcarrService()
    source_row("reset-subbed-source", "Reset Subbed", 10)
    eid, _, _ = s.ingest_manga("reset-subbed-source", manga(31701, "Subscribed English Work"))

    class FakeMain:
        async def sources(self):
            return [{"id": "autosourcarr-main", "name": "AutoSourcarr", "displayName": "AutoSourcarr"}]

        async def library_mangas(self):
            return [{
                "id": 9001,
                "sourceId": "autosourcarr-main",
                "url": str(eid),
                "title": "Subscribed English Work",
                "thumbnailUrl": "",
                "inLibrary": True,
            }]

    monkeypatch.setattr(service_module, "main_suwayomi", FakeMain())
    snapshot = asyncio.run(s.snapshot_subbed_reset_seeds())
    assert snapshot["ok"] is True
    assert snapshot["seeds"][0]["entry_id"] == eid

    s.reset_database()
    assert s.restore_subbed_reset_seeds(snapshot["seeds"]) == 1
    assert s.get_entry(eid)["preferred_title"] == "Subscribed English Work"

    calls = []

    monkeypatch.setattr(s, "list_sources", lambda: [{"source_id": "reset-subbed-source"}])

    async def fake_discover(entry_id, source_id=None, *, priority_rebuild=False):
        calls.append((entry_id, priority_rebuild))
        return {"entry_id": entry_id, "added": 0}

    monkeypatch.setattr(s, "discover_missing_source_matches", fake_discover)
    result = asyncio.run(s.rebrowse_subbed_reset_seeds(snapshot["seeds"]))
    assert result["searched"] == 1
    assert calls == [(eid, True)]


def test_restored_romaji_library_title_yields_to_trusted_english_source_title():
    s = AutoSourcarrService()
    s.reset_database()
    assert s.restore_subbed_reset_seeds([{
        "entry_id": 31801,
        "title": "100-mankai no Suki o Ageru",
        "main_manga_id": 99001,
    }]) == 1
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_language,enabled_search,enabled_chapters,priority,last_seen,available)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)""",
            ("trusted-en", "Trusted English", "Trusted English", "en", "https://example.invalid", "", 1, 1, 1, 1, 10, "now", 1),
        )
    correct = manga(31802, "+99 Reinforced Wooden Stick")
    s._upsert_match(31801, "trusted-en", correct, 1.0)
    s._choose_metadata(31801)
    assert s.get_entry(31801)["preferred_title"] == "+99 Reinforced Wooden Stick"


def test_placeholder_alias_cannot_create_duplicate_candidate():
    s = AutoSourcarrService()
    source_row("placeholder-a", "Placeholder A", 10)
    source_row("placeholder-b", "Placeholder B", 20)
    left = s._create_entry(manga(120001, "Overlord of Sichuan"), "placeholder-a")
    right = s._create_entry(manga(120002, "I Was Thrown Away, but Is It Really Okay for Me to Be Loved This Much?"), "placeholder-b")
    s.add_title(left, "No alternative titles.", title_type="description-alias")
    s.add_title(right, "No alternative titles.", title_type="description-alias")
    with db.catalog() as c:
        stored = [r["title"] for r in c.execute("SELECT title FROM titles WHERE entry_id IN (?,?)", (left, right))]
    assert "No alternative titles." not in stored
    assert s.duplicate_candidates([left, right]) == {}


def test_ingest_stores_clean_synopsis_not_related_work_or_link_metadata():
    s = AutoSourcarrService()
    source_row("summary-clean", "Summary Clean", 10)
    raw = (
        "A civil engineering student wakes as Lloyd and uses engineering to rescue a debt-ridden noble family. "
        "Chronology of works in this universe: [Other Work](https://example.invalid/other) "
        "Official Translations: [English](https://example.invalid/en)"
    )
    eid, _, _ = s.ingest_manga("summary-clean", manga(120010, "The Greatest Engineer", raw))
    description = s.get_entry(eid)["description"]
    assert "Lloyd" in description
    assert "Chronology of works" not in description
    assert "Official Translations" not in description
    assert "example.invalid" not in description


def test_federated_browse_catalogs_discovered_source_entries(monkeypatch):
    s = AutoSourcarrService()
    source_row("browse-catalog", "Browse Catalog", 10)

    async def fake_fetch_source_manga(source_id, mode, page, query=""):
        return {"mangas": [manga(120020, "Discovered While Browsing")], "hasNextPage": False}

    monkeypatch.setattr(source_host, "fetch_source_manga", fake_fetch_source_manga)
    result = asyncio.run(s.federated("POPULAR", source_ids=["browse-catalog"], force_live=True))
    assert result["cataloged_entries"] == 1
    assert len(result["items"]) == 1
    eid = int(result["items"][0]["id"])
    assert s.get_entry(eid)["matches"][0]["source_manga_id"] == 120020


def test_single_word_alias_does_not_auto_match_unrelated_source_record():
    s = AutoSourcarrService()
    source_row("single-a", "Single A", 10)
    source_row("single-b", "Single B", 20)
    first, _, _ = s.ingest_manga("single-a", manga(120001, "The Tyrant Returned From Another World"))
    s.add_title(first, "King", title_type="manual")
    second, _, _ = s.ingest_manga("single-b", manga(120002, "King"))
    assert second != first


def test_single_word_primary_source_title_can_still_match():
    s = AutoSourcarrService()
    source_row("single-primary-a", "Single Primary A", 10)
    source_row("single-primary-b", "Single Primary B", 20)
    first, _, _ = s.ingest_manga("single-primary-a", manga(120101, "Bleach"))
    second, confidence, _ = s.ingest_manga("single-primary-b", manga(120102, "Bleach"))
    assert second == first
    assert confidence >= 0.94


def test_missing_source_discovery_reuses_manual_search_path(monkeypatch):
    s = AutoSourcarrService()
    source_row("missing-base", "Missing Base", 10)
    source_row("missing-target", "Missing Target", 20)
    title = "Skill Lender's Retrieving Tale ~I Told You It's 10% per 10 Days at First, Didn't I?~"
    eid, _, _ = s.ingest_manga("missing-base", manga(121001, title))
    candidate = manga(121002, title, "A betrayed adventurer reclaims the skills he lent to former companions and starts over with a new party.")

    async def fake_search(source_id, mode, page, query=""):
        assert source_id == "missing-target"
        if "Skill Lender" in query:
            return {"mangas": [candidate], "hasNextPage": False}
        return {"mangas": [], "hasNextPage": False}

    async def fake_details(mid, fetch_details=True, fetch_chapters=True):
        assert int(mid) == 121002
        return {"manga": candidate, "chapters": []}

    monkeypatch.setattr(source_host, "fetch_source_manga", fake_search)
    monkeypatch.setattr(source_host, "fetch_manga_and_chapters", fake_details)
    result = asyncio.run(s.discover_missing_source_matches(eid, "missing-target"))
    assert result["added"] == 1
    entry = s.get_entry(eid)
    assert any(m["source_id"] == "missing-target" and int(m["source_manga_id"]) == 121002 for m in entry["matches"])


def test_expanded_search_prioritizes_preferred_title_simplifications_and_skips_noisy_single_words(monkeypatch):
    from app.config import settings

    s = AutoSourcarrService()
    source_row("priority-search", "Priority Search", 10)
    title = "Skill Lender's Retrieving Tale ~I Told You It's 10% per 10 Days at First, Didn't I?~"
    eid, _, _ = s.ingest_manga("priority-search", manga(121101, title))
    # These used to occupy the small live-query budget before any useful shortened
    # form of the long preferred title was attempted.
    for alias in ("King", "Skill", "Lender", "Completely Different Long Alias"):
        s.add_title(eid, alias, title_type="manual")

    monkeypatch.setattr(settings, "manual_search_alias_limit", 30)
    names, meta = asyncio.run(s._expanded_search_names(eid, "priority-search"))
    live_direct = [m["query"] for m in meta if m.get("kind") not in {"translated-origin", "translated-literal"}]

    assert live_direct[0] == title
    # The useful shortened title must fit inside the normal four-query live budget.
    assert any("Skill Lender's Retrieving Tale" == q for q in live_direct[:4])
    # Generic one-word aliases are never automatic source-search evidence.
    assert "King" not in names
    assert "Skill" not in names
    assert "Lender" not in names


def test_catalog_titles_store_matched_source_titles_as_search_only_aliases():
    s = AutoSourcarrService()
    source_row("title-provenance-219", "Title Provenance", 10)
    m = manga(
        219001,
        "+99 Wooden Stick",
        "A real plot synopsis long enough to remain useful.\n\nAlternative titles: 99 Hardened Wooden Stick, 99 Reinforced Wood Stick",
    )
    eid, _, _ = s.ingest_manga("title-provenance-219", m)
    entry = s.get_entry(eid)
    stored = {(row["title"], row["title_type"]) for row in entry["titles"]}
    # Active source display titles are indexed as matched aliases so Suwayomi's
    # Browse/Migrate exact-title search can always resolve them. They are explicitly
    # search-only and never automatic matching evidence.
    assert any(kind == "matched-alias" for _, kind in stored)
    assert ("99 Hardened Wooden Stick", "description-alias") in stored
    assert ("99 Reinforced Wood Stick", "description-alias") in stored
    from app.matcher import is_match_evidence_title
    assert is_match_evidence_title("+99 Wooden Stick", "matched-alias") is False


def test_replacing_source_record_removes_old_record_aliases_and_thumbnail_data():
    s = AutoSourcarrService()
    source_row("replace-provenance-219", "Replace Provenance", 10)
    old = manga(
        219101,
        "Replacement Work",
        "A stable work summary for replacement testing.\n\nAssociated Names: Old Alias Title, Old Secondary Name",
    )
    old["thumbnailUrl"] = "https://example.invalid/old.jpg"
    eid, _, _ = s.ingest_manga("replace-provenance-219", old)
    s._choose_metadata(eid)
    assert "Old Alias Title" in [x["title"] for x in s.get_entry(eid)["titles"]]

    new = manga(
        219102,
        "Replacement Work",
        "A stable work summary for replacement testing.\n\nAssociated Names: New Alias Title, New Secondary Name",
    )
    new["thumbnailUrl"] = "https://example.invalid/new.jpg"
    s._upsert_match(eid, "replace-provenance-219", new, 1.0, "manual", True)
    s._choose_metadata(eid)
    entry = s.get_entry(eid)
    titles = [x["title"] for x in entry["titles"]]
    assert "Old Alias Title" not in titles
    assert "Old Secondary Name" not in titles
    assert "New Alias Title" in titles
    assert "New Secondary Name" in titles
    assert entry["thumbnail_url"] == "https://example.invalid/new.jpg"

    # A later replacement that has no cover must not retain the old source's image.
    newest = manga(
        219103,
        "Replacement Work",
        "A stable replacement with no cover.\n\nAssociated Names: Final Alias Title",
    )
    newest["thumbnailUrl"] = ""
    s._upsert_match(eid, "replace-provenance-219", newest, 1.0, "manual", True)
    s._choose_metadata(eid)
    entry = s.get_entry(eid)
    assert entry["thumbnail_url"] == ""
    assert "New Alias Title" not in [x["title"] for x in entry["titles"]]
    assert "Final Alias Title" in [x["title"] for x in entry["titles"]]


def test_manual_main_title_override_does_not_create_an_alternate_title():
    s = AutoSourcarrService()
    source_row("main-title-219", "Main Title", 10)
    m = manga(
        219201,
        "Native Display Name",
        "A sufficiently normal synopsis.\n\nAlternative titles: Trusted Alternate Name",
    )
    eid, _, _ = s.ingest_manga("main-title-219", m)
    before = [(x["title"], x["title_type"]) for x in s.get_entry(eid)["titles"]]
    updated = s.set_preferred_title(eid, "My Preferred English Title")
    after = [(x["title"], x["title_type"]) for x in updated["titles"]]
    assert updated["preferred_title"] == "My Preferred English Title"
    assert before == after
    assert ("My Preferred English Title", "manual") not in after


def test_startup_sanitizer_splits_legacy_comma_packed_description_alias_and_drops_generated_titles():
    s = AutoSourcarrService()
    source_row("legacy-title-cleanup-219", "Legacy Title Cleanup", 10)
    eid, _, _ = s.ingest_manga("legacy-title-cleanup-219", manga(219301, "Legacy Cleanup Work"))
    with db.catalog() as c:
        c.execute(
            "INSERT OR IGNORE INTO titles(entry_id,title,normalized,language,title_type,source_id,source_manga_id) VALUES(?,?,?,?,?,?,?)",
            (eid, "Alias Alpha Name, Alias Beta Name, Alias Gamma Name", "alias alpha name alias beta name alias gamma name", "en", "description-alias", "legacy-title-cleanup-219", 219301),
        )
        c.execute(
            "INSERT OR IGNORE INTO titles(entry_id,title,normalized,language,title_type,source_id,source_manga_id) VALUES(?,?,?,?,?,?,?)",
            (eid, "Generated Machine Alias", "generated machine alias", "en", "translated-literal", "translation", 0),
        )
    s.run_startup_catalog_maintenance(force=True)
    with db.catalog() as c:
        rows = db.rows(c.execute("SELECT title,title_type FROM titles WHERE entry_id=? ORDER BY title", (eid,)))
    assert ("Generated Machine Alias", "translated-literal") not in {(r["title"], r["title_type"]) for r in rows}
    # Legacy aliases that cannot be proven from the preserved source description
    # are intentionally discarded rather than kept as orphaned parser output.
    assert [r["title"] for r in rows if r["title_type"] == "description-alias"] == []


def test_startup_sanitizer_rebuilds_real_comma_title_from_raw_source_metadata():
    s = AutoSourcarrService()
    source_row("comma-rebuild-221", "Comma Rebuild", 10)
    full = "Skill Lender's Retrieving (Tale) ～I Told You It's 10% Per 10 Days at First, Didn't I～"
    desc = f"A betrayed lender takes his skills back from his former party.\n\nAlternative titles: {full}\nStatus: Ongoing"
    eid, _, _ = s.ingest_manga("comma-rebuild-221", manga(221301, "Skill Lender's Retrieving Tale", desc))
    # Simulate rows produced by the old over-eager comma splitter while preserving
    # the original source description in metadata_json.
    with db.catalog() as c:
        c.execute("DELETE FROM titles WHERE entry_id=? AND title_type='description-alias'", (eid,))
        for value in (
            "Skill Lender's Retrieving (Tale) ～I Told You It's 10% Per 10 Days at First",
            "Didn't I～",
        ):
            c.execute(
                "INSERT OR IGNORE INTO titles(entry_id,title,normalized,language,title_type,source_id,source_manga_id) VALUES(?,?,?,?,?,?,?)",
                (eid, value, normalize_title(value), "en", "description-alias", "comma-rebuild-221", 221301),
            )
        c.execute(
            "INSERT OR IGNORE INTO titles(entry_id,title,normalized,language,title_type,source_id,source_manga_id) VALUES(?,?,?,?,?,?,?)",
            (eid, "Didn't I?~", normalize_title("Didn't I?~"), "en", "description-alias", "", 0),
        )
    s.run_startup_catalog_maintenance(force=True)
    titles = [x["title"] for x in s.get_entry(eid)["titles"] if x["title_type"] == "description-alias"]
    assert titles == [full]


def test_entry_search_still_finds_canonical_and_active_source_titles_after_alias_cleanup():
    s = AutoSourcarrService()
    source_row("search-provenance-219", "Search Provenance", 10)
    eid, _, _ = s.ingest_manga(
        "search-provenance-219",
        manga(219401, "Canonical Searchable Work", "A normal summary without alternate names."),
    )
    # Source display titles are indexed as provenance-bound matched aliases so
    # exact Browse/Migrate searches can resolve locally.
    with db.catalog() as c:
        rows = db.rows(c.execute("SELECT title,title_type FROM titles WHERE entry_id=?", (eid,)))
    assert any(r["title"] == "Canonical Searchable Work" and r["title_type"] == "matched-alias" for r in rows)
    assert eid in [row["id"] for row in s.entry_list("Canonical Searchable", 20)]
    s.set_preferred_title(eid, "User Chosen Main Name")
    assert eid in [row["id"] for row in s.entry_list("User Chosen", 20)]


def test_missing_source_discovery_never_merges_an_existing_owner(monkeypatch):
    s = AutoSourcarrService()
    source_row("stub-base-220", "Stub Base 220", 10)
    source_row("stub-target-220", "Stub Target 220", 20)
    title = "Skill Lender's Retrieving Tale"
    desc = "A betrayed adventurer reclaims the skills he lent to his former party and grows stronger after taking them back."

    target = s._create_entry(manga(220001, title, desc), "stub-base-220")
    s._upsert_match(target, "stub-base-220", manga(220001, title, desc), 1.0, "auto", False)
    candidate = manga(220002, title, desc)
    owner = s._create_entry(candidate, "stub-target-220")
    s._upsert_match(owner, "stub-target-220", candidate, 1.0, "auto", False)
    assert owner != target

    async def fake_search(entry_id, source_id, query="", page=1, all_names=True, **kwargs):
        assert kwargs.get("background") is True
        return {"mangas": [{**candidate, "auto_match_eligible": True}], "warnings": []}

    async def fake_details(mid, fetch_details=True, fetch_chapters=True):
        assert int(mid) == 220002
        return {"manga": candidate, "chapters": []}

    monkeypatch.setattr(s, "search_source_for_manual", fake_search)
    monkeypatch.setattr(source_host, "fetch_manga_and_chapters", fake_details)
    result = asyncio.run(s.discover_missing_source_matches(target, "stub-target-220"))
    assert result["added"] == 0
    assert result["blocked_candidates"] >= 1
    assert s.canonical_entry_id(target) == target
    assert s.canonical_entry_id(owner) == owner
    assert all(m["source_id"] != "stub-target-220" for m in s.get_entry(target)["matches"])

def test_missing_source_discovery_tries_later_candidate_when_first_owner_is_protected(monkeypatch):
    s = AutoSourcarrService()
    source_row("retry-base-220", "Retry Base 220", 10)
    source_row("retry-target-220", "Retry Target 220", 20)
    title = "Skill Lender's Retrieving Tale"
    desc = "A betrayed adventurer takes back the powers he lent to companions and begins his revenge."

    target = s._create_entry(manga(220101, title, desc), "retry-base-220")
    s._upsert_match(target, "retry-base-220", manga(220101, title, desc), 1.0, "auto", False)

    blocked = manga(220102, title, "A different synopsis that should not independently merge these catalog entries.")
    blocked_owner = s._create_entry(blocked, "retry-target-220")
    s._upsert_match(blocked_owner, "retry-target-220", blocked, 1.0, "manual", True)
    with db.catalog() as c:
        c.execute("UPDATE entries SET preferred_title='Protected Different Work',manual_preferred_title=1 WHERE id=?", (blocked_owner,))

    good = manga(220103, title, desc)

    async def fake_search(entry_id, source_id, query="", page=1, all_names=True, **kwargs):
        return {
            "mangas": [
                {**blocked, "auto_match_eligible": True},
                {**good, "auto_match_eligible": True},
            ],
            "warnings": [],
        }

    async def fake_details(mid, fetch_details=True, fetch_chapters=True):
        payload = blocked if int(mid) == 220102 else good
        return {"manga": payload, "chapters": []}

    monkeypatch.setattr(s, "search_source_for_manual", fake_search)
    monkeypatch.setattr(source_host, "fetch_manga_and_chapters", fake_details)
    result = asyncio.run(s.discover_missing_source_matches(target, "retry-target-220"))
    entry = s.get_entry(target)
    assert result["added"] == 1
    assert result["blocked_candidates"] >= 1
    assert any(m["source_id"] == "retry-target-220" and int(m["source_manga_id"]) == 220103 for m in entry["matches"])
    assert s.canonical_entry_id(blocked_owner) == blocked_owner



def test_manual_main_title_does_not_inherit_to_new_entry_via_source_identity():
    s = AutoSourcarrService()
    s.reset_database()
    source_row("persistent-title-source", "Persistent title source", 10)
    first, _, _ = s.ingest_manga("persistent-title-source", manga(141001, "Source Generated Title"))
    s.set_preferred_title(first, "My Entry Local Main Title")
    assert s.get_entry(first)["preferred_title"] == "My Entry Local Main Title"

    # A non-subscribed work rebuilt under a new entry id is a different entry for
    # manual-title purposes. The source record must not carry the old override.
    s.reset_database()
    source_row("persistent-title-source", "Persistent title source", 10)
    second, _, _ = s.ingest_manga("persistent-title-source", manga(141001, "Source Generated Title"))
    assert second != first
    entry = s.get_entry(second)
    assert entry["preferred_title"] == "Source Generated Title"
    assert int(entry["manual_preferred_title"]) == 0

def test_cached_only_manual_candidate_cannot_authorize_automatic_source_match(monkeypatch):
    import app.service as service_module

    s = AutoSourcarrService()
    source_row("cache-gate-base", "Cache Gate Base", 10)
    source_row("cache-gate-target", "Cache Gate Target", 20)
    eid, _, _ = s.ingest_manga("cache-gate-base", manga(142001, "Cache Gate Work"))

    async def fake_manual(*args, **kwargs):
        candidate = manga(142002, "Cache Gate Work")
        candidate.update({
            "auto_match_eligible": True,
            "live_result": False,
            "cached_result": True,
            "_live_evidence_manga": {},
        })
        return {"mangas": [candidate], "warnings": []}

    async def failed_details(*args, **kwargs):
        raise TimeoutError("source detail unavailable")

    monkeypatch.setattr(s, "search_source_for_manual", fake_manual)
    monkeypatch.setattr(service_module.source_host, "fetch_manga_and_chapters", failed_details)
    result = asyncio.run(s.discover_missing_source_matches(eid, "cache-gate-target"))
    assert result["added"] == 0
    entry = s.get_entry(eid)
    assert all(str(row.get("source_id")) != "cache-gate-target" for row in entry["matches"])


def test_live_manual_result_replaces_stale_cached_title(monkeypatch):
    import app.service as service_module

    s = AutoSourcarrService()
    source_row("cache-live-base", "Cache Live Base", 10)
    source_row("cache-live-target", "Cache Live Target", 20)
    target, _, _ = s.ingest_manga("cache-live-base", manga(143001, "Fresh Correct Title"))

    stale = manga(143002, "Completely Stale Cached Title", "Old cached summary that must not become current identity.")
    stale.update({"cached_result": True, "matched_query": "Fresh Correct Title", "autosourcarr_match_score": 0.6})
    monkeypatch.setattr(s, "_cached_manual_source_candidates", lambda *a, **k: {143002: dict(stale)})

    async def expanded(*args, **kwargs):
        return ["Fresh Correct Title"], [{"query": "Fresh Correct Title", "kind": "real", "language": "en"}]

    async def live_search(source_id, mode, page, query_text):
        return {"hasNextPage": False, "mangas": [manga(143002, "Fresh Correct Title", "")]}

    monkeypatch.setattr(s, "_expanded_search_names", expanded)
    monkeypatch.setattr(service_module.source_host, "fetch_source_manga", live_search)
    result = asyncio.run(s.search_source_for_manual(target, "cache-live-target", "", 1, True))
    row = next(x for x in result["mangas"] if int(x["id"]) == 143002)
    assert row["title"] == "Fresh Correct Title"
    assert row["live_result"] is True
    assert row["_live_evidence_manga"]["title"] == "Fresh Correct Title"


def test_auto_source_title_cannot_cascade_into_another_automatic_match():
    s = AutoSourcarrService()
    source_row("cascade-base-224", "Cascade Base", 10)
    source_row("cascade-wrong-224", "Cascade Wrong", 20)
    source_row("cascade-next-224", "Cascade Next", 30)
    eid = s._create_entry(manga(224001, "Beast Evolution"), "cascade-base-224")
    s._upsert_match(eid, "cascade-base-224", manga(224001, "Beast Evolution"), 1.0, "auto", False)
    # Simulate an old bad automatic match already present in the database.
    wrong = manga(224002, "Global Beast Tamer: I Can See the Path of Evolution")
    s._upsert_match(eid, "cascade-wrong-224", wrong, 1.0, "auto", False)

    # A later source with the same wrong title must not use that prior automatic
    # source title as trusted evidence and cascade into the canonical entry.
    new_eid, _, _ = s.ingest_manga(
        "cascade-next-224",
        manga(224003, "Global Beast Tamer: I Can See the Path of Evolution"),
    )
    assert new_eid != eid


def test_source_record_chapter_stats_are_bounded_by_each_records_own_latest():
    s = AutoSourcarrService()
    source_row("coverage-record-224", "Coverage Record", 10)
    eid, _, _ = s.ingest_manga("coverage-record-224", manga(224101, "Coverage Specific Work"))
    s._upsert_source_variant(eid, "coverage-record-224", manga(224102, "Coverage Specific Work"), 1.0, "auto-additional", False)
    match_id = s._match_id(eid, "coverage-record-224")

    primary_chapters = [chapter(224200 + i, float(i), 10) for i in range(1, 156)]
    missing = {10, 20, 30, 40, 50}
    variant_chapters = [chapter(225000 + i, float(i), 10) for i in range(1, 81) if i not in missing]
    s._ingest_chapters(
        eid,
        {"source_id": "coverage-record-224", "source_name": "Coverage Record", "source_manga_id": 224101},
        match_id,
        primary_chapters,
    )
    s._ingest_chapters(
        eid,
        {"source_id": "coverage-record-224", "source_name": "Coverage Record", "source_manga_id": 224102},
        match_id,
        variant_chapters,
    )
    stats = s.source_record_chapter_stats([eid])
    primary = stats[(eid, "coverage-record-224", 224101)]
    variant = stats[(eid, "coverage-record-224", 224102)]
    assert primary["latest_chapter"] == 155.0
    assert primary["missing_chapter_count"] == 0
    assert variant["latest_chapter"] == 80.0
    assert variant["missing_chapter_count"] == 5


def test_promote_variant_to_primary_keeps_old_primary_as_additional():
    s = AutoSourcarrService()
    source_row("promote-224", "Promote", 10)
    eid, _, _ = s.ingest_manga("promote-224", manga(224301, "Promote Work"))
    s._upsert_source_variant(eid, "promote-224", manga(224302, "Promote Work Alternate"), 0.99, "auto-additional", False)
    updated = s.promote_variant_to_primary(eid, "promote-224", 224302)
    assert int(updated["matches"][0]["source_manga_id"]) == 224302
    assert int(updated["matches"][0]["locked"]) == 1
    assert str(updated["matches"][0]["match_type"]) == "manual"
    assert {int(v["source_manga_id"]) for v in updated["source_variants"]} == {224301}


def test_zero_chapter_scan_is_recorded_as_checked_not_uncached():
    s = AutoSourcarrService()
    source_row("zero-chapters-225", "Zero Chapters", 10)
    eid, _, _ = s.ingest_manga("zero-chapters-225", manga(225101, "Zero Chapter Work"))
    match_id = s._match_id(eid, "zero-chapters-225")
    s._ingest_chapters(
        eid,
        {"source_id": "zero-chapters-225", "source_name": "Zero Chapters", "source_manga_id": 225101},
        match_id,
        [],
    )
    stat = s.source_record_chapter_stats([eid])[(eid, "zero-chapters-225", 225101)]
    assert stat["chapters_checked"] is True
    assert stat["chapter_count"] == 0
    assert stat["latest_chapter"] is None
    assert stat["missing_chapter_count"] == 0


def test_single_source_record_chapter_refresh_updates_only_requested_record(monkeypatch):
    s = AutoSourcarrService()
    source_row("check-one-225", "Check One", 10)
    eid, _, _ = s.ingest_manga("check-one-225", manga(225201, "Check Chapters Work"))
    s._upsert_source_variant(eid, "check-one-225", manga(225202, "Check Chapters Work Extra"), 1.0, "auto-additional", False)

    async def fake_fetch(mid, fetch_details, fetch_chapters):
        assert int(mid) == 225202
        assert fetch_details is False
        assert fetch_chapters is True
        return {"manga": {}, "chapters": [chapter(225301, 1.0, 12), chapter(225302, 2.0, 12)]}

    monkeypatch.setattr(source_host, "fetch_manga_and_chapters", fake_fetch)
    result = asyncio.run(s.refresh_source_record_chapters(eid, "check-one-225", 225202))
    assert result["stats"]["chapters_checked"] is True
    assert result["stats"]["chapter_count"] == 2
    stats = s.source_record_chapter_stats([eid])
    assert stats[(eid, "check-one-225", 225202)]["latest_chapter"] == 2.0
    # The untouched primary still has no source-record scan row.
    assert (eid, "check-one-225", 225201) not in stats


def test_matched_alias_is_removed_when_source_record_is_detached():
    s = AutoSourcarrService()
    source_row("matched-alias-remove-226", "Matched Alias Remove", 10)
    source_row("matched-alias-anchor-226", "Matched Alias Anchor", 20)
    eid, _, _ = s.ingest_manga("matched-alias-anchor-226", manga(226001, "Matched Alias Anchor Work"))
    variant = manga(226002, "Exact Matched Source Alias 226")
    s._upsert_source_variant(eid, "matched-alias-remove-226", variant, 1.0, "manual", True)
    before = s.get_entry(eid)
    assert any(row["title"] == "Exact Matched Source Alias 226" and row["title_type"] == "matched-alias" for row in before["titles"])
    s.clear_source_variant(eid, "matched-alias-remove-226", 226002)
    after = s.get_entry(eid)
    assert not any(row["title"] == "Exact Matched Source Alias 226" for row in after["titles"])


def test_exact_matched_alias_search_returns_local_entry_without_live_source(monkeypatch):
    s = AutoSourcarrService()
    source_row("exact-alias-226", "Exact Alias", 10)
    eid, _, _ = s.ingest_manga("exact-alias-226", manga(226101, "Unique Exact Matched Alias 226"))

    async def should_not_fetch(*args, **kwargs):
        raise AssertionError("exact known alias search must be database-first")

    monkeypatch.setattr(source_host, "fetch_source_manga", should_not_fetch)
    result = asyncio.run(s.federated("SEARCH", query="Unique Exact Matched Alias 226", min_matches=0))
    assert result.get("exact_local_match") is True
    assert any(int(item["canonical_id"]) == int(eid) for item in result["items"])


def test_entry_chapter_stats_use_unified_latest_across_sources():
    s = AutoSourcarrService()
    source_row("entry-stats-a-226", "Entry Stats A", 10)
    source_row("entry-stats-b-226", "Entry Stats B", 20)
    eid, _, _ = s.ingest_manga("entry-stats-a-226", manga(226201, "Unified Latest Work 226"))
    s._upsert_match(eid, "entry-stats-b-226", manga(226202, "Unified Latest Work 226"), 1.0, "manual", True)
    s._ingest_chapters(
        eid,
        {"source_id": "entry-stats-a-226", "source_name": "Entry Stats A", "source_manga_id": 226201},
        s._match_id(eid, "entry-stats-a-226"),
        [chapter(226210, 1, 20), chapter(226211, 80, 20)],
    )
    s._ingest_chapters(
        eid,
        {"source_id": "entry-stats-b-226", "source_name": "Entry Stats B", "source_manga_id": 226202},
        s._match_id(eid, "entry-stats-b-226"),
        [chapter(226220, 1, 20), chapter(226221, 155, 20)],
    )
    stats = s.entry_chapter_stats([eid])[eid]
    assert stats["served_latest_chapter"] == 155.0
    # The unified feed is a union of virtual chapters, not the raw chapter count
    # from main Suwayomi or from either individual source record.
    assert stats["served_chapter_count"] >= 3


def test_auto_merge_preview_lists_only_eligible_pairs():
    s = AutoSourcarrService()
    source_row("preview-a", "Preview A", 10); source_row("preview-b", "Preview B", 20)
    a = manga(42001, "Shared Preview Title", "")
    b = manga(42002, "Shared Preview Title", "")
    e1 = s._create_entry(a, "preview-a"); s._upsert_match(e1, "preview-a", a, 1.0)
    e2 = s._create_entry(b, "preview-b"); s._upsert_match(e2, "preview-b", b, 1.0)
    s.add_title(e1, "Second Preview Alias", title_type="manual")
    s.add_title(e2, "Second Preview Alias", title_type="manual")
    preview = s.auto_merge_preview()
    assert preview["pair_count"] >= 1
    pairs = [p for g in preview["groups"] for p in g["pairs"]]
    assert any({int(p["left_id"]), int(p["right_id"])} == {e1, e2} for p in pairs)
    assert all(p["auto_merge_eligible"] for p in pairs)


def test_auto_variant_title_matching_penalizes_extra_words(monkeypatch):
    from app.config import settings
    s = AutoSourcarrService()
    monkeypatch.setattr(settings, "auto_match_threshold", 0.94)
    score, reason = s._variant_candidate_score(
        {"title": "Bride of the War God", "description": "", "author": ""},
        entry_titles=["God of War"], entry_descriptions=[], entry_author="",
        real_normalized_titles={"god of war"},
    )
    assert score < 0.94
    assert "insufficient" in reason
    exact_score, _ = s._variant_candidate_score(
        {"title": "God of War", "description": "", "author": ""},
        entry_titles=["God of War"], entry_descriptions=[], entry_author="",
        real_normalized_titles={"god of war"},
    )
    assert exact_score == 1.0


def test_download_size_status_scans_configured_readonly_tree(tmp_path, monkeypatch):
    from app.config import settings
    root = tmp_path / "downloads" / "mangas" / "AutoSourcarr"
    (root / "Work" / "Chapter 1").mkdir(parents=True)
    (root / "Work" / "Chapter 1" / "001.jpg").write_bytes(b"x" * 123)
    (root / "Work" / "Chapter 1" / "002.jpg").write_bytes(b"y" * 77)
    monkeypatch.setattr(settings, "main_suwayomi_downloads_path", str(tmp_path / "downloads" / "mangas"))
    s = AutoSourcarrService()
    out = s.main_download_size_status(force=True)
    assert out["available"] is True
    assert out["bytes"] == 200
    assert out["files"] == 2
    assert out["path"].endswith("AutoSourcarr")



def test_chapter_resolver_prefers_season_one_over_reset_season_two_candidate():
    s = AutoSourcarrService()
    # Give the bad season-2 source a much better normal priority and page count;
    # the season guard must still prevent it from serving chapter 1.
    source_row("season2-bad-229", "Season Two Reset", 1)
    source_row("season1-good-229", "Season One", 90)
    eid, _, _ = s.ingest_manga("season2-bad-229", manga(229001, "+99 Wooden Stick"))
    s._upsert_match(eid, "season1-good-229", manga(229002, "+99 Wooden Stick"), 1.0, "manual", True)
    s._ingest_chapters(
        eid,
        {"source_id": "season2-bad-229", "source_name": "Season Two Reset", "source_manga_id": 229001},
        s._match_id(eid, "season2-bad-229"),
        [chapter(229011, 1.0, 80, "(S2) Ep. 1 - Log In (1) (Season 2 Premiere)")],
    )
    s._ingest_chapters(
        eid,
        {"source_id": "season1-good-229", "source_name": "Season One", "source_manga_id": 229002},
        s._match_id(eid, "season1-good-229"),
        [chapter(229012, 1.0, 20, "Ep. 1 - Wooden Beginning")],
    )
    listed = s.list_chapters(eid)
    assert len(listed) == 1
    assert listed[0]["serving_source_id"] == "season1-good-229"
    assert listed[0]["display_name"] == "Chapter 1: Wooden Beginning"
    detail = s.chapter_details(listed[0]["id"])
    assert detail["candidates"][0]["source_id"] == "season1-good-229"


def test_public_chapter_name_is_chapter_number_plus_source_subtitle():
    s = AutoSourcarrService()
    source_row("chapter-name-229", "Chapter Name", 10)
    eid, _, _ = s.ingest_manga("chapter-name-229", manga(229101, "Chapter Name Work"))
    s._ingest_chapters(
        eid,
        {"source_id": "chapter-name-229", "source_name": "Chapter Name", "source_manga_id": 229101},
        s._match_id(eid, "chapter-name-229"),
        [chapter(229111, 12.0, 10, "Chapter 12 - A Better Future")],
    )
    listed = s.list_chapters(eid)
    assert listed[0]["display_name"] == "Chapter 12: A Better Future"


def test_explicit_colored_edition_stays_separate_without_polluting_display_title():
    from app.color_profile import save_source_color_profile

    s = AutoSourcarrService()
    source_row("color-mono", "Mono", 10)
    source_row("color-full", "Color", 20)
    e1, _, _ = s.ingest_manga("color-mono", manga(99101, "Edition Test"))
    e2, _, _ = s.ingest_manga("color-full", manga(99201, "Edition Test (Colored)"))
    assert e1 != e2

    save_source_color_profile("color-mono", 99101, "monochrome", 0.99, 4, "test")
    save_source_color_profile("color-full", 99201, "colored", 0.99, 4, "test")
    s._recompute_entry_color_profile(e1)
    s._recompute_entry_color_profile(e2)
    s._sync_related_colored_labels(e1)
    s._sync_related_colored_labels(e2)
    assert s.entry_preferred_title(e1) == "Edition Test"
    assert s.entry_preferred_title(e2) == "Edition Test"
    assert s._auto_merge_evidence(e1, e2)["eligible"] is False


def test_unlabeled_colored_and_monochrome_profiles_are_a_merge_conflict():
    from app.color_profile import save_source_color_profile

    s = AutoSourcarrService()
    source_row("profile-a", "Profile A", 10)
    source_row("profile-b", "Profile B", 20)
    e1, _, _ = s.ingest_manga("profile-a", manga(99301, "Same Edition Name"))
    e2 = s._create_entry(manga(99401, "Same Edition Name"), "profile-b")
    s._upsert_match(e2, "profile-b", manga(99401, "Same Edition Name"), 1.0)
    save_source_color_profile("profile-a", 99301, "monochrome", 0.99, 4, "test")
    save_source_color_profile("profile-b", 99401, "colored", 0.99, 4, "test")
    s._recompute_entry_color_profile(e1)
    s._recompute_entry_color_profile(e2)
    assert s._entries_have_color_conflict(e1, e2) is True
    assert s._entry_merge_score(e1, e2)[0] == 0.0


def test_mixed_automatic_color_records_are_split_into_separate_entries():
    from app.color_profile import save_source_color_profile

    s = AutoSourcarrService()
    source_row("split-mono", "Split Mono", 10)
    source_row("split-color", "Split Color", 20)
    eid, _, _ = s.ingest_manga("split-mono", manga(99501, "Color Split Test"))
    s._upsert_match(eid, "split-color", manga(99601, "Color Split Test"), 1.0, "auto", False)
    save_source_color_profile("split-mono", 99501, "monochrome", 0.99, 4, "test")
    save_source_color_profile("split-color", 99601, "colored", 0.99, 4, "test")
    assert s._recompute_entry_color_profile(eid)["color_mode"] == "mixed"
    result = s._auto_split_mixed_color_entry(eid)
    assert result is not None
    new_id = result["new_entry_id"]
    assert s._entry_color_profile(eid)["color_mode"] == "monochrome"
    assert s._entry_color_profile(new_id)["color_mode"] == "colored"
    s._sync_related_colored_labels(eid)
    s._sync_related_colored_labels(new_id)
    assert s.entry_preferred_title(new_id) == "Color Split Test"


def test_lone_colored_edition_does_not_get_colored_suffix_until_counterpart_exists():
    from app.color_profile import save_source_color_profile

    s = AutoSourcarrService()
    source_row("lone-color", "Lone Color", 10)
    eid, _, _ = s.ingest_manga("lone-color", manga(99701, "Lone Edition (Colored)"))
    save_source_color_profile("lone-color", 99701, "colored", 0.99, 4, "test")
    s._recompute_entry_color_profile(eid)
    s._sync_related_colored_labels(eid)
    assert s.entry_preferred_title(eid) == "Lone Edition"


def test_unlabeled_exact_title_does_not_auto_merge_into_known_colored_entry():
    s = AutoSourcarrService()
    source_row("known-color", "Known Color", 10)
    source_row("unknown-edition", "Unknown Edition", 20)
    colored_id, _, _ = s.ingest_manga("known-color", manga(99801, "Edition Guard (Colored)"))
    assert s._entry_color_profile(colored_id)["color_mode"] == "colored"
    other_id, _, _ = s.ingest_manga("unknown-edition", manga(99802, "Edition Guard"))
    assert other_id != colored_id


def test_manga_and_webtoon_versions_stay_separate_and_label_only_when_both_exist():
    s = AutoSourcarrService()
    source_row("media-manga", "Manga Source", 10)
    source_row("media-webtoon", "Webtoon Source", 20)
    e1, _, _ = s.ingest_manga("media-manga", manga(100101, "Adaptation Pair (Manga)"))
    assert s.entry_preferred_title(e1) == "Adaptation Pair"
    e2, _, _ = s.ingest_manga("media-webtoon", manga(100102, "Adaptation Pair (Webtoon)"))
    assert e1 != e2
    s._recompute_entry_media_mode(e1); s._recompute_entry_media_mode(e2)
    s._sync_related_media_labels(e1); s._sync_related_media_labels(e2)
    assert s.entry_preferred_title(e1) == "Adaptation Pair (manga)"
    assert s.entry_preferred_title(e2) == "Adaptation Pair (webtoon)"
    assert s._entries_have_media_conflict(e1, e2) is True
    assert s._auto_merge_evidence(e1, e2)["eligible"] is False


def test_lone_webtoon_version_does_not_get_suffix_until_manga_counterpart_exists():
    s = AutoSourcarrService()
    source_row("lone-webtoon", "Lone Webtoon", 10)
    eid, _, _ = s.ingest_manga("lone-webtoon", manga(100201, "Lone Adaptation (Webtoon)"))
    s._recompute_entry_media_mode(eid)
    s._sync_related_media_labels(eid)
    assert s.entry_preferred_title(eid) == "Lone Adaptation"


def test_service_construction_does_not_run_heavy_catalog_migrations(monkeypatch):
    monkeypatch.setattr(AutoSourcarrService, "_sanitize_catalog_text", lambda self: (_ for _ in ()).throw(AssertionError("sanitize ran during construction")))
    monkeypatch.setattr(AutoSourcarrService, "_refresh_media_variants", lambda self: (_ for _ in ()).throw(AssertionError("media migration ran during construction")))
    AutoSourcarrService()


def test_alias_repair_recovers_interrupted_merge_chain():
    s = AutoSourcarrService()
    source_row("alias-repair", "Alias Repair Source", 70)
    survivor, _, _ = s.ingest_manga("alias-repair", manga(991001, "Alias Repair Survivor"))
    stale_old = 991101
    stale_terminal = 991102
    with db.catalog() as c:
        c.execute(
            "INSERT OR REPLACE INTO entry_aliases(old_entry_id,canonical_entry_id,reason,merged_at) VALUES(?,?,?,?)",
            (stale_old, stale_terminal, "simulated-interrupted-merge", now_iso()),
        )
        c.execute(
            """INSERT INTO match_history(entry_id,source_id,old_source_manga_id,new_source_manga_id,change_type,changed_at)
               VALUES(?,?,?,?,?,?)""",
            (survivor, "*", stale_terminal, survivor, "merge:simulated-interrupted-merge", now_iso()),
        )
    s._load_alias_cache()
    assert s.canonical_entry_id(stale_old) == stale_terminal
    result = s._repair_entry_alias_integrity()
    assert result["repaired"] >= 1
    assert s.canonical_entry_id(stale_old) == survivor
    assert s.canonical_entry_id(stale_terminal) == survivor


def test_duplicate_candidates_skip_a_stale_pair_instead_of_crashing(monkeypatch):
    s = AutoSourcarrService()
    monkeypatch.setattr(
        s,
        "_duplicate_shortlist",
        lambda entry_ids=None: ({(992001, 992002)}, {992001: ["Stale Work"], 992002: ["Stale Work"]}, {(992001, 992002): {"exact-title"}}),
    )
    monkeypatch.setattr(s, "_entry_merge_score", lambda left, right: (_ for _ in ()).throw(KeyError(right)))
    assert s.duplicate_candidates([992001]) == {}


def test_status_stats_never_rebuilds_duplicate_graph(monkeypatch):
    s = AutoSourcarrService()

    def explode():
        raise AssertionError("Status must not call duplicate_groups()")

    monkeypatch.setattr(s, "duplicate_groups", explode)
    stats = s.status_stats()
    assert "duplicate_snapshot_ready" in stats["catalog"]


def test_duplicate_matrix_badges_use_completed_snapshot_only():
    import time

    s = AutoSourcarrService()
    source_row("snapshot-src", "Snapshot Source", 1)
    e1 = s._create_entry(manga(19101, "Snapshot Duplicate A"), "snapshot-src")
    e2 = s._create_entry(manga(19102, "Snapshot Duplicate B"), "snapshot-src")
    s._duplicate_snapshot = (
        time.monotonic(),
        [{
            "group_id": f"{e1}-{e2}",
            "entry_ids": [e1, e2],
            "entries": [],
            "best_score": 0.98,
            "pairs": [{
                "left_id": e1,
                "right_id": e2,
                "title_score": 0.98,
                "merge_score": 0.97,
                "matched_title": "Snapshot Duplicate A",
                "other_title": "Snapshot Duplicate B",
                "reason": "test",
            }],
        }],
    )
    result = s.duplicate_candidates_cached([e1, e2])
    assert result is not None
    assert result[e1]["entry_ids"] == [e2]
    assert result[e2]["entry_ids"] == [e1]


def test_cached_library_status_never_requires_live_main_suwayomi():
    import time

    s = AutoSourcarrService()
    s._subbed_cache = (
        time.monotonic(),
        {
            "ok": True,
            "error": "",
            "entry_ids": [10, 20],
            "stats_by_entry": {
                "10": {"downloaded_chapter_count": 3},
                "20": {"downloaded_chapter_count": 4},
            },
        },
    )
    s._download_size_cache = (time.monotonic(), {"configured": True, "available": True, "bytes": 1234, "files": 9, "path": "/tmp", "error": ""})
    status = s.cached_library_status()
    assert status["ok"] is True
    assert status["subbed_entries"] == 2
    assert status["downloaded_chapters"] == 7
    assert status["download_size"]["bytes"] == 1234


def test_description_alt_title_does_not_classify_current_record_as_colored():
    s = AutoSourcarrService()
    payload = manga(1019901, "Ordinary Work", "Alternative titles: Ordinary Work (Colored)")
    assert s._candidate_title_color_mode(payload) == "unknown"


def test_semantic_colored_word_is_preserved_and_not_matched_to_stripped_title():
    s = AutoSourcarrService()
    source_row("semantic-color-a-236", "Semantic Color A", 10)
    source_row("semantic-color-b-236", "Semantic Color B", 20)
    first, _, _ = s.ingest_manga("semantic-color-a-236", manga(1019911, "Pencil"))
    second, _, _ = s.ingest_manga("semantic-color-b-236", manga(1019912, "Colored Pencil"))
    assert second != first
    assert s.entry_preferred_title(second) == "Colored Pencil"
    assert s._entry_color_profile(second)["color_mode"] == "unknown"


def test_legacy_false_colored_attachment_is_split_conservatively():
    from app.color_profile import save_source_color_profile

    s = AutoSourcarrService()
    source_row("legacy-color-base-236", "Legacy Base", 10)
    source_row("legacy-color-word-236", "Legacy Word", 20)
    entry_id, _, _ = s.ingest_manga("legacy-color-base-236", manga(1019921, "Pencil"))
    # Simulate the persisted bad state made possible by the broad v0.2.30-v0.2.35
    # title classifier: a different work whose actual title contains "Colored" was
    # attached to the stripped "Pencil" entry and marked as a colored edition.
    s._upsert_match(entry_id, "legacy-color-word-236", manga(1019922, "Colored Pencil"), 0.99, "auto", False)
    save_source_color_profile("legacy-color-word-236", 1019922, "colored", 0.995, 0, "explicit-title-marker")

    repair = s._repair_legacy_color_marker_profiles()
    assert repair["invalidated_profiles"] >= 1
    split = s._repair_legacy_color_false_attachments({entry_id})
    assert split["split_entries"] == 1
    assert split["split_records"] == 1

    original = s.get_entry(entry_id)
    assert any(m["source_manga_id"] == 1019921 for m in original["matches"])
    assert all(m["source_manga_id"] != 1019922 for m in original["matches"])
    with db.catalog() as c:
        moved = c.execute(
            "SELECT entry_id FROM source_matches WHERE source_id=? AND source_manga_id=? AND active=1",
            ("legacy-color-word-236", 1019922),
        ).fetchone()
    assert moved is not None
    new_id = int(moved["entry_id"])
    assert new_id != entry_id
    assert s.entry_preferred_title(new_id) == "Colored Pencil"


def test_manual_match_can_commit_from_search_card_without_waiting_for_source_host(monkeypatch):
    s = AutoSourcarrService()
    source_row("fast-manual", "Fast Manual", 10)
    eid, _, _ = s.ingest_manga("fast-manual", manga(45001, "Old Fast Match"))
    called = {"fetch": 0, "hydrate": 0}

    async def forbidden_fetch(*args, **kwargs):
        called["fetch"] += 1
        raise AssertionError("fast manual commit should not fetch the source synchronously")

    def fake_schedule(*args, **kwargs):
        called["hydrate"] += 1
        return True

    monkeypatch.setattr(source_host, "fetch_manga_and_chapters", forbidden_fetch)
    monkeypatch.setattr(s, "schedule_manual_match_hydration", fake_schedule)
    candidate = manga(45002, "New Fast Match", "Search-card metadata is already enough for an immediate local commit.")
    result = asyncio.run(
        s.manual_match(
            eid,
            "fast-manual",
            45002,
            "replace",
            candidate=candidate,
            defer_hydration=True,
        )
    )
    assert called["fetch"] == 0
    assert called["hydrate"] == 1
    assert result["background_hydration"] is True
    assert result["matches"][0]["source_manga_id"] == 45002
    assert result["matches"][0]["locked"] == 1


def test_background_entry_refresh_is_acknowledged_before_network_work_finishes(monkeypatch):
    s = AutoSourcarrService()
    source_row("queued-refresh", "Queued Refresh", 10)
    eid, _, _ = s.ingest_manga("queued-refresh", manga(45101, "Queued Refresh Work"))

    async def slow_refresh(*args, **kwargs):
        await asyncio.sleep(30)
        return s.get_entry(eid)

    monkeypatch.setattr(s, "refresh_entry", slow_refresh)

    async def scenario():
        result = s.schedule_entry_refresh_job(eid)
        assert result["queued"] is True
        assert result["task_id"]
        # Let the background task start, then cancel it so the test doesn't wait.
        await asyncio.sleep(0)
        task_registry.request_cancel(result["task_id"])
        return result

    from app.task_registry import task_registry
    result = asyncio.run(scenario())
    assert result["entry_id"] == eid


def test_ingest_strips_owning_source_name_from_source_and_preferred_titles():
    s = AutoSourcarrService()
    source_row("source-title-clean-243", "ManhuaUS", 10)
    s._reload_source_cache()
    eid, _, _ = s.ingest_manga(
        "source-title-clean-243",
        manga(243001, "Son-in-Law Does Cheap Cultivation – ManhuaUS"),
    )
    entry = s.get_entry(eid)
    assert entry["preferred_title"] == "Son-in-Law Does Cheap Cultivation"
    assert entry["matches"][0]["source_title"] == "Son-in-Law Does Cheap Cultivation"
    assert all("ManhuaUS" not in str(t.get("title") or "") for t in entry["titles"])
