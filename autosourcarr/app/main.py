import asyncio
import httpx
import json
from contextlib import asynccontextmanager
from functools import partial
from pathlib import Path
import secrets
import sys
import re
import uuid
from typing import Annotated, Any
from urllib.parse import unquote

from fastapi import Depends, FastAPI, Header, HTTPException, Query, Request
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse, Response, StreamingResponse
from fastapi.staticfiles import StaticFiles
from starlette.middleware.gzip import GZipMiddleware
from .backup_import import MAX_BACKUP_BYTES
from .read_lane import ReadLane, read_lane
from .request_origin import CrossSiteWriteMiddleware
from .async_utils import run_blocking
from .async_utils import run_sync
from .metadata_catalog import metadata_catalog
from .config import settings
from .source_engine import source_engine
from .source_webview import webview_router
from .cover_cache import cover_cache
from .env_settings import file_path as env_file_path, settings_payload as env_settings_payload, update_env, upsert_env_value
from .db import db
from .error_registry import error_registry
from .diagnostics import runtime_context, scrub, exception_context
from .support_logs import support_logs
from .extension_download import apk_status, apk_bytes, ApkUnavailable
from .models import MixedEntryReviewRequest, RepositoryPreviewRequest, RepositoryChangeRequest, AliasRequest, DatabaseMoveRequest, DuplicateReviewRequest, DuplicateUnmatchRequest, EnvFileUpdate, ExtensionActionRequest, ManualGroupMemberRequest, ManualMatchRequest, PreferredTitleRequest, SeparateSourceRequest, QualityRequest, SourceGroupCreate, SourceGroupUpdate, SourceUpdate, UiPreferencesUpdate
from .quality import analyze_candidate
from .service import service, ReaderBrowsePending
from .source_attention import source_attention
from .source_health import source_health, health_router
from .source_raws import source_raws
from .repair_workflow import repair_workflow
from .source_browser import source_browser, validate_preferences
from .source_maintenance import source_maintenance, maintenance_router, inventory as maintenance_inventory, save_backup as source_package_backup
from .suwayomi import ExtensionInstallUnavailable, HumanAssistanceRequired, SourceEngineUnavailable, SourceWorkDeferred, main_suwayomi, source_host
from .translation import translator
from .task_registry import task_registry
from .task_journal import TaskJournal
from .library_manager import LibraryManager
from .source_repositories import repository_snapshot, preview_repository_async, change_repository, RepositoryConflict
from . import tag_store
from .catalog_views import source_entry_ids, source_browse
from .source_orphans import source_orphans, orphan_router
from . import source_controls
from pydantic import BaseModel, Field
from typing import Literal


# Cover bursts cannot occupy every catalogue-view worker.
cover_read_lane = ReadLane(workers=2)
# Fuzzy whole-vocabulary suggestions cannot occupy every local UI read worker.
tag_suggestion_lane = ReadLane(workers=1)
chapter_stats_lane = ReadLane(workers=2)
diagnostic_read_lane = ReadLane(workers=1)
task_journal = TaskJournal(db, task_registry)
library_manager = LibraryManager(db, service, main_suwayomi)

async def cover_metadata(function, /, *args):
    def build():
        try:
            return function(*args)
        finally:
            db.close_current_thread()
    return await cover_read_lane.run(build)


async def catalogue_response(function, /, *args, lane=read_lane, **kwargs):
    """Build and encode local-view JSON away from the HTTP loop/source worker pool."""
    def build():
        try:
            return JSONResponse(function(*args, **kwargs))
        finally:
            db.close_current_thread()
    return await lane.run(build)


@asynccontextmanager
async def lifespan(app: FastAPI):
    from .internal_services import internal_services
    startup_sync = startup_metadata_repair = startup_catalog_maintenance = startup_merge_recovery = None
    try:
        support_logs.start(settings.data_dir, vars(settings))
        await run_blocking(internal_services.configure)
        internal_services.start()
        await task_journal.start()
        service.resume_background_tasks()
        # If a database move was staged in the previous process, this process has now
        # opened the copied DBs from DATABASE_DIR. Verify them before deleting only the
        # old SQLite files. This never delays network startup by more than local checks.
        # A pending move is verified before old files are cleaned up. Do not hide a
        # relocation verification failure: failing startup is safer than silently
        # opening a stale/new database after a storage mount mistake.
        await run_blocking(db.finalize_database_relocation, settings.settings_config_dir)
        # Do not hold HTTP startup hostage to the private Suwayomi container.  The
        # persistent catalog/feed cache is useful even while dependencies are still
        # starting or temporarily offline.
        async def initial_source_sync() -> None:
            try:
                await asyncio.wait_for(service.sync_sources(), timeout=6.0)
            except Exception:
                pass

        async def initial_metadata_repair() -> None:
            if not settings.metadata_enrichment_on_startup:
                return
            try:
                await asyncio.sleep(max(0.0, float(settings.metadata_enrichment_startup_delay_seconds)))
                service.queue_sparse_metadata_enrichment(settings.metadata_enrichment_startup_limit)
            except asyncio.CancelledError:
                raise
            except Exception:
                pass

        async def initial_catalog_maintenance() -> None:
            # Expensive one-time upgrade cleanup must never delay Uvicorn startup.
            # Give the first page request a head start, then run it off the event loop.
            try:
                await asyncio.sleep(1.0)
                # v0.2.69 reruns the strengthened safety repair after tightening duplicate/source evidence: a
                # historical snowball is detected from its concrete records/alias shape as
                # well. Keep the migration visible in Tasks so the user can verify it ran.
                if str(db.setting("automatic_merge_safety_repair_version", "") or "") != "0.2.69":
                    repair_task_id = task_registry.create(
                        "Repair unsafe duplicate merges",
                        message="Scanning suspicious historical merged entries",
                    )
                    try:
                        task_registry.mark_active(repair_task_id, reset_estimate=True)
                        repair_result = await run_blocking(service.run_automatic_merge_safety_repair)
                        task_registry.complete(
                            repair_task_id,
                            message=(
                                f"Safety repair complete · checked {int(repair_result.get('scanned_candidates') or 0)} suspicious entries"
                                f" + {int((repair_result.get('source_attachment_repair') or {}).get('checked') or 0)} automatic source records"
                                f" · repaired {int(repair_result.get('repaired_entries') or 0) + int((repair_result.get('source_attachment_repair') or {}).get('repaired_entries') or 0)} entries"
                                f" · split {int(repair_result.get('split_records') or 0) + int((repair_result.get('source_attachment_repair') or {}).get('split_records') or 0)} source records"
                            ),
                        )
                    except asyncio.CancelledError:
                        task_registry.cancel(repair_task_id, message="Safety repair cancelled during maintenance/shutdown")
                        raise
                    except Exception as exc:
                        task_registry.fail(repair_task_id, exc, message="Automatic-merge safety repair failed")
                        raise
                await asyncio.sleep(1.0)
                # v0.2.36: one-time migration work is local, but it can still contend for
                # the catalog lock with the first Matrix/Status/entry requests. Wait for
                # a genuinely quiet browser window before touching the catalog.
                await service._wait_for_background_idle(quiet_seconds=max(10.0, float(settings.background_idle_seconds)))
                await run_blocking(service.run_startup_catalog_maintenance)
                # v0.2.78 title policy is a lightweight, separate migration. Do not
                # rerun the historical full-catalog repair suite just to recalculate
                # automatic main-title consensus.
                await run_blocking(service.run_bound_title_consensus_upgrade)
                # v0.2.80 repairs source-less subscription clones left by older reset
                # logic after an automatic merge had multiple raw library URLs.
                await service.repair_subbed_orphan_aliases_upgrade()
                # v0.2.83 fixes title-only/source-less entries whose metadata provider
                # searches used to be discarded because the identity scorer had an empty
                # native-title evidence set. This is a one-time, metadata-only background
                # repair and deliberately does not start a broad source crawl at startup.
                await service.repair_orphan_identity_metadata_upgrade()
            except asyncio.CancelledError:
                raise
            except Exception:
                pass

        async def initial_merge_origin_recovery() -> None:
            # This retry is needed on every restart, independently of versioned,
            # browser-idle maintenance. Only pending local origin writes are touched.
            if not await run_blocking(service.pending_entry_origin_count):
                return
            task_id = task_registry.create("Recover interrupted chapter changes", message="Completing interrupted local chapter changes")
            task_registry.mark_active(task_id, reset_estimate=True)
            try:
                result = await run_blocking(service.reconcile_pending_entry_origins)
                if result["failed"]:
                    raise RuntimeError(f"Chapter recovery is still pending for {result['failed']} entries")
                task_registry.complete(task_id, message=f"Recovered {result['completed']} interrupted chapter changes")
            except asyncio.CancelledError:
                task_registry.cancel(task_id, message="Chapter recovery cancelled during maintenance/shutdown")
                raise
            except Exception as exc:
                task_registry.fail(task_id, exc, message="Chapter recovery failed; retry Auto-merge duplicates")

        startup_merge_recovery = service.track_startup_task("merge-origin-recovery", initial_merge_origin_recovery())
        startup_sync = service.track_startup_task("source-sync", initial_source_sync())
        startup_metadata_repair = service.track_startup_task("metadata-repair", initial_metadata_repair())
        startup_catalog_maintenance = service.track_startup_task("catalog-maintenance", initial_catalog_maintenance())
        tag_store.queue_reindex(service, startup=True)
        service.track_startup_task("gallery-chapters", service.reconcile_gallery_chapters())
        service.schedule_startup_dedup()
        service.schedule_startup_translation_enrichment()
        service.schedule_startup_additional_record_discovery()
        service.schedule_startup_refresh()
        # Resume an interrupted post-reset exact-ID replay before speculative catalog
        # growth. The replay itself is low-priority and yields to foreground/Subbed work.
        pending_reset_hints = settings.data_dir / "reset-rebuild-hints.json"
        if pending_reset_hints.is_file():
            service.schedule_reset_hint_replay(str(pending_reset_hints))
        service.schedule_continuous_catalog_discovery()
        # Pipeline upgrades invalidate old results once. On ordinary restarts, resume
        # only unfinished discovery without rechecking completed review results.
        if not service.schedule_import_candidate_pipeline_upgrade(delay=4.0):
            service.schedule_import_discovery_resume(delay=4.0)
        # Status/library metrics and duplicate review indexes are useful, but neither is
        # a prerequisite for serving the UI. Build them after HTTP startup and keep the
        # expensive duplicate scorer completely off the request path.
        service.schedule_status_aux_refresh(delay=5.0)
        service.schedule_duplicate_snapshot_refresh(delay=5.0)
        await run_blocking(repair_workflow.initialize_safe)
        await run_blocking(source_maintenance.recover)
        await source_raws.start(_database_admin_lock)
        repair_workflow.start(_database_admin_lock)
        source_engine.start()
        source_maintenance.start()
        source_attention.start(service, source_host)
        source_health.start(_database_admin_lock)
        library_manager.start(_database_admin_lock)
        yield
    except Exception as exc:
        support_logs.record('app','Application lifecycle failed: '+json.dumps(exception_context(exc)))
        raise
    finally:
        from .lifecycle import cleanup_steps
        primary = sys.exception()
        service._background_stopping = True
        tasks = [task for task in (startup_sync, startup_metadata_repair, startup_catalog_maintenance, startup_merge_recovery)
                 if task is not None]
        for task in tasks:
            if not task.done(): task.cancel()
        engine_shutdown = None
        def begin_engine_stop():
            nonlocal engine_shutdown
            engine_shutdown = asyncio.create_task(source_engine.stop())
        async def finish_engine_stop():
            if engine_shutdown is not None: await engine_shutdown
        steps = [
            ('library monitoring', library_manager.stop),
            ('source health', source_health.stop), ('repair workflow', repair_workflow.stop),
            ('raw sources', source_raws.stop), ('source maintenance', source_maintenance.stop),
            ('internal services', internal_services.stop), ('orphan sources', source_orphans.stop),
            ('source browser', source_browser.stop), ('begin engine stop', begin_engine_stop),
            ('startup tasks', lambda: asyncio.gather(*tasks, return_exceptions=True)),
            ('background tasks', service.cancel_background_tasks), ('covers', cover_cache.close),
        ]
        clients = [source_host, translator, metadata_catalog]
        if main_suwayomi is not None: clients.append(main_suwayomi)
        steps.extend((f'client {index}', client.close) for index,client in enumerate(clients))
        steps.extend([
            ('catalog read lane', read_lane.close), ('cover read lane', cover_read_lane.close),
            ('chapter read lane', chapter_stats_lane.close), ('tag read lane', tag_suggestion_lane.close),
            ('task history', task_journal.stop),
            ('database', db.close_current_thread), ('engine stop', finish_engine_stop),
            ('support logs', support_logs.stop), ('diagnostic lane', diagnostic_read_lane.close),
        ])
        await cleanup_steps(steps, primary=primary, report=lambda name,exc:
            support_logs.record('app','Application cleanup failed at '+name+': '+json.dumps(exception_context(exc))))


class AdminGZipMiddleware:
    """Compress bulky admin/status JSON without touching manga image streams."""

    def __init__(self, app):
        self.app = app
        self.gzip = GZipMiddleware(app, minimum_size=1024, compresslevel=5)

    async def __call__(self, scope, receive, send):
        path = scope.get("path", "") if scope.get("type") == "http" else ""
        encrypted_backup=path.startswith('/api/admin/instance/backups/') and path.endswith('/download')
        if (path.startswith("/api/admin/") and path not in {"/api/admin/extension-apk/download", "/api/admin/extension-jar/download"} and not encrypted_backup) or path == "/api/v1/health":
            return await self.gzip(scope, receive, send)
        return await self.app(scope, receive, send)


class HttpSafetyMiddleware:
    """Keep untrusted host/asset headers out of URL and browser security decisions."""
    def __init__(self, app):
        self.app = app

    async def __call__(self, scope, receive, send):
        if scope.get("type") != "http":
            return await self.app(scope, receive, send)
        headers = scope.get("headers", [])
        hosts = [value for key, value in headers if key.lower() == b"host"]
        if len(hosts) > 1 or any(any(byte <= 32 or byte >= 127 or byte in b"/\\?#@" for byte in host) for host in hosts):
            return await JSONResponse({"detail": "Invalid Host header"}, status_code=400)(scope, receive, send)
        path = str(scope.get("path") or "")
        if path == "/" or path.startswith("/static/"):
            # The UI assets are small whole-file resources, not resumable media.
            # Avoid expensive/malicious multipart Range handling on old installations.
            scope = dict(scope, headers=[(key, value) for key, value in headers
                                         if key.lower() not in {b"range", b"if-range"}])
        relative = path.replace("/api/reader/v1/", "/api/v1/", 1)
        is_asset = any(relative.startswith(prefix) for prefix in (
            "/api/v1/pages/", "/api/v1/covers/", "/api/v1/source-covers/",
            "/api/v1/source-icon/", "/api/v1/source-extension-icon/"))
        async def safe_send(message):
            if message.get("type") == "http.response.start":
                message = dict(message)
                result = [(key, value) for key, value in message.get("headers", [])
                          if key.lower() != b"x-content-type-options"]
                result.append((b"x-content-type-options", b"nosniff"))
                if path.startswith("/api/admin/") or path == "/api/v1/health":
                    result.append((b"x-autosourcarr-version", APP_VERSION.encode("ascii")))
                if is_asset:
                    # A challenge page or malicious SVG/HTML returned by an upstream
                    # must not execute with the AutoSourcarr origin if opened directly.
                    result = [(key, value) for key, value in result if key.lower() != b"content-security-policy"]
                    result.append((b"content-security-policy", b"sandbox; default-src 'none'"))
                message["headers"] = result
            await send(message)
        return await self.app(scope, receive, safe_send)


