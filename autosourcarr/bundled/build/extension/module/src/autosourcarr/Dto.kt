package eu.kanade.tachiyomi.extension.all.autosourcarr

import eu.kanade.tachiyomi.source.model.SChapter
import eu.kanade.tachiyomi.source.model.SManga

class BrowseDto(
    val items: List<EntryDto> = emptyList(),
    val hasNextPage: Boolean = false,
)

class EntryDto(
    val id: Int,
    val preferredTitle: String,
    val description: String = "",
    val descriptionForExtension: String = "",
    val coverUrl: String = "",
    val author: String = "",
    val artist: String = "",
    val genre: String = "",
    val status: Int = 0,
    val matchCount: Int = 1,
    val metadataComplete: Boolean = true,
) {
    fun toSManga(baseUrl: String, apiPrefix: String = AUTOSOURCARR_API_PREFIX) = SManga.create().apply {
        url = id.toString()
        title = preferredTitle
        thumbnail_url = absolute(baseUrl, coverUrl, apiPrefix)
        val renderedDescription = descriptionForExtension.ifEmpty { this@EntryDto.description }
        if (renderedDescription.isNotEmpty()) {
            description = renderedDescription
        }
        author = this@EntryDto.author
        artist = this@EntryDto.artist
        genre = this@EntryDto.genre
        status = this@EntryDto.status
        // Complete cached cards stay fast. Sparse cards deliberately remain
        // uninitialized so Suwayomi requests manga details and AutoSourcarr gets
        // another chance to hydrate cover/summary/creator/status data.
        initialized = metadataComplete
    }
}

class RefreshDto(
    val entry: EntryDto,
    val chapters: List<ChapterDto> = emptyList(),
)

class ChapterDto(
    val id: Int,
    val displayName: String,
    val chapterNumber: Float = -1f,
    val uploadDate: Long? = 0,
) {
    fun toSChapter() = SChapter.create().apply {
        url = id.toString()
        name = displayName
        chapter_number = chapterNumber
        date_upload = uploadDate ?: 0L
    }
}

class PagesDto(
    val pages: List<String> = emptyList(),
)

class SourcesDto(
    val items: List<SourceDto> = emptyList(),
)

class SourceDto(
    val sourceId: String,
    val name: String,
    val lang: String = "",
    val baseUrl: String = "",
    val enabledLanguage: Boolean = true,
    val enabledSearch: Boolean = true,
)

private fun Map<String, Any?>.toEntryDtoOrNull(): EntryDto? {
    val id = jsonInt("id", -1)
    val title = jsonString("preferred_title")
    if (id < 0 || title.isBlank()) return null
    return EntryDto(
        id = id,
        preferredTitle = title,
        description = jsonString("description"),
        descriptionForExtension = jsonString("description_for_extension"),
        coverUrl = jsonString("cover_url"),
        author = jsonString("author"),
        artist = jsonString("artist"),
        genre = jsonString("genre"),
        status = jsonInt("status"),
        matchCount = jsonInt("match_count", 1),
        metadataComplete = jsonBoolean("metadata_complete", true),
    )
}

private fun Map<String, Any?>.toChapterDtoOrNull(): ChapterDto? {
    val id = jsonInt("id", -1)
    val displayName = jsonString("display_name")
    if (id < 0 || displayName.isBlank()) return null
    return ChapterDto(
        id = id,
        displayName = displayName,
        chapterNumber = jsonFloat("chapter_number", -1f),
        uploadDate = jsonLongOrNull("upload_date"),
    )
}

private fun Map<String, Any?>.toSourceDtoOrNull(): SourceDto? {
    val id = jsonString("source_id")
    val name = jsonString("name")
    if (id.isBlank() || name.isBlank()) return null
    return SourceDto(
        sourceId = id,
        name = name,
        lang = jsonString("lang"),
        baseUrl = jsonString("base_url"),
        enabledLanguage = jsonBoolean("enabled_language", true),
        enabledSearch = jsonBoolean("enabled_search", true),
    )
}

fun parseBrowseDto(text: String): BrowseDto {
    val root = MiniJson.parseObject(text)
    return BrowseDto(
        items = root["items"].jsonArrayOrEmpty().mapNotNull { it.jsonObjectOrNull()?.toEntryDtoOrNull() },
        hasNextPage = root.jsonBoolean("has_next_page"),
    )
}

fun parseRefreshDto(text: String): RefreshDto {
    val root = MiniJson.parseObject(text)
    val entry = root["entry"].jsonObjectOrNull()?.toEntryDtoOrNull()
        ?: throw IllegalArgumentException("AutoSourcarr refresh response is missing entry metadata")
    return RefreshDto(
        entry = entry,
        chapters = root["chapters"].jsonArrayOrEmpty().mapNotNull { it.jsonObjectOrNull()?.toChapterDtoOrNull() },
    )
}

fun parsePagesDto(text: String): PagesDto {
    val root = MiniJson.parseObject(text)
    return PagesDto(root["pages"].jsonArrayOrEmpty().mapNotNull { it as? String })
}

fun parseSourcesDto(text: String): SourcesDto {
    val root = MiniJson.parseObject(text)
    return SourcesDto(root["items"].jsonArrayOrEmpty().mapNotNull { it.jsonObjectOrNull()?.toSourceDtoOrNull() })
}

// Each server slot picks its own prefix: /api/reader/v1 when a reader key is set, /api/v1 otherwise.
fun absolute(baseUrl: String, value: String, apiPrefix: String = AUTOSOURCARR_API_PREFIX): String {
    val routed = if (apiPrefix != "/api/v1" && value.startsWith("/api/v1/")) {
        apiPrefix + value.removePrefix("/api/v1")
    } else {
        value
    }
    return when {
        routed.startsWith("https://") || routed.startsWith("http://") -> routed
        routed.startsWith("/") -> baseUrl + routed
        routed.isEmpty() -> ""
        else -> "$baseUrl/$routed"
    }
}
