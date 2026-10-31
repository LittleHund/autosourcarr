from stat import S_ISREG
import asyncio
import json
import time
import threading
from collections import OrderedDict
from pathlib import Path
from typing import Awaitable, Callable

from .config import settings
from .async_utils import run_sync
from .image_safety import MAX_IMAGE_BYTES


Loader = Callable[[], Awaitable[tuple[bytes, str]]]


class CoverCache:
    """Small persistent cover cache.

    Source thumbnail endpoints can be unexpectedly slow when an upstream site,
    Cloudflare, or FlareSolverr is unhealthy.  Covers must never hold up manga
    browsing, so the request path waits only briefly and lets a background task
    finish populating the cache.
    """

    def __init__(self, *, max_pending: int = 64, loader_deadline: float | None = None) -> None:
        self.root = settings.data_dir / "cover-cache"
        self.root.mkdir(parents=True, exist_ok=True)
        self._tasks: dict[str, asyncio.Task] = {}
        self._write_lock = threading.RLock()
        self._state_lock = threading.RLock()
        self._generations: dict[str, object] = {}
        self._failed_until: OrderedDict[str, float] = OrderedDict()
        self._clearing = False
        self._presence: OrderedDict[tuple[str, str], tuple[float, bool, float]] = OrderedDict()
        self._presence_epoch = 0
        self._lock = asyncio.Lock()
        self._loader_semaphore = asyncio.Semaphore(max(1, int(settings.cover_fetch_concurrency)))
        self._max_pending = max(1, int(max_pending))
        self._loader_deadline = max(.01, float(loader_deadline if loader_deadline is not None else 30.0))
        self._closed = False

    def _paths(self, key: str) -> tuple[Path, Path]:
        safe = "".join(c if c.isalnum() or c in "-_." else "_" for c in key)
        return self.root / f"{safe}.img", self.root / f"{safe}.json"

    def read(self, key: str) -> tuple[bytes, str] | None:
        image, meta = self._paths(key)
        try:
            stat = image.stat()
            if time.time() - stat.st_mtime > settings.cover_cache_ttl_seconds:
                return None
            content_type = "image/jpeg"
            if meta.exists():
                metadata = json.loads(meta.read_text("utf-8"))
                if not isinstance(metadata, dict):
                    return None
                content_type = metadata.get("content_type") or content_type
                if not isinstance(content_type, str):
                    return None
            return image.read_bytes(), content_type
        except (OSError, ValueError, json.JSONDecodeError):
            return None

    def write(self, key: str, content: bytes, content_type: str) -> None:
        if len(content) > MAX_IMAGE_BYTES:
            raise ValueError("Cover exceeds the 32 MiB cache limit")
        if not content:
            return
        with self._write_lock:
            image, meta = self._paths(key)
            tmp = image.with_suffix(".tmp")
            meta_tmp = meta.with_suffix(".json.tmp")
            try:
                tmp.write_bytes(content)
                meta_tmp.write_text(json.dumps({"content_type": content_type or "image/jpeg"}), "utf-8")
                tmp.replace(image)
                meta_tmp.replace(meta)
                with self._state_lock:
                    self._presence_epoch += 1
                    ttl = float(settings.cover_cache_ttl_seconds)
                    self._remember_presence(key, True, min(30.0, ttl), ttl=ttl)
            finally:
                tmp.unlink(missing_ok=True)
                meta_tmp.unlink(missing_ok=True)

    def _write_current(self, key: str, generation: object, content: bytes, content_type: str) -> bool:
        with self._write_lock:
            with self._state_lock:
                if self._generations.get(key) is not generation:
                    return False
            self.write(key, content, content_type)
            return True

    def _remember_presence(self, key: str, present: bool, seconds: float, *, ttl: float | None = None) -> None:
        cache_key = (str(self.root), str(key))
        self._presence[cache_key] = (time.monotonic() + max(0.0, seconds), present, float(settings.cover_cache_ttl_seconds) if ttl is None else ttl)
        self._presence.move_to_end(cache_key)
        while len(self._presence) > 4096:
            self._presence.popitem(last=False)

    def is_cached(self, key: str) -> bool:
        """Bounded UI-only freshness hint, not an image-serving cache.

        Repeated 500-card heartbeats should not stat 500 files on a NAS. Owned
        writes/deletions invalidate immediately; out-of-process changes are seen
        within 30 seconds (missing covers within 5). Actual image reads still
        validate their file and TTL every time.
        """
        cache_key = (str(self.root), str(key))
        now = time.monotonic()
        ttl = float(settings.cover_cache_ttl_seconds)
        with self._state_lock:
            known = self._presence.get(cache_key)
            if known and known[0] > now and known[2] == float(settings.cover_cache_ttl_seconds):
                self._presence.move_to_end(cache_key)
                return known[1]
            epoch = self._presence_epoch
        image, _meta = self._paths(key)
        remaining = 0.0
        try:
            stat = image.stat()
            remaining = ttl - (time.time() - stat.st_mtime)
            present = S_ISREG(stat.st_mode) and stat.st_size > 0 and remaining >= 0
        except OSError:
            present = False
        with self._state_lock:
            # A slow stat must not overwrite a just-completed write or clear.
            if epoch == self._presence_epoch:
                self._remember_presence(key, present, min(30.0, remaining) if present else 5.0, ttl=ttl)
            else:
                known = self._presence.get(cache_key)
                if known and known[0] > time.monotonic() and known[2] == float(settings.cover_cache_ttl_seconds):
                    return known[1]
                return False
        return present

    async def read_async(self, key: str) -> tuple[bytes, str] | None:
        # Cached covers can be hundreds of KiB. Keep filesystem reads off the
        # asyncio event loop when a Suwayomi grid requests many cards at once.
        return await asyncio.to_thread(self.read, key)

    async def write_async(self, key: str, content: bytes, content_type: str) -> None:
        await run_sync(self.write, key, content, content_type)

    def _clear_keys_sync(self, keys: list[str]) -> int:
        removed = 0
        for key in keys:
            for path in self._paths(key):
                try:
                    path.unlink()
                    removed += 1
                except FileNotFoundError:
                    pass
                except OSError:
                    pass
        return removed

    def clear_keys_sync(self, keys) -> int:
        """Best-effort synchronous invalidation for catalog mutation paths.

        Async request handlers should prefer ``clear_keys`` because it also cancels
        in-flight loaders. This variant is intentionally tiny and safe to call from
        synchronous database maintenance code so a removed source record cannot
        leave its old unified/source image on disk until TTL expiry.
        """
        unique = list(dict.fromkeys(str(key) for key in keys if key))
        with self._write_lock:
            with self._state_lock:
                for key in unique:
                    self._generations.pop(key, None)
                    self._failed_until.pop(key, None)
                    self._presence.pop((str(self.root), key), None)
                self._presence_epoch += 1
            removed = self._clear_keys_sync(unique)
            with self._state_lock:
                self._presence_epoch += 1
                for key in unique:
                    self._remember_presence(key, False, 5.0)
            return removed

    async def clear_keys(self, keys) -> int:
        """Invalidate selected cached covers, including in-flight loaders."""
        unique = list(dict.fromkeys(str(key) for key in keys if key))
        async with self._lock:
            tasks = []
            for key in unique:
                task = self._tasks.pop(key, None)
                if task is not None and not task.done():
                    task.cancel()
                    tasks.append(task)
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)
        return await run_sync(self.clear_keys_sync, unique)

    def _clear_all_sync(self) -> int:
        with self._state_lock:
            self._presence.clear()
            self._presence_epoch += 1
        removed = 0
        try:
            paths = list(self.root.glob("*.img")) + list(self.root.glob("*.json")) + list(self.root.glob("*.tmp"))
        except OSError:
            paths = []
        for path in paths:
            try:
                path.unlink()
                removed += 1
            except FileNotFoundError:
                pass
            except OSError:
                pass
        with self._state_lock:
            self._presence.clear()
            self._presence_epoch += 1
        return removed

    async def clear_all(self) -> int:
        """Drop cached covers after every owned writer/loader has stopped."""
        self._clearing = True
        try:
            async with self._lock:
                tasks = [task for task in self._tasks.values() if not task.done()]
                self._tasks.clear()
                with self._state_lock:
                    self._generations.clear()
                    self._failed_until.clear()
            for task in tasks:
                task.cancel()
            if tasks:
                await asyncio.gather(*tasks, return_exceptions=True)
            return await run_sync(self._clear_all_sync)
        finally:
            self._clearing = False

    async def close(self) -> None:
        """Cancel outstanding background cover fetches during application shutdown."""
        async with self._lock:
            self._closed = True
            tasks = [task for task in self._tasks.values() if not task.done()]
        for task in tasks:
            task.cancel()
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)

    async def _run_loader(self, key: str, loader: Loader, generation: object) -> tuple[bytes, str] | None:
        try:
            # Browsing can request dozens of covers at once. Serializing this to a
            # small worker pool avoids turning the private source host into a burst
            # of thumbnail requests that triggers 429/Cloudflare failures.
            async with asyncio.timeout(self._loader_deadline):
                async with self._loader_semaphore:
                    content, content_type = await loader()
            if not content:
                raise ValueError("Empty cover response")
            if not await run_sync(self._write_current, key, generation, content, content_type):
                return None
            return content, content_type
        except Exception:
            with self._state_lock:
                if self._generations.get(key) is generation:
                    self._failed_until[key] = time.monotonic() + 30.0
                    self._failed_until.move_to_end(key)
                    while len(self._failed_until) > 2048:
                        self._failed_until.popitem(last=False)
            return None
        finally:
            async with self._lock:
                current = self._tasks.get(key)
                if current is asyncio.current_task():
                    self._tasks.pop(key, None)
                with self._state_lock:
                    if self._generations.get(key) is generation:
                        self._generations.pop(key, None)

    async def get(self, key: str, loader: Loader, wait: float | None = None) -> tuple[bytes, str] | None:
        cached = await self.read_async(key)
        if cached:
            return cached

        async with self._lock:
            if self._clearing or self._closed:
                return None
            with self._state_lock:
                if self._failed_until.get(key, 0.0) > time.monotonic():
                    return None
                self._failed_until.pop(key, None)
            task = self._tasks.get(key)
            if task is None or task.done():
                if len(self._tasks) >= self._max_pending:
                    # Local admission deferral is not a source failure or a
                    # negative cache observation. A later request may try again.
                    return None
                generation = object()
                with self._state_lock:
                    self._generations[key] = generation
                task = asyncio.create_task(self._run_loader(key, loader, generation))
                self._tasks[key] = task

        wait_for = settings.cover_inline_wait if wait is None else max(0.0, float(wait))
        if wait_for <= 0:
            return None
        try:
            result = await asyncio.wait_for(asyncio.shield(task), timeout=wait_for)
            return result or await self.read_async(key)
        except asyncio.TimeoutError:
            return None


cover_cache = CoverCache()