class ReaderApiAuthMiddleware:
    """Expose a token-protected reader API without giving Authelia a browser session.

    The ordinary /api/v1 routes are unchanged: the web UI reaches them through the
    normal Authelia-protected virtual host and the Suwayomi JAR reaches them directly
    on the Docker network. Android uses /api/reader/v1 instead. NGINX/Authelia may
    bypass only that narrow prefix because AutoSourcarr validates the reader token
    here before rewriting the request onto the same read API implementation.
    """

    reader_prefix = "/api/reader/v1"
    internal_prefix = "/api/v1"

    def __init__(self, app):
        self.app = app

    @staticmethod
    def _header(scope, name: bytes) -> str:
        for key, value in scope.get("headers", []):
            if key.lower() == name:
                return value.decode("latin-1", errors="ignore")
        return ""

    async def __call__(self, scope, receive, send):
        if scope.get("type") != "http":
            return await self.app(scope, receive, send)
        path = str(scope.get("path") or "")
        if not (path == self.reader_prefix or path.startswith(self.reader_prefix + "/")):
            return await self.app(scope, receive, send)

        if str(scope.get("method") or "GET").upper() not in {"GET", "HEAD"}:
            response = JSONResponse(
                status_code=405,
                content={"detail": "AutoSourcarr reader API is read-only"},
                headers={"Allow": "GET, HEAD"},
            )
            return await response(scope, receive, send)

        expected = str(settings.autosourcarr_reader_api_token or "").strip()
        if not expected:
            response = JSONResponse(
                status_code=503,
                content={"detail": "AutoSourcarr reader API token is not configured"},
            )
            return await response(scope, receive, send)

        supplied = self._header(scope, b"x-autosourcarr-api-key").strip()
        if not supplied:
            authorization = self._header(scope, b"authorization").strip()
            if authorization.lower().startswith("bearer "):
                supplied = authorization[7:].strip()
        if not supplied or not secrets.compare_digest(supplied.encode("utf-8"), expected.encode("utf-8")):
            response = JSONResponse(
                status_code=401,
                content={"detail": "Invalid AutoSourcarr reader API token"},
                headers={"WWW-Authenticate": "Bearer"},
            )
            return await response(scope, receive, send)

        rewritten = dict(scope)
        rewritten_path = self.internal_prefix + path[len(self.reader_prefix):]
        rewritten["path"] = rewritten_path
        rewritten["raw_path"] = rewritten_path.encode("utf-8")
        return await self.app(rewritten, receive, send)


_database_admin_lock = asyncio.Lock()
_catalog_reset_active = False
_catalog_http_requests = 0


@asynccontextmanager
async def _exclusive_catalog_reset(timeout: float = 30.0):
    """Reject new catalogue requests and drain admitted handlers before reset.

    The caller owns _database_admin_lock. Health/tasks remain readable; if a
    foreground handler does not finish, abort without clearing any data.
    """
    global _catalog_reset_active
    _catalog_reset_active = True
    deadline = asyncio.get_running_loop().time() + timeout
    try:
        while _catalog_http_requests:
            if asyncio.get_running_loop().time() >= deadline:
                raise HTTPException(503, "Database reset aborted: foreground requests are still active; retry after they finish")
            await asyncio.sleep(0.01)
        yield
    finally:
        _catalog_reset_active = False

APP_VERSION = (Path(__file__).resolve().parent.parent / "VERSION").read_text("utf-8").strip()
_last_dependency_health = None
app = FastAPI(title="AutoSourcarr", version=APP_VERSION, lifespan=lifespan)
app.add_middleware(AdminGZipMiddleware)
app.add_middleware(CrossSiteWriteMiddleware)
app.add_middleware(ReaderApiAuthMiddleware)
app.add_middleware(HttpSafetyMiddleware)


@app.exception_handler(HumanAssistanceRequired)
async def human_assistance_response(request: Request, exc: HumanAssistanceRequired):
    return JSONResponse(status_code=503, content={'detail': str(exc), 'needs_attention': True, 'attention_id': exc.attention_id},
                        headers={'Retry-After': str(exc.retry_after), 'Cache-Control': 'no-store'})


@app.exception_handler(ReaderBrowsePending)
async def reader_browse_pending_response(request: Request, exc: ReaderBrowsePending):
    return JSONResponse(status_code=503,
        content={"detail": "The AutoSourcarr catalog is preparing this page. Retry shortly.", "preparing": True},
        headers={"Retry-After": str(exc.retry_after), "Cache-Control": "no-store",
                 "X-AutoSourcarr-Reader-State": "preparing"})


@app.exception_handler(Exception)
async def capture_unhandled_error(request: Request, exc: Exception):
    """Keep unexpected failures visible without dumping an opaque ASGI traceback.

    The public response contains only a short error id; full diagnostic text is
    available from the admin Errors panel. Handled HTTPException responses keep
    their normal FastAPI behavior and do not enter this registry.
    """
    error_id = error_registry.record(
        "AutoSourcarr",
        str(exc) or exc.__class__.__name__,
        key=f"unhandled|{request.method}|{request.scope.get("path", "")}|{exc.__class__.__name__}|{str(exc)}",
        path=request.scope.get("path", ""),
        method=request.method,
        status_code=500,
        error_type=exc.__class__.__name__,
        detail=f"{exc.__class__.__name__}: {exc}", exception=exc,
        context={"operation": "http_request", "route": request.scope.get("path", "")},
    )
    return JSONResponse(
        status_code=500,
        content={"detail": "Unexpected AutoSourcarr error", "error_id": error_id},
    )


def _mutation_task_name(method: str, path: str) -> str | None:
    """Give every user-triggered admin mutation a visible Tasks entry.

    Long-running operations that already create a progress-aware task are excluded
    here so they keep their real N/M progress instead of appearing twice.
    """
    method = str(method or "").upper()
    path = str(path or "")
    if method not in {"POST", "PUT", "DELETE"} or not path.startswith("/api/admin/"):
        return None
    if path.startswith("/api/admin/tasks/"):
        return None
    # Read-only batch lookups use POST to avoid enormous URLs. They are not
    # application updates, even when validation fails or a browser disconnects.
    if path in {"/api/admin/entries/summaries", "/api/admin/duplicate-candidates", "/api/admin/matrix/chapter-stats"}:
        return None
    if path == "/api/admin/duplicates/automatic":
        return None  # owns its own progress-aware task
    if path == "/api/admin/imports/clear-history":
        return None
    if path == "/api/admin/imports/reset-rejections":
        return None
    if path == "/api/admin/imports/reset-manual-autoresolve":
        return None
    if path.startswith("/api/admin/source-browser"):
        return None
    if path.startswith(("/api/admin/source-maintenance", "/api/admin/source-orphans", "/api/admin/source-health")):
        return None
    if path.startswith("/api/admin/setup"):
        return None
    if path.startswith("/api/admin/attention"):
        return None
    if path.startswith("/api/admin/errors"):
        return None
    if path == "/api/admin/ui-preferences":
        return None
    if path == "/api/admin/maintenance/refresh-metadata":
        return None
    if path == "/api/admin/maintenance/discover-additional":
        return None
    if path in {"/api/admin/subbed/search-all", "/api/admin/subbed/repair-unmatched"}:
        return None
    if path == "/api/admin/maintenance/full-reset":
        return None
    if "/check-chapters" in path:
        return None
    if path == "/api/admin/maintenance/consolidate":
        return None
    if path == "/api/admin/maintenance/reset-covers": return "Reset covers"
    if path.endswith("/refresh-covers"): return "Refresh covers"
    if path.endswith("/unmatch-duplicates"): return "Unmatch duplicates"
    if path.endswith("/unbind-auto-matches"): return "Unbind automatic matches"
    if path.endswith("/redo-automation"): return None
    if path == "/api/admin/maintenance/reset-database": return "Reset database"
    if path == "/api/admin/maintenance/reset-manual-groups": return "Reset manual groups"
    if path == "/api/admin/manual-actions": return None
    if path == "/api/admin/maintenance/reset-manual-matches": return "Reset manual matches"
    if path.startswith("/api/admin/manual-groups/"):
        if method == "DELETE": return "Remove manual group member"
        if method == "POST": return "Add manual group member"
    if path == "/api/admin/maintenance/refresh-feeds": return "Refresh browse feeds"
    if path == "/api/admin/duplicates/review": return "Review duplicate"
    if path == "/api/admin/sources/sync": return "Sync sources"
    if path == "/api/admin/source-extensions/upload": return "Install source APK"
    if method == "PUT" and re.fullmatch(r"/api/admin/sources/[^/]+/preferences", path): return "Save source settings"
    if method == "PUT" and path.startswith("/api/admin/sources/"): return "Update source"
    if method == "PUT" and path.startswith("/api/admin/settings/env/"): return "Save settings"
    if path.startswith("/api/admin/candidates/") and path.endswith("/quality"): return "Analyze chapter quality"
    if path.startswith("/api/admin/imports/"):
        # Match/retry endpoints create their own long-lived progress tasks and return
        # immediately. Do not create a second one-request middleware task for them.
        if path.endswith("/retry") or path.endswith("/reject") or "/match/" in path: return None
        if path.endswith("/skip"): return "Skip backup import"
    if path.startswith("/api/admin/entries/"):
        if method == "DELETE" and "/variants/" in path: return "Remove additional match"
        if path.endswith("/promote"): return "Use as primary"
        if path.endswith("/refresh-metadata"): return "Refresh metadata"
        if path.endswith("/refresh"): return "Refresh metadata + chapters"
        if path.endswith("/discover-additional"): return "Find missing + extra source records"
        if path.endswith("/manual-match"): return "Update match"
        if method == "DELETE" and "/matches/" in path: return "Clear match"
        if path.endswith("/preferred-title"): return "Change main title"
        if path.endswith("/aliases"): return "Add alternate title"
        if method == "DELETE" and path.count("/") == 4: return "Delete entry"
    return f"Admin action: {method} {path.removeprefix('/api/admin/')}"


@app.middleware("http")
async def image_response_safety_headers(request: Request, call_next):
    response = await call_next(request)
    if str(request.scope.get("path") or "").startswith(("/api/v1/covers/", "/api/v1/source-covers/", "/api/v1/source-icon/", "/api/v1/source-extension-icon/", "/api/v1/pages/", "/api/reader/v1/covers/", "/api/reader/v1/pages/")):
        response.headers["X-Content-Type-Options"] = "nosniff"
        response.headers["Content-Security-Policy"] = "sandbox; default-src 'none'; style-src 'unsafe-inline'"
        response.headers["Referrer-Policy"] = "no-referrer"
    return response


@app.middleware("http")
async def track_admin_mutations(request: Request, call_next):
    # Interactive reads/writes get priority over optional source discovery and
    # maintenance. Image/static/status polling is excluded so an open page does not
    # permanently starve background work. Track the full request lifetime so Tasks
    # can yield while a real UI action is in flight rather than relying on timestamps
    # alone.
    path = request.scope.get("path", "")
    local_poll = request.method == "GET" and (
        path in {"/api/admin/imports", "/api/admin/matrix", "/api/admin/source-health",
                 "/api/admin/source-maintenance", "/api/admin/source-maintenance/control"}
        or bool(re.fullmatch(r"/api/admin/imports/[^/]+/items(?:/[0-9]+)?", path))
    )
    interactive = not (
        local_poll
        or path.startswith("/static/")
        or path.startswith("/api/v1/covers/")
        or path.startswith("/api/v1/source-covers/")
        or path.startswith("/api/v1/source-icon/")
        or path.startswith("/api/v1/source-extension-icon/")
        or path in {
            "/api/v1/live", "/api/v1/health", "/api/admin/tasks", "/api/admin/errors",
            "/api/admin/duplicates", "/api/admin/duplicate-candidates", "/api/admin/entries/summaries", "/api/admin/matrix/chapter-stats",
        }
    )
    if interactive:
        service.begin_interactive_request()
    name = _mutation_task_name(request.method, path)
    recovery_prefix = f"unhandled|{request.method}|{request.scope.get("path", "")}|"
    try:
        if not name:
            response = await call_next(request)
            if response.status_code < 500:
                error_registry.resolve_prefix(recovery_prefix)
            return response
        task_id = task_registry.create(name, total=1, message="Running", origin="manual")
        try:
            response = await call_next(request)
        except Exception as exc:
            task_registry.fail(task_id, exc)
            raise
        if response.status_code < 400:
            task_registry.complete(task_id, message="Completed")
        else:
            task_registry.fail(task_id, f"HTTP {response.status_code}")
        if response.status_code < 500:
            error_registry.resolve_prefix(recovery_prefix)
        return response
    finally:
        if interactive:
            service.end_interactive_request()


@app.middleware("http")
async def catalogue_reset_admission(request: Request, call_next):
    global _catalog_http_requests
    path = request.scope.get("path", "")
    # These POST actions serialize on _database_admin_lock themselves. Counting
    # a second reset waiting for that lock would deadlock the first reset's drain.
    control = (request.method == "POST" and (path in {
        "/api/admin/maintenance/reset-database", "/api/admin/maintenance/full-reset",
        "/api/admin/maintenance/reset-all-manual-actions",
        "/api/admin/storage/database/move",
    } or path.startswith(("/api/admin/instance/", "/api/admin/source-orphans/", "/api/admin/source-maintenance/", "/api/admin/source-extensions/", "/api/admin/source-health/")))) or (
        request.method == "PUT" and (path=='/api/admin/source-health/daily' or bool(re.fullmatch(r"/api/admin/sources/[^/]+/preferences", path))))
    # These operations also serialize on the same admin lock. A queued operation
    # must not count as foreground work that the lock-owning backup waits to drain.
    control = control or (request.method in {'POST','PUT','DELETE'} and (
        path.startswith(('/api/admin/library/','/api/admin/profiles/','/api/admin/downloads','/api/admin/review-workbench/'))
        or path.startswith(('/api/admin/metadata-fields/','/api/admin/serving-upgrades/','/api/admin/policy-simulator/','/api/admin/engine/compatibility'))
        or (path.startswith('/api/admin/candidates/') and path.endswith('/quality'))
        or (path.startswith('/api/admin/tasks/') and path.endswith('/retry-safe'))))
    monitoring = request.method in {"GET", "HEAD"} and path in {
        "/api/v1/live", "/api/v1/health", "/api/admin/tasks", "/api/admin/errors",
    }
    if not path.startswith("/api/") or control or monitoring:
        return await call_next(request)
    if _catalog_reset_active:
        return JSONResponse(status_code=503,
            content={"detail": "Database reset in progress; retry when it finishes"},
            headers={"Retry-After": "5"})
    # GET discovery can populate the catalogue too. Track both reads and writes,
    # not just mutations. The counter is owned by the event-loop thread.
    _catalog_http_requests += 1
    try:
        return await call_next(request)
    finally:
        _catalog_http_requests -= 1


static_dir = Path(__file__).parent / "static"
app.mount("/static", StaticFiles(directory=static_dir), name="static")


async def admin_guard(authorization: Annotated[str | None, Header()] = None, x_admin_token: Annotated[str | None, Header()] = None):
    expected = settings.admin_token.strip()
    if not expected:
        return True
    token = x_admin_token or ""
    if authorization and authorization.lower().startswith("bearer "):
        token = authorization[7:]
    if not secrets.compare_digest(token.encode("utf-8"), expected.encode("utf-8")):
        raise HTTPException(401, "Invalid admin token")
    return True


from .internal_services import services_router, internal_services
app.include_router(services_router(admin_guard))
app.include_router(webview_router(admin_guard))
app.include_router(maintenance_router(admin_guard, _database_admin_lock))
app.include_router(orphan_router(admin_guard, _database_admin_lock))
app.include_router(health_router(admin_guard, _database_admin_lock))
from .matching_review import router as matching_review_router
app.include_router(matching_review_router(db, service, admin_guard))


class TagReviewRequest(BaseModel):
    left_id: int = Field(gt=0)
    right_id: int = Field(gt=0)
    decision: Literal["same", "different", "clear"]


@app.get("/api/admin/tags", dependencies=[Depends(admin_guard)])
async def admin_tags(search: str = Query("", max_length=200), limit: int = Query(100, ge=1, le=200), offset: int = Query(0, ge=0)):
    return await catalogue_response(tag_store.list_tags, search, limit, offset)


@app.get("/api/admin/tags/suggestions", dependencies=[Depends(admin_guard)])
async def admin_tag_suggestions():
    return await catalogue_response(tag_store.suggestions, lane=tag_suggestion_lane)


@app.get("/api/admin/tags/reviews", dependencies=[Depends(admin_guard)])
async def admin_tag_reviews():
    return await catalogue_response(tag_store.reviews)


@app.post("/api/admin/tags/review", dependencies=[Depends(admin_guard)])
async def admin_review_tags(body: TagReviewRequest):
    try:
        result = await run_blocking(service.review_tags, body.left_id, body.right_id, body.decision)
    except ValueError as exc:
        raise HTTPException(409, str(exc)) from exc
    # Refresh saved suggestions. A tag decision itself never merges/splits works.
    service._duplicate_snapshot_stale = True
    service._search_cache.clear()
    service.schedule_duplicate_snapshot_refresh()
    return result


@app.post("/api/admin/tags/reindex", dependencies=[Depends(admin_guard)])
async def admin_reindex_tags():
    return tag_store.queue_reindex(service)


@app.get("/api/admin/entries/{entry_id}/tags", dependencies=[Depends(admin_guard)])
async def admin_entry_tags(entry_id: int):
    try:
        return await catalogue_response(tag_store.entry_metadata, entry_id)
    except KeyError as exc:
        raise HTTPException(404, "Entry not found") from exc


