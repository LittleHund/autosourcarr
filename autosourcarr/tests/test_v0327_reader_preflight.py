"""Reader configuration checks exercise the real authenticated public routers."""
import asyncio
import json
import os
from pathlib import Path
import subprocess

import pytest
from fastapi import FastAPI, HTTPException, Request
from fastapi.testclient import TestClient

from app.config import Settings
from app import internal_services as services_module
from app import setup_api, setup_store
from app.reader_build import evaluate_reader_config


@pytest.fixture
def reader_routes(tmp_path, monkeypatch):
    config = Settings(_env_file=None, settings_config_dir=tmp_path/'config',
                      app_services_mode='integrated', source_engine_mode='external',
                      autosourcarr_internal_url='http://autosourcarr:8282',
                      autosourcarr_public_url='https://reader.example',
                      autosourcarr_reader_api_token='')
    monkeypatch.setattr(services_module, 'settings', config)
    manager = services_module.InternalServices()
    spawned = []

    async def no_build():
        spawned.append(True)

    monkeypatch.setattr(manager, '_build', no_build)
    monkeypatch.setattr(services_module, 'internal_services', manager)
    monkeypatch.setattr(setup_api, 'cleanup_after_setup', lambda *args: None)

    def guard(request: Request):
        if request.headers.get('x-admin-token') != 'fixture':
            raise HTTPException(401, 'Admin authorization required')

    app = FastAPI()
    app.include_router(setup_api.router(config, guard, '0.3.27'))
    app.include_router(services_module.services_router(guard))
    with TestClient(app) as client:
        yield client, config, manager, spawned


AUTH = {'X-Admin-Token': 'fixture'}


def test_preview_uses_actual_draft_without_saving_or_echoing_reader_key(reader_routes):
    client, config, _, _ = reader_routes
    snapshot = client.get('/api/admin/setup', headers=AUTH).json()
    token = 'DraftSecretNeverEchoed' + 'a'*32
    response = client.post('/api/admin/setup/reader-preflight', headers=AUTH,
                           json={'revision': snapshot['revision'], 'changes': {
                               'AUTOSOURCARR_READER_API_TOKEN': token,
                               'AUTOSOURCARR_PUBLIC_URL': 'http://reader.example'}})
    assert response.status_code == 200
    result = response.json()['reader_build']
    assert result['ready'] is False
    assert result['exit_code'] == 14
    assert any(item['key'] == 'AUTOSOURCARR_PUBLIC_URL' for item in result['errors'])
    assert token not in response.text
    assert not config.settings_config_dir.exists()


def test_invalid_build_is_blocked_before_a_task_can_start(reader_routes):
    client, _, manager, spawned = reader_routes
    response = client.post('/api/admin/services/builder/start', headers=AUTH)
    assert response.status_code == 400
    assert 'reader' in response.json()['detail'].lower()
    assert not spawned
    assert 'builder' not in manager.tasks
    assert manager.states['builder']['state'] == 'blocked'


def test_saved_masked_key_is_retained_but_explicit_removal_is_not(reader_routes):
    client, config, _, _ = reader_routes
    config.autosourcarr_reader_api_token = 'SavedNeverEchoed' + 'a'*32
    snapshot = client.get('/api/admin/setup', headers=AUTH).json()
    secret = next(f for f in snapshot['fields'] if f['key'] == 'AUTOSOURCARR_READER_API_TOKEN')
    assert secret['value'] == '' and secret['configured'] is True
    assert snapshot['reader_build']['ready'] is True
    for changes, ready in [({}, True), ({'AUTOSOURCARR_READER_API_TOKEN': ''}, False)]:
        response = client.post('/api/admin/setup/reader-preflight', headers=AUTH,
                               json={'revision': snapshot['revision'], 'changes': changes})
        assert response.status_code == 200
        assert response.json()['reader_build']['ready'] is ready
        assert config.autosourcarr_reader_api_token not in response.text
    assert not config.settings_config_dir.exists()


def test_reader_advisory_does_not_block_internal_setup_finish(reader_routes):
    client, config, _, _ = reader_routes
    snapshot = client.get('/api/admin/setup', headers=AUTH).json()
    response = client.post('/api/admin/setup/finish', headers=AUTH,
                           json={'revision': snapshot['revision'], 'transaction': 'a33eac96-d48b-4427-af1b-50b19dbdaf6d',
                                 'changes': {'AUTOSOURCARR_PUBLIC_URL': 'http://127.0.0.1:8282'}})
    assert response.status_code == 200
    result = response.json()
    assert result['completed'] is True and result['reader_build']['ready'] is False
    repeated = client.post('/api/admin/setup/finish', headers=AUTH,
                           json={'revision': snapshot['revision'], 'transaction': result['transaction'], 'changes': {}})
    assert repeated.status_code == 200
    assert repeated.json()['reader_build'] == result['reader_build']
    assert 'AUTOSOURCARR_PUBLIC_URL=http://127.0.0.1:8282' in (config.settings_config_dir/'autosourcarr.env').read_text()


def test_preview_conflict_authentication_and_origin_are_enforced(reader_routes):
    client, _, _, _ = reader_routes
    revision = client.get('/api/admin/setup', headers=AUTH).json()['revision']
    body = {'revision': revision, 'changes': {}}
    assert client.post('/api/admin/setup/reader-preflight', json=body).status_code == 401
    assert client.post('/api/admin/setup/reader-preflight', headers={**AUTH, 'Origin': 'https://elsewhere.example'}, json=body).status_code == 403
    assert client.post('/api/admin/setup/reader-preflight', headers=AUTH, json={**body, 'revision': 'stale'}).status_code == 409
    assert client.post('/api/admin/setup/reader-preflight', headers=AUTH, json={**body, 'changes': {'UNKNOWN': 'value'}}).status_code == 400


