"""Bookkeeping failure disables repair features without resetting files."""
import importlib
from pathlib import Path

import pytest


def test_history_initialization_failure_is_explicit_and_preserves_the_bad_journal(tmp_path,monkeypatch):
    module=importlib.import_module('app.repair_workflow');maintenance=importlib.import_module('app.source_maintenance')
    monkeypatch.setattr(maintenance.settings,'data_dir',tmp_path)
    path=tmp_path/'source-maintenance/history'/('a'*64+'.json');path.parent.mkdir(parents=True);path.write_bytes(b'corrupt journal')
    workflow=module.RepairWorkflow();workflow.initialize_safe()
    assert workflow.history is None and workflow.error and path.read_bytes()==b'corrupt journal'


@pytest.mark.asyncio
async def test_raw_storage_failure_remains_visible_without_raising_from_startup(monkeypatch):
    module=importlib.import_module('app.source_raws')
    def unavailable(*args,**kwargs):raise ValueError('Synthetic damaged request receipt')
    monkeypatch.setattr(module,'SourceRawCatalog',unavailable)
    service=module.SourceRawService();await service.start()
    assert service.catalog is None and service.error
    assert service.pending_items()[0]['state']=='failed'
    assert not service.tasks
