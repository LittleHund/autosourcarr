from __future__ import annotations

import ast
from pathlib import Path


def root() -> Path:
    return Path(__file__).resolve().parents[2]


def function_text(path: str, name: str) -> str:
    text = (root() / path).read_text()
    marker = f"    def {name}"
    async_marker = f"    async def {name}"
    start = text.find(marker)
    if start < 0:
        start = text.find(async_marker)
    assert start >= 0, name
    candidates = [x for x in (text.find("\n    def ", start + 5), text.find("\n    async def ", start + 5)) if x >= 0]
    end = min(candidates) if candidates else len(text)
    return text[start:end]


def test_v073_changelog_history_is_retained():
    changelog = (root() / "CHANGELOG.md").read_text()
    assert "## 0.2.73 - 2026-08-29" in changelog
    assert "0.2.73 → 1.4.2073" in changelog


def test_duplicate_pause_is_real_and_redo_owns_it():
    service = (root() / "autosourcarr/app/service.py").read_text()
    begin = function_text("autosourcarr/app/service.py", "begin_automatic_duplicate_pause")
    assert "self.begin_automatic_duplicate_pause" not in begin
    assert "_automatic_duplicate_pause_event.set()" in begin
    redo = function_text("autosourcarr/app/service.py", "schedule_entry_automation_redo")
    assert 'pause_automatic_duplicate_matching(f"redo-entry:{canonical}")' in redo
    scheduler = function_text("autosourcarr/app/service.py", "schedule_automatic_duplicate_matching")
    assert "_automatic_duplicate_pause_event.is_set()" in scheduler
    consolidate = function_text("autosourcarr/app/service.py", "_consolidate_entries_locked")
    assert "_automatic_duplicate_pause_event.is_set()" in consolidate
    snapshot = function_text("autosourcarr/app/service.py", "schedule_duplicate_snapshot_refresh")
    assert "_automatic_duplicate_pause_event.is_set()" in snapshot
    assert '"paused": bool(pause_reasons)' in service


def test_duplicate_ui_disables_manual_sweep_while_rebuild_paused():
    js = (root() / "autosourcarr/app/static/app.js").read_text()
    assert "autoButton.disabled=!!automatic.paused" in js
    assert "Paused by rebuild" in js
    assert "automatic_matching?.paused" in js


def test_reset_uses_old_source_ids_only_as_revalidated_hints():
    main = (root() / "autosourcarr/app/main.py").read_text()
    service = (root() / "autosourcarr/app/service.py").read_text()
    assert "capture_reset_rebuild_hints" in main
    assert "reset_rebuild_hints_for_entries" in main
    assert "schedule_reset_hint_replay" in main
    revalidate = function_text("autosourcarr/app/service.py", "_revalidate_reset_hints_for_entry")
    assert "source_manga_details" in revalidate
    assert "_strict_automatic_title_evidence" in revalidate
    assert "_trusted_automatic_entry_evidence" in revalidate
    assert 'evidence.get("eligible")' in revalidate
    replay = function_text("autosourcarr/app/service.py", "schedule_reset_hint_replay")
    assert "bounded_map(" in replay
    assert "fetch_limit = max(1, min(4" in replay
    assert "source_manga_details" in replay
    assert "self.ingest_manga" in replay
    rebrowse = function_text("autosourcarr/app/service.py", "rebrowse_subbed_reset_seeds")
    assert rebrowse.index("_revalidate_reset_hints_for_entry") < rebrowse.index("discover_missing_source_matches")


def test_continuous_catalog_crawler_is_low_priority_and_backpressure_aware():
    crawler = function_text("autosourcarr/app/service.py", "schedule_continuous_catalog_discovery")
    assert "_subbed_rebuild_active" in crawler
    assert "_reset_hint_replay_active" in crawler
    assert "_import_discovery_pending" in crawler
    assert "continuous_catalog_backlog_limit" in crawler
    assert "_wait_for_background_idle" in crawler
    assert "background_source_work" in crawler
    assert 'run_blocking(db.setting, "continuous_catalog_cursor"' in crawler
    assert 'run_blocking(db.set_setting, "continuous_catalog_cursor"' in crawler
    assert "newly_catalogued" in crawler
    assert "if newly_catalogued:" in crawler
    assert "loop.call_later(5.0, self.schedule_continuous_catalog_discovery)" in crawler
    main = (root() / "autosourcarr/app/main.py").read_text()
    assert "service.schedule_continuous_catalog_discovery()" in main


def test_database_move_is_copy_verify_final_sync_and_live_cutover():
    db = (root() / "autosourcarr/app/db.py").read_text()
    stage = function_text("autosourcarr/app/db.py", "stage_database_relocation")
    activate = function_text("autosourcarr/app/db.py", "activate_database_relocation")
    assert "src_conn.backup(dst_conn" in db
    assert "PRAGMA quick_check" in db
    assert "database-relocation.json" in stage
    assert "_database_relocating = True" in activate
    assert "_active_db_contexts > 0" in activate
    assert "self._backup_database_file(source, target)" in activate
    assert "settings.database_dir = target_dir" in activate
    assert 'marker["final_sync_at"] = now_iso()' in activate
    assert "current_path != wanted_path" in db


def test_database_move_persists_env_and_fails_closed_on_missing_mount():
    main = (root() / "autosourcarr/app/main.py").read_text()
    config = (root() / "autosourcarr/app/config.py").read_text()
    js = (root() / "autosourcarr/app/static/app.js").read_text()
    compose = (root() / "docker-compose.truenas.yml").read_text()
    assert 'upsert_env_value, env_path, "DATABASE_DIR"' in main
    assert "db.activate_database_relocation" in main
    assert "will not initialize an empty replacement database" in config
    assert 'missing = [name for name in ("catalog.sqlite", "chapter-origins.sqlite")' in config
    assert "Move databases" in (root() / "autosourcarr/app/static/index.html").read_text()
    assert "copies, verifies and switches both DBs live" in js
    assert "/mnt/OTHERPOOL/apps/autosourcarr/database:/database" in compose


def test_python_async_routes_still_parse_after_v073_changes():
    # A cheap AST guard catches accidental indentation/route corruption in the large
    # main/service files in addition to py_compile in the project checker.
    ast.parse((root() / "autosourcarr/app/main.py").read_text())
    ast.parse((root() / "autosourcarr/app/service.py").read_text())
    ast.parse((root() / "autosourcarr/app/db.py").read_text())
