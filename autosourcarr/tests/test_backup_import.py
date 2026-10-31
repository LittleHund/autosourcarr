from __future__ import annotations

import asyncio
import gzip
import json
import os
import tempfile
from pathlib import Path

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ.setdefault("DATA_DIR", _TEST_DATA.name)
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")

from app.backup_import import BackupDecodeError, decode_backup_bytes, decode_backup_file
from app.db import db
from app.service import AutoSourcarrService
import app.service as service_module


def _varint(value: int) -> bytes:
    if value < 0:
        value += 1 << 64
    out = bytearray()
    while True:
        b = value & 0x7F
        value >>= 7
        if value:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def _len_field(number: int, payload: bytes) -> bytes:
    return _varint((number << 3) | 2) + _varint(len(payload)) + payload


def _str_field(number: int, value: str) -> bytes:
    return _len_field(number, value.encode("utf-8"))


def _int_field(number: int, value: int) -> bytes:
    return _varint(number << 3) + _varint(value)


def _backup_source(*, source: int, name: str) -> bytes:
    payload = b"".join([_str_field(1, name), _int_field(2, source)])
    return _len_field(101, payload)


def _backup_manga(*, source: int, url: str, title: str, favorite: bool = True) -> bytes:
    payload = b"".join(
        [
            _int_field(1, source),
            _str_field(2, url),
            _str_field(3, title),
            _str_field(5, "Backup Author"),
            _str_field(6, "A real backup description."),
            _str_field(7, "Action"),
            _int_field(8, 1),
            _str_field(9, "https://example.invalid/cover.jpg"),
            _int_field(100, 1 if favorite else 0),
        ]
    )
    return _len_field(1, payload)


def _source_row(source_id: str, name: str = "Backup Source") -> None:
    with db.catalog() as c:
        c.execute(
            """INSERT OR REPLACE INTO source_config
            (source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_search,enabled_chapters,priority,last_seen)
            VALUES(?,?,?,?,?,?,?,?,?,?,?)""",
            (source_id, name, name, "en", "https://example.invalid", "", 1, 1, 1, 10, "now"),
        )


def _manga(mid: int, title: str, url: str) -> dict:
    return {
        "id": mid,
        "title": title,
        "url": url,
        "thumbnailUrl": "",
        "description": "A real backup description.",
        "author": "Backup Author",
        "artist": "Backup Author",
        "genre": ["Action"],
        "status": 1,
        "initialized": True,
        "realUrl": "",
    }


def test_decode_gzipped_mihon_backup_to_readable_json(tmp_path: Path):
    raw = _backup_manga(source=12345, url="/series/abc", title="Imported Work", favorite=True)
    raw += _backup_manga(source=67890, url="/series/read-only", title="Not In Library", favorite=False)
    payload = gzip.compress(raw)

    decoded = decode_backup_bytes(payload)
    assert decoded["manga_count"] == 2
    assert decoded["favorite_count"] == 1
    assert decoded["manga"][0]["source"] == 12345
    assert decoded["manga"][0]["url"] == "/series/abc"
    assert decoded["manga"][0]["title"] == "Imported Work"
    assert decoded["manga"][1]["favorite"] is False

    source = tmp_path / "library.tachibk"
    readable = tmp_path / "decoded-library.json"
    source.write_bytes(payload)
    decode_backup_file(source, readable)
    parsed = json.loads(readable.read_text(encoding="utf-8"))
    assert parsed["manga"][0]["title"] == "Imported Work"
    assert parsed["favorite_count"] == 1


def test_decode_rejects_non_backup_payload():
    try:
        decode_backup_bytes(b"not a protobuf backup")
    except BackupDecodeError:
        pass
    else:
        raise AssertionError("invalid backup should be rejected")


def test_import_readable_backup_is_add_only_and_skips_nonfavorites(monkeypatch, tmp_path: Path):
    s = AutoSourcarrService()
    _source_row("12345")
    entry_id, _, _ = s.ingest_manga("12345", _manga(50001, "Imported Work", "/series/abc"))

    readable = tmp_path / "decoded-library.json"
    readable.write_text(
        json.dumps(
            {
                "format": "mihon-tachiyomi-protobuf",
                "manga": [
                    {"source": 12345, "url": "/series/abc", "title": "Imported Work", "favorite": True},
                    {"source": 12345, "url": "/series/other", "title": "Not In Library", "favorite": False},
                ],
            }
        ),
        encoding="utf-8",
    )

    class FakeMain:
        def __init__(self):
            self.library_calls = []

        async def sources(self):
            return [{"id": "999", "name": "AutoSourcarr", "displayName": "AutoSourcarr"}]

        async def library_mangas(self):
            return []

        async def fetch_source_manga(self, source_id, mode, page, query_text):
            assert str(source_id) == "999"
            return {"mangas": [{"id": 77001, "sourceId": "999", "url": str(entry_id), "title": "Imported Work"}]}

        async def set_manga_in_library(self, manga_id, in_library=True):
            self.library_calls.append((int(manga_id), bool(in_library)))
            return {"id": int(manga_id), "inLibrary": bool(in_library)}

    fake = FakeMain()
    monkeypatch.setattr(service_module, "main_suwayomi", fake)
    result = asyncio.run(s.import_readable_backup(readable))
    assert result["library_entries"] == 1
    assert result["added"] == 1
    assert result["skipped_existing"] == 0
    assert fake.library_calls == [(77001, True)]
    assert all(in_library is True for _, in_library in fake.library_calls)
    assert result["results"][0]["state"] == "added-main-library"
    assert result["results"][0]["reason"] == "Added to main Suwayomi library"


def test_import_readable_backup_skips_existing_subbed_entry(monkeypatch, tmp_path: Path):
    s = AutoSourcarrService()
    _source_row("54321", "Existing Backup Source")
    entry_id, _, _ = s.ingest_manga("54321", _manga(51001, "Already Subbed", "/series/existing"))
    readable = tmp_path / "decoded-library.json"
    readable.write_text(
        json.dumps({"manga": [{"source": 54321, "url": "/series/existing", "title": "Already Subbed", "favorite": True}]}),
        encoding="utf-8",
    )

    class FakeMain:
        def __init__(self):
            self.library_calls = []

        async def sources(self):
            return [{"id": "999", "name": "AutoSourcarr", "displayName": "AutoSourcarr"}]

        async def library_mangas(self):
            return [{"id": 88001, "sourceId": "999", "url": str(entry_id), "title": "Already Subbed", "inLibrary": True}]

        async def fetch_source_manga(self, *args, **kwargs):
            raise AssertionError("already-subbed entries must not be materialized again")

        async def set_manga_in_library(self, *args, **kwargs):
            self.library_calls.append(args)

    fake = FakeMain()
    monkeypatch.setattr(service_module, "main_suwayomi", fake)
    result = asyncio.run(s.import_readable_backup(readable))
    assert result["added"] == 0
    assert result["skipped_existing"] == 1
    assert fake.library_calls == []


