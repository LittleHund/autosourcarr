package autosourcarr.host

/*
 * Adapted from Suwayomi-Server v2.4.2366 server/util/CEFManager.kt (Mozilla Public License 2.0,
 * Copyright (C) Contributors to the Suwayomi project). Linux only; the configuration comes from HostConfig.
 */

import com.jetbrains.cef.JCefAppConfig
import eu.kanade.tachiyomi.network.NetworkHelper
import io.github.oshai.kotlinlogging.KotlinLogging
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.Cookie
import okhttp3.OkHttpClient
import okhttp3.Request
import org.apache.commons.compress.archivers.tar.TarArchiveInputStream
import org.apache.commons.compress.compressors.gzip.GzipCompressorInputStream
import org.cef.CefApp
import org.cef.CefSettings.LogSeverity
import org.cef.SystemBootstrap
import org.cef.network.CefCookie
import org.cef.network.CefCookieManager
import uy.kohesive.injekt.Injekt
import uy.kohesive.injekt.api.get
import xyz.nulldev.androidcompat.webkit.CefHelper
import xyz.nulldev.androidcompat.webkit.KcefWebViewProvider
import java.io.BufferedOutputStream
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardOpenOption
import java.nio.file.attribute.PosixFilePermission
import java.util.Date
import kotlin.io.path.ExperimentalPathApi
import kotlin.io.path.absolutePathString
import kotlin.io.path.createDirectories
import kotlin.io.path.createTempDirectory
import kotlin.io.path.deleteRecursively
import kotlin.io.path.div
import kotlin.io.path.exists
import kotlin.io.path.getPosixFilePermissions
import kotlin.io.path.inputStream
import kotlin.io.path.isRegularFile
import kotlin.io.path.listDirectoryEntries
import kotlin.io.path.moveTo
import kotlin.io.path.outputStream
import kotlin.io.path.readLines
import kotlin.io.path.readText
import kotlin.io.path.setPosixFilePermissions
import kotlin.io.path.writeText
import kotlin.time.Duration.Companion.minutes

private val logger = KotlinLogging.logger {}

/**
 * The embedded Chromium (JetBrains JCEF) that backs android.webkit.WebView for extensions and the Cloudflare
 * WebView solver. The Chromium build is downloaded once from the JetBrains Runtime release into
 * `<data>/bin/kcef` (about 150 MB). It renders off screen; on a server without a display the app starts Xvfb and
 * passes DISPLAY. WEBVIEW_ENABLED=false turns it off.
 */
@OptIn(ExperimentalPathApi::class)
object WebViewEngine {
    /** The JCEF build the androidCompat WebView code is compiled against (Suwayomi v2.4.2366 libs.versions.toml). */
    const val JCEF_VERSION = "144.0.15-g72717cf-chromium-144.0.7559.172-api-1.21-262-b37"
    const val JBR_RELEASE = "jbr-release-25.0.4b508.27"

    private val cefDir by lazy { HostConfig.dataDir.toPath() / "bin" / "kcef" }
    private val releaseFile by lazy { cefDir / "release" }

    @Volatile var state: String = "not started"
        private set

    /** Koin binding androidCompat's WebView asks for: copies the stored cookies into Chromium when a WebView opens. */
    val initBrowserHandler =
        object : KcefWebViewProvider.InitBrowserHandler {
            override fun init(provider: KcefWebViewProvider) = copyCookiesIntoBrowser()
        }

    fun ready(): Boolean = CefHelper.cefApp.value.getOrNull() != null && state == "ready"

    /** Starts Chromium on a background thread. Failures are logged and leave the WebView unavailable. */
    fun start() {
        if (!HostConfig.webViewEnabled) {
            state = "disabled"
            CefHelper.cefApp.value = Result.failure(CefHelper.CefException("WebView is disabled"))
            return
        }
        Thread({ runCatching { startBlocking() } }, "webview-start").apply { isDaemon = true }.start()
        Runtime.getRuntime().addShutdownHook(
            Thread {
                CefHelper.cefApp.value.getOrNull()?.let { runCatching { it.dispose() } }
            },
        )
    }

