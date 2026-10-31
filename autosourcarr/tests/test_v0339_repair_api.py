"""Actual request/HTTP boundaries, synthetic packages and no worker/source execution."""
import asyncio
import base64
import importlib.util
import json
from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import AsyncMock

from fastapi import FastAPI
import httpx
import pytest

from test_v0339_baseline_manifest import PKG, XML, CLASS, CODE, package
from test_v0339_repair_payload import request as proof_request


@pytest.mark.asyncio
async def test_original_apk_attachment_uses_installed_inventory_and_never_invokes_installer(tmp_path, monkeypatch):
    from app import source_maintenance as module
    from app.config import settings
    from app.suwayomi import source_host
    from app import repair_baseline
    monkeypatch.setattr(settings, 'data_dir', tmp_path)
    monkeypatch.setattr(settings, 'source_engine_mode', 'integrated')
    extension = {'pkgName': PKG, 'isInstalled': True, 'versionName': '1.4.23'}
    installed = tmp_path/'installed.jar'; installed.write_bytes(package([(CLASS, CODE)]))
    before = installed.read_bytes()
    monkeypatch.setattr(module, 'installed_path', lambda _: installed)
    inventory = AsyncMock(return_value=[extension])
    monkeypatch.setattr(module, 'inventory', inventory)
    installer = AsyncMock(side_effect=AssertionError('Attaching evidence must never install a package'))
    monkeypatch.setattr(source_host, 'install_extension_file', installer)
    # Isolate POSIX durability from this HTTP/file behavior check on Windows.
    monkeypatch.setattr(module, 'atomic', lambda path, data: (path.parent.mkdir(parents=True, exist_ok=True), path.write_bytes(data)))
    app = FastAPI(); app.include_router(module.maintenance_router(lambda: None, asyncio.Lock()))
    data = package([('AndroidManifest.xml', XML), ('classes.dex', b'dex\n035\0fixture')])
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://test') as client:
        endpoint = '/api/admin/source-maintenance/original-apk/' + PKG
        assert (await client.post(endpoint, content=data)).status_code == 400
        assert not (tmp_path/'source-maintenance/repair-apks').exists()
        headers = {'X-AutoSourcarr-Original-Apk-Confirmed': 'true', 'X-AutoSourcarr-Original-Apk-Version': '1.4.23'}
        assert (await client.post(endpoint, content=data, headers={**headers, 'X-AutoSourcarr-Original-Apk-Version': '1.4.22'})).status_code == 409
        assert not (tmp_path/'source-maintenance/repair-apks').exists()
        response = await client.post(endpoint, content=data, headers=headers)
        assert response.status_code == 200, response.text
        digest = response.json()['sha256']
        assert (tmp_path/'source-maintenance/repair-apks'/digest/'uploaded.apk').read_bytes() == data
        receipt = json.loads((tmp_path/'source-maintenance/repair-apks'/digest/'receipt.json').read_text())
        assert receipt['state'] == 'provided'
        assert receipt['confirmed_original'] is True
        assert receipt['target_version'] == extension['versionName']
        assert receipt['target_jar_sha256'] == module.sha(before)
        assert repair_baseline.retained_apk(extension, baseline_sha256=module.sha(before)) == data
        assert repair_baseline.retained_apk({**extension, 'versionName': '1.4.24'}, baseline_sha256=module.sha(before)) is None
        assert repair_baseline.retained_apk(extension, baseline_sha256='f'*64) is None
        assert installed.read_bytes() == before
        # A different valid APK must not attach to the selected installed source.
        wrong = package([('AndroidManifest.xml', XML.replace(PKG.encode(), PKG.replace('example', 'another').encode())), ('classes.dex', b'dex\n035\0fixture')])
        assert (await client.post(endpoint, content=wrong, headers=headers)).status_code == 400
        monkeypatch.setattr(module, 'MAX_PACKAGE', 10)
        assert (await client.post(endpoint, content=data, headers=headers)).status_code == 413
    installer.assert_not_awaited()
    assert len(list((tmp_path/'source-maintenance/repair-apks').iterdir())) == 1


@pytest.mark.asyncio
async def test_private_worker_admission_pins_both_input_files_and_excludes_package_bodies(tmp_path, monkeypatch):
    worker = Path(__file__).resolve().parents[2]/'repair-worker'
    monkeypatch.syspath_prepend(str(worker))
    # No worker lifespan, source command or platform lock is used by this ASGI test.
    monkeypatch.setitem(sys.modules, 'control', SimpleNamespace(Control=object))
    monkeypatch.setitem(sys.modules, 'runner', SimpleNamespace(Runner=object))
    spec = importlib.util.spec_from_file_location('fixture_server_v0339', worker/'server.py')
    server = importlib.util.module_from_spec(spec); spec.loader.exec_module(server)
    import contracts
    class Worker:
        active = None
        def path(self, identifier): return tmp_path/identifier
        def save(self, job, state, message): job.update(state=state, message=message)
        def run(self, job): return None
    server.runner = Worker()
    monkeypatch.setenv('REPAIR_TOKEN', 'x' * 40)
    body = proof_request()
    try:
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=server.app), base_url='http://test') as client:
            headers = {'Authorization': 'Bearer ' + 'x' * 40}
            response = await client.post('/jobs', json=body, headers=headers)
            assert response.status_code == 202, response.text
            value = response.json(); folder = tmp_path/value['id']
            assert (folder/'baseline.jar').read_bytes() == base64.b64decode(body['baseline'])
            assert (folder/'source.apk').read_bytes() == base64.b64decode(body['source_apk'])
            assert value['source_apk_sha256'] == contracts.digest((folder/'source.apk').read_bytes())
            assert 'baseline' not in value and 'source_apk' not in value and 'installed_sources' not in value
            async def drained():
                while server.pending: await asyncio.sleep(0)
            await asyncio.wait_for(drained(), 1)
            bad = {**body, 'source_apk': 'invalid-base64!'}
            assert (await client.post('/jobs', json=bad, headers=headers)).status_code == 400
            monkeypatch.setattr(server, 'MAX_REQUEST', 10)
            assert (await client.post('/jobs', json=body, headers=headers)).status_code == 413
        assert len(list(tmp_path.iterdir())) == 1
    finally:
        server.executor.shutdown(wait=True)


def test_isolated_apk_install_uses_apk_filename_and_retains_returned_conversion_metadata(monkeypatch):
    worker = Path(__file__).resolve().parents[2]/'repair-worker'
    monkeypatch.syspath_prepend(str(worker))
    import validate
    calls = []
    def transport(request):
        calls.append(request)
        if request.url.path == '/api/v1/extension/install': return httpx.Response(200, text=PKG)
        assert 'apkName versionName' in json.loads(request.content)['query']
        return httpx.Response(200, json={'data': {'extensions': {'nodes': [{'pkgName': PKG, 'isInstalled': True,
            'apkName': 'candidate.apk', 'versionName': '1.4.23', 'source': {'nodes': [{'id': '99', 'name': 'Example', 'lang': 'en'}]}}]}}})
    client = httpx.Client(base_url='http://fixture', transport=httpx.MockTransport(transport))
    validator = validate.Validator('http://fixture', client=client)
    try:
        assert validator.install(b'SYNTHETIC_APK', PKG, apk=True)[0]['id'] == '99'
        assert b'filename="candidate.apk"' in calls[0].content
        assert validator.installed_extension['apkName'] == 'candidate.apk'
    finally:
        validator.close()
