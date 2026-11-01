package autosourcarr.host

import okhttp3.Interceptor
import okhttp3.Response
import java.io.IOException
import java.io.InterruptedIOException
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

/**
 * SRC-9 paced traffic: at least [intervalMs] between the starts of two network requests to the same host.
 *
 * Each host has its own reservation clock (no lock shared between hosts): a request takes the next free slot with one
 * compare-and-set and sleeps until it, so consecutive slots are always [intervalMs] apart. The wait is bounded by
 * [maxWaitMs]: when the next free slot lies further ahead than that (more than maxWaitMs / intervalMs requests already
 * queued for the host), the request fails at once with [LocalPacingDeferred] and takes no slot, instead of waiting longer or
 * being let through early. Letting it through would break the spacing (every overflow request would share one slot and
 * burst together); waiting longer would tie up dispatcher threads and run into call timeouts anyway. The caller (a
 * page or chapter fetch) gets a local deferral, never a source error, and can retry once the queue has drained. A slot taken by a request that
 * is then cancelled stays used, leaving at most one interval of idle time. The wait ends early
 * when the call is cancelled (call timeout or a cancelled request) or the thread is interrupted. Loopback hosts
 * (FlareSolverr, the host itself) are not paced. Installed as a network interceptor, so cached responses never wait.
 * 0 turns pacing off.
 */
class HostPacingInterceptor(
    private val intervalMs: Long = HostConfig.sourceMinIntervalMs,
    private val maxWaitMs: Long = 30_000,
    private val nanoTime: () -> Long = System::nanoTime,
) : Interceptor {
    private val next = ConcurrentHashMap<String, AtomicLong>()

    override fun intercept(chain: Interceptor.Chain): Response {
        if (intervalMs <= 0) return chain.proceed(chain.request())
        val host = chain.request().url.host.lowercase()
        if (isLoopback(host)) return chain.proceed(chain.request())
        val waitNanos = reserve(host)
        if (waitNanos > 0) sleep(chain, waitNanos)
        return chain.proceed(chain.request())
    }

    /**
     * Takes the next slot of [host] and answers how long to wait for it, in nanoseconds (at most [maxWaitMs]).
     * Throws [LocalPacingDeferred] without taking a slot when the next free slot is further ahead than [maxWaitMs].
     */
    internal fun reserve(host: String): Long {
        val interval = TimeUnit.MILLISECONDS.toNanos(intervalMs)
        val horizon = TimeUnit.MILLISECONDS.toNanos(maxWaitMs.coerceAtLeast(0))
        val clock = next.computeIfAbsent(host) { AtomicLong(Long.MIN_VALUE) }
        while (true) {
            val now = nanoTime()
            val current = clock.get()
            val slot = if (current == Long.MIN_VALUE || current - now < 0) now else current
            if (slot - now > horizon) {
                val retryAfter = TimeUnit.NANOSECONDS.toSeconds(slot - now - horizon + 999_999_999).coerceAtLeast(1)
                throw LocalPacingDeferred("paced: request deferred before upstream; too many queued requests for $host", retryAfter)
            }
            if (clock.compareAndSet(current, slot + interval)) {
                if (next.size > MAX_HOSTS) prune(now)
                return slot - now
            }
        }
    }

    private fun sleep(
        chain: Interceptor.Chain,
        waitNanos: Long,
    ) {
        val end = nanoTime() + waitNanos
        while (true) {
            // The call timed out or was cancelled while still waiting for its local slot: nothing reached the site.
            if (chain.call().isCanceled()) throw LocalPacingDeferred("Canceled while waiting for a pacing slot for ${chain.request().url.host}")
            val left = end - nanoTime()
            if (left <= 0) return
            try {
                TimeUnit.NANOSECONDS.sleep(minOf(left, TimeUnit.MILLISECONDS.toNanos(STEP_MS)))
            } catch (error: InterruptedException) {
                Thread.currentThread().interrupt()
                throw InterruptedIOException("Interrupted while pacing requests to ${chain.request().url.host}")
            }
        }
    }

    /** Forgets hosts whose last slot is long past, so the map stays small on a long-running host. */
    private fun prune(now: Long) {
        val old = TimeUnit.MINUTES.toNanos(10)
        next.entries.removeIf { entry -> entry.value.get().let { it != Long.MIN_VALUE && now - it > old } }
    }

    private fun isLoopback(host: String): Boolean =
        host == "localhost" || host.endsWith(".localhost") || host == "::1" || host == "[::1]" || host.startsWith("127.")

    companion object {
        private const val STEP_MS = 100L
        private const val MAX_HOSTS = 2048
    }
}

/**
 * The host's own pacing queue deferred a request before it reached the site. Never evidence of a source outage:
 * [SourceBackoff] does not count it and [HostApi] answers it as a local deferral. An [IOException], so OkHttp
 * propagates it like any call failure; wrappers keep it in the cause chain, see [of].
 */
class LocalPacingDeferred(
    message: String,
    val retryAfterSeconds: Long = 5,
) : IOException(message) {
    companion object {
        /** The deferral in [error] or its cause chain, or null. */
        fun of(error: Throwable): LocalPacingDeferred? {
            var current: Throwable? = error
            var depth = 0
            while (current != null && depth++ < 16) {
                val here: Throwable = current
                if (here is LocalPacingDeferred) return here
                current = here.cause.takeIf { it !== here }
            }
            return null
        }
    }
}