def test_scheduled_import_removes_uploaded_and_readable_files(monkeypatch, tmp_path: Path):
    s = AutoSourcarrService()
    job_dir = tmp_path / "job"
    job_dir.mkdir()
    raw_path = job_dir / "library.tachibk"
    readable_path = job_dir / "decoded-library.json"
    raw = _backup_manga(source=12345, url="/series/cleanup", title="Cleanup Work", favorite=True)
    raw_path.write_bytes(gzip.compress(raw))

    async def fake_import(path: Path, *, task_id: str | None = None, import_id: str | None = None):
        assert path == readable_path
        assert import_id == job_dir.name
        assert readable_path.exists()
        decoded = json.loads(readable_path.read_text(encoding="utf-8"))
        assert decoded["manga"][0]["title"] == "Cleanup Work"
        return {"added": 1, "skipped_existing": 0, "unresolved": 0, "failed": 0}

    monkeypatch.setattr(s, "import_readable_backup", fake_import)

    async def run():
        result = s.schedule_backup_import(raw_path, readable_path, filename="library.tachibk")
        task_id = result["task_id"]
        runtime = s._refresh_tasks[f"backup-import:{task_id}"]
        await runtime
        await asyncio.sleep(0)

    asyncio.run(run())
    assert not raw_path.exists()
    assert not readable_path.exists()
    assert not job_dir.exists()



def test_backup_import_rejects_unconfirmed_main_library_mutation(monkeypatch, tmp_path: Path):
    s = AutoSourcarrService()
    _source_row("22999", "Unconfirmed Source")
    entry_id, _, _ = s.ingest_manga("22999", _manga(52999, "Needs Main Library Confirmation", "/series/confirm"))
    readable = tmp_path / "decoded-library.json"
    readable.write_text(json.dumps({"manga": [{"source": 22999, "url": "/series/confirm", "title": "Needs Main Library Confirmation", "favorite": True}]}), encoding="utf-8")

    class FakeMain:
        async def sources(self):
            return [{"id": "999", "name": "AutoSourcarr", "displayName": "AutoSourcarr"}]
        async def library_mangas(self):
            return []
        async def fetch_source_manga(self, *args, **kwargs):
            return {"mangas": [{"id": 77999, "sourceId": "999", "url": str(entry_id), "title": "Needs Main Library Confirmation"}]}
        async def set_manga_in_library(self, manga_id, in_library=True):
            return {"id": int(manga_id), "inLibrary": False}

    monkeypatch.setattr(service_module, "main_suwayomi", FakeMain())
    result = asyncio.run(s.import_readable_backup(readable))
    assert result["added"] == 0
    assert result["failed"] == 1
    assert result["results"][0]["state"] == "failed"
    assert "did not confirm" in result["results"][0]["reason"]


def test_import_results_are_persisted_after_temporary_backup_is_deleted(monkeypatch, tmp_path: Path):
    s = AutoSourcarrService()
    _source_row("73001", "Persistent Import Source")
    entry_id, _, _ = s.ingest_manga("73001", _manga(73002, "Persistent Import Work", "/series/persistent"))
    readable = tmp_path / "decoded-library.json"
    readable.write_text(json.dumps({"manga": [
        {"source": 73001, "url": "/series/persistent", "title": "Persistent Import Work", "favorite": True},
        {"source": 73001, "url": "/series/missing", "title": "Unresolved Persistent Work", "favorite": True},
    ]}), encoding="utf-8")

    class FakeMain:
        async def sources(self):
            return [{"id": "999", "name": "AutoSourcarr", "displayName": "AutoSourcarr"}]
        async def library_mangas(self):
            return []
        async def fetch_source_manga(self, source_id, mode, page, query_text):
            if "Persistent Import Work" in query_text:
                return {"mangas": [{"id": 73003, "sourceId": "999", "url": str(entry_id), "title": "Persistent Import Work"}]}
            return {"mangas": []}
        async def set_manga_in_library(self, manga_id, in_library=True):
            return {"id": int(manga_id), "inLibrary": True}

    monkeypatch.setattr(service_module, "main_suwayomi", FakeMain())
    s._create_import_run("run-persist", "library.tachibk")
    result = asyncio.run(s.import_readable_backup(readable, import_id="run-persist"))
    s._recount_import_run("run-persist", state="completed")
    assert result["added"] == 1
    assert result["unresolved"] == 1
    runs = s.list_import_runs()
    run = next(r for r in runs if r["id"] == "run-persist")
    assert run["added"] == 1 and run["unresolved"] == 1
    items = s.import_run_items("run-persist")["items"]
    assert {x["state"] for x in items} == {"added", "unresolved"}
    unresolved = next(x for x in items if x["state"] == "unresolved")
    assert unresolved["title"] == "Unresolved Persistent Work"


def test_manual_import_resolution_adds_real_main_library_and_updates_state(monkeypatch, tmp_path: Path):
    s = AutoSourcarrService()
    _source_row("74001", "Manual Import Source")
    entry_id, _, _ = s.ingest_manga("74001", _manga(74002, "Manual Import Target", "/series/manual-target"))
    s._create_import_run("run-manual", "manual.tachibk")
    item = {"source": 74001, "url": "/old/source", "title": "Old Backup Name", "favorite": True}
    item_id = s._store_import_item("run-manual", 1, item, state="unresolved", reason="not found", entry_id=None)

    class FakeMain:
        def __init__(self): self.added=[]
        async def sources(self): return [{"id":"999","name":"AutoSourcarr","displayName":"AutoSourcarr"}]
        async def library_mangas(self): return []
        async def fetch_source_manga(self, source_id, mode, page, query_text):
            return {"mangas":[{"id":74003,"sourceId":"999","url":str(entry_id),"title":"Manual Import Target"}]}
        async def set_manga_in_library(self, manga_id, in_library=True):
            self.added.append(int(manga_id)); return {"id":int(manga_id),"inLibrary":True}
    fake=FakeMain(); monkeypatch.setattr(service_module,"main_suwayomi",fake)
    resolved=asyncio.run(s.match_import_item("run-manual",item_id,entry_id))
    assert resolved["state"] == "added"
    assert fake.added == [74003]
    saved=s.import_run_items("run-manual")["items"][0]
    assert saved["state"] == "added" and saved["entry_id"] == entry_id


def test_import_existing_match_short_circuits_private_source_search(monkeypatch, tmp_path: Path):
    s = AutoSourcarrService()
    _source_row("75001", "Existing Fast Path Source")
    entry_id, _, _ = s.ingest_manga("75001", _manga(75002, "Existing Fast Path Work", "/series/existing-fast"))
    readable = tmp_path / "existing-fast.json"
    readable.write_text(json.dumps({"manga": [
        {"source": 75001, "url": "/series/existing-fast", "title": "Existing Fast Path Work", "favorite": True}
    ]}), encoding="utf-8")

    class FakeMain:
        async def sources(self):
            return [{"id": "999", "name": "AutoSourcarr", "displayName": "AutoSourcarr"}]
        async def library_mangas(self):
            return [{"id": 75003, "sourceId": "999", "url": str(entry_id), "title": "Existing Fast Path Work", "inLibrary": True}]

    class ForbiddenSourceHost:
        async def fetch_source_manga(self, *args, **kwargs):
            raise AssertionError("existing import entries must not search the private source host")

    monkeypatch.setattr(service_module, "main_suwayomi", FakeMain())
    monkeypatch.setattr(service_module, "source_host", ForbiddenSourceHost())
    result = asyncio.run(s.import_readable_backup(readable))
    assert result["skipped_existing"] == 1
    assert result["added"] == 0
    assert result["unresolved"] == 0