@app.get("/api/admin/source-engine", dependencies=[Depends(admin_guard)])
async def admin_source_engine():
    return source_engine.snapshot()


@app.post("/api/admin/source-engine/challenges", dependencies=[Depends(admin_guard)])
async def admin_source_challenges():
    return {"queued": source_engine.schedule_challenge_setup(force=True), **source_engine.snapshot()}


@app.get('/api/admin/source-browser', dependencies=[Depends(admin_guard)])
async def admin_source_browser():
    return await source_browser.snapshot()


@app.put('/api/admin/source-browser/user-agent', dependencies=[Depends(admin_guard)], status_code=202)
async def admin_source_user_agent(request: Request):
    try:
        value = await request.json()
        if not isinstance(value, dict):raise ValueError('Expected a user-agent object')
        preferences = validate_preferences(value.get('preset', 'custom'), value.get('user_agent', ''))
        source_browser.queue('user-agent', _database_admin_lock, preferences=preferences)
    except ValueError as exc:
        raise HTTPException(400, str(exc))
    return {'queued': True}


@app.post('/api/admin/source-browser/clear-data', dependencies=[Depends(admin_guard)], status_code=202)
async def admin_clear_source_browser():
    try:
        source_browser.queue('clear-data', _database_admin_lock)
    except ValueError as exc:
        raise HTTPException(409, str(exc))
    return {'queued': True}


@app.post('/api/admin/source-browser/restart', dependencies=[Depends(admin_guard)], status_code=202)
async def admin_restart_sources():
    try:
        source_browser.queue('restart', _database_admin_lock)
    except ValueError as exc:
        raise HTTPException(409, str(exc))
    return {'queued': True}


@app.get('/api/admin/attention', dependencies=[Depends(admin_guard)])
async def admin_attention():
    return await source_attention.snapshot()


@app.post('/api/admin/attention/{attention_id}/retry', dependencies=[Depends(admin_guard)], status_code=202)
async def admin_attention_retry(attention_id: str):
    items = await source_attention.snapshot()
    row = next((row for row in items['items'] if row['id'] == attention_id), None)
    if row is None:
        raise HTTPException(404, 'This assistance item is already resolved')
    if service._background_stopping:
        raise HTTPException(503, 'Maintenance in progress; retry shortly')
    # The HTTP request returns promptly; owned probes are drained on reset/shutdown.
    service._track_auxiliary(source_attention.retry(attention_id, source_host, manual=True))
    return {'queued': True, 'attention_id': attention_id}


def _ui_user_context(request: Request) -> dict[str, Any]:
    """Resolve the reverse-proxy/Authelia identity used for UI preferences.

    AutoSourcarr's canonical NGINX/NPM contract is exactly the user's existing
    Authelia block: ``Remote-User``, ``Remote-Email`` and ``Remote-Groups``. Common
    aliases remain fallback-only so other proxy layouts continue to work.
    """
    custom = str(getattr(settings, "authelia_user_header", "") or "").strip()
    # The existing NPM snippet supplied by the project/user sets these exact three
    # headers after auth_request. Keep them first and expose all three diagnostically.
    primary_user_headers: list[tuple[str, str]] = [("remote-user", "Remote-User")]
    if custom and custom.casefold() != "remote-user":
        primary_user_headers.insert(0, (custom.casefold(), custom))
    fallback_user_headers: tuple[tuple[str, str], ...] = (
        ("x-forwarded-user", "X-Forwarded-User"),
        ("x-auth-request-user", "X-Auth-Request-User"),
        ("x-remote-user", "X-Remote-User"),
        ("x-forwarded-username", "X-Forwarded-Username"),
        ("x-auth-request-username", "X-Auth-Request-Username"),
        ("x-forwarded-preferred-username", "X-Forwarded-Preferred-Username"),
        ("x-auth-request-preferred-username", "X-Auth-Request-Preferred-Username"),
        ("x-webauth-user", "X-Webauth-User"),
        ("x-user", "X-User"),
        ("x-username", "X-Username"),
    )
    candidates: list[tuple[str, str]] = []
    seen: set[str] = set()
    for header, label in [*primary_user_headers, *fallback_user_headers]:
        key = str(header or "").strip().casefold()
        if key and key not in seen:
            seen.add(key)
            candidates.append((key, label))

    remote_email = str(request.headers.get("remote-email") or "").split(",", 1)[0].strip()[:256]
    remote_groups_raw = str(request.headers.get("remote-groups") or "").strip()
    remote_groups = [x.strip() for x in remote_groups_raw.split(",") if x.strip()][:64]
    identity_headers_present: list[str] = []
    for canonical_name in ("remote-user", "remote-email", "remote-groups"):
        if str(request.headers.get(canonical_name) or "").strip():
            identity_headers_present.append("-".join(part.capitalize() for part in canonical_name.split("-")))

    proxy_names = sorted({
        str(k) for k in request.headers.keys()
        if str(k).casefold().startswith(("x-forwarded-", "x-auth-request-", "remote-"))
    })[:32]
    proxy_detected = bool(proxy_names)
    host = str(request.headers.get("host") or "")[:256]

    for header, label in candidates:
        value = str(request.headers.get(header) or "").strip()
        if value:
            value = value.split(",", 1)[0].strip()[:256]
            return {
                "key": f"authelia:{value.casefold()}", "user": value, "header": label, "authelia": True,
                "email": remote_email, "groups": remote_groups,
                "identity_headers_present": sorted(set(identity_headers_present + [label])),
                "proxy_detected": proxy_detected, "proxy_headers": proxy_names,
                "custom_header": custom, "host": host, "direct_access": False,
            }

    # Email is a stable fallback when the proxy intentionally omits Remote-User.
    for header, label in (
        ("remote-email", "Remote-Email"),
        ("x-forwarded-email", "X-Forwarded-Email"),
        ("x-auth-request-email", "X-Auth-Request-Email"),
    ):
        value = str(request.headers.get(header) or "").strip()
        if value:
            value = value.split(",", 1)[0].strip()[:256]
            return {
                "key": f"authelia-email:{value.casefold()}", "user": value, "header": label, "authelia": True,
                "email": value, "groups": remote_groups,
                "identity_headers_present": sorted(set(identity_headers_present + [label])),
                "proxy_detected": True, "proxy_headers": proxy_names,
                "custom_header": custom, "host": host, "direct_access": False,
            }

    # No proxy identity can exist when the browser opens the backend directly
    # (e.g. http://192.168.x.x:30060). Make that explicit instead of implying the
    # provided Authelia config is broken.
    return {
        "key": "local:default", "user": "Local user", "header": "", "authelia": False,
        "email": "", "groups": [], "identity_headers_present": identity_headers_present,
        "proxy_detected": proxy_detected, "proxy_headers": proxy_names, "custom_header": custom,
        "host": host, "direct_access": not proxy_detected,
    }


@app.get("/", response_class=HTMLResponse)
async def admin_home():
    return FileResponse(static_dir / "index.html")


@app.get("/api/v1/live")
def live():
    """Cheap local liveness check for Docker/TrueNAS health monitoring."""
    try:
        with db.catalog() as c:
            c.execute("SELECT 1").fetchone()
        with db.origins() as o:
            o.execute("SELECT 1").fetchone()
        return {"ok": True, "version": app.version}
    except Exception as exc:
        raise HTTPException(503, f"Database health check failed: {exc}")


@app.get("/api/v1/health")
async def health():
    global _last_dependency_health
    async def checked(client, *, configured=True, component="dependency", error_key="dependency-health"):
        if client is None:
            return {"ok": False, "configured": False, "url": "", "error": "MAIN_SUWAYOMI_URL is not configured"}
        try:
            result = await asyncio.wait_for(client.health(), timeout=5.0)
        except asyncio.TimeoutError:
            result = {"ok": False, "url": client.base_url, "error": "health check timed out after 5s"}
        except Exception as exc:
            result = {"ok": False, "url": getattr(client, "base_url", ""), "error": str(exc) or type(exc).__name__, "exception_chain": exception_context(exc)}
        result = scrub(result)
        result["configured"] = configured
        result["checked_at"] = runtime_context()["server_time"]
        if result.get("ok") or result.get("deferred"):
            error_registry.resolve(key=error_key)
        elif configured:
            error_registry.record(
                component,
                str(result.get("error") or result.get("warning") or "Health check failed"),
                key=error_key,
                status_code=503,
                error_type="DependencyHealth",
                detail=f"URL: {result.get('url') or ''}",
                context={"operation": "dependency_health", "dependency": component, "probes": result.get("probes", []), "exception_chain": result.get("exception_chain", [])},
            )
        return result

    async def checked_translation():
        try:
            result = await asyncio.wait_for(translator.health(), timeout=4.0)
        except asyncio.TimeoutError:
            result = {"ok": False, "configured": bool(settings.translation_api_url.strip()), "url": settings.translation_api_url, "error": "health check timed out after 4s"}
        except Exception as exc:
            result = {"ok": False, "configured": bool(settings.translation_api_url.strip()), "url": settings.translation_api_url, "error": str(exc) or type(exc).__name__, "exception_chain": exception_context(exc)}
        result = scrub(result)
        result["checked_at"] = runtime_context()["server_time"]
        configured = bool(result.get("configured", bool(settings.translation_api_url.strip())))
        if result.get("ok") or not configured:
            error_registry.resolve(key="translation-health")
        else:
            error_registry.record(
                "Title translation",
                str(result.get("error") or "Translation health check failed"),
                key="translation-health",
                status_code=503,
                error_type="DependencyHealth",
                detail=f"URL: {result.get('url') or settings.translation_api_url}",
                context={"operation": "dependency_health", "dependency": "translation", "exception_chain": result.get("exception_chain", [])},
            )
        return result

    async def library_status():
        # v0.2.29 was responsive because Status did not have to wait for a fresh
        # main-library walk on every browser refresh. Return the last completed
        # snapshot immediately and refresh it independently in the background.
        result = service.cached_library_status()
        service.schedule_status_aux_refresh(delay=0.0)
        if result.get("ok") is True or main_suwayomi is None:
            error_registry.resolve(key="main-library-status")
        # A missing/stale cache is not an error. Main-Suwayomi reachability is
        # already represented by its own bounded dependency health check.
        return result

    async def safe_status_stats():
        try:
            result = await service.status_stats_snapshot(timeout=4.0)
            error_registry.resolve(key="status-stats")
            return result
        except asyncio.TimeoutError:
            # A busy SQLite worker can make optional metrics late without making
            # AutoSourcarr unhealthy. Keep this informational so the global red
            # error badge represents functional failures, not a slow dashboard.
            error_registry.record(
                "Status metrics", "Local status metrics exceeded 4 seconds", key="status-stats", status_code=200,
                error_type="StatusTimeout", severity="warning",
                detail="Optional dashboard metrics were skipped for this refresh; normal APIs remain available.",
            )
            return {"warning": "Local status metrics are still refreshing"}
        except Exception as exc:
            error_registry.record(
                "Status metrics", str(exc), key="status-stats", status_code=500,
                error_type=exc.__class__.__name__, detail=f"{exc.__class__.__name__}: {exc}",
            )
            return {"error": str(exc)}

    async def checked_source():
        if source_engine.state == 'setup-required':
            error_registry.resolve(key='source-host-health')
            return {"ok": False, "configured": True, "url": settings.effective_source_url,
                    "error": source_engine.error, "setup_required": True}
        return await checked(source_host, component="Source engine", error_key="source-host-health")

    host, main, translation, stats, library = await asyncio.gather(
        checked_source(),
        checked(main_suwayomi, configured=main_suwayomi is not None, component="Main Suwayomi", error_key="main-suwayomi-health"),
        checked_translation(),
        safe_status_stats(),
        library_status(),
    )
    captured = runtime_context()
    _last_dependency_health = scrub({"captured_at": captured["server_time"], "version": APP_VERSION,
                                    "source_host": host, "main_suwayomi": main, "translation": translation, "library": library})
    # GraphQL liveness does not establish that an individual source CAPTCHA or
    # chapter-page failure has recovered. Only a successful chapter lookup does.
    errors = error_registry.snapshot(limit=5, include_resolved=False)
    return {
        "ok": bool(host.get("ok")),
        "version": app.version, "captured_at": captured["server_time"], "instance_id": captured["instance_id"],
        "source_host": host,
        "source_engine": source_engine.snapshot(),
        "main_suwayomi": main,
        "translation": translation,
        "library": library,
        "catalog": str(settings.catalog_path),
        "origins": str(settings.origins_path),
        "stats": stats,
        "errors": {
            "active": int(errors.get("grouped_active_count", errors.get("active_count")) or 0),
            "active_errors": int(errors.get("grouped_active_error_count", errors.get("active_error_count")) or 0),
            "active_warnings": int(errors.get("grouped_active_warning_count", errors.get("active_warning_count")) or 0),
            # Include a few already-known active records so clicking the badge can
            # paint diagnostics immediately; /api/admin/errors still supplies the
            # complete/resolved history asynchronously.
            "items": list(errors.get("items") or []),
            "groups": list(errors.get("groups") or []),
        },
    }


@app.get("/api/v1/sources")
def public_sources():
    return {"items": service.list_sources()}


@app.get("/api/v1/browse/popular")
async def browse_popular(
    page: int = Query(1, ge=1),
    min_matches: int = Query(1, ge=0),
    match_count: int | None = Query(None, ge=0),
    sources: list[str] | None = Query(None),
    sort: str = Query("relevance", pattern="^(relevance|sources|title)$"),
):
    return await service.reader_federated(
        "POPULAR", page=page, source_ids=sources, min_matches=min_matches,
        exact_match_count=match_count, sort=sort,
    )


@app.get("/api/v1/browse/latest")
async def browse_latest(
    page: int = Query(1, ge=1),
    min_matches: int = Query(1, ge=0),
    match_count: int | None = Query(None, ge=0),
    sources: list[str] | None = Query(None),
    sort: str = Query("relevance", pattern="^(relevance|sources|title)$"),
):
    return await service.reader_federated(
        "LATEST", page=page, source_ids=sources, min_matches=min_matches,
        exact_match_count=match_count, sort=sort,
    )


@app.get("/api/v1/search")
async def search(
    q: str = "",
    page: int = Query(1, ge=1),
    min_matches: int = Query(1, ge=0),
    match_count: int | None = Query(None, ge=0),
    sources: list[str] | None = Query(None),
    sort: str = Query("relevance", pattern="^(relevance|sources|title)$"),
):
    return await service.reader_federated(
        "SEARCH", page=page, query=q, source_ids=sources, min_matches=min_matches,
        exact_match_count=match_count, sort=sort,
    )


@app.get("/api/v1/entries/{entry_id}")
def entry(entry_id: int):
    try:
        from .metadata_fields import overlay
        result=overlay(db,service.get_entry(entry_id))
        result['description_for_extension']=service.description_for_extension(result)
        return result
    except KeyError:
        raise HTTPException(404, "Entry not found")


@app.get("/api/v1/entries/{entry_id}/refresh")
@app.post("/api/v1/entries/{entry_id}/refresh")
async def refresh_entry(
    entry_id: int,
    fetch_details: bool = True,
    fetch_chapters: bool = True,
):
    try:
        entry = await service.refresh_entry(
            entry_id,
            fetch_details=fetch_details,
            fetch_chapters=fetch_chapters,
        )
        return {"entry": entry, "chapters": service.list_chapters(entry_id)}
    except KeyError:
        raise HTTPException(404, "Entry not found")


@app.get("/api/v1/entries/{entry_id}/chapters")
def entry_chapters(entry_id: int):
    return {"items": service.list_chapters(entry_id)}


@app.get("/api/v1/chapters/{chapter_id}")
def chapter(chapter_id: int):
    try:
        return service.chapter_details(chapter_id)
    except KeyError:
        raise HTTPException(404, "Chapter not found")


