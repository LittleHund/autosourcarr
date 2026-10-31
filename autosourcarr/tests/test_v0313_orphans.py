"""Persistent local-source labels, real upload receipts, and admin classification."""
import asyncio
import json
from unittest.mock import AsyncMock

from fastapi import FastAPI, HTTPException
import httpx
import pytest

from app import source_controls as controls, source_orphans as module
from app.config import settings
from app.suwayomi import SuwayomiClient
from test_v0311_source_controls import apk

PKG = 'eu.kanade.tachiyomi.extension.en.example'
ACTIVE = 'https://example.org/extensions/index.min.json'
OLD = 'https://retired.example.org/extensions/index.min.json'


def extension(**kwargs):
    return {'pkgName': PKG, 'name': 'Example', 'lang': 'en', 'versionName': '1.4.1',
            'isInstalled': True, 'isObsolete': False, 'hasUpdate': False,
            'storeIndexUrl': ACTIVE, 'apkUrl': ACTIVE + '/example.apk',
            'source': {'nodes': [{'id': '9223372036854775806', 'name': 'Example', 'lang': 'en'}]}, **kwargs}


@pytest.fixture
def manager(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, 'data_dir', tmp_path/'data')
    monkeypatch.setattr(settings, 'settings_config_dir', tmp_path/'config')
    settings.settings_config_dir.mkdir()
    (settings.settings_config_dir/'source-host.env').write_text('EXTENSION_STORES='+json.dumps([ACTIVE])+'\n')
    m = module.SourceOrphans()
    m.path().parent.mkdir(parents=True)
    m.path().write_text(json.dumps({'records': {}, 'current': [], 'refresh': {'state':'idle'}}))
    monkeypatch.setattr(module, 'source_orphans', m)
    monkeypatch.setattr(controls, 'source_orphans', m)
    return m


@pytest.mark.asyncio
@pytest.mark.parametrize('pkg', [PKG, 'org.example.extensions.en.custom'])
async def test_successful_apk_upload_is_persistent_orphan_even_when_repo_still_has_package(manager, pkg):
    calls = []
    row = extension(pkgName=pkg)
    def transport(request):
        calls.append(request)
        if request.url.path == '/api/graphql':
            return httpx.Response(200, json={'data': {'extensions': {'nodes': [] if len(calls)==1 else [row]}}})
        return httpx.Response(200, text=pkg)
    client = SuwayomiClient('http://source.invalid'); await client.close()
    client.client = httpx.AsyncClient(transport=httpx.MockTransport(transport))
    try:
        result = await controls.install_uploaded_apk(client, apk(pkg), 'manual.apk')
        assert result['extension']['is_orphaned'] and result['extension']['orphan_reason']=='manual-upload'
        assert len(calls)==3
        restarted = module.SourceOrphans(); restarted.load(); restarted.current={pkg}
        restarted.accept_inventory([extension(pkgName=pkg, versionName='1.4.2', hasUpdate=True)])
        status = await restarted.snapshot()
        orphan = status['items'][0]
        assert orphan['installed'] and orphan['versionName']=='1.4.2' and orphan['hasUpdate']
        assert orphan['orphan_reason']=='manual-upload' and orphan['can_unmark_orphan']
        assert not orphan['can_install'] and not orphan.get('commit') and not orphan.get('archive_url')
        assert restarted.records=={}  # A manual install is not historical provenance.
    finally:
        await client.close()


@pytest.mark.parametrize('state,expected', [('installed',True),('failed',False),('uncertain',False),('restored',False),('installing',False)])
def test_legacy_upload_receipts_only_migrate_confirmed_success(manager, state, expected):
    receipt = manager.path().parent/'uploads'/'fixture'/'receipt.json'
    receipt.parent.mkdir(parents=True)
    receipt.write_text(json.dumps({'pkgName':PKG,'state':state,'completed_at':123}))
    manager.load()
    result = manager.annotate_extensions([extension()])[0]
    assert result['is_orphaned'] is expected


def test_manual_labels_and_undo_survive_discovery_and_restart(manager):
    row = extension(storeIndexUrl=OLD)
    manager.set_label(row, True)
    manager.merge_found({PKG:{'pkgName':PKG,'name':'History','commit':'a'*40,'archive_url':'https://example.invalid/original.jar'}})
    manager.current={PKG}; manager.save()
    restarted=module.SourceOrphans(); restarted.load()
    assert restarted.annotate_extensions([row])[0]['is_orphaned']
    assert restarted.records[PKG]['commit']=='a'*40
    restarted.set_label(row,False)
    again=module.SourceOrphans(); again.load()
    assert not again.annotate_extensions([row])[0]['is_orphaned']
    assert again.annotate_extensions([row])[0]['can_mark_orphan']
    assert again.records[PKG]['archive_url']=='https://example.invalid/original.jar'


