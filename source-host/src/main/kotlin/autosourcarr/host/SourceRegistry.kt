package autosourcarr.host

import eu.kanade.tachiyomi.source.CatalogueSource
import eu.kanade.tachiyomi.source.Source
import java.util.concurrent.ConcurrentHashMap

/** Loaded source instances by source id, with the extension package each one came from. */
object SourceRegistry {
    data class Entry(
        val source: Source,
        val pkg: String,
        val contentWarning: Int,
    )

    private val sources = ConcurrentHashMap<Long, Entry>()

    fun register(
        source: Source,
        pkg: String,
        contentWarning: Int,
    ) {
        sources[source.id] = Entry(source, pkg, contentWarning)
    }

    fun unregisterPackage(pkg: String) {
        sources.entries.removeIf { it.value.pkg == pkg }
    }

    fun get(id: Long): Entry? = sources[id]

    fun catalogue(id: Long): CatalogueSource =
        sources[id]?.source as? CatalogueSource
            ?: throw NoSuchElementException("Source $id is not installed")

    fun all(): List<Entry> = sources.values.sortedWith(compareBy({ it.source.lang }, { it.source.name }))

    /**
     * The user agent changed (Cloudflare clearance): sources build their headers once, so they are recreated.
     * Suwayomi unregisters all sources here; the host reloads them from their jars instead.
     */
    @Volatile
    var onUserAgentChanged: () -> Unit = {}
}