@app.get("/api/v1/chapters/{chapter_id}/pages")
async def chapter_pages(chapter_id: int, request: Request):
    try:
        result = await service.resolve_pages(chapter_id)
        if result.get("source_warning") and not result.get("source_warning_deferred"):
            error_registry.record(
                "Private source host",
                str(result.get("source_warning")),
                key=f"source-host-pages:{chapter_id}",
                severity="warning",
                path=f"/api/v1/chapters/{chapter_id}/pages",
                method="GET",
                status_code=200,
                error_type="StalePageFallback",
                source_context=result.get("source_warning_context"),
                detail="Fresh chapter-page lookup failed; AutoSourcarr served the most recent cached page map instead.",
            )
        elif not result.get("source_warning"):
            error_registry.resolve(key=f"source-host-pages:{chapter_id}")
        # Service-level page URLs stay relative so one catalog can serve both the
        # Docker-internal Suwayomi JAR and an external Android APK. Resolve them
        # against the host/scheme of this request; --proxy-headers preserves HTTPS
        # behind NPM. This also keeps older extension builds compatible because the
        # wire response still contains absolute image URLs.
        request_base = str(request.base_url).rstrip("/")
        result["pages"] = [
            str(url) if str(url).startswith(("http://", "https://")) else f"{request_base}/{str(url).lstrip('/')}"
            for url in (result.get("pages") or [])
        ]
        return result
    except KeyError:
        raise HTTPException(404, "Chapter not found")
    except HumanAssistanceRequired:
        raise
    except SourceEngineUnavailable as exc:
        raise HTTPException(503, str(exc), headers={"Retry-After": str(exc.retry_after)})
    except httpx.PoolTimeout as exc:
        raise HTTPException(503, "Local source HTTP connection capacity is busy; retry shortly",
                            headers={"Retry-After": "1"}) from exc
    except Exception as exc:
        error_id = error_registry.record(
            "Private source host",
            str(exc),
            key=f"source-host-pages:{chapter_id}",
            path=f"/api/v1/chapters/{chapter_id}/pages",
            method="GET",
            status_code=503,
            error_type=exc.__class__.__name__,
            source_context=getattr(exc, "source_context", None),
            detail=f"Could not resolve pages for virtual chapter {chapter_id}. Source host URL: {source_host.base_url}\n{getattr(exc, 'detail', str(exc))}",
        )
        retry_after = getattr(exc, "retry_after", None)
        if retry_after is not None:
            raise HTTPException(503, f"{exc} (error {error_id})", headers={"Retry-After": str(retry_after)})
        raise HTTPException(503, f"Private source host unavailable while resolving chapter pages (error {error_id})")


class OwnedUpstreamByteStream(httpx.AsyncByteStream):
    """Own the first HTTPX stream close, even after Response.is_closed is set."""
    def __init__(self, stream: httpx.AsyncByteStream):
        self.stream = stream
        self._close_task = None

    async def __aiter__(self):
        async for chunk in self.stream:
            yield chunk

    async def aclose(self):
        if self._close_task is None:
            self._close_task = asyncio.create_task(self.stream.aclose())
        cancelled = False
        while not self._close_task.done():
            try:
                await asyncio.shield(self._close_task)
            except asyncio.CancelledError:
                cancelled = True
        self._close_task.result()
        if cancelled:
            raise asyncio.CancelledError()


class UpstreamStreamingResponse(StreamingResponse):
    """Close the source response even when sending headers fails before iteration."""
    def __init__(self, *args, upstream, **kwargs):
        super().__init__(*args, **kwargs)
        self.upstream = upstream
        self._upstream_stream = OwnedUpstreamByteStream(upstream.stream)
        upstream.stream = self._upstream_stream

    async def _close_upstream(self):
        try:
            await self.upstream.aclose()
        finally:
            # HTTPX marks is_closed before awaiting stream closure. Join the
            # original close owner even if a later Response.aclose is a no-op.
            await self._upstream_stream.aclose()

    async def __call__(self, scope, receive, send):
        try:
            await super().__call__(scope, receive, send)
        finally:
            close = asyncio.create_task(self._close_upstream())
            cancelled = False
            while not close.done():
                try:
                    await asyncio.shield(close)
                except asyncio.CancelledError:
                    cancelled = True
            close.result()
            if cancelled:
                raise asyncio.CancelledError()


@app.get("/api/v1/pages/{candidate_id}/{page_index}")
async def proxy_page(candidate_id: int, page_index: int):
    upstream = None
    try:
        info = await run_blocking(service.page_fetch_info, candidate_id, page_index)
        chapter_index = int(info.get("source_chapter_index", -1))
        if chapter_index == -1:
            # A new/reordered candidate has not yet resolved its positional REST
            # chapter index. Retry once at the moment a page is actually needed.
            try:
                chapter_index = await source_host.chapter_index_for_id(
                    int(info["source_manga_id"]), int(info["source_chapter_id"])
                )
            except (SourceWorkDeferred, httpx.PoolTimeout):
                chapter_index = -1  # Preserve the unresolved mapping for a later retry.
            except Exception:
                chapter_index = -2
            if chapter_index != -1:
                await run_blocking(service.set_candidate_chapter_index, candidate_id, chapter_index)
            else:
                # Deferred mapping has no write to perform the membership fence.
                # A split/detach during that await must not open an old-work URL.
                info = await run_blocking(service.page_fetch_info, candidate_id, page_index)
        if chapter_index >= 0:
            upstream = await source_host.open_page_image_stream(
                info["source_manga_id"], chapter_index, page_index
            )
        else:
            # Compatibility fallback for sources whose chapter-index REST mapping
            # is unavailable; simple extensions often expose directly fetchable URLs.
            upstream = await source_host.open_binary_stream(info["private_url"])

        # Upstream headers may take long enough for source ownership to change.
        # Check immediately before handing the body to the reader; keep the
        # source writer gate out of network waits and streaming.
        await run_blocking(service.page_fetch_info, candidate_id, page_index)

        stream_response = upstream

        async def chunks():
            try:
                async for chunk in stream_response.aiter_bytes(64 * 1024):
                    yield chunk
            finally:
                await stream_response.aclose()

        headers = {"Cache-Control": "private, max-age=3600"}
        content_length = upstream.headers.get("content-length")
        if content_length and upstream.headers.get("content-encoding", "identity").lower() == "identity":
            headers["Content-Length"] = content_length
        headers["X-Content-Type-Options"] = "nosniff"
        response = UpstreamStreamingResponse(
            chunks(),
            upstream=upstream,
            media_type=upstream.headers.get("content-type", "application/octet-stream"),
            headers=headers,
        )
        upstream = None  # The streaming response now owns closure.
        return response
    except KeyError:
        raise HTTPException(404, "Page not found; reopen the chapter to refresh page URLs")
    except HumanAssistanceRequired:
        raise
    except SourceEngineUnavailable as exc:
        raise HTTPException(503, str(exc), headers={"Retry-After": str(exc.retry_after)})
    except httpx.PoolTimeout as exc:
        raise HTTPException(503, "Local source HTTP connection capacity is busy; retry shortly",
                            headers={"Retry-After": "1"}) from exc
    except Exception as exc:
        raise HTTPException(502, f"Source page failed: {exc}")
    finally:
        # A failed publication fence or cancellation still owns this response.
        # Drain repeated cancellation so its HTTP pool slot is really released.
        if upstream is not None:
            close = asyncio.create_task(upstream.aclose())
            cancelled = False
            while not close.done():
                try:
                    await asyncio.shield(close)
                except asyncio.CancelledError:
                    cancelled = True
            close.result()
            if cancelled:
                raise asyncio.CancelledError()


def _cover_placeholder(title: str = "No cover") -> Response:
    safe = (title or "No cover")[:34].replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    svg = f'<svg xmlns="http://www.w3.org/2000/svg" width="401" height="601"><rect width="100%" height="100%" fill="#22262d"/><rect y="430" width="100%" height="170" fill="#121419"/><text x="50%" y="505" fill="#b9c0ca" text-anchor="middle" font-family="sans-serif" font-size="19">{safe}</text></svg>'
    return Response(svg, media_type="image/svg+xml", headers={"Cache-Control": "no-store"})


async def _load_cover(hint: dict, *, manga_id: int | None = None) -> tuple[bytes, str]:
    url = (hint or {}).get("thumbnail_url") or ""
    referer = (hint or {}).get("base_url") or ""
    if url:
        try:
            content, content_type = await source_host.fetch_external_binary(url, referer, timeout=settings.cover_fetch_timeout)
            if (content_type or "").lower().startswith("image/"):
                return content, content_type
        except Exception:
            pass
    source_manga_id = manga_id or (hint or {}).get("source_manga_id")
    if source_manga_id:
        content, content_type = await source_host.fetch_manga_thumbnail(int(source_manga_id), timeout=settings.cover_fetch_timeout)
        if not (content_type or "").lower().startswith("image/"):
            raise RuntimeError("Thumbnail endpoint did not return an image")
        return content, content_type
    raise RuntimeError("No usable cover source")


@app.get("/api/v1/covers/{entry_id}")
async def proxy_cover(entry_id: int):
    from .metadata_fields import artwork_source
    canonical_id = await cover_metadata(service.canonical_entry_id, entry_id)
    chosen=await cover_metadata(artwork_source,db,canonical_id)
    if chosen is not None:
        if not chosen:raise HTTPException(404,'Selected artwork is cleared or its source record is unavailable')
        return await proxy_source_cover(chosen)
    cached = await cover_cache.read_async(f"entry-{entry_id}")
    if cached:
        content, content_type = cached
        return Response(content, media_type=content_type, headers={"Cache-Control": "public, max-age=21600"})
    canonical_id = await cover_metadata(service.canonical_entry_id, entry_id)

    # Re-use an already cached canonical-entry cover before any source request.
    for key in (f"entry-{canonical_id}",):
        cached = await cover_cache.read_async(key)
        if cached:
            content, content_type = cached
            if key != f"entry-{entry_id}":
                await cover_cache.write_async(f"entry-{entry_id}", content, content_type)
            return Response(content, media_type=content_type, headers={"Cache-Control": "public, max-age=21600"})

    hints = await cover_metadata(service.entry_cover_hints, canonical_id)

    # A unified entry can be backed by several sources. Try every matched record:
    # one site may omit/block its cover while another has a healthy thumbnail.
    async def load_from_any_match() -> tuple[bytes, str]:
        last_error: Exception | None = None
        seen_manga_ids: set[int] = set()
        for hint in hints:
            manga_id = int((hint or {}).get("source_manga_id") or 0)
            if manga_id and manga_id not in seen_manga_ids:
                seen_manga_ids.add(manga_id)
                shared = await cover_cache.read_async(f"source-{manga_id}")
                if shared:
                    return shared
            try:
                content, content_type = await _load_cover(hint)
                if manga_id:
                    await cover_cache.write_async(f"source-{manga_id}", content, content_type)
                return content, content_type
            except Exception as exc:
                last_error = exc
        raise last_error or RuntimeError("No usable cover source")

    result = await cover_cache.get(f"entry-{canonical_id}", load_from_any_match)
    if result:
        content, content_type = result
        if canonical_id != entry_id:
            await cover_cache.write_async(f"entry-{entry_id}", content, content_type)
        return Response(content, media_type=content_type, headers={"Cache-Control": "public, max-age=21600"})
    # A sparse search/list record may know the source manga id before its final
    # thumbnail URL. Promote one bounded detail hydration so missing cards repair
    # themselves instead of depending on the user opening the Matrix inspector.
    await service.recover_entry_cover(canonical_id)
    try:
        title = await cover_metadata(service.entry_preferred_title, canonical_id)
    except KeyError:
        raise HTTPException(404, "Entry not found")
    return _cover_placeholder(title)


@app.get("/api/v1/source-covers/{manga_id}")
async def proxy_source_cover(manga_id: int):
    """Fast, cached private-source thumbnail for matrix/manual-match browsing."""
    async def load():
        hint = await cover_metadata(service.source_cover_hint, int(manga_id))
        return await _load_cover(hint, manga_id=int(manga_id))
    result = await cover_cache.get(f"source-{int(manga_id)}", load)
    if result:
        content, content_type = result
        return Response(content, media_type=content_type, headers={"Cache-Control": "public, max-age=21600"})
    return _cover_placeholder("No cover")


@app.get("/api/admin/maintenance/consolidate-preview", dependencies=[Depends(admin_guard)])
async def admin_consolidate_preview():
    return await run_blocking(service.auto_merge_preview)


@app.post("/api/admin/maintenance/consolidate", dependencies=[Depends(admin_guard)])
async def admin_consolidate():
    task_id = task_registry.create("Auto-merge duplicates", message="Queued behind foreground work", origin="manual")

    async def runner() -> None:
        try:
            async with service._background_task_slot(wait_for_idle=True):
                await run_blocking(service.repair_catalog_duplicates, 10000, task_id=task_id)
            service.schedule_duplicate_snapshot_refresh(delay=0.0)
        except asyncio.CancelledError:
            task_registry.cancel(task_id)
            raise
        except RuntimeError as exc:
            if task_registry.cancel_requested(task_id):
                task_registry.cancel(task_id)
            else:
                task_registry.fail(task_id, exc)
        except Exception as exc:
            task_registry.fail(task_id, exc)

    runtime = service._track_auxiliary(runner())
    task_registry.bind_runtime(task_id, runtime)
    return {"queued": True, "task_id": task_id}


@app.post("/api/admin/maintenance/reset-covers", dependencies=[Depends(admin_guard)])
async def admin_reset_covers():
    """Invalidate every cached cover; source thumbnails are reacquired on demand."""
    removed = await cover_cache.clear_all()
    return {"ok": True, "removed_files": removed}


@app.post("/api/admin/entries/{entry_id}/refresh-covers", dependencies=[Depends(admin_guard)])
async def admin_refresh_entry_covers(entry_id: int):
    """Invalidate the unified cover plus every active matched-source cover."""
    canonical = service.canonical_entry_id(int(entry_id))
    try:
        hints = service.entry_cover_hints(canonical)
        service.entry_preferred_title(canonical)
    except KeyError:
        raise HTTPException(404, "Entry not found")
    keys = {f"entry-{int(entry_id)}", f"entry-{canonical}"}
    source_ids = []
    for hint in hints:
        manga_id = int((hint or {}).get("source_manga_id") or 0)
        if manga_id:
            keys.add(f"source-{manga_id}")
            source_ids.append(manga_id)
    removed = await cover_cache.clear_keys(keys)
    return {"ok": True, "entry_id": canonical, "source_manga_ids": sorted(set(source_ids)), "removed_files": removed}


@app.get("/api/admin/mixed-entries", dependencies=[Depends(admin_guard)])
async def admin_mixed_entries(state: str = Query("needs_review", pattern="^(needs_review|reviewed|all)$"),
                              q: str = Query("", max_length=500), offset: int = Query(0, ge=0), limit: int = Query(50, ge=1, le=100)):
    return await catalogue_response(service.mixed_entry_candidates, state=state, query=q, offset=offset, limit=limit)


@app.get("/api/admin/mixed-entries/{entry_id}", dependencies=[Depends(admin_guard)])
async def admin_mixed_entry_detail(entry_id: int):
    try:
        return await catalogue_response(service.mixed_entry_detail, entry_id)
    except KeyError as exc:
        raise HTTPException(404, str(exc)) from exc


@app.post("/api/admin/mixed-entries/{entry_id}/review", dependencies=[Depends(admin_guard)])
async def admin_mixed_entry_review(entry_id: int, body: MixedEntryReviewRequest):
    try:
        return await run_blocking(service.review_mixed_entry, entry_id, body.fingerprint, body.decision)
    except KeyError as exc:
        raise HTTPException(404, str(exc)) from exc
    except ValueError as exc:
        raise HTTPException(409, str(exc)) from exc


@app.get("/api/admin/maintenance/unmatch-duplicates", dependencies=[Depends(admin_guard)])
def admin_unmatch_duplicate_candidates():
    return {"items": service.duplicate_unmatch_candidates()}


@app.post("/api/admin/entries/{entry_id}/unmatch-duplicates", dependencies=[Depends(admin_guard)])
def admin_unmatch_duplicate_records(entry_id: int, request: DuplicateUnmatchRequest):
    try:
        return service.unmatch_duplicate_records(entry_id, [r.model_dump() for r in request.records])
    except KeyError:
        raise HTTPException(404, "Entry not found")
    except ValueError as exc:
        raise HTTPException(409, str(exc))


@app.post("/api/admin/entries/{entry_id}/unbind-auto-matches", dependencies=[Depends(admin_guard)])
def admin_unbind_entry_automatic_records(entry_id: int):
    try:
        return service.unbind_entry_automatic_records(entry_id)
    except KeyError:
        raise HTTPException(404, "Entry not found")
    except ValueError as exc:
        raise HTTPException(409, str(exc))


@app.post("/api/admin/entries/{entry_id}/redo-automation", dependencies=[Depends(admin_guard)])
async def admin_redo_entry_automation(entry_id: int):
    try:
        return service.schedule_entry_automation_redo(entry_id)
    except KeyError:
        raise HTTPException(404, "Entry not found")


@app.get("/api/admin/manual-actions", dependencies=[Depends(admin_guard)])
def admin_manual_actions():
    return service.manual_actions_payload()


@app.get("/api/admin/manual-groups", dependencies=[Depends(admin_guard)])
def admin_manual_groups():
    return service.manual_groups_payload()


@app.get("/api/admin/manual-groups/{group_id}/candidates", dependencies=[Depends(admin_guard)])
def admin_manual_group_candidates(group_id: str, q: str = "", limit: int = Query(60, ge=1, le=100)):
    try:
        return service.manual_group_candidates(group_id, q, limit)
    except KeyError:
        raise HTTPException(404, "Manual group not found")


