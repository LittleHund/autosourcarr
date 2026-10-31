import asyncio
from unittest.mock import AsyncMock

import httpx
import pytest

from app import source_challenges as module
from app.config import settings
from app.error_registry import ErrorRegistry
from app.source_challenges import configure_cloudflare, configure_source_captcha, solver_timeout
from app.source_errors import source_error_kind, chapter_retry_delay


@pytest.fixture
def state(monkeypatch):
    values = {}
    monkeypatch.setattr(module.db, "setting", lambda key, default=None: values.get(key, default))
    monkeypatch.setattr(module.db, "set_setting", lambda key, value: values.__setitem__(key, value))
    monkeypatch.setattr(module, "error_registry", ErrorRegistry())
    return values


def test_mangafire_discovers_switch_position_keeps_large_source_id_and_other_preferences(state):
    sid = "6084907896154116083"
    prefs = [{"__typename": "ListPreference"}, {"__typename": "SwitchPreference", "key": "unrelated", "currentValue": False},
             {"__typename": "SwitchPreference", "key": "solve_captcha", "enabled": True, "currentValue": False}]
    calls = []

    async def graphql(query, variables=None):
        calls.append((query, variables))
        if "mutation" in query:
            assert variables == {"input": {"source": sid, "change": {"position": 2, "switchState": True}}}
            prefs[2]["currentValue"] = True
            return {"updateSourcePreference": {"preferences": prefs}}
        assert variables == {"id": sid}
        return {"source": {"preferences": prefs}}

    client = type("Client", (), {"graphql": staticmethod(graphql)})()
    result = asyncio.run(configure_source_captcha(client, [{"id": sid, "name": "MangaFire"}, {"id": "other", "name": "Other"}]))
    assert result[0]["state"] == "enabled" and len(calls) == 3
    assert prefs[1]["currentValue"] is False
    calls.clear()
    asyncio.run(configure_source_captcha(client, [{"id": sid, "name": "MangaFire"}]))
    assert len(calls) == 1  # already enabled: no mutation/source reload


@pytest.mark.parametrize("prefs", [[], [{"__typename": "SwitchPreference", "key": "solve_captcha", "enabled": False}]])
def test_missing_or_disabled_switch_is_not_guessed(state, prefs):
    client = type("Client", (), {"graphql": AsyncMock(return_value={"source": {"preferences": prefs}})})()
    result = asyncio.run(configure_source_captcha(client, [{"id": "1", "name": "MangaFire"}]))
    assert result[0]["state"] == "unsupported" and client.graphql.await_count == 1


def test_preference_failed_confirmation_is_actionable(state):
    prefs = [{"__typename": "SwitchPreference", "key": "solve_captcha", "currentValue": False}]
    client = type("Client", (), {"graphql": AsyncMock(return_value={"source": {"preferences": prefs}})})()
    result = asyncio.run(configure_source_captcha(client, [{"id": "1", "name": "MangaFire"}]))
    assert result[0]["state"] == "unavailable"
    assert module.error_registry.snapshot()["active_count"] == 1


def solver_client():
    runtime = {"flareSolverrEnabled": True, "flareSolverrUrl": "http://solver.invalid:8191", "flareSolverrTimeout": 180}

    async def graphql(query, variables=None):
        if variables:
            patch = variables["input"]["settings"]
            assert set(patch) == {"flareSolverrEnabled", "flareSolverrTimeout"}
            runtime.update(patch)
            return {"setSettings": {"settings": dict(runtime)}}
        return {"settings": dict(runtime)}

    return type("Client", (), {"graphql": staticmethod(graphql)})(), runtime


def test_solver_health_failure_then_restart_retains_intent_and_recovers(state, monkeypatch):
    status = [503]

    async def health(self, url, **kwargs):
        assert url == "http://solver.invalid:8191/health"
        return httpx.Response(status[0], json={"status": "ok"}, request=httpx.Request("GET", url))

    monkeypatch.setattr(httpx.AsyncClient, "get", health)
    client, runtime = solver_client()
    result = asyncio.run(configure_cloudflare(client, {}))
    assert result["state"] == "unavailable" and runtime["flareSolverrEnabled"] is False
    assert runtime["flareSolverrTimeout"] == 30
    assert state["source_solver_gate_v1"]["enabled"] is True
    status[0] = 200
    # A new env dict represents restarting after the JVM persisted the temporary
    # disable. An explicit check bypasses the retained automatic retry deadline.
    result = asyncio.run(configure_cloudflare(client, {}, force=True))
    assert result["state"] == "ready" and runtime["flareSolverrEnabled"] is True
    assert not state["source_solver_gate_v1"]
    assert module.error_registry.snapshot()["active_count"] == 0


def test_explicit_disabled_solver_does_not_make_network_request(state, monkeypatch):
    health = AsyncMock(side_effect=AssertionError("no solver call"))
    monkeypatch.setattr(httpx.AsyncClient, "get", health)
    client, runtime = solver_client()
    result = asyncio.run(configure_cloudflare(client, {"FLARESOLVERR_ENABLED": "false"}))
    assert result["state"] == "not_configured" and runtime["flareSolverrEnabled"] is False
    health.assert_not_awaited()


def test_timeout_reserves_runtime_overhead(monkeypatch):
    monkeypatch.setattr(settings, "source_request_timeout", 20)
    assert solver_timeout("180") == 9


@pytest.mark.parametrize("message,kind", [("Cloudflare bypass currently disabled", "captcha"), ("HTTP error 403", "access_denied"), ("HTTP error 521", "origin_unavailable"), ("chapter id 429 unavailable", "source_error")])
def test_failure_classification(message, kind):
    assert source_error_kind(message) == kind


def test_rate_limit_retry_after_is_not_shortened():
    response = httpx.Response(429, headers={"retry-after": "300"}, request=httpx.Request("GET", "https://source.invalid"))
    error = httpx.HTTPStatusError("Rate limited", request=response.request, response=response)
    assert chapter_retry_delay(error) == 300


def test_rate_limit_immediately_pauses_source_for_retry_after(monkeypatch):
    import time
    from app.service import AutoSourcarrService
    service = AutoSourcarrService()
    response = httpx.Response(429, headers={"retry-after": "600"}, request=httpx.Request("GET", "https://source.invalid"))
    error = httpx.HTTPStatusError("Rate limited", request=response.request, response=response)
    service._record_source_result("limited", error)
    assert service._source_in_cooldown("limited")
    assert service._source_failures["limited"][1] - time.monotonic() > 599
