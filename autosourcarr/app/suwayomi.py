import asyncio
import re
import time
from contextlib import asynccontextmanager
from contextvars import ContextVar
from typing import Any
from urllib.parse import urlparse

import httpx

from .config import settings
from .image_safety import MAX_IMAGE_BYTES
from .diagnostics import scrub, exception_context


_BACKGROUND_SOURCE_WORK: ContextVar[bool] = ContextVar("autosourcarr_background_source_work", default=False)


@asynccontextmanager
async def background_source_work():
    """Mark private-Suwayomi work as low priority for this coroutine tree.

    Context variables propagate into asyncio child tasks, so a background federated
    search that internally starts several source requests still shares one global
    background budget instead of consuming every private-Suwayomi worker.
    """
    token = _BACKGROUND_SOURCE_WORK.set(True)
    try:
        yield
    finally:
        _BACKGROUND_SOURCE_WORK.reset(token)


@asynccontextmanager
async def foreground_source_work():
    """Restore foreground admission for an explicit action spawned by other work."""
    token = _BACKGROUND_SOURCE_WORK.set(False)
    try:
        yield
    finally:
        _BACKGROUND_SOURCE_WORK.reset(token)


class SourceWorkDeferred(RuntimeError):
    """No upstream work started: the private source-host lane was occupied."""
    local_source_deferral = True


class HumanAssistanceRequired(SourceWorkDeferred):
    """A known website challenge is paused; other websites remain usable."""
    human_assistance_required = True

    def __init__(self, record, retry_after):
        self.attention_id = record['id']
        self.retry_after = retry_after
        self.source_context = {'source_id': record.get('source_id', ''), 'source_name': record.get('name', 'Source'), 'source_chapter_url': record.get('base_url', '')}
        super().__init__(f"{record.get('name') or 'Source'}: {record['reason']}. Open Needs attention → WebView. Automatic retry is limited to once per hour.")


class SourceEngineUnavailable(SourceWorkDeferred):
    """The shared local runtime is unavailable; no website request was attempted."""
    def __init__(self, message: str, *, retry_after: int = 5):
        super().__init__(message)
        self.retry_after = retry_after


class BatchDeadlineExceeded(TimeoutError):
    """A local work budget expired; this alone does not establish source failure."""
    local_source_deferral = True


_SOURCE_WORK_BUDGET: ContextVar[tuple[float, float, asyncio.Timeout] | None] = ContextVar("source_work_budget", default=None)


@asynccontextmanager
async def source_work_budget(seconds: float, *, admission_timeout: float = 3.0):
    seconds = max(0.01, float(seconds))
    async with asyncio.timeout(seconds) as clock:
        token = _SOURCE_WORK_BUDGET.set((seconds, max(0.01, float(admission_timeout)), clock))
        try:
            yield
        finally:
            _SOURCE_WORK_BUDGET.reset(token)


MANGA_FIELDS = """
id sourceId url title thumbnailUrl initialized artist author description genre status realUrl inLibrary
"""
CHAPTER_FIELDS = """
id url name uploadDate chapterNumber scanlator mangaId sourceOrder realUrl pageCount
"""
SOURCE_FIELDS = """
id name lang iconUrl supportsLatest isConfigurable isNsfw displayName homeUrl baseUrl
"""


EXTENSION_FIELDS = """
pkgName name lang versionName versionCodeLong isInstalled hasUpdate isObsolete iconUrl contentWarning storeIndexUrl apkUrl apkName
source { nodes { id name lang isConfigurable } }
"""
EXTENSION_STORE_FIELDS = """
name indexUrl badgeLabel isLegacy extensionListUrl
"""


class SuwayomiError(RuntimeError):
    pass


class ExtensionInstallUnavailable(SuwayomiError):
    """Repository package recovery failed; the source engine itself is reachable."""


