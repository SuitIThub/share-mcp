package de.suitithub.share.core

import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.IOException
import java.net.ServerSocket
import java.net.Socket
import java.time.Instant
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicReference
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager
import kotlin.concurrent.thread
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

class LocalEndpointTest {
    @get:Rule val tmp = TemporaryFolder()
    private val id = Identity.generate("a")
    private val secret = ByteArray(32) { 7 }
    private val token = AccessToken.create(secret, "t1", "d1")
    private val client = OkHttpClient.Builder().pinTo(id.fingerprint).build()
    private val offers = HashMap<String, Offer>()
    private lateinit var ep: LocalEndpoint
    private var port = 0

    @Before fun setUp() {
        ep = LocalEndpoint(id, preferredPort = 0, headerTimeout = 300.milliseconds) { offers[it] }
        port = ep.start()
    }

    @After fun tearDown() = ep.close()

    private fun offer(vararg sources: FileSource) {
        offers["t1"] = Offer(Transfer("t1", "s", Kind.FILES, files = sources.map { it.item }, expiresAt = Instant.now()), secret, sources.toList())
    }

    private fun file(size: Int, name: String = "f.bin") = FileOnDisk(tmp.newFile(name).apply { writeBytes(randomBytes(size)) })

    private fun get(path: String, range: String? = null, tok: String? = token): Response {
        val b = Request.Builder().url("https://127.0.0.1:$port$path")
        if (tok != null) b.header("Authorization", "Bearer $tok")
        if (range != null) b.header("Range", range)
        return client.newCall(b.build()).execute()
    }

    private fun code(path: String, range: String? = null, tok: String? = token) = get(path, range, tok).use { it.code }

    @Test fun pingAndFullDownload() {
        val f = file(3 * 1024 * 1024)
        offer(f)
        assertTrue(ep.running)
        assertEquals(204, code("/t/t1/ping"))
        get("/t/t1/files/0").use {
            assertEquals(200, it.code)
            assertEquals("${f.item.size}", it.header("Content-Length"))
            assertArrayEquals(f.file.readBytes(), it.body.bytes())
        }
    }

    @Test fun rangeResume() {
        val f = file(5000)
        offer(f)
        get("/t/t1/files/0", "bytes=1000-").use {
            assertEquals(206, it.code)
            assertEquals("bytes 1000-4999/5000", it.header("Content-Range"))
            assertArrayEquals(f.file.readBytes().drop(1000).toByteArray(), it.body.bytes())
        }
        assertEquals(206, code("/t/t1/files/0", "bytes=4999-"))
        for (bad in listOf("bytes=5000-", "bytes=1-2", "bytes=-5", "bytes=x-", "bytes=99999999999999999999-", "bytes=1-,3-")) {
            get("/t/t1/files/0", bad).use {
                assertEquals(bad, 416, it.code)
                assertEquals("bytes */5000", it.header("Content-Range"))
            }
        }
    }

    @Test fun authRules() {
        offer(file(10))
        for (p in listOf("/t/t1/ping", "/t/t1/files/0")) {
            assertEquals(401, code(p, tok = null))
            assertEquals(401, code(p, tok = "d1.00"))
            assertEquals(401, code(p, tok = AccessToken.create(secret, "t2", "d1")))
            assertEquals(401, code(p, tok = AccessToken.create(ByteArray(32), "t1", "d1")))
        }
        assertEquals(404, code("/t/t2/ping"))
        for (n in listOf("9", "-1", "x", "", "99999999999")) assertEquals(n, 404, code("/t/t1/files/$n"))
    }

    @Test fun textOffersAreNotServed() {
        offers["t1"] = Offer(Transfer("t1", "s", Kind.TEXT, text = "x", expiresAt = Instant.now()), secret, emptyList())
        assertEquals(404, code("/t/t1/ping"))
    }

    @Test fun changedFileIs409() {
        val f = file(10)
        offer(f)
        f.file.writeBytes(ByteArray(11))
        assertEquals(409, code("/t/t1/files/0"))
    }

    @Test fun unreadableFileIs410() {
        val f = file(10)
        offer(object : FileSource by f {
            override fun openAt(offset: Long) = throw IOException("gone")
        })
        assertEquals(410, code("/t/t1/files/0"))
    }

    @Test fun emptyFile() {
        offer(file(0))
        get("/t/t1/files/0").use {
            assertEquals(200, it.code)
            assertEquals("0", it.header("Content-Length"))
            assertEquals(0, it.body.bytes().size)
        }
    }

    @Test fun otherMethodsAndPaths() {
        offer(file(10))
        val post = Request.Builder().url("https://127.0.0.1:$port/t/t1/ping").header("Authorization", "Bearer $token").post(ByteArray(0).toRequestBody()).build()
        assertEquals(405, client.newCall(post).execute().use { it.code })
        for (p in listOf("/", "/t/t1", "/t/t1/files", "/t/t1/files/0/x", "/t//ping", "/v1/devices")) assertEquals(p, 404, code(p))
        assertTrue(raw("GET /t/../x HTTP/1.1\r\n\r\n").startsWith("HTTP/1.1 404"))
    }