def test_enabled_backup_source_exact_url_becomes_resolved_reference(monkeypatch):
    s = AutoSourcarrService()
    _source_row("76001", "Enabled Backup Source")

    class FakeSourceHost:
        async def fetch_source_manga(self, source_id, mode, page, query):
            assert str(source_id) == "76001"
            return {"mangas": [_manga(76002, "Native Backup Work", "/series/native-work")], "hasNextPage": False}
        async def fetch_manga_and_chapters(self, manga_id, fetch_manga=True, fetch_chapters=False):
            manga = _manga(76002, "Native Backup Work", "/series/native-work")
            manga["description"] = "A detailed source summary that can be used to rank other import candidates reliably."
            return {"manga": manga, "chapters": []}

    monkeypatch.setattr(service_module, "source_host", FakeSourceHost())
    details = asyncio.run(s.resolve_backup_manga_entry_details({
        "source": 76001, "url": "/series/native-work", "title": "Native Backup Work", "favorite": True,
    }))
    assert details["entry_id"] is not None
    assert details["reference_entry_id"] == details["entry_id"]
    assert "source URL" in details["reason"]
    summary = s._entry_summary(int(details["entry_id"]))
    assert "detailed source summary" in summary["description"].lower()


def test_enabled_source_unique_full_title_reference_auto_matches(monkeypatch):
    s = AutoSourcarrService()
    _source_row("76101", "Reference Only Source")

    class FakeSourceHost:
        async def fetch_source_manga(self, source_id, mode, page, query):
            return {"mangas": [_manga(76102, "Reference Only Work", "/series/new-url")], "hasNextPage": False}
        async def fetch_manga_and_chapters(self, manga_id, fetch_manga=True, fetch_chapters=False):
            manga = _manga(76102, "Reference Only Work", "/series/new-url")
            manga["description"] = "Reference synopsis retained for manual import review."
            return {"manga": manga, "chapters": []}

    monkeypatch.setattr(service_module, "source_host", FakeSourceHost())
    details = asyncio.run(s.resolve_backup_manga_entry_details({
        "source": 76101, "url": "/series/old-url", "title": "Reference Only Work", "favorite": True,
    }))
    assert details["entry_id"] is not None
    assert details["reference_entry_id"] == details["entry_id"]
    assert "title" in details["reason"] or "compatible" in details["reason"]


def test_unresolved_suggestions_filter_unrelated_titles_and_use_summary():
    s = AutoSourcarrService()
    _source_row("77001", "Suggestion Source")
    matching = _manga(77002, "A Completely Different English Title", "/series/summary-match")
    matching["description"] = "A prince is betrayed by his clan and returns years later to reclaim his lost throne with forbidden cultivation."
    matching_id, _, _ = s.ingest_manga("77001", matching)
    unrelated = _manga(77003, "Solo Max-Level Newbie", "/series/unrelated")
    unrelated["description"] = "A gamer climbs a mysterious tower after a game becomes reality."
    unrelated_id, _, _ = s.ingest_manga("77001", unrelated)

    item = {
        "source": 999999,
        "title": "Zheng Yan Hou, Wo De Dizi Chengle Nvdi Da Mo Tou?!",
        "description": "A prince is betrayed by his clan and returns years later to reclaim his lost throne with forbidden cultivation.",
        "author": "",
    }
    ids = s._backup_candidate_ids(item, limit=8)
    assert int(matching_id) in ids
    assert int(unrelated_id) not in ids


def test_unresolved_translation_and_suggestions_enrich_after_import(monkeypatch):
    s = AutoSourcarrService()
    _source_row("78001", "Enrichment Candidate Source")
    target = _manga(78002, "English Target Title", "/series/enrichment-target")
    target["description"] = "A foreign-language hero returns home and discovers that their former disciple has become an empress."
    target_id, _, _ = s.ingest_manga("78001", target)
    s._create_import_run("run-enrich", "enrich.tachibk")
    item = {
        "source": 999999,
        "url": "/foreign/work",
        "title": "外国语标题",
        "description": "A foreign-language hero returns home and discovers that their former disciple has become an empress.",
        "favorite": True,
    }
    item_id = s._store_import_item(
        "run-enrich", 1, item, state="unresolved", reason="not found", entry_id=None,
        generate_candidates=False,
    )

    class FakeTranslator:
        enabled = True
        def cached(self, text, target): return ""
        async def translate(self, text, target, source="auto"):
            return "English Target Title"

    monkeypatch.setattr(service_module, "translator", FakeTranslator())

    async def run():
        task_id = s.schedule_import_translation_enrichment("run-enrich")
        assert task_id
        runtime = next(v for k, v in s._refresh_tasks.items() if k.startswith("import-enrich:run-enrich:"))
        await runtime
        await asyncio.sleep(0)

    asyncio.run(run())
    with db.catalog() as c:
        row = c.execute("SELECT english_title,translation_checked,candidate_ids_json FROM import_items WHERE id=?", (item_id,)).fetchone()
    assert row["english_title"] == "English Target Title"
    assert int(row["translation_checked"]) == 1
    assert int(target_id) in json.loads(row["candidate_ids_json"])


def test_decode_backup_embedded_source_name_is_attached_to_manga():
    raw = _backup_manga(source=88123, url="/series/named", title="Named Source Work", favorite=True)
    raw += _backup_source(source=88123, name="MangaFire (EN)")
    decoded = decode_backup_bytes(gzip.compress(raw))
    assert decoded["backup_sources"] == [{"name": "MangaFire (EN)", "sourceId": 88123}]
    assert decoded["manga"][0]["sourceName"] == "MangaFire (EN)"


def test_import_run_uses_backup_source_name_when_extension_is_not_present():
    s = AutoSourcarrService()
    s._create_import_run("run-source-name", "named.tachibk")
    item = {
        "source": 991234567,
        "sourceName": "Expected Missing Source",
        "url": "/series/missing-source",
        "title": "Missing Source Work",
        "favorite": True,
    }
    s._store_import_item("run-source-name", 1, item, state="unresolved", reason="not found", entry_id=None, generate_candidates=False)
    saved = s.import_run_items("run-source-name")["items"][0]
    assert saved["source_name"] == "Expected Missing Source"


