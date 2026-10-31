"""Import retries keep discovering candidates when one upstream source is blocked.

All provider/source-host/Main-Suwayomi traffic is mocked. Each test uses its own
SQLite databases; it does not contact MangaFire or attempt to solve a CAPTCHA.
"""
import asyncio
import json
import os
import subprocess
import tempfile
import time
from pathlib import Path

import pytest

_DATA = tempfile.TemporaryDirectory()
os.environ.setdefault("DATA_DIR", _DATA.name)

from app import service as service_module
from app.config import settings
from app.db import Database
from app.error_registry import error_registry
from app.service import AutoSourcarrService
from app.source_errors import compact_import_reason, source_error_kind, source_error_summary

CAPTCHA = (
    "Exception while fetching data (/fetchSourceManga) : Shape-selecting captcha detected. "
    "Open in WebView to solve manually or turn on the setting to solve automatically. "
    "java.io.IOException: Shape-selecting captcha detected. "
    + "at eu.kanade.tachiyomi.extension.all.mangafire.h.intercept(Unknown Source) " * 60
)
TITLE = "Jingshenbing Mianqian Gui Dongxi Suan Ge Qiu"
ALIAS = "Ghosts Before the Asylum Patient"


def manga(mid, title=TITLE, **kw):
    return {
        "id": mid, "title": title, "url": f"/manga/{mid}",
        "thumbnailUrl": "", "description": "", "author": "", "artist": "",
        "genre": [], "status": 1, "initialized": True, "realUrl": "", **kw,
    }


def backup(**kw):
    return {"title": TITLE, "source": "mf", "sourceName": "MangaFire", "url": "/manga/old-title",
            "_autosourcarr_metadata_identity": {
                "checked": True, "titles": [TITLE, ALIAS], "descriptions": [],
                "identities": [], "records": [],
                "matches": [{"provider": "anilist", "external_id": "84", "confidence": 1.0}],
            }, **kw}


def source(s, db, sid, name, priority=10, **kw):
    with db.catalog() as c:
        c.execute("""INSERT INTO source_config(source_id,name,display_name,lang,base_url,
                     enabled_language,enabled_search,enabled_chapters,priority,last_seen,available)
                     VALUES(?,?,?,?,?,1,?,?,?,'now',1)""",
                  (sid, name, name, "en", f"https://{sid}.invalid", kw.get("search", 1), 1, priority))
    s._reload_source_cache()


def store(s, item=None, *, reason="Original source lookup failed"):
    s._create_import_run("v84-run", "test.tachibk")
    return s._store_import_item("v84-run", 1, item or backup(), state="unresolved", reason=reason,
                                entry_id=None, translation_checked=True, suggestion_checked=True,
                                generate_candidates=False)


@pytest.fixture
def env(monkeypatch, tmp_path):
    monkeypatch.setattr(settings, "data_dir", tmp_path)
    monkeypatch.setattr(settings, "database_dir", "")
    monkeypatch.setattr(settings, "exact_thumbnail_matching", False)
    monkeypatch.setattr(settings, "metadata_identity_on_ingest", False)
    monkeypatch.setattr(settings, "automatic_duplicate_matching", False)
    monkeypatch.setattr(settings, "source_failure_threshold", 3)
    monkeypatch.setattr(settings, "source_cooldown_seconds", 120.0)
    database = Database()
    monkeypatch.setattr(service_module, "db", database)
    s = AutoSourcarrService()
    for name in ("schedule_source_metadata_hydration", "schedule_entry_identity",
                 "schedule_duplicate_snapshot_refresh", "schedule_automatic_duplicate_matching"):
        monkeypatch.setattr(s, name, lambda *a, **kw: False)

    async def empty_metadata(*a, **kw):
        return []

    async def empty_details(*a, **kw):
        return {"manga": {}}

    async def no_identity(*a, **kw):
        return {}

    monkeypatch.setattr(s, "_metadata_provider_search", empty_metadata)
    monkeypatch.setattr(s, "resolve_entry_identity", no_identity)
    monkeypatch.setattr(service_module.source_host, "fetch_manga_and_chapters", empty_details)
    error_registry.clear(resolved_only=False)
    yield s, database