    private fun startBlocking() {
        try {
            state = "starting"
            CefHelper.cefApp.value = Result.success(null)
            if (!System.getProperty("os.name").lowercase().contains("linux")) throw CefHelper.CefException("The WebView runs on Linux only")
            System.loadLibrary("jawt")
            if (!installationValid()) {
                state = "downloading"
                download()
                if (!installationValid()) throw CefHelper.CefException("The downloaded Chromium build is incomplete")
                logger.info { "Downloaded the WebView Chromium build" }
            }
            val app =
                CefApp.getInstanceIfAny() ?: run {
                    val config =
                        JCefAppConfig.getInstance(cefDir.toString(), false).apply {
                            appArgsAsList.addAll(
                                arrayOf(
                                    "--disable-gpu",
                                    "--off-screen-rendering-enabled",
                                    "--disable-dev-shm-usage",
                                    "--change-stack-guard-on-fork=disable",
                                ),
                            )
                            cefSettings.apply {
                                windowless_rendering_enabled = true
                                cache_path = (HostConfig.dataDir.toPath() / "cache" / "kcef").absolutePathString()
                                log_severity = LogSeverity.LOGSEVERITY_WARNING
                            }
                        }
                    config.getLoader().loadLibrary("cef")
                    config.getLoader().loadLibrary("jcef")
                    CefApp.setIsRemoteEnabled(config.isRemoteEnabled)
                    SystemBootstrap.setLoader(config.getLoader())
                    CefApp.startup(config.getAppArgs())
                    CefApp.getInstance(config.getAppArgs(), config.cefSettings, config.getServerExe())
                }
            CefHelper.cefApp.value = Result.success(app)
            runBlocking { withTimeout(2.minutes) { CefHelper.waitForInit().first() } }
            state = "ready"
            logger.info { "WebView ready (Chromium ${JCEF_VERSION.substringAfter("chromium-").substringBefore("-")})" }
        } catch (error: Throwable) {
            state = "failed: ${error.message ?: error.javaClass.simpleName}"
            logger.error(error) { "The WebView could not start" }
            CefHelper.cefApp.value = Result.failure(error)
        }
    }

    fun copyCookiesIntoBrowser() {
        val manager = CefCookieManager.getGlobalManager() ?: return
        for (cookie in Injekt.get<NetworkHelper>().cookieStore.getStoredCookies()) {
            runCatching { manager.setCookie("https://" + cookie.domain, cookie.toCefCookie()) }
                .onFailure { logger.warn { "Copying cookie ${cookie.name} into the WebView failed" } }
        }
    }

    fun Cookie.toCefCookie(): CefCookie =
        CefCookie(
            name,
            value,
            if (hostOnly) domain else ".$domain",
            path,
            secure,
            httpOnly,
            Date(),
            null,
            expiresAt < 253402300799999L,
            Date(expiresAt),
        )

    private fun installationValid(): Boolean {
        if (!releaseFile.exists() || !releaseFile.isRegularFile()) return false
        return runCatching {
            releaseFile.readLines().firstNotNullOfOrNull { if (it.contains("JCEF_VERSION_DETAILED")) it.split("=").getOrNull(1) else null }
                ?.let { JCEF_VERSION.split("-chromium")[0] == it.trim().trim('"').split("-chromium")[0] } ?: false
        }.getOrDefault(false)
    }

    private val json = Json { ignoreUnknownKeys = true }

    @Serializable
    private data class GitHubRelease(
        val body: String = "",
        val assets: List<Asset> = emptyList(),
    ) {
        @Serializable
        data class Asset(
            val name: String = "",
            @SerialName("browser_download_url") val downloadUrl: String = "",
        )
    }

    private fun archAliases(): List<String> =
        when (System.getProperty("os.arch").lowercase()) {
            "aarch64", "arm64" -> listOf("aarch64", "arm64")
            else -> listOf("x64", "amd64", "x86_64")
        }

