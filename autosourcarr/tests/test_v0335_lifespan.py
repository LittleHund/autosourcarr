"""Failures before HTTP readiness must still close owned startup resources."""
import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock

import pytest

from app import main
from app import internal_services as internal_module


@pytest.mark.asyncio
@pytest.mark.parametrize('failure', ['configure', 'relocation', 'cancelled'])
async def test_pre_yield_startup_failure_runs_existing_teardown(monkeypatch, failure):
    internal = SimpleNamespace(configure=Mock(), start=Mock(), stop=AsyncMock())
    database = SimpleNamespace(finalize_database_relocation=Mock(), close_current_thread=Mock())
    if failure in {'configure', 'cancelled'}:
        internal.configure.side_effect = asyncio.CancelledError() if failure == 'cancelled' else RuntimeError('startup fixture')
    else:
        database.finalize_database_relocation.side_effect = RuntimeError('startup fixture')
    service = SimpleNamespace(resume_background_tasks=Mock(), cancel_background_tasks=AsyncMock())
    monkeypatch.setattr(internal_module, 'internal_services', internal)
    monkeypatch.setattr(main, 'db', database)
    monkeypatch.setattr(main, 'service', service)
    resources = {}
    for name in ('source_maintenance', 'source_orphans', 'source_browser', 'source_engine'):
        resources[name] = SimpleNamespace(stop=AsyncMock())
        monkeypatch.setattr(main, name, resources[name])
    for name in ('cover_cache', 'source_host', 'translator', 'metadata_catalog',
                 'read_lane', 'cover_read_lane', 'chapter_stats_lane', 'tag_suggestion_lane'):
        resources[name] = SimpleNamespace(close=AsyncMock())
        monkeypatch.setattr(main, name, resources[name])
    monkeypatch.setattr(main, 'main_suwayomi', None)
    expected = asyncio.CancelledError if failure == 'cancelled' else RuntimeError
    with pytest.raises(expected):
        async with main.lifespan(main.app):
            pytest.fail('Failed startup must not yield HTTP readiness')
    internal.stop.assert_awaited_once()
    service.cancel_background_tasks.assert_awaited_once()
    database.close_current_thread.assert_called_once()
    assert service._background_stopping is True
    for resource in resources.values():
        getattr(resource, 'stop', getattr(resource, 'close', None)).assert_awaited_once()


@pytest.mark.asyncio
@pytest.mark.parametrize('failure', [None, 'recovery', 'body'])
async def test_lifespan_drains_registered_work_on_late_failure_and_normal_shutdown(monkeypatch, failure):
    tasks = []
    def track(_name, coroutine):
        task = asyncio.create_task(coroutine)
        tasks.append(task)
        return task
    async def drain():
        for task in tasks:
            if not task.done():
                task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
    service = SimpleNamespace(resume_background_tasks=Mock(), cancel_background_tasks=AsyncMock(side_effect=drain),
                              track_startup_task=track, sync_sources=AsyncMock(return_value=[]),
                              pending_entry_origin_count=Mock(return_value=0), reconcile_gallery_chapters=AsyncMock())
    for name in ('schedule_startup_dedup', 'schedule_startup_translation_enrichment',
                 'schedule_startup_additional_record_discovery', 'schedule_startup_refresh',
                 'schedule_continuous_catalog_discovery', 'schedule_import_candidate_pipeline_upgrade',
                 'schedule_import_discovery_resume', 'schedule_status_aux_refresh', 'schedule_duplicate_snapshot_refresh'):
        setattr(service, name, Mock(return_value=False))
    internal = SimpleNamespace(configure=Mock(), start=Mock(), stop=AsyncMock())
    database = SimpleNamespace(finalize_database_relocation=Mock(), close_current_thread=Mock())
    monkeypatch.setattr(internal_module, 'internal_services', internal)
    monkeypatch.setattr(main, 'db', database)
    monkeypatch.setattr(main, 'service', service)
    monkeypatch.setattr(main.tag_store, 'queue_reindex', Mock())
    monkeypatch.setattr(main.settings, 'metadata_enrichment_on_startup', False)
    resources = {}
    for name in ('source_maintenance', 'source_orphans', 'source_browser', 'source_engine'):
        resources[name] = SimpleNamespace(stop=AsyncMock(), start=Mock(), recover=Mock())
        monkeypatch.setattr(main, name, resources[name])
    if failure == 'recovery':
        resources['source_maintenance'].recover.side_effect = RuntimeError('recovery fixture')
    for name in ('cover_cache', 'source_host', 'translator', 'metadata_catalog',
                 'read_lane', 'cover_read_lane', 'chapter_stats_lane', 'tag_suggestion_lane'):
        resources[name] = SimpleNamespace(close=AsyncMock())
        monkeypatch.setattr(main, name, resources[name])
    monkeypatch.setattr(main, 'main_suwayomi', None)
    monkeypatch.setattr(main, 'source_attention', SimpleNamespace(start=Mock()))
    async def exercise():
        async with main.lifespan(main.app):
            assert failure != 'recovery'
            if failure == 'body':
                raise RuntimeError('body fixture')
    if failure is None:
        await exercise()
    else:
        with pytest.raises(RuntimeError, match=f'{failure} fixture'):
            await exercise()
    assert len(tasks) == 5 and all(task.done() for task in tasks)
    internal.stop.assert_awaited_once()
    service.cancel_background_tasks.assert_awaited_once()
    database.close_current_thread.assert_called_once()
    for resource in resources.values():
        getattr(resource, 'stop', getattr(resource, 'close', None)).assert_awaited_once()
