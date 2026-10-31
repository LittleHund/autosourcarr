package eu.kanade.tachiyomi.extension.all.autosourcarr

import eu.kanade.tachiyomi.network.GET
import eu.kanade.tachiyomi.source.UnmeteredSource
import eu.kanade.tachiyomi.source.model.FilterList
import eu.kanade.tachiyomi.source.model.MangasPage
import eu.kanade.tachiyomi.source.model.Page
import eu.kanade.tachiyomi.source.model.SChapter
import eu.kanade.tachiyomi.source.model.SManga
import eu.kanade.tachiyomi.source.online.HttpSource
import keiyoushi.annotation.Source
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.Headers
import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import java.io.IOException
import java.io.InterruptedIOException

@Source
abstract class AutoSourcarr : HttpSource(), UnmeteredSource {

    override val supportsLatest = true

    // The Android artifact uses AutoSourcarr's dedicated reader endpoint and API
    // key. The Suwayomi JAR is built with the ordinary /api/v1 prefix and no key.
    override fun headersBuilder(): Headers.Builder = super.headersBuilder().apply {
        if (AUTOSOURCARR_READER_API_KEY.isNotEmpty()) {
            set("X-AutoSourcarr-API-Key", AUTOSOURCARR_READER_API_KEY)
        }
    }

    // A network interceptor also runs on redirects. Never forward our custom
    // reader credential to an external image host or a different scheme/port.
    override val client: OkHttpClient = network.client.newBuilder()
        // HttpSource rejects non-2xx responses before calling our parser. Handle
        // the bounded catalogue-preparation protocol at the transport boundary.
        .addInterceptor(ReaderPreparingInterceptor({ baseUrl }, AUTOSOURCARR_API_PREFIX))
        .addNetworkInterceptor { chain ->
            val request = chain.request()
            val origin = baseUrl.toHttpUrl()
            val target = request.url
            val sameOrigin = target.scheme == origin.scheme &&
                target.host == origin.host && target.port == origin.port
            val scoped = if (sameOrigin) request else request.newBuilder()
                .removeHeader("X-AutoSourcarr-API-Key")
                .build()
            chain.proceed(scoped)
        }
        .build()

    private fun endpoint(path: String): String = "$baseUrl$AUTOSOURCARR_API_PREFIX$path"

    override fun popularMangaRequest(page: Int): Request =
        GET("${endpoint("/browse/popular")}?page=$page", headers)

    override fun popularMangaParse(response: Response): MangasPage = parseBrowse(response)

    override fun latestUpdatesRequest(page: Int): Request =
        GET("${endpoint("/browse/latest")}?page=$page", headers)

    override fun latestUpdatesParse(response: Response): MangasPage = parseBrowse(response)

    override fun searchMangaRequest(page: Int, query: String, filters: FilterList): Request {
        var sourceIds: Set<String> = emptySet()
        var languageSourceIds: Set<String>? = null
        var exactMatchCount: Int? = null
        var sort = "relevance"

        filters.forEach { filter ->
            when (filter) {
                is SourceFilter -> sourceIds = filter.sourceIds()
                is LanguageFilter -> languageSourceIds = filter.sourceIds()
                is MatchCountFilter -> exactMatchCount = filter.exact()
                is AutoSourcarrSortFilter -> sort = filter.value()
                else -> {}
            }
        }

        val selectedLanguages = languageSourceIds
        if (selectedLanguages != null && selectedLanguages.isEmpty()) {
            return GET("${endpoint("/search")}?q=__autosourcarr_no_enabled_language__&page=1&match_count=999999", headers)
        }

        val selectedSourceIds = when {
            selectedLanguages == null -> sourceIds
            sourceIds.isEmpty() -> selectedLanguages
            else -> sourceIds intersect selectedLanguages
        }
        if (sourceIds.isNotEmpty() && selectedLanguages != null && selectedSourceIds.isEmpty()) {
            return GET("${endpoint("/search")}?q=__autosourcarr_no_matching_source__&page=1&match_count=999999", headers)
        }

        val url = endpoint("/search").toHttpUrl().newBuilder()
            .addQueryParameter("q", query)
            .addQueryParameter("page", page.toString())
            .addQueryParameter("min_matches", "0")
            .addQueryParameter("sort", sort)
            .apply { exactMatchCount?.let { addQueryParameter("match_count", it.toString()) } }
            .apply { selectedSourceIds.forEach { addQueryParameter("sources", it) } }
            .build()
        return GET(url, headers)
    }