def test_captcha_immediately_opens_circuit_and_success_clears_it(env):
    s, _ = env
    s._record_source_result("mf", RuntimeError(CAPTCHA))
    assert s._source_in_cooldown("mf")
    assert s._source_failures["mf"][1] - time.monotonic() > 290
    assert "CAPTCHA" in s._source_failures["mf"][2]
    s._record_source_result("mf", None)
    assert not s._source_in_cooldown("mf")


def test_regular_failure_count_survives_health_checks(env):
    s, _ = env
    for i in range(1, 4):
        s._record_source_result("slow", RuntimeError("temporary network error"))
        assert s._source_in_cooldown("slow") is (i == 3)
        assert s._source_failures["slow"][0] == i
    failures, _, text = s._source_failures["slow"]
    s._source_failures["slow"] = (failures, time.monotonic() - 1, text)
    assert not s._source_in_cooldown("slow")


@pytest.mark.parametrize("error,kind", [(RuntimeError(CAPTCHA), "captcha"),
    (TimeoutError(), "timeout"), (RuntimeError("HTTP 429 Too Many Requests"), "rate_limit"),
    (RuntimeError("connection refused"), "source_error")])
def test_source_error_classification_is_concise(error, kind):
    assert source_error_kind(error) == kind
    summary = source_error_summary(error, "MangaFire")
    assert len(summary) < 340
    assert "Unknown Source" not in summary


def test_original_source_captcha_is_diagnostic_not_search_miss(env, monkeypatch):
    s, database = env
    source(s, database, "mf", "MangaFire")
    item = backup()
    calls = []

    async def fail(*args):
        calls.append(args)
        raise RuntimeError(CAPTCHA)

    monkeypatch.setattr(service_module.source_host, "fetch_source_manga", fail)
    result = asyncio.run(s._hydrate_backup_source_reference(item))
    second = asyncio.run(s._hydrate_backup_source_reference(item))
    assert len(calls) == 1
    assert result["entry_id"] is None and result["deferred"]
    assert len(result["reason"]) < 180
    assert "not a 'no results'" in result["reason"]
    assert second["source_error"]["cooldown"]
    assert item["_autosourcarr_source_diagnostics"]["mf"]["detail"] == CAPTCHA
    assert error_registry.snapshot()["active_warning_count"] == 1
    with database.catalog() as c:
        assert c.execute("SELECT COUNT(*) FROM source_matches").fetchone()[0] == 0


def test_exact_local_source_url_works_even_during_captcha_cooldown(env, monkeypatch):
    s, database = env
    source(s, database, "mf", "MangaFire")
    payload = manga(84, url="/manga/old-title")
    eid = s._create_entry(payload, "mf")
    s._upsert_match(eid, "mf", payload, 1.0)
    s._record_source_result("mf", RuntimeError(CAPTCHA))
    result = asyncio.run(s._hydrate_backup_source_reference(backup()))
    assert result["entry_id"] == eid
    assert result["exact"]


def test_deferred_bind_still_applies_metadata_without_fake_source_record(env, monkeypatch):
    s, database = env
    source(s, database, "mf", "MangaFire")
    eid = s._create_entry(manga(84), "good")
    applied = []
    monkeypatch.setattr(s, "_apply_import_metadata_to_entry", lambda entry, item: applied.append(entry))
    s._record_source_result("mf", RuntimeError(CAPTCHA))
    result = asyncio.run(s._bind_backup_source_to_entry(eid, backup()))
    assert applied == [eid]
    assert result["entry_id"] == eid and result["deferred"]
    assert result["bound"] is False and result["searched"] is False
    with database.catalog() as c:
        assert c.execute("SELECT COUNT(*) FROM source_matches WHERE source_id='mf'").fetchone()[0] == 0