@app.post("/api/admin/manual-groups/{group_id}/members", dependencies=[Depends(admin_guard)])
def admin_add_manual_group_member(group_id: str, request: ManualGroupMemberRequest):
    try:
        return service.add_manual_group_member(group_id, request.source_id, request.source_manga_id)
    except KeyError:
        raise HTTPException(404, "Manual group or source record not found")


@app.delete("/api/admin/manual-groups/{group_id}/members/{source_id}/{source_manga_id}", dependencies=[Depends(admin_guard)])
def admin_remove_manual_group_member(group_id: str, source_id: str, source_manga_id: int):
    try:
        return service.remove_manual_group_member(group_id, source_id, source_manga_id)
    except KeyError:
        raise HTTPException(404, "Manual group member not found")



class ManualResetRequest(BaseModel):
    revision: str = Field(min_length=64,max_length=64,pattern=r"^[a-f0-9]{64}$")
    confirmation: str


@app.get("/api/admin/maintenance/reset-all-manual-actions", dependencies=[Depends(admin_guard)])
def admin_manual_reset_preview():
    from . import manual_reset
    return manual_reset.preview(service, db)


@app.post("/api/admin/maintenance/reset-all-manual-actions", dependencies=[Depends(admin_guard)])
async def admin_reset_all_manual_actions(request: ManualResetRequest):
    from . import manual_reset
    if request.confirmation != "RESET ALL MANUAL ACTIONS":
        raise HTTPException(400,"Type RESET ALL MANUAL ACTIONS to confirm.")
    async with _database_admin_lock:
        async with _exclusive_catalog_reset():
            reason=service.begin_automatic_duplicate_pause("manual-actions-reset")
            try:
                await service.cancel_background_tasks()
                return await run_blocking(manual_reset.reset,service,db,request.revision)
            except manual_reset.StaleManualPreview as exc:
                raise HTTPException(409,str(exc)) from exc
            finally:
                service.resume_background_tasks()
                source_attention.start(service,source_host)
                service.schedule_continuous_catalog_discovery()
                service.end_automatic_duplicate_pause(reason,schedule=True)


@app.post("/api/admin/maintenance/reset-manual-groups", dependencies=[Depends(admin_guard)])
def admin_reset_manual_groups():
    return service.reset_manual_groups()


@app.post("/api/admin/maintenance/reset-manual-matches", dependencies=[Depends(admin_guard)])
def admin_reset_manual_matches():
    return service.reset_manual_matches()


@app.post("/api/admin/maintenance/reset-database", dependencies=[Depends(admin_guard)])
async def admin_reset_database():
    async with _database_admin_lock:
        async with _exclusive_catalog_reset():
            return await _admin_reset_database()


async def _admin_reset_database():
    # Snapshot subscriptions *before* deleting catalog rows. Their AutoSourcarr
    # entry ids live in the main-Suwayomi library URLs, so keeping the same ids lets
    # existing subscriptions survive the reset while their matches are rediscovered.
    snapshot = await service.snapshot_subbed_reset_seeds()
    if not snapshot.get("ok"):
        raise HTTPException(503, f"Database reset aborted: unable to snapshot subscribed entries: {snapshot.get('error') or 'unknown error'}")

    # Preserve old source IDs only as acceleration hints. They are never restored
    # blindly: subscribed entries re-fetch/re-score them first, and the remaining
    # catalog is replayed through the current clean matcher after Subbed recovers.
    seeds = list(snapshot.get("seeds") or [])
    canonical_seed_ids = [
        service.canonical_entry_id(int(seed.get("entry_id") or 0))
        for seed in seeds if int(seed.get("entry_id") or 0) > 0
    ]
    seed_hint_map = await run_blocking(service.reset_rebuild_hints_for_entries, canonical_seed_ids)
    for seed in seeds:
        try:
            old_canonical = service.canonical_entry_id(int(seed.get("entry_id") or 0))
        except Exception:
            old_canonical = 0
        seed["rebuild_hints"] = list(seed_hint_map.get(old_canonical) or [])
    rebuild_hints = await run_blocking(service.capture_reset_rebuild_hints)
    source_preferences = await asyncio.to_thread(service.capture_source_preferences)

    pause_reason = service.begin_automatic_duplicate_pause("database-reset")
    try:
        # Stop background writers first so a feed/metadata/crawler job cannot
        # repopulate the database halfway through the reset. The duplicate thread
        # also observes the pause Event and exits its pair loop if already running.
        await service.cancel_background_tasks()

        cover_files_removed = await cover_cache.clear_all()
        result = await run_blocking(service.reset_database)
        result["cover_cache_files_removed"] = cover_files_removed
        try:
            await asyncio.wait_for(service.sync_sources(), timeout=8.0)
            result["sources_resynced"] = True
        except Exception as exc:
            result["sources_resynced"] = False
            result["source_sync_error"] = str(exc)
        result["source_preferences_restored"] = await run_sync(service.restore_source_preferences, source_preferences)
        result["subbed_snapshot"] = len(seeds)
        result["subbed_restored"] = await run_blocking(service.restore_subbed_reset_seeds, seeds)
        result["manual_matches"] = await run_blocking(service.restore_persistent_manual_matches)
        result["rebuild_hint_records"] = int(rebuild_hints.get("records") or 0)
        service.resume_background_tasks()
        result["subbed_rebrowse_queued"] = service.schedule_subbed_reset_rebrowse(seeds) if seeds else False
        result["catalog_hint_replay_queued"] = service.schedule_reset_hint_replay(str(rebuild_hints.get("path") or ""))
        # reset_database cancelled every refresh task, including the permanent
        # low-priority crawler. Restart it after the priority rebuild queues exist.
        result["continuous_catalog_queued"] = service.schedule_continuous_catalog_discovery()
        return result
    finally:
        service.resume_background_tasks()
        source_attention.start(service, source_host)
        service.end_automatic_duplicate_pause(pause_reason, schedule=True)


@app.post("/api/admin/maintenance/full-reset", dependencies=[Depends(admin_guard)])
async def admin_full_reset():
    async with _database_admin_lock:
        async with _exclusive_catalog_reset():
            return await _admin_full_reset()


async def _admin_full_reset():
    # Keep configuration, but rebuild all catalog/user relationship state from a
    # clean slate. Main-Suwayomi subscriptions are snapshotted so their existing
    # AutoSourcarr URLs remain valid after the data reset.
    snapshot = await service.snapshot_subbed_reset_seeds()
    if not snapshot.get("ok"):
        raise HTTPException(503, f"Full reset aborted: unable to snapshot subscribed entries: {snapshot.get('error') or 'unknown error'}")
    seeds = list(snapshot.get("seeds") or [])
    # Full reset intentionally discards old source hints/manual state. Keep only the
    # main-library identity/title metadata needed to recreate subscribed anchors.
    for seed in seeds:
        seed["rebuild_hints"] = []
        seed["title"] = str(seed.get("library_title") or seed.get("title") or "").strip()
        seed["manual_title"] = False
        seed["description"] = ""
        seed["thumbnail_url"] = ""
        seed["status"] = 0
        seed["author"] = ""
        seed["artist"] = ""
        seed["genre"] = ""
    pause_reason = service.begin_automatic_duplicate_pause("full-reset")
    try:
        await service.cancel_background_tasks()

        cover_files_removed = await cover_cache.clear_all()
        result = await run_blocking(service.full_reset_database)
        result["cover_cache_files_removed"] = cover_files_removed
        try:
            await asyncio.wait_for(service.sync_sources(), timeout=8.0)
            result["sources_resynced"] = True
        except Exception as exc:
            result["sources_resynced"] = False
            result["source_sync_error"] = str(exc)
        result["subbed_snapshot"] = len(seeds)
        result["subbed_restored"] = await run_blocking(service.restore_subbed_reset_seeds, seeds)
        service.resume_background_tasks()
        result["subbed_rebrowse_queued"] = service.schedule_subbed_reset_rebrowse(seeds) if seeds else False
        result["continuous_catalog_queued"] = service.schedule_continuous_catalog_discovery()
        return result
    finally:
        service.resume_background_tasks()
        source_attention.start(service, source_host)
        service.end_automatic_duplicate_pause(pause_reason)


def _backup_import_receipt(import_id: str) -> dict[str, Any] | None:
    """A durable acknowledgement of an accepted upload, even after its task ends."""
    try:
        with db.catalog() as c:
            row = c.execute("SELECT id,filename,state FROM import_runs WHERE id=?", (import_id,)).fetchone()
        if row is None:
            return None
        return {"queued": True, "import_id": str(row["id"]),
                "filename": str(row["filename"]), "state": str(row["state"])}
    finally:
        db.close_current_thread()


@app.get("/api/admin/imports/{import_id}/receipt", dependencies=[Depends(admin_guard)])
async def admin_backup_import_receipt(import_id: str):
    if not re.fullmatch(r"[0-9a-f]{32}", import_id):
        raise HTTPException(400, "Invalid import identity")
    receipt = await run_sync(_backup_import_receipt, import_id)
    if receipt is None:
        raise HTTPException(404, "No accepted upload is recorded for this identity")
    return receipt


@app.post("/api/admin/import/mihon-backup", dependencies=[Depends(admin_guard)])
async def admin_import_mihon_backup(request: Request, x_filename: str | None = Header(None)):
    """Upload a Mihon/Tachiyomi protobuf backup and queue an add-only library import."""
    try:
        size_hint = int(request.headers.get("content-length", "0"))
    except ValueError:
        raise HTTPException(400, "Invalid Content-Length")
    if size_hint < 0:
        raise HTTPException(400, "Invalid Content-Length")
    if size_hint > MAX_BACKUP_BYTES:
        raise HTTPException(413, "Backup file is larger than 128 MiB")
    # The explicit encoded header preserves literal percent signs in legacy names.
    encoded_filename = request.headers.get("x-filename-utf8")
    if encoded_filename is not None:
        if not encoded_filename.isascii() or re.search(r"%(?![0-9a-fA-F]{2})", encoded_filename):
            raise HTTPException(400, "Invalid UTF-8 backup filename")
        try:
            submitted_filename = unquote(encoded_filename, encoding="utf-8", errors="strict")
        except UnicodeDecodeError:
            raise HTTPException(400, "Invalid UTF-8 backup filename") from None
    else:
        submitted_filename = str(x_filename or "backup.tachibk")
    if any(ord(char) < 32 or ord(char) == 127 for char in submitted_filename):
        raise HTTPException(400, "Invalid backup filename")
    if len(submitted_filename.encode("utf-8")) > 240:
        raise HTTPException(400, "Backup filename is too long")
    filename = Path(submitted_filename).name or "backup.tachibk"
    lowered = filename.casefold()
    if not (lowered.endswith(".tachibk") or lowered.endswith(".proto.gz") or lowered.endswith(".gz")):
        raise HTTPException(400, "Expected a .tachibk or .proto.gz backup")
    import_id = str(request.headers.get("x-import-id") or uuid.uuid4().hex).strip().lower()
    if not re.fullmatch(r"[0-9a-f]{32}", import_id):
        raise HTTPException(400, "Invalid import identity")
    existing = await run_sync(_backup_import_receipt, import_id)
    if existing is not None:
        if existing["filename"] != filename:
            raise HTTPException(409, "This import identity already belongs to another filename")
        return existing
    job_dir = settings.data_dir / "imports" / import_id
    try:
        job_dir.mkdir(parents=True, exist_ok=False)
    except FileExistsError:
        # A simultaneous upload may have finished between the receipt read and
        # directory creation. Never overwrite an admitted upload's files.
        existing = await run_sync(_backup_import_receipt, import_id)
        if existing is not None and existing["filename"] == filename:
            return existing
        raise HTTPException(409, "An upload with this identity is still being received")
    raw_path = job_dir / filename
    readable_path = job_dir / "decoded-library.json"
    queued = False
    try:
        size = 0
        # Enforce the limit while receiving, even without Content-Length. Never
        # materialize an unbounded request body on the event loop.
        with raw_path.open("xb") as output:
            async for chunk in request.stream():
                size += len(chunk)
                if size > MAX_BACKUP_BYTES:
                    raise HTTPException(413, "Backup file is larger than 128 MiB")
                if chunk:
                    await run_sync(output.write, chunk)
        if size == 0:
            raise HTTPException(400, "Backup file is empty")
        result = service.schedule_backup_import(raw_path, readable_path, filename=filename, import_id=job_dir.name)
        queued = bool(result.get("queued"))
        return result
    finally:
        if not queued:
            for path in (raw_path, readable_path):
                path.unlink(missing_ok=True)
            try:
                job_dir.rmdir()
            except OSError:
                pass


@app.get("/api/admin/imports", dependencies=[Depends(admin_guard)])
async def admin_import_runs(limit: int = Query(20, ge=1, le=100)):
    return await catalogue_response(lambda: {"runs": service.list_import_runs(limit)})


@app.post("/api/admin/imports/clear-history", dependencies=[Depends(admin_guard)])
def admin_clear_import_history():
    return {"ok": True, **service.clear_import_history()}


@app.post("/api/admin/imports/reset-rejections", dependencies=[Depends(admin_guard)])
async def admin_reset_import_rejections():
    result = service.reset_import_match_rejections()
    queued = 0
    for import_id in result.pop("import_ids", []):
        try:
            if service.schedule_import_translation_enrichment(import_id, origin="manual"):
                queued += 1
        except Exception:
            pass
    return {"ok": True, **result, "enrichment_tasks_queued": queued}


@app.post("/api/admin/imports/reset-manual-autoresolve", dependencies=[Depends(admin_guard)])
def admin_reset_import_manual_autoresolve():
    return {"ok": True, **service.reset_import_manual_autoresolve()}


@app.get("/api/admin/imports/{import_id}/items", dependencies=[Depends(admin_guard)])
async def admin_import_items(import_id: str, state: str = "", limit: int = Query(1000, ge=1, le=5000),
                             offset: int = Query(0, ge=0), review: str = "all"):
    try:
        return await catalogue_response(service.import_run_items, import_id, state=state, limit=limit, offset=offset, review=review)
    except ValueError as exc:
        raise HTTPException(400, str(exc))
    except KeyError:
        raise HTTPException(404, "Import run not found")


@app.get("/api/admin/imports/{import_id}/items/{item_id}", dependencies=[Depends(admin_guard)])
async def admin_import_item(import_id: str, item_id: int):
    try:
        return await catalogue_response(service.import_run_item, import_id, item_id)
    except KeyError:
        raise HTTPException(404, "Import item not found")


@app.get("/api/admin/imports/{import_id}/items/{item_id}/suggestions", dependencies=[Depends(admin_guard)])
async def admin_import_item_suggestions(import_id: str, item_id: int, q: str = ""):
    try:
        return await catalogue_response(service.import_item_suggestions, import_id, item_id, query=q)
    except KeyError:
        raise HTTPException(404, "Import item not found")


@app.get("/api/admin/imports/{import_id}/items/{item_id}/suggestions/live", dependencies=[Depends(admin_guard)])
async def admin_import_item_live_suggestions(import_id: str, item_id: int, q: str = ""):
    try:
        return await service.import_item_live_suggestions(import_id, item_id, query=q)
    except KeyError:
        raise HTTPException(404, "Import item not found")


@app.post("/api/admin/imports/{import_id}/items/{item_id}/retry", dependencies=[Depends(admin_guard)])
async def admin_retry_import_item(import_id: str, item_id: int, background: bool = Query(False)):
    try:
        if background:
            return service.schedule_import_item_retry(import_id, item_id)
        return await service.retry_import_item(import_id, item_id)
    except KeyError:
        raise HTTPException(404, "Import item not found")


@app.post("/api/admin/imports/{import_id}/items/{item_id}/match/{entry_id}", dependencies=[Depends(admin_guard)])
async def admin_match_import_item(import_id: str, item_id: int, entry_id: int, background: bool = Query(False)):
    try:
        if background:
            return service.schedule_import_item_match(import_id, item_id, entry_id)
        return await service.match_import_item(import_id, item_id, entry_id)
    except KeyError:
        raise HTTPException(404, "Import item or entry not found")
    except Exception as exc:
        raise HTTPException(502, str(exc))


@app.post("/api/admin/imports/{import_id}/items/{item_id}/reject", dependencies=[Depends(admin_guard)])
async def admin_reject_import_item(import_id: str, item_id: int):
    try:
        return service.schedule_import_item_reject(import_id, item_id)
    except KeyError:
        raise HTTPException(404, "Import item not found")
    except ValueError as exc:
        raise HTTPException(409, str(exc))


