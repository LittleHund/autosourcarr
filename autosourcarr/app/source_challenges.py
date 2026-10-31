"""Use supported runtime/extension challenge handlers without retry storms."""
from __future__ import annotations

import asyncio
import hashlib
import time
from urllib.parse import urlsplit

import httpx

from .config import settings
from .async_utils import run_blocking
from .db import db
from .error_registry import error_registry

PREFERENCES = "__typename ... on SwitchPreference { key title enabled currentValue default }"
PROBE_KEY = "source_solver_probe_v1"
PROBE_DELAYS = (60, 120, 300, 900, 1800, 3600)


def solver_endpoint(url: str) -> str:
    """Report the destination without leaking URL credentials or query tokens."""
    try:
        parsed = urlsplit(url)
        if parsed.scheme not in {"http", "https"} or not parsed.hostname:
            return "Invalid or missing solver URL"
        host = parsed.hostname
        if ":" in host:
            host = "[" + host + "]"
        return f"{parsed.scheme}://{host}" + (f":{parsed.port}" if parsed.port else "")
    except ValueError:
        return "Invalid solver URL"


async def probe_solver(url: str) -> dict:
    endpoint = solver_endpoint(url)
    try:
        parsed = urlsplit(url)
        if (parsed.scheme not in {"http", "https"} or not parsed.hostname
                or parsed.query or parsed.fragment or parsed.port == 0):
            raise ValueError("Invalid solver URL")
        if settings.source_request_timeout <= 11:
            return {"failure_kind": "request_budget", "detail": "The source request timeout leaves no solver execution budget.",
                    "action": "Set SOURCE_REQUEST_TIMEOUT above 11 seconds before enabling the solver."}
        # A slow health response is different from an unreachable service. This
        # background probe never delays HTTP startup or increases source budgets.
        async with httpx.AsyncClient(timeout=httpx.Timeout(5.0, connect=2.0), trust_env=False) as probe:
            response = await asyncio.wait_for(probe.get(url + "/health"), timeout=8.0)
            response.raise_for_status()
            payload = response.json()
            if not isinstance(payload, dict) or payload.get("status") != "ok":
                return {"failure_kind": "not_ready", "detail": f"{endpoint} /health did not report status=ok.",
                        "action": "Check the FlareSolverr service logs and confirm this address belongs to FlareSolverr."}
        return {}
    except httpx.ConnectTimeout:
        kind, detail = "connect_timeout", "could not establish a connection within 2 seconds"
    except httpx.ReadTimeout:
        return {"failure_kind": "read_timeout", "detail": f"{endpoint} /health did not send a response within 5 seconds.",
                "action": "Check FlareSolverr load and logs. The service may be stalled or busy; a health timeout is not a website CAPTCHA."}
    except httpx.ConnectError:
        kind, detail = "connection_failed", "could not connect (check DNS, port and whether the service is running)"
    except httpx.HTTPStatusError as exc:
        status = exc.response.status_code
        return {"failure_kind": "http_error", "upstream_status": status,
                "detail": f"{endpoint} /health returned HTTP {status}.",
                "action": "Check the solver URL and any proxy authentication; the endpoint must return FlareSolverr health JSON."}
    except (ValueError, httpx.InvalidURL):
        return {"failure_kind": "invalid_configuration_or_response", "detail": f"{endpoint}: invalid base URL or non-JSON health response.",
                "action": "Use the FlareSolverr HTTP(S) base URL, without /v1, query parameters or a fragment, in FLARESOLVERR_URL."}
    except (httpx.HTTPError, asyncio.TimeoutError):
        return {"failure_kind": "health_request_failed", "detail": f"{endpoint} /health failed within the bounded 8-second probe.",
                "action": "Check the FlareSolverr service and its network/proxy configuration."}
    return {"failure_kind": kind, "detail": f"{endpoint} /health {detail}.",
            "action": "Confirm FlareSolverr is running and FLARESOLVERR_URL is reachable from the AutoSourcarr container. Check the address, port and Docker network."}


def solver_timeout(value: str = "30") -> int:
    try:
        requested = int(value)
    except (TypeError, ValueError):
        requested = 30
    # The JVM's solver is serialized. It must finish before our HTTP client gives up.
    return max(1, min(requested, 30, int(settings.source_request_timeout) - 11))