def test_retry_discovers_metadata_alias_on_another_source_and_adds_to_main(env, monkeypatch):
    s, database = env
    source(s, database, "mf", "MangaFire", 1)
    source(s, database, "good", "Working Source", 2)
    item_id = store(s)
    calls, library_calls = [], []

    async def fetch(sid, mode, page, query):
        calls.append((sid, query))
        if sid == "mf":
            raise RuntimeError(CAPTCHA)
        return {"mangas": [manga(8401, ALIAS)] if query == ALIAS else []}

    async def add(eid, title, **kwargs):
        library_calls.append((eid, title))
        return "added", "Added to main Suwayomi library"

    monkeypatch.setattr(service_module.source_host, "fetch_source_manga", fetch)
    monkeypatch.setattr(s, "_add_import_entry_to_main_library", add)
    result = asyncio.run(s.retry_import_item("v84-run", item_id))
    assert result["state"] == "added", result
    assert [x for x in calls if x[0] == "mf"] == [("mf", TITLE)]
    assert ("good", ALIAS) in calls
    assert library_calls == [(result["entry_id"], TITLE)]
    assert "CAPTCHA" in result["reason"]
    with database.catalog() as c:
        assert c.execute("SELECT COUNT(*) FROM source_matches WHERE source_id='mf'").fetchone()[0] == 0
        assert c.execute("SELECT source_title FROM source_matches WHERE source_id='good'").fetchone()[0] == ALIAS
        raw = json.loads(c.execute("SELECT raw_json FROM import_items WHERE id=?", (item_id,)).fetchone()[0])
        assert raw["title"] == TITLE
        assert raw["_autosourcarr_metadata_identity"]["matches"][0]["confidence"] == 1.0
        assert raw["_autosourcarr_source_diagnostics"]["mf"]["kind"] == "captcha"


def test_retry_uses_all_enabled_sources_when_original_is_missing(env, monkeypatch):
    s, database = env
    for i in range(10):
        source(s, database, f"live{i}", f"Live {i}", i + 1)
    source(s, database, "disabled", "Disabled", 0, search=0)
    item_id = store(s)
    calls = []

    async def fetch(sid, mode, page, query):
        calls.append(sid)
        return {"mangas": [manga(8410, TITLE)] if sid == "live9" else []}

    async def add(*a, **kwargs):
        return "added", "Added to main Suwayomi library"

    monkeypatch.setattr(service_module.source_host, "fetch_source_manga", fetch)
    monkeypatch.setattr(s, "_add_import_entry_to_main_library", add)
    result = asyncio.run(s.retry_import_item("v84-run", item_id))
    assert result["state"] == "added"
    assert "live9" in calls and "disabled" not in calls and "mf" not in calls


def test_existing_catalog_match_does_not_require_blocked_original_source(env, monkeypatch):
    s, database = env
    source(s, database, "mf", "MangaFire")
    source(s, database, "good", "Working Source")
    payload = manga(8402, TITLE)
    eid = s._create_entry(payload, "good")
    s._upsert_match(eid, "good", payload, 1.0)
    item_id = store(s)
    s._record_source_result("mf", RuntimeError(CAPTCHA))
    calls = []

    async def add(*args, **kwargs):
        calls.append(args)
        return "existing", "Already in main Suwayomi library"

    monkeypatch.setattr(s, "_add_import_entry_to_main_library", add)
    result = asyncio.run(s.retry_import_item("v84-run", item_id))
    assert result["state"] == "existing" and result["entry_id"] == eid
    assert calls == [(eid, TITLE)]