def test_import_binds_enabled_backup_source_to_found_entry(monkeypatch):
    s = AutoSourcarrService()
    _source_row("88201", "Enabled Original Source")
    _source_row("88202", "Catalog Source")
    entry_id, _, _ = s.ingest_manga("88202", _manga(88220, "Binding Target", "/catalog/binding-target"))
    calls = []

    class FakeSourceHost:
        async def fetch_source_manga(self, source_id, mode, page, query):
            calls.append((str(source_id), mode, page, query))
            return {"mangas": [_manga(88210, "Binding Target", "/original/binding-target")], "hasNextPage": False}
        async def fetch_manga_and_chapters(self, manga_id, fetch_manga=True, fetch_chapters=False):
            return {"manga": _manga(88210, "Binding Target", "/original/binding-target"), "chapters": []}

    monkeypatch.setattr(service_module, "source_host", FakeSourceHost())
    result = asyncio.run(s._bind_backup_source_to_entry(entry_id, {
        "source": 88201, "sourceName": "Enabled Original Source",
        "url": "/original/binding-target", "title": "Binding Target",
    }))
    assert result["bound"] is True and result["searched"] is True
    assert len(calls) == 1
    entry = s.get_entry(int(result["entry_id"]))
    bound = [x for x in entry["matches"] + entry["source_variants"] if str(x["source_id"]) == "88201"]
    assert len(bound) == 1
    assert int(bound[0]["source_manga_id"]) == 88210
    assert bound[0]["match_type"] == "import"


def test_import_binding_existing_source_match_skips_network(monkeypatch):
    s = AutoSourcarrService()
    _source_row("88301", "Existing Original Source")
    entry_id, _, _ = s.ingest_manga("88301", _manga(88310, "Already Bound Work", "/original/already-bound"))

    class ForbiddenSourceHost:
        async def fetch_source_manga(self, *args, **kwargs):
            raise AssertionError("an already-bound backup source must not be searched again")

    monkeypatch.setattr(service_module, "source_host", ForbiddenSourceHost())
    result = asyncio.run(s._bind_backup_source_to_entry(entry_id, {
        "source": 88301, "sourceName": "Existing Original Source",
        "url": "/original/already-bound", "title": "Already Bound Work",
    }))
    assert result["bound"] is True
    assert result["searched"] is False


def test_clear_import_history_preserves_running_imports():
    s = AutoSourcarrService()
    # The test module intentionally shares one temporary DB. Remove finished rows
    # left by earlier import tests so this assertion measures only the rows below.
    s.clear_import_history()
    before_running = sum(1 for row in s.list_import_runs(100) if row["state"] == "running")
    s._create_import_run("run-old", "old.tachibk")
    s._store_import_item("run-old", 0, {"title": "Old", "source": 1}, state="existing", reason="already there", entry_id=1)
    s._recount_import_run("run-old", state="completed")
    s._create_import_run("run-live", "live.tachibk")
    s._store_import_item("run-live", 0, {"title": "Live", "source": 2}, state="unresolved", reason="pending", entry_id=None)

    result = s.clear_import_history()

    assert result["runs_cleared"] == 1
    assert result["items_cleared"] == 1
    assert result["running_preserved"] == before_running + 1
    runs = {row["id"]: row for row in s.list_import_runs(100)}
    assert "run-old" not in runs
    assert "run-live" in runs
    assert s.import_run_items("run-live")["items"][0]["title"] == "Live"


def test_import_title_fallbacks_keep_the_on_short_titles_and_only_drop_it_on_long_titles():
    # Three-word identities keep their leading article even when an edition suffix is
    # present.  The suffix may still be a second-pass search hint.
    assert service_module._fallback_title_queries("The Example Work (Colored)") == [
        "The Example Work",
    ]
    assert service_module._fallback_title_queries("The Hero Returns") == []
    # Four-or-more-word titles may use the article fallback after the exact search.
    assert service_module._fallback_title_queries("The Example Work Series (Colored)") == [
        "Example Work Series (Colored)",
        "The Example Work Series",
        "Example Work Series",
    ]
    # Semantic uses of the word colored are not edition markers and short titles keep The.
    assert service_module._fallback_title_queries("The Colored Pencil") == []


def test_backup_source_numeric_miss_can_resolve_unique_enabled_source_by_name():
    s = AutoSourcarrService()
    _source_row("88401", "MangaFire")
    s._reload_source_cache()
    assert s._current_source_id_for_backup(999999999, "MangaFire (EN)") == "88401"


def test_import_original_source_binding_retries_without_leading_the_only_for_long_title(monkeypatch):
    s = AutoSourcarrService()
    _source_row("88501", "Original Article Source")
    _source_row("88502", "Catalog Article Source")
    entry_id, _, _ = s.ingest_manga("88502", _manga(88520, "The Article Work Series", "/catalog/article"))
    calls: list[str] = []

    class FakeSourceHost:
        async def fetch_source_manga(self, source_id, mode, page, query):
            calls.append(query)
            if query == "Article Work Series":
                return {"mangas": [_manga(88510, "The Article Work Series", "/original/article")], "hasNextPage": False}
            return {"mangas": [], "hasNextPage": False}
        async def fetch_manga_and_chapters(self, manga_id, fetch_manga=True, fetch_chapters=False):
            return {"manga": _manga(88510, "The Article Work Series", "/original/article"), "chapters": []}

    monkeypatch.setattr(service_module, "source_host", FakeSourceHost())
    result = asyncio.run(s._bind_backup_source_to_entry(entry_id, {
        "source": 88501, "sourceName": "Original Article Source",
        "url": "/original/article", "title": "The Article Work Series",
    }))
    assert result["bound"] is True
    assert calls[:2] == ["The Article Work Series", "Article Work Series"]


def test_import_original_source_binding_does_not_drop_the_from_short_title(monkeypatch):
    s = AutoSourcarrService()
    _source_row("88511", "Short Article Source")
    _source_row("88512", "Short Catalog Source")
    entry_id, _, _ = s.ingest_manga("88512", _manga(88521, "The Hero Returns", "/catalog/hero"))
    calls: list[str] = []

    class FakeSourceHost:
        async def fetch_source_manga(self, source_id, mode, page, query):
            calls.append(query)
            return {"mangas": [], "hasNextPage": False}

    monkeypatch.setattr(service_module, "source_host", FakeSourceHost())
    result = asyncio.run(s._bind_backup_source_to_entry(entry_id, {
        "source": 88511, "sourceName": "Short Article Source",
        "url": "/missing/hero", "title": "The Hero Returns",
    }))
    assert result["bound"] is False
    assert calls[0] == "The Hero Returns"
    assert "Hero Returns" not in calls


def test_import_original_source_binding_retries_without_colored_suffix(monkeypatch):
    s = AutoSourcarrService()
    _source_row("88601", "Original Color Source")
    _source_row("88602", "Catalog Color Source")
    entry_id, _, _ = s.ingest_manga("88602", _manga(88620, "Edition Work (Colored)", "/catalog/color"))
    calls: list[str] = []

    class FakeSourceHost:
        async def fetch_source_manga(self, source_id, mode, page, query):
            calls.append(query)
            if query == "Edition Work":
                return {"mangas": [_manga(88610, "Edition Work", "/original/color")], "hasNextPage": False}
            return {"mangas": [], "hasNextPage": False}
        async def fetch_manga_and_chapters(self, manga_id, fetch_manga=True, fetch_chapters=False):
            return {"manga": _manga(88610, "Edition Work", "/original/color"), "chapters": []}

    monkeypatch.setattr(service_module, "source_host", FakeSourceHost())
    result = asyncio.run(s._bind_backup_source_to_entry(entry_id, {
        "source": 88601, "sourceName": "Original Color Source",
        "url": "/original/color", "title": "Edition Work (Colored)",
    }))
    assert result["bound"] is True
    assert calls == ["Edition Work (Colored)", "Edition Work"]


