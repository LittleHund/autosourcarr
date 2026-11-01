package autosourcarr.host

import androidx.preference.ListPreference
import androidx.preference.MultiSelectListPreference
import androidx.preference.Preference
import androidx.preference.PreferenceScreen
import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import eu.kanade.tachiyomi.source.CatalogueSource
import eu.kanade.tachiyomi.source.ConfigurableSource
import eu.kanade.tachiyomi.source.model.Page
import eu.kanade.tachiyomi.source.model.SChapter
import eu.kanade.tachiyomi.source.model.SManga
import eu.kanade.tachiyomi.source.online.HttpSource
import eu.kanade.tachiyomi.source.sourcePreferences
import io.github.oshai.kotlinlogging.KotlinLogging
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.int
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.longOrNull
import kotlinx.serialization.json.put
import uy.kohesive.injekt.Injekt
import uy.kohesive.injekt.api.get
import java.net.InetSocketAddress
import java.net.URLDecoder
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.util.concurrent.Executors

/**
 * REST API of the source host. Objects keep the field names of Suwayomi's GraphQL schema (manga, chapter, source,
 * extension), ids are the host's stable numeric ids, and images use Suwayomi's REST paths
 * (/api/v1/manga/{id}/thumbnail, /api/v1/manga/{id}/chapter/{sourceOrder}/page/{index}), so AutoSourcarr keeps
 * its stored ids and response checks. Source ids are unsigned 64-bit numbers sent as strings.
 */