def test_no_working_sources_remains_unresolved_with_metadata_retained(env, monkeypatch):
    s, database = env
    source(s, database, "mf", "MangaFire")
    item_id = store(s)
    calls = []

    async def fail(*args):
        calls.append(args)
        raise RuntimeError(CAPTCHA)

    monkeypatch.setattr(service_module.source_host, "fetch_source_manga", fail)
    result = asyncio.run(s.retry_import_item("v84-run", item_id))
    assert result["state"] == "unresolved"
    assert "Metadata identified" in result["reason"] and "CAPTCHA" in result["reason"]
    assert len(result["reason"]) < 400 and "Unknown Source" not in result["reason"]
    assert len(calls) == 1
    with database.catalog() as c:
        assert c.execute("SELECT COUNT(*) FROM entries").fetchone()[0] == 0
        row = c.execute("SELECT * FROM import_items WHERE id=?", (item_id,)).fetchone()
        assert row["suggestion_checked"] == 1
        assert json.loads(row["raw_json"])["_autosourcarr_metadata_identity"]["matches"]


def test_partial_live_candidates_survive_timeout_and_failed_rerank(env, monkeypatch):
    s, database = env
    item_id = store(s)
    eid = s._create_entry(manga(8403, "An Unrelated Review Candidate"), "good")

    async def live(*a, **kw):
        with database.catalog() as c:
            c.execute("UPDATE import_items SET candidate_ids_json=? WHERE id=?", (json.dumps([eid]), item_id))
        raise asyncio.TimeoutError()

    monkeypatch.setattr(s, "import_item_live_suggestions", live)
    monkeypatch.setattr(s, "_backup_candidate_ids", lambda *a, **kw: [])
    result = asyncio.run(s.retry_import_item("v84-run", item_id))
    assert result["state"] == "unresolved"
    assert "time limit" in result["reason"]
    with database.catalog() as c:
        row = c.execute("SELECT candidate_ids_json FROM import_items WHERE id=?", (item_id,)).fetchone()
        assert json.loads(row[0]) == [eid]


def test_high_review_scores_do_not_override_strict_gate(env, monkeypatch):
    s, database = env
    item_id = store(s)
    ids = [s._create_entry(manga(8404 + i, f"Different Work Number {i}"), "good") for i in range(2)]

    async def live(*a, **kw):
        return {"suggestions": [{"id": eid, "import_score": 1.0} for eid in ids]}

    monkeypatch.setattr(s, "import_item_live_suggestions", live)
    result = asyncio.run(s.retry_import_item("v84-run", item_id))
    assert result["state"] == "unresolved"
    assert result["entry_id"] is None


def test_skipped_wave_request_does_not_clear_captcha_circuit(env, monkeypatch):
    s, database = env
    source(s, database, "healthy", "Healthy", 1)
    source(s, database, "blocked", "Blocked", 2)
    item_id = store(s)
    monkeypatch.setattr(settings, "max_source_concurrency", 1)
    calls = []

    async def fetch(sid, *args):
        calls.append(sid)
        if sid == "healthy":
            s._record_source_result("blocked", RuntimeError(CAPTCHA))
        return {"mangas": []}

    monkeypatch.setattr(service_module.source_host, "fetch_source_manga", fetch)
    result = asyncio.run(s.import_item_live_suggestions("v84-run", item_id, automatic=True))
    assert "blocked" not in calls
    assert "Blocked" not in result["live_sources"]
    assert s._source_in_cooldown("blocked")
    assert any(d["source_id"] == "blocked" for d in result["source_errors"])


def test_legacy_stack_is_collapsed_in_single_and_bulk_item_response(env):
    s, _ = env
    item_id = store(s, reason=CAPTCHA)
    one = s.import_run_item("v84-run", item_id)
    many = s.import_run_items("v84-run")["items"][0]
    # Per-row endpoint returns the item envelope used by the browser.
    if "item" in one:
        one = one["item"]
    for row in (one, many):
        assert len(row["reason"]) < 180
        assert row["reason_detail"] == CAPTCHA