@app.post("/api/admin/imports/{import_id}/items/{item_id}/skip", dependencies=[Depends(admin_guard)])
def admin_skip_import_item(import_id: str, item_id: int):
    try:
        return service.skip_import_item(import_id, item_id)
    except KeyError:
        raise HTTPException(404, "Import item not found")


@app.post("/api/admin/maintenance/refresh-feeds", dependencies=[Depends(admin_guard)])
async def admin_refresh_feeds():
    return service.schedule_feed_refresh()


@app.post("/api/admin/maintenance/refresh-metadata", dependencies=[Depends(admin_guard)])
async def admin_refresh_metadata(limit: int = Query(500, ge=1, le=5000)):
    """Queue detail hydration for entries missing cover/description/creator/status data."""
    return service.queue_sparse_metadata_enrichment(limit, track=True)


@app.get("/api/admin/browse", dependencies=[Depends(admin_guard)])
async def admin_browse(
    mode: str = Query("POPULAR", pattern="^(POPULAR|LATEST|SEARCH)$"),
    q: str = "",
    page: int = Query(1, ge=1),
    min_matches: int = Query(0, ge=0),
    match_count: int | None = Query(None, ge=0),
    sources: list[str] | None = Query(None),
    sort: str = Query("relevance", pattern="^(relevance|sources|title)$"),
    page_size: int | None = Query(None, ge=1, le=500),
    force_live: bool = False,
    source_id: str = "",
):
    """Companion-app browser using the same federated discovery pipeline as the extension."""
    if source_id:
        return await catalogue_response(source_browse, service, source_id, search=q,
            page=page, page_size=page_size or settings.browse_page_size,
            min_matches=min_matches, exact_match_count=match_count, sort=sort)
    return await service.federated(
        mode, page=page, query=q, source_ids=sources, min_matches=min_matches,
        exact_match_count=match_count, sort=sort, page_size=page_size, force_live=force_live,
    )


@app.post("/api/admin/maintenance/discover-additional", dependencies=[Depends(admin_guard)])
async def admin_discover_additional_catalog(limit: int = Query(200, ge=1, le=5000)):
    return service.schedule_catalog_additional_record_scan(limit)


@app.get("/api/admin/duplicates", dependencies=[Depends(admin_guard)])
async def admin_duplicates():
    subscription = service.cached_subbed_payload()
    service.schedule_status_aux_refresh(delay=0.0)
    groups = service.duplicate_groups_cached()
    status = service.duplicate_snapshot_summary()
    if groups is None or status.get("stale"):
        service.schedule_duplicate_snapshot_refresh(delay=0.0)

    def build():
        # Capture the completed snapshot on its owner loop; do its DB lookups and
        # potentially multi-megabyte JSON encoding in the local read lane.
        ids = [int(x) for x in (subscription.get("entry_ids") or []) if int(x) > 0]
        return {
            "groups": groups or [], "pending": groups is None or bool(status.get("stale")),
            "stale": bool(groups is not None and status.get("stale")),
            "subbed_entry_ids": list(dict.fromkeys(service.canonical_entry_ids(ids))),
            "subscription_ok": subscription.get("ok"),
            "subscription_pending": subscription.get("ok") is None,
            "subscription_error": str(subscription.get("error") or ""),
            "automatic_matching": service.automatic_duplicate_matching_status(),
        }
    return await catalogue_response(build)


@app.post("/api/admin/duplicates/automatic", dependencies=[Depends(admin_guard)])
async def admin_run_automatic_duplicates():
    queued = service.schedule_automatic_duplicate_matching(delay=0.0)
    status = await run_blocking(service.automatic_duplicate_matching_status)
    return {"queued": bool(queued), "already_running": bool(status.get("running") and not queued), "status": status}


@app.post("/api/admin/duplicates/review", dependencies=[Depends(admin_guard)])
async def admin_duplicate_review(request: DuplicateReviewRequest):
    try:
        result = await run_blocking(service.review_duplicate, request.left_entry_id, request.right_entry_id, request.decision)
        service.schedule_duplicate_snapshot_refresh(delay=0.0)
        return result
    except KeyError:
        # A stale UI can race with a background auto-merge. Give it a fresh queue
        # instead of leaving the duplicate reviewer stuck on dead ids.
        return {"decision": "stale", "groups": service.duplicate_groups_cached() or [], "pending": service.duplicate_groups_cached() is None}



@app.get("/api/admin/ui-preferences", dependencies=[Depends(admin_guard)])
def admin_ui_preferences(request: Request):
    identity = _ui_user_context(request)
    return {
        "user": identity["user"],
        "identity_header": identity["header"],
        "authelia": bool(identity["authelia"]),
        "identity_diagnostics": {
            "proxy_detected": bool(identity.get("proxy_detected")),
            "proxy_headers": list(identity.get("proxy_headers") or []),
            "identity_headers_present": list(identity.get("identity_headers_present") or []),
            "custom_header": str(identity.get("custom_header") or ""),
            "remote_email": str(identity.get("email") or ""),
            "remote_groups": list(identity.get("groups") or []),
            "host": str(identity.get("host") or ""),
            "direct_access": bool(identity.get("direct_access")),
        },
        "values": db.user_ui_preferences(str(identity["key"])),
    }


@app.put("/api/admin/ui-preferences", dependencies=[Depends(admin_guard)])
def admin_update_ui_preferences(request: Request, update: UiPreferencesUpdate):
    identity = _ui_user_context(request)
    try:
        values = db.set_user_ui_preferences(str(identity["key"]), update.values, merge=True)
    except ValueError as exc:
        raise HTTPException(400, str(exc))
    return {
        "user": identity["user"],
        "identity_header": identity["header"],
        "authelia": bool(identity["authelia"]),
        "identity_diagnostics": {
            "proxy_detected": bool(identity.get("proxy_detected")),
            "proxy_headers": list(identity.get("proxy_headers") or []),
            "identity_headers_present": list(identity.get("identity_headers_present") or []),
            "custom_header": str(identity.get("custom_header") or ""),
            "remote_email": str(identity.get("email") or ""),
            "remote_groups": list(identity.get("groups") or []),
            "host": str(identity.get("host") or ""),
            "direct_access": bool(identity.get("direct_access")),
        },
        "values": values,
    }


@app.post("/api/admin/sources/sync", dependencies=[Depends(admin_guard)])
async def admin_sync_sources():
    return {"items": await service.sync_sources()}


@app.get("/api/admin/sources", dependencies=[Depends(admin_guard)])
def admin_sources():
    return {"items": service.list_sources(), "groups": service.list_source_groups()}


@app.post("/api/admin/source-groups", dependencies=[Depends(admin_guard)])
def admin_create_source_group(request: SourceGroupCreate):
    try:
        return service.create_source_group(request.name, request.priority)
    except ValueError as exc:
        raise HTTPException(400, str(exc))


@app.put("/api/admin/source-groups/{group_id}", dependencies=[Depends(admin_guard)])
def admin_rename_source_group(group_id: int, request: SourceGroupUpdate):
    try:
        return service.update_source_group(group_id, name=request.name, priority=request.priority)
    except KeyError:
        raise HTTPException(404, "Source group not found")
    except ValueError as exc:
        raise HTTPException(400, str(exc))


@app.delete("/api/admin/source-groups/{group_id}", dependencies=[Depends(admin_guard)])
def admin_delete_source_group(group_id: int):
    try:
        return service.delete_source_group(group_id)
    except KeyError:
        raise HTTPException(404, "Source group not found")


@app.put("/api/admin/sources/{source_id}", dependencies=[Depends(admin_guard)])
def admin_update_source(source_id: str, update: SourceUpdate):
    try:
        return service.update_source(source_id, **update.model_dump(exclude_unset=True))
    except KeyError:
        raise HTTPException(404, "Source not found")
    except ValueError as exc:
        raise HTTPException(400, str(exc))


async def _source_control_body(request: Request, limit: int, timeout: float = 30.0) -> bytes:
    """Bound uploads even without Content-Length; never include their data in errors."""
    length = request.headers.get("content-length")
    if length is not None:
        try:
            size = int(length)
        except ValueError:
            raise HTTPException(400, "Invalid request length") from None
        if size < 0:
            raise HTTPException(400, "Invalid request length")
        if size > limit:
            raise HTTPException(413, "Upload exceeds the allowed size")
    data = bytearray()
    try:
        async with asyncio.timeout(timeout):
            async for chunk in request.stream():
                if len(data) + len(chunk) > limit:
                    raise HTTPException(413, "Upload exceeds the allowed size")
                data.extend(chunk)
    except TimeoutError:
        raise HTTPException(408, "Upload took too long; retry with a stable connection") from None
    return bytes(data)


def _source_control_error(exc: Exception) -> HTTPException:
    if isinstance(exc, source_controls.SourceControlFailed):
        return HTTPException(502, str(exc))
    if isinstance(exc, source_controls.SourcePreferencesConflict):
        return HTTPException(409, str(exc))
    if isinstance(exc, ValueError):
        return HTTPException(400, str(exc))
    if isinstance(exc, SourceEngineUnavailable):
        return HTTPException(503, "Source engine is starting or unavailable; retry shortly",
                             headers={"Retry-After": str(exc.retry_after)})
    # Preferences can contain passwords, tokens or account names. Never echo the
    # engine's GraphQL request/response, or attach its exception to global logs.
    return HTTPException(503, "Source operation could not be confirmed. Refresh before retrying.")


@app.get("/api/admin/sources/{source_id}/preferences", dependencies=[Depends(admin_guard)])
async def admin_source_preferences(source_id: str):
    try:
        result = await source_controls.preferences_snapshot(source_host, source_id)
        return JSONResponse(result, headers={"Cache-Control": "no-store"})
    except Exception as exc:
        raise _source_control_error(exc) from None


@app.put("/api/admin/sources/{source_id}/preferences", dependencies=[Depends(admin_guard)])
async def admin_save_source_preference(source_id: str, request: Request):
    data = await _source_control_body(request, 64 * 1024)
    try:
        change = json.loads(data)
    except (ValueError, UnicodeError):
        raise HTTPException(400, "Source setting must be valid JSON") from None
    if not isinstance(change, dict):
        raise HTTPException(400, "Source setting must be an object")
    try:
        async with _database_admin_lock:
            result = await source_controls.update_preference(source_host, source_id, change)
        # Suwayomi reloads the source after preferences change; refresh its stored
        # domain/language metadata without delaying the saved-settings response.
        service.schedule_source_reconciliation(delay=0.25)
        return JSONResponse(result, headers={"Cache-Control": "no-store"})
    except Exception as exc:
        raise _source_control_error(exc) from None


_source_extension_icons: dict[str, str] = {}


def _remember_extension_icons(extensions) -> None:
    for item in extensions or []:
        if not isinstance(item, dict):
            continue
        pkg = str(item.get("pkgName") or "").strip()
        icon = str(item.get("iconUrl") or "").strip()
        if pkg and icon:
            _source_extension_icons[pkg] = icon


@app.get("/api/v1/source-icon/{source_id}")
async def source_icon(source_id: str):
    with db.catalog() as c:
        row = c.execute("SELECT icon_url FROM source_config WHERE source_id=?", (str(source_id),)).fetchone()
    if not row or not str(row["icon_url"] or "").strip():
        raise HTTPException(404, "Source icon not available")
    try:
        body, content_type = await source_host.fetch_asset(str(row["icon_url"]))
        return Response(content=body, media_type=content_type, headers={"Cache-Control": "public, max-age=86400"})
    except Exception as exc:
        raise HTTPException(404, f"Unable to load source icon: {exc}")


@app.get("/api/v1/source-extension-icon/{pkg_name}")
async def source_extension_icon(pkg_name: str):
    icon = str(_source_extension_icons.get(str(pkg_name)) or "").strip()
    if not icon:
        raise HTTPException(404, "Extension icon not available")
    try:
        body, content_type = await source_host.fetch_asset(icon)
        return Response(content=body, media_type=content_type, headers={"Cache-Control": "public, max-age=86400"})
    except Exception as exc:
        raise HTTPException(404, f"Unable to load extension icon: {exc}")


def _repository_error(exc: Exception):
    if isinstance(exc, (RepositoryConflict, OSError)):
        return HTTPException(409, str(exc))
    return HTTPException(400, str(exc))


@app.get("/api/admin/source-repositories", dependencies=[Depends(admin_guard)])
def admin_source_repositories():
    try:
        return {**repository_snapshot(env_file_path(settings.settings_config_dir, "source_host")),
                'can_restart': settings.source_engine_mode == 'integrated'}
    except (ValueError, OSError) as exc:
        raise _repository_error(exc)


@app.post("/api/admin/source-repositories/preview", dependencies=[Depends(admin_guard)])
async def admin_preview_source_repository(request: RepositoryPreviewRequest):
    try:
        return await preview_repository_async(request.url)
    except (ValueError, OSError, TimeoutError) as exc:
        raise _repository_error(exc)


@app.post("/api/admin/source-repositories", dependencies=[Depends(admin_guard)])
def admin_add_source_repository(request: RepositoryChangeRequest):
    try:
        return change_repository(env_file_path(settings.settings_config_dir, "source_host"), request.url, request.revision)
    except (ValueError, OSError) as exc:
        raise _repository_error(exc)


@app.delete("/api/admin/source-repositories", dependencies=[Depends(admin_guard)])
def admin_remove_source_repository(request: RepositoryChangeRequest):
    try:
        return change_repository(env_file_path(settings.settings_config_dir, "source_host"), request.url, request.revision, remove=True)
    except (ValueError, OSError) as exc:
        raise _repository_error(exc)


@app.get("/api/admin/source-extensions", dependencies=[Depends(admin_guard)])
async def admin_source_extensions(refresh: bool = False):
    try:
        inventory_revision = source_orphans.inventory_revision
        result = await source_host.extensions(refresh=bool(refresh))
        _remember_extension_icons(result.get("extensions") or [])
        if inventory_revision == source_orphans.inventory_revision:
            source_orphans.accept_inventory(result.get("extensions") or [])
        result['extensions'] = await run_blocking(source_orphans.annotate_extensions, result.get('extensions') or [])
        return result
    except SourceEngineUnavailable as exc:
        raise HTTPException(503, str(exc), headers={"Retry-After": str(exc.retry_after)})
    except Exception as exc:
        raise HTTPException(503, f"Unable to read source-host extension catalog: {exc}")


@app.post("/api/admin/source-extensions/{pkg_name}/action", dependencies=[Depends(admin_guard)])
async def admin_source_extension_action(pkg_name: str, request: ExtensionActionRequest):
    try:
        async with _database_admin_lock:
            if settings.repair_worker_url and settings.source_engine_mode == 'integrated' and request.action in {'update', 'uninstall'}:
                for item in await maintenance_inventory():
                    if item['pkgName'] == pkg_name and item.get('isInstalled'):
                        await run_blocking(source_package_backup, item)
            extension = await source_host.update_extension(pkg_name, request.action)
            source_orphans.update_inventory_package(pkg_name, extension)
            if extension:
                extension = (await run_blocking(source_orphans.annotate_extensions, [extension]))[0]
            if request.action=='uninstall':
                if source_raws.catalog:await run_blocking(source_raws.catalog.removed,pkg_name)
                if repair_workflow.history:await run_blocking(repair_workflow.history.observe,pkg_name,None)
        # The Suwayomi mutation is the user-facing operation. A full source scan can
        # take tens of seconds with many extensions, so never hold Install/Update/
        # Uninstall open for it. Reconcile concrete source IDs in the background.
        queued = service.schedule_source_reconciliation(delay=0.25)
        if extension and extension.get('isInstalled') and request.action in {'install','update'}:
            source_raws.schedule(extension,manual=False)
        return {"extension": extension, "source_sync_pending": bool(queued)}
    except ValueError as exc:
        raise HTTPException(400, str(exc))
    except ExtensionInstallUnavailable as exc:
        raise HTTPException(502, str(exc))
    except SourceEngineUnavailable as exc:
        raise HTTPException(503, str(exc), headers={"Retry-After": str(exc.retry_after)})
    except Exception as exc:
        raise HTTPException(503, f"Source extension {request.action} failed: {exc}")


@app.post("/api/admin/source-extensions/upload", dependencies=[Depends(admin_guard)])
async def admin_upload_source_apk(request: Request):
    media_type = request.headers.get("content-type", "").split(";", 1)[0].strip().lower()
    if media_type not in {"application/vnd.android.package-archive", "application/octet-stream"}:
        raise HTTPException(415, "Choose an Android source extension APK file")
    try:
        filename = unquote(request.headers.get("x-filename", ""), encoding="utf-8", errors="strict")
    except UnicodeError:
        raise HTTPException(400, "Invalid APK filename") from None
    data = await _source_control_body(request, source_controls.MAX_PACKAGE, timeout=120.0)
    try:
        async with _database_admin_lock:
            result = await source_controls.install_uploaded_apk(source_host, data, filename)
        if result.get('extension'):source_raws.schedule(result['extension'],manual=True)
        queued = service.schedule_source_reconciliation(delay=0.25)
        return JSONResponse({**result, "source_sync_pending": bool(queued)}, headers={"Cache-Control": "no-store"})
    except Exception as exc:
        raise _source_control_error(exc) from None


