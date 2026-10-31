"""Owned acquisition is bounded, coalesced and drained before shutdown."""
import asyncio
import hashlib
import importlib
import threading
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest


@pytest.mark.asyncio
async def test_acquisition_coalesces_and_shutdown_waits_for_an_owned_filesystem_thread(monkeypatch):
    module=importlib.import_module('app.source_raws');registry=importlib.import_module('app.task_registry')
    monkeypatch.setattr(registry,'task_registry',registry.TaskRegistry())
    service=module.SourceRawService();service.semaphore=asyncio.Semaphore(1)
    started=threading.Event();release=threading.Event();finished=[]
    def io():
        started.set();assert release.wait(3);finished.append('flushed')
    async def operation():await module.run_blocking(io)
    service._own(operation,'test.package.source',key=('same',))
    service._own(operation,'test.package.source',key=('same',))
    assert len(service.tasks)==1
    for _ in range(100):
        if started.is_set():break
        await asyncio.sleep(.005)
    assert started.is_set()
    stop=asyncio.create_task(service.stop())
    try:
        await asyncio.sleep(.02);assert not stop.done() and not finished
    finally:release.set()
    await stop
    assert finished==['flushed'] and not service.tasks


@pytest.mark.asyncio
async def test_normal_capture_binds_the_actual_binary_without_a_second_inventory_query(tmp_path,monkeypatch):
    module=importlib.import_module('app.source_raws');maintenance=importlib.import_module('app.source_maintenance')
    workflow=importlib.import_module('app.repair_workflow').repair_workflow
    monkeypatch.setattr(workflow,'history',None)
    native=tmp_path/'source.jar';native.write_bytes(b'actual installed bytes')
    monkeypatch.setattr(maintenance,'installed_path',lambda _:native)
    monkeypatch.setattr(maintenance,'inventory',AsyncMock(side_effect=AssertionError('Existing apkName metadata should avoid a redundant inventory read')))
    calls=[]
    class Catalog:
        def begin(self,extension,**options):calls.append(options);return {'state':'available'}
    service=module.SourceRawService();service.catalog=Catalog();service.semaphore=asyncio.Semaphore(1);service.admin_lock=asyncio.Lock()
    service.schedule({'pkgName':'test.package.source','versionName':'1.4.23','apkName':'source.apk'})
    await asyncio.gather(*service.tasks)
    assert calls[0]['binary_sha256']==hashlib.sha256(native.read_bytes()).hexdigest()
    maintenance.inventory.assert_not_awaited()


@pytest.mark.asyncio
async def test_startup_backfill_only_queues_missing_changed_or_previously_removed_sources(monkeypatch):
    module=importlib.import_module('app.source_raws');maintenance=importlib.import_module('app.source_maintenance')
    engine=importlib.import_module('app.source_engine');orphans=importlib.import_module('app.source_orphans')
    monkeypatch.setattr(engine,'source_engine',SimpleNamespace(state='ready'))
    rows=[{'pkgName':'test.package.kept','versionName':'1.4.23','isInstalled':True},
          {'pkgName':'test.package.missing','versionName':'1.4.23','isInstalled':True},
          {'pkgName':'test.package.notinstalled','versionName':'1.4.23','isInstalled':False}]
    monkeypatch.setattr(maintenance,'inventory',AsyncMock(return_value=rows))
    monkeypatch.setattr(orphans.source_orphans,'annotate_extensions',lambda values:values)
    service=module.SourceRawService();service.catalog=SimpleNamespace(status=lambda pkg:{'version':'1.4.23','state':'available'} if pkg.endswith('.kept') else None)
    queued=[];monkeypatch.setattr(service,'schedule',lambda row,**kwargs:queued.append(row))
    await service._backfill()
    assert [row['pkgName'] for row in queued]==['test.package.missing']