def test_retry_cancellation_propagates(env, monkeypatch):
    s, _ = env
    item_id = store(s)

    async def cancelled(*a, **kw):
        raise asyncio.CancelledError()

    monkeypatch.setattr(s, "import_item_live_suggestions", cancelled)
    with pytest.raises(asyncio.CancelledError):
        asyncio.run(s.retry_import_item("v84-run", item_id))


def test_source_diagnostic_html_is_escaped_and_collapsed():
    root = Path(__file__).resolve().parents[2]
    js = (root / "autosourcarr/app/static/app.js").read_text()
    function = js[js.index("function importReasonHtml("):js.index("function importItemHtml(")]
    script = '''const esc = s => String(s).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');\n'''
    script += function + '\nconsole.log(importReasonHtml(' + json.dumps({
        "reason": "CAPTCHA blocked", "source_diagnostics": [{"kind": "captcha", "source_name": "MangaFire",
        "detail": "<script>alert(1)</script>", "message": "CAPTCHA blocked"}]
    }) + '));'
    output = subprocess.check_output(["node", "-e", script], text=True)
    assert '<details class="importDiagnostics">' in output
    assert '<details open' not in output and '<script>' not in output
    assert '&lt;script&gt;' in output
    assert "Sources → WebView" in output


def test_version_assets_and_upgrade_marker():
    root = Path(__file__).resolve().parents[2]
    version = (root / "VERSION").read_text().strip()
    assert version
    assert (root / "autosourcarr/VERSION").read_text().strip() == version
    html = (root / "autosourcarr/app/static/index.html").read_text()
    assert f'/static/app.js?v={version}' in html and f'/static/style.css?v={version}' in html
    import inspect
    assert 'version = "0.2.84"' in inspect.getsource(AutoSourcarrService.schedule_import_candidate_pipeline_upgrade)


def test_rejected_live_shortlist_cannot_be_accepted_or_restored(env, monkeypatch):
    s, database = env
    source(s, database, "good", "Working Source")
    payload = manga(8450, TITLE)
    eid = s._create_entry(payload, "good")
    s._upsert_match(eid, "good", payload, 1.0)
    item = backup()
    s._remember_import_rejection(item, eid)
    assert s._is_import_entry_rejected(item, eid)
    item_id = store(s, item)

    async def live(*a, **kw):
        return {"suggestions": [{"id": eid, "import_score": 1.0}]}

    monkeypatch.setattr(s, "import_item_live_suggestions", live)
    result = asyncio.run(s.retry_import_item("v84-run", item_id))
    assert result["state"] == "unresolved"
    assert not s._import_candidate_evidence(item, eid)["eligible"]
    with database.catalog() as c:
        ids = json.loads(c.execute("SELECT candidate_ids_json FROM import_items WHERE id=?", (item_id,)).fetchone()[0])
    assert eid not in ids


def test_shared_metadata_identity_does_not_override_color_edition(env, monkeypatch):
    s, _ = env
    eid = s._create_entry(manga(8451, "A Bright Moon"), "good")
    monkeypatch.setattr(s, "_entry_color_profile", lambda entry: {"color_mode": "monochrome"})
    monkeypatch.setattr(s, "_import_entry_identity_overlap", lambda item, entry: {("anilist", "84")})
    item = backup(title="A Bright Moon (Colored)")
    item["_autosourcarr_metadata_identity"]["titles"] = ["A Bright Moon"]
    evidence = s._import_candidate_evidence(item, eid)
    assert not evidence["eligible"]
    assert evidence["reason"] == "colored/monochrome edition conflict"