class SuwayomiClient:
    def __init__(self, base_url: str | None = None, *, work_limit: int = 0, managed_engine: bool = False, credentials=None) -> None:
        self.managed_engine = managed_engine
        self.attention = None
        self.base_url = (base_url or settings.effective_source_url).rstrip("/")
        import base64
        self._auth_header = 'Basic '+base64.b64encode((':'.join(credentials)).encode()).decode() if credentials and credentials[0] else ''
        self.graphql_url = f"{self.base_url}/api/graphql"
        self.timeout = settings.source_request_timeout
        self.work_limit = max(0, int(work_limit or 0))
        self._work_loop = None
        self._work_sem = None
        self._background_sem = None
        limits = httpx.Limits(
            max_connections=max(4, int(settings.http_max_connections)),
            max_keepalive_connections=max(2, int(settings.http_max_keepalive_connections)),
            keepalive_expiry=max(5.0, float(settings.http_keepalive_expiry_seconds)),
        )
        transport = httpx.AsyncHTTPTransport(limits=limits, retries=max(0, int(settings.http_transport_retries)))
        # Reuse one pool for the lifetime of the service. Creating an AsyncClient per
        # GraphQL/image call disabled keep-alive and was one of the largest avoidable
        # latency/connection-pressure costs in earlier AutoSourcarr builds.
        self.client = httpx.AsyncClient(
            timeout=self.timeout,
            follow_redirects=True,
            limits=limits,
            transport=transport,
            event_hooks={"request": [self._check_engine_request]},
        )

    async def _check_engine_request(self, request: httpx.Request) -> None:
        base = httpx.URL(self.base_url)
        if self._auth_header and (request.url.scheme, request.url.netloc) == (base.scheme, base.netloc):
            request.headers['Authorization'] = self._auth_header
        if not self.managed_engine or settings.source_engine_mode != "integrated":
            return
        # External image URLs must still work using retained page maps.
        if request.url.netloc != httpx.URL(self.base_url).netloc:
            return
        from .source_browser import source_browser
        if source_browser.busy:
            raise SourceEngineUnavailable("Source browser maintenance is running; retry shortly", retry_after=5)
        from .source_engine import source_engine
        if source_engine.state in {"setup-required", "starting", "restarting", "unresponsive"}:
            message = source_engine.error or "Integrated source engine is starting; retry shortly."
            source_engine.record_unavailable(request.url.path)
            raise SourceEngineUnavailable(message, retry_after=30 if source_engine.state == "setup-required" else 5)

    async def close(self) -> None:
        await self.client.aclose()

    async def graphql(self, query: str, variables: dict[str, Any] | None = None) -> dict[str, Any]:
        response = await self.client.post(self.graphql_url, json={"query": query, "variables": variables or {}})
        response.raise_for_status()
        try:body = response.json()
        except (ValueError,UnicodeError) as exc:raise SuwayomiError('Invalid GraphQL JSON response') from exc
        if not isinstance(body, dict):
            raise SuwayomiError('Invalid GraphQL response: expected an object')
        if 'errors' in body:
            errors=body['errors']
            if not isinstance(errors,list) or any(not isinstance(e,dict) or not isinstance(e.get('message'),str) for e in errors):
                raise SuwayomiError('Invalid GraphQL error envelope')
            if errors:
                raise SuwayomiError('; '.join(str(scrub(e['message']))[:500] for e in errors[:5]))
        if not isinstance(body.get('data'),dict):
            raise SuwayomiError('Incomplete GraphQL response: no data object')
        return body['data']

    def _work_semaphores(self):
        if self.work_limit <= 0:
            return None, None
        loop = asyncio.get_running_loop()
        if self._work_loop is not loop or self._work_sem is None or self._background_sem is None:
            self._work_loop = loop
            self._work_sem = asyncio.Semaphore(self.work_limit)
            # Background Tasks get only their reserved slice. Foreground requests use
            # only the total semaphore, so they can always consume the remaining slots.
            bg_limit = max(1, min(int(settings.background_source_concurrency), max(1, self.work_limit - 1)))
            self._background_sem = asyncio.Semaphore(bg_limit)
        return self._work_sem, self._background_sem

    async def _run_source_work(self, factory, operation=None, context=None):
        from .runtime_observability import metrics
        attention = self.attention if operation else None
        budget = _SOURCE_WORK_BUDGET.get()
        acquired = []
        started = time.monotonic()

        async def admit():
            nonlocal context
            if attention:
                context = context or await attention.context(self.base_url, operation)
                await attention.check(context)
            # Shared runtime/verification checks are local admission, not website work.
            await self._check_engine_request(httpx.Request("POST", self.graphql_url))
            total, background = self._work_semaphores()
            semaphores = ([background] if _BACKGROUND_SOURCE_WORK.get() and background is not None else []) + ([total] if total is not None else [])
            for semaphore in semaphores:
                await semaphore.acquire()
                acquired.append(semaphore)
            if attention:
                await attention.check(context)

        async def execute():
            try:
                return await factory()
            except Exception as exc:
                if self.managed_engine and isinstance(exc, httpx.RequestError):
                    from .source_browser import source_browser
                    if source_browser.busy:
                        raise SourceEngineUnavailable('Source browser maintenance interrupted this request; retry shortly', retry_after=5) from exc
                if attention:
                    await attention.failed(context, operation, exc)
                raise

        try:
            if budget:
                budget[2].reschedule(None)
                try:
                    with metrics.phase('admission'):
                        await asyncio.wait_for(admit(), timeout=budget[1])
                except TimeoutError as exc:
                    if getattr(exc, "local_source_deferral", False):
                        raise
                    deferred = SourceWorkDeferred("Source capacity or verification check busy; request deferred before upstream execution")
                    deferred.diagnostic_context = {"phase": "source_admission", "timeout_seconds": budget[1],
                                                   "elapsed_ms": round((time.monotonic()-started)*1000), "upstream_started": False}
                    raise deferred from exc
                budget[2].reschedule(asyncio.get_running_loop().time() + budget[0])
            else:
                with metrics.phase('admission'):await admit()
            with metrics.phase('source'):return await execute()
        finally:
            for semaphore in reversed(acquired):
                semaphore.release()

    async def health(self) -> dict[str, Any]:
        probes = []
        async def probe(operation, method, path, timeout, call):
            started = time.monotonic()
            row = {"operation": operation, "method": method, "path": path, "timeout_seconds": timeout}
            try:
                await asyncio.wait_for(call(), timeout=timeout)
                row["ok"] = True
                return True
            except Exception as exc:
                row.update(ok=False, error_type=type(exc).__name__,
                           error=str(exc) or (f"No response within {timeout:g}s" if isinstance(exc, TimeoutError) else type(exc).__name__),
                           exception_chain=exception_context(exc))
                response = getattr(exc, "response", None)
                if response is not None:
                    row["http_status"] = response.status_code
                if getattr(exc, "local_source_deferral", False):
                    row["deferred"] = True
                return False
            finally:
                row["elapsed_ms"] = round((time.monotonic()-started)*1000)
                probes.append(scrub(row))

        async def graphql_probe():
            data = await self.graphql("query AutoSourcarrHealth { __typename }")
            if not data.get("__typename"):
                raise ValueError("Health query returned no GraphQL type")

        if await probe("graphql_health", "POST", "/api/graphql", 2.25, graphql_probe):
            return {"ok": True, "url": self.base_url, "probes": probes}
        if probes[-1].get("deferred"):
            return {"ok": False, "deferred": True, "url": self.base_url, "error": probes[-1]["error"], "probes": probes}

        async def api_probe():
            response = await self.client.get(self.base_url + "/api/v1/settings/about", timeout=2.0)
            response.raise_for_status()
            if not isinstance(response.json(), dict):
                raise ValueError("About endpoint returned an unexpected response")

        # An actual API endpoint still works when the optional source WebUI is
        # disabled. Do not treat an authentication/redirect page as healthy.
        available = await probe("api_liveness", "GET", "/api/v1/settings/about", 2.0, api_probe)
        if available:
            return {"ok": True, "degraded": True, "url": self.base_url,
                    "warning": "Source API is reachable, but its GraphQL health check failed or was slow.", "probes": probes}
        return {"ok": False, "url": self.base_url,
                "error": "Source API health checks failed; the engine may be busy or unavailable.", "probes": probes}

    async def sources(self) -> list[dict[str, Any]]:
        # Current Suwayomi exposes a paginated SourceConnection. A second shape is
        # retained as a compatibility fallback for older builds.
        queries = [
            f"query AutoSourcarrSources {{ sources {{ nodes {{ {SOURCE_FIELDS} }} }} }}",
            f"query AutoSourcarrSources {{ sources {{ {SOURCE_FIELDS} }} }}",
        ]
        last_error: Exception | None = None
        for query in queries:
            try:
                data = await self.graphql(query)
                raw = self._connection_nodes(data.get('sources'))
                if any(not isinstance(x.get('id'),(str,int)) or isinstance(x.get('id'),bool) or not str(x['id']) for x in raw):
                    raise SuwayomiError('Invalid source identity in GraphQL response')
                return raw
            except (SourceWorkDeferred, httpx.PoolTimeout):
                raise
            except Exception as exc:
                last_error = exc
        raise SuwayomiError(f"Unable to query sources: {last_error}")

    @staticmethod
    def _connection_nodes(value: Any) -> list[dict[str, Any]]:
        if isinstance(value,list): raw=value
        elif isinstance(value,dict) and 'nodes' in value: raw=value['nodes']
        elif isinstance(value,dict) and isinstance(value.get('edges'),list):
            edges=value['edges']
            if any(not isinstance(x,dict) or not isinstance(x.get('node'),dict) for x in edges):
                raise SuwayomiError('Invalid GraphQL connection edges')
            raw=[x['node'] for x in edges]
        else: raise SuwayomiError('Incomplete GraphQL connection')
        if not isinstance(raw,list) or any(not isinstance(x,dict) for x in raw):
            raise SuwayomiError('Invalid GraphQL connection nodes')
        return [dict(x) for x in raw]

    async def extensions(self, *, refresh: bool = False) -> dict[str, list[dict[str, Any]]]:
        """Return the private source-host extension catalog.

        ``refresh=True`` asks Suwayomi to re-fetch configured extension stores first.
        The normal read path uses the local Suwayomi DB so opening AutoSourcarr's
        Manage Sources tab does not hit every remote repository on every view.
        """
        if refresh:
            gql = f"""
            mutation AutoSourcarrFetchExtensions($input: FetchExtensionsInput!) {{
              fetchExtensions(input: $input) {{
                extensions {{ {EXTENSION_FIELDS} }}
                extensionStores {{ {EXTENSION_STORE_FIELDS} }}
              }}
            }}
            """
            data = await self.graphql(gql, {"input": {}})
            payload = data.get("fetchExtensions") or {}
            if not isinstance(payload.get("extensions"), list):
                raise SuwayomiError("Source engine returned an incomplete extension catalog refresh")
            return {
                "extensions": self._connection_nodes(payload.get("extensions") or []),
                "stores": self._connection_nodes(payload.get("extensionStores") or []),
            }

        # The extension list itself has a cheap query backed by Suwayomi's local DB.
        # Extension stores are returned by fetchExtensions(), not by the ExtensionQuery
        # root on current Suwayomi. Keep normal Manage Sources rendering local-only;
        # the explicit Refresh catalog action obtains store metadata and refreshes the
        # remote repositories.
        gql = f"""
        query AutoSourcarrExtensions {{
          extensions {{ nodes {{ {EXTENSION_FIELDS} }} }}
        }}
        """
        data = await self.graphql(gql)
        return {
            "extensions": self._connection_nodes(data.get("extensions")),
            "stores": [],
        }

    async def update_extension(self, pkg_name: str, action: str) -> dict[str, Any] | None:
        action = str(action or "").strip().lower()
        if action not in {"install", "update", "uninstall"}:
            raise ValueError(f"Unsupported extension action: {action}")
        gql = f"""
        mutation AutoSourcarrUpdateExtension($input: UpdateExtensionInput!) {{
          updateExtension(input: $input) {{ extension {{ {EXTENSION_FIELDS} }} }}
        }}
        """
        variables = {"input": {"id": str(pkg_name), "patch": {action: True}}}
        async def perform():
            data = await self.graphql(gql, variables)
            payload = data.get("updateExtension") or {}
            item = payload.get("extension")
            if action == "install" and (not isinstance(item, dict)
                    or item.get("pkgName") != str(pkg_name) or item.get("isInstalled") is not True):
                raise SuwayomiError(
                    f"Source engine did not confirm installation of {pkg_name}. "
                    "Refresh the installed extension list before trying again.")
            return dict(item) if isinstance(item, dict) else None

        try:
            return await perform()
        except SuwayomiError as exc:
            if action != "install" or not self._is_extension_download_missing(exc):
                raise

        # v2.3.2243 installs the package URL saved in its catalog. A repository
        # can retire that version while the local row still advertises it. Only
        # recover a confirmed download 404, never replay an uncertain mutation,
        # update/uninstall, a rate limit, or a website verification failure.
        try:
            inventory = await asyncio.wait_for(self.extensions(refresh=True), timeout=15.0)
        except SourceWorkDeferred:
            raise
        except Exception as exc:
            raise ExtensionInstallUnavailable(
                f"{pkg_name}: the package download returned HTTP 404 and AutoSourcarr "
                "could not refresh the extension catalog. Use Sources → Manage → "
                f"Refresh catalog before trying again. No install retry was sent. {exc}"
            ) from exc
        item = next((row for row in inventory["extensions"] if row.get("pkgName") == str(pkg_name)), None)
        if item and item.get("isInstalled") is True:
            return item
        if not item or item.get("isObsolete"):
            raise ExtensionInstallUnavailable(
                f"{pkg_name}: the package download returned HTTP 404 and the extension is "
                "not available in the refreshed catalog. Check the configured repository "
                "or try Refresh catalog later; it may be temporarily unavailable or removed. "
                "No install retry was sent.")
        try:
            return await perform()
        except SuwayomiError as exc:
            if not self._is_extension_download_missing(exc):
                raise
            raise ExtensionInstallUnavailable(
                f"{pkg_name}: the repository still returns HTTP 404 for its package "
                "after refreshing the catalog and retrying once. No further automatic retries. "
                "Try Refresh catalog later or report the missing download to the repository maintainer."
            ) from exc

    async def install_extension_file(self, filename: str, data: bytes, *, apk: bool = True) -> None:
        """Pinned Suwayomi v2.3.2243 REST installer converts APKs and reloads sources."""
        media = 'application/vnd.android.package-archive' if apk else 'application/java-archive'
        response = await self.client.post(self.base_url + '/api/v1/extension/install',
            files={'file': (filename, data, media)}, timeout=httpx.Timeout(90, connect=5),
            follow_redirects=False)
        # A login redirect or a 200 HTML login page is not a successful installation.
        response.raise_for_status()
        if 'text/html' in response.headers.get('content-type', '').lower():
            raise SuwayomiError('Source engine returned a login page instead of installing the extension')

    @staticmethod
    def _is_extension_download_missing(exc: SuwayomiError) -> bool:
        # Match the actual mutation error, not a transport-level /api/graphql
        # 404 or an incidental status code buried in a Java stack trace.
        return bool(re.match(
            r"^Exception while fetching data \(/updateExtension\)\s*:\s*HTTP error 404\b",
            str(exc), re.IGNORECASE))

    def runtime_asset_url(self, value: str) -> str:
        """Retain old cached engine URLs when consolidating containers, not websites."""
        raw = str(value or "").strip()
        previous = settings.source_suwayomi_url.rstrip("/")
        if (settings.source_engine_mode == "integrated" and self.base_url == settings.effective_source_url
                and raw.startswith(previous + "/api/")):
            return self.base_url + raw[len(previous):]
        return raw

    async def fetch_asset(self, url: str) -> tuple[bytes, str]:
        """Fetch an icon/asset URL that originated from this Suwayomi host.

        Suwayomi often reports relative icon URLs that are unreachable from a browser
        behind AutoSourcarr/Authelia. Fetching them server-side keeps private hostnames
        out of the browser and also works for repository-hosted HTTPS extension icons.
        """
        raw = self.runtime_asset_url(url)
        if not raw:
            raise SuwayomiError("Asset URL is empty")
        if raw.startswith("/"):
            target = self.base_url + raw
        elif raw.startswith("http://") or raw.startswith("https://"):
            target = raw
            base = urlparse(self.base_url)
            parsed = urlparse(target)
            if (parsed.scheme, parsed.hostname, parsed.port) != (base.scheme, base.hostname, base.port):
                raise SuwayomiError("Refusing to proxy an external asset URL")
        else:
            target = self.base_url + "/" + raw.lstrip("/")
        body, content_type = await self._fetch_limited_binary(
            target, timeout=min(max(float(self.timeout), 5.0), 20.0), max_bytes=4 * 1024 * 1024,
        )
        content_type = str(content_type).split(";", 1)[0].strip()
        if content_type and not (content_type.startswith("image/") or content_type == "application/octet-stream"):
            raise SuwayomiError(f"Unexpected asset content type: {content_type}")
        return body, content_type or "application/octet-stream"

    async def update_all_extensions(self) -> dict[str, Any]:
        inventory = await self.extensions(refresh=True)
        ids = [
            str(x.get("pkgName") or "")
            for x in inventory["extensions"]
            if x.get("isInstalled") and x.get("hasUpdate") and not x.get("isObsolete") and x.get("pkgName")
        ]
        if not ids:
            return {"updated": 0, "extensions": inventory["extensions"], "stores": inventory["stores"]}
        gql = f"""
        mutation AutoSourcarrUpdateExtensions($input: UpdateExtensionsInput!) {{
          updateExtensions(input: $input) {{ extensions {{ {EXTENSION_FIELDS} }} }}
        }}
        """
        data = await self.graphql(gql, {"input": {"ids": ids, "patch": {"update": True}}})
        payload = data.get("updateExtensions") or {}
        changed = self._connection_nodes(payload.get("extensions") or [])
        return {"updated": len(ids), "extensions": changed, "stores": inventory["stores"]}

    @staticmethod
    def _is_search_not_found(error: Exception) -> bool:
        """Treat source-level HTTP 404 during SEARCH as an empty result page.

        A number of Tachiyomi extensions (including Drake Scans) surface a website/API
        404 when a query has no hits instead of returning an empty manga list. Suwayomi
        wraps that as a GraphQL error from ``fetchSourceManga``. That is a query miss,
        not evidence that the source itself is offline, so it must not trip the source
        circuit breaker or prevent AutoSourcarr from trying the next alias/fallback.
        """
        message = str(error or "").casefold()
        return "http error 404" in message or "http 404" in message

    async def fetch_source_manga(self, source_id: str, mode: str, page: int = 1, query_text: str = "") -> dict[str, Any]:
        gql = f"""
        mutation AutoSourcarrBrowse($input: FetchSourceMangaInput!) {{
          fetchSourceManga(input: $input) {{
            hasNextPage
            mangas {{ {MANGA_FIELDS} }}
          }}
        }}
        """
        input_data: dict[str, Any] = {
            "source": str(source_id),
            "type": mode.upper(),
            "page": int(page),
            "query": query_text or "",
        }
        try:
            data = await self._run_source_work(lambda: self.graphql(gql, {"input": input_data}), {"kind": "browse", "id": str(source_id), "source_id": str(source_id), "query": gql, "variables": {"input": input_data}})
        except SourceWorkDeferred:
            raise
        except Exception as exc:
            # Some source extensions use HTTP 404 as their normal "zero search hits"
            # response. Convert only SEARCH-mode 404s into an empty page; LATEST and
            # POPULAR 404s remain real source errors and continue through diagnostics.
            if mode.upper() == "SEARCH" and self._is_search_not_found(exc):
                return {
                    "hasNextPage": False,
                    "mangas": [],
                    "searchMiss": True,
                    "searchWarning": "Source returned HTTP 404 for this search query; treated as no results.",
                }
            raise
        payload=data.get('fetchSourceManga')
        if not isinstance(payload,dict) or type(payload.get('hasNextPage')) is not bool or not isinstance(payload.get('mangas'),list):
            raise SuwayomiError('Incomplete source feed response')
        if any(not isinstance(row,dict) or type(row.get('id')) is not int or row['id']<=0 or str(row.get('sourceId'))!=str(source_id) for row in payload['mangas']):
            raise SuwayomiError('Invalid source feed identity')
        if self.attention:self.attention.learn(payload)
        return payload

    async def fetch_manga_and_chapters(self, manga_id: int, fetch_manga: bool = True, fetch_chapters: bool = True) -> dict[str, Any]:
        gql = f"""
        mutation AutoSourcarrManga($input: FetchMangaAndChaptersInput!) {{
          fetchMangaAndChapters(input: $input) {{
            manga {{ {MANGA_FIELDS} }}
            chapters {{ {CHAPTER_FIELDS} }}
          }}
        }}
        """
        variables = {"input": {"id": int(manga_id), "fetchManga": bool(fetch_manga), "fetchChapters": bool(fetch_chapters)}}
        data = await self._run_source_work(lambda: self.graphql(gql, variables), {"kind": "manga", "id": int(manga_id), "query": gql, "variables": variables})
        payload=data.get('fetchMangaAndChapters')
        if not isinstance(payload,dict) or not isinstance(payload.get('manga'),dict) or type(payload['manga'].get('id')) is not int or payload['manga']['id']!=int(manga_id):
            raise SuwayomiError('Incomplete manga identity response')
        if fetch_chapters and (not isinstance(payload.get('chapters'),list) or any(not isinstance(row,dict) or type(row.get('id')) is not int or row['id']<=0 or row.get('mangaId')!=int(manga_id) for row in payload['chapters'])):
            raise SuwayomiError('Incomplete chapter response')
        if self.attention:self.attention.learn(payload)
        return payload

    async def fetch_chapter_pages(self, chapter_id: int) -> list[str]:
        gql = """
        mutation AutoSourcarrPages($input: FetchChapterPagesInput!) {
          fetchChapterPages(input: $input) { pages }
        }
        """
        data = await self._run_source_work(lambda: self.graphql(gql, {"input": {"chapterId": int(chapter_id)}}), {"kind": "pages", "id": int(chapter_id), "query": gql, "variables": {"input": {"chapterId": int(chapter_id)}}})
        payload = data.get('fetchChapterPages')
        if not isinstance(payload,dict) or not isinstance(payload.get('pages'),list) or any(not isinstance(p,str) or not p for p in payload['pages']):
            raise SuwayomiError('Incomplete page response')
        return payload['pages']


    async def retry_verification(self, operation, context):
        data = await self._run_source_work(lambda: self.graphql(operation['query'], operation['variables']), operation, context)
        kind = operation['kind']
        if kind == 'pages':
            pages = (data.get('fetchChapterPages') or {}).get('pages')
            valid = isinstance(pages, list) and bool(pages) and all(isinstance(url, str) and url for url in pages)
        elif kind == 'manga':
            manga = (data.get('fetchMangaAndChapters') or {}).get('manga') or {}
            valid = manga.get('id') == operation['id'] and (not context.get('source_id') or str(manga.get('sourceId')) == context['source_id'])
        else:
            payload = data.get('fetchSourceManga')
            valid = isinstance(payload, dict) and isinstance(payload.get('mangas'), list)
        if not valid:
            raise SuwayomiError('Verification retry returned an incomplete response; assistance remains open')
        return data


    async def library_mangas(self, first: int = 500) -> list[dict[str, Any]]:
        """Return manga currently in this Suwayomi instance's library.

        Chapter totals and downloaded-chapter counts deliberately come from the
        MAIN Suwayomi instance. AutoSourcarr's private source host is only a
        discovery/runtime worker and must never be treated as download state.

        The first query uses the current Suwayomi Manga fields `downloadCount`
        and `chapters { totalCount }`. A basic-field fallback keeps the Subbed
        view usable against an older/temporarily incompatible GraphQL schema.
        """
        field_sets = (
            "id sourceId url title thumbnailUrl initialized artist author description genre status realUrl inLibrary downloadCount chapters { totalCount }",
            "id sourceId url title thumbnailUrl inLibrary downloadCount chapters { totalCount }",
            "id sourceId url title thumbnailUrl inLibrary",
        )
        last_error: Exception | None = None
        for fields in field_sets:
            gql = f"""
            query AutoSourcarrLibrary($first: Int!, $after: Cursor) {{
              mangas(condition: {{inLibrary: true}}, first: $first, after: $after) {{
                nodes {{ {fields} }}
                pageInfo {{ hasNextPage endCursor }}
              }}
            }}
            """
            items: list[dict[str, Any]] = []
            after = None
            seen_cursors = set()
            seen_ids=set()
            try:
                for _ in range(100):
                    data = await self.graphql(gql, {"first": int(first), "after": after})
                    conn = data.get("mangas")
                    if not isinstance(conn,dict) or not isinstance(conn.get('nodes'),list):raise SuwayomiError('Incomplete main library connection')
                    nodes = self._connection_nodes(conn)
                    for row in nodes:
                        if type(row.get('id')) is not int or row['id']<=0 or row['id'] in seen_ids or not isinstance(row.get('sourceId'),(str,int)) or isinstance(row.get('sourceId'),bool) or not str(row['sourceId']) or not isinstance(row.get('url'),str) or not row['url'] or row.get('inLibrary') is not True:
                            raise SuwayomiError('Incomplete or repeated main library identity')
                        seen_ids.add(row['id'])
                    items.extend(dict(x) for x in nodes)
                    page = conn.get("pageInfo")
                    if not isinstance(page,dict) or type(page.get('hasNextPage')) is not bool:raise SuwayomiError('Incomplete main library pageInfo')
                    if page['hasNextPage'] is False:
                        return items
                    after = page.get("endCursor")
                    if not after or after in seen_cursors:
                        raise SuwayomiError("Main library pagination is incomplete or its cursor repeated; refusing an incomplete snapshot")
                    seen_cursors.add(after)
                raise SuwayomiError("Main library pagination exceeded the 100-page safety limit")
            except Exception as exc:
                last_error = exc
        raise SuwayomiError(f"Unable to query main library: {last_error}")

    async def set_manga_in_library(self, manga_id: int, in_library: bool = True) -> dict[str, Any]:
        """Use Suwayomi's GraphQL updateManga mutation to change library state.

        AutoSourcarr's backup importer only calls this with ``True``; the boolean
        argument keeps the client helper generally useful without making the import
        workflow capable of removing existing subscriptions.
        """
        gql = f"""
        mutation AutoSourcarrSetLibrary($input: UpdateMangaInput!) {{
          updateManga(input: $input) {{
            manga {{ {MANGA_FIELDS} }}
          }}
        }}
        """
        data = await self.graphql(gql, {"input": {"id": int(manga_id), "patch": {"inLibrary": bool(in_library)}}})
        payload = data.get("updateManga") or {}
        return dict(payload.get("manga") or {})

    async def source_manga_details(self, manga_id: int) -> dict[str, Any]:
        payload = await self.fetch_manga_and_chapters(int(manga_id), True, False)
        return dict(payload.get("manga") or {})

    async def _check_asset_attention(self, url):
        if not self.attention:
            return
        target = urlparse(url)
        if target.netloc == urlparse(self.base_url).netloc:
            match = re.match(r"/api/v1/manga/(\d+)/", target.path)
            if match:
                context = await self.attention.context(self.base_url, {'kind': 'manga', 'id': int(match[1])})
                await self.attention.check(context)
        else:
            await self.attention.check_website(self.base_url, target.hostname or '')

    async def check_chapter_attention(self, chapter_id):
        if self.attention:
            context = await self.attention.context(self.base_url, {'kind': 'pages', 'id': int(chapter_id)})
            await self.attention.check(context)

    async def open_binary_stream(self, url: str, timeout: float | None = None) -> httpx.Response:
        """Open an upstream response without buffering its body in AutoSourcarr."""
        url = self.runtime_asset_url(url)
        if url.startswith("/"):
            url = self.base_url + url
        await self._check_asset_attention(url)
        request = self.client.build_request("GET", url, timeout=self.timeout if timeout is None else timeout)
        response = await self.client.send(request, stream=True)
        try:
            response.raise_for_status()
        except Exception:
            await response.aclose()
            raise
        return response

    async def _fetch_limited_binary(
        self, url: str, *, timeout: float | None = None,
        headers: dict[str, str] | None = None, max_bytes: int = 32 * 1024 * 1024,
    ) -> tuple[bytes, str]:
        """Bound decoded asset/sample bytes; reader pages use the streaming API."""
        url = self.runtime_asset_url(url)
        if url.startswith("/"):
            url = self.base_url + url
        await self._check_asset_attention(url)
        request_headers={**(headers or {}), 'Accept-Encoding':'identity'}
        async with self.client.stream("GET", url, headers=request_headers,
                                      timeout=self.timeout if timeout is None else timeout) as response:
            response.raise_for_status()
            from .asset_decode import buffered_asset
            try:
                content=await buffered_asset(response,max_bytes)
            except ValueError as exc:
                raise SuwayomiError('Asset/sample buffered-analysis limit: '+str(exc)) from exc
            return content, response.headers.get("content-type", "application/octet-stream")

    async def fetch_binary(self, url: str, timeout: float | None = None) -> tuple[bytes, str]:
        return await self._fetch_limited_binary(url, timeout=timeout, max_bytes=MAX_IMAGE_BYTES)


    async def rest_json(self, path: str) -> Any:
        url = path if path.startswith("http://") or path.startswith("https://") else self.base_url + (path if path.startswith("/") else f"/{path}")
        response = await self.client.get(url)
        response.raise_for_status()
        return response.json()

    async def chapter_index_for_id(self, manga_id: int, chapter_id: int) -> int:
        """Resolve Suwayomi's REST chapterIndex from the GraphQL chapter database id."""
        payload = await self.rest_json(f"/api/v1/manga/{int(manga_id)}/chapters")
        chapters = payload.get("chapters", payload) if isinstance(payload, dict) else payload
        if not isinstance(chapters, list):
            raise SuwayomiError("Unexpected chapter-list response from source host")
        for index, chapter in enumerate(chapters):
            if isinstance(chapter, dict) and int(chapter.get("id", -1)) == int(chapter_id):
                # Some API variants expose their own index; otherwise list position is the REST chapterIndex.
                for key in ("index", "chapterIndex"):
                    if chapter.get(key) is not None:
                        return int(chapter[key])
                return index
        raise SuwayomiError(f"Chapter id {chapter_id} not found in manga {manga_id}")

    async def open_page_image_stream(self, manga_id: int, chapter_index: int, page_index: int) -> httpx.Response:
        # Let Suwayomi perform the real extension request. This preserves extension
        # cookies, headers, image decoding/interception and downloaded-page handling.
        return await self.open_binary_stream(
            f"/api/v1/manga/{int(manga_id)}/chapter/{int(chapter_index)}/page/{int(page_index)}"
        )

    async def fetch_page_image(self, manga_id: int, chapter_index: int, page_index: int) -> tuple[bytes, str]:
        return await self.fetch_binary(
            f"/api/v1/manga/{int(manga_id)}/chapter/{int(chapter_index)}/page/{int(page_index)}"
        )

    async def fetch_manga_thumbnail(self, manga_id: int, timeout: float | None = None) -> tuple[bytes, str]:
        return await self.fetch_binary(f"/api/v1/manga/{int(manga_id)}/thumbnail", timeout=timeout)

    async def fetch_external_binary(self, url: str, referer: str = "", timeout: float | None = None) -> tuple[bytes, str]:
        headers = {"Referer": referer} if referer else None
        return await self._fetch_limited_binary(url, headers=headers, timeout=timeout)