    override fun searchMangaParse(response: Response): MangasPage = parseBrowse(response)

    override fun mangaDetailsRequest(manga: SManga): Request =
        GET("${endpoint("/entries/${manga.url}/refresh")}?fetch_details=true&fetch_chapters=false", headers)

    override fun mangaDetailsParse(response: Response): SManga =
        parseRefreshDto(readerJsonBody(response)).entry.toSManga(baseUrl)

    override fun chapterListRequest(manga: SManga): Request =
        GET("${endpoint("/entries/${manga.url}/refresh")}?fetch_details=false&fetch_chapters=true", headers)

    override fun chapterListParse(response: Response): List<SChapter> =
        parseRefreshDto(readerJsonBody(response)).chapters.map { it.toSChapter() }

    override fun pageListRequest(chapter: SChapter): Request =
        GET(endpoint("/chapters/${chapter.url}/pages"), headers)

    override fun pageListParse(response: Response): List<Page> =
        parsePagesDto(readerJsonBody(response)).pages.mapIndexed { index, url -> Page(index, imageUrl = absolute(baseUrl, url)) }

    // Keep the reader API key on page-image fetches as well as JSON requests.
    override fun imageRequest(page: Page): Request = GET(requireNotNull(page.imageUrl), headers)

    override fun imageUrlParse(response: Response): String = throw UnsupportedOperationException()

    override fun getMangaUrl(manga: SManga): String = "$baseUrl/api/v1/entries/${manga.url}"

    override fun getChapterUrl(chapter: SChapter): String = "$baseUrl/api/v1/chapters/${chapter.url}"

    private fun parseBrowse(response: Response): MangasPage {
        val dto = parseBrowseDto(readerJsonBody(response))
        return MangasPage(dto.items.map { it.toSManga(baseUrl) }, dto.hasNextPage)
    }

    private fun readerJsonBody(response: Response): String {
        if (!response.isSuccessful) {
            if (response.code == 503 && response.header("X-AutoSourcarr-Reader-State") == "preparing") {
                throw IOException("AutoSourcarr catalogue is busy or preparing this page. Retry shortly; requests already in progress are reused.")
            }
            throw IOException("AutoSourcarr reader API returned HTTP ${response.code}")
        }
        val text = response.body.string()
        val trimmed = text.trimStart()
        val looksHtml = trimmed.startsWith("<!doctype html", ignoreCase = true) ||
            trimmed.startsWith("<html", ignoreCase = true) ||
            (trimmed.startsWith("<") && text.contains("authelia", ignoreCase = true))
        if (looksHtml) {
            throw IOException(
                "AutoSourcarr reader API was intercepted by Authelia. " +
                    "Bypass only /api/reader/v1/ in Authelia; AutoSourcarr still validates its API key.",
            )
        }
        if (!trimmed.startsWith("{")) {
            throw IOException("AutoSourcarr reader API returned a non-JSON response")
        }
        return text
    }

    override fun getFilterList(): FilterList {
        val items = loadFilterSources()
            .filter { it.enabledLanguage && it.enabledSearch }

        val sourceChoices = items
            .groupBy { it.name to it.baseUrl }
            .map { (family, variants) ->
                SourceChoice(family.first, variants.map { it.sourceId }.toSet())
            }
            .sortedBy { it.label.lowercase() }

        val languageChoices = items
            .groupBy { it.lang.ifBlank { "unknown" } }
            .map { (lang, variants) ->
                LanguageCheckBox(
                    code = lang,
                    sourceIds = variants.map { it.sourceId }.toSet(),
                    label = languageLabel(lang),
                )
            }
            .sortedBy { it.code }

        return FilterList(
            SourceFilter(listOf(SourceChoice("All enabled sources", emptySet())) + sourceChoices),
            LanguageFilter(languageChoices),
            MatchCountFilter(sourceChoices.size),
            AutoSourcarrSortFilter(),
        )
    }