def test_import_binding_can_use_embedded_source_name_when_numeric_id_changed(monkeypatch):
    s = AutoSourcarrService()
    _source_row("88701", "Current Named Source")
    _source_row("88702", "Catalog Source Name Target")
    s._reload_source_cache()
    entry_id, _, _ = s.ingest_manga("88702", _manga(88720, "Source Name Target", "/catalog/name-target"))

    class FakeSourceHost:
        async def fetch_source_manga(self, source_id, mode, page, query):
            assert str(source_id) == "88701"
            return {"mangas": [_manga(88710, "Source Name Target", "/original/name-target")], "hasNextPage": False}
        async def fetch_manga_and_chapters(self, manga_id, fetch_manga=True, fetch_chapters=False):
            return {"manga": _manga(88710, "Source Name Target", "/original/name-target"), "chapters": []}

    monkeypatch.setattr(service_module, "source_host", FakeSourceHost())
    result = asyncio.run(s._bind_backup_source_to_entry(entry_id, {
        "source": 123456789, "sourceName": "Current Named Source (EN)",
        "url": "/original/name-target", "title": "Source Name Target",
    }))
    assert result["bound"] is True
    assert result["source_id"] == "88701"


def test_import_original_source_can_bind_when_general_discovery_search_is_disabled(monkeypatch):
    s = AutoSourcarrService()
    _source_row("88801", "Import Only Source")
    _source_row("88802", "Import Only Catalog")
    with db.catalog() as c:
        c.execute("UPDATE source_config SET enabled_search=0 WHERE source_id='88801'")
    s._reload_source_cache()
    entry_id, _, _ = s.ingest_manga("88802", _manga(88820, "Import Only Work", "/catalog/import-only"))

    class FakeSourceHost:
        async def fetch_source_manga(self, source_id, mode, page, query):
            assert str(source_id) == "88801"
            return {"mangas": [_manga(88810, "Import Only Work", "/original/import-only")], "hasNextPage": False}
        async def fetch_manga_and_chapters(self, manga_id, fetch_manga=True, fetch_chapters=False):
            return {"manga": _manga(88810, "Import Only Work", "/original/import-only"), "chapters": []}

    monkeypatch.setattr(service_module, "source_host", FakeSourceHost())
    result = asyncio.run(s._bind_backup_source_to_entry(entry_id, {
        "source": 88801, "sourceName": "Import Only Source",
        "url": "/original/import-only", "title": "Import Only Work",
    }))
    assert result["bound"] is True
    assert result["source_id"] == "88801"


def test_internal_entry_lookup_is_local_and_never_touches_source_host(monkeypatch):
    s = AutoSourcarrService()
    _source_row("88901", "Internal Lookup Source")
    entry_id, _, _ = s.ingest_manga("88901", _manga(88910, "Instant Library Target", "/instant/target"))
    calls = 0

    class NoSourceHost:
        async def fetch_source_manga(self, *args, **kwargs):
            nonlocal calls
            calls += 1
            raise AssertionError("internal entry lookup must not touch the private source host")

    monkeypatch.setattr(service_module, "source_host", NoSourceHost())
    result = asyncio.run(s.federated("SEARCH", query=f"__autosourcarr_entry_id__:{entry_id}", min_matches=0))
    assert result["internal_entry_lookup"] is True
    assert result["items"][0]["canonical_id"] == entry_id
    assert calls == 0


def test_scheduled_import_match_changes_state_before_background_work_finishes(monkeypatch):
    from app.task_registry import task_registry

    s = AutoSourcarrService()
    _source_row("88911", "Fast Import Source")
    entry_id, _, _ = s.ingest_manga("88911", _manga(88912, "Fast Import Work", "/fast/import"))
    s._create_import_run("run-fast-ui", "fast-ui.tachibk")
    item_id = s._store_import_item(
        "run-fast-ui",
        1,
        {"source": 88911, "sourceName": "Fast Import Source", "url": "/fast/import", "title": "Fast Import Work"},
        state="unresolved",
        reason="Needs review",
        entry_id=None,
        generate_candidates=False,
    )

    async def scenario():
        gate = asyncio.Event()

        async def slow_match(import_id, current_item_id, current_entry_id, *, task_id=None, decision_revision=None):
            await gate.wait()
            return s._update_import_item_state(import_id, current_item_id, "added", "Done", current_entry_id)

        monkeypatch.setattr(s, "match_import_item", slow_match)
        queued = s.schedule_import_item_match("run-fast-ui", item_id, entry_id)
        assert queued["queued"] is True
        assert queued["state"] == "resolving"

        immediate = s.import_run_item("run-fast-ui", item_id)["item"]
        assert immediate["state"] == "resolving"
        assert immediate["entry_id"] == entry_id
        assert queued["task_id"] in {x["id"] for x in task_registry.snapshot()["items"] if x["state"] == "running"}

        gate.set()
        await s._refresh_tasks[f"__import-match__:run-fast-ui:{item_id}"]
        final = s.import_run_item("run-fast-ui", item_id)["item"]
        assert final["state"] == "added"

    asyncio.run(scenario())


def test_backup_source_url_slug_is_used_before_weak_title_fallbacks(monkeypatch):
    s = AutoSourcarrService()
    _source_row("89001", "MangaFire")
    s._reload_source_cache()
    calls: list[str] = []

    class FakeSourceHost:
        async def fetch_source_manga(self, source_id, mode, page, query):
            calls.append(query)
            if query == "hero i quit a long time agoo":
                return {"mangas": [_manga(89002, "The Hero Returns (Hero? I Quit a Long Time Ago)", "/manga/hero-i-quit-a-long-time-agoo.rjmmy")], "hasNextPage": False}
            return {"mangas": [], "hasNextPage": False}
        async def fetch_manga_and_chapters(self, manga_id, fetch_manga=True, fetch_chapters=False):
            return {"manga": _manga(89002, "The Hero Returns (Hero? I Quit a Long Time Ago)", "/manga/hero-i-quit-a-long-time-agoo.rjmmy"), "chapters": []}

    monkeypatch.setattr(service_module, "source_host", FakeSourceHost())
    details = asyncio.run(s.resolve_backup_manga_entry_details({
        "source": 89001,
        "sourceName": "MangaFire (EN)",
        "url": "/manga/hero-i-quit-a-long-time-agoo.rjmmy",
        "title": "The Return of the Superhero (Colored)",
    }))
    assert details["entry_id"] is not None
    assert details["reference_entry_id"] == details["entry_id"]
    assert calls[:2] == ["The Return of the Superhero (Colored)", "hero i quit a long time agoo"]


