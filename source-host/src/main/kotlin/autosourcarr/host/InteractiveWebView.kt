package autosourcarr.host

/*
 * Input translation adapted from Suwayomi-Server v2.4.2366 server/global/impl/KcefWebView.kt (Mozilla Public
 * License 2.0, Copyright (C) Contributors to the Suwayomi project). Suwayomi streams PNG frames over its own
 * websocket; this host has no websocket server (JDK HttpServer), so the app reads a framed stream and posts input.
 */

import eu.kanade.tachiyomi.network.NetworkHelper
import io.github.oshai.kotlinlogging.KotlinLogging
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.put
import okhttp3.Cookie
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import org.cef.CefClient
import org.cef.browser.CefBrowser
import org.cef.browser.CefFrame
import org.cef.browser.CefRendering
import org.cef.handler.CefDisplayHandlerAdapter
import org.cef.handler.CefLoadHandler
import org.cef.handler.CefLoadHandlerAdapter
import org.cef.handler.CefRenderHandlerAdapter
import org.cef.handler.CefRequestHandlerAdapter
import org.cef.handler.CefResourceRequestHandler
import org.cef.handler.CefResourceRequestHandlerAdapter
import org.cef.misc.BoolRef
import org.cef.network.CefCookieManager
import org.cef.network.CefRequest
import uy.kohesive.injekt.Injekt
import uy.kohesive.injekt.api.get
import xyz.nulldev.androidcompat.webkit.CefHelper
import xyz.nulldev.androidcompat.webkit.dispose
import xyz.nulldev.androidcompat.webkit.disposeWithJsHandler
import java.awt.Component
import java.awt.Rectangle
import java.awt.event.InputEvent
import java.awt.event.KeyEvent
import java.awt.event.MouseEvent
import java.awt.event.MouseWheelEvent
import java.awt.image.BufferedImage
import java.awt.image.DataBufferInt
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import javax.imageio.IIOImage
import javax.imageio.ImageIO
import javax.imageio.ImageWriteParam
import javax.swing.JPanel

/**
 * SRC-5 "Open in WebView": an interactive page in the embedded Chromium for an administrator (image CAPTCHAs, sign-ins,
 * anything a source needs a human for). The page renders off screen; the app reads frames and events from
 * `GET /v1/webview/sessions/{id}/stream` and sends input to `POST /v1/webview/sessions/{id}/input`. Cookies the page
 * gets are copied into the host cookie jar (every 5 seconds, after each page load and on close), which is how a
 * solved CAPTCHA unblocks the source. Bounds: at most [MAX_SESSIONS] sessions, idle 5 minutes, 15 minutes in total,
 * at most 5 frames a second.
 *
 * Stream format: repeated messages of one type byte, a 4-byte big-endian length and the payload:
 * 'F' a JPEG frame, 'E' a UTF-8 JSON event ({type: ready|load|address|title|cookies|closed, ...}), 'H' a heartbeat
 * (empty).
 */
object InteractiveWebView {
    private val logger = KotlinLogging.logger {}
    const val MAX_SESSIONS = 4
    const val IDLE_MS = 5 * 60 * 1000L
    const val MAX_MS = 15 * 60 * 1000L
    const val FRAME_INTERVAL_MS = 200L
    private const val COOKIE_SYNC_MS = 5000L
    private const val HEARTBEAT_MS = 15_000L

    private val sessions = ConcurrentHashMap<String, Session>()
    private val random = SecureRandom()
    private val reaper =
        Executors.newSingleThreadScheduledExecutor { Thread(it, "webview-session-reaper").apply { isDaemon = true } }.also { executor ->
            executor.scheduleWithFixedDelay({
                runCatching {
                    val now = System.currentTimeMillis()
                    sessions.values.forEach { session ->
                        when {
                            now - session.created >= MAX_MS -> close(session.id, "The 15-minute WebView limit was reached")
                            now - session.lastActivity >= IDLE_MS -> close(session.id, "The WebView was idle for 5 minutes")
                        }
                    }
                }
            }, 5, 5, TimeUnit.SECONDS)
        }

