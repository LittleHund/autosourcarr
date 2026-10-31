from __future__ import annotations

import asyncio
import threading
import time
import uuid
from dataclasses import dataclass, asdict
from datetime import datetime, timezone
from typing import Any, Callable


def _iso_now() -> str:
    return datetime.now(timezone.utc).isoformat()


@dataclass
class TaskRecord:
    id: str
    name: str
    state: str = "running"
    current: int = 0
    total: int = 0
    message: str = ""
    created_at: str = ""
    started_at: str = ""
    updated_at: str = ""
    finished_at: str | None = None
    error: str = ""
    cancellable: bool = False
    cancel_requested: bool = False
    activity: str = "running"
    waiting_since: str = ""
    context: str = ""
    origin: str = "automatic"
    kind: str = ""
    interrupted: bool = False


class TaskRegistry:
    """User-visible orchestration/progress registry with cooperative cancellation.

    A task can bind either its asyncio runtime task or a small cancellation callback.
    Only bound jobs are exposed as cancellable; quick request-tracking rows stay
    visible but do not pretend they can be interrupted safely.
    """

    def __init__(self) -> None:
        self._lock = threading.RLock()
        self._tasks: dict[str, TaskRecord] = {}
        self._runtime_tasks: dict[str, asyncio.Task] = {}
        self._cancel_callbacks: dict[str, Callable[[], None]] = {}
        self._journal = None

    def _changed_locked(self):
        if self._journal is not None:
            self._journal.changed()

    def create(self, name: str, *, total: int = 0, message: str = "", activity: str = "running", context: str = "", origin: str = "automatic", kind: str = "") -> str:
        if activity not in {"running", "queued", "waiting"}:
            raise ValueError("Invalid task activity")
        if origin not in {"manual", "automatic"}:
            raise ValueError("Invalid task origin")
        now = _iso_now()
        task_id = uuid.uuid4().hex[:12]
        record = TaskRecord(
            id=task_id,
            name=str(name).strip() or "Background task",
            current=0,
            total=max(0, int(total)),
            message=str(message or ""),
            created_at=now,
            started_at=now if activity == "running" else "",
            updated_at=now,
            activity=activity,
            waiting_since=now if activity != "running" else "",
            context=str(context or "")[:240],
            origin=origin,
            kind=str(kind or '')[:64],
        )
        with self._lock:
            self._tasks[task_id] = record
            self._prune_locked()
            self._changed_locked()
        return task_id


    def mark_waiting(self, task_id: str, *, message: str, queued: bool = False) -> None:
        """Describe work waiting for a dependency without claiming it is executing.

        Lifecycle state stays 'running' for compatibility with cancellation and old
        clients. Repeated queue messages must not restart the wait-time clock.
        """
        with self._lock:
            record = self._tasks.get(str(task_id))
            if not record or record.state != "running":
                return
            if not record.waiting_since:
                record.waiting_since = _iso_now()
            record.activity = "queued" if queued else "waiting"
            record.message = str(message)
            record.updated_at = _iso_now()
            self._changed_locked()

    def mark_active(self, task_id: str, *, reset_estimate: bool = True, message: str | None = None) -> None:
        """Mark queued work as actively executing and start/restart ETA timing.

        Task creation time is useful for ordinary jobs that begin immediately, while
        scheduler-backed jobs can spend minutes waiting in a queue.  Those jobs call
        this method when they actually acquire their execution lane so queue time does
        not distort the remaining-time estimate.
        """
        with self._lock:
            record = self._tasks.get(str(task_id))
            if not record or record.state != "running":
                return
            record.activity = "running"
            record.waiting_since = ""
            if reset_estimate or not record.started_at:
                record.started_at = _iso_now()
            if message is not None:
                record.message = str(message)
            record.updated_at = _iso_now()
            self._changed_locked()

    @staticmethod
    def _timing_fields(record: TaskRecord) -> dict[str, Any]:
        waiting = record.activity in {"queued", "waiting"}
        start_raw = (record.waiting_since or record.created_at) if waiting else (record.started_at or record.created_at)
        if not start_raw:
            return {"elapsed_seconds": 0, "eta_seconds": None, "rate_per_second": None}
        try:
            started = datetime.fromisoformat(start_raw)
            if started.tzinfo is None:
                started = started.replace(tzinfo=timezone.utc)
            if record.finished_at:
                ended = datetime.fromisoformat(record.finished_at)
                if ended.tzinfo is None:
                    ended = ended.replace(tzinfo=timezone.utc)
            else:
                ended = datetime.now(timezone.utc)
            elapsed = max(0.0, (ended - started).total_seconds())
        except Exception:
            elapsed = 0.0
        current = max(0, int(record.current or 0))
        total = max(0, int(record.total or 0))
        rate = None
        eta = None
        if record.state == "running" and not waiting and total > 0 and current > 0 and elapsed >= 0.25:
            rate = current / elapsed
            if current >= total:
                eta = 0.0
            elif rate > 0:
                eta = min(30.0 * 24.0 * 3600.0, max(0.0, (total - current) / rate))
        return {
            "elapsed_seconds": int(round(elapsed)) if not waiting else 0,
            "waiting_seconds": int(round(elapsed)) if waiting else 0,
            "eta_seconds": int(round(eta)) if eta is not None else None,
            "rate_per_second": round(float(rate), 6) if rate is not None else None,
        }

    def bind_runtime(self, task_id: str, task: asyncio.Task | None = None) -> None:
        task = task or asyncio.current_task()
        if task is None:
            return
        with self._lock:
            record = self._tasks.get(str(task_id))
            if not record or record.state != "running":
                return
            self._runtime_tasks[str(task_id)] = task
            record.cancellable = True
            record.updated_at = _iso_now()
            self._changed_locked()
        def runtime_done(completed: asyncio.Task) -> None:
            # A task cancelled before its coroutine first runs cannot execute its
            # own except/finally. Never leave that queued row permanently running.
            if completed.cancelled():
                self.cancel(str(task_id))
            else:
                error = completed.exception()
                if error is not None:
                    self.fail(str(task_id), error)
        task.add_done_callback(runtime_done)

    def bind_canceller(self, task_id: str, callback: Callable[[], None]) -> None:
        with self._lock:
            record = self._tasks.get(str(task_id))
            if not record or record.state != "running":
                return
            self._cancel_callbacks[str(task_id)] = callback
            record.cancellable = True
            record.updated_at = _iso_now()
            self._changed_locked()

    def is_running(self, task_id: str) -> bool:
        with self._lock:
            record = self._tasks.get(str(task_id))
            return bool(record and record.state == "running")

    def cancel_requested(self, task_id: str) -> bool:
        with self._lock:
            record = self._tasks.get(str(task_id))
            return bool(record and record.cancel_requested)

    def update(self, task_id: str, *, current: int | None = None, total: int | None = None, message: str | None = None) -> None:
        with self._lock:
            record = self._tasks.get(str(task_id))
            if not record or record.state != "running":
                return
            if current is not None:
                record.current = max(0, int(current))
            if total is not None:
                record.total = max(0, int(total))
            if message is not None:
                record.message = str(message)
            record.updated_at = _iso_now()
            self._changed_locked()

    def advance(self, task_id: str, amount: int = 1, *, message: str | None = None) -> None:
        with self._lock:
            record = self._tasks.get(str(task_id))
            if not record or record.state != "running":
                return
            record.current = max(0, record.current + int(amount))
            if record.total and record.current > record.total:
                record.current = record.total
            if message is not None:
                record.message = str(message)
            record.updated_at = _iso_now()
            self._changed_locked()

    def complete(self, task_id: str, *, message: str | None = None) -> None:
        with self._lock:
            record = self._tasks.get(str(task_id))
            if not record or record.state != "running":
                return
            if record.total:
                record.current = record.total
            if message is not None:
                record.message = str(message)
            record.state = "completed"
            record.finished_at = _iso_now()
            record.updated_at = record.finished_at
            self._changed_locked()
            record.cancellable = False
            self._runtime_tasks.pop(str(task_id), None)
            self._cancel_callbacks.pop(str(task_id), None)
            self._prune_locked()

    def fail(self, task_id: str, error: BaseException | str, *, message: str | None = None) -> None:
        with self._lock:
            record = self._tasks.get(str(task_id))
            if not record or record.state != "running":
                return
            record.state = "failed"
            record.error = str(error) or (type(error).__name__ if isinstance(error, BaseException) else "Task failed")
            if message is not None:
                record.message = str(message)
            record.finished_at = _iso_now()
            record.updated_at = record.finished_at
            self._changed_locked()
            record.cancellable = False
            self._runtime_tasks.pop(str(task_id), None)
            self._cancel_callbacks.pop(str(task_id), None)
            self._prune_locked()

    def cancel(self, task_id: str, *, message: str = "Cancelled by user") -> None:
        """Finish a cancelled task in the Failed section as requested by the UI."""
        with self._lock:
            record=self._tasks.get(str(task_id))
            if record and record.state=='running': record.cancel_requested=True
        self.fail(task_id, message, message=message)

    def request_cancel(self, task_id: str) -> bool:
        runtime: asyncio.Task | None = None
        callback: Callable[[], None] | None = None
        with self._lock:
            record = self._tasks.get(str(task_id))
            if not record or record.state != "running" or not record.cancellable:
                return False
            record.cancel_requested = True
            record.message = "Cancelling…"
            record.updated_at = _iso_now()
            self._changed_locked()
            runtime = self._runtime_tasks.get(str(task_id))
            callback = self._cancel_callbacks.get(str(task_id))
        if callback is not None:
            try:
                callback()
            except Exception:
                pass
        if runtime is not None and not runtime.done():
            loop = runtime.get_loop()
            if not loop.is_closed():
                loop.call_soon_threadsafe(runtime.cancel)
        # Move it immediately to Failed so the Tasks screen reflects the user's
        # action even if a worker needs a moment to unwind its network request.
        self.fail(task_id, "Cancelled by user", message="Cancelled by user")
        return True

    def clear_history(self) -> dict[str, int]:
        """Remove finished task history while preserving currently running work."""
        with self._lock:
            removable = [
                task_id for task_id, record in self._tasks.items()
                if record.state != "running"
            ]
            completed = sum(1 for task_id in removable if self._tasks[task_id].state == "completed")
            failed = sum(1 for task_id in removable if self._tasks[task_id].state == "failed")
            for task_id in removable:
                self._tasks.pop(task_id, None)
                self._runtime_tasks.pop(task_id, None)
                self._cancel_callbacks.pop(task_id, None)
            self._changed_locked()
        return {"cleared": len(removable), "completed": completed, "failed": failed}

    def snapshot(self) -> dict[str, Any]:
        with self._lock:
            self._prune_locked()
            items = []
            for record in self._tasks.values():
                item = asdict(record)
                item.update(self._timing_fields(record))
                items.append(item)
        running = sorted((x for x in items if x["state"] == "running"),
                         key=lambda x: ({"running": 0, "queued": 1, "waiting": 2}.get(x["activity"], 0), x["created_at"]))
        completed = sorted((x for x in items if x["state"] == "completed"), key=lambda x: x["updated_at"], reverse=True)
        failed = sorted((x for x in items if x["state"] == "failed"), key=lambda x: x["updated_at"], reverse=True)
        return {
            "running": len(running),  # ongoing total, retained for old clients
            "active": sum(x["activity"] == "running" for x in running),
            "queued": sum(x["activity"] == "queued" for x in running),
            "waiting": sum(x["activity"] == "waiting" for x in running),
            "completed": len(completed),
            "failed": len(failed),
            "items": running + completed[:20] + failed[:20],
            "history_error": self._journal.error if self._journal is not None else '',
            "durable": bool(self._journal is not None and self._journal.available),
        }

    def _prune_locked(self) -> None:
        now = time.time()
        stale: list[str] = []
        for task_id, record in self._tasks.items():
            if record.state == "running" or not record.finished_at:
                continue
            try:
                ended = datetime.fromisoformat(record.finished_at).timestamp()
            except Exception:
                ended = now
            if now - ended > 7 * 86400:
                stale.append(task_id)
        finished=sorted((r for r in self._tasks.values() if r.state!='running' and r.id not in stale),
                        key=lambda r:r.updated_at,reverse=True)
        stale.extend(r.id for r in finished[300:])
        for task_id in stale:
            self._tasks.pop(task_id, None)
            self._runtime_tasks.pop(task_id, None)
            self._cancel_callbacks.pop(task_id, None)

    def history(self, *, offset=0, limit=50):
        with self._lock:
            self._prune_locked()
            rows=sorted((asdict(r) for r in self._tasks.values() if r.state!='running'),
                        key=lambda r:r['updated_at'],reverse=True)
        return {'items':rows[offset:offset+limit],'total':len(rows),'offset':offset,'limit':limit,
                'note':'Up to 300 finished general tasks retained for seven days. Interrupted work is never replayed automatically.'}


task_registry = TaskRegistry()