def test_backup_source_url_slug_finds_romanized_title(monkeypatch):
    s = AutoSourcarrService()
    _source_row("89101", "MangaFire")
    s._reload_source_cache()
    calls: list[str] = []
    romanized = "tsuihou sareta onimotsu tamer sekai yuiitsu no necromancer ni kakusei suruu"

    class FakeSourceHost:
        async def fetch_source_manga(self, source_id, mode, page, query):
            calls.append(query)
            if query == romanized:
                return {"mangas": [_manga(89102, "Tsuihou Sareta Onimotsu Tamer, Sekai Yuiitsu no Necromancer ni Kakusei Suru", "/manga/tsuihou-sareta-onimotsu-tamer-sekai-yuiitsu-no-necromancer-ni-kakusei-suruu.jnjky")], "hasNextPage": False}
            return {"mangas": [], "hasNextPage": False}
        async def fetch_manga_and_chapters(self, manga_id, fetch_manga=True, fetch_chapters=False):
            return {"manga": _manga(89102, "Tsuihou Sareta Onimotsu Tamer, Sekai Yuiitsu no Necromancer ni Kakusei Suru", "/manga/tsuihou-sareta-onimotsu-tamer-sekai-yuiitsu-no-necromancer-ni-kakusei-suruu.jnjky"), "chapters": []}

    monkeypatch.setattr(service_module, "source_host", FakeSourceHost())
    details = asyncio.run(s.resolve_backup_manga_entry_details({
        "source": 89101,
        "sourceName": "MangaFire (EN)",
        "url": "/manga/tsuihou-sareta-onimotsu-tamer-sekai-yuiitsu-no-necromancer-ni-kakusei-suruu.jnjky",
        "title": "The Tale of the Necromancer.",
    }))
    assert details["entry_id"] is not None
    assert calls[:2] == ["The Tale of the Necromancer.", romanized]


def test_live_import_search_can_catalog_original_source_result(monkeypatch):
    s = AutoSourcarrService()
    _source_row("89201", "MangaFire")
    s._reload_source_cache()
    s._create_import_run("run-live-import-search", "live-search.tachibk")
    item = {
        "source": 89201,
        "sourceName": "MangaFire (EN)",
        "url": "/manga/hero-i-quit-a-long-time-agoo.rjmmy",
        "title": "The Return of the Superhero (Colored)",
        "favorite": True,
    }
    item_id = s._store_import_item(
        "run-live-import-search", 1, item,
        state="unresolved", reason="not found", entry_id=None, generate_candidates=False,
    )

    class FakeSourceHost:
        async def fetch_source_manga(self, source_id, mode, page, query):
            if query == "hero i quit a long time agoo":
                return {"mangas": [_manga(89202, "The Hero Returns (Hero? I Quit a Long Time Ago)", "/manga/hero-i-quit-a-long-time-agoo.rjmmy")], "hasNextPage": False}
            return {"mangas": [], "hasNextPage": False}
        async def fetch_manga_and_chapters(self, manga_id, fetch_manga=True, fetch_chapters=False):
            return {"manga": _manga(89202, "The Hero Returns (Hero? I Quit a Long Time Ago)", "/manga/hero-i-quit-a-long-time-agoo.rjmmy"), "chapters": []}

    monkeypatch.setattr(service_module, "source_host", FakeSourceHost())
    result = asyncio.run(s.import_item_live_suggestions(
        "run-live-import-search", item_id, "Hero, I Quit a Long Time Ago"
    ))
    assert result["suggestions"]
    assert any("Hero Returns" in str(x.get("preferred_title") or "") for x in result["suggestions"])
    assert "live_searched" in result


def test_import_added_existing_rows_expose_the_accepted_match_card_data():
    s = AutoSourcarrService()
    _source_row("89301", "Accepted Match Source")
    entry_id, _, _ = s.ingest_manga("89301", _manga(89302, "Accepted Import Work", "/accepted/import-work"))
    s._create_import_run("run-accepted-card", "accepted.tachibk")
    s._store_import_item(
        "run-accepted-card", 1,
        {"source": 89301, "sourceName": "Accepted Match Source", "url": "/accepted/import-work", "title": "Accepted Import Work"},
        state="added", reason="Added to main Suwayomi library", entry_id=entry_id, generate_candidates=False,
    )
    row = s.import_run_items("run-accepted-card")["items"][0]
    assert row["matched_entry"]["id"] == entry_id
    assert row["matched_entry"]["preferred_title"] == "Accepted Import Work"
    assert any("Accepted Match Source" in source for source in row["matched_entry"]["sources"])


def test_import_rejection_survives_database_reset_and_filters_rebuilt_candidate():
    s = AutoSourcarrService()
    _source_row("89401", "Durable Reject Source")
    item = {
        "source": 89401, "sourceName": "Durable Reject Source",
        "url": "/durable/rejected-work", "title": "Durable Rejected Work",
    }
    entry_id, _, _ = s.ingest_manga("89401", _manga(89402, "Durable Rejected Work", "/durable/rejected-work"))
    remembered = s._remember_import_rejection(item, entry_id)
    assert remembered["signatures"] >= 1
    assert s._is_import_entry_rejected(item, entry_id) is True

    s.reset_database()
    _source_row("89401", "Durable Reject Source")
    s._reload_source_cache()
    rebuilt_id, _, _ = s.ingest_manga("89401", _manga(89402, "Durable Rejected Work", "/durable/rejected-work"))
    assert s._is_import_entry_rejected(item, rebuilt_id) is True
    assert rebuilt_id not in s._backup_candidate_ids(item, limit=8)


def test_reject_import_match_updates_state_before_background_cleanup(monkeypatch):
    from app.task_registry import task_registry

    s = AutoSourcarrService()
    _source_row("89501", "Reject UI Source")
    entry_id, _, _ = s.ingest_manga("89501", _manga(89502, "Wrong Imported Work", "/reject/wrong"))
    s._create_import_run("run-reject-ui", "reject-ui.tachibk")
    item_id = s._store_import_item(
        "run-reject-ui", 1,
        {"source": 89501, "sourceName": "Reject UI Source", "url": "/reject/wrong", "title": "Backup Intended Work"},
        state="added", reason="Added to main Suwayomi library", entry_id=entry_id, generate_candidates=False,
    )

    async def scenario():
        gate = asyncio.Event()

        async def slow_remove(current_entry_id, current_item_id, **kwargs):
            await gate.wait()
            return {"removed": True, "reason": "removed"}

        async def no_alternative(*args, **kwargs):
            with db.catalog() as c:
                c.execute("UPDATE import_items SET suggestion_checked=1 WHERE id=?", (item_id,))
            return {"suggestions": []}

        monkeypatch.setattr(s, "_remove_import_added_entry_from_main_library", slow_remove)
        monkeypatch.setattr(s, "import_item_live_suggestions", no_alternative)
        queued = s.schedule_import_item_reject("run-reject-ui", item_id)
        assert queued["queued"] is True
        immediate = s.import_run_item("run-reject-ui", item_id)["item"]
        assert immediate["state"] == "unresolved"
        assert immediate["entry_id"] is None
        assert queued["task_id"] in {x["id"] for x in task_registry.snapshot()["items"] if x["state"] == "running"}
        assert s._is_import_entry_rejected({
            "source": 89501, "sourceName": "Reject UI Source", "url": "/reject/wrong", "title": "Backup Intended Work"
        }, entry_id)
        gate.set()
        await s._refresh_tasks[f"__import-reject__:run-reject-ui:{item_id}"]

    asyncio.run(scenario())


