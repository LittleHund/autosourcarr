package autosourcarr.host

import eu.kanade.tachiyomi.network.NetworkHelper
import io.github.oshai.kotlinlogging.KotlinLogging
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.longOrNull
import kotlinx.serialization.json.put
import okhttp3.Cookie
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import uy.kohesive.injekt.Injekt
import uy.kohesive.injekt.api.get
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit

/**
 * The Cloudflare fallback after the built-in WebView (CON-2): FlareSolverr-compatible solvers (FlareSolverr, Byparr;
 * both answer `POST /v1 {"cmd":"request.get"}`). Cookies only: the solver's cookies go into the host cookie jar and
 * its user agent is remembered for that website host; the solver's page body is never used as page content. The
 * request is then retried by the caller with that user agent. A clearance is cached per host until its cf_clearance
 * cookie expires (30 minutes when the solver gives no expiry), so other requests to the host reuse the user agent
 * without asking the solver again. One solve per host at a time; callers for the same host share it.
 */
object ExternalSolvers {
    private val logger = KotlinLogging.logger {}
    private val json = Json { ignoreUnknownKeys = true }
    private val jsonType = "application/json".toMediaType()
    private const val CACHE_WITHOUT_EXPIRY_MS = 30 * 60 * 1000L

    data class Endpoint(
        /** Base URL without a trailing slash or /v1, e.g. http://flaresolverr:8191. */
        val url: String,
        val timeoutMs: Long,
        /** "connection" (AUTOSOURCARR_SOLVERS) or "legacy" (FLARESOLVERR_URL). */
        val origin: String,
    ) {
        /** Scheme, host and port only: what status pages may show. */
        val display: String get() = url.toHttpUrlOrNull()?.let { "${it.scheme}://${it.host}:${it.port}" } ?: "invalid"
    }

    data class Clearance(
        val userAgent: String,
        val expiresAt: Long,
        val solver: String,
    )

    private val cache = ConcurrentHashMap<String, Clearance>()
    private val inflight = ConcurrentHashMap<String, CompletableFuture<Clearance?>>()
    private val baseClient by lazy {
        // Not the source client: no Cloudflare interceptor (no recursion), no cookie jar (the solver gets no site cookies).
        OkHttpClient.Builder().connectTimeout(10, TimeUnit.SECONDS).followRedirects(false).build()
    }

    fun endpoint(
        raw: String,
        timeoutMs: Long,
        origin: String,
    ): Endpoint? {
        val url = raw.trim().removeSuffix("/").removeSuffix("/v1").removeSuffix("/").toHttpUrlOrNull() ?: return null
        if (url.scheme !in setOf("http", "https") || url.username.isNotEmpty() || url.password.isNotEmpty() || url.query != null) return null
        return Endpoint(url.toString().removeSuffix("/"), timeoutMs.coerceIn(10_000L, 300_000L), origin)
    }

    /** AUTOSOURCARR_SOLVERS: a JSON array of {url, timeoutMs}; bad entries are skipped with a warning. */
    fun parse(raw: String): List<Endpoint> {
        if (raw.isBlank()) return emptyList()
        val array = runCatching { json.parseToJsonElement(raw.trim()) as? JsonArray }.getOrNull()
        if (array == null) {
            logger.warn { "AUTOSOURCARR_SOLVERS is not a JSON array; no external solvers" }
            return emptyList()
        }
        return array.mapNotNull { item ->
            val row = item as? JsonObject
            val url = (row?.get("url") as? JsonPrimitive)?.contentOrNull
            val timeout = (row?.get("timeoutMs") as? JsonPrimitive)?.longOrNull ?: 60_000L
            url?.let { endpoint(it, timeout, "connection") }.also { if (it == null) logger.warn { "Skipped an invalid AUTOSOURCARR_SOLVERS entry" } }
        }
    }

    fun configured(): List<Endpoint> = HostConfig.externalSolvers

    /** The user agent a cached clearance for this host belongs to, or null (none, or expired). */
    fun userAgentFor(host: String): String? {
        val entry = cache[host] ?: return null
        if (entry.expiresAt > System.currentTimeMillis()) return entry.userAgent
        cache.remove(host, entry)
        return null
    }

    fun invalidate(host: String) {
        cache.remove(host)
    }

    fun clear() = cache.clear()

    fun cached(): Map<String, Clearance> = cache.filterValues { it.expiresAt > System.currentTimeMillis() }

