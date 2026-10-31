package autosourcarr.host

import io.github.oshai.kotlinlogging.KotlinLogging
import kotlinx.coroutines.runBlocking
import okhttp3.Cookie
import okhttp3.HttpUrl
import org.cef.browser.CefBrowser
import org.cef.browser.CefFrame
import org.cef.browser.CefRendering
import org.cef.handler.CefRenderHandlerAdapter
import org.cef.handler.CefRequestHandlerAdapter
import org.cef.handler.CefResourceRequestHandler
import org.cef.handler.CefResourceRequestHandlerAdapter
import org.cef.misc.BoolRef
import org.cef.network.CefCookie
import org.cef.network.CefCookieManager
import org.cef.network.CefRequest
import xyz.nulldev.androidcompat.webkit.CefHelper
import java.awt.Rectangle
import java.nio.ByteBuffer
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import javax.swing.JPanel
import kotlin.time.Duration

/**
 * Solves a Cloudflare challenge in the embedded Chromium, the way Mihon does with its WebView: open the page off
 * screen, let the challenge run, and wait for a `cf_clearance` cookie for the host. The clearance belongs to the
 * browser's own user agent, so the caller must use [Result.userAgent] for the retried request. One solve per host at
 * a time; callers for the same host share the result.
 */
object CloudflareWebViewSolver {
    private val logger = KotlinLogging.logger {}
    private val inflight = ConcurrentHashMap<String, CompletableFuture<Result?>>()

    data class Result(
        val cookies: List<Cookie>,
        val userAgent: String,
    )

    fun solve(
        url: HttpUrl,
        timeout: Duration,
    ): Result? {
        val mine = CompletableFuture<Result?>()
        val existing = inflight.putIfAbsent(url.host, mine)
        if (existing != null) return runCatching { existing.get(timeout.inWholeMilliseconds + 5000, TimeUnit.MILLISECONDS) }.getOrNull()
        try {
            val result = runCatching { solveNow(url, timeout) }.onFailure { logger.warn(it) { "WebView Cloudflare solve failed for ${url.host}" } }.getOrNull()
            mine.complete(result)
            return result
        } finally {
            inflight.remove(url.host, mine)
        }
    }

    private fun solveNow(
        url: HttpUrl,
        timeout: Duration,
    ): Result? {
        if (!WebViewEngine.ready()) return null
        val manager = CefCookieManager.getGlobalManager() ?: return null
        // A stale clearance would end the wait at once: remove the host's old one first.
        manager.deleteCookies(url.scheme + "://" + url.host, "cf_clearance")
        val userAgent = CompletableFuture<String>()
        val client = runBlocking { CefHelper.createClient() }
        var browser: CefBrowser? = null
        try {
            client.addRequestHandler(
                object : CefRequestHandlerAdapter() {
                    @Suppress("DEPRECATION")
                    override fun getResourceRequestHandler(
                        browser: CefBrowser,
                        frame: CefFrame,
                        request: CefRequest,
                        isNavigation: Boolean,
                        isDownload: Boolean,
                        requestInitiator: String,
                        disableDefaultHandling: BoolRef,
                    ): CefResourceRequestHandler =
                        object : CefResourceRequestHandlerAdapter() {
                            override fun onBeforeResourceLoad(
                                browser: CefBrowser?,
                                frame: CefFrame?,
                                request: CefRequest,
                            ): Boolean {
                                request.getHeaderByName("User-Agent")?.takeIf { it.isNotBlank() }?.let { userAgent.complete(it) }
                                return false
                            }
                        }
                },
            )
            browser =
                client.createBrowser(url.toString(), CefRendering.CefRenderingWithHandler(OffScreen, JPanel()), false).apply { createImmediately() }
            val deadline = System.nanoTime() + timeout.inWholeNanoseconds
            while (System.nanoTime() < deadline) {
                Thread.sleep(1000)
                val cookies = cookiesFor(manager, url)
                if (cookies.any { it.name == "cf_clearance" }) {
                    val agent = runCatching { userAgent.get(1, TimeUnit.SECONDS) }.getOrNull() ?: return null
                    logger.info { "WebView passed the Cloudflare check for ${url.host}" }
                    return Result(cookies, agent)
                }
            }
            logger.warn { "WebView did not pass the Cloudflare check for ${url.host} within $timeout" }
            return null
        } finally {
            browser?.close(true)
            browser?.dispose()
            client.dispose()
        }
    }

    private fun cookiesFor(
        manager: CefCookieManager,
        url: HttpUrl,
    ): List<Cookie> {
        val found = mutableListOf<CefCookie>()
        val done = CompletableFuture<Unit>()
        val started =
            manager.visitUrlCookies(url.toString(), true) { cookie, count, total, _ ->
                found += cookie
                if (count + 1 >= total) done.complete(Unit)
                true
            }
        if (!started) return emptyList()
        runCatching { done.get(2, TimeUnit.SECONDS) }
        return found.mapNotNull { cookie ->
            Cookie
                .Builder()
                .name(cookie.name)
                .value(cookie.value)
                .path(cookie.path ?: "/")
                .apply {
                    val domain = cookie.domain.removePrefix(".")
                    if (cookie.domain.startsWith(".")) domain(domain) else hostOnlyDomain(domain)
                    if (cookie.secure) secure()
                    if (cookie.httponly) httpOnly()
                    if (cookie.hasExpires && cookie.expires != null) expiresAt(cookie.expires.time)
                }.let { runCatching { it.build() }.getOrNull() }
        }
    }

    private object OffScreen : CefRenderHandlerAdapter() {
        override fun getViewRect(browser: CefBrowser): Rectangle = Rectangle(0, 0, 1280, 1024)

        override fun onPaint(
            browser: CefBrowser,
            popup: Boolean,
            dirtyRects: Array<Rectangle>,
            buffer: ByteBuffer,
            width: Int,
            height: Int,
        ) = Unit
    }
}