def test_import_enrichment_live_searches_rows_with_no_local_candidate(monkeypatch):
    s = AutoSourcarrService()
    s._create_import_run("run-auto-live-rescue", "auto-live.tachibk")
    item_id = s._store_import_item(
        "run-auto-live-rescue", 1,
        {"source": 999123, "sourceName": "Missing Old Source", "url": "/missing/old", "title": "Perfect Manual Search Work"},
        state="unresolved", reason="not found", entry_id=None,
        translation_checked=True, suggestion_checked=False, generate_candidates=False,
    )
    monkeypatch.setattr(s, "_backup_candidate_ids", lambda *args, **kwargs: [])
    calls = []

    async def fake_live(import_id, current_item_id, query="", *, automatic=False):
        calls.append((import_id, current_item_id, query, automatic))
        with db.catalog() as c:
            c.execute("UPDATE import_items SET suggestion_checked=1 WHERE id=?", (current_item_id,))
        return {"suggestions": []}

    monkeypatch.setattr(s, "import_item_live_suggestions", fake_live)

    async def scenario():
        task_id = s.schedule_import_translation_enrichment("run-auto-live-rescue")
        assert task_id
        runtime = next(v for k, v in s._refresh_tasks.items() if k.startswith("import-enrich:run-auto-live-rescue:"))
        await runtime

    asyncio.run(scenario())
    assert calls == [("run-auto-live-rescue", item_id, "Perfect Manual Search Work", True)]
    with db.catalog() as c:
        checked = c.execute("SELECT suggestion_checked FROM import_items WHERE id=?", (item_id,)).fetchone()[0]
    assert int(checked) == 1


def test_import_added_match_snapshot_survives_catalog_entry_disappearing():
    s = AutoSourcarrService()
    _source_row("99001", "Snapshot Source")
    s._reload_source_cache()
    entry_id, _, _ = s.ingest_manga("99001", _manga(99002, "Snapshot Review Work", "/snapshot/review"))
    s._create_import_run("run-snapshot-review", "snapshot.tachibk")
    item_id = s._store_import_item(
        "run-snapshot-review", 1,
        {"source": 99001, "sourceName": "Snapshot Source", "url": "/snapshot/review", "title": "Snapshot Review Work"},
        state="added", reason="Added to main Suwayomi library", entry_id=entry_id,
        generate_candidates=False,
    )
    first = s.import_run_item("run-snapshot-review", item_id)["item"]
    assert first["applied_match"]["preferred_title"] == "Snapshot Review Work"

    # Import review is an audit trail. It must still show what was accepted even if
    # a later catalog rebuild temporarily removes the current entry row.
    with db.catalog() as c:
        c.execute("DELETE FROM source_matches WHERE entry_id=?", (entry_id,))
        c.execute("DELETE FROM source_variants WHERE entry_id=?", (entry_id,))
        c.execute("DELETE FROM titles WHERE entry_id=?", (entry_id,))
        c.execute("DELETE FROM entries WHERE id=?", (entry_id,))
    second = s.import_run_item("run-snapshot-review", item_id)["item"]
    assert second["applied_match"]["preferred_title"] == "Snapshot Review Work"
    assert second["matched_entry"]["preferred_title"] == "Snapshot Review Work"


def test_automatic_import_candidate_search_reaches_enabled_source_beyond_first_ten(monkeypatch):
    s = AutoSourcarrService()
    for idx in range(1, 13):
        _source_row(str(99100 + idx), f"Candidate Source {idx:02d}")
    s._reload_source_cache()
    s._create_import_run("run-all-enabled-sources", "all-sources.tachibk")
    item_id = s._store_import_item(
        "run-all-enabled-sources", 1,
        {"source": 199999, "sourceName": "Missing Original Source", "url": "/old/work", "title": "Perfect Across Sources"},
        state="unresolved", reason="not found", entry_id=None,
        translation_checked=True, suggestion_checked=False, generate_candidates=False,
    )
    calls: list[tuple[str, str]] = []

    async def fake_fetch(source_id, mode, page, query_text):
        calls.append((str(source_id), str(query_text)))
        if str(source_id) == "99112" and str(query_text) == "Perfect Across Sources":
            return {"mangas": [_manga(9911201, "Perfect Across Sources", "/perfect-across-sources")]}
        return {"mangas": []}

    monkeypatch.setattr(service_module.source_host, "fetch_source_manga", fake_fetch)
    monkeypatch.setattr(s, "schedule_source_metadata_hydration", lambda *args, **kwargs: None)

    result = asyncio.run(
        s.import_item_live_suggestions(
            "run-all-enabled-sources", item_id, "Perfect Across Sources", automatic=True,
        )
    )
    assert any(source_id == "99112" for source_id, _ in calls)
    assert result["suggestions"]
    assert result["suggestions"][0]["preferred_title"] == "Perfect Across Sources"
    with db.catalog() as c:
        row = c.execute("SELECT suggestion_checked,suggestion_state FROM import_items WHERE id=?", (item_id,)).fetchone()
    assert int(row["suggestion_checked"]) == 1
    assert row["suggestion_state"] == "done"


def test_v245_candidate_pipeline_upgrade_requeues_existing_unresolved_once(monkeypatch):
    s = AutoSourcarrService()
    s._create_import_run("run-v245-upgrade", "old-v244.tachibk")
    item_id = s._store_import_item(
        "run-v245-upgrade", 1,
        {"source": 123, "sourceName": "Old Source", "url": "/old", "title": "Old Unresolved"},
        state="unresolved", reason="old limited search", entry_id=None,
        translation_checked=True, suggestion_checked=True, suggestion_state="done", generate_candidates=False,
    )
    db.set_setting("import_candidate_pipeline_version", "0.2.52")
    calls: list[str] = []
    monkeypatch.setattr(s, "schedule_import_translation_enrichment", lambda run_id: calls.append(str(run_id)) or "queued-task")

    async def scenario():
        task_id = s.schedule_import_candidate_pipeline_upgrade(delay=0)
        assert task_id
        runtime = next(v for k, v in s._refresh_tasks.items() if k.startswith("import-candidate-upgrade:"))
        await runtime
        await asyncio.sleep(0)

    asyncio.run(scenario())
    assert "run-v245-upgrade" in calls
    with db.catalog() as c:
        row = c.execute("SELECT suggestion_checked,suggestion_state FROM import_items WHERE id=?", (item_id,)).fetchone()
    assert int(row["suggestion_checked"]) == 0
    assert row["suggestion_state"] == "queued"
    assert db.setting("import_candidate_pipeline_version", "") == "0.2.84"
    assert s.schedule_import_candidate_pipeline_upgrade(delay=0) is None


