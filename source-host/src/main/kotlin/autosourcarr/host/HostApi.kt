package autosourcarr.host

import androidx.preference.CheckBoxPreference
import androidx.preference.EditTextPreference
import androidx.preference.ListPreference
import androidx.preference.MultiSelectListPreference
import androidx.preference.Preference
import androidx.preference.PreferenceScreen
import androidx.preference.TwoStatePreference
import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import eu.kanade.tachiyomi.source.ConfigurableSource
import eu.kanade.tachiyomi.source.model.MangasPage
import eu.kanade.tachiyomi.source.model.Page
import eu.kanade.tachiyomi.source.model.SChapter
import eu.kanade.tachiyomi.source.model.SManga
import eu.kanade.tachiyomi.source.online.HttpSource
import eu.kanade.tachiyomi.source.sourcePreferences
import io.github.oshai.kotlinlogging.KotlinLogging
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.Serializable
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
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import uy.kohesive.injekt.Injekt
import uy.kohesive.injekt.api.get
import java.net.InetSocketAddress
import java.net.URLDecoder
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.util.concurrent.Executors

/**
 * REST API of the source host (JSON). It replaces the Suwayomi GraphQL operations AutoSourcarr used:
 * sources, source preferences, extensions, browse (popular/latest/search), manga details + chapters,
 * page lists and images. Ids are strings because source ids are unsigned 64-bit values.
 */
