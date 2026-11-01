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
 * compare-and-set and sleeps until it. The wait is bounded by [maxWaitMs]; a slot never lies further ahead than that,
 * so a burst of page images queues for at most [maxWaitMs] and then goes out at the interval rate. The wait ends early
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

    /** Takes the next slot of [host] and answers how long to wait for it, in nanoseconds (at most [maxWaitMs]). */
    internal fun reserve(host: String): Long {
        val interval = TimeUnit.MILLISECONDS.toNanos(intervalMs)
        val horizon = TimeUnit.MILLISECONDS.toNanos(maxWaitMs.coerceAtLeast(0))
        val clock = next.computeIfAbsent(host) { AtomicLong(Long.MIN_VALUE) }
        while (true) {
            val now = nanoTime()
            val current = clock.get()
            val free = if (current == Long.MIN_VALUE || current - now < 0) now else current
            val slot = if (free - now > horizon) now + horizon else free
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
            if (chain.call().isCanceled()) throw IOException("Canceled")
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
