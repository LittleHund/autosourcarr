package autosourcarr.host

import eu.kanade.tachiyomi.source.model.SChapter
import eu.kanade.tachiyomi.source.model.SManga
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import java.io.File
import java.sql.Connection
import java.sql.DriverManager
import java.sql.ResultSet

/**
 * The host's only persistent state besides extensions: stable numeric ids for (source, url) of every title and
 * chapter it has served, their last known fields, the library flag and the page list of each chapter.
 * AutoSourcarr's catalogue keeps these ids exactly as it kept Suwayomi's (an import keeps the old Suwayomi ids).
 */
class HostDb(
    file: File,
) {
    private val connection: Connection = DriverManager.getConnection("jdbc:sqlite:${file.absolutePath}")
    private val gate = Any()

    init {
        connection.createStatement().use { statement ->
            statement.executeUpdate("PRAGMA journal_mode=WAL")
            statement.executeUpdate("PRAGMA busy_timeout=5000")
            statement.executeUpdate(
                """
                CREATE TABLE IF NOT EXISTS manga (
                  id INTEGER PRIMARY KEY AUTOINCREMENT, source_id TEXT NOT NULL, url TEXT NOT NULL,
                  title TEXT NOT NULL DEFAULT '', thumbnail_url TEXT, artist TEXT, author TEXT, description TEXT, genre TEXT,
                  status INTEGER NOT NULL DEFAULT 0, initialized INTEGER NOT NULL DEFAULT 0, in_library INTEGER NOT NULL DEFAULT 0,
                  real_url TEXT, updated_at INTEGER NOT NULL DEFAULT 0, UNIQUE(source_id, url))
                """.trimIndent(),
            )
            statement.executeUpdate(
                """
                CREATE TABLE IF NOT EXISTS chapter (
                  id INTEGER PRIMARY KEY AUTOINCREMENT, manga_id INTEGER NOT NULL REFERENCES manga(id) ON DELETE CASCADE,
                  url TEXT NOT NULL, name TEXT NOT NULL DEFAULT '', upload_date INTEGER NOT NULL DEFAULT 0,
                  chapter_number REAL NOT NULL DEFAULT -1, scanlator TEXT, source_order INTEGER NOT NULL DEFAULT 0,
                  real_url TEXT, page_count INTEGER NOT NULL DEFAULT -1, present INTEGER NOT NULL DEFAULT 1,
                  pages_json TEXT, UNIQUE(manga_id, url))
                """.trimIndent(),
            )
            statement.executeUpdate("CREATE INDEX IF NOT EXISTS chapter_order ON chapter(manga_id, source_order)")
        }
        // tachiyomix 1.6 memo: source-private JSON a source sets on a title or chapter and reads back later
        // (e.g. Asura Scans and Mangago keep their reader URLs there). Added to databases made before it.
        for (table in listOf("manga", "chapter")) {
            val columns =
                connection.createStatement().use { st ->
                    st.executeQuery("PRAGMA table_info($table)").use { rows -> generateSequence { if (rows.next()) rows.getString("name") else null }.toSet() }
                }
            if ("memo" !in columns) connection.createStatement().use { it.executeUpdate("ALTER TABLE $table ADD COLUMN memo TEXT") }
        }
    }

    private fun memoText(value: JsonObject?): String? = value?.takeIf { it.isNotEmpty() }?.toString()

    private fun memoOf(text: String?): JsonObject = text?.let { runCatching { Json.parseToJsonElement(it) as? JsonObject }.getOrNull() } ?: JsonObject(emptyMap())

    data class Manga(
        val id: Long,
        val sourceId: String,
        val url: String,
        val title: String,
        val thumbnailUrl: String?,
        val artist: String?,
        val author: String?,
        val description: String?,
        val genre: String?,
        val status: Int,
        val initialized: Boolean,
        val inLibrary: Boolean,
        val realUrl: String?,
        val memo: JsonObject = JsonObject(emptyMap()),
    )

    data class Chapter(
        val id: Long,
        val mangaId: Long,
        val url: String,
        val name: String,
        val uploadDate: Long,
        val chapterNumber: Double,
        val scanlator: String?,
        val sourceOrder: Int,
        val realUrl: String?,
        val pageCount: Int,
        val memo: JsonObject = JsonObject(emptyMap()),
    )

    /** Records a title seen in a browse/search list; details already known are kept. */
    fun upsertListed(
        sourceId: String,
        manga: SManga,
    ): Manga =
        synchronized(gate) {
            connection
                .prepareStatement(
                    """
                    INSERT INTO manga(source_id, url, title, thumbnail_url, updated_at, memo) VALUES(?,?,?,?,?,?)
                    ON CONFLICT(source_id, url) DO UPDATE SET title=excluded.title,
                      thumbnail_url=COALESCE(excluded.thumbnail_url, manga.thumbnail_url), memo=COALESCE(excluded.memo, manga.memo)
                    WHERE (manga.title, manga.thumbnail_url, manga.memo) IS NOT
                      (excluded.title, COALESCE(excluded.thumbnail_url, manga.thumbnail_url), COALESCE(excluded.memo, manga.memo))
                    """.trimIndent(),
                ).use {
                    it.setString(1, sourceId)
                    it.setString(2, manga.url)
                    it.setString(3, manga.title)
                    it.setString(4, manga.thumbnail_url)
                    it.setLong(5, System.currentTimeMillis())
                    it.setString(6, memoText(manga.memo))
                    it.executeUpdate()
                }
            mangaBy("source_id=? AND url=?", sourceId, manga.url)!!
        }

    fun updateDetails(
        id: Long,
        manga: SManga,
        realUrl: String?,
    ): Manga =
        synchronized(gate) {
            connection
                .prepareStatement(
                    """
                    UPDATE manga SET title=COALESCE(NULLIF(?, ''), title), thumbnail_url=COALESCE(?, thumbnail_url), artist=?, author=?,
                      description=?, genre=?, status=?, initialized=1, real_url=?, updated_at=?, memo=COALESCE(?, memo) WHERE id=?
                    """.trimIndent(),
                ).use {
                    // Many sources leave title unset in details (it is lateinit): keep the listed title then.
                    it.setString(1, runCatching { manga.title }.getOrNull())
                    it.setString(2, manga.thumbnail_url)
                    it.setString(3, manga.artist)
                    it.setString(4, manga.author)
                    it.setString(5, manga.description)
                    it.setString(6, manga.genre)
                    it.setInt(7, manga.status)
                    it.setString(8, realUrl)
                    it.setLong(9, System.currentTimeMillis())
                    it.setString(10, memoText(manga.memo))
                    it.setLong(11, id)
                    it.executeUpdate()
                }
            manga(id)!!
        }

    /**
     * Stores the chapter list in source order. Like Suwayomi, sourceOrder counts from the oldest chapter (1) up,
     * so the newest chapter has the highest order and existing ids stay when new chapters arrive.
     */
    fun replaceChapters(
        mangaId: Long,
        chapters: List<SChapter>,
        realUrl: (SChapter) -> String?,
    ): List<Chapter> =
        synchronized(gate) {
            connection.autoCommit = false
            try {
                connection.prepareStatement("UPDATE chapter SET present=0 WHERE manga_id=?").use {
                    it.setLong(1, mangaId)
                    it.executeUpdate()
                }
                connection
                    .prepareStatement(
                        """
                        INSERT INTO chapter(manga_id, url, name, upload_date, chapter_number, scanlator, source_order, real_url, present, memo)
                        VALUES(?,?,?,?,?,?,?,?,1,?)
                        ON CONFLICT(manga_id, url) DO UPDATE SET name=excluded.name, upload_date=excluded.upload_date,
                          chapter_number=excluded.chapter_number, scanlator=excluded.scanlator, source_order=excluded.source_order,
                          real_url=excluded.real_url, present=1, memo=excluded.memo
                        """.trimIndent(),
                    ).use { statement ->
                        chapters.forEachIndexed { index, chapter ->
                            statement.setLong(1, mangaId)
                            statement.setString(2, chapter.url)
                            statement.setString(3, chapter.name)
                            statement.setLong(4, chapter.date_upload)
                            statement.setDouble(5, chapter.chapter_number.toDouble())
                            statement.setString(6, chapter.scanlator)
                            statement.setInt(7, chapters.size - index)
                            statement.setString(8, realUrl(chapter))
                            statement.setString(9, memoText(chapter.memo))
                            statement.addBatch()
                        }
                        statement.executeBatch()
                    }
                connection.commit()
            } catch (error: Throwable) {
                connection.rollback()
                throw error
            } finally {
                connection.autoCommit = true
            }
            chapters(mangaId)
        }

    fun manga(id: Long): Manga? = synchronized(gate) { mangaBy("id=?", id) }

    fun library(
        first: Int,
        after: Long,
    ): List<Manga> =
        synchronized(gate) {
            connection.prepareStatement("SELECT * FROM manga WHERE in_library=1 AND id>? ORDER BY id LIMIT ?").use {
                it.setLong(1, after)
                it.setInt(2, first)
                it.executeQuery().use { rows -> generateSequence { if (rows.next()) mangaRow(rows) else null }.toList() }
            }
        }

    fun setLibrary(
        id: Long,
        inLibrary: Boolean,
    ): Manga? =
        synchronized(gate) {
            connection.prepareStatement("UPDATE manga SET in_library=? WHERE id=?").use {
                it.setInt(1, if (inLibrary) 1 else 0)
                it.setLong(2, id)
                it.executeUpdate()
            }
            mangaBy("id=?", id)
        }

    fun chapters(mangaId: Long): List<Chapter> =
        synchronized(gate) {
            connection.prepareStatement("SELECT * FROM chapter WHERE manga_id=? AND present=1 ORDER BY source_order DESC").use {
                it.setLong(1, mangaId)
                it.executeQuery().use { rows -> generateSequence { if (rows.next()) chapterRow(rows) else null }.toList() }
            }
        }

    fun chapter(id: Long): Chapter? =
        synchronized(gate) {
            connection.prepareStatement("SELECT * FROM chapter WHERE id=?").use {
                it.setLong(1, id)
                it.executeQuery().use { rows -> if (rows.next()) chapterRow(rows) else null }
            }
        }

    fun chapterByOrder(
        mangaId: Long,
        sourceOrder: Int,
    ): Chapter? =
        synchronized(gate) {
            connection.prepareStatement("SELECT * FROM chapter WHERE manga_id=? AND source_order=? AND present=1").use {
                it.setLong(1, mangaId)
                it.setInt(2, sourceOrder)
                it.executeQuery().use { rows -> if (rows.next()) chapterRow(rows) else null }
            }
        }

    fun savePages(
        chapterId: Long,
        pagesJson: String,
        count: Int,
    ) = synchronized(gate) {
        connection.prepareStatement("UPDATE chapter SET pages_json=?, page_count=? WHERE id=?").use {
            it.setString(1, pagesJson)
            it.setInt(2, count)
            it.setLong(3, chapterId)
            it.executeUpdate()
        }
    }

    fun pagesJson(chapterId: Long): String? =
        synchronized(gate) {
            connection.prepareStatement("SELECT pages_json FROM chapter WHERE id=?").use {
                it.setLong(1, chapterId)
                it.executeQuery().use { rows -> if (rows.next()) rows.getString(1) else null }
            }
        }

    /** Cutover from Suwayomi: keeps the ids the AutoSourcarr catalogue already stores. Existing rows are left alone. */
    /**
     * Cutover: takes the ids the catalogue already uses (from the main Suwayomi) so stored references stay valid.
     * A title or chapter the host has already seen under another id is moved to the imported id (its chapters and
     * pages follow). An imported id that already belongs to a different title or chapter is reported as a conflict.
     * Returns (titles, chapters, conflicts).
     */
    fun importIds(
        mangas: List<Triple<Long, String, String>>,
        chapters: List<Chapter>,
    ): Triple<Int, Int, Int> =
        synchronized(gate) {
            var mangaCount = 0
            var chapterCount = 0
            var conflicts = 0
            fun scalar(sql: String, vararg values: Any): Long? =
                connection.prepareStatement(sql).use { st ->
                    values.forEachIndexed { i, v -> st.setObject(i + 1, v) }
                    st.executeQuery().use { if (it.next()) it.getLong(1) else null }
                }
            fun exec(sql: String, vararg values: Any): Int =
                connection.prepareStatement(sql).use { st ->
                    values.forEachIndexed { i, v -> st.setObject(i + 1, v) }
                    st.executeUpdate()
                }
            connection.autoCommit = false
            try {
                for ((id, source, url) in mangas) {
                    val existing = scalar("SELECT id FROM manga WHERE source_id=? AND url=?", source, url)
                    val holder = scalar("SELECT id FROM manga WHERE id=?", id)
                    when {
                        existing == id -> Unit
                        holder != null -> conflicts++
                        existing != null -> {
                            exec("UPDATE manga SET id=? WHERE id=?", id, existing)
                            exec("UPDATE chapter SET manga_id=? WHERE manga_id=?", id, existing)
                            mangaCount++
                        }
                        else -> mangaCount += exec("INSERT INTO manga(id, source_id, url) VALUES(?,?,?)", id, source, url)
                    }
                }
                for (chapter in chapters) {
                    if (scalar("SELECT id FROM manga WHERE id=?", chapter.mangaId) == null) {
                        conflicts++
                        continue
                    }
                    val existing = scalar("SELECT id FROM chapter WHERE manga_id=? AND url=?", chapter.mangaId, chapter.url)
                    val holder = scalar("SELECT id FROM chapter WHERE id=?", chapter.id)
                    when {
                        existing == chapter.id -> Unit
                        holder != null -> conflicts++
                        existing != null -> chapterCount += exec("UPDATE chapter SET id=? WHERE id=?", chapter.id, existing)
                        else ->
                            chapterCount +=
                                exec(
                                    "INSERT INTO chapter(id, manga_id, url, name, source_order, chapter_number, upload_date, scanlator) VALUES(?,?,?,?,?,?,?,?)",
                                    chapter.id, chapter.mangaId, chapter.url, chapter.name, chapter.sourceOrder, chapter.chapterNumber,
                                    chapter.uploadDate, chapter.scanlator ?: "",
                                )
                    }
                }
                connection.commit()
            } catch (error: Throwable) {
                connection.rollback()
                throw error
            } finally {
                connection.autoCommit = true
            }
            Triple(mangaCount, chapterCount, conflicts)
        }

    private fun mangaBy(
        where: String,
        vararg values: Any,
    ): Manga? =
        connection.prepareStatement("SELECT * FROM manga WHERE $where").use { statement ->
            values.forEachIndexed { index, value ->
                when (value) {
                    is Long -> statement.setLong(index + 1, value)
                    else -> statement.setString(index + 1, value.toString())
                }
            }
            statement.executeQuery().use { rows -> if (rows.next()) mangaRow(rows) else null }
        }

    private fun mangaRow(rows: ResultSet) =
        Manga(
            rows.getLong("id"), rows.getString("source_id"), rows.getString("url"), rows.getString("title"), rows.getString("thumbnail_url"),
            rows.getString("artist"), rows.getString("author"), rows.getString("description"), rows.getString("genre"), rows.getInt("status"),
            rows.getInt("initialized") != 0, rows.getInt("in_library") != 0, rows.getString("real_url"), memoOf(rows.getString("memo")),
        )

    private fun chapterRow(rows: ResultSet) =
        Chapter(
            rows.getLong("id"), rows.getLong("manga_id"), rows.getString("url"), rows.getString("name"), rows.getLong("upload_date"),
            rows.getDouble("chapter_number"), rows.getString("scanlator"), rows.getInt("source_order"), rows.getString("real_url"),
            rows.getInt("page_count"), memoOf(rows.getString("memo")),
        )
}
