from __future__ import annotations

from collections import OrderedDict
from datetime import datetime, timezone
from threading import RLock
from typing import Any
import uuid
import json

from .diagnostic_groups import group_diagnostics
from .diagnostics import APP_VERSION, INSTANCE_ID, STARTED_AT, scrub, redact_text, exception_context
from .support_logs import support_logs


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


class ErrorRegistry:
    """Small in-memory diagnostic registry for recent runtime failures.

    Errors are deduplicated by a stable key so repeated reader retries do not grow
    memory or the UI indefinitely. Runtime history intentionally resets when the
    container restarts; durable application data stays in SQLite as before.
    """

    def __init__(self, max_entries: int = 100) -> None:
        self.max_entries = max(20, int(max_entries))
        self._items: OrderedDict[str, dict[str, Any]] = OrderedDict()
        self._lock = RLock()

    def record(
        self,
        component: str,
        message: str,
        *,
        key: str | None = None,
        severity: str = "error",
        path: str = "",
        method: str = "",
        status_code: int = 500,
        error_type: str = "",
        detail: str = "",
        source_context: dict | None = None,
        pinned: bool = False,
        context: dict | None = None,
        exception: BaseException | None = None,
    ) -> str:
        component = str(component or "AutoSourcarr")[:120]
        message = redact_text(message or "Unknown error")[:2000]
        severity = str(severity or "error").lower()
        if severity not in {"error", "warning"}:
            severity = "error"
        signature = redact_text(key or f"{component}|{method}|{path}|{error_type}|{message}")[:1200]
        source_details = {k: str(v)[:500] for k, v in (source_context or {}).items() if k in {"source_id", "source_name", "source_chapter_id", "source_chapter_url"}}
        detail_text = redact_text(detail or "")
        metadata = {"app_version": APP_VERSION, "instance_id": INSTANCE_ID,
                    "context": scrub(context or {}), "exception_chain": exception_context(exception),
                    "detail_truncated": len(detail_text) > 12000}
        source_details = scrub(source_details)
        support_logs.record('errors', json.dumps({'component':component,'message':message,
            'error_type':error_type,'status_code':status_code,'path':path,'method':method,
            'detail':detail_text[:12000],'exception_chain':metadata['exception_chain']},ensure_ascii=False))
        now = _now_iso()
        with self._lock:
            existing = self._items.get(signature)
            if existing is not None:
                existing.update(
                    {
                        "last_seen": now,
                        "message": message,
                        "detail": detail_text[:12000],
                        "source_context": dict(source_details),
                        "status_code": int(status_code or 0),
                        "error_type": str(error_type or "")[:160],
                        "path": redact_text(path or "")[:500],
                        "method": str(method or "")[:16],
                        "severity": severity,
                        "resolved": False,
                    }
                )
                existing.update(metadata)
                existing["count"] = int(existing.get("count") or 0) + 1
                existing["pinned"] = bool(pinned or existing.get("pinned"))
                existing.pop("resolved_at", None)
                self._items.move_to_end(signature)
                return str(existing["id"])

            item = {
                "id": uuid.uuid4().hex[:12],
                "key": signature,
                "component": component,
                "severity": severity,
                "message": message,
                "detail": detail_text[:12000],
                "source_context": dict(source_details),
                "error_type": str(error_type or "")[:160],
                "path": redact_text(path or "")[:500],
                "method": str(method or "")[:16],
                "status_code": int(status_code or 0),
                "first_seen": now,
                "last_seen": now,
                "count": 1,
                "resolved": False,
                "pinned": bool(pinned),
            }
            item.update(metadata)
            self._items[signature] = item
            while len(self._items) > self.max_entries:
                victim = next((k for k, v in self._items.items() if v.get("resolved")), None)
                if victim is None:
                    victim = next((k for k, v in self._items.items() if not v.get("pinned")), next(iter(self._items)))
                self._items.pop(victim)
            return str(item["id"])

    def resolve(self, *, key: str | None = None, component: str | None = None) -> int:
        changed = 0
        now = _now_iso()
        with self._lock:
            for signature, item in self._items.items():
                if key is not None and signature != key:
                    continue
                if component is not None and str(item.get("component")) != str(component):
                    continue
                if not item.get("resolved"):
                    item["resolved"] = True
                    item["resolved_at"] = now
                    changed += 1
        return changed

    def resolve_prefix(self, prefix: str) -> int:
        prefix = str(prefix or "")
        if not prefix:
            return 0
        changed = 0
        now = _now_iso()
        with self._lock:
            for signature, item in self._items.items():
                if not signature.startswith(prefix):
                    continue
                if not item.get("resolved"):
                    item["resolved"] = True
                    item["resolved_at"] = now
                    changed += 1
        return changed

    def clear(self, *, resolved_only: bool = True) -> int:
        with self._lock:
            if not resolved_only:
                count = len(self._items)
                self._items.clear()
                return count
            keys = [key for key, item in self._items.items() if item.get("resolved")]
            for key in keys:
                self._items.pop(key, None)
            return len(keys)

    def snapshot(self, *, limit: int = 50, include_resolved: bool = True) -> dict[str, Any]:
        limit = max(1, min(100, int(limit or 50)))
        with self._lock:
            values = list(self._items.values())
            active = [item for item in values if not item.get("resolved")]
            errors = [item for item in active if item.get("severity") == "error"]
            warnings = [item for item in active if item.get("severity") == "warning"]
            shown = values if include_resolved else active
            shown = sorted(reversed(shown), key=lambda item: not (item.get("pinned") and not item.get("resolved")))[:limit]
            groups = group_diagnostics(values if include_resolved else active)
            active_groups = [row for row in group_diagnostics(active)]
            return {
                "app_version": APP_VERSION, "instance_id": INSTANCE_ID, "started_at": STARTED_AT, "captured_at": _now_iso(),
                "retention_limit": self.max_entries, "omitted_records": len(values if include_resolved else active)-len(shown),
                "groups": groups[:limit],
                "grouped_active_count": len(active_groups),
                "grouped_active_error_count": sum(row.get("severity") == "error" for row in active_groups),
                "grouped_active_warning_count": sum(row.get("severity") == "warning" for row in active_groups),
                "active_count": len(active),
                "active_error_count": len(errors),
                "active_warning_count": len(warnings),
                "history_count": len(values),
                "items": [scrub(item) for item in shown],
            }


error_registry = ErrorRegistry()