async def configure_cloudflare(client, env: dict, *, force: bool = False) -> dict:
    retained = await asyncio.wait_for(client.graphql(
        "query AutoSourcarrChallengeConfiguration { settings { flareSolverrEnabled flareSolverrUrl flareSolverrTimeout } }"
    ), timeout=4.0)
    retained = retained.get("settings") or {}
    marker_key = "source_solver_gate_v1"
    url = str(env.get("FLARESOLVERR_URL", retained.get("flareSolverrUrl")) or "").rstrip("/")
    fingerprint = hashlib.sha256(url.encode()).hexdigest()
    marker = await run_blocking(db.setting, marker_key, {}) or {}
    if "FLARESOLVERR_ENABLED" not in env and marker.get("url_hash") == fingerprint:
        # A guarded disable is persisted by the JVM. Restore its recorded intent
        # after restart; explicit env configuration always takes precedence.
        retained["flareSolverrEnabled"] = bool(marker.get("enabled"))
    # Remember the intended retained setting across temporary health failures; our
    # runtime gate must not become the next attempt's desired configuration.
    env.setdefault("FLARESOLVERR_ENABLED", str(retained.get("flareSolverrEnabled", False)))
    env.setdefault("FLARESOLVERR_URL", str(retained.get("flareSolverrUrl") or ""))
    env.setdefault("FLARESOLVERR_TIMEOUT", str(retained.get("flareSolverrTimeout") or 30))
    enabled = str(env.get("FLARESOLVERR_ENABLED", retained.get("flareSolverrEnabled", False))).lower() == "true"
    url = str(env.get("FLARESOLVERR_URL", retained.get("flareSolverrUrl")) or "").rstrip("/")
    timeout = solver_timeout(env.get("FLARESOLVERR_TIMEOUT", retained.get("flareSolverrTimeout", 30)))
    state = {"state": "not_configured", "message": "No Cloudflare solver configured; use source WebView when verification is required."}
    ready = False
    attempted = False
    if enabled:
        previous = await run_blocking(db.setting, PROBE_KEY, {}) or {}
        if previous.get("url_hash") != fingerprint:
            previous = {}
        now = time.time()
        if not force and previous.get("next_probe_at", 0) > now:
            state = {**previous["diagnostic"], "next_probe_at": min(previous["next_probe_at"], now + 3600)}
        else:
            attempted = True
            failure = await probe_solver(url)
            ready = not failure
            if failure:
                failures = min(int(previous.get("failures", 0)) + 1, len(PROBE_DELAYS))
                state = {"state": "unavailable", "message": "Cloudflare solver health check failed. Automatic Cloudflare handling is paused; use WebView if a source requests verification.",
                         "endpoint": solver_endpoint(url), **failure,
                         "next_probe_at": time.time() + PROBE_DELAYS[failures - 1]}
                await run_blocking(db.set_setting, PROBE_KEY, {"url_hash": fingerprint, "failures": failures,
                                   "next_probe_at": state["next_probe_at"], "diagnostic": state})
        if ready:
            state = {"state": "ready", "message": "Automatic Cloudflare handling available through the configured solver; unhandled challenges need WebView.", "timeout_seconds": timeout}
            state["endpoint"] = solver_endpoint(url)
    # Never replace the user's solver URL/session/cookies. Only gate execution and
    # bound its existing timeout; preserve their response-fallback preference.
    if enabled and not ready:
        await run_blocking(db.set_setting, marker_key, {"url_hash": fingerprint, "enabled": True})
    data = await asyncio.wait_for(client.graphql(
        "mutation AutoSourcarrChallengeSettings($input:SetSettingsInput!) { setSettings(input:$input) { settings { flareSolverrEnabled flareSolverrTimeout } } }",
        {"input": {"settings": {"flareSolverrEnabled": ready, "flareSolverrTimeout": timeout}}},
    ), timeout=4.0)
    applied = (data.get("setSettings") or {}).get("settings") or {}
    if applied.get("flareSolverrEnabled") is not ready or int(applied.get("flareSolverrTimeout") or 0) != timeout:
        raise RuntimeError("Runtime did not confirm Cloudflare configuration")
    if ready or not enabled:
        await run_blocking(db.set_setting, marker_key, {})
        await run_blocking(db.set_setting, PROBE_KEY, {})
    if state["state"] == "unavailable":
        # Reusing a retained cooldown is not another failed attempt. Restore the
        # warning once after restart, without inflating occurrence counts.
        active = any(row.get("key") == "cloudflare-solver" for row in error_registry.snapshot(limit=100, include_resolved=False)["items"])
        if attempted or not active:
            error_registry.record("Cloudflare solver", state["message"], key="cloudflare-solver", severity="warning", status_code=503,
                                  error_type="ChallengeSolverUnavailable", detail=state.get("detail", "") + " " + state.get("action", ""))
    else:
        error_registry.resolve(key="cloudflare-solver")
    return state


async def configure_source_captcha(client, sources: list[dict]) -> list[dict]:
    results = []
    for source in sources:
        # Explicitly supported preference; never guess a switch in another source.
        if str(source.get("name") or "").replace(" ", "").casefold() != "mangafire":
            continue
        sid = str(source["id"])
        result = {"source_id": sid, "source_name": "MangaFire", "state": "unsupported", "changed": False}
        key = "source-captcha-setting:" + sid
        try:
            async def read():
                data = await asyncio.wait_for(client.graphql(
                    f"query AutoSourcarrCaptchaPreference($id:LongString!) {{ source(id:$id) {{ preferences {{ {PREFERENCES} }} }} }}",
                    {"id": sid},
                ), timeout=4.0)
                return (data.get("source") or {}).get("preferences") or []

            preferences = await read()
            index = next((i for i, pref in enumerate(preferences) if pref.get("__typename") == "SwitchPreference" and pref.get("key") == "solve_captcha"), None)
            if index is None or preferences[index].get("enabled") is False:
                result["message"] = "Installed MangaFire version has no available automatic CAPTCHA switch; update its extension or use WebView."
            else:
                if preferences[index].get("currentValue") is not True:
                    await asyncio.wait_for(client.graphql(
                        f"mutation AutoSourcarrEnableCaptcha($input:UpdateSourcePreferenceInput!) {{ updateSourcePreference(input:$input) {{ preferences {{ {PREFERENCES} }} }} }}",
                        {"input": {"source": sid, "change": {"position": index, "switchState": True}}},
                    ), timeout=4.0)
                    preferences = await read()
                    result["changed"] = True
                if not any(pref.get("key") == "solve_captcha" and pref.get("currentValue") is True for pref in preferences):
                    raise RuntimeError("Source did not confirm automatic CAPTCHA handling")
                result.update(state="enabled", message="Extension automatic WAF/CAPTCHA handling enabled; failed verification still needs WebView.")
                error_registry.resolve(key=key)
        except Exception as exc:
            result.update(state="unavailable", message="Could not enable the source's automatic CAPTCHA setting. Retry setup or use WebView.")
            error_registry.record("Source verification", result["message"], key=key, severity="warning", error_type="CaptchaPreferenceUnavailable", detail=str(exc), source_context={"source_id": sid, "source_name": "MangaFire"})
        results.append(result)
    return results
