import asyncio
import time
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock

import httpx
import pytest

from app import source_challenges as mod, source_engine as engine_mod, suwayomi
from app.config import settings
from app.error_registry import ErrorRegistry
from test_v087_recovery import env

_LOCAL_HTTP_TRANSPORT = httpx.AsyncHTTPTransport.handle_async_request

@pytest.fixture
def state(monkeypatch):
    values = {}
    monkeypatch.setattr(mod, 'db', SimpleNamespace(
        setting=lambda key, default=None: values.get(key, default),
        set_setting=lambda key, value: values.__setitem__(key, value)))
    monkeypatch.setattr(mod, 'error_registry', ErrorRegistry())
    monkeypatch.setattr(settings, 'source_request_timeout', 60)
    return values


def client(url='http://solver.invalid:8191'):
    runtime = {'flareSolverrEnabled': True, 'flareSolverrUrl': url, 'flareSolverrTimeout': 30}
    async def graphql(query, variables=None):
        if variables:
            patch = variables['input']['settings']
            assert set(patch) == {'flareSolverrEnabled', 'flareSolverrTimeout'}
            runtime.update(patch)
            return {'setSettings': {'settings': dict(runtime)}}
        return {'settings': dict(runtime)}
    return SimpleNamespace(graphql=graphql), runtime


@pytest.mark.asyncio
async def test_persistent_backoff_stops_minute_poll_storm_and_manual_check_recovers(state, monkeypatch):
    now = [10000.0]
    monkeypatch.setattr(mod, 'time', SimpleNamespace(time=lambda: now[0]))
    health = AsyncMock(side_effect=httpx.ConnectTimeout('secret should not appear'))
    monkeypatch.setattr(httpx.AsyncClient, 'get', health)
    source, runtime = client()
    # Fresh env on every call also exercises restart recovery of guarded intent.
    for minute in range(121):
        now[0] = 10000.0 + minute * 60
        result = await mod.configure_cloudflare(source, {})
        assert result['state'] == 'unavailable'
        assert runtime['flareSolverrEnabled'] is False
    assert health.await_count == 7  # formerly 121 probes for this workload
    assert state[mod.PROBE_KEY]['next_probe_at'] > now[0]
    errors = mod.error_registry.snapshot()['items']
    assert len(errors) == 1 and errors[0]['count'] == 7
    assert errors[0]['status_code'] == 503 and 'secret' not in errors[0]['detail']
    # A process restart restores the warning without defeating the saved delay.
    monkeypatch.setattr(mod, 'error_registry', ErrorRegistry())
    await mod.configure_cloudflare(source, {})
    await mod.configure_cloudflare(source, {})
    assert health.await_count == 7
    assert mod.error_registry.snapshot()['items'][0]['count'] == 1
    health.side_effect = None
    health.return_value = httpx.Response(200, json={'status': 'ok'}, request=httpx.Request('GET', runtime['flareSolverrUrl']))
    result = await mod.configure_cloudflare(source, {}, force=True)
    assert result['state'] == 'ready' and runtime['flareSolverrEnabled'] is True
    assert health.await_count == 8
    assert state[mod.PROBE_KEY] == {} and state['source_solver_gate_v1'] == {}
    assert mod.error_registry.snapshot()['active_count'] == 0


@pytest.mark.asyncio
async def test_changed_endpoint_is_checked_immediately_and_disabled_solver_clears_gate(state, monkeypatch):
    health = AsyncMock(side_effect=httpx.ConnectError('refused'))
    monkeypatch.setattr(httpx.AsyncClient, 'get', health)
    source, _ = client()
    await mod.configure_cloudflare(source, {})
    source, runtime = client('http://replacement.invalid:8191')
    result = await mod.configure_cloudflare(source, {})
    assert health.await_count == 2 and state[mod.PROBE_KEY]['failures'] == 1
    assert result['endpoint'] == runtime['flareSolverrUrl']
    await mod.configure_cloudflare(source, {'FLARESOLVERR_ENABLED': 'false'})
    assert health.await_count == 2 and state[mod.PROBE_KEY] == {}
    assert mod.error_registry.snapshot()['active_count'] == 0


