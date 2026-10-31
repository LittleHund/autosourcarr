package autosourcarr.host

import kotlinx.coroutines.flow.MutableStateFlow
import java.io.File

/**
 * Settings of the source host, read once from the environment the C# app starts it with.
 * Replaces Suwayomi's server.conf; FlareSolverr values keep Suwayomi's StateFlow shape so the vendored
 * CloudflareInterceptor reads them unchanged.
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
    val flareSolverrAsResponseFallback = MutableStateFlow(env("FLARESOLVERR_RESPONSE_FALLBACK", "false").toBoolean())

    private fun env(
        name: String,
        default: String,
    ): String = System.getenv(name)?.takeIf { it.isNotBlank() } ?: default
}