def test_valid_reader_build_admits_and_coalesces_without_echoing_key(reader_routes):
    client, config, manager, _ = reader_routes
    config.autosourcarr_reader_api_token = 'a'*32
    status = client.get('/api/admin/services', headers=AUTH)
    assert status.json()['reader_build']['ready'] is True
    assert config.autosourcarr_reader_api_token not in status.text
    # The real admission method keeps one owner while the actual build is paused.
    async def scenario():
        entered, release = asyncio.Event(), asyncio.Event()
        calls = []
        async def build():
            calls.append(True); entered.set(); await release.wait()
        manager._build = build
        manager.build(); await entered.wait(); manager.build()
        assert calls == [True]
        release.set(); await manager.tasks['builder']
    asyncio.run(scenario())


@pytest.mark.parametrize('changes,code', [
    ({'AUTOSOURCARR_READER_API_TOKEN': ''}, 12),
    ({'AUTOSOURCARR_READER_API_TOKEN': 'short'}, 13),
    ({'AUTOSOURCARR_READER_API_TOKEN': 'a'*257}, 13),
    ({'AUTOSOURCARR_READER_API_TOKEN': 'a'*31+'€'}, 13),
    ({'AUTOSOURCARR_PUBLIC_URL': 'http://reader.example'}, 14),
    ({'AUTOSOURCARR_PUBLIC_URL': 'https://autosourcarr:8282'}, 11),
    ({'AUTOSOURCARR_PUBLIC_URL': 'https://private-autosourcarr'}, 11),
    ({'AUTOSOURCARR_PUBLIC_URL': 'https://user:password@reader.example'}, 14),
    ({'AUTOSOURCARR_PUBLIC_URL': 'https://reader.example/path?query=yes'}, 14),
    ({'AUTOSOURCARR_PUBLIC_URL': 'https://reader.example/#fragment'}, 14),
    ({'AUTOSOURCARR_PUBLIC_URL': 'https://reader.example/$injected'}, 14),
    ({'AUTOSOURCARR_PUBLIC_URL': 'https://reader.example:65536'}, 14),
    ({'AUTOSOURCARR_PUBLIC_URL': 'https://reader.example:invalid'}, 14),
    ({'AUTOSOURCARR_PUBLIC_URL': 'https://reader.example/space here'}, 14),
    ({'AUTOSOURCARR_INTERNAL_URL': 'ftp://server.example'}, 14),
    ({'AUTOSOURCARR_PUBLIC_URL': 'https://[2001:db8::1]:8443/base'}, 0),
    ({'AUTOSOURCARR_INTERNAL_URL': 'http://private-autosourcarr:8282/base'}, 0),
])
def test_real_shell_check_and_web_contract_agree_without_building(tmp_path, changes, code, monkeypatch):
    for key in ('AUTOSOURCARR_ALLOW_INSECURE_READER_API', 'AUTOSOURCARR_ALLOW_DOCKER_ANDROID_URL'):
        monkeypatch.delenv(key, raising=False)
    values = {'AUTOSOURCARR_INTERNAL_URL': 'http://autosourcarr:8282',
              'AUTOSOURCARR_PUBLIC_URL': 'https://reader.example',
              'AUTOSOURCARR_READER_API_TOKEN': 'a'*32, **changes}
    result = evaluate_reader_config(values)
    assert result['exit_code'] == code
    assert result['ready'] is (code == 0)
    config = tmp_path/'config'; config.mkdir()
    (config/'autosourcarr.env').write_text('\n'.join(key+'='+value for key,value in values.items())+'\n')
    env = {k:v for k,v in os.environ.items() if not k.startswith(('AUTOSOURCARR_', 'SOURCE_'))}
    env.update(AUTOSOURCARR_CONFIG_DIR=str(config), AUTOSOURCARR_BUILD_ROOT=str(tmp_path))
    root = Path(__file__).resolve().parents[2]
    command = subprocess.run(['bash', str(root/'extension/build-in-container.sh'), '--single', '--check'],
                             env=env, capture_output=True, text=True, timeout=5)
    assert command.returncode == code, command.stderr
    assert not (tmp_path/'extension').exists()
    assert not (config/'.extension-signing').exists()


def test_explicit_insecure_override_and_docker_port_warning_are_shared(tmp_path, monkeypatch):
    values = {'AUTOSOURCARR_INTERNAL_URL': 'http://private-autosourcarr:8283',
              'AUTOSOURCARR_PUBLIC_URL': 'http://reader.example', 'AUTOSOURCARR_READER_API_TOKEN': 'a'*32}
    assert evaluate_reader_config(values)['ready'] is False
    monkeypatch.setenv('AUTOSOURCARR_ALLOW_INSECURE_READER_API', '1')
    result = evaluate_reader_config(values)
    assert result['ready'] is True and result['warnings'][0]['key'] == 'AUTOSOURCARR_INTERNAL_URL'
    root = Path(__file__).resolve().parents[2]
    command = subprocess.run([str(Path(os.sys.executable)), str(root/'extension/reader_build_config.py'), '--check-env', '--json'],
                             env={**os.environ, **values}, capture_output=True, text=True, timeout=5)
    assert command.returncode == 0
    assert json.loads(command.stdout) == result
