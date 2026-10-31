"""Retained upload selection with real files, synthetic APKs and no live data."""
import base64
import hashlib
import json
from unittest.mock import AsyncMock

import pytest

from test_v0339_baseline_manifest import PKG, XML, CLASS, CODE, package


@pytest.fixture
def uploads(tmp_path, monkeypatch):
    from app import repair_baseline as module, source_maintenance as maintenance
    from app.config import settings
    monkeypatch.setattr(settings, 'data_dir', tmp_path)
    baseline = package([(CLASS, CODE)])
    monkeypatch.setattr(maintenance, 'save_backup', lambda _: baseline)
    extension = {'pkgName': PKG, 'versionName': '1.4.23', 'source': {'nodes': [
        {'id': '9223372036854775806', 'name': 'Example', 'lang': 'en', 'baseUrl': 'https://example.test'}]}}
    def upload(data, stamp=1, state='installed', pkg=PKG):
        digest = hashlib.sha256(data).hexdigest()
        folder = tmp_path/'source-maintenance/uploads'/digest
        folder.mkdir(parents=True, exist_ok=True)
        (folder/'uploaded.apk').write_bytes(data)
        (folder/'receipt.json').write_text(json.dumps({'state': state, 'pkgName': pkg, 'sha256': digest, 'completed_at': stamp}))
        return folder
    return module, extension, baseline, upload


def test_converted_input_uses_only_the_latest_confirmed_original_upload_and_preserves_raw_backup(uploads):
    module, extension, baseline, upload = uploads
    old = package([('AndroidManifest.xml', XML), ('classes.dex', b'dex\n035\0old')])
    newest = package([('AndroidManifest.xml', XML), ('classes.dex', b'dex\n035\0new')])
    uncertain = package([('AndroidManifest.xml', XML), ('classes.dex', b'dex\n035\0uncertain')])
    upload(old, 1); upload(newest, 2); upload(uncertain, 3, 'uncertain')
    result = module.repair_input(extension)
    assert base64.b64decode(result['baseline']) == baseline
    assert base64.b64decode(result['source_apk']) == newest
    assert result['installed_version'] == extension['versionName']
    assert result['original_apk_basis'] == 'recorded-installed-upload'
    assert result['installed_sources'][0]['id'] == '9223372036854775806'


def test_missing_original_apk_reports_a_web_ui_recovery_action(uploads):
    module, extension, baseline, upload = uploads
    with pytest.raises(ValueError, match='Original APK for repair'):
        module.repair_input(extension)


def test_changed_retained_apk_does_not_fall_back_to_an_older_unrelated_version(uploads):
    module, extension, baseline, upload = uploads
    upload(package([('AndroidManifest.xml', XML), ('classes.dex', b'dex\n035\0old')]), 1)
    folder = upload(package([('AndroidManifest.xml', XML), ('classes.dex', b'dex\n035\0new')]), 2)
    (folder/'uploaded.apk').write_bytes(b'UNEXPECTED_CHANGED_BYTES')
    with pytest.raises(ValueError, match='digest'):
        module.repair_input(extension)


def test_existing_native_jar_needs_no_upload_selection(uploads, monkeypatch):
    from app import source_maintenance as maintenance
    module, extension, baseline, upload = uploads
    native = package([('AndroidManifest.xml', XML), (CLASS, CODE)])
    monkeypatch.setattr(maintenance, 'save_backup', lambda _: native)
    monkeypatch.setattr(module, 'retained_apk', lambda _: pytest.fail('Native JAR must not select an APK'))
    assert module.repair_input(extension) == {'baseline': base64.b64encode(native).decode()}


@pytest.mark.asyncio
async def test_installed_orphan_uses_raw_first_admission_without_manufacturing_archive_provenance(tmp_path, monkeypatch):
    import asyncio
    from app import source_orphans as module
    from app.config import settings
    monkeypatch.setattr(settings, 'data_dir', tmp_path)
    manager = module.SourceOrphans()
    manager.path().parent.mkdir(parents=True)
    manager.path().write_text(json.dumps({'records': {}, 'current': []}))
    manager.load()
    extension = {'pkgName': PKG, 'isInstalled': True}
    monkeypatch.setattr(module, 'inventory', AsyncMock(return_value=[extension]))
    monkeypatch.setattr(manager, 'repair_revision', AsyncMock(return_value=''))
    from app.repair_workflow import repair_workflow
    remote = AsyncMock(return_value={'id': 'a' * 32, 'state': 'queued'})
    monkeypatch.setattr(repair_workflow, 'create', remote)
    await manager.repair(PKG, asyncio.Lock())
    assert remote.call_args.args[0] == extension
    manager.repair_revision.assert_not_awaited()
    assert manager.records == {} and not manager.current
