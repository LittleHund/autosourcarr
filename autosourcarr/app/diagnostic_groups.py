"""Presentation-only grouping; original registry keys/records own recovery and export."""
from __future__ import annotations

from copy import deepcopy
import hashlib
import json
import re
from urllib.parse import urlsplit


def _route(path: str) -> str:
    try:
        parsed = urlsplit(str(path or ""))
    except ValueError:
        return str(path or "")
    route = parsed.path
    # Only known application identifiers are volatile. Do not erase arbitrary
    # numbers in source URLs/messages (in particular 403 vs 429 vs 521).
    route = re.sub(r"(/api/(?:admin|v1)/chapters/)\d+(?=/|$)", r"\1{id}", route)
    route = re.sub(r"(/api/(?:admin|v1)/entries/)\d+(?=/|$)", r"\1{id}", route)
    route = re.sub(r"(/api/admin/imports/)[A-Za-z0-9_-]+(/items/)\d+(?=/|$)", r"\1{run}\2{id}", route)
    return f"{parsed.scheme}://{parsed.netloc}{route}" if parsed.netloc else route


def _identity(item: dict) -> tuple:
    context = item.get("source_context") or {}
    source = str(context.get("source_id") or "")
    if not source:
        source = str(context.get("source_name") or "").strip().casefold()
    # Older import records used a source-specific key without source_context.
    key = str(item.get("key") or "")
    if not source and key.startswith("import-source:"):
        source = key.removeprefix("import-source:")
    message = " ".join(str(item.get("message") or "Unknown error").split())
    message = re.sub(r"\s*\(error [a-f0-9]{12}\)", "", message, flags=re.I)
    # A source context is required before coalescing variable source traces. For
    # unknown failures preserve the message verbatim apart from whitespace/ID.
    if source:
        message = re.split(r"\s+at\s+(?:[\w$]+\.)+[\w$<>]+\(", message, maxsplit=1)[0]
    message = re.sub(r"then retry in \d+ seconds", "then retry after cooldown", message)
    unknown_source = key if not source and key.startswith("source-host-pages:") else ""
    return (
        unknown_source, bool(item.get("client_side")), str(item.get("component") or "AutoSourcarr"),
        str(item.get("severity") or "error"), int(item.get("status_code") or 0),
        str(item.get("error_type") or ""), bool(item.get("resolved")),
        bool(item.get("pinned")), source, str(item.get("method") or "").upper(),
        _route(str(item.get("path") or "")), message,
    )


def group_diagnostics(items: list[dict]) -> list[dict]:
    """Combine retained records without changing registry state or event counts.

    `items` remains the detailed export/backward-compatible response. Group IDs
    use an original member ID so existing affected-source actions still resolve.
    `group_key` is a stable presentation identity independent of the newest item.
    """
    grouped: dict[tuple, dict] = {}
    for item in items:
        identity = _identity(item)
        group = grouped.get(identity)
        count = max(1, int(item.get("count") or 1))
        if group is None:
            group = deepcopy(item)
            group.update(
                group_key="diagnostic-" + hashlib.sha256(json.dumps(identity).encode()).hexdigest()[:16],
                count=0, record_count=0, member_ids=[], paths=[],
            )
            grouped[identity] = group
        elif str(item.get("last_seen") or item.get("first_seen") or "") > str(group.get("last_seen") or group.get("first_seen") or ""):
            # Latest request supplies a useful WebView target and representative
            # trace. The full original traces remain in snapshot.items/export.
            for field in ("id", "key", "message", "detail", "source_context", "path", "last_seen", "resolved_at", "app_version", "instance_id", "context", "exception_chain", "detail_truncated"):
                if field in item:
                    group[field] = deepcopy(item[field])
                else:
                    group.pop(field, None)
        group["count"] += count
        group["record_count"] += 1
        if item.get("id") not in group["member_ids"]:
            group["member_ids"].append(item.get("id"))
        if item.get("path") and item["path"] not in group["paths"]:
            group["paths"].append(item["path"])
        first = str(item.get("first_seen") or "")
        if first and (not group.get("first_seen") or first < group["first_seen"]):
            group["first_seen"] = first
    return sorted(grouped.values(), key=lambda row: (
        bool(row.get("pinned") and not row.get("resolved")),
        str(row.get("last_seen") or row.get("first_seen") or ""),
    ), reverse=True)