    /** A WebView address: http or https with a host, no user name or password. */
    fun checkedUrl(raw: String): HttpUrl {
        val url = raw.trim().toHttpUrlOrNull() ?: throw IllegalArgumentException("The WebView opens http and https addresses only")
        require(url.username.isEmpty() && url.password.isEmpty()) { "Addresses with a user name or password are not opened" }
        return url
    }

    /** Opens a session at [url], or at an empty page (the administrator types an address) when it is null. */
    fun open(
        url: HttpUrl?,
        width: Int,
        height: Int,
    ): Session {
        if (!WebViewEngine.ready()) throw HostApi.HttpError(503, "The WebView is not ready (${WebViewEngine.state})")
        synchronized(this) {
            while (sessions.size >= MAX_SESSIONS) {
                val oldest = sessions.values.minByOrNull { it.created } ?: break
                close(oldest.id, "Another WebView session was opened")
            }
            val bytes = ByteArray(16).also { random.nextBytes(it) }
            val id = bytes.joinToString("") { "%02x".format(it) }
            val session = Session(id, url?.toString() ?: "about:blank", width.coerceIn(320, 1920), height.coerceIn(240, 1920))
            sessions[id] = session
            return session
        }
    }

    fun get(id: String): Session = sessions[id] ?: throw NoSuchElementException("No WebView session $id (it may have timed out)")

    fun close(
        id: String,
        reason: String,
    ) {
        sessions.remove(id)?.close(reason)
    }

    fun count(): Int = sessions.size

