"""Execute the shipped preparing interceptor with real OkHttp against local HTTP.

Set AUTOSOURCARR_KOTLIN_TEST_CLASSPATH to a path-separated, locally supplied Kotlin
compiler/runtime + OkHttp/Okio classpath. Nothing is downloaded during pytest.
This is a JVM transport contract, not an Android APK or Suwayomi extension build.
"""
from pathlib import Path
import os
import shutil
import subprocess

import pytest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'extension/module/src/autosourcarr/AutoSourcarr.kt'


def test_preparation_handler_is_registered_before_http_source_response_parsing():
    source = SOURCE.read_text()
    # Phase 10 (READ-1): each server slot has its own address and API prefix.
    assert '.addInterceptor(ReaderPreparingInterceptor({ baseUrl }, { apiPrefix }))' in source
    assert '.addNetworkInterceptor { chain ->' in source
    assert 'removeHeader("X-AutoSourcarr-API-Key")' in source


HARNESS = r'''
package eu.kanade.tachiyomi.extension.all.autosourcarr

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import java.io.IOException
import java.util.concurrent.TimeUnit
import java.util.concurrent.CountDownLatch
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import okhttp3.*
import okhttp3.RequestBody.Companion.toRequestBody
import okio.BufferedSource
import okio.ForwardingSource
import okio.buffer

private class Server(private val respond: (Int, String) -> Triple<Int, Map<String, String>, String>) : AutoCloseable {
    val count = AtomicInteger()
    val requests = mutableListOf<String>()
    val http = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0).apply {
        createContext("/") { exchange ->
            val n = count.incrementAndGet()
            synchronized(requests) { requests.add(exchange.requestURI.toString()) }
            val (status, headers, text) = respond(n, exchange.requestURI.path)
            headers.forEach { (name, value) -> exchange.responseHeaders.add(name, value) }
            val bytes = text.toByteArray()
            exchange.sendResponseHeaders(status, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
            exchange.close()
        }
        start()
    }
    val url get() = "http://127.0.0.1:${http.address.port}"
    override fun close() = http.stop(0)
}
private val marker = mapOf("X-AutoSourcarr-Reader-State" to "preparing", "Retry-After" to "-7")
private fun client(origin: String, prefix: String = "/api/v1", timeout: Long = 0): OkHttpClient =
    OkHttpClient.Builder().callTimeout(timeout, TimeUnit.MILLISECONDS)
        .addInterceptor(ReaderPreparingInterceptor({ origin }, prefix)).build()
private fun get(client: OkHttpClient, url: String): Pair<Int, String> =
    client.newCall(Request.Builder().url(url).build()).execute().use { it.code to it.body!!.string() }
private fun verify(condition: Boolean, message: String) { if (!condition) error(message) }

fun main() {
    // An inherited HttpSource would reject each 503 before parsing. The real
    // application interceptor instead delivers the eventual successful response.
    val body = "{\"items\":[{\"id\":17}],\"has_next_page\":true}"
    Server { n, _ -> if (n < 3) Triple(503, marker, "preparing") else Triple(200, emptyMap(), body) }.use { server ->
        val closed = AtomicInteger()
        val downstreamCalls = AtomicInteger()
        val http = client(server.url).newBuilder().addInterceptor { chain ->
            verify(closed.get() == downstreamCalls.get(), "previous response was not closed before polling")
            downstreamCalls.incrementAndGet()
            val response = chain.proceed(chain.request())
            val original = response.body!!
            val countedSource = object : ForwardingSource(original.source()) {
                private var counted = false
                override fun close() {
                    if (!counted) { counted = true; closed.incrementAndGet() }
                    super.close()
                }
            }.buffer()
            response.newBuilder().body(object : ResponseBody() {
                override fun contentType() = original.contentType()
                override fun contentLength() = original.contentLength()
                override fun source(): BufferedSource = countedSource
            }).build()
        }.build()
        val path = "/api/v1/search?q=title&page=2&sources=source-a&match_count=2"
        val start = System.nanoTime()
        verify(get(http, server.url + path) == (200 to body), "successful feed was not preserved")
        val elapsed = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start)
        verify(server.count.get() == 3 && server.requests.all { it == path }, "polls changed the feed identity")
        verify(closed.get() == 3, "response bodies leaked: ${closed.get()} for ${downstreamCalls.get()}")
        verify(elapsed >= 1900 && elapsed < 5000, "Retry-After lower clamp was not one second")
    }
    // Exhaustion must report a useful transport exception rather than reaching
    // HttpSource's generic non-2xx branch or inventing a successful empty page.
    Server { _, _ -> Triple(503, marker, "preparing") }.use { server ->
        try { get(client(server.url), server.url + "/api/v1/browse/popular"); error("expected exhaustion") }
        catch (error: IOException) { verify(error.message.orEmpty().contains("still preparing"), "unhelpful exhaustion error") }
        verify(server.count.get() == 3, "preparation retries were not bounded")
    }
    // Neither website/WAF failures nor another HTTP method or endpoint is polled.
    val cases = listOf(
        Triple(503, emptyMap<String, String>(), "/api/v1/browse/popular"),
        Triple(403, marker, "/api/v1/browse/latest"),
        Triple(503, marker, "/api/v1/entries/7/refresh"),
        Triple(503, marker, "/api/v1/browse/popular/extra"),
    )
    for ((status, headers, path) in cases) {
        Server { _, _ -> Triple(status, headers, "blocked") }.use { server ->
            verify(get(client(server.url), server.url + path) == (status to "blocked"), "non-preparation response changed")
            verify(server.count.get() == 1, "unrelated response was retried")
        }
    }
    Server { _, _ -> Triple(503, marker, "blocked") }.use { server ->
        client(server.url).newCall(Request.Builder().url(server.url + "/api/v1/search").post("x".toRequestBody()).build())
            .execute().use { verify(it.code == 503, "POST response changed") }
        verify(server.count.get() == 1, "mutation was retried")
        verify(get(client("http://127.0.0.1:1"), server.url + "/api/v1/search").first == 503, "off-origin response changed")
        verify(server.count.get() == 2, "off-origin request was retried")
    }
    Server { _, _ -> Triple(503, marker, "external") }.use { destination ->
        Server { _, _ -> Triple(302, mapOf("Location" to destination.url + "/api/v1/search"), "redirect") }.use { origin ->
            verify(get(client(origin.url), origin.url + "/api/v1/search").first == 503, "redirected response changed")
            verify(origin.count.get() == 1 && destination.count.get() == 1, "off-origin redirect was polled")
        }
    }
    Server { _, path -> if (path.endsWith("search")) Triple(302, mapOf("Location" to "/api/v1/browse/latest"), "redirect") else Triple(503, marker, "other") }.use { server ->
        verify(get(client(server.url), server.url + "/api/v1/search").first == 503, "other-job response changed")
        verify(server.count.get() == 2, "redirected different job was polled")
    }
    // Android's reader prefix and reverse-proxy path prefixes use the same policy.
    for ((prefix, path) in listOf("/api/reader/v1" to "/api/reader/v1/browse/latest", "/api/v1" to "/nested/api/v1/browse/popular")) {
        Server { n, _ -> if (n == 1) Triple(503, marker, "preparing") else Triple(200, emptyMap(), body) }.use { server ->
            val origin = server.url + if (path.startsWith("/nested")) "/nested" else ""
            verify(get(client(origin, prefix), server.url + path).first == 200, "reader prefix not polled")
            verify(server.count.get() == 2, "reader prefix retry count")
        }
    }
    // A large server Retry-After cannot create an unbounded sleeping worker.
    Server { n, _ -> if (n == 1) Triple(503, marker + ("Retry-After" to "9999"), "preparing") else Triple(200, emptyMap(), body) }.use { server ->
        val start = System.nanoTime()
        verify(get(client(server.url), server.url + "/api/v1/browse/latest").first == 200, "upper-clamp result")
        val elapsed = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start)
        verify(elapsed >= 2900 && elapsed < 5000 && server.count.get() == 2, "Retry-After upper clamp")
    }
    // Call cancellation and its configured whole-call deadline remain effective
    // while waiting; no follow-up reaches the server after cancellation.
    Server { _, _ -> Triple(503, marker + ("Retry-After" to "3"), "preparing") }.use { server ->
        val responseSeen = CountDownLatch(1)
        val http = client(server.url).newBuilder().addInterceptor { chain ->
            chain.proceed(chain.request()).also { responseSeen.countDown() }
        }.build()
        val call = http.newCall(Request.Builder().url(server.url + "/api/v1/search").build())
        val failure = AtomicReference<Throwable>()
        val thread = Thread { try { call.execute().close(); failure.set(AssertionError("cancelled call succeeded")) } catch (e: Throwable) { failure.set(e) } }
        thread.start(); verify(responseSeen.await(2, TimeUnit.SECONDS), "request did not start")
        val start = System.nanoTime(); call.cancel(); thread.join(1000)
        verify(!thread.isAlive && failure.get() is IOException, "cancel did not interrupt polling")
        verify(TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start) < 600, "cancellation delayed")
        verify(server.count.get() == 1, "cancelled call retried")
    }
    Server { _, _ -> Triple(503, marker + ("Retry-After" to "3"), "preparing") }.use { server ->
        val start = System.nanoTime()
        try { get(client(server.url, timeout=350), server.url + "/api/v1/browse/latest"); error("deadline succeeded") }
        catch (_: IOException) {}
        verify(TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start) < 1000, "whole-call deadline ignored")
        verify(server.count.get() == 1, "expired call retried")
    }
    println("REAL_OKHTTP_PREPARATION_CONTRACT_OK")
}
'''


