package autosourcarr.host

import kotlinx.coroutines.flow.MutableStateFlow
import java.io.File

/**
 * Settings of the source host, read once from the environment the C# app starts it with.
 * Replaces Suwayomi's server.conf; the FlareSolverr values keep Suwayomi's StateFlow shape (the session and
 * response-fallback ones are no longer read: external solvers are used for cookies only).
 */
object HostConfig {
    /** The Suwayomi build whose extension API (AppInfo version, lib 1.3 to 1.6) this host reproduces. */
    const val COMPAT_VERSION = "v2.4.2366"
    const val HOST_VERSION = "0.5.0"

    val dataDir: File = File(env("SOURCE_HOST_DATA", "./source-host-data")).absoluteFile
    val extensionsDir: File get() = File(dataDir, "extensions")
    val bind: String = env("SOURCE_HOST_BIND", "127.0.0.1")
    val port: Int = env("SOURCE_HOST_PORT", "4567").toInt()

    /** When set, every request needs the header X-Source-Host-Token with this value. */
    val token: String = env("SOURCE_HOST_TOKEN", "")

    val flareSolverrEnabled = MutableStateFlow(env("FLARESOLVERR_ENABLED", "false").toBoolean())
    val flareSolverrUrl = MutableStateFlow(env("FLARESOLVERR_URL", "http://localhost:8191"))
    val flareSolverrTimeout = MutableStateFlow(env("FLARESOLVERR_TIMEOUT", "60").toInt())
    val flareSolverrSessionName = MutableStateFlow(env("FLARESOLVERR_SESSION_NAME", "autosourcarr"))
    val flareSolverrSessionTtl = MutableStateFlow(env("FLARESOLVERR_SESSION_TTL", "15").toInt())

    /**
     * Phase 7 (CON-2): the built-in WebView solves Cloudflare challenges first; these FlareSolverr-compatible solvers
     * (FlareSolverr, Byparr) are the fallback, cookies only. AUTOSOURCARR_SOLVERS is the JSON list the app builds from
     * the enabled solver connections: `[{"url":"http://flaresolverr:8191","timeoutMs":60000}]`. The older
     * FLARESOLVERR_ENABLED/FLARESOLVERR_URL pair adds one more entry, so existing installs keep their solver.
     */
    val externalSolvers: List<ExternalSolvers.Endpoint> by lazy {
        val listed = ExternalSolvers.parse(System.getenv("AUTOSOURCARR_SOLVERS").orEmpty())
        val legacy =
            if (flareSolverrEnabled.value) {
                ExternalSolvers.endpoint(flareSolverrUrl.value, flareSolverrTimeout.value.coerceIn(10, 300) * 1000L, "legacy")
            } else {
                null
            }
        (listed + listOfNotNull(legacy)).distinctBy { it.url }
    }

    /** Test-only switch (AUTOSOURCARR_DISABLE_BUILTIN_SOLVER=1): skip the WebView solver so the external fallback runs. */
    val builtinSolverEnabled: Boolean = System.getenv("AUTOSOURCARR_DISABLE_BUILTIN_SOLVER") != "1"

    /**
     * SRC-9 paced traffic: least milliseconds between two requests to the same website host (SOURCE_MIN_INTERVAL_MS,
     * default 250; 0 turns pacing off). Read by [HostPacingInterceptor] in every source client.
     */
    val sourceMinIntervalMs: Long = env("SOURCE_MIN_INTERVAL_MS", "250").toLongOrNull()?.coerceIn(0L, 60_000L) ?: 250L

    /** The embedded Chromium for extension WebViews and Cloudflare (downloaded on first start, about 150 MB). */
    val webViewEnabled: Boolean = env("WEBVIEW_ENABLED", "true").toBoolean()

    private fun env(
        name: String,
        default: String,
    ): String = System.getenv(name)?.takeIf { it.isNotBlank() } ?: default
}