    class Session internal constructor(
        val id: String,
        start: String,
        @Volatile private var width: Int,
        @Volatile private var height: Int,
    ) {
        val created = System.currentTimeMillis()

        @Volatile var lastActivity = created
            private set

        @Volatile private var closedReason: String? = null
        private val events = LinkedBlockingQueue<String>(256)
        private val streams = AtomicInteger()
        private val frameLock = Any()
        private var painted: BufferedImage? = null
        private var paintedVersion = 0L

        @Volatile private var browserAgent: String? = null

        @Volatile private var currentUrl: String = start
        private val clearances = ConcurrentHashMap<String, String>()
        private val client: CefClient = runBlocking { CefHelper.createClient() }
        private val browser: CefBrowser

        init {
            client.addRequestHandler(RequestHandler())
            client.addDisplayHandler(DisplayHandler())
            client.addLoadHandler(LoadHandler())
            // The page starts with the source cookies the host already has (sign-ins, earlier clearances).
            WebViewEngine.copyCookiesIntoBrowser()
            browser =
                client.createBrowser(start, CefRendering.CefRenderingWithHandler(RenderHandler(), JPanel()), false).apply {
                    createImmediately()
                    setFocus(true)
                }
            event("ready") {
                put("id", id)
                put("url", start)
                put("width", width)
                put("height", height)
            }
        }

        val closed: Boolean get() = closedReason != null

        fun touch() {
            lastActivity = System.currentTimeMillis()
        }

        private fun event(
            type: String,
            fill: kotlinx.serialization.json.JsonObjectBuilder.() -> Unit = {},
        ) {
            val text =
                buildJsonObject {
                    put("type", type)
                    fill()
                }.toString()
            // A slow reader loses the oldest events, never blocks Chromium's threads.
            while (!events.offer(text)) events.poll()
        }

        /**
         * Writes frames and events until the session closes, another stream replaces this one or the reader goes
         * away (IOException). One stream at a time.
         */
        fun stream(out: OutputStream) {
            val generation = streams.incrementAndGet()
            touch()
            var sentVersion = -1L
            var lastFrame = 0L
            var lastSync = System.currentTimeMillis()
            var lastWrite = System.currentTimeMillis()
            try {
                while (closedReason == null && streams.get() == generation) {
                    var wrote = false
                    while (true) {
                        val text = events.poll() ?: break
                        write(out, 'E', text.toByteArray(Charsets.UTF_8))
                        wrote = true
                    }
                    val now = System.currentTimeMillis()
                    if (now - lastFrame >= FRAME_INTERVAL_MS) {
                        val frame = frameAfter(sentVersion)
                        if (frame != null) {
                            write(out, 'F', frame.second)
                            sentVersion = frame.first
                            lastFrame = now
                            wrote = true
                        }
                    }
                    if (now - lastSync >= COOKIE_SYNC_MS) {
                        syncCookies()
                        lastSync = now
                    }
                    if (!wrote && now - lastWrite >= HEARTBEAT_MS) {
                        write(out, 'H', ByteArray(0))
                        wrote = true
                    }
                    if (wrote) {
                        out.flush()
                        lastWrite = now
                    }
                    Thread.sleep(50)
                }
                closedReason?.let { reason ->
                    write(out, 'E', buildJsonObject { put("type", "closed"); put("reason", reason) }.toString().toByteArray(Charsets.UTF_8))
                    out.flush()
                }
            } catch (_: IOException) {
                // The app closed the stream; the session stays until it is closed or times out.
            } catch (_: InterruptedException) {
                Thread.currentThread().interrupt()
            }
        }

        private fun write(
            out: OutputStream,
            type: Char,
            payload: ByteArray,
        ) {
            val header = ByteBuffer.allocate(5).put(type.code.toByte()).putInt(payload.size).array()
            out.write(header)
            out.write(payload)
        }

        /** The newest painted frame as JPEG when it is newer than [version]. */
        private fun frameAfter(version: Long): Pair<Long, ByteArray>? {
            val copy: BufferedImage
            val copied: Long
            synchronized(frameLock) {
                val image = painted ?: return null
                if (paintedVersion == version) return null
                copy = BufferedImage(image.width, image.height, BufferedImage.TYPE_INT_RGB)
                System.arraycopy(
                    (image.raster.dataBuffer as DataBufferInt).data,
                    0,
                    (copy.raster.dataBuffer as DataBufferInt).data,
                    0,
                    image.width * image.height,
                )
                copied = paintedVersion
            }
            val writer = ImageIO.getImageWritersByFormatName("jpeg").next()
            val bytes = ByteArrayOutputStream()
            try {
                ImageIO.createImageOutputStream(bytes).use { output ->
                    writer.output = output
                    val param =
                        writer.defaultWriteParam.apply {
                            compressionMode = ImageWriteParam.MODE_EXPLICIT
                            compressionQuality = 0.7f
                        }
                    writer.write(null, IIOImage(copy, null, null), param)
                }
            } finally {
                writer.dispose()
            }
            return copied to bytes.toByteArray()
        }

        /** One input event from the app (see the C# SourceWebViewBroker for the accepted shapes). */
        fun input(message: JsonObject) {
            if (closedReason != null) throw HostApi.HttpError(410, "The WebView session is closed")
            touch()
            fun text(key: String) = (message[key] as? JsonPrimitive)?.contentOrNull
            fun number(key: String) = (message[key] as? JsonPrimitive)?.doubleOrNull
            fun flag(key: String) = (message[key] as? JsonPrimitive)?.booleanOrNull == true
            val component = browser.uiComponent
            val x = (number("x") ?: 0.0).toInt().coerceIn(0, width - 1)
            val y = (number("y") ?: 0.0).toInt().coerceIn(0, height - 1)
            val modifiers =
                (if (flag("ctrl")) InputEvent.CTRL_DOWN_MASK else 0) or
                    (if (flag("shift")) InputEvent.SHIFT_DOWN_MASK else 0) or
                    (if (flag("meta")) InputEvent.META_DOWN_MASK else 0)
            when (val type = text("type")) {
                "click" -> {
                    mouse(component, MouseEvent.MOUSE_MOVED, x, y, modifiers, 0)
                    mouse(component, MouseEvent.MOUSE_PRESSED, x, y, modifiers, (message["button"] as? JsonPrimitive)?.intOrNull ?: 0)
                    mouse(component, MouseEvent.MOUSE_RELEASED, x, y, modifiers, (message["button"] as? JsonPrimitive)?.intOrNull ?: 0)
                }
                "mousedown" -> mouse(component, MouseEvent.MOUSE_PRESSED, x, y, modifiers, (message["button"] as? JsonPrimitive)?.intOrNull ?: 0)
                "mouseup" -> mouse(component, MouseEvent.MOUSE_RELEASED, x, y, modifiers, (message["button"] as? JsonPrimitive)?.intOrNull ?: 0)
                "mousemove" -> mouse(component, MouseEvent.MOUSE_MOVED, x, y, modifiers, -1)
                "wheel" -> {
                    // As Suwayomi: the browser's pixel delta, negated (CEF scrolls down for a negative delta).
                    val pixels = (number("deltaY") ?: 0.0).toInt().coerceIn(-2000, 2000)
                    browser.sendMouseWheelEvent(
                        MouseWheelEvent(component, MouseEvent.MOUSE_WHEEL, 0L, modifiers, x, y, 0, false, MouseWheelEvent.WHEEL_UNIT_SCROLL, 1, -pixels),
                    )
                }
                "key", "keydown", "keyup" -> {
                    val key = text("key")?.takeIf { it.isNotEmpty() && it.length <= 32 } ?: return
                    if (type != "keyup") {
                        browser.sendKeyEvent(keyEvent(key, component, KeyEvent.KEY_PRESSED, modifiers))
                        typed(key, component, modifiers)?.let { browser.sendKeyEvent(it) }
                    }
                    if (type != "keydown") browser.sendKeyEvent(keyEvent(key, component, KeyEvent.KEY_RELEASED, modifiers))
                }
                "text" -> {
                    val value = text("text").orEmpty().take(2000)
                    for (char in value) {
                        val key = char.toString()
                        browser.sendKeyEvent(keyEvent(key, component, KeyEvent.KEY_PRESSED, 0))
                        typed(key, component, 0)?.let { browser.sendKeyEvent(it) }
                        browser.sendKeyEvent(keyEvent(key, component, KeyEvent.KEY_RELEASED, 0))
                    }
                }
                "navigate" -> {
                    val url = checkedUrl(text("url").orEmpty())
                    browser.loadURL(url.toString())
                }
                "back" -> if (browser.canGoBack()) browser.goBack()
                "forward" -> if (browser.canGoForward()) browser.goForward()
                "reload" -> browser.reload()
                "resize" -> {
                    width = ((number("width") ?: width.toDouble()).toInt()).coerceIn(320, 1920)
                    height = ((number("height") ?: height.toDouble()).toInt()).coerceIn(240, 1920)
                    browser.wasResized(width, height)
                }
                "sync" -> syncCookies()
                else -> throw IllegalArgumentException("Unknown WebView input ${type ?: "(no type)"}")
            }
        }

        private fun mouse(
            component: Component,
            id: Int,
            x: Int,
            y: Int,
            modifiers: Int,
            button: Int,
        ) {
            val (mask, awtButton) =
                when (button) {
                    1 -> InputEvent.BUTTON2_DOWN_MASK to MouseEvent.BUTTON2
                    2 -> InputEvent.BUTTON3_DOWN_MASK to MouseEvent.BUTTON3
                    -1 -> 0 to MouseEvent.NOBUTTON
                    else -> InputEvent.BUTTON1_DOWN_MASK to MouseEvent.BUTTON1
                }
            val held = if (id == MouseEvent.MOUSE_RELEASED) 0 else mask
            val clicks = if (id == MouseEvent.MOUSE_MOVED) 0 else 1
            browser.sendMouseEvent(MouseEvent(component, id, 0L, modifiers or held, x, y, x, y, clicks, false, awtButton))
        }

        private fun keyEvent(
            key: String,
            component: Component,
            id: Int,
            modifiers: Int,
        ): KeyEvent {
            val char = if (key.length == 1) key[0] else KeyEvent.CHAR_UNDEFINED
            val code = keyCode(key)
            return KeyEvent(component, id, 0L, modifiers, code, if (code == KeyEvent.VK_ENTER) '\n' else char, KeyEvent.KEY_LOCATION_STANDARD)
        }

        private fun typed(
            key: String,
            component: Component,
            modifiers: Int,
        ): KeyEvent? {
            val char =
                when {
                    key.length == 1 -> key[0]
                    key == "Enter" -> '\n'
                    else -> return null
                }
            return KeyEvent(component, KeyEvent.KEY_TYPED, 0L, modifiers, KeyEvent.VK_UNDEFINED, char, KeyEvent.KEY_LOCATION_UNKNOWN)
        }

        private fun keyCode(key: String): Int {
            if (key.length == 1) {
                val char = key[0].uppercaseChar()
                if (char in 'A'..'Z' || char in '0'..'9') return char.code
                return when (char) {
                    ' ' -> KeyEvent.VK_SPACE
                    '.' -> KeyEvent.VK_PERIOD
                    ',' -> KeyEvent.VK_COMMA
                    '-' -> KeyEvent.VK_MINUS
                    '/' -> KeyEvent.VK_SLASH
                    ';' -> KeyEvent.VK_SEMICOLON
                    '=' -> KeyEvent.VK_EQUALS
                    else -> KeyEvent.VK_UNDEFINED
                }
            }
            return when (key) {
                "Backspace" -> KeyEvent.VK_BACK_SPACE
                "Delete" -> KeyEvent.VK_DELETE
                "Enter" -> KeyEvent.VK_ENTER
                "Escape" -> KeyEvent.VK_ESCAPE
                "Tab" -> KeyEvent.VK_TAB
                "ArrowDown" -> KeyEvent.VK_DOWN
                "ArrowUp" -> KeyEvent.VK_UP
                "ArrowLeft" -> KeyEvent.VK_LEFT
                "ArrowRight" -> KeyEvent.VK_RIGHT
                "Home" -> KeyEvent.VK_HOME
                "End" -> KeyEvent.VK_END
                "PageUp" -> KeyEvent.VK_PAGE_UP
                "PageDown" -> KeyEvent.VK_PAGE_DOWN
                "Shift" -> KeyEvent.VK_SHIFT
                "Control" -> KeyEvent.VK_CONTROL
                "Alt" -> KeyEvent.VK_ALT
                "Meta" -> KeyEvent.VK_META
                else -> KeyEvent.VK_UNDEFINED
            }
        }

        /**
         * Copies Chromium's cookies into the host cookie jar (as Suwayomi's WebView does). A new cf_clearance belongs
         * to the browser's user agent, so the source client then uses that agent (as after a WebView Cloudflare solve).
         */
        fun syncCookies() {
            val manager = CefCookieManager.getGlobalManager() ?: return
            val network = Injekt.get<NetworkHelper>()
            var copied = 0
            var newClearance = false
            // Chromium visits the cookies on its own thread, one call each; the last call finishes the copy.
            manager.visitAllCookies { cookie, count, total, _ ->
                runCatching {
                    val domain = cookie.domain.removePrefix(".")
                    val built =
                        Cookie
                            .Builder()
                            .name(cookie.name)
                            .value(cookie.value)
                            .path(if (cookie.path?.startsWith('/') == true) cookie.path else "/")
                            .apply {
                                if (cookie.domain.startsWith(".")) domain(domain) else hostOnlyDomain(domain)
                                if (cookie.hasExpires && cookie.expires != null) expiresAt(cookie.expires.time) else expiresAt(Long.MAX_VALUE)
                                if (cookie.httponly) httpOnly()
                                if (cookie.secure) secure()
                            }.build()
                    network.cookieStore.addAll(HttpUrl.Builder().scheme("http").host(domain).build(), listOf(built))
                    copied++
                    if (cookie.name == "cf_clearance" && clearances.put(domain, cookie.value) != cookie.value) newClearance = true
                }.onFailure { logger.debug { "Copying WebView cookie ${cookie.name} failed: ${it.message}" } }
                if (count + 1 >= total) {
                    val agent = browserAgent
                    if (newClearance && agent != null && agent != network.defaultUserAgentProvider()) network.setUserAgent(agent)
                    event("cookies") { put("count", copied) }
                }
                true
            }
        }

        fun close(reason: String) {
            if (closedReason != null) return
            runCatching { syncCookies() }.onFailure { logger.warn(it) { "Copying the WebView cookies failed" } }
            closedReason = reason
            runCatching {
                browser.close(true)
                browser.dispose()
                client.disposeWithJsHandler()
            }.onFailure { logger.warn(it) { "Closing the WebView session failed" } }
            logger.info { "WebView session closed: $reason" }
        }

        fun describe(): JsonObject =
            buildJsonObject {
                put("id", id)
                put("url", currentUrl)
                put("width", width)
                put("height", height)
                put("created", created)
                put("last_activity", lastActivity)
            }

        private inner class RenderHandler : CefRenderHandlerAdapter() {
            override fun getViewRect(browser: CefBrowser): Rectangle = Rectangle(0, 0, width, height)

            override fun onPaint(
                browser: CefBrowser,
                popup: Boolean,
                dirtyRects: Array<Rectangle>,
                buffer: ByteBuffer,
                width: Int,
                height: Int,
            ) {
                // Popup widgets (select lists) arrive as a separate small buffer; only the page is streamed.
                if (popup || width <= 0 || height <= 0) return
                synchronized(frameLock) {
                    var image = painted
                    if (image == null || image.width != width || image.height != height) {
                        image = BufferedImage(width, height, BufferedImage.TYPE_INT_RGB)
                        painted = image
                    }
                    // BGRA bytes read as little-endian ints are 0xAARRGGBB; TYPE_INT_RGB ignores the alpha byte.
                    val target = (image.raster.dataBuffer as DataBufferInt).data
                    val source = buffer.duplicate().order(ByteOrder.LITTLE_ENDIAN).asIntBuffer()
                    source.get(target, 0, minOf(target.size, source.remaining()))
                    paintedVersion++
                }
            }
        }

        private inner class RequestHandler : CefRequestHandlerAdapter() {
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
                        request.getHeaderByName("User-Agent")?.takeIf { it.isNotBlank() }?.let { browserAgent = it }
                        return false
                    }
                }
        }

        private inner class DisplayHandler : CefDisplayHandlerAdapter() {
            override fun onAddressChange(
                browser: CefBrowser,
                frame: CefFrame,
                url: String,
            ) {
                if (!frame.isMain) return
                currentUrl = url
                event("address") { put("url", url) }
            }

            override fun onTitleChange(
                browser: CefBrowser?,
                newTitle: String?,
            ) {
                event("title") { put("title", newTitle ?: "") }
            }
        }

        private inner class LoadHandler : CefLoadHandlerAdapter() {
            override fun onLoadEnd(
                browser: CefBrowser,
                frame: CefFrame,
                httpStatusCode: Int,
            ) {
                if (!frame.isMain) return
                event("load") {
                    put("url", frame.url)
                    put("status", httpStatusCode)
                }
                runCatching { syncCookies() }
            }

            override fun onLoadError(
                browser: CefBrowser,
                frame: CefFrame,
                errorCode: CefLoadHandler.ErrorCode,
                errorText: String,
                failedUrl: String,
            ) {
                if (!frame.isMain) return
                event("load") {
                    put("url", failedUrl)
                    put("status", 0)
                    put("error", errorText)
                }
            }
        }
    }
}