@pytest.mark.asyncio
@pytest.mark.parametrize('error,kind,text', [
    (httpx.ConnectTimeout('credential'), 'connect_timeout', 'connection within 2 seconds'),
    (httpx.ReadTimeout('credential'), 'read_timeout', 'response within 5 seconds'),
    (httpx.ConnectError('credential'), 'connection_failed', 'DNS'),
    (httpx.RemoteProtocolError('credential'), 'health_request_failed', '8-second'),
])
async def test_connection_diagnostics_distinguish_failures_without_exposing_credentials(state, monkeypatch, error, kind, text):
    health = AsyncMock(side_effect=error)
    monkeypatch.setattr(httpx.AsyncClient, 'get', health)
    source, _ = client('http://name:password@solver.invalid:8191')
    result = await mod.configure_cloudflare(source, {})
    assert result['failure_kind'] == kind and text in result['detail']
    assert result['endpoint'] == 'http://solver.invalid:8191'
    for payload in (result, mod.error_registry.snapshot()):
        assert 'password' not in str(payload) and 'credential' not in str(payload)
    assert result['action']


@pytest.mark.asyncio
@pytest.mark.parametrize('status,payload,kind', [
    (503, {'status': 'error'}, 'http_error'),
    (401, {}, 'http_error'),
    (404, {}, 'http_error'),
    (200, [], 'not_ready'),
    (200, {'status': 'starting'}, 'not_ready'),
])
async def test_http_and_not_ready_responses_are_actionable(state, monkeypatch, status, payload, kind):
    response = httpx.Response(status, json=payload, request=httpx.Request('GET', 'http://solver.invalid/health'))
    monkeypatch.setattr(httpx.AsyncClient, 'get', AsyncMock(return_value=response))
    source, _ = client()
    result = await mod.configure_cloudflare(source, {})
    assert result['state'] == 'unavailable' and result['failure_kind'] == kind
    if status != 200:
        assert result['upstream_status'] == status and f'HTTP {status}' in result['detail']


@pytest.mark.asyncio
@pytest.mark.parametrize('url', ['', 'invalid', 'http://host:bad', 'http://host:8191?token=private', 'http://host/#private'])
async def test_invalid_configuration_never_makes_a_health_request(state, monkeypatch, url):
    health = AsyncMock(side_effect=AssertionError('unexpected request'))
    monkeypatch.setattr(httpx.AsyncClient, 'get', health)
    source, _ = client(url)
    result = await mod.configure_cloudflare(source, {})
    assert result['state'] == 'unavailable'
    assert result['failure_kind'] == 'invalid_configuration_or_response'
    assert 'private' not in str(result)
    health.assert_not_awaited()


@pytest.mark.asyncio
async def test_slow_healthy_service_succeeds_over_real_local_http(state, monkeypatch):
    async def localhost_only(transport, request):
        assert request.url.host == '127.0.0.1'
        return await _LOCAL_HTTP_TRANSPORT(transport, request)
    monkeypatch.setattr(httpx.AsyncHTTPTransport, 'handle_async_request', localhost_only)
    requests = []
    done = asyncio.Event()
    async def serve(reader, writer):
        try:
            requests.append(await reader.readuntil(b'\r\n\r\n'))
            await asyncio.sleep(2.15)  # the previous 2-second timeout rejected this
            body = b'{"status":"ok"}'
            writer.write(b'HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 15\r\nConnection: close\r\n\r\n' + body)
            await writer.drain()
        finally:
            writer.close()
            await writer.wait_closed()
            done.set()
    server = await asyncio.start_server(serve, '127.0.0.1', 0)
    try:
        source, runtime = client(f'http://127.0.0.1:{server.sockets[0].getsockname()[1]}')
        result = await mod.configure_cloudflare(source, {})
        await asyncio.wait_for(done.wait(), 3)
        assert result['state'] == 'ready' and runtime['flareSolverrEnabled'] is True
        assert requests[0].startswith(b'GET /health HTTP/1.1')
    finally:
        server.close()
        await server.wait_closed()


@pytest.mark.asyncio
async def test_solver_only_retries_reuse_sources_and_manual_check_revalidates(state, monkeypatch):
    source = SimpleNamespace(sources=AsyncMock(return_value=[{'id': '1', 'name': 'MangaFire'}]))
    monkeypatch.setattr(suwayomi, 'source_host', source)
    handler = AsyncMock(return_value={'state': 'unavailable', 'next_probe_at': time.time() + 3590})
    monkeypatch.setattr(engine_mod, 'configure_cloudflare', handler)
    prefs = AsyncMock(return_value=[{'source_id': '1', 'state': 'enabled', 'changed': True}])
    monkeypatch.setattr(engine_mod, 'configure_source_captcha', prefs)
    engine = engine_mod.SourceEngine()
    await engine._setup_challenges()
    assert engine.challenge_retry_at - time.monotonic() > 3500
    assert engine.challenge_setup_needs_retry()
    await engine._setup_challenges()
    assert source.sources.await_count == 1 and prefs.await_count == 1
    assert engine.challenges['sources'][0]['changed'] is False
    await engine._setup_challenges(force=True)
    assert source.sources.await_count == 2 and prefs.await_count == 2
    assert handler.call_args.kwargs == {'force': True}


