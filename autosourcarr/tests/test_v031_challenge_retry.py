import asyncio
import copy
import time
from types import SimpleNamespace
from unittest.mock import AsyncMock

import httpx
import pytest

from app import source_challenges as challenges
from app import source_engine as engine_module
from app import suwayomi
from app.config import settings
from app.error_registry import ErrorRegistry
from app.service import service

SID = '6084907896154116083'

@pytest.fixture
def setup_state(monkeypatch):
    values = {}
    monkeypatch.setattr(challenges, 'db', SimpleNamespace(setting=lambda key,default=None:values.get(key,default), set_setting=lambda key,value:values.__setitem__(key,value)))
    monkeypatch.setattr(challenges, 'error_registry', ErrorRegistry())
    monkeypatch.setattr(service, '_source_failures', {})
    monkeypatch.setattr(service, '_page_lookup_failures', {})
    monkeypatch.setattr(settings, 'source_engine_mode', 'integrated')
    monkeypatch.setattr(settings, 'source_auto_challenges', True)
    return values

@pytest.mark.asyncio
@pytest.mark.parametrize('cloudflare_state', ['ready', 'not_configured'])
async def test_transient_preference_failure_is_retryable_with_healthy_cloudflare(setup_state, monkeypatch, cloudflare_state):
    calls = 0
    prefs = [{'__typename': 'SwitchPreference', 'key': 'solve_captcha', 'enabled': True, 'currentValue': True}]
    async def query(query, variables=None):
        nonlocal calls
        calls += 1
        assert variables == {'id': SID}
        if calls == 1:
            raise suwayomi.SuwayomiError('Source engine temporarily busy')
        return {'source': {'preferences': prefs}}
    source_client = SimpleNamespace(sources=AsyncMock(return_value=[{'id': SID, 'name': 'MangaFire'}]), graphql=query)
    monkeypatch.setattr(suwayomi, 'source_host', source_client)
    monkeypatch.setattr(engine_module, 'configure_cloudflare', AsyncMock(return_value={'state': cloudflare_state}))
    engine = engine_module.SourceEngine()
    engine.state = 'ready'
    started = time.monotonic()
    assert engine.schedule_challenge_setup()
    assert not engine.schedule_challenge_setup()  # one owned setup at a time
    await engine.challenge_task
    assert engine.challenge_retry_at >= started + 60
    assert engine.challenges['state'] == 'partial'
    assert engine.challenge_setup_needs_retry()
    assert challenges.error_registry.snapshot()['active_count'] == 1
    # The monitor waits for challenge_retry_at; this explicit retry exercises the
    # same setup path after the transient failure has cleared.
    assert engine.schedule_challenge_setup()
    await engine.challenge_task
    assert engine.challenges['sources'][0]['state'] == 'enabled'
    assert not engine.challenge_setup_needs_retry()
    assert calls == 2  # already enabled; never mutated preferences
    assert challenges.error_registry.snapshot()['active_count'] == 0

@pytest.mark.asyncio
@pytest.mark.parametrize('prefs', [[], [{'__typename': 'SwitchPreference', 'key': 'solve_captcha', 'enabled': False}]])
async def test_unsupported_preferences_remain_actionable_without_retry_loop(setup_state, monkeypatch, prefs):
    query = AsyncMock(return_value={'source': {'preferences': prefs}})
    client = SimpleNamespace(sources=AsyncMock(return_value=[{'id': SID, 'name': 'MangaFire'}]), graphql=query)
    monkeypatch.setattr(suwayomi, 'source_host', client)
    monkeypatch.setattr(engine_module, 'configure_cloudflare', AsyncMock(return_value={'state': 'ready'}))
    engine = engine_module.SourceEngine()
    await engine._setup_challenges()
    assert engine.challenges['state'] == 'partial'
    assert engine.challenges['sources'][0]['state'] == 'unsupported'
    assert 'update' in engine.challenges['sources'][0]['message'].lower()
    assert not engine.challenge_setup_needs_retry()
    assert query.await_count == 1

@pytest.mark.asyncio
async def test_source_inventory_failure_retries_without_changing_cloudflare_state(setup_state, monkeypatch):
    client = SimpleNamespace(sources=AsyncMock(side_effect=[suwayomi.SuwayomiError('Busy'), []]))
    monkeypatch.setattr(suwayomi, 'source_host', client)
    monkeypatch.setattr(engine_module, 'configure_cloudflare', AsyncMock(return_value={'state': 'ready'}))
    engine = engine_module.SourceEngine()
    await engine._setup_challenges()
    assert engine.challenge_setup_needs_retry()
    assert engine.challenges['cloudflare']['state'] == 'ready'
    await engine._setup_challenges()
    assert not engine.challenge_setup_needs_retry()


def settings_client(url):
    runtime = {'flareSolverrEnabled': True, 'flareSolverrUrl': url, 'flareSolverrTimeout': 180}
    async def query(query, variables=None):
        if variables:
            patch = variables['input']['settings']
            assert set(patch) == {'flareSolverrEnabled', 'flareSolverrTimeout'}
            runtime.update(patch)
            return {'setSettings': {'settings': copy.deepcopy(runtime)}}
        return {'settings': copy.deepcopy(runtime)}
    return SimpleNamespace(graphql=query), runtime

@pytest.mark.asyncio
async def test_health_timeout_diagnostic_does_not_claim_all_sources_need_captcha(setup_state, monkeypatch):
    async def timeout(self, url, **kwargs):
        raise httpx.ReadTimeout('timed out', request=httpx.Request('GET', url))
    monkeypatch.setattr(httpx.AsyncClient, 'get', timeout)
    client, runtime = settings_client('http://solver.invalid:8191')
    result = await challenges.configure_cloudflare(client, {})
    assert result['state'] == 'unavailable'
    assert '/health' in result['detail'] and '5 seconds' in result['detail']
    assert result['failure_kind'] == 'read_timeout'
    assert 'health check failed' in result['message'].lower()
    assert 'if a source requests' in result['message']
    assert runtime['flareSolverrEnabled'] is False
    assert runtime['flareSolverrUrl'] == 'http://solver.invalid:8191'
    assert setup_state['source_solver_gate_v1']['enabled'] is True
    records = challenges.error_registry.snapshot()['items']
    assert len(records) == 1 and records[0]['error_type'] == 'ChallengeSolverUnavailable'