    private fun loadFilterSources(): List<SourceDto> {
        val now = System.currentTimeMillis()
        val cached = cachedFilterSources
        if (cached.isNotEmpty() && now - filterSourcesLoadedAt < FILTER_SOURCE_TTL_MS) {
            return cached
        }

        return try {
            val items = client.newCall(GET(endpoint("/sources"), headers)).execute().use { response ->
                if (!response.isSuccessful) throw Exception("Source list HTTP ${response.code}")
                parseSourcesDto(readerJsonBody(response)).items
            }
            cachedFilterSources = items
            filterSourcesLoadedAt = now
            items
        } catch (_: Exception) {
            cachedFilterSources
        }
    }

    private fun languageLabel(code: String): String = when (code.lowercase()) {
        "all" -> "All / multi-language"
        "en" -> "English"
        "ja" -> "Japanese"
        "ko" -> "Korean"
        "zh" -> "Chinese"
        "ru" -> "Russian"
        "vi" -> "Vietnamese"
        "fr" -> "French"
        "pl" -> "Polish"
        "id" -> "Indonesian"
        "tr" -> "Turkish"
        "it" -> "Italian"
        "es" -> "Spanish"
        "uk" -> "Ukrainian"
        "de" -> "German"
        "th" -> "Thai"
        "ro" -> "Romanian"
        "ms" -> "Malay"
        "sv" -> "Swedish"
        "no" -> "Norwegian"
        "nl" -> "Dutch"
        "pt" -> "Portuguese"
        "pt-br" -> "Portuguese (Brazil)"
        else -> code.ifBlank { "Unspecified" }
    }

    companion object {
        private const val FILTER_SOURCE_TTL_MS = 60_000L

        @Volatile
        private var cachedFilterSources: List<SourceDto> = emptyList()

        @Volatile
        private var filterSourcesLoadedAt: Long = 0L
    }
}

/** Only poll our own in-progress catalogue job; never retry website failures. */
internal class ReaderPreparingInterceptor(
    private val baseUrl: () -> String,
    private val apiPrefix: String,
) : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response {
        val request = chain.request()
        val origin = baseUrl().toHttpUrl()
        val target = request.url
        val pathPrefix = origin.encodedPath.trimEnd('/') + apiPrefix.trimEnd('/')
        val eligible = request.method == "GET" &&
            target.scheme == origin.scheme && target.host == origin.host && target.port == origin.port &&
            target.encodedPath in setOf(
                "$pathPrefix/browse/popular", "$pathPrefix/browse/latest", "$pathPrefix/search",
            )
        var followups = 0
        while (true) {
            checkCancelled(chain)
            val response = chain.proceed(request)
            // A redirect to another URL must not be mistaken for the same job.
            // Unmarked 503s, CAPTCHA/WAF responses and non-reader paths pass through.
            if (!eligible || response.request.url != request.url || response.code != 503 ||
                response.header("X-AutoSourcarr-Reader-State") != "preparing"
            ) {
                return response
            }
            val delaySeconds = response.header("Retry-After")?.trim()?.toLongOrNull()?.coerceIn(1L, 3L) ?: 3L
            response.close()
            checkCancelled(chain)
            if (followups >= 2) {
                throw IOException(
                    "AutoSourcarr is still preparing this catalogue page. " +
                        "Try again shortly; its existing background request is reused.",
                )
            }
            followups += 1
            val deadline = System.nanoTime() + delaySeconds * 1_000_000_000L
            while (System.nanoTime() < deadline) {
                checkCancelled(chain)
                val remainingMillis = ((deadline - System.nanoTime()) / 1_000_000L).coerceAtLeast(1L)
                try {
                    Thread.sleep(minOf(remainingMillis, 100L))
                } catch (_: InterruptedException) {
                    Thread.currentThread().interrupt()
                    throw InterruptedIOException("AutoSourcarr catalogue request was interrupted")
                }
            }
        }
    }

    private fun checkCancelled(chain: Interceptor.Chain) {
        if (chain.call().isCanceled() || Thread.currentThread().isInterrupted) {
            throw InterruptedIOException("AutoSourcarr catalogue request was cancelled")
        }
    }
}
