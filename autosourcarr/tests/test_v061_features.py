import inspect
import os
import tempfile
from datetime import datetime, timedelta, timezone

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ["DATA_DIR"] = _TEST_DATA.name
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")

from app.config import settings
from app.service import AutoSourcarrService
from app.task_registry import TaskRegistry


def test_task_registry_exposes_elapsed_and_eta_for_running_work():
    registry = TaskRegistry()
    task_id = registry.create("Long import scan", total=10)
    # Make the estimate deterministic without sleeping.
    registry._tasks[task_id].started_at = (datetime.now(timezone.utc) - timedelta(seconds=10)).isoformat()
    registry.update(task_id, current=2, total=10)
    row = registry.snapshot()["items"][0]
    assert 9 <= row["elapsed_seconds"] <= 11
    assert row["rate_per_second"] is not None
    assert 35 <= row["eta_seconds"] <= 45


def test_mark_active_resets_queued_time_for_eta():
    registry = TaskRegistry()
    task_id = registry.create("Queued work", total=20)
    registry._tasks[task_id].started_at = (datetime.now(timezone.utc) - timedelta(minutes=20)).isoformat()
    registry.mark_active(task_id, reset_estimate=True, message="Started")
    row = registry.snapshot()["items"][0]
    assert row["elapsed_seconds"] <= 1
    assert row["eta_seconds"] is None
    assert row["message"] == "Started"


def test_manual_import_resolves_have_parallel_dedicated_lane():
    service = AutoSourcarrService()
    assert service._import_resolve_capacity >= 1
    assert service._import_resolve_capacity <= max(1, int(settings.import_resolve_concurrency))
    src = inspect.getsource(AutoSourcarrService.schedule_import_item_match)
    assert "self._import_resolve_semaphore" in src
    assert "self._background_task_slot(source=True)" not in src
    assert '"Resolve imported entry", total=3' in src
    assert "task_registry.mark_active" in src


def test_manual_import_resolve_reports_three_progress_phases():
    src = inspect.getsource(AutoSourcarrService.match_import_item)
    assert 'message="Binding original source and metadata"' in src
    assert 'message="Adding entry to main Suwayomi library"' in src
    assert 'message="Saving resolved import state"' in src
    assert "current=3, total=3" in src


def test_duplicate_automation_yields_to_manual_import_resolves():
    schedule = inspect.getsource(AutoSourcarrService.schedule_automatic_duplicate_matching)
    consolidate = inspect.getsource(AutoSourcarrService._consolidate_entries_locked)
    assert "_import_resolve_pending" in schedule
    assert "Yielded to manual import resolves" in schedule
    assert "Yielding to" in consolidate and "manual import resolve" in consolidate
    assert "_automatic_duplicate_rescan_requested = True" in consolidate


def test_import_upgrade_recovers_orphaned_resolve_and_retry_rows():
    src = inspect.getsource(AutoSourcarrService.schedule_import_candidate_pipeline_upgrade)
    assert 'version = "0.2.84"' in src
    assert "WHERE state='resolving' AND entry_id IS NOT NULL" in src
    assert "WHERE state='retrying'" in src
    assert "schedule_import_item_match" in src
    assert "schedule_import_item_retry" in src


def test_task_ui_contains_eta_and_elapsed_rendering():
    js = open("autosourcarr/app/static/app.js", encoding="utf-8").read()
    assert "function taskDurationText" in js
    assert "function taskTimingText" in js
    assert "ETA ~" in js
    assert "ETA after first item" in js