    /** Sends raw bytes over TLS and returns what comes back until the peer closes. */
    private fun raw(text: String): String = tls().use { s ->
        s.soTimeout = 5000
        s.outputStream.write(text.toByteArray())
        s.outputStream.flush()
        try { s.inputStream.readBytes().decodeToString() } catch (e: IOException) { "" }
    }

    private fun tls(): Socket {
        val trustAll = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out java.security.cert.X509Certificate>?, authType: String?) = Unit
            override fun checkServerTrusted(chain: Array<out java.security.cert.X509Certificate>?, authType: String?) = Unit
            override fun getAcceptedIssuers() = emptyArray<java.security.cert.X509Certificate>()
        }
        return SSLContext.getInstance("TLS").apply { init(null, arrayOf(trustAll), null) }.socketFactory.createSocket("127.0.0.1", port)
    }

    private fun closedWithin(s: Socket, ms: Int): Boolean {
        s.soTimeout = ms
        return try {
            s.inputStream.read() == -1
        } catch (e: java.net.SocketTimeoutException) {
            false
        } catch (e: IOException) {
            true
        }
    }

    @Test fun abuseDoesNotKillServer() {
        offer(file(10))
        Socket("127.0.0.1", port).use { it.outputStream.write("GET / HTTP/1.1\r\n\r\n".toByteArray()) }
        for (junk in listOf("\r\n\r\n", "GET\r\n\r\n", "GET /t/t1/ping\r\n\r\n", "\u0000\u0001\r\n\r\n", "GET /t/t1/ping HTTP/1.1\r\nno colon\r\n\r\n")) raw(junk)
        val big = raw("GET /t/t1/ping HTTP/1.1\r\nX: " + "a".repeat(100_000) + "\r\n\r\n")
        assertTrue(big, big.isEmpty() || big.startsWith("HTTP/1.1 431"))
        Socket("127.0.0.1", port).use { assertTrue("silent tcp", closedWithin(it, 3000)) }
        tls().use { assertTrue("silent tls", closedWithin(it, 3000)) }
        // dripping bytes must not extend the deadline
        tls().use { s ->
            val start = System.nanoTime()
            try {
                while (System.nanoTime() - start < 5.seconds.inWholeNanoseconds) {
                    s.outputStream.write('a'.code)
                    Thread.sleep(50)
                }
            } catch (e: IOException) {
                // closed by the server
            }
            assertTrue(System.nanoTime() - start < 4.seconds.inWholeNanoseconds)
        }
        assertEquals(204, code("/t/t1/ping"))
    }

    @Test fun connectionLimitRecovers() {
        offer(file(10))
        val silent = (1..60).map { Socket("127.0.0.1", port) }
        Thread.sleep(800)
        silent.forEach { it.close() }
        assertEquals(204, code("/t/t1/ping"))
    }

    @Test fun slowDownloadOutlivesHeaderTimeout() {
        val f = file(20_000)
        offer(SlowSource(f, 1000, 50))
        get("/t/t1/files/0").use { assertArrayEquals(f.file.readBytes(), it.body.bytes()) }
    }

    @Test fun fallsBackToFreePort() {
        ServerSocket(0).use { busy ->
            LocalEndpoint(id, preferredPort = busy.localPort) { null }.use {
                val p = it.start()
                assertNotEquals(busy.localPort, p)
                assertTrue(p > 0)
            }
        }
    }

    @Test fun startIsIdempotentAndCloseStops() {
        assertEquals(port, ep.start())
        ep.close()
        assertFalse(ep.running)
        try {
            code("/t/t1/ping")
            throw AssertionError("still reachable")
        } catch (e: IOException) {
            // refused
        }
        assertTrue(ep.start() > 0)
        assertTrue(ep.running)
    }

    @Test fun fourParallelDownloads() {
        val f = file(8 * 1024 * 1024)
        offer(f)
        val want = f.file.readBytes()
        val pool = Executors.newFixedThreadPool(4)
        (1..4).map { pool.submit<ByteArray> { get("/t/t1/files/0").use { it.body.bytes() } } }.forEach { assertArrayEquals(want, it.get()) }
        pool.shutdown()
    }

    @Test fun shorterSourceBreaksTheConnection() {
        val f = file(5000)
        offer(object : FileSource by f {
            override fun openAt(offset: Long) = ByteArray(100).inputStream()
        })
        try {
            get("/t/t1/files/0").use { it.body.bytes() }
            throw AssertionError("truncated download looked complete")
        } catch (e: IOException) {
            // expected
        }
    }

    private fun assertEnds(end: () -> Unit) {
        offer(SlowSource(file(50 * 1024 * 1024), 1000, 20))
        val error = AtomicReference<Throwable>()
        val started = java.util.concurrent.CountDownLatch(1)
        val t = thread {
            try {
                get("/t/t1/files/0").use {
                    val s = it.body.byteStream()
                    s.read()
                    started.countDown()
                    s.readBytes()
                }
            } catch (e: IOException) {
                error.set(e)
            }
        }
        started.await()
        end()
        t.join(2000)
        assertFalse(t.isAlive)
        assertTrue(error.get() is IOException)
    }

    @Test fun closeEndsRunningDownloads() = assertEnds { ep.close() }

    @Test fun closeConnectionsEndsDownloadsOfThatOffer() = assertEnds { ep.closeConnections("t1") }
}
