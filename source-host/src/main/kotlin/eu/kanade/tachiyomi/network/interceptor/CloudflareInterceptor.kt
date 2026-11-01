package eu.kanade.tachiyomi.network.interceptor

import autosourcarr.host.HostConfig
import eu.kanade.tachiyomi.network.NetworkHelper
import io.github.oshai.kotlinlogging.KotlinLogging
import okhttp3.Cookie
import okhttp3.HttpUrl
import okhttp3.Interceptor
import okhttp3.Request
import okhttp3.Response
import uy.kohesive.injekt.injectLazy
import java.io.IOException
import kotlin.time.Duration.Companion.seconds

class CloudflareInterceptor(
    private val setUserAgent: (String) -> Unit,
) : Interceptor {
    private val logger = KotlinLogging.logger {}

    /**
     * AutoSourcarr (CON-2): the built-in WebView solves a Cloudflare challenge first (Mihon's approach); the
     * FlareSolverr-compatible solvers configured as connections (and the older FLARESOLVERR_URL) are the fallback,
     * cookies only ([autosourcarr.host.ExternalSolvers]). A host with a cached solver clearance is called with the
     * solver's user agent, which its cf_clearance cookie belongs to.
     */
    override fun intercept(chain: Interceptor.Chain): Response {
        var request = chain.request()
        val host = request.url.host
        val cachedAgent = autosourcarr.host.ExternalSolvers.userAgentFor(host)
        if (cachedAgent != null && request.header("User-Agent") != cachedAgent) {
            request = request.newBuilder().header("User-Agent", cachedAgent).build()
        }

        val originalResponse = chain.proceed(request)

        // Check if Cloudflare anti-bot is on
        if (!(originalResponse.code in ERROR_CODES && originalResponse.header("Server") in SERVER_CHECK)) {
            return originalResponse
        }
        originalResponse.close()
        // The cached clearance no longer passes: forget it and solve again.
        if (cachedAgent != null) autosourcarr.host.ExternalSolvers.invalidate(host)

        if (HostConfig.builtinSolverEnabled && autosourcarr.host.WebViewEngine.ready()) {
            val solved =
                autosourcarr.host.CloudflareWebViewSolver.solve(request.url, HostConfig.flareSolverrTimeout.value.coerceAtLeast(90).seconds)
            if (solved != null) {
                CFClearance.storeCookies(request.url, solved.cookies)
                if (request.header("User-Agent") != solved.userAgent) setUserAgent(solved.userAgent)
                return chain.proceed(CFClearance.buildRequestWithStoredCookies(request, solved.userAgent))
            }
            logger.warn { "The WebView did not pass the Cloudflare check for $host; trying the configured solvers" }
        }

        val clearance =
            try {
                autosourcarr.host.ExternalSolvers.solve(request.url)
            } catch (error: Exception) {
                // OkHttp's enqueue only handles IOExceptions
                throw IOException(error)
            }
        if (clearance != null) return chain.proceed(CFClearance.buildRequestWithStoredCookies(request, clearance.userAgent))

        throw IOException(
            when {
                autosourcarr.host.ExternalSolvers.configured().isNotEmpty() -> "Neither the WebView nor the configured solvers passed the Cloudflare check for $host"
                autosourcarr.host.WebViewEngine.ready() -> "The WebView could not pass the Cloudflare check for $host"
                else -> "Cloudflare bypass currently disabled"
            },
        )
    }

    companion object {
        private val ERROR_CODES = listOf(403, 503)
        private val SERVER_CHECK = arrayOf("cloudflare-nginx", "cloudflare")
        val COOKIE_NAMES = listOf("cf_clearance")
    }
}

/*
 * This class is ported from https://github.com/vvanglro/cf-clearance
 * The original code is licensed under Apache 2.0
*/
object CFClearance {
    private val logger = KotlinLogging.logger {}
    private val network: NetworkHelper by injectLazy()

    fun storeCookies(
        url: HttpUrl,
        cookies: List<Cookie>,
    ) {
        network.cookieStore.addAll(url, cookies)
    }

    fun buildRequestWithStoredCookies(
        request: Request,
        userAgent: String,
    ): Request {
        val cookies =
            network.cookieStore.get(request.url).joinToString("; ", postfix = "; ") {
                "${it.name}=${it.value}"
            }

        logger.trace { "Final cookies\n$cookies" }

        return request
            .newBuilder()
            .header("Cookie", cookies)
            .header("User-Agent", userAgent)
            .build()
    }
}
