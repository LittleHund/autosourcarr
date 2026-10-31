package autosourcarr.host

import eu.kanade.tachiyomi.network.GET
import eu.kanade.tachiyomi.network.NetworkHelper
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.longOrNull
import uy.kohesive.injekt.Injekt
import uy.kohesive.injekt.api.get
import java.io.File
import java.util.zip.GZIPInputStream

/**
 * Extension repositories ("extension stores"): the index of each configured repository is fetched, parsed and
 * merged with the installed extensions into Suwayomi's extension list shape (installed, hasUpdate, isObsolete).
 * Both index formats are read: index.pb (Mihon 0.20.1+, protobuf) and index.min.json (older apps).
 */
class ExtensionRepos(
    private val stateFile: File,
) {
    private val json = Json { ignoreUnknownKeys = true; prettyPrint = true }

    @Serializable
    data class Store(
        val indexUrl: String,
        val name: String = "",
        val badgeLabel: String = "",
        val extensionListUrl: String = "",
        val isLegacy: Boolean = false,
    )

    @Serializable
    data class SourceInfo(
        val id: String,
        val name: String,
        val lang: String,
        val baseUrl: String = "",
    )

    @Serializable
    data class Available(
        val pkg: String,
        val name: String,
        val lang: String,
        val versionName: String,
        val versionCode: Long,
        val libVersion: String,
        val apkUrl: String,
        val apkName: String,
        val iconUrl: String,
        val contentWarning: Int,
        val storeIndexUrl: String,
        val sources: List<SourceInfo>,
    )

    @Serializable
    private data class State(
        val stores: List<Store> = emptyList(),
        val available: List<Available> = emptyList(),
    )

    @Volatile
    private var state: State = if (stateFile.isFile) runCatching { json.decodeFromString<State>(stateFile.readText()) }.getOrElse { State() } else State()

    fun stores(): List<Store> = state.stores

    fun available(): List<Available> = state.available

    /** Sets the repository list (index URLs); the next fetch reads them. */
    fun setStores(urls: List<String>) {
        val stores = urls.map { url -> state.stores.firstOrNull { it.indexUrl == url } ?: Store(indexUrl = url) }.distinctBy { it.indexUrl }
        save(state.copy(stores = stores, available = state.available.filter { item -> stores.any { it.indexUrl == item.storeIndexUrl } }))
    }

    /** Fetches every index; a store that fails keeps its previous list and reports the error. */
    fun fetch(): Map<String, String> {
        val errors = linkedMapOf<String, String>()
        val stores = mutableListOf<Store>()
        val available = mutableListOf<Available>()
        for (store in state.stores) {
            try {
                val (parsedStore, items) = read(store)
                stores += parsedStore
                available += items
            } catch (error: Exception) {
                errors[store.indexUrl] = error.message ?: error.javaClass.simpleName
                stores += store
                available += state.available.filter { it.storeIndexUrl == store.indexUrl }
            }
        }
        save(State(stores, available.distinctBy { it.pkg }))
        return errors
    }

    private fun read(store: Store): Pair<Store, List<Available>> {
        val client = Injekt.get<NetworkHelper>().client
        val bytes =
            client.newCall(GET(store.indexUrl)).execute().use { response ->
                if (!response.isSuccessful) throw IllegalStateException("HTTP ${response.code}")
                response.body.bytes()
            }
        val data = if (bytes.size > 2 && bytes[0] == 0x1f.toByte() && bytes[1] == 0x8b.toByte()) GZIPInputStream(bytes.inputStream()).readBytes() else bytes
        val base = store.indexUrl.substringBeforeLast('/') + "/"
        return if (store.indexUrl.endsWith(".pb") || data.firstOrNull() != '['.code.toByte()) readProto(store, data) else readJson(store, data, base)
    }

    /** index.pb: 1 name, 2 badge, 101 repeated extension {1 name, 2 pkg, 3 {1 apk, 2 icon}, 4 lib, 5 code, 6 version, 7 content warning, 8 sources}. */
    private fun readProto(
        store: Store,
        data: ByteArray,
    ): Pair<Store, List<Available>> {
        var name = store.name
        var badge = store.badgeLabel
        val items = mutableListOf<Available>()
        for ((field, value) in Proto.fields(data)) {
            when (field) {
                1 -> name = Proto.text(value)
                2 -> badge = Proto.text(value)
                101 ->
                    for ((rowField, row) in Proto.fields(value as ByteArray)) {
                        if (rowField != 1) continue
                        var extName = ""
                        var pkg = ""
                        var apk = ""
                        var icon = ""
                        var lib = ""
                        var code = 0L
                        var version = ""
                        var warning = 0
                        val sources = mutableListOf<SourceInfo>()
                        for ((f, v) in Proto.fields(row as ByteArray)) {
                            when (f) {
                                1 -> extName = Proto.text(v)
                                2 -> pkg = Proto.text(v)
                                3 ->
                                    for ((uf, uv) in Proto.fields(v as ByteArray)) {
                                        if (uf == 1 && apk.isEmpty()) apk = Proto.text(uv)
                                        if (uf == 2) icon = Proto.text(uv)
                                    }
                                4 -> lib = Proto.text(v)
                                5 -> code = v as Long
                                6 -> version = Proto.text(v)
                                7 -> warning = (v as Long).toInt()
                                8 -> {
                                    var id = 0L
                                    var sourceName = ""
                                    var lang = ""
                                    var url = ""
                                    for ((sf, sv) in Proto.fields(v as ByteArray)) {
                                        when (sf) {
                                            1 -> id = sv as Long
                                            2 -> sourceName = Proto.text(sv)
                                            3 -> lang = Proto.text(sv)
                                            4 -> url = Proto.text(sv)
                                        }
                                    }
                                    sources += SourceInfo(id.toString(), sourceName, lang, url)
                                }
                            }
                        }
                        if (pkg.isEmpty() || apk.isEmpty()) continue
                        val langs = sources.map { it.lang }.toSet()
                        items +=
                            Available(
                                pkg = pkg, name = extName.removePrefix("Tachiyomi: "), lang = if (langs.size == 1) langs.first() else "all",
                                versionName = version, versionCode = code, libVersion = lib, apkUrl = apk, apkName = apk.substringAfterLast('/'),
                                iconUrl = icon, contentWarning = warning, storeIndexUrl = store.indexUrl, sources = sources,
                            )
                    }
            }
        }
        return store.copy(name = name.ifEmpty { store.indexUrl }, badgeLabel = badge, isLegacy = false) to items
    }

    /** index.min.json: [{name, pkg, apk, lang, code, version, nsfw, sources:[{id, name, lang, baseUrl}]}]; APKs live in apk/. */
    private fun readJson(
        store: Store,
        data: ByteArray,
        base: String,
    ): Pair<Store, List<Available>> {
        val rows = json.parseToJsonElement(data.decodeToString()) as JsonArray
        val items =
            rows.map { node ->
                val row = node.jsonObject
                val apk = row["apk"]!!.jsonPrimitive.content
                val version = row["version"]!!.jsonPrimitive.content
                val pkg = row["pkg"]!!.jsonPrimitive.content
                Available(
                    pkg = pkg, name = row["name"]!!.jsonPrimitive.content.removePrefix("Tachiyomi: "), lang = row["lang"]?.jsonPrimitive?.contentOrNull ?: "",
                    versionName = version, versionCode = row["code"]?.jsonPrimitive?.longOrNull ?: 0, libVersion = version.substringBeforeLast('.'),
                    apkUrl = base + "apk/" + apk, apkName = apk, iconUrl = base + "icon/" + pkg + ".png",
                    contentWarning = row["nsfw"]?.jsonPrimitive?.intOrNull ?: 0, storeIndexUrl = store.indexUrl,
                    sources =
                        row["sources"]?.jsonArray.orEmpty().map { source ->
                            val item = source.jsonObject
                            SourceInfo(
                                item["id"]!!.jsonPrimitive.content, item["name"]?.jsonPrimitive?.contentOrNull ?: "",
                                item["lang"]?.jsonPrimitive?.contentOrNull ?: "", item["baseUrl"]?.jsonPrimitive?.contentOrNull ?: "",
                            )
                        },
                )
            }
        return store.copy(name = store.name.ifEmpty { base }, isLegacy = true) to items
    }

    private fun save(value: State) {
        state = value
        stateFile.parentFile.mkdirs()
        val temporary = File(stateFile.parentFile, stateFile.name + ".tmp")
        temporary.writeText(json.encodeToString(State.serializer(), value))
        if (!temporary.renameTo(stateFile)) {
            stateFile.delete()
            temporary.renameTo(stateFile)
        }
    }

    /** Minimal protobuf reader: (field, value) where value is Long (varint/fixed) or ByteArray (length-delimited). */
    object Proto {
        fun fields(data: ByteArray): List<Pair<Int, Any>> {
            val out = mutableListOf<Pair<Int, Any>>()
            var i = 0
            fun varint(): Long {
                var result = 0L
                var shift = 0
                while (true) {
                    val b = data[i++].toInt() and 0xFF
                    result = result or ((b and 0x7F).toLong() shl shift)
                    if (b < 0x80) return result
                    shift += 7
                }
            }
            while (i < data.size) {
                val key = varint()
                val field = (key ushr 3).toInt()
                when ((key and 7).toInt()) {
                    0 -> out += field to varint()
                    1 -> {
                        var v = 0L
                        for (k in 0 until 8) v = v or ((data[i + k].toLong() and 0xFF) shl (8 * k))
                        i += 8
                        out += field to v
                    }
                    2 -> {
                        val length = varint().toInt()
                        out += field to data.copyOfRange(i, i + length)
                        i += length
                    }
                    5 -> {
                        var v = 0L
                        for (k in 0 until 4) v = v or ((data[i + k].toLong() and 0xFF) shl (8 * k))
                        i += 4
                        out += field to v
                    }
                    else -> throw IllegalArgumentException("Unsupported protobuf wire type")
                }
            }
            return out
        }

        fun text(value: Any): String = (value as ByteArray).decodeToString()
    }
}