def test_upgrade_queues_existing_imports_once_without_deleting_evidence(env, monkeypatch):
    s, database = env
    item_id = store(s)
    database.set_setting("import_candidate_pipeline_version", "0.2.83")
    queued = []
    monkeypatch.setattr(s, "schedule_import_translation_enrichment", lambda run: queued.append(run) or True)

    async def run():
        task_id = s.schedule_import_candidate_pipeline_upgrade(delay=0)
        assert task_id
        await s._refresh_tasks[f"import-candidate-upgrade:{task_id}"]
        assert s.schedule_import_candidate_pipeline_upgrade(delay=0) is None

    asyncio.run(run())
    assert queued == ["v84-run"]
    assert database.setting("import_candidate_pipeline_version", "") == "0.2.84"
    with database.catalog() as c:
        row = c.execute("SELECT * FROM import_items WHERE id=?", (item_id,)).fetchone()
        assert row["suggestion_checked"] == 0 and row["suggestion_state"] == "queued"
        assert json.loads(row["raw_json"])["_autosourcarr_metadata_identity"]["matches"]


def test_deferred_binding_preserves_full_accepted_metadata_on_target(env):
    s, database = env
    source(s, database, "mf", "MangaFire")
    source(s, database, "good", "Working Source")
    payload = manga(8452, ALIAS)
    eid = s._create_entry(payload, "good")
    s._upsert_match(eid, "good", payload, 1.0)
    candidate = {
        "provider": "anilist", "provider_name": "AniList", "identity_scope": "work",
        "external_id": "84", "url": "https://anilist.invalid/84", "title": TITLE,
        "titles": [{"title": TITLE, "language": "en", "type": "primary"},
                   {"title": ALIAS, "language": "en", "type": "alias"}],
        "description": "An accepted metadata synopsis long enough to hydrate a title-only entry without its original source.",
        "cover_url": "https://images.invalid/84.jpg", "author": "Fixture Author", "artist": "Fixture Artist",
        "genres": ["Fantasy"], "status": "completed", "country": "CN", "media_type": "MANGA", "links": {},
    }
    item = backup()
    item["_autosourcarr_metadata_identity"]["records"] = [
        {"provider": "anilist", "confidence": 1.0, "reason": "accepted fixture", "candidate": candidate}
    ]
    s._record_source_result("mf", RuntimeError(CAPTCHA))
    result = asyncio.run(s._bind_backup_source_to_entry(eid, item))
    assert result["deferred"]
    entry = s.get_entry(eid)
    assert entry["description"] == candidate["description"]
    assert entry["thumbnail_url"] == candidate["cover_url"]
    assert entry["author"] == candidate["author"]
    assert any(x["external_id"] == "84" for x in s._identity_links(eid))
    with database.catalog() as c:
        assert c.execute("SELECT COUNT(*) FROM source_matches WHERE source_id='mf'").fetchone()[0] == 0


def test_completed_wave_retains_diagnostics_even_without_candidates(env, monkeypatch):
    s, database = env
    source(s, database, "mf", "MangaFire")
    for i in range(6):
        source(s, database, f"other{i}", f"Other {i}")
    item_id = store(s)
    searches = []

    async def fetch(sid, *args):
        searches.append(sid)
        if sid == "mf":
            raise RuntimeError(CAPTCHA)
        return {"mangas": []}

    calls = 0

    def rerank(*args):
        nonlocal calls
        calls += 1
        if calls == 3:
            raise TimeoutError("Stop after the next wave")
        return {"suggestions": []}

    monkeypatch.setattr(service_module.source_host, "fetch_source_manga", fetch)
    monkeypatch.setattr(s, "import_item_suggestions", rerank)
    with pytest.raises(TimeoutError):
        asyncio.run(s.import_item_live_suggestions(
            "v84-run", item_id, automatic=True, skip_backup_reference=True))
    with database.catalog() as c:
        row = c.execute("SELECT raw_json,candidate_ids_json FROM import_items WHERE id=?", (item_id,)).fetchone()
    assert json.loads(row["candidate_ids_json"]) == []
    diagnostics = json.loads(row["raw_json"])["_autosourcarr_source_diagnostics"]
    assert diagnostics["mf"]["kind"] == "captcha"
    assert diagnostics["mf"]["detail"] == CAPTCHA
    assert searches.count("mf") == 1