class HostApi(
    private val store: ExtensionStore,
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
            exchange.use {
                if (HostConfig.token.isNotEmpty() && !sameToken(exchange.requestHeaders.getFirst("X-Source-Host-Token") ?: "")) {
                    throw HttpError(401, "Missing or wrong X-Source-Host-Token")
                }
                val path = exchange.requestURI.rawPath.trimEnd('/')
                val query = query(exchange.requestURI.rawQuery)
                val parts = path.split('/').filter { it.isNotEmpty() }
                val method = exchange.requestMethod
                if (parts.firstOrNull() != "v1") throw HttpError(404, "Unknown path")
                route(exchange, method, parts.drop(1), query)
            }
        } catch (error: HttpError) {
            reply(exchange, error.status, buildJsonObject { put("detail", error.message) })
        } catch (error: NoSuchElementException) {
            reply(exchange, 404, buildJsonObject { put("detail", error.message ?: "Not found") })
        } catch (error: IllegalArgumentException) {
            reply(exchange, 400, buildJsonObject { put("detail", error.message ?: "Bad request") })
        } catch (error: Throwable) {
            logger.warn(error) { "Request ${exchange.requestMethod} ${exchange.requestURI.rawPath} failed" }
            // A source error (site down, changed layout, Cloudflare) is the source's answer, not a host failure.
            reply(exchange, 502, buildJsonObject {
                put("detail", error.message ?: error.javaClass.simpleName)
                put("error_type", error.javaClass.simpleName)
            })
        }
    }

    private fun route(
        exchange: HttpExchange,
        method: String,
        parts: List<String>,
        query: Map<String, String>,
    ) {
        when {
            method == "GET" && parts == listOf("health") -> reply(exchange, 200, health())
            method == "GET" && parts == listOf("extensions") -> reply(exchange, 200, JsonArray(store.installed().map { extensionJson(it) }))
            method == "POST" && parts == listOf("extensions", "install") -> {
                val body = body(exchange)
                val url = body["apk_url"]?.jsonPrimitive?.contentOrNull ?: throw IllegalArgumentException("Send apk_url")
                val sha = body["sha256"]?.jsonPrimitive?.contentOrNull
                reply(exchange, 200, extensionJson(store.installFromUrl(url, sha)))
            }
            method == "DELETE" && parts.size == 2 && parts[0] == "extensions" -> {
                if (!store.uninstall(parts[1])) throw NoSuchElementException("Extension ${parts[1]} is not installed")
                reply(exchange, 200, buildJsonObject { put("ok", true) })
            }
            method == "GET" && parts == listOf("sources") -> reply(exchange, 200, JsonArray(SourceRegistry.all().map { sourceJson(it) }))
            parts.size >= 2 && parts[0] == "sources" -> sourceRoute(exchange, method, parseId(parts[1]), parts.drop(2), query)
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
        val page = query["page"]?.toIntOrNull()?.coerceAtLeast(1) ?: 1
        when {
            method == "GET" && rest.isEmpty() -> reply(exchange, 200, sourceJson(entry))
            method == "GET" && rest == listOf("popular") ->
                reply(exchange, 200, pageJson(runBlocking { SourceRegistry.catalogue(id).getPopularManga(page) }))
            method == "GET" && rest == listOf("latest") -> {
                val source = SourceRegistry.catalogue(id)
                if (!source.supportsLatest) throw IllegalArgumentException("${source.name} has no latest list")
                reply(exchange, 200, pageJson(runBlocking { source.getLatestUpdates(page) }))
            }
            method == "GET" && rest == listOf("search") -> {
                val source = SourceRegistry.catalogue(id)
                val text = query["q"] ?: ""
                reply(exchange, 200, pageJson(runBlocking { source.getSearchManga(page, text, source.getFilterList()) }))
            }
            method == "POST" && rest == listOf("manga") -> {
                val body = body(exchange)
                val manga = mangaFromJson(body)
                val withChapters = body["chapters"]?.jsonPrimitive?.booleanOrNull ?: true
                val source = entry.source
                // tachiyomix 1.6 combined call; CatalogueSource bridges older extensions to their fetch* methods.
                val update = runBlocking { source.getMangaUpdate(manga, emptyList(), fetchDetails = true, fetchChapters = withChapters) }
                val details = update.manga.also { it.url = manga.url }
                val chapters: List<SChapter> = update.chapters
                reply(exchange, 200, buildJsonObject {
                    put("manga", mangaJson(details))
                    put("chapters", JsonArray(chapters.mapIndexed { index: Int, chapter: SChapter -> chapterJson(chapter, index) }))
                })
            }
            method == "POST" && rest == listOf("pages") -> {
                val body = body(exchange)
                val chapter = SChapter.create().apply {
                    url = body["url"]?.jsonPrimitive?.contentOrNull ?: throw IllegalArgumentException("Send the chapter url")
                    name = body["name"]?.jsonPrimitive?.contentOrNull ?: ""
                }
                val pages = runBlocking { entry.source.getPageList(chapter) }
                val source = entry.source as? HttpSource
                val resolved =
                    pages.map { page ->
                        if (page.imageUrl.isNullOrEmpty() && source != null && page.url.isNotEmpty()) {
                            page.imageUrl = runBlocking { source.getImageUrl(page) }
                        }
                        page
                    }
                reply(exchange, 200, buildJsonObject { put("pages", JsonArray(resolved.map { pageJson(it) })) })
            }
            method == "GET" && rest == listOf("image") -> {
                val source = entry.source as? HttpSource ?: throw IllegalArgumentException("This source has no images over HTTP")
                val url = query["url"] ?: throw IllegalArgumentException("Send url")
                val response = runBlocking { source.getImage(Page(0, imageUrl = url)) }
                response.use {
                    val bytes = it.body.bytes()
                    exchange.responseHeaders.add("Content-Type", it.header("Content-Type") ?: "application/octet-stream")
                    exchange.responseHeaders.add("X-Source-Status", it.code.toString())
                    exchange.sendResponseHeaders(if (it.isSuccessful) 200 else 502, bytes.size.toLong().coerceAtLeast(-1))
                    exchange.responseBody.write(bytes)
                }
            }
            method == "GET" && rest == listOf("preferences") -> reply(exchange, 200, JsonArray(preferences(entry).mapIndexed { index, pref -> preferenceJson(index, pref) }))
            method == "PUT" && rest == listOf("preferences") -> {
                val body = body(exchange)
                val position = body["position"]?.jsonPrimitive?.int ?: throw IllegalArgumentException("Send position")
                val value = body["value"] ?: throw IllegalArgumentException("Send value")
                val prefs = preferences(entry)
                val pref = prefs.getOrNull(position) ?: throw IllegalArgumentException("No preference at position $position")
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
                    // Sources read preferences when they are built, so the package is loaded again.
                    store.reload(entry.pkg)
                }
                val fresh = SourceRegistry.get(id) ?: entry
                reply(exchange, 200, JsonArray(preferences(fresh).mapIndexed { index, item -> preferenceJson(index, item) }))
            }
            else -> throw HttpError(404, "Unknown path")
        }
    }

    private fun preferences(entry: SourceRegistry.Entry): List<Preference> {
        val source = entry.source as? ConfigurableSource ?: return emptyList()
        val screen = PreferenceScreen(Injekt.get<android.app.Application>())
        screen.sharedPreferences = source.sourcePreferences()
        source.setupPreferenceScreen(screen)
        return screen.preferences
    }

    private fun health(): JsonObject =
        buildJsonObject {
            put("ok", true)
            put("version", HostConfig.HOST_VERSION)
            put("compat_version", HostConfig.COMPAT_VERSION)
            put("extensions", store.installed().size)
            put("sources", SourceRegistry.all().size)
            put("java", System.getProperty("java.version"))
        }

    private fun extensionJson(extension: ExtensionStore.InstalledExtension): JsonElement = json.encodeToJsonElement(ExtensionStore.InstalledExtension.serializer(), extension)

    private fun sourceJson(entry: SourceRegistry.Entry): JsonObject =
        buildJsonObject {
            val source = entry.source
            put("id", source.id.toULong().toString())
            put("name", source.name)
            put("lang", source.lang)
            put("pkg", entry.pkg)
            put("content_warning", entry.contentWarning)
            put("base_url", (source as? HttpSource)?.baseUrl)
            put("supports_latest", (source as? eu.kanade.tachiyomi.source.CatalogueSource)?.supportsLatest ?: false)
            put("configurable", source is ConfigurableSource)
            put("version_id", (source as? HttpSource)?.versionId)
        }

    private fun pageJson(page: MangasPage): JsonObject =
        buildJsonObject {
            put("mangas", JsonArray(page.mangas.map { mangaJson(it) }))
            put("has_next_page", page.hasNextPage)
        }

    private fun mangaJson(manga: SManga): JsonObject =
        buildJsonObject {
            put("url", manga.url)
            put("title", manga.title)
            put("artist", manga.artist)
            put("author", manga.author)
            put("description", manga.description)
            put("genre", manga.genre)
            put("status", manga.status)
            put("thumbnail_url", manga.thumbnail_url)
            put("initialized", manga.initialized)
        }

    private fun mangaFromJson(body: JsonObject): SManga =
        SManga.create().apply {
            url = body["url"]?.jsonPrimitive?.contentOrNull ?: throw IllegalArgumentException("Send the manga url")
            title = body["title"]?.jsonPrimitive?.contentOrNull ?: ""
            thumbnail_url = body["thumbnail_url"]?.jsonPrimitive?.contentOrNull
        }

    private fun chapterJson(
        chapter: SChapter,
        index: Int,
    ): JsonObject =
        buildJsonObject {
            put("index", index)
            put("url", chapter.url)
            put("name", chapter.name)
            put("date_upload", chapter.date_upload)
            put("chapter_number", chapter.chapter_number.toDouble())
            put("scanlator", chapter.scanlator)
        }

    private fun pageJson(page: Page): JsonObject =
        buildJsonObject {
            put("index", page.index)
            put("url", page.url)
            put("image_url", page.imageUrl)
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
            put("value_type", pref.defaultValueType)
            put("default_value", toJson(pref.defaultValue))
            put("current_value", toJson(pref.currentValue))
            when (pref) {
                is ListPreference -> {
                    put("entries", JsonArray(pref.entries.orEmpty().map { JsonPrimitive(it.toString()) }))
                    put("entry_values", JsonArray(pref.entryValues.orEmpty().map { JsonPrimitive(it.toString()) }))
                }
                is MultiSelectListPreference -> {
                    put("entries", JsonArray(pref.entries.orEmpty().map { JsonPrimitive(it.toString()) }))
                    put("entry_values", JsonArray(pref.entryValues.orEmpty().map { JsonPrimitive(it.toString()) }))
                }
                is EditTextPreference, is CheckBoxPreference, is TwoStatePreference -> Unit
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

    private fun parseId(text: String): Long = text.toULongOrNull()?.toLong() ?: throw IllegalArgumentException("Source ids are unsigned integers")

    private fun body(exchange: HttpExchange): JsonObject {
        val bytes = exchange.requestBody.readNBytes(1024 * 1024 + 1)
        if (bytes.size > 1024 * 1024) throw IllegalArgumentException("The request body is larger than 1 MiB")
        if (bytes.isEmpty()) return JsonObject(emptyMap())
        return json.parseToJsonElement(String(bytes, StandardCharsets.UTF_8)).jsonObject
    }

    private fun query(raw: String?): Map<String, String> =
        raw.orEmpty().split('&').filter { it.isNotEmpty() }.associate { part ->
            val name = part.substringBefore('=')
            val value = part.substringAfter('=', "")
            URLDecoder.decode(name, StandardCharsets.UTF_8) to URLDecoder.decode(value, StandardCharsets.UTF_8)
        }

    private fun reply(
        exchange: HttpExchange,
        status: Int,
        body: JsonElement,
    ) {
        val bytes = json.encodeToString(JsonElement.serializer(), body).toByteArray(StandardCharsets.UTF_8)
        exchange.responseHeaders.set("Content-Type", "application/json; charset=utf-8")
        exchange.sendResponseHeaders(status, bytes.size.toLong())
        exchange.responseBody.write(bytes)
    }

    private fun sameToken(supplied: String): Boolean =
        MessageDigest.isEqual(supplied.toByteArray(StandardCharsets.UTF_8), HostConfig.token.toByteArray(StandardCharsets.UTF_8))
}