class HostApi(
    private val store: ExtensionStore,
    private val repos: ExtensionRepos,
    private val db: HostDb,
    private val backoff: SourceBackoff,
) {
    private val logger = KotlinLogging.logger {}
    private val json = Json { ignoreUnknownKeys = true; explicitNulls = true; encodeDefaults = true }

    class HttpError(
        val status: Int,
        message: String,
    ) : RuntimeException(message)

    fun start(
        bind: String,
        port: Int,
    ): HttpServer {
        val server = HttpServer.create(InetSocketAddress(bind, port), 64)
        server.executor = Executors.newFixedThreadPool(32) { runnable -> Thread(runnable, "source-host-http").apply { isDaemon = true } }
        server.createContext("/") { exchange -> handle(exchange) }
        server.start()
        return server
    }

    private fun handle(exchange: HttpExchange) {
        try {
            if (HostConfig.token.isNotEmpty() && !sameToken(exchange.requestHeaders.getFirst("X-Source-Host-Token") ?: "")) {
                throw HttpError(401, "Missing or wrong X-Source-Host-Token")
            }
            val parts = exchange.requestURI.rawPath.split('/').filter { it.isNotEmpty() }.map { URLDecoder.decode(it, StandardCharsets.UTF_8) }
            val query = query(exchange.requestURI.rawQuery)
            when {
                parts.size >= 2 && parts[0] == "api" && parts[1] == "v1" -> images(exchange, parts.drop(2))
                parts.firstOrNull() == "v1" -> route(exchange, exchange.requestMethod, parts.drop(1), query)
                else -> throw HttpError(404, "Unknown path")
            }
        } catch (error: HttpError) {
            reply(exchange, error.status, problem(error.message ?: "Error"))
        } catch (error: NoSuchElementException) {
            reply(exchange, 404, problem(error.message ?: "Not found"))
        } catch (error: IllegalArgumentException) {
            reply(exchange, 400, problem(error.message ?: "Bad request"))
        } catch (error: SourceBackoff.BackedOff) {
            reply(exchange, 503, problem(error.message ?: "Source paused", "SourceBackedOff"))
        } catch (error: Throwable) {
            val deferred = LocalPacingDeferred.of(error)
            if (deferred != null) {
                // Local pacing, not a source answer: the client's deferral contract (503 + SourceBackedOff), no outage.
                exchange.responseHeaders.set("Retry-After", deferred.retryAfterSeconds.toString())
                reply(exchange, 503, problem(deferred.message ?: "Request deferred by local pacing", "SourceBackedOff"))
                return
            }
            logger.warn(error) { "Request ${exchange.requestMethod} ${exchange.requestURI.rawPath} failed" }
            // A source error (site down, changed layout, Cloudflare) is the source's answer, not a host failure.
            reply(exchange, 502, problem(error.message ?: error.javaClass.simpleName, error.javaClass.simpleName))
        } finally {
            exchange.close()
        }
    }

    private fun problem(
        detail: String,
        type: String? = null,
    ) = buildJsonObject {
        put("detail", detail)
        if (type != null) put("error_type", type)
    }

    private fun route(
        exchange: HttpExchange,
        method: String,
        parts: List<String>,
        query: Map<String, String>,
    ) {
        when {
            method == "GET" && parts == listOf("health") -> reply(exchange, 200, health())
            // Source browser identity and data (the app's Source browser page).
            method == "GET" && parts == listOf("browser") -> reply(exchange, 200, browserJson())
            method == "PUT" && parts == listOf("browser", "user-agent") -> {
                val agent = body(exchange)["user_agent"]?.jsonPrimitive?.contentOrNull?.trim() ?: ""
                require(agent.length <= 1024 && agent.all { it.code in 32..126 }) { "User agent must be at most 1024 printable ASCII characters" }
                val network = Injekt.get<eu.kanade.tachiyomi.network.NetworkHelper>()
                network.setUserAgent(agent.ifEmpty { eu.kanade.tachiyomi.network.NetworkHelper.DEFAULT_USER_AGENT })
                reply(exchange, 200, browserJson())
            }
            method == "POST" && parts == listOf("browser", "clear-data") -> {
                Injekt.get<eu.kanade.tachiyomi.network.NetworkHelper>().cookieStore.removeAll()
                ExternalSolvers.clear()
                runCatching { if (WebViewEngine.ready()) org.cef.network.CefCookieManager.getGlobalManager()?.deleteCookies("", "") }
                reply(exchange, 200, browserJson())
            }
            method == "POST" && parts == listOf("sources", "reload") -> {
                store.reloadAll()
                reply(exchange, 200, health())
            }
            // Diagnostic: one GET through the source client with the Cloudflare interceptor (FlareSolverr or WebView).
            method == "POST" && parts == listOf("webview", "fetch") -> {
                val url = body(exchange)["url"]?.jsonPrimitive?.contentOrNull ?: throw IllegalArgumentException("Send url")
                reply(exchange, 200, webViewFetch(url))
            }
            // Extensions and repositories
            method == "GET" && parts == listOf("extensions") -> reply(exchange, 200, extensionList())
            method == "POST" && parts == listOf("extensions", "fetch") -> {
                val errors = repos.fetch()
                reply(exchange, 200, extensionList(errors))
            }
            method == "GET" && parts == listOf("extension-stores") -> reply(exchange, 200, JsonArray(repos.stores().map { storeJson(it) }))
            method == "PUT" && parts == listOf("extension-stores") -> {
                val urls = body(exchange)["urls"]?.jsonArray?.map { it.jsonPrimitive.content } ?: throw IllegalArgumentException("Send urls")
                repos.setStores(urls)
                reply(exchange, 200, JsonArray(repos.stores().map { storeJson(it) }))
            }
            method == "POST" && parts.size == 3 && parts[0] == "extensions" -> extensionAction(exchange, parts[1], parts[2])
            method == "POST" && parts == listOf("extensions", "install-url") -> {
                val body = body(exchange)
                val url = body["apk_url"]?.jsonPrimitive?.contentOrNull ?: throw IllegalArgumentException("Send apk_url")
                val installed = store.installFromUrl(url, body["sha256"]?.jsonPrimitive?.contentOrNull)
                reply(exchange, 200, extensionJson(installed.pkg))
            }
            method == "POST" && parts == listOf("extensions", "install-file") -> {
                val name = query["name"] ?: "extension.apk"
                if (!name.endsWith(".apk")) throw IllegalArgumentException("Only .apk files can be installed")
                val file = java.io.File.createTempFile("upload-", ".apk", HostConfig.extensionsDir)
                try {
                    file.outputStream().use { output -> limited(exchange, 64L * 1024 * 1024).copyTo(output) }
                    val installed = store.installApk(file)
                    reply(exchange, 200, extensionJson(installed.pkg))
                } finally {
                    file.delete()
                }
            }
            // AutoSourcarr source repair: the installed jar (last working code) and a repaired or restored jar for it.
            method == "GET" && parts.size == 3 && parts[0] == "extensions" && parts[2] == "jar" -> {
                val jar = store.jarFile(parts[1]) ?: throw NoSuchElementException("${parts[1]} is not installed")
                val bytes = jar.readBytes()
                exchange.responseHeaders.set("Content-Type", "application/java-archive")
                exchange.responseHeaders.set("Content-Disposition", "attachment; filename=\"${jar.name}\"")
                exchange.responseHeaders.set("Cache-Control", "no-store")
                exchange.sendResponseHeaders(200, bytes.size.toLong())
                exchange.responseBody.write(bytes)
            }
            method == "POST" && parts == listOf("extensions", "install-jar") -> {
                val pkg = query["pkg"]?.takeIf { it.isNotBlank() } ?: throw IllegalArgumentException("Send pkg")
                val file = java.io.File.createTempFile("upload-", ".jar", HostConfig.extensionsDir)
                try {
                    file.outputStream().use { output -> limited(exchange, 64L * 1024 * 1024).copyTo(output) }
                    val installed = store.installJar(pkg, file)
                    reply(exchange, 200, extensionJson(installed.pkg))
                } finally {
                    file.delete()
                }
            }
            // Sources
            method == "GET" && parts == listOf("sources") -> reply(exchange, 200, JsonArray(SourceRegistry.all().map { sourceJson(it) }))
            parts.size >= 2 && parts[0] == "sources" -> sourceRoute(exchange, method, parseSourceId(parts[1]), parts.drop(2), query)
            // Titles, chapters and library (stable ids)
            method == "GET" && parts == listOf("library") -> {
                val first = (query["first"]?.toIntOrNull() ?: 500).coerceIn(1, 10_000)
                val after = query["after"]?.toLongOrNull() ?: 0
                val rows = db.library(first + 1, after)
                reply(exchange, 200, buildJsonObject {
                    put("nodes", JsonArray(rows.take(first).map { mangaJson(it) }))
                    put("pageInfo", buildJsonObject {
                        put("hasNextPage", rows.size > first)
                        put("endCursor", rows.take(first).lastOrNull()?.id?.toString())
                    })
                })
            }
            parts.size >= 2 && parts[0] == "mangas" -> mangaRoute(exchange, method, parts[1].toLongOrNull() ?: throw IllegalArgumentException("Bad manga id"), parts.drop(2))
            method == "POST" && parts.size == 3 && parts[0] == "chapters" && parts[2] == "pages" -> {
                val chapter = db.chapter(parts[1].toLongOrNull() ?: throw IllegalArgumentException("Bad chapter id")) ?: throw NoSuchElementException("Unknown chapter")
                val pages = loadPages(chapter, refresh = true)
                reply(exchange, 200, buildJsonObject {
                    put("pages", JsonArray(pages.indices.map { JsonPrimitive("/api/v1/manga/${chapter.mangaId}/chapter/${chapter.sourceOrder}/page/$it") }))
                    put("chapter", chapterJson(db.chapter(chapter.id)!!))
                })
            }
            method == "POST" && parts == listOf("import-ids") -> importIds(exchange)
            // Phase 7: the Cloudflare handlers (built-in WebView first, then the configured solvers) and the
            // interactive WebView ("Open in WebView", image CAPTCHAs).
            method == "GET" && parts == listOf("challenges", "solvers") -> reply(exchange, 200, solversJson())
            method == "GET" && parts == listOf("browser", "cookies") -> {
                val url = InteractiveWebView.checkedUrl(query["url"] ?: throw IllegalArgumentException("Send url"))
                val network = Injekt.get<eu.kanade.tachiyomi.network.NetworkHelper>()
                // Names and expiry only: cookie values never leave the host.
                reply(exchange, 200, buildJsonObject {
                    put("url", url.toString())
                    put("cookies", JsonArray(network.cookieStore.get(url).map { cookie ->
                        buildJsonObject {
                            put("name", cookie.name)
                            put("domain", cookie.domain)
                            put("expires_at", if (cookie.persistent) cookie.expiresAt else null)
                        }
                    }))
                })
            }
            parts.size >= 2 && parts[0] == "webview" && parts[1] == "sessions" -> webViewSession(exchange, method, parts.drop(2))
            else -> throw HttpError(404, "Unknown path")
        }
    }

    private fun solversJson(): JsonObject =
        buildJsonObject {
            put("builtin", buildJsonObject {
                put("name", "WebView")
                put("enabled", HostConfig.webViewEnabled && HostConfig.builtinSolverEnabled)
                put("state", WebViewEngine.state)
                put("ready", HostConfig.builtinSolverEnabled && WebViewEngine.ready())
            })
            put("external", JsonArray(ExternalSolvers.configured().map { solver ->
                buildJsonObject {
                    put("url", solver.display)
                    put("timeout_ms", solver.timeoutMs)
                    put("origin", solver.origin)
                }
            }))
            put("cached", JsonArray(ExternalSolvers.cached().map { (host, clearance) ->
                buildJsonObject {
                    put("host", host)
                    put("solver", clearance.solver)
                    put("expires_at", clearance.expiresAt)
                }
            }))
            put("interactive_sessions", InteractiveWebView.count())
        }

    /**
     * POST /v1/webview/sessions {url?, width?, height?} opens a session (an empty page without url); GET .../{id} describes it; GET .../{id}/stream
     * is the frame and event stream (see [InteractiveWebView]); POST .../{id}/input sends one input event;
     * DELETE .../{id} closes it (cookies are copied into the jar first).
     */
    private fun webViewSession(
        exchange: HttpExchange,
        method: String,
        rest: List<String>,
    ) {
        when {
            method == "POST" && rest.isEmpty() -> {
                val body = body(exchange)
                val url = body["url"]?.jsonPrimitive?.contentOrNull?.takeIf { it.isNotBlank() }?.let { InteractiveWebView.reachableUrl(it) }
                val session =
                    InteractiveWebView.open(
                        url,
                        body["width"]?.jsonPrimitive?.intOrNull ?: 1280,
                        body["height"]?.jsonPrimitive?.intOrNull ?: 800,
                    )
                reply(exchange, 200, session.describe())
            }
            method == "GET" && rest.size == 1 -> reply(exchange, 200, InteractiveWebView.get(rest[0]).describe())
            method == "GET" && rest.size == 2 && rest[1] == "stream" -> {
                val session = InteractiveWebView.get(rest[0])
                exchange.responseHeaders.set("Content-Type", "application/octet-stream")
                exchange.responseHeaders.set("Cache-Control", "no-store")
                exchange.sendResponseHeaders(200, 0) // chunked: each flush sends what was written
                session.stream(exchange.responseBody)
            }
            method == "POST" && rest.size == 2 && rest[1] == "input" -> {
                InteractiveWebView.get(rest[0]).input(body(exchange))
                reply(exchange, 200, buildJsonObject { put("ok", true) })
            }
            method == "DELETE" && rest.size == 1 -> {
                InteractiveWebView.close(rest[0], "Closed by AutoSourcarr")
                reply(exchange, 200, buildJsonObject { put("ok", true) })
            }
            else -> throw HttpError(404, "Unknown path")
        }
    }

    private fun sourceRoute(
        exchange: HttpExchange,
        method: String,
        id: Long,
        rest: List<String>,
        query: Map<String, String>,
    ) {
        val entry = SourceRegistry.get(id) ?: throw NoSuchElementException("Source $id is not installed")
        when {
            method == "GET" && rest.isEmpty() -> reply(exchange, 200, sourceJson(entry))
            method == "GET" && rest == listOf("mangas") -> {
                val type = (query["type"] ?: "POPULAR").uppercase()
                val page = (query["page"]?.toIntOrNull() ?: 1).coerceAtLeast(1)
                val source = entry.source as? CatalogueSource ?: throw IllegalArgumentException("This source cannot be browsed")
                val result =
                    backoff.run(source.id) {
                        runBlocking {
                            when (type) {
                                "POPULAR" -> source.getPopularManga(page)
                                "LATEST" -> {
                                    if (!source.supportsLatest) throw IllegalArgumentException("${source.name} has no latest list")
                                    source.getLatestUpdates(page)
                                }
                                "SEARCH" -> source.getSearchManga(page, query["query"] ?: "", source.getFilterList())
                                else -> throw IllegalArgumentException("type is POPULAR, LATEST or SEARCH")
                            }
                        }
                    }
                val sourceId = id.toString()
                val stored = result.mangas.map { db.upsertListed(sourceId, it) }
                reply(exchange, 200, buildJsonObject {
                    put("hasNextPage", result.hasNextPage)
                    put("mangas", JsonArray(stored.map { mangaJson(it) }))
                })
            }
            method == "GET" && rest == listOf("preferences") -> reply(exchange, 200, JsonArray(preferences(entry).mapIndexed { index, pref -> preferenceJson(index, pref) }))
            method == "PUT" && rest == listOf("preferences") -> {
                val body = body(exchange)
                val position = body["position"]?.jsonPrimitive?.int ?: throw IllegalArgumentException("Send position")
                val value = body["value"] ?: throw IllegalArgumentException("Send value")
                val pref = preferences(entry).getOrNull(position) ?: throw IllegalArgumentException("No preference at position $position")
                if (pref.isEnabled) {
                    val newValue: Any =
                        when (pref.defaultValueType) {
                            "String" -> value.jsonPrimitive.content
                            "Boolean" -> value.jsonPrimitive.boolean
                            "Set<String>" -> value.jsonArray.map { it.jsonPrimitive.content }.toSet()
                            else -> throw IllegalArgumentException("Unsupported preference type ${pref.defaultValueType}")
                        }
                    pref.saveNewValue(newValue)
                    pref.callChangeListener(newValue)
                    // Sources read their preferences when they are built, so the package is loaded again.
                    store.reload(entry.pkg)
                }
                val fresh = SourceRegistry.get(id) ?: entry
                reply(exchange, 200, JsonArray(preferences(fresh).mapIndexed { index, item -> preferenceJson(index, item) }))
            }
            method == "GET" && rest == listOf("backoff") -> reply(exchange, 200, backoff.state(id))
            method == "DELETE" && rest == listOf("backoff") -> {
                backoff.reset(id)
                reply(exchange, 200, backoff.state(id))
            }
            else -> throw HttpError(404, "Unknown path")
        }
    }

    private fun mangaRoute(
        exchange: HttpExchange,
        method: String,
        id: Long,
        rest: List<String>,
    ) {
        val manga = db.manga(id) ?: throw NoSuchElementException("Unknown manga $id")
        when {
            method == "GET" && rest.isEmpty() -> reply(exchange, 200, mangaJson(manga))
            method == "GET" && rest == listOf("chapters") -> reply(exchange, 200, JsonArray(db.chapters(id).map { chapterJson(it) }))
            method == "POST" && rest == listOf("fetch") -> {
                val body = body(exchange)
                val fetchManga = body["fetchManga"]?.jsonPrimitive?.booleanOrNull ?: true
                val fetchChapters = body["fetchChapters"]?.jsonPrimitive?.booleanOrNull ?: true
                val entry = SourceRegistry.get(parseSourceId(manga.sourceId)) ?: throw NoSuchElementException("Source ${manga.sourceId} is not installed")
                val source = entry.source
                val input =
                    SManga.create().apply {
                        url = manga.url
                        title = manga.title
                        thumbnail_url = manga.thumbnailUrl
                        initialized = manga.initialized
                        memo = manga.memo
                    }
                val known = db.chapters(id).map { row -> row.toSChapter() }
                // tachiyomix 1.6 combined call; CatalogueSource bridges older extensions to their fetch* methods.
                val update = backoff.run(source.id) { runBlocking { source.getMangaUpdate(input, known, fetchManga, fetchChapters) } }
                val http = source as? HttpSource
                var stored = manga
                if (fetchManga) {
                    val details = update.manga.also { it.url = manga.url }
                    stored = db.updateDetails(id, details, http?.let { runCatching { it.getMangaUrl(details) }.getOrNull() })
                }
                val chapters =
                    if (fetchChapters) {
                        db.replaceChapters(id, update.chapters) { chapter -> http?.let { runCatching { it.getChapterUrl(chapter) }.getOrNull() } }
                    } else {
                        db.chapters(id)
                    }
                reply(exchange, 200, buildJsonObject {
                    put("manga", mangaJson(stored))
                    put("chapters", JsonArray(chapters.map { chapterJson(it) }))
                })
            }
            method == "PUT" && rest == listOf("library") -> {
                val inLibrary = body(exchange)["inLibrary"]?.jsonPrimitive?.booleanOrNull ?: throw IllegalArgumentException("Send inLibrary")
                reply(exchange, 200, mangaJson(db.setLibrary(id, inLibrary)!!))
            }
            else -> throw HttpError(404, "Unknown path")
        }
    }

    /** Suwayomi's image paths: thumbnail and page by chapter source order. */
    private fun images(
        exchange: HttpExchange,
        parts: List<String>,
    ) {
        if (exchange.requestMethod == "GET" && parts == listOf("settings", "about")) {
            reply(exchange, 200, buildJsonObject {
                put("name", "AutoSourcarr source host")
                put("version", HostConfig.HOST_VERSION)
                put("revision", HostConfig.COMPAT_VERSION)
            })
            return
        }
        if (exchange.requestMethod == "GET" && parts.size == 3 && parts[0] == "manga" && parts[2] == "chapters") {
            val id = parts[1].toLongOrNull() ?: throw IllegalArgumentException("Bad manga id")
            db.manga(id) ?: throw NoSuchElementException("Unknown manga")
            // Suwayomi's REST chapter list: "index" is the chapter's source order (used in page paths).
            reply(exchange, 200, JsonArray(db.chapters(id).map { chapter -> buildJsonObject {
                put("id", chapter.id)
                put("index", chapter.sourceOrder)
                put("name", chapter.name)
                put("url", chapter.url)
                put("mangaId", chapter.mangaId)
            } }))
            return
        }
        if (exchange.requestMethod != "GET" || parts.size < 3 || parts[0] != "manga") throw HttpError(404, "Unknown path")
        val manga = db.manga(parts[1].toLongOrNull() ?: throw IllegalArgumentException("Bad manga id")) ?: throw NoSuchElementException("Unknown manga")
        val source = SourceRegistry.get(parseSourceId(manga.sourceId))?.source as? HttpSource ?: throw NoSuchElementException("Source ${manga.sourceId} is not installed")
        when {
            parts.size == 3 && parts[2] == "thumbnail" -> {
                val url = manga.thumbnailUrl ?: throw NoSuchElementException("This title has no cover")
                sendImage(exchange, backoff.run(source.id) { runBlocking { source.getImage(Page(0, imageUrl = url)) } })
            }
            parts.size == 6 && parts[2] == "chapter" && parts[4] == "page" -> {
                val order = parts[3].toIntOrNull() ?: throw IllegalArgumentException("Bad chapter index")
                val index = parts[5].toIntOrNull() ?: throw IllegalArgumentException("Bad page index")
                val chapter = db.chapterByOrder(manga.id, order) ?: throw NoSuchElementException("Unknown chapter")
                val pages = loadPages(chapter, refresh = false)
                val page = pages.getOrNull(index) ?: throw NoSuchElementException("Page $index does not exist")
                if (page.imageUrl.isNullOrEmpty()) page.imageUrl = backoff.run(source.id) { runBlocking { source.getImageUrl(page) } }
                sendImage(exchange, backoff.run(source.id) { runBlocking { source.getImage(page) } })
            }
            else -> throw HttpError(404, "Unknown path")
        }
    }

    private fun sendImage(
        exchange: HttpExchange,
        response: okhttp3.Response,
    ) {
        response.use {
            if (!it.isSuccessful) throw IllegalStateException("The source answered HTTP ${it.code} for the image")
            val bytes = it.body.bytes()
            exchange.responseHeaders.set("Content-Type", it.header("Content-Type") ?: "application/octet-stream")
            exchange.responseHeaders.set("Cache-Control", "no-store")
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.write(bytes)
        }
    }

    private fun loadPages(
        chapter: HostDb.Chapter,
        refresh: Boolean,
    ): List<Page> {
        if (!refresh) {
            db.pagesJson(chapter.id)?.let { text ->
                return json.parseToJsonElement(text).jsonArray.map { node ->
                    val row = node.jsonObject
                    Page(row["index"]!!.jsonPrimitive.int, row["url"]?.jsonPrimitive?.contentOrNull ?: "", row["imageUrl"]?.jsonPrimitive?.contentOrNull)
                }
            }
        }
        val manga = db.manga(chapter.mangaId)!!
        val source = SourceRegistry.get(parseSourceId(manga.sourceId))?.source ?: throw NoSuchElementException("Source ${manga.sourceId} is not installed")
        // The fields Suwayomi passes too: some sources read the number, scanlator or date when building the request.
        val input = chapter.toSChapter()
        val pages = backoff.run(source.id) { runBlocking { source.getPageList(input) } }
        val text =
            JsonArray(
                pages.map { page ->
                    buildJsonObject {
                        put("index", page.index)
                        put("url", page.url)
                        put("imageUrl", page.imageUrl)
                    }
                },
            ).toString()
        db.savePages(chapter.id, text, pages.size)
        return pages
    }

    private fun extensionAction(
        exchange: HttpExchange,
        pkg: String,
        action: String,
    ) {
        when (action) {
            "install", "update" -> {
                val available = repos.available().firstOrNull { it.pkg == pkg } ?: throw NoSuchElementException("$pkg is in no configured repository")
                store.installFromUrl(available.apkUrl)
            }
            "uninstall" -> if (!store.uninstall(pkg)) throw NoSuchElementException("$pkg is not installed")
            else -> throw IllegalArgumentException("Action is install, update or uninstall")
        }
        reply(exchange, 200, buildJsonObject { put("extension", extensionJson(pkg)) })
    }

    private fun extensionList(errors: Map<String, String> = emptyMap()): JsonObject {
        val installed = store.installed().associateBy { it.pkg }
        val available = repos.available().associateBy { it.pkg }
        val packages = (installed.keys + available.keys).sorted()
        return buildJsonObject {
            put("extensions", JsonArray(packages.map { extensionJson(it, installed, available) }))
            put("extensionStores", JsonArray(repos.stores().map { storeJson(it) }))
            if (errors.isNotEmpty()) put("storeErrors", buildJsonObject { errors.forEach { (url, message) -> put(url, message) } })
        }
    }

    private fun extensionJson(
        pkg: String,
        installed: Map<String, ExtensionStore.InstalledExtension> = store.installed().associateBy { it.pkg },
        available: Map<String, ExtensionRepos.Available> = repos.available().associateBy { it.pkg },
    ): JsonObject {
        val local = installed[pkg]
        val remote = available[pkg]
        return buildJsonObject {
            put("pkgName", pkg)
            put("name", local?.name ?: remote?.name ?: pkg)
            put("lang", local?.lang ?: remote?.lang ?: "")
            put("versionName", local?.versionName ?: remote?.versionName)
            put("versionCodeLong", (local?.versionCode?.toLong() ?: remote?.versionCode ?: 0).toString())
            put("isInstalled", local != null)
            put("hasUpdate", local != null && remote != null && remote.versionCode > local.versionCode)
            put("isObsolete", local != null && remote == null && repos.stores().isNotEmpty())
            put("iconUrl", remote?.iconUrl)
            put("contentWarning", local?.contentWarning ?: remote?.contentWarning ?: 0)
            put("storeIndexUrl", remote?.storeIndexUrl)
            put("apkUrl", remote?.apkUrl ?: local?.apkUrl)
            put("apkName", remote?.apkName)
            put("libVersion", local?.libVersion ?: remote?.libVersion)
            put("loadError", local?.loadError)
            put("source", buildJsonObject {
                val nodes =
                    if (local != null) {
                        local.sourceIds.mapNotNull { id -> SourceRegistry.get(parseSourceId(id)) }.map { entry ->
                            buildJsonObject {
                                put("id", entry.source.id.toString())
                                put("name", entry.source.name)
                                put("lang", entry.source.lang)
                                put("baseUrl", (entry.source as? HttpSource)?.baseUrl)
                                put("isConfigurable", entry.source is ConfigurableSource)
                            }
                        }
                    } else {
                        remote?.sources.orEmpty().map { source ->
                            buildJsonObject {
                                put("id", source.id)
                                put("name", source.name)
                                put("lang", source.lang)
                                put("baseUrl", source.baseUrl)
                                put("isConfigurable", false)
                            }
                        }
                    }
                put("nodes", JsonArray(nodes))
            })
        }
    }

    private fun storeJson(store: ExtensionRepos.Store) =
        buildJsonObject {
            put("name", store.name)
            put("indexUrl", store.indexUrl)
            put("badgeLabel", store.badgeLabel)
            put("isLegacy", store.isLegacy)
            put("extensionListUrl", store.extensionListUrl)
        }

    private fun importIds(exchange: HttpExchange) {
        val body = body(exchange)
        val mangas =
            body["mangas"]?.jsonArray.orEmpty().map { node ->
                val row = node.jsonObject
                Triple(row["id"]!!.jsonPrimitive.longOrNull!!, row["sourceId"]!!.jsonPrimitive.content, row["url"]!!.jsonPrimitive.content)
            }
        val chapters =
            body["chapters"]?.jsonArray.orEmpty().map { node ->
                val row = node.jsonObject
                HostDb.Chapter(
                    row["id"]!!.jsonPrimitive.longOrNull!!, row["mangaId"]!!.jsonPrimitive.longOrNull!!, row["url"]!!.jsonPrimitive.content,
                    row["name"]?.jsonPrimitive?.contentOrNull ?: "", row["uploadDate"]?.jsonPrimitive?.contentOrNull?.toLongOrNull() ?: 0,
                    row["chapterNumber"]?.jsonPrimitive?.contentOrNull?.toDoubleOrNull() ?: -1.0, row["scanlator"]?.jsonPrimitive?.contentOrNull,
                    row["sourceOrder"]?.jsonPrimitive?.intOrNull ?: 0, null, -1,
                )
            }
        val (mangaCount, chapterCount, conflicts) = db.importIds(mangas, chapters)
        reply(exchange, 200, buildJsonObject {
            put("mangas", mangaCount)
            put("chapters", chapterCount)
            put("conflicts", conflicts)
        })
    }

    private fun preferences(entry: SourceRegistry.Entry): List<Preference> {
        val source = entry.source as? ConfigurableSource ?: return emptyList()
        val screen = PreferenceScreen(Injekt.get<android.app.Application>())
        screen.sharedPreferences = source.sourcePreferences()
        source.setupPreferenceScreen(screen)
        return screen.preferences
    }

    private fun HostDb.Chapter.toSChapter(): SChapter {
        val row = this
        return SChapter.create().apply {
            url = row.url
            name = row.name
            date_upload = row.uploadDate
            chapter_number = row.chapterNumber.toFloat()
            scanlator = row.scanlator
            memo = row.memo
        }
    }

    private fun health(): JsonObject =
        buildJsonObject {
            put("ok", true)
            put("version", HostConfig.HOST_VERSION)
            put("compat_version", HostConfig.COMPAT_VERSION)
            put("extensions", store.installed().size)
            put("extension_load_errors", store.installed().count { it.loadError != null })
            put("sources", SourceRegistry.all().size)
            put("backed_off_sources", backoff.count())
            put("java", System.getProperty("java.version"))
            put("webview", WebViewEngine.state)
        }

    private fun browserJson(): JsonObject {
        val network = Injekt.get<eu.kanade.tachiyomi.network.NetworkHelper>()
        return buildJsonObject {
            put("user_agent", network.defaultUserAgentProvider())
            put("default_user_agent", eu.kanade.tachiyomi.network.NetworkHelper.DEFAULT_USER_AGENT)
            put("webview", WebViewEngine.state)
            put("cookies", network.cookieStore.getStoredCookies().size)
        }
    }

    private fun webViewFetch(url: String): JsonObject {
        val network = Injekt.get<eu.kanade.tachiyomi.network.NetworkHelper>()
        val target = okhttp3.HttpUrl.Builder().scheme("https").host("x").build().resolve(url) ?: throw IllegalArgumentException("Bad url")
        val started = System.nanoTime()
        return runCatching {
            network.cloudflareClient.newCall(okhttp3.Request.Builder().url(target).build()).execute().use { response ->
                val text = response.body.string()
                buildJsonObject {
                    put("status", response.code)
                    put("server", response.header("Server") ?: "")
                    put("challenge_page", text.contains("challenge-platform") || text.contains("Just a moment"))
                    put("clearance", network.cookieStore.get(target).any { it.name == "cf_clearance" })
                    put("ms", (System.nanoTime() - started) / 1_000_000)
                }
            }
        }.getOrElse { error ->
            buildJsonObject {
                put("status", 0)
                put("error", error.message ?: error.javaClass.simpleName)
                put("clearance", network.cookieStore.get(target).any { it.name == "cf_clearance" })
                put("ms", (System.nanoTime() - started) / 1_000_000)
            }
        }
    }

    private fun sourceJson(entry: SourceRegistry.Entry): JsonObject =
        buildJsonObject {
            val source = entry.source
            val http = source as? HttpSource
            put("id", source.id.toString())
            put("name", source.name)
            put("lang", source.lang)
            put("displayName", "${source.name} (${source.lang.uppercase()})")
            put("iconUrl", repos.available().firstOrNull { it.pkg == entry.pkg }?.iconUrl)
            put("supportsLatest", (source as? CatalogueSource)?.supportsLatest ?: false)
            put("isConfigurable", source is ConfigurableSource)
            put("isNsfw", entry.contentWarning != 0)
            put("baseUrl", http?.baseUrl)
            put("homeUrl", http?.let { runCatching { it.getHomeUrl() }.getOrNull() })
            put("pkg", entry.pkg)
        }

    private fun mangaJson(manga: HostDb.Manga): JsonObject =
        buildJsonObject {
            put("id", manga.id)
            put("sourceId", manga.sourceId)
            put("url", manga.url)
            put("title", manga.title)
            put("thumbnailUrl", if (manga.thumbnailUrl.isNullOrEmpty()) null else "/api/v1/manga/${manga.id}/thumbnail")
            put("initialized", manga.initialized)
            put("artist", manga.artist)
            put("author", manga.author)
            put("description", manga.description)
            put("genre", JsonArray(manga.genre.orEmpty().split(",").map { it.trim() }.filter { it.isNotEmpty() }.map { JsonPrimitive(it) }))
            put("status", STATUS.getOrElse(manga.status) { "UNKNOWN" })
            put("realUrl", manga.realUrl)
            put("inLibrary", manga.inLibrary)
        }

    private fun chapterJson(chapter: HostDb.Chapter): JsonObject =
        buildJsonObject {
            put("id", chapter.id)
            put("url", chapter.url)
            put("name", chapter.name)
            put("uploadDate", chapter.uploadDate.toString())
            put("chapterNumber", chapter.chapterNumber)
            put("scanlator", chapter.scanlator)
            put("mangaId", chapter.mangaId)
            put("sourceOrder", chapter.sourceOrder)
            put("realUrl", chapter.realUrl)
            put("pageCount", chapter.pageCount)
        }

    private fun preferenceJson(
        position: Int,
        pref: Preference,
    ): JsonObject =
        buildJsonObject {
            put("position", position)
            put("type", pref.javaClass.simpleName)
            put("key", pref.key)
            put("title", pref.title?.toString())
            put("summary", pref.summary?.toString())
            put("enabled", pref.isEnabled)
            put("visible", pref.visible)
            put("valueType", pref.defaultValueType)
            put("defaultValue", toJson(pref.defaultValue))
            put("currentValue", toJson(pref.currentValue))
            when (pref) {
                is ListPreference -> {
                    put("entries", JsonArray(pref.entries.orEmpty().map { JsonPrimitive(it.toString()) }))
                    put("entryValues", JsonArray(pref.entryValues.orEmpty().map { JsonPrimitive(it.toString()) }))
                }
                is MultiSelectListPreference -> {
                    put("entries", JsonArray(pref.entries.orEmpty().map { JsonPrimitive(it.toString()) }))
                    put("entryValues", JsonArray(pref.entryValues.orEmpty().map { JsonPrimitive(it.toString()) }))
                }
                else -> Unit
            }
        }

    private fun toJson(value: Any?): JsonElement =
        when (value) {
            null -> JsonNull
            is Boolean -> JsonPrimitive(value)
            is Number -> JsonPrimitive(value)
            is Collection<*> -> buildJsonArray { value.forEach { add(JsonPrimitive(it?.toString())) } }
            else -> JsonPrimitive(value.toString())
        }

    /** Source ids are 64-bit; Suwayomi and the AutoSourcarr catalogue write them signed, the repository index unsigned. Both are read. */
    private fun parseSourceId(text: String): Long = text.toLongOrNull() ?: text.toULongOrNull()?.toLong() ?: throw IllegalArgumentException("Bad source id")

    private fun limited(
        exchange: HttpExchange,
        maximum: Long,
    ): java.io.InputStream {
        val length = exchange.requestHeaders.getFirst("Content-Length")?.toLongOrNull()
        if (length != null && length > maximum) throw IllegalArgumentException("The upload is larger than ${maximum / 1024 / 1024} MiB")
        // Chunked uploads have no Content-Length: count while reading.
        val input = exchange.requestBody
        return object : java.io.FilterInputStream(input) {
            private var total = 0L

            private fun count(n: Int): Int {
                if (n > 0) {
                    total += n
                    if (total > maximum) throw IllegalArgumentException("The upload is larger than ${maximum / 1024 / 1024} MiB")
                }
                return n
            }

            override fun read(): Int = super.read().also { if (it >= 0) count(1) }

            override fun read(
                b: ByteArray,
                off: Int,
                len: Int,
            ): Int = count(super.read(b, off, len))
        }
    }

    private fun body(exchange: HttpExchange): JsonObject {
        val bytes = exchange.requestBody.readNBytes(8 * 1024 * 1024 + 1)
        if (bytes.size > 8 * 1024 * 1024) throw IllegalArgumentException("The request body is larger than 8 MiB")
        if (bytes.isEmpty()) return JsonObject(emptyMap())
        return json.parseToJsonElement(String(bytes, StandardCharsets.UTF_8)).jsonObject
    }

    private fun query(raw: String?): Map<String, String> =
        raw.orEmpty().split('&').filter { it.isNotEmpty() }.associate { part ->
            URLDecoder.decode(part.substringBefore('='), StandardCharsets.UTF_8) to URLDecoder.decode(part.substringAfter('=', ""), StandardCharsets.UTF_8)
        }

    private fun reply(
        exchange: HttpExchange,
        status: Int,
        body: JsonElement,
    ) {
        val bytes = json.encodeToString(JsonElement.serializer(), body).toByteArray(StandardCharsets.UTF_8)
        try {
            exchange.responseHeaders.set("Content-Type", "application/json; charset=utf-8")
            exchange.sendResponseHeaders(status, bytes.size.toLong())
            exchange.responseBody.write(bytes)
        } catch (error: java.io.IOException) {
            logger.debug(error) { "The client went away before the answer was sent" }
        }
    }

    private fun sameToken(supplied: String): Boolean =
        MessageDigest.isEqual(supplied.toByteArray(StandardCharsets.UTF_8), HostConfig.token.toByteArray(StandardCharsets.UTF_8))

    companion object {
        /** SManga.status values in Suwayomi's MangaStatus names. */
        val STATUS = listOf("UNKNOWN", "ONGOING", "COMPLETED", "LICENSED", "PUBLISHING_FINISHED", "CANCELLED", "ON_HIATUS")
    }
}