    /** Asks the configured solvers in order; null when none is configured or none produced a cf_clearance cookie. */
    fun solve(url: HttpUrl): Clearance? {
        val solvers = configured()
        if (solvers.isEmpty()) return null
        val mine = CompletableFuture<Clearance?>()
        val existing = inflight.putIfAbsent(url.host, mine)
        if (existing != null) {
            val wait = solvers.sumOf { it.timeoutMs } + 15_000L
            return runCatching { existing.get(wait, TimeUnit.MILLISECONDS) }.getOrNull()
        }
        try {
            var result: Clearance? = null
            for (solver in solvers) {
                result = runCatching { ask(solver, url) }
                    .onFailure { logger.warn { "Solver ${solver.display} failed for ${url.host}: ${it.message ?: it.javaClass.simpleName}" } }
                    .getOrNull()
                if (result != null) break
            }
            if (result != null) cache[url.host] = result
            mine.complete(result)
            return result
        } catch (error: Throwable) {
            mine.complete(null)
            throw error
        } finally {
            inflight.remove(url.host, mine)
        }
    }

    private fun ask(
        solver: Endpoint,
        url: HttpUrl,
    ): Clearance? {
        val body =
            buildJsonObject {
                put("cmd", "request.get")
                put("url", url.toString())
                put("maxTimeout", solver.timeoutMs)
                // FlareSolverr then leaves the page body out; Byparr ignores it. The body is not used either way.
                put("returnOnlyCookies", true)
            }.toString()
        val client =
            baseClient
                .newBuilder()
                .callTimeout(solver.timeoutMs + 15_000L, TimeUnit.MILLISECONDS)
                .readTimeout(solver.timeoutMs + 10_000L, TimeUnit.MILLISECONDS)
                .build()
        val request = Request.Builder().url(solver.url + "/v1").post(body.toRequestBody(jsonType)).build()
        val answer =
            client.newCall(request).execute().use { response ->
                // A solver answers about 1 KB with returnOnlyCookies; read at most 4 MiB in case it sends the page.
                val text = String(response.body.byteStream().readNBytes(4 * 1024 * 1024), Charsets.UTF_8)
                if (!response.isSuccessful) throw IllegalStateException("HTTP ${response.code}")
                json.parseToJsonElement(text).jsonObject
            }
        if ((answer["status"] as? JsonPrimitive)?.contentOrNull != "ok") {
            throw IllegalStateException((answer["message"] as? JsonPrimitive)?.contentOrNull ?: "status is not ok")
        }
        val solution = answer["solution"]?.jsonObject ?: throw IllegalStateException("no solution")
        val agent = solution["userAgent"]?.jsonPrimitive?.contentOrNull?.takeIf { it.isNotBlank() } ?: throw IllegalStateException("no userAgent")
        val cookies = (solution["cookies"] as? JsonArray).orEmpty().mapNotNull { cookie(it as? JsonObject, url) }
        val clearance = cookies.firstOrNull { it.name == "cf_clearance" }
        if (clearance == null) {
            logger.warn { "Solver ${solver.display} answered for ${url.host} without a cf_clearance cookie" }
            return null
        }
        val network = Injekt.get<NetworkHelper>()
        cookies.groupBy { it.domain }.forEach { (domain, group) ->
            network.cookieStore.addAll(HttpUrl.Builder().scheme(url.scheme).host(domain).build(), group)
        }
        val now = System.currentTimeMillis()
        val expires = if (clearance.persistent && clearance.expiresAt > now) clearance.expiresAt else now + CACHE_WITHOUT_EXPIRY_MS
        logger.info { "Solver ${solver.display} passed the Cloudflare check for ${url.host}" }
        return Clearance(agent, expires, solver.display)
    }

    /** FlareSolverr's cookie shape (also Byparr's): name, value, domain, path, expires (seconds), httpOnly, secure. */
    private fun cookie(
        row: JsonObject?,
        url: HttpUrl,
    ): Cookie? {
        row ?: return null
        val name = row["name"]?.jsonPrimitive?.contentOrNull ?: return null
        val value = row["value"]?.jsonPrimitive?.contentOrNull ?: return null
        val rawDomain = row["domain"]?.jsonPrimitive?.contentOrNull?.takeIf { it.isNotBlank() } ?: url.host
        val domain = rawDomain.removePrefix(".")
        // Only cookies for the website asked about (or its parent domains), never for other sites.
        if (url.host != domain && !url.host.endsWith(".$domain")) return null
        return runCatching {
            Cookie
                .Builder()
                .name(name)
                .value(value)
                .apply {
                    if (rawDomain.startsWith(".")) domain(domain) else hostOnlyDomain(domain)
                    row["path"]?.jsonPrimitive?.contentOrNull?.takeIf { it.startsWith("/") }?.let { path(it) }
                    row["expires"]?.jsonPrimitive?.doubleOrNull?.takeIf { it > 0 }?.let { expiresAt((it * 1000).toLong()) }
                    if (row["httpOnly"]?.jsonPrimitive?.booleanOrNull == true) httpOnly()
                    if (row["secure"]?.jsonPrimitive?.booleanOrNull == true) secure()
                }.build()
        }.getOrNull()
    }
}
