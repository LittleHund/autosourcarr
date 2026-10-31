package autosourcarr.host

import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

/**
 * Per-source back-off: after [threshold] failed calls in a row a source is paused for 30 min, then 2 h, 6 h and
 * 12 h on each further failed round. One successful call ends the pause ladder. Wrong requests
 * (IllegalArgumentException) and missing items do not count; network errors, HTTP errors and parse failures do.
 */
class SourceBackoff(
    private val threshold: Int = 3,
    private val clock: () -> Instant = Instant::now,
) {
    class BackedOff(
        message: String,
    ) : RuntimeException(message)

    private data class State(
        var failures: Int = 0,
        var level: Int = 0,
        var until: Instant? = null,
        var lastError: String = "",
    )

    private val states = ConcurrentHashMap<Long, State>()

    fun <T> run(
        sourceId: Long,
        call: () -> T,
    ): T {
        val state = states.computeIfAbsent(sourceId) { State() }
        synchronized(state) {
            val until = state.until
            if (until != null && clock().isBefore(until)) {
                throw BackedOff("Source paused until $until after repeated errors (last: ${state.lastError})")
            }
        }
        return try {
            call().also {
                synchronized(state) {
                    state.failures = 0
                    state.level = 0
                    state.until = null
                }
            }
        } catch (error: IllegalArgumentException) {
            throw error
        } catch (error: NoSuchElementException) {
            throw error
        } catch (error: Throwable) {
            synchronized(state) {
                state.failures++
                state.lastError = (error.message ?: error.javaClass.simpleName).take(300)
                if (state.failures >= threshold) {
                    state.until = clock().plus(LADDER[state.level.coerceAtMost(LADDER.lastIndex)])
                    state.level++
                    state.failures = 0
                }
            }
            throw error
        }
    }

    fun reset(sourceId: Long) {
        states.remove(sourceId)
    }

    fun count(): Int = states.values.count { state -> synchronized(state) { state.until?.isAfter(clock()) == true } }

    fun state(sourceId: Long): JsonObject {
        val state = states[sourceId]
        return buildJsonObject {
            put("sourceId", sourceId.toString())
            if (state == null) {
                put("paused", false)
            } else {
                synchronized(state) {
                    val until = state.until
                    put("paused", until?.isAfter(clock()) == true)
                    put("until", until?.toString())
                    put("level", state.level)
                    put("failures", state.failures)
                    put("lastError", state.lastError)
                }
            }
        }
    }

    companion object {
        val LADDER: List<java.time.Duration> =
            listOf(java.time.Duration.ofMinutes(30), java.time.Duration.ofHours(2), java.time.Duration.ofHours(6), java.time.Duration.ofHours(12))
    }
}
