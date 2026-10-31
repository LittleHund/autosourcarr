"""Proposed deployment regressions: real SQLite and controlled engine startup."""
import asyncio
from pathlib import Path

import httpx
import pytest

from app import service as service_module
from app import source_engine as engine_module
from app.config import settings
from app.db import Database


def test_source_return_preserves_choices_and_unavailable_admission(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, 'data_dir', tmp_path)
    monkeypatch.setattr(settings, 'database_dir', tmp_path)
    monkeypatch.setattr(settings, 'source_languages', 'en')
    database = Database()
    monkeypatch.setattr(service_module, 'db', database)
    service = service_module.AutoSourcarrService()
    a = {'id': 'source-a', 'name': 'A', 'lang': 'en'}
    b = {'id': 'source-b', 'name': 'B', 'lang': 'en'}
    try:
        service._persist_source_snapshot([a, b])
        service.update_source('source-b', enabled_search=False, enabled_chapters=False)
        service._persist_source_snapshot([b])
        assert 'source-a' not in service_module._enabled_chapter_source_ids()
        with database.catalog() as connection:
            missing = dict(connection.execute("SELECT * FROM source_config WHERE source_id='source-a'").fetchone())
        assert missing['available'] == 0
        assert missing['enabled_search'] == missing['enabled_chapters'] == 1
        service._persist_source_snapshot([a, b])
        with database.catalog() as connection:
            returned = {row['source_id']: dict(row) for row in connection.execute('SELECT * FROM source_config')}
        assert returned['source-a']['available'] == 1
        assert returned['source-a']['enabled_search'] == returned['source-a']['enabled_chapters'] == 1
        assert returned['source-b']['enabled_search'] == returned['source-b']['enabled_chapters'] == 0
        assert service_module._enabled_chapter_source_ids() == {'source-a'}
    finally:
        database.close_current_thread()


@pytest.mark.asyncio
@pytest.mark.parametrize('automatic,configured,intended', [
    (False, 'false', 'false'), (False, 'true', 'false'),
    (False, None, 'false'), (True, 'false', 'false'),
    (True, 'true', 'true'), (True, None, 'true'),
])
async def test_solver_startup_respects_disable_and_waits_for_readiness(
        tmp_path, monkeypatch, automatic, configured, intended):
    command = tmp_path / 'unused-engine'
    command.write_text('never executed')
    monkeypatch.setattr(engine_module, 'ENGINE_COMMAND', str(command))
    monkeypatch.setattr(engine_module, 'ENGINE_DATA', tmp_path)
    monkeypatch.setattr(settings, 'settings_config_dir', tmp_path)
    monkeypatch.setattr(settings, 'source_engine_mode', 'integrated')
    monkeypatch.setattr(settings, 'app_services_mode', 'integrated')
    monkeypatch.setattr(settings, 'source_auto_challenges', automatic)
    monkeypatch.setattr(settings, 'source_request_timeout', 20)
    monkeypatch.setattr(engine_module, 'child_identity', lambda: {})
    environment = {'FLARESOLVERR_TIMEOUT': '180'}
    if configured is not None:
        environment['FLARESOLVERR_ENABLED'] = configured
    monkeypatch.setattr(engine_module, 'child_environment', lambda _: dict(environment))
    class Probe:
        def __init__(self, **kwargs): pass
        async def __aenter__(self): return self
        async def __aexit__(self, *args): pass
        async def get(self, url): raise httpx.ConnectError('Controlled absent engine')
    monkeypatch.setattr(engine_module.httpx, 'AsyncClient', Probe)
    captured = {}
    async def spawn(*args, **kwargs):
        captured.update(kwargs['env'])
        raise asyncio.CancelledError()
    monkeypatch.setattr(engine_module.asyncio, 'create_subprocess_exec', spawn)
    engine = engine_module.SourceEngine()
    with pytest.raises(asyncio.CancelledError):
        await engine._run()
    assert captured['FLARESOLVERR_ENABLED'] == 'false'
    assert captured['FLARESOLVERR_TIMEOUT'] == '9'
    assert engine.challenge_env['FLARESOLVERR_ENABLED'] == intended
    if not automatic:
        engine.state = 'ready'
        assert engine.schedule_challenge_setup() is False
        assert engine.challenges['state'] == 'disabled'