source_host = SuwayomiClient(work_limit=settings.source_host_work_concurrency, managed_engine=True)
from .source_attention import source_attention
source_host.attention = source_attention
main_suwayomi = SuwayomiClient(settings.main_suwayomi_url, credentials=(settings.main_suwayomi_username, settings.main_suwayomi_password)) if settings.main_suwayomi_url.strip() else None


async def bounded_map(
    items,
    func,
    limit: int | None = None,
    *,
    per_item_timeout: float | None = None,
    overall_timeout: float | None = None,
):
    """Run bounded work, retaining completed results and owning every child task.

    Parent cancellation must drain requests before a reset/retry can start another
    generation of work. Results remain in input order, including deadline failures.
    """
    items = list(items)
    if not items:
        return []
    concurrency = max(1, int(settings.max_source_concurrency if limit is None else limit))
    results = [None] * len(items)
    next_index = 0

    async def worker():
        nonlocal next_index
        while next_index < len(items):
            index = next_index
            next_index += 1
            item = items[index]
            try:
                work = func(item)
                if per_item_timeout is not None and per_item_timeout > 0:
                    # Private-source admission has its own bounded deferral. The
                    # execution clock starts only after that request owns capacity;
                    # the mapper's collective deadline still bounds the whole batch.
                    # Other workers keep the same ordinary per-item clock.
                    async with source_work_budget(per_item_timeout):
                        value = await work
                else:
                    value = await work
                results[index] = (item, value, None)
            except asyncio.CancelledError:
                # A child operation that cancels itself must not silently abandon
                # the remaining queue. Parent cancellation is handled by finally.
                if asyncio.current_task().cancelling():
                    raise
                results[index] = (item, None, RuntimeError("source operation was cancelled"))
            except BatchDeadlineExceeded as exc:
                results[index] = (item, None, exc)
            except TimeoutError as exc:
                text = (f"source timed out after {per_item_timeout:g}s"
                        if per_item_timeout is not None and per_item_timeout > 0
                        else str(exc) or "source request timed out")
                results[index] = (item, None, exc if getattr(exc, "diagnostic_context", None) and per_item_timeout is None else TimeoutError(text))
            except Exception as exc:
                results[index] = (item, None, exc)

    workers = [asyncio.create_task(worker()) for _ in range(min(concurrency, len(items)))]
    try:
        await asyncio.wait(workers, timeout=overall_timeout)
        timeout_text = (f"federated deadline reached after {overall_timeout:g}s"
                        if overall_timeout is not None else "source worker stopped")
        return [result if result is not None else (items[i], None, BatchDeadlineExceeded(timeout_text))
                for i, result in enumerate(results)]
    finally:
        for task in workers:
            if not task.done():
                task.cancel()
        drain = asyncio.gather(*workers, return_exceptions=True)
        cancelled_during_cleanup = False
        while not drain.done():
            try:
                await asyncio.shield(drain)
            except asyncio.CancelledError:
                cancelled_during_cleanup = True
        drain.result()
        if cancelled_during_cleanup:
            raise asyncio.CancelledError()