@app.post("/api/admin/source-extensions/update-all", dependencies=[Depends(admin_guard)])
async def admin_update_all_source_extensions():
    try:
        async with _database_admin_lock:
            if settings.repair_worker_url and settings.source_engine_mode == 'integrated':
                for item in await maintenance_inventory():
                    if item.get('isInstalled'): await run_blocking(source_package_backup, item)
            result = await source_host.update_all_extensions()
        for extension in result.get('extensions',[]):
            if extension.get('isInstalled'):source_raws.schedule(extension,manual=False)
        queued = service.schedule_source_reconciliation(delay=0.25)
        return {**result, "source_sync_pending": bool(queued)}
    except Exception as exc:
        raise HTTPException(503, f"Unable to update source extensions: {exc}")


@app.get("/api/admin/entries", dependencies=[Depends(admin_guard)])
def admin_entries(search: str = "", limit: int = Query(200, ge=1, le=1000)):
    return {"items": service.entry_list(search, limit)}


@app.post("/api/admin/entries/summaries", dependencies=[Depends(admin_guard)])
async def admin_entry_summaries(request: Request):
    """Cheap metadata heartbeat for already-rendered cards.

    Background source hydration can discover a cover/summary after Matrix, Browse or
    Imports already rendered.  Returning compact SQLite summaries lets the browser
    patch those cards without reloading a 500-row Matrix or querying a manga source.
    """
    try:
        payload = await request.json()
    except Exception:
        payload = {}
    raw_ids = payload.get("ids") if isinstance(payload, dict) else []
    if not isinstance(raw_ids, list):
        raise HTTPException(400, "ids must be a JSON array")
    ids: list[int] = []
    seen: set[int] = set()
    for raw in raw_ids:
        try:
            eid = int(raw)
        except (TypeError, ValueError):
            continue
        if eid > 0 and eid not in seen:
            seen.add(eid); ids.append(eid)
        if len(ids) >= 500:
            break
    return await catalogue_response(lambda: {"items": service._entry_summaries(ids)})


@app.get("/api/admin/matrix", dependencies=[Depends(admin_guard)])
async def admin_matrix(
    search: str = "",
    limit: int = Query(100, ge=1, le=500),
    subbed: bool = False,
    include_chapter_stats: bool = True,
    source_id: str = "",
):
    # Subscription state is returned for both views, but normal Matrix must stay
    # database-first. Before v0.2.30 the UI did not wait on a fresh MAIN Suwayomi
    # library walk just to render local catalog rows. Use the last completed
    # snapshot and refresh it independently. The Subbed page may wait briefly for
    # a first snapshot because its row set is defined by that library.
    subscription = service.cached_subbed_payload()
    service.schedule_status_aux_refresh(delay=0.0)
    if subbed and subscription.get("ok") is None and include_chapter_stats:
        try:
            subscription = await asyncio.wait_for(service.subbed_entry_ids(force=True), timeout=8.0)
        except Exception:
            subscription = service.cached_subbed_payload()

    def build_matrix():
        if source_id:
            restricted = service.canonical_entry_ids(subscription.get("entry_ids") or []) if subbed else None
            ids = source_entry_ids(source_id, search=search, limit=limit, restrict_ids=restricted)
            entries = service._entry_summaries(ids)
        elif subbed:
            entries = service.entries_by_ids(subscription.get("entry_ids") or [], search, limit)
        else:
            entries = service.entry_list(search, limit)
        ids = [e["id"] for e in entries]
        entry_stats = service.entry_chapter_stats(ids) if include_chapter_stats else {}
        for entry in entries:
            if not include_chapter_stats:
                entry["chapter_stats_pending"] = True
                continue
            stat = entry_stats.get(int(entry["id"]), {})
            entry["served_chapter_count"] = int(stat.get("served_chapter_count") or 0)
            entry["served_latest_chapter"] = stat.get("served_latest_chapter")
            entry["chapter_records_checked"] = bool(stat.get("chapter_records_checked", False))
        matches = []
        variants = []
        if ids:
            q = ",".join("?" for _ in ids)
            with db.catalog() as c:
                matches = db.rows(c.execute(
                    f"""SELECT sm.id match_id,sm.entry_id,sm.source_id,sm.source_title,sm.confidence,sm.match_type,sm.locked,
                        sm.source_manga_id,sm.thumbnail_url,sm.description,sm.author,sm.artist,sm.real_url,
                        sc.name source_name,sc.lang source_lang,sc.base_url base_url,sc.priority
                        FROM source_matches sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id
                        WHERE sm.active=1 AND sm.entry_id IN ({q})""", ids
                ))
                variants = db.rows(c.execute(
                    f"""SELECT sv.id variant_id,sv.entry_id,sv.source_id,sv.source_title,sv.confidence,sv.match_type,sv.locked,
                        sv.source_manga_id,sv.thumbnail_url,sv.description,sv.author,sv.artist,sv.real_url,
                        sc.name source_name,sc.lang source_lang,sc.base_url base_url,sc.priority
                        FROM source_variants sv LEFT JOIN source_config sc ON sc.source_id=sv.source_id
                        WHERE sv.active=1 AND sv.entry_id IN ({q})""", ids
                ))
            record_stats = service.source_record_chapter_stats(ids) if include_chapter_stats else {}
            for row in [*matches, *variants]:
                if not include_chapter_stats:
                    row["chapter_stats_pending"] = True
                    continue
                stat = record_stats.get((int(row["entry_id"]), str(row["source_id"]), int(row["source_manga_id"])), {})
                row["chapter_count"] = int(stat.get("chapter_count") or 0)
                row["latest_chapter"] = stat.get("latest_chapter")
                row["missing_chapter_count"] = int(stat.get("missing_chapter_count") or 0)
                row["chapters_checked"] = bool(stat.get("chapters_checked", False))
                row["chapter_checked_at"] = stat.get("chapter_checked_at")
                row["chapter_check_error"] = str(stat.get("chapter_check_error") or "")
        return {
            "entries": entries,
            "sources": service.list_sources(),
            "matches": matches,
            "variants": variants,
            # Duplicate scoring is intentionally loaded in a second request. It can
            # inspect many catalog titles and must not hold the whole Matrix response
            # hostage (or make initial app loading look like startup failure).
            "duplicates": {},
            "duplicates_pending": bool(ids),
            **({"chapter_stats_pending": bool(ids)} if not include_chapter_stats else {}),
            "discovery_outcomes": service.source_discovery_outcomes(ids),
            "subscription": subscription,
        }

    # Matrix generation can touch hundreds of rows and run duplicate scoring. Keep
    # that CPU/SQLite work off the asyncio event loop so reader/browse requests stay
    # responsive while the admin page is open.
    payload = await catalogue_response(build_matrix)
    # Matrix/Search is an inspection surface. Merely viewing or filtering it must not
    # launch source-host searches; automatic discovery is queued when live feed records
    # are ingested, and the explicit per-entry discovery action remains available.
    return payload


def _normalize_duplicate_ids(values) -> list[int]:
    parsed: list[int] = []
    seen: set[int] = set()
    for raw in values:
        try:
            entry_id = int(raw)
        except (TypeError, ValueError):
            continue
        if entry_id > 0 and entry_id not in seen:
            seen.add(entry_id); parsed.append(entry_id)
        if len(parsed) >= 500:
            break
    return parsed


@app.post("/api/admin/matrix/chapter-stats", dependencies=[Depends(admin_guard)])
async def admin_matrix_chapter_stats(request: Request):
    try:
        payload = await request.json()
    except (ValueError, TypeError):
        raise HTTPException(400, "Expected an object with an ids array")
    values = payload.get("ids") if isinstance(payload, dict) else None
    if not isinstance(values, list) or len(values) > 500:
        raise HTTPException(400, "ids must be an array of at most 500 entry IDs")
    ids = _normalize_duplicate_ids(values)

    def build():
        return {"entry_stats": service.entry_chapter_stats(ids), "record_stats": [
            {"entry_id": key[0], "source_id": key[1], "source_manga_id": key[2], **value}
            for key, value in service.source_record_chapter_stats(ids).items()
        ]}
    return await catalogue_response(build, lane=chapter_stats_lane)


async def _cached_duplicate_candidates(parsed: list[int]):
    if not parsed:
        return {"duplicates": {}, "pending": False}
    # Filtering a large persisted snapshot is local work, but not necessarily cheap.
    duplicate_map, status = await run_blocking(
        lambda: (service.duplicate_candidates_cached(parsed), service.duplicate_snapshot_summary())
    )
    if duplicate_map is None:
        service.schedule_duplicate_snapshot_refresh(delay=0.0)
        return {"duplicates": {}, "pending": True}
    if status.get("stale"):
        service.schedule_duplicate_snapshot_refresh(delay=0.0)
    return {"duplicates": {str(k): v for k, v in duplicate_map.items()}, "pending": bool(status.get("stale"))}


@app.get("/api/admin/duplicate-candidates", dependencies=[Depends(admin_guard)])
async def admin_duplicate_candidates(ids: str = Query(default="")):
    # Backward-compatible GET. New UI uses POST so 500 ids never become a giant URL.
    return await _cached_duplicate_candidates(_normalize_duplicate_ids(str(ids or "").split(",")))


@app.post("/api/admin/duplicate-candidates", dependencies=[Depends(admin_guard)])
async def admin_duplicate_candidates_post(request: Request):
    try:
        payload = await request.json()
    except Exception:
        payload = {}
    values = payload.get("ids") if isinstance(payload, dict) else []
    if not isinstance(values, list):
        raise HTTPException(400, "ids must be a JSON array")
    return await _cached_duplicate_candidates(_normalize_duplicate_ids(values))


from .repair_tasks import tasks_router
app.include_router(tasks_router(admin_guard, lambda: task_registry))


@app.get("/api/admin/extension-apk", dependencies=[Depends(admin_guard)])
def admin_extension_apk():
    return JSONResponse(apk_status(settings.settings_config_dir, APP_VERSION), headers={"Cache-Control": "no-store"})


@app.get("/api/admin/extension-apk/download", dependencies=[Depends(admin_guard)])
def admin_extension_apk_download(version: str | None = None):
    if version is not None and version != APP_VERSION:
        return JSONResponse({"detail": "The app version changed. Refresh Status before downloading."}, status_code=409, headers={"Cache-Control": "no-store"})
    try:
        content = apk_bytes(settings.settings_config_dir, APP_VERSION)
    except ApkUnavailable as exc:
        return JSONResponse({"detail": str(exc)}, status_code=409, headers={"Cache-Control": "no-store"})
    return Response(content, media_type="application/vnd.android.package-archive", headers={
        "Content-Disposition": f'attachment; filename="AutoSourcarr-v{APP_VERSION}-Tachiyomi-Mihon.apk"',
        "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff",
    })


@app.get("/api/admin/extension-jar", dependencies=[Depends(admin_guard)])
def admin_extension_jar():
    from .extension_download import jar_status
    return JSONResponse(jar_status(settings.settings_config_dir, APP_VERSION), headers={"Cache-Control":"no-store"})


@app.get("/api/admin/extension-jar/download", dependencies=[Depends(admin_guard)])
def admin_extension_jar_download(version: str | None = None):
    from .extension_download import jar_bytes
    if version is not None and version != APP_VERSION: raise HTTPException(409,"Refresh Status for the current version")
    try: content=jar_bytes(settings.settings_config_dir,APP_VERSION)
    except ApkUnavailable as exc: raise HTTPException(409,str(exc))
    return Response(content,media_type="application/java-archive",headers={
        "Content-Disposition":f'attachment; filename="AutoSourcarr-v{APP_VERSION}-Suwayomi.jar"',
        "Cache-Control":"no-store","X-Content-Type-Options":"nosniff"})


@app.get("/api/admin/diagnostics", dependencies=[Depends(admin_guard)])
async def admin_diagnostics():
    """Export local evidence without waiting for or retrying remote dependencies."""
    snapshot = error_registry.snapshot(limit=100, include_resolved=True)
    from .repair_tasks import repair_tasks
    from .source_maintenance import source_maintenance
    tasks = repair_tasks.diagnostic_snapshot(task_registry.snapshot(), source_maintenance.operation)
    support_logs.privacy.register(vars(settings))
    context = {"schema_version": 3, "runtime": runtime_context(), "errors": snapshot,
               "health": _last_dependency_health, "source_engine": source_engine.snapshot(),
               "internal_services": internal_services.snapshot(),
               "task_counts": {key: value for key, value in tasks.items() if key != "items"},
               "tasks": tasks.get("items", [])[:100],
               "tasks_omitted": max(0, len(tasks.get("items", []))-100),
               "limits": {key: getattr(settings, key) for key in (
                   "max_source_concurrency", "source_host_work_concurrency", "background_source_concurrency",
                   "import_resolve_concurrency", "manual_search_timeout", "manual_search_overall_timeout", "source_request_timeout")}}
    def build():
        try:
            context["imports"] = service.import_diagnostic_context(snapshot["items"])
        except Exception as exc:
            context["partial_error"] = {"section": "imports", "error": str(exc) or type(exc).__name__, "exception_chain": exception_context(exc)}
        result = support_logs.privacy.scrub(context)
        result['logs'] = support_logs.snapshot()
        return result
    response = await catalogue_response(build, lane=diagnostic_read_lane)
    response.headers['Cache-Control'] = 'no-store'
    return response


@app.get("/api/admin/errors", dependencies=[Depends(admin_guard)])
async def admin_errors(limit: int = Query(50, ge=1, le=100), include_resolved: bool = True):
    return error_registry.snapshot(limit=limit, include_resolved=include_resolved)


@app.post("/api/admin/errors/clear", dependencies=[Depends(admin_guard)])
def admin_clear_errors(resolved_only: bool = True):
    return {"ok": True, "cleared": error_registry.clear(resolved_only=resolved_only)}


@app.post("/api/admin/entries/{entry_id}/source-records/{source_id}/{source_manga_id}/check-chapters", dependencies=[Depends(admin_guard)])
async def admin_check_source_record_chapters(entry_id: int, source_id: str, source_manga_id: int):
    try:
        return await service.refresh_source_record_chapters(entry_id, source_id, source_manga_id)
    except KeyError:
        raise HTTPException(404, "Attached source record not found")
    except Exception as exc:
        raise HTTPException(502, f"Unable to refresh chapters: {exc}")


@app.get("/api/admin/subbed", dependencies=[Depends(admin_guard)])
async def admin_subbed():
    return await service.subbed_entry_ids()


@app.post("/api/admin/subbed/search-all", dependencies=[Depends(admin_guard)])
async def admin_subbed_search_all():
    subscription = await service.subbed_entry_ids(force=True)
    if not subscription.get("ok"):
        raise HTTPException(503, f"Unable to load Subbed library: {subscription.get('error') or 'main Suwayomi unavailable'}")
    return service.schedule_subbed_search_all([int(x) for x in subscription.get("entry_ids") or []])


@app.post("/api/admin/subbed/repair-unmatched", dependencies=[Depends(admin_guard)])
async def admin_subbed_repair_unmatched():
    subscription = await service.subbed_entry_ids(force=True)
    if not subscription.get("ok"):
        raise HTTPException(503, f"Unable to load Subbed library: {subscription.get('error') or 'main Suwayomi unavailable'}")
    return service.schedule_subbed_search_all([int(x) for x in subscription.get("entry_ids") or []], unmatched_only=True)


@app.get("/api/admin/entries/{entry_id}", dependencies=[Depends(admin_guard)])
async def admin_entry(entry_id: int):
    try:
        from .metadata_fields import overlay
        def display():
            from .identity_review import decorate
            entry=overlay(db,service.get_entry(entry_id))
            return decorate(db,entry)
        return await catalogue_response(display)
    except KeyError:
        raise HTTPException(404, "Entry not found")


@app.delete("/api/admin/entries/{entry_id}", dependencies=[Depends(admin_guard)])
async def admin_delete_entry(entry_id: int, force: bool = False):
    if not force:
        # Deleting a local catalog row must not synchronously walk Main Suwayomi.
        # Use the completed library snapshot; if it is absent/stale, refresh in the
        # background and fail safely instead of making the browser wait until timeout.
        subscription = service.cached_subbed_payload()
        max_age = max(5.0, float(settings.subbed_cache_ttl_seconds))
        cache_age = subscription.get("cache_age_seconds")
        fresh = subscription.get("ok") is True and cache_age is not None and float(cache_age) <= max_age
        if not fresh:
            service.schedule_status_aux_refresh(delay=0.0, force=True)
            raise HTTPException(409, "Main Suwayomi library state is refreshing. Wait a moment and retry, or repeat with force=true to delete anyway.")
        if service.canonical_entry_id(entry_id) in {service.canonical_entry_id(x) for x in subscription.get("entry_ids", [])}:
            raise HTTPException(409, "This entry is in the main Suwayomi library. Repeat with force=true to delete it anyway.")
    try:
        return service.delete_entry(entry_id)
    except KeyError:
        raise HTTPException(404, "Entry not found")