def test_actual_jvm_reader_preparing_transport(tmp_path):
    classpath = os.environ.get('AUTOSOURCARR_KOTLIN_TEST_CLASSPATH', '')
    java = shutil.which('java')
    if not classpath or not java:
        pytest.skip('optional actual JVM transport contract requires local Kotlin compiler + OkHttp classpath')
    for item in classpath.split(os.pathsep):
        assert Path(item).is_file(), f'Missing local JVM dependency: {item}'
    source = SOURCE.read_text()
    # Compile the exact production class; only unrelated Android/HttpSource code
    # above it is omitted. This does not translate or duplicate its implementation.
    production = source[source.index('internal class ReaderPreparingInterceptor('):]
    unit = tmp_path / 'ReaderPreparingInterceptor.kt'
    unit.write_text('package eu.kanade.tachiyomi.extension.all.autosourcarr\n'
                    'import okhttp3.*\nimport okhttp3.HttpUrl.Companion.toHttpUrl\n'
                    'import java.io.IOException\nimport java.io.InterruptedIOException\n' + production)
    harness = tmp_path / 'TransportContract.kt'
    harness.write_text(HARNESS)
    classes = tmp_path / 'classes'
    compiled = subprocess.run([java, '-cp', classpath, 'org.jetbrains.kotlin.cli.jvm.K2JVMCompiler',
                               '-no-stdlib', '-no-reflect', '-jvm-target', '17', '-classpath', classpath,
                               '-d', str(classes), str(unit), str(harness)], capture_output=True, text=True, timeout=60)
    assert compiled.returncode == 0, compiled.stdout + compiled.stderr
    executed = subprocess.run([java, '-cp', str(classes) + os.pathsep + classpath,
                               'eu.kanade.tachiyomi.extension.all.autosourcarr.TransportContractKt'],
                              capture_output=True, text=True, timeout=40)
    assert executed.returncode == 0, executed.stdout + executed.stderr
    assert 'REAL_OKHTTP_PREPARATION_CONTRACT_OK' in executed.stdout
