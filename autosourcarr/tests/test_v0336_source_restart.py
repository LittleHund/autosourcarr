import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock,Mock

import httpx
import pytest

from app import source_browser as browser,source_engine as engine,main


@pytest.mark.asyncio
async def test_internal_source_restart_is_admitted_as_an_owned_browser_action(monkeypatch):
    manager=browser.SourceBrowser()
    monkeypatch.setattr(browser.settings,'source_engine_mode','integrated')
    run=AsyncMock()
    monkeypatch.setattr(manager,'_run',run)
    lock=asyncio.Lock()
    manager.queue('restart',lock)
    await manager.task
    run.assert_awaited_once_with('restart',lock,None)


@pytest.mark.asyncio
async def test_restart_keeps_cookies_preferences_extensions_and_library_data(tmp_path,monkeypatch):
    command=tmp_path/'start'
    command.write_text('fixture')
    retained=tmp_path/'profile/settings/cookie_store.xml'
    retained.parent.mkdir(parents=True)
    retained.write_text('retained signed-in source profile')
    fake=SimpleNamespace(child=None,state='ready',stop=AsyncMock(),start=Mock())
    monkeypatch.setattr(engine,'source_engine',fake)
    monkeypatch.setattr(engine,'ENGINE_COMMAND',str(command))
    monkeypatch.setattr(engine,'ENGINE_DATA',tmp_path/'profile')
    monkeypatch.setattr(browser,'clear_local_browser_data',Mock(side_effect=AssertionError('Restart cannot clear browser data')))
    monkeypatch.setattr(browser,'save_preferences',Mock(side_effect=AssertionError('Restart cannot change identity')))
    manager=browser.SourceBrowser()
    monkeypatch.setattr(manager,'_assert_runtime_absent',AsyncMock())
    await manager._run('restart',asyncio.Lock(),None)
    assert manager.state=='completed' and not manager.busy
    fake.stop.assert_awaited_once()
    fake.start.assert_called_once()
    assert retained.read_text()=='retained signed-in source profile'


@pytest.mark.asyncio
async def test_restart_endpoint_requires_admin_and_queues_only_one_owned_action(monkeypatch):
    queue=Mock()
    monkeypatch.setattr(main.source_browser,'queue',queue)
    monkeypatch.setattr(main.settings,'admin_token','restart-admin-secret')
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app),base_url='http://test') as client:
        assert (await client.post('/api/admin/source-browser/restart')).status_code==401
        response=await client.post('/api/admin/source-browser/restart',headers={'X-Admin-Token':'restart-admin-secret'})
        assert response.status_code==202 and response.json()['queued']
    queue.assert_called_once_with('restart',main._database_admin_lock)