def test_local_native_package_is_orphan_without_remote_repository_lookup(manager):
    row=manager.annotate_extensions([extension(storeIndexUrl=None,apkUrl=None)])[0]
    assert row['is_orphaned'] and row['orphan_reason']=='manual-upload'
    assert row['can_unmark_orphan']


def test_catalog_absence_and_unknown_inventory_are_not_conflated(manager):
    assert not manager.annotate_extensions([extension()])[0]['can_mark_orphan']
    missing=manager.annotate_extensions([extension(storeIndexUrl=OLD)])[0]
    assert missing['can_mark_orphan'] and not missing['is_orphaned']
    (settings.settings_config_dir/'source-host.env').write_text('EXTENSION_STORES=invalid\n')
    assert not manager.annotate_extensions([extension(storeIndexUrl=OLD)])[0]['can_mark_orphan']
    assert not manager.annotate_extensions([{'pkgName':PKG,'isInstalled':True}])[0]['can_mark_orphan']
    (settings.settings_config_dir/'source-host.env').write_text('TZ=Europe/Amsterdam\n')
    assert not manager.annotate_extensions([extension(storeIndexUrl=OLD)])[0]['can_mark_orphan']


@pytest.mark.asyncio
async def test_uninstall_retains_label_without_inventing_reinstall_or_repair(manager):
    manager.set_label(extension(),True,'manual-upload')
    manager.accept_inventory([])
    row=(await manager.snapshot())['items'][0]
    assert not row['installed'] and not row['can_install'] and not row['can_repair']
    assert 'Upload its APK' in row['message'] and row['state']=='unavailable'


@pytest.mark.asyncio
async def test_uninstalled_upload_available_in_repo_keeps_normal_install_action(manager):
    manager.set_label(extension(),True,'manual-upload')
    uninstalled=extension(isInstalled=False)
    manager.accept_inventory([uninstalled])
    available=manager.annotate_extensions([uninstalled])[0]
    assert not available['is_orphaned'] and available['apkUrl']
    assert (await manager.snapshot())['items']==[]
    manager.accept_inventory([extension()])
    assert (await manager.snapshot())['items'][0]['orphan_reason']=='manual-upload'


def make_app(manager, monkeypatch, rows, *, denied=False):
    async def guard():
        if denied: raise HTTPException(403,'Admin required')
    fake=AsyncMock(return_value=rows)
    monkeypatch.setattr(module,'inventory',fake)
    app=FastAPI(); app.include_router(module.orphan_router(guard,asyncio.Lock()))
    return app,fake


@pytest.mark.asyncio
async def test_mark_and_undo_api_use_exact_installed_package_and_persist(manager, monkeypatch):
    app,fake=make_app(manager,monkeypatch,[extension(storeIndexUrl=OLD)])
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://test') as client:
        marked=await client.post('/api/admin/source-orphans/'+PKG+'/mark')
        assert marked.status_code==200 and marked.json()['extension']['is_orphaned']
        snapshot=await client.get('/api/admin/source-orphans')
        assert snapshot.json()['items'][0]['installed']
        undone=await client.post('/api/admin/source-orphans/'+PKG+'/unmark')
        assert undone.status_code==200 and not undone.json()['extension']['is_orphaned']
        assert (await client.get('/api/admin/source-orphans')).json()['items']==[]
    assert fake.await_count==2
    assert module.SourceOrphans().annotate_extensions([extension(storeIndexUrl=OLD)])[0]['is_orphaned'] is False


@pytest.mark.asyncio
@pytest.mark.parametrize('kind,status', [('active',409),('uninstalled',404),('offline',503),('denied',403)])
async def test_invalid_or_uncertain_mark_does_not_persist(manager, monkeypatch, kind, status):
    app,fake=make_app(manager,monkeypatch,[extension(isInstalled=kind!='uninstalled')],denied=kind=='denied')
    if kind=='offline': fake.side_effect=RuntimeError('secret engine response')
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://test') as client:
        response=await client.post('/api/admin/source-orphans/'+PKG+'/mark')
    assert response.status_code==status
    assert 'secret engine response' not in response.text
    assert manager.labels=={}
    if kind=='denied': fake.assert_not_awaited()


def test_failed_label_save_does_not_change_visible_memory(manager, monkeypatch):
    manager.load()
    monkeypatch.setattr(module,'atomic',lambda *a: (_ for _ in ()).throw(OSError('full')))
    with pytest.raises(OSError):manager.set_label(extension(),True)
    assert not manager.annotate_extensions([extension()])[0]['is_orphaned']


@pytest.mark.asyncio
async def test_older_inventory_poll_cannot_erase_new_upload_inventory(manager, monkeypatch):
    started,finish=asyncio.Event(),asyncio.Event()
    async def old(): started.set(); await finish.wait(); return []
    monkeypatch.setattr(module,'inventory',old)
    task=asyncio.create_task(manager.refresh_inventory()); await started.wait()
    manager.set_label(extension(),True,'manual-upload');manager.accept_inventory([extension()])
    finish.set();await task
    assert (await manager.snapshot())['items'][0]['installed']