@pytest.mark.asyncio
async def test_preference_recovery_does_not_wait_for_solver_hourly_backoff(state, monkeypatch):
    monkeypatch.setattr(suwayomi, 'source_host', SimpleNamespace(sources=AsyncMock(return_value=[])))
    monkeypatch.setattr(engine_mod, 'configure_cloudflare', AsyncMock(return_value={'state': 'unavailable', 'next_probe_at': time.time() + 3600}))
    prefs = AsyncMock(side_effect=[[{'state': 'unavailable'}], []])
    monkeypatch.setattr(engine_mod, 'configure_source_captcha', prefs)
    engine = engine_mod.SourceEngine()
    await engine._setup_challenges()
    assert 55 < engine.challenge_retry_at - time.monotonic() <= 60
    await engine._setup_challenges()
    assert prefs.await_count == 2 and engine.challenge_retry_at - time.monotonic() > 3500


@pytest.mark.asyncio
async def test_manual_endpoint_bypasses_automatic_delay(monkeypatch):
    from app import main
    fake = SimpleNamespace(schedule_challenge_setup=Mock(return_value=True), snapshot=lambda: {'state': 'ready'})
    monkeypatch.setattr(main, 'source_engine', fake)
    assert (await main.admin_source_challenges())['queued']
    fake.schedule_challenge_setup.assert_called_once_with(force=True)


@pytest.mark.asyncio
async def test_source_reconciliation_invalidates_preference_cache_without_forcing_solver(state, monkeypatch):
    monkeypatch.setattr(settings, 'source_engine_mode', 'integrated')
    monkeypatch.setattr(settings, 'source_auto_challenges', True)
    source = SimpleNamespace(sources=AsyncMock(side_effect=[[], [{'id': 'new', 'name': 'MangaFire'}]]))
    monkeypatch.setattr(suwayomi, 'source_host', source)
    handler = AsyncMock(return_value={'state': 'unavailable', 'next_probe_at': time.time() + 3590})
    monkeypatch.setattr(engine_mod, 'configure_cloudflare', handler)
    prefs = AsyncMock(side_effect=[[], [{'source_id': 'new', 'state': 'enabled'}]])
    monkeypatch.setattr(engine_mod, 'configure_source_captcha', prefs)
    engine = engine_mod.SourceEngine()
    engine.state = 'ready'
    await engine._setup_challenges()
    assert engine.challenges['sources'] == []
    assert engine.schedule_challenge_setup(refresh_sources=True)
    await engine.challenge_task
    assert source.sources.await_count == 2
    assert engine.challenges['sources'][0]['source_id'] == 'new'
    assert handler.call_args.kwargs == {'force': False}


@pytest.mark.asyncio
async def test_reconciliation_during_active_setup_is_not_lost(state, monkeypatch):
    monkeypatch.setattr(settings, 'source_engine_mode', 'integrated')
    monkeypatch.setattr(settings, 'source_auto_challenges', True)
    source = SimpleNamespace(sources=AsyncMock(return_value=[]))
    monkeypatch.setattr(suwayomi, 'source_host', source)
    monkeypatch.setattr(engine_mod, 'configure_cloudflare', AsyncMock(return_value={'state': 'ready'}))
    started, finish = asyncio.Event(), asyncio.Event()
    async def prefs(*args):
        started.set()
        await finish.wait()
        return []
    monkeypatch.setattr(engine_mod, 'configure_source_captcha', prefs)
    engine = engine_mod.SourceEngine()
    engine.state = 'ready'
    assert engine.schedule_challenge_setup()
    await started.wait()
    assert not engine.schedule_challenge_setup(refresh_sources=True)
    finish.set()
    await engine.challenge_task
    assert not engine.challenge_sources_checked and engine.challenge_setup_needs_retry()
    assert engine.schedule_challenge_setup()
    await engine.challenge_task
    assert source.sources.await_count == 2 and not engine.challenge_setup_needs_retry()


@pytest.mark.parametrize('method', ['reset_database', 'full_reset_database'])
def test_catalog_reset_retains_solver_connection_state(env, method):
    service, database = env
    records = {'source_solver_gate_v1': {'url_hash': 'saved', 'enabled': True},
               mod.PROBE_KEY: {'url_hash': 'saved', 'next_probe_at': 99999},
               'source_human_attention_v1': {'items': []}}
    for key, value in records.items():
        database.set_setting(key, value)
    database.set_setting('temporary_catalog_setting', {'value': 1})
    getattr(service, method)()
    for key, value in records.items():
        assert database.setting(key) == value
    assert database.setting('temporary_catalog_setting') is None
