package autosourcarr.host

import eu.kanade.tachiyomi.network.GET
import eu.kanade.tachiyomi.network.NetworkHelper
import eu.kanade.tachiyomi.source.Source
import eu.kanade.tachiyomi.source.SourceFactory
import io.github.oshai.kotlinlogging.KotlinLogging
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import suwayomi.tachidesk.manga.impl.util.PackageTools
import uy.kohesive.injekt.Injekt
import uy.kohesive.injekt.api.get
import java.io.File
import java.nio.file.Path
import java.security.MessageDigest
import java.util.zip.ZipEntry
import java.util.zip.ZipInputStream
import java.util.zip.ZipOutputStream
import kotlin.io.path.absolutePathString

/**
 * Installs, loads and removes Tachiyomi/Mihon extension APKs without a database (port of Suwayomi's
 * Extension.installExtension / setupJar / extractAssetsFromApk). The list of installed extensions is
 * installed.json in the extensions folder; each extension is <pkg>-v<version>.jar next to it.
 */
class ExtensionStore(
    private val root: File,
) {
    private val logger = KotlinLogging.logger {}
    private val json = Json { ignoreUnknownKeys = true; prettyPrint = true }
    private val gate = Any()
    private val installed = linkedMapOf<String, InstalledExtension>()
    private val index = File(root, "installed.json")

    @Serializable
    data class InstalledExtension(
        val pkg: String,
        val name: String,
        val versionName: String,
        val versionCode: Int,
        val libVersion: String,
        val className: String,
        val jar: String,
        val lang: String,
        val contentWarning: Int,
        val signatureSha256: String? = null,
        val apkUrl: String? = null,
        val sourceIds: List<String> = emptyList(),
        val loadError: String? = null,
    )

    fun installed(): List<InstalledExtension> = synchronized(gate) { installed.values.toList() }

    /** Loads every extension of installed.json; one broken extension never stops the others. */
    fun loadInstalled() {
        val saved =
            if (index.isFile) runCatching { json.decodeFromString<List<InstalledExtension>>(index.readText()) }.getOrElse { emptyList() } else emptyList()
        for (extension in saved) {
            val loaded =
                try {
                    val ids = load(extension.pkg, File(root, extension.jar).toPath(), extension.className, extension.contentWarning)
                    extension.copy(sourceIds = ids.map { it.toString() }, loadError = null)
                } catch (error: Throwable) {
                    logger.warn(error) { "Extension ${extension.pkg} could not be loaded" }
                    extension.copy(loadError = error.message ?: error.javaClass.simpleName)
                }
            synchronized(gate) { installed[loaded.pkg] = loaded }
        }
    }

    /** Downloads an APK and installs it (an update replaces the installed version). */
    fun installFromUrl(
        apkUrl: String,
        expectedSha256: String? = null,
    ): InstalledExtension {
        val client = Injekt.get<NetworkHelper>().client
        root.mkdirs()
        val download = File.createTempFile("extension-", ".apk", root)
        try {
            client.newCall(GET(apkUrl)).execute().use { response ->
                if (!response.isSuccessful) throw IllegalStateException("Download failed: HTTP ${response.code} for $apkUrl")
                download.outputStream().use { output -> response.body.byteStream().copyTo(output) }
            }
            if (expectedSha256 != null && !expectedSha256.equals(sha256(download), ignoreCase = true)) {
                throw IllegalStateException("The downloaded APK does not match the expected SHA-256")
            }
            return installApk(download, apkUrl)
        } finally {
            download.delete()
        }
    }

    fun installApk(
        apk: File,
        apkUrl: String? = null,
    ): InstalledExtension {
        val info = PackageTools.getPackageInfo(apk.toPath())
        val pkg = info.packageName
        val features = info.reqFeatures.orEmpty().mapNotNull { it.name }
        if (PackageTools.EXTENSION_FEATURE !in features) throw IllegalArgumentException("This file is not a Tachiyomi extension")
        val libVersion = info.versionName.substringBeforeLast('.').toDouble()
        if (libVersion < PackageTools.LIB_VERSION_MIN || libVersion > PackageTools.LIB_VERSION_MAX) {
            throw IllegalArgumentException(
                "Extension library $libVersion is not supported (${PackageTools.LIB_VERSION_MIN} to ${PackageTools.LIB_VERSION_MAX})",
            )
        }
        val meta = info.applicationInfo.metaData
        val contentWarning =
            listOf(PackageTools.METADATA_CONTENT_WARNING, PackageTools.METADATA_NSFW)
                .firstNotNullOfOrNull { key -> meta.getString(key)?.toIntOrNull()?.takeIf { it != 0 } } ?: 0
        val sourceClass = meta.getString(PackageTools.METADATA_SOURCE_CLASS)?.trim() ?: throw IllegalArgumentException("The extension names no source class")
        val className = if (sourceClass.startsWith(".")) pkg + sourceClass else sourceClass
        val name =
            meta.getString(PackageTools.METADATA_NAME)
                ?: info.applicationInfo.nonLocalizedLabel?.toString()?.substringAfter("Tachiyomi: ")
                ?: pkg
        val libLabel = meta.getString(PackageTools.METADATA_EXTENSION_LIB)?.takeUnless { it == "0" } ?: info.versionName.substringBeforeLast('.')

        val jarName = "$pkg-v${info.versionName}.jar"
        val jar = File(root, jarName).toPath()
        val previous = synchronized(gate) { installed[pkg] }
        if (previous != null && previous.jar == jarName && previous.loadError == null) {
            throw IllegalStateException("$name ${info.versionName} is already installed")
        }
        val temporary = File(root, "$jarName.tmp").toPath()
        PackageTools.dex2jar(apk.toPath(), temporary)
        addAssets(apk, temporary.toFile())

        // Unload the old version before the new jar takes its place.
        if (previous != null) unload(previous)
        temporary.toFile().renameTo(jar.toFile())
        val ids =
            try {
                load(pkg, jar, className, contentWarning)
            } catch (error: Throwable) {
                jar.toFile().delete()
                throw error
            }
        val langs = ids.mapNotNull { SourceRegistry.get(it)?.source?.lang }.toSet()
        val result =
            InstalledExtension(
                pkg = pkg, name = name, versionName = info.versionName, versionCode = info.versionCode, libVersion = libLabel,
                className = className, jar = jarName, lang = if (langs.size == 1) langs.first() else if (langs.isEmpty()) "" else "all",
                contentWarning = contentWarning, signatureSha256 = PackageTools.getSignatureHash(info), apkUrl = apkUrl,
                sourceIds = ids.map { it.toString() },
            )
        synchronized(gate) {
            installed[pkg] = result
            save()
        }
        if (previous != null && previous.jar != jarName) File(root, previous.jar).delete()
        return result
    }

    /** Rebuilds the sources of one package (after a preference change). */
    fun reload(pkg: String) {
        val extension = synchronized(gate) { installed[pkg] } ?: return
        load(pkg, File(root, extension.jar).toPath(), extension.className, extension.contentWarning)
    }

    fun uninstall(pkg: String): Boolean {
        val extension = synchronized(gate) { installed.remove(pkg)?.also { save() } } ?: return false
        unload(extension)
        File(root, extension.jar).delete()
        return true
    }

    private fun unload(extension: InstalledExtension) {
        SourceRegistry.unregisterPackage(extension.pkg)
        PackageTools.jarLoaderMap.remove(File(root, extension.jar).toPath().absolutePathString())?.close()
    }

    private fun load(
        pkg: String,
        jar: Path,
        className: String,
        contentWarning: Int,
    ): List<Long> {
        val instance = runBlocking { PackageTools.loadExtensionSources(jar, className) }
        val sources: List<Source> =
            when (instance) {
                is Source -> listOf(instance)
                is SourceFactory -> instance.createSources()
                else -> throw IllegalStateException("Unknown source class type ${instance.javaClass.name}")
            }
        SourceRegistry.unregisterPackage(pkg)
        sources.forEach { SourceRegistry.register(it, pkg, contentWarning) }
        return sources.map { it.id }
    }

    private fun save() {
        val temporary = File(root, "installed.json.tmp")
        temporary.writeText(json.encodeToString(installed.values.toList()))
        if (!temporary.renameTo(index)) {
            index.delete()
            temporary.renameTo(index)
        }
    }

    /** Extensions read their assets from the class path, so the APK's assets/ go into the jar (Suwayomi does the same). */
    private fun addAssets(
        apk: File,
        jar: File,
    ) {
        val assets = linkedMapOf<String, ByteArray>()
        ZipInputStream(apk.inputStream()).use { zip ->
            generateSequence { zip.nextEntry }.forEach { entry ->
                if (entry.name.startsWith("assets/") && !entry.isDirectory) assets[entry.name] = zip.readBytes()
            }
        }
        val merged = File(jar.parentFile, jar.name + ".assets")
        ZipInputStream(jar.inputStream()).use { input ->
            ZipOutputStream(merged.outputStream()).use { output ->
                val names = HashSet<String>()
                generateSequence { input.nextEntry }.forEach { entry ->
                    if (!entry.name.startsWith("META-INF/") && names.add(entry.name)) {
                        output.putNextEntry(ZipEntry(entry.name))
                        input.copyTo(output)
                        output.closeEntry()
                    }
                }
                for ((name, bytes) in assets) {
                    if (names.add(name)) {
                        output.putNextEntry(ZipEntry(name))
                        output.write(bytes)
                        output.closeEntry()
                    }
                }
            }
        }
        jar.delete()
        merged.renameTo(jar)
    }

    private fun sha256(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input ->
            val buffer = ByteArray(64 * 1024)
            while (true) {
                val read = input.read(buffer)
                if (read < 0) break
                digest.update(buffer, 0, read)
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }
}
