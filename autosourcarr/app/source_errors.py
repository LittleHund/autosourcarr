"""Readable source failures without treating a blocked lookup as a search miss."""
from __future__ import annotations

import re
from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
from typing import Any

import httpx


def source_http_status(error: Any) -> int | None:
    status = getattr(getattr(error, "response", None), "status_code", None)
    match = re.search(r"(?:HTTP(?: error)?|status(?: code)?)\s*:?\s*([45]\d\d)\b", str(error or ""), re.I)
    return int(match.group(1)) if match else status


def assistance_reason(error: Any) -> str:
    text = str(error or "").casefold()
    if isinstance(error, httpx.PoolTimeout) or getattr(error, "local_source_deferral", False):
        return ""
    if any(marker in text for marker in ("captcha_required", "/@waf/", "shape-selecting captcha", "waf challenge", "waf block", "web application firewall")):
        return "WAF verification needs your help"
    if any(marker in text for marker in ("cloudflarebypassexception", "failed to bypass cloudflare", "cloudflare challenge", "cloudflare protection", "cf-chl-", "challenge-platform")):
        return "Cloudflare verification needs your help"
    if "captcha" in text or "human verification" in text:
        return "CAPTCHA needs your help"
    return ""


def source_error_kind(error: Any) -> str:
    text = str(error or "").casefold()
    if isinstance(error, httpx.PoolTimeout) or getattr(error, "local_source_deferral", False) or "capacity busy" in text or "request deferred before upstream" in text:
        return "deferred"
    if any(marker in text for marker in (
        "captcha", "cloudflare challenge", "cloudflare protection", "cf-chl-", "challenge-platform",
        "cloudflarebypassexception", "failed to bypass cloudflare", "cloudflare bypass currently disabled", "human verification",
    )):
        return "captcha"
    if source_http_status(error) == 429 or "too many requests" in text or "rate limit" in text:
        return "rate_limit"
    if source_http_status(error) == 403 or "web application firewall" in text or "waf block" in text:
        return "access_denied"
    if source_http_status(error) in {521, 522, 523, 524}:
        return "origin_unavailable"
    if isinstance(error, (TimeoutError, httpx.TimeoutException)) or "timeout" in text or "timed out" in text:
        return "timeout"
    return "source_error"


def source_error_summary(error: Any, source_name: str = "Source") -> str:
    name = " ".join(str(source_name or "Source").split())
    kind = source_error_kind(error)
    if getattr(error, "human_assistance_required", False):
        return str(error)
    if kind == "deferred":
        return f"{name} search deferred by local capacity or a shared search time limit; retry is available."
    if kind == "captcha":
        return f"{name} is blocked by a CAPTCHA; its lookup is deferred, not a 'no results' response."
    if kind == "rate_limit":
        return f"{name} is rate-limited; its lookup is deferred."
    if kind == "access_denied":
        return f"{name} denied access. Inspect this source in WebView; a WAF or access restriction may require manual action."
    if kind == "origin_unavailable":
        return f"{name} website origin is unavailable (HTTP {source_http_status(error)}); retry later or use another source."
    if kind == "timeout":
        return f"{name} lookup timed out; other enabled sources can still be searched."
    text = " ".join(str(error or "Source request failed").split())
    # Suwayomi embeds a whole Java trace in its GraphQL error message, often on
    # one line. Stop before the trace; the full value belongs in diagnostics.
    text = re.split(r"\s+at\s+(?:[\w$]+\.)+[\w$<>]+\(", text, maxsplit=1)[0]
    text = text[:280].rstrip()
    return f"{name} lookup failed: {text}"


def compact_import_reason(reason: str, source_name: str = "Source") -> str:
    """Render legacy stack-trace reasons compactly without changing stored data."""
    reason = str(reason or "")
    if len(reason) > 600 or re.search(r"\s+at\s+(?:[\w$]+\.)+[\w$<>]+\(", reason):
        return source_error_summary(reason, source_name)
    return reason


def chapter_retry_delay(error: Any) -> int:
    """Bound only known per-chapter failures; never cool down an entire source."""
    if source_error_kind(error) == "captcha":
        return 30
    delay = {403: 30, 429: 60, 521: 30, 522: 30, 523: 30, 524: 30}.get(source_http_status(error), 0)
    retry = getattr(getattr(error, "response", None), "headers", {}).get("retry-after", "")
    if delay and retry:
        try:
            delay = max(delay, int(retry))
        except ValueError:
            try:
                deadline = parsedate_to_datetime(retry)
                delay = max(delay, int((deadline - datetime.now(timezone.utc)).total_seconds()) + 1)
            except (TypeError, ValueError, OverflowError):
                pass
    return delay
