import inspect
import os
import tempfile

_TEST_DATA = tempfile.TemporaryDirectory()
os.environ["DATA_DIR"] = _TEST_DATA.name
os.environ.setdefault("SOURCE_LANGUAGES", "en,all")

from app.config import settings
from app.db import db
from app.service import AutoSourcarrService


def test_import_discovery_is_serialized_and_coalesced_per_run():
    src = inspect.getsource(AutoSourcarrService.schedule_import_translation_enrichment)
    assert 'import-enrich:' in src
    assert 'self._import_discovery_semaphore' in src
    assert 'Queued behind another import-discovery run' in src
    assert 'self._background_task_runtime(runner(), source=True)' not in src


def test_duplicate_automation_defers_to_import_discovery_without_holding_generic_slot():
    src = inspect.getsource(AutoSourcarrService.schedule_automatic_duplicate_matching)
    assert '_import_discovery_pending' in src
    assert 'Deferred until' in src
    assert 'runtime = loop.create_task(runner())' in src
    assert '_background_task_runtime(runner())' not in src


def test_duplicate_snapshot_does_not_race_automatic_full_scan():
    s = AutoSourcarrService()

    class Running:
        def done(self):
            return False

    s._refresh_tasks['__automatic-duplicate-matching__'] = Running()
    assert s.schedule_duplicate_snapshot_refresh(delay=0) is False
    assert s._duplicate_snapshot_stale is True


def test_idle_wait_happens_before_background_slot_acquisition():
    src = inspect.getsource(AutoSourcarrService._background_task_slot)
    wait_pos = src.index('await self._wait_for_background_idle()')
    slot_pos = src.index('async with self._background_task_semaphore')
    assert wait_pos < slot_pos


def test_duplicate_progress_uses_candidate_pairs_not_entire_catalog_as_fake_zero_progress():
    repair = inspect.getsource(AutoSourcarrService._repair_catalog_duplicates_locked)
    consolidate = inspect.getsource(AutoSourcarrService._consolidate_entries_locked)
    assert 'Building duplicate shortlist across' in repair
    assert 'pair_total = len(pairs)' in consolidate
    assert 'Evaluating {pair_total} duplicate candidate pair' in consolidate
    assert 'Evaluated {min(pair_total, len(evaluated_pairs))}/{pair_total}' in consolidate


def test_v060_requeues_existing_unresolved_imports():
    src = inspect.getsource(AutoSourcarrService.schedule_import_candidate_pipeline_upgrade)
    assert 'version = "0.2.84"' in src