    private fun download() {
        logger.info { "Downloading the WebView Chromium build ($JBR_RELEASE)" }
        cefDir.deleteRecursively()
        cefDir.createDirectories()
        val client = OkHttpClient.Builder().followRedirects(true).build()
        val release =
            client
                .newCall(
                    Request
                        .Builder()
                        .url("https://api.github.com/repos/JetBrains/JetBrainsRuntime/releases/tags/$JBR_RELEASE")
                        .header("Accept", "application/vnd.github+json")
                        .build(),
                ).execute()
                .use { response ->
                    check(response.isSuccessful) { "GitHub answered ${response.code} for the Chromium release" }
                    json.decodeFromString<GitHubRelease>(response.body.string())
                }
        val urlRegex = "(https?://|www.)[-a-zA-Z0-9+&@#/%?=~_|!:.;]*[-a-zA-Z0-9+&@#/%=~_|]".toRegex()
        val arch = archAliases()
        val candidates =
            (
                urlRegex.findAll(release.body).map { it.value }.toList() +
                    release.assets.map { it.downloadUrl }
            ).filter { url ->
                url.contains("jcef", true) && !url.endsWith(".checksum", true) && url.contains("linux", true) &&
                    arch.any { url.contains(it, true) }
            }.distinct()
                .sortedWith(compareBy<String> { if (it.contains("sdk", true)) 1 else 0 }.thenBy { if (it.endsWith(".tar.gz", true)) 0 else 1 })
        val url = candidates.firstOrNull() ?: throw CefHelper.CefException("No Chromium build for linux/${arch.first()} in $JBR_RELEASE")
        val temp = createTempDirectory("cef")
        try {
            val archive = temp / "download.tar.gz"
            client.newCall(Request.Builder().url(url).build()).execute().use { response ->
                check(response.isSuccessful) { "Chromium download answered ${response.code}" }
                archive.outputStream().use { output -> response.body.byteStream().use { it.copyTo(output) } }
            }
            extract(archive)
            flatten()
        } finally {
            temp.deleteRecursively()
        }
    }

    private fun extract(archive: Path) {
        archive.inputStream().use { input ->
            TarArchiveInputStream(GzipCompressorInputStream(input)).use { tar ->
                while (tar.nextEntry != null) {
                    val entry = tar.currentEntry ?: continue
                    val file = cefDir / entry.name
                    if (!file.normalize().startsWith(cefDir)) throw CefHelper.CefException("Bad Chromium archive entry ${entry.name}")
                    if (entry.isDirectory) {
                        file.createDirectories()
                        continue
                    }
                    file.parent.createDirectories()
                    if (entry.isSymbolicLink) {
                        val target = file.parent.resolve(entry.linkName).normalize()
                        if (!target.startsWith(cefDir)) throw CefHelper.CefException("Bad Chromium archive link ${entry.name}")
                        Files.createSymbolicLink(file, Path.of(entry.linkName))
                        continue
                    }
                    BufferedOutputStream(file.outputStream(StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE), 4096).use { tar.copyTo(it) }
                    runCatching {
                        file.setPosixFilePermissions(
                            file.getPosixFilePermissions() +
                                setOf(PosixFilePermission.OWNER_EXECUTE, PosixFilePermission.GROUP_EXECUTE, PosixFilePermission.OTHERS_EXECUTE),
                        )
                    }
                }
            }
        }
    }

    /** The archive holds `<name>/lib/...` and `<name>/release`: keep lib's contents and the release file at the top. */
    private fun flatten() {
        val release =
            Files.walk(cefDir).use { paths -> paths.filter { Files.isRegularFile(it) && it.fileName.toString() == "release" }.findFirst().orElse(null) }
        val releaseText = release?.readText() ?: ""
        val parent = cefDir.listDirectoryEntries().firstOrNull { (it / "lib").exists() } ?: return
        val lib = (parent / "lib").moveTo(cefDir / ".lib")
        cefDir.listDirectoryEntries().filter { it != lib }.forEach { it.deleteRecursively() }
        lib.listDirectoryEntries().forEach { it.moveTo(cefDir / it.fileName) }
        Files.delete(lib)
        (cefDir / "release").writeText(releaseText)
    }
}