@app.delete("/api/admin/entries/{entry_id}/variants/{source_id}/{source_manga_id}", dependencies=[Depends(admin_guard)])
async def admin_clear_variant(entry_id: int, source_id: str, source_manga_id: int):
    try:
        result = service.clear_source_variant(entry_id, source_id, source_manga_id)
        canonical = service.canonical_entry_id(int(entry_id))
        await cover_cache.clear_keys([f"entry-{int(entry_id)}", f"entry-{canonical}", f"source-{int(source_manga_id)}"])
        return result
    except KeyError:
        raise HTTPException(404, "Additional match not found")


@app.post("/api/admin/entries/{entry_id}/variants/{source_id}/{source_manga_id}/promote", dependencies=[Depends(admin_guard)])
def admin_promote_variant(entry_id: int, source_id: str, source_manga_id: int):
    try:
        return service.promote_variant_to_primary(entry_id, source_id, source_manga_id)
    except KeyError:
        raise HTTPException(404, "Entry or additional record not found")


@app.post("/api/admin/entries/{entry_id}/refresh", dependencies=[Depends(admin_guard)])
async def admin_refresh_entry(entry_id: int, background: bool = Query(False)):
    if background:
        if not service.entry_exists(entry_id):
            raise HTTPException(404, "Entry not found")
        return service.schedule_entry_refresh_job(entry_id, fetch_details=True, fetch_chapters=True)
    return await refresh_entry(entry_id)


@app.post("/api/admin/entries/{entry_id}/refresh-all", dependencies=[Depends(admin_guard)])
async def admin_refresh_entry_all(entry_id: int):
    """Refresh the complete entry: cover cache, metadata, identities and chapters."""
    canonical = service.canonical_entry_id(int(entry_id))
    if not service.entry_exists(canonical):
        raise HTTPException(404, "Entry not found")
    hints = service.entry_cover_hints(canonical)
    keys = {f"entry-{int(entry_id)}", f"entry-{canonical}"}
    for hint in hints:
        manga_id = int((hint or {}).get("source_manga_id") or 0)
        if manga_id:
            keys.add(f"source-{manga_id}")
    removed = await cover_cache.clear_keys(keys)
    queued = service.schedule_entry_refresh_job(canonical, fetch_details=True, fetch_chapters=True)
    service.schedule_entry_identity(canonical, force=True, eager=True)
    queued["removed_cover_files"] = removed
    return queued


@app.post("/api/admin/entries/{entry_id}/refresh-metadata", dependencies=[Depends(admin_guard)])
async def admin_refresh_entry_metadata(entry_id: int, background: bool = Query(False)):
    """Refresh details from every active match without doing a chapter refresh."""
    if not service.entry_exists(entry_id):
        raise HTTPException(404, "Entry not found")
    if background:
        return service.schedule_entry_refresh_job(entry_id, fetch_details=True, fetch_chapters=False)
    entry = await service.refresh_entry(entry_id, fetch_details=True, fetch_chapters=False)
    if service._entry_needs_metadata(entry_id):
        entry = await service.enrich_entry_metadata(entry_id) or entry
    return {"entry": entry, "still_sparse": service._entry_needs_metadata(entry_id)}


@app.get("/api/admin/metadata-providers", dependencies=[Depends(admin_guard)])
def admin_metadata_providers():
    return {"items": service.identity_provider_status()}


@app.post("/api/admin/entries/{entry_id}/identity/refresh", dependencies=[Depends(admin_guard)])
async def admin_refresh_entry_identity(entry_id: int, force: bool = Query(False)):
    if not service.entry_exists(entry_id):
        raise HTTPException(404, "Entry not found")
    result = await service.resolve_entry_identity(entry_id, force=force)
    result["entry"] = service.get_entry(entry_id)
    return result


@app.post("/api/admin/entries/{entry_id}/discover-additional", dependencies=[Depends(admin_guard)])
async def admin_discover_additional(entry_id: int, source_id: str | None = None, background: bool = Query(False)):
    if not service.entry_exists(entry_id):
        raise HTTPException(404, "Entry not found")
    if background:
        return service.schedule_entry_discovery_job(entry_id, source_id)
    return await service.discover_all_source_records(entry_id, source_id, user_requested=True)


@app.get("/api/admin/entries/{entry_id}/history", dependencies=[Depends(admin_guard)])
def admin_history(entry_id: int):
    return service.entry_history(entry_id)


@app.get("/api/admin/entries/{entry_id}/manual-search", dependencies=[Depends(admin_guard)])
async def admin_manual_search(
    entry_id: int, source_id: str, q: str = "", page: int = Query(1, ge=1), all_names: bool = True
):
    if not service.entry_exists(entry_id):
        raise HTTPException(404, "Entry not found")
    return await service.search_source_for_manual(entry_id, source_id, q, page, all_names)


@app.post("/api/admin/entries/{entry_id}/manual-match", dependencies=[Depends(admin_guard)])
async def admin_manual_match(entry_id: int, request: ManualMatchRequest):
    try:
        return await service.manual_match(
            entry_id, request.source_id, request.source_manga_id, request.mode,
            candidate=request.candidate, defer_hydration=bool(request.defer_hydration),
        )
    except KeyError:
        raise HTTPException(404, "Entry not found")


@app.post("/api/admin/entries/{entry_id}/separate-source", dependencies=[Depends(admin_guard)])
async def admin_separate_source(entry_id: int, request: SeparateSourceRequest):
    try:
        return await service.separate_source_record(
            entry_id, request.source_id, request.source_manga_id,
            candidate=request.candidate, defer_hydration=bool(request.defer_hydration),
        )
    except KeyError:
        raise HTTPException(404, "Entry not found")
    except ValueError as exc:
        raise HTTPException(409, str(exc))


@app.delete("/api/admin/entries/{entry_id}/separate-source/{source_id}/{source_manga_id}", dependencies=[Depends(admin_guard)])
def admin_allow_separate_source(entry_id: int, source_id: str, source_manga_id: int):
    try:
        return service.allow_source_record_match(entry_id, source_id, source_manga_id)
    except KeyError:
        raise HTTPException(404, "Entry not found")


@app.get("/api/admin/source-manga/{manga_id}", dependencies=[Depends(admin_guard)])
async def admin_source_manga(manga_id: int):
    try:
        return await service.source_manga_details(manga_id)
    except Exception as exc:
        raise HTTPException(502, f"Unable to load source manga details: {exc}")


@app.delete("/api/admin/entries/{entry_id}/matches/{source_id}", dependencies=[Depends(admin_guard)])
async def admin_clear_match(entry_id: int, source_id: str):
    try:
        before = service.get_entry(entry_id)
        mids = {
            int(row.get("source_manga_id") or 0)
            for row in [*(before.get("matches") or []), *(before.get("source_variants") or [])]
            if str(row.get("source_id")) == str(source_id) and int(row.get("source_manga_id") or 0) > 0
        }
        result = service.clear_match(entry_id, source_id)
        canonical = service.canonical_entry_id(int(entry_id))
        await cover_cache.clear_keys([f"entry-{int(entry_id)}", f"entry-{canonical}", *[f"source-{mid}" for mid in sorted(mids)]])
        return result
    except KeyError:
        raise HTTPException(404, "Entry not found")


@app.post("/api/admin/entries/{entry_id}/preferred-title", dependencies=[Depends(admin_guard)])
def admin_preferred_title(entry_id: int, request: PreferredTitleRequest):
    try:
        return service.set_preferred_title(entry_id, request.title)
    except KeyError:
        raise HTTPException(404, "Entry not found")
    except ValueError as exc:
        raise HTTPException(400, str(exc))


@app.delete("/api/admin/entries/{entry_id}/manual-titles", dependencies=[Depends(admin_guard)])
def admin_clear_manual_titles(entry_id: int):
    try:
        return service.clear_manual_titles(entry_id)
    except KeyError:
        raise HTTPException(404, "Entry not found")


@app.post("/api/admin/entries/{entry_id}/aliases", dependencies=[Depends(admin_guard)])
def admin_add_alias(entry_id: int, request: AliasRequest):
    if not service.entry_exists(entry_id):
        raise HTTPException(404, "Entry not found")
    service.add_title(entry_id, request.title, request.language, "manual")
    return service.get_entry(entry_id)


@app.get("/api/admin/chapters/{chapter_id}", dependencies=[Depends(admin_guard)])
def admin_chapter(chapter_id: int):
    return chapter(chapter_id)


@app.post("/api/admin/candidates/{candidate_id}/quality", dependencies=[Depends(admin_guard)])
async def admin_quality(candidate_id: int, request: QualityRequest):
    try:
        async with _database_admin_lock:
            return await analyze_candidate(candidate_id, request.sample_count)
    except RuntimeError as exc:
        raise HTTPException(409, str(exc))


@app.post("/api/admin/candidates/{candidate_id}/reject", dependencies=[Depends(admin_guard)])
def admin_reject_candidate(candidate_id: int):
    try:
        return service.reject_chapter_candidate(candidate_id)
    except KeyError:
        raise HTTPException(404, "Chapter candidate not found")


@app.post("/api/admin/candidates/{candidate_id}/restore", dependencies=[Depends(admin_guard)])
def admin_restore_candidate(candidate_id: int):
    try:
        return service.restore_chapter_candidate(candidate_id)
    except KeyError:
        raise HTTPException(404, "Chapter candidate not found")


@app.get("/api/admin/storage/database", dependencies=[Depends(admin_guard)])
def admin_database_storage():
    return db.database_relocation_status(settings.settings_config_dir)


@app.post("/api/admin/storage/database/move", dependencies=[Depends(admin_guard)])
async def admin_move_database_storage(request: DatabaseMoveRequest):
    target = Path(request.path.strip())
    env_path = env_file_path(settings.settings_config_dir, "autosourcarr")
    if not env_path.is_file():
        raise HTTPException(409, f"Settings file is not mounted: {env_path}")

    write_database_dir = partial(upsert_env_value, env_path, "DATABASE_DIR")

    def move() -> dict[str, Any]:
        # One owned thread completes stage/env/cut-over together even if the HTTP
        # caller disconnects. Never cancel between persisting paths and activation.
        old_dir = str(Path(settings.database_base_dir).resolve())
        staged = db.stage_database_relocation(target, settings.settings_config_dir)
        if staged.get("already_active"):
            return {**staged, "message": "Database is already using this location."}
        try:
            write_database_dir(str(Path(staged["target_dir"])))
            activated = db.activate_database_relocation(settings.settings_config_dir)
        except BaseException:
            write_database_dir(old_dir)
            raise
        return {
            **staged, **activated,
            "message": "Database copied, verified and switched live. The old SQLite files remain a safety copy until the next redeploy verifies the new databases again.",
        }

    try:
        async with _database_admin_lock:
            return await run_sync(move)
    except ValueError as exc:
        raise HTTPException(400, str(exc))
    except (OSError, RuntimeError) as exc:
        raise HTTPException(409, str(exc))


from .setup_api import router as setup_router
app.include_router(setup_router(settings, admin_guard, APP_VERSION))


@app.get("/api/admin/settings/env", dependencies=[Depends(admin_guard)])
def admin_env_settings():
    return env_settings_payload(settings.settings_config_dir)


@app.put("/api/admin/settings/env/{file_id}", dependencies=[Depends(admin_guard)])
def admin_update_env_settings(file_id: str, request: EnvFileUpdate):
    try:
        path = env_file_path(settings.settings_config_dir, file_id)
    except KeyError:
        raise HTTPException(404, "Unknown settings file")
    if not path.is_file():
        raise HTTPException(409, f"Settings file is not mounted: {path}")
    try:
        result = update_env(path, request.values, revision=request.revision)
    except PermissionError:
        raise HTTPException(409, f"Settings file is read-only: {path}")
    except OSError as exc:
        raise HTTPException(409, f"Unable to write settings file {path}: {exc}")
    except ValueError as exc:
        raise HTTPException(400, str(exc))
    return {**result, "file_id": file_id, "message": "Saved. Restart/redeploy the TrueNAS Custom App to apply all env_file changes."}


@app.get("/api/admin/config", dependencies=[Depends(admin_guard)])
def admin_config():
    return {
        "source_suwayomi_url": settings.effective_source_url,
        "autosourcarr_public_url": settings.autosourcarr_public_url,
        "main_suwayomi_url": settings.main_suwayomi_url,
        "auto_match_threshold": settings.auto_match_threshold,
        "federated_source_timeout": settings.federated_source_timeout,
        "federated_overall_timeout": settings.federated_overall_timeout,
        "source_languages": sorted(settings.allowed_languages),
        "metadata_identity_enabled": bool(settings.metadata_identity_enabled),
        "metadata_identity_providers": service.identity_provider_status(),
        "metadata_identity_include_adult": bool(settings.metadata_identity_include_adult),
        "admin_token_required": bool(settings.admin_token),
    }


@asynccontextmanager
async def _instance_snapshot_pause():
    """Stop instance writers before a coordinated DB/config/source snapshot."""
    from .internal_services import internal_services
    from .lifecycle import cleanup_steps
    async with _database_admin_lock:
        async with _exclusive_catalog_reset():
            reason=service.begin_automatic_duplicate_pause('instance-backup')
            ready=False
            try:
                # Finish or cancel visible manual work before freezing its state.
                # Do not interrupt active repairs/account/build operations merely
                # to make a backup. Automatic owners are paused and drained below.
                manual=[r for r in task_registry.snapshot()['items'] if r['state']=='running' and r['origin']=='manual'
                        and not str(r.get('name','')).lower().startswith(('create instance backup','admin action'))]
                if manual:
                    raise HTTPException(409,'Finish active manual tasks before creating an instance backup')
                if internal_services.cleanup_failed:
                    raise HTTPException(409,'Resolve uncertain internal-tool cleanup before creating an instance backup')
                builder=internal_services.tasks.get('builder')
                if builder is not None and not builder.done():
                    raise HTTPException(409,'Finish the reader build before creating an instance backup')
                repair_process=internal_services.processes.get('repair')
                if repair_process is not None and repair_process.returncode is None:
                    from .source_maintenance import worker as repair_worker
                    from .instance_backup import repair_idle_for_snapshot
                    try:
                        repair_status=await asyncio.wait_for(repair_worker('GET','/status'),timeout=4)
                    except (HTTPException,TimeoutError) as exc:
                        raise HTTPException(409,'Repair/account ownership is unavailable; no tools were stopped for backup') from exc
                    if not repair_idle_for_snapshot(repair_status):
                        raise HTTPException(409,'Finish or recover active repair/account work before backup; no tools were stopped')
                await cleanup_steps([
                    ('library monitoring',library_manager.stop), ('health',source_health.stop),
                    ('repair workflow',repair_workflow.stop), ('raw sources',source_raws.stop),
                    ('maintenance',source_maintenance.stop), ('background tasks',service.cancel_background_tasks),
                    ('internal services',internal_services.stop), ('orphans',source_orphans.stop),
                    ('browser',source_browser.stop), ('engine',source_engine.stop), ('task history',task_journal.stop),
                ])
                ready=True
                yield
            finally:
                if ready:
                    # The snapshot operation owns its blocking work through
                    # cancellation. Restart owners only after it actually ends.
                    service.resume_background_tasks()
                    await task_journal.start()
                    internal_services.start()
                    await source_raws.start(_database_admin_lock)
                    repair_workflow.start(_database_admin_lock)
                    source_engine.start();source_maintenance.start()
                    source_attention.start(service,source_host)
                    source_health.start(_database_admin_lock);library_manager.start(_database_admin_lock)
                    service.schedule_continuous_catalog_discovery()
                service.end_automatic_duplicate_pause(reason,schedule=ready)


def _restart_after_restore():
    # The normal container already has its restart policy. SIGTERM uses the full
    # lifespan drain; the launcher applies the verified journal before opening DBs.
    import os,signal
    asyncio.get_running_loop().call_later(1.5,lambda:os.kill(os.getpid(),signal.SIGTERM))


from .reimagined_api import router as reimagined_router
app.include_router(reimagined_router(db,service,library_manager,settings,admin_guard,
                                    _database_admin_lock,_instance_snapshot_pause,_restart_after_restore))
