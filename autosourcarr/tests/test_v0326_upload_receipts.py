"""Upload acknowledgements persist by identity; ambiguous responses need no replay."""
from pathlib import Path

import httpx
import pytest

from app import main
from app.config import settings
from test_v087_recovery import env


@pytest.fixture
def upload_app(env, monkeypatch):
    service, database = env
    monkeypatch.setattr(main, 'db', database)
    monkeypatch.setattr(main, 'service', service)
    monkeypatch.setattr(main.app, 'dependency_overrides', {main.admin_guard: lambda: None})
    accepted = []

    def queue(raw, decoded, *, filename, import_id):
        accepted.append((import_id, filename, Path(raw).read_bytes()))
        service._create_import_run(import_id, filename)
        return {'queued': True, 'import_id': import_id, 'filename': filename}

    monkeypatch.setattr(service, 'schedule_backup_import', queue)
    return service, database, accepted


@pytest.mark.asyncio
async def test_accepted_upload_receipt_survives_and_repeat_does_not_queue(upload_app):
    _, database, accepted = upload_app
    identity = 'ab' * 16
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url='http://test') as client:
        first = await client.post('/api/admin/import/mihon-backup', content=b'original backup',
                                  headers={'X-Filename': 'audit.tachibk', 'X-Import-ID': identity})
        assert first.status_code == 200
        assert first.json()['import_id'] == identity
        # A caller that lost the response can ask for this identity after restart.
        database.close_current_thread()
        receipt = await client.get(f'/api/admin/imports/{identity}/receipt')
        assert receipt.status_code == 200
        assert receipt.json() == {'queued': True, 'import_id': identity,
                                  'filename': 'audit.tachibk', 'state': 'running'}
        repeat = await client.post('/api/admin/import/mihon-backup', content=b'an accidental replay',
                                   headers={'X-Filename': 'audit.tachibk', 'X-Import-ID': identity})
        assert repeat.status_code == 200
        assert repeat.json()['import_id'] == identity
        different = await client.post('/api/admin/import/mihon-backup', content=b'other',
                                      headers={'X-Filename': 'other.tachibk', 'X-Import-ID': identity})
        assert different.status_code == 409
    assert accepted == [(identity, 'audit.tachibk', b'original backup')]


@pytest.mark.asyncio
async def test_receiving_upload_cannot_be_overwritten_and_is_not_yet_acknowledged(upload_app):
    _, _, accepted = upload_app
    identity = 'cd' * 16
    directory = settings.data_dir / 'imports' / identity
    directory.mkdir(parents=True)
    partial = directory / 'audit.tachibk'
    partial.write_bytes(b'owned partial stream')
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url='http://test') as client:
        conflict = await client.post('/api/admin/import/mihon-backup', content=b'new bytes',
                                     headers={'X-Filename': 'audit.tachibk', 'X-Import-ID': identity})
        assert conflict.status_code == 409
        missing = await client.get(f'/api/admin/imports/{identity}/receipt')
        assert missing.status_code == 404
    assert partial.read_bytes() == b'owned partial stream'
    assert accepted == []


@pytest.mark.asyncio
@pytest.mark.parametrize('identity', ['../escape', 'x' * 32, 'ab' * 15])
async def test_invalid_upload_identity_is_rejected_before_creating_files(upload_app, identity):
    _, _, accepted = upload_app
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url='http://test') as client:
        response = await client.post('/api/admin/import/mihon-backup', content=b'backup',
                                     headers={'X-Filename': 'audit.tachibk', 'X-Import-ID': identity})
    assert response.status_code == 400
    assert accepted == []
    assert not (settings.data_dir / 'imports').exists()