def test_manual_import_match_teaches_future_import_autoresolve(monkeypatch, tmp_path: Path):
    s = AutoSourcarrService()
    _source_row("94601", "Future Import Source")
    entry_id, _, _ = s.ingest_manga(
        "94601", _manga(94602, "Canonical Future Import Work", "/source/canonical-future")
    )
    item = {
        "source": 94699,
        "sourceName": "Old Backup Source",
        "url": "/old/renamed-future-work",
        "title": "Old Completely Different Backup Name",
        "favorite": True,
    }
    s._create_import_run("run-autoresolve-teach", "teach.tachibk")
    item_id = s._store_import_item(
        "run-autoresolve-teach", 1, item, state="unresolved", reason="Needs manual review", entry_id=None,
        generate_candidates=False,
    )

    async def local_bind(current_entry_id, current_item, *, manual=False):
        return {"entry_id": int(current_entry_id), "reason": "bound"}

    async def local_add(current_entry_id, backup_title, **kwargs):
        return "added", "Added locally for test"

    monkeypatch.setattr(s, "_bind_backup_source_to_entry", local_bind)
    monkeypatch.setattr(s, "_add_import_entry_to_main_library", local_add)
    resolved = asyncio.run(s.match_import_item("run-autoresolve-teach", item_id, entry_id))
    assert resolved["state"] == "added"

    with db.catalog() as c:
        mapping = c.execute(
            "SELECT * FROM import_manual_autoresolve WHERE backup_key=?",
            (s._backup_rejection_key(item),),
        ).fetchone()
        manual = c.execute(
            "SELECT * FROM manual_match_bindings WHERE source_id=? AND source_manga_id=?",
            ("94601", 94602),
        ).fetchone()
    assert mapping is not None
    assert int(mapping["target_entry_id_hint"]) == entry_id
    assert manual is not None, "manual import autoresolve should share the durable manual-match layer"

    readable = tmp_path / "redo-manual-import.json"
    readable.write_text(json.dumps({"manga": [item]}), encoding="utf-8")

    class FakeMain:
        pass

    async def empty_subbed(force=False):
        return {"ok": True, "entry_ids": []}

    monkeypatch.setattr(service_module, "main_suwayomi", FakeMain())
    monkeypatch.setattr(s, "subbed_entry_ids", empty_subbed)
    s._create_import_run("run-autoresolve-redo", "redo.tachibk")
    result = asyncio.run(s.import_readable_backup(readable, import_id="run-autoresolve-redo"))
    assert result["added"] == 1
    assert result["unresolved"] == 0
    saved = s.import_run_items("run-autoresolve-redo")["items"][0]
    assert saved["state"] == "added"
    assert saved["entry_id"] == entry_id
    assert "remembered manual import match" in saved["reason"]


def test_import_manual_autoresolve_survives_database_reset_and_has_separate_reset():
    s = AutoSourcarrService()
    _source_row("94701", "Persistent Import Manual Source")
    entry_id, _, _ = s.ingest_manga(
        "94701", _manga(94702, "Persistent Import Manual Work", "/persistent/manual-work")
    )
    item = {
        "source": 94799,
        "sourceName": "Historic Backup Source",
        "url": "/historic/persistent-import-work",
        "title": "Historic Persistent Import Name",
    }
    learned = s._remember_import_manual_autoresolve(item, entry_id)
    assert learned["entry_id"] == entry_id

    s.reset_database()
    with db.catalog() as c:
        assert c.execute("SELECT 1 FROM import_manual_autoresolve WHERE backup_key=?", (learned["backup_key"],)).fetchone()
        assert c.execute("SELECT 1 FROM manual_match_bindings WHERE source_id='94701' AND source_manga_id=94702").fetchone()

    restored_id, reason = s._import_manual_autoresolve_entry(item)
    assert restored_id is not None
    assert "remembered manual import match" in reason
    with db.catalog() as c:
        assert c.execute("SELECT 1 FROM entries WHERE id=?", (int(restored_id),)).fetchone()

    reset = s.reset_import_manual_autoresolve()
    assert reset["autoresolve_mappings_removed"] >= 1
    missing_id, _ = s._import_manual_autoresolve_entry(item)
    assert missing_id is None
    with db.catalog() as c:
        assert c.execute("SELECT 1 FROM manual_match_bindings WHERE source_id='94701' AND source_manga_id=94702").fetchone(), \
            "resetting import autoresolve must not reset ordinary manual matches"


def test_pre_v0246_manual_import_history_is_learned_on_reimport():
    s = AutoSourcarrService()
    with db.catalog() as c:
        c.execute("DELETE FROM import_manual_autoresolve_state WHERE key='legacy_history_learning_disabled'")
    _source_row("94801", "Legacy Manual Import Source")
    entry_id, _, _ = s.ingest_manga(
        "94801", _manga(94802, "Legacy Manual Import Work", "/legacy/manual-target")
    )
    item = {
        "source": 94899,
        "sourceName": "Legacy Backup Source",
        "url": "/legacy/backup-work",
        "title": "Legacy Backup Name",
    }
    s._create_import_run("run-legacy-manual-import", "legacy.tachibk")
    item_id = s._store_import_item(
        "run-legacy-manual-import", 1, item, state="unresolved", reason="Needs review", entry_id=None,
        generate_candidates=False,
    )
    # Simulate v0.2.45 after the user clicked one of the displayed candidates: the
    # accepted row retained candidate_ids_json but there was no dedicated autoresolve table.
    with db.catalog() as c:
        c.execute(
            "UPDATE import_items SET state='added',entry_id=?,candidate_ids_json=?,updated_at=? WHERE id=?",
            (entry_id, json.dumps([entry_id]), "2099-01-01T00:00:00Z", item_id),
        )
        c.execute("DELETE FROM import_manual_autoresolve WHERE backup_key=?", (s._backup_rejection_key(item),))

    learned_id, reason = s._import_manual_autoresolve_entry(item)
    assert learned_id == entry_id
    assert "remembered manual import match" in reason
    with db.catalog() as c:
        assert c.execute(
            "SELECT 1 FROM import_manual_autoresolve WHERE backup_key=?",
            (s._backup_rejection_key(item),),
        ).fetchone()

    # The dedicated reset must stay reset even though the legacy audit row remains.
    s.reset_import_manual_autoresolve()
    after_reset, _ = s._import_manual_autoresolve_entry(item)
    assert after_reset is None


def test_unresolved_import_discovery_retains_row_and_wave_deadlines():
    """Regression: automatic discovery must never have an unbounded wait path."""
    import inspect
    source = inspect.getsource(AutoSourcarrService.schedule_import_translation_enrichment)
    live = inspect.getsource(AutoSourcarrService.import_item_live_suggestions)
    assert "await asyncio.wait_for(row_runtime, timeout=180.0)" in source
    assert "row_runtime = asyncio.create_task(process_row(row))" in source
    assert "deferred/errors" in source
    # Admission/execution deadlines are behavioral tests in test_v037_import_capacity.
    assert "overall_timeout=(min(30.0" in live
