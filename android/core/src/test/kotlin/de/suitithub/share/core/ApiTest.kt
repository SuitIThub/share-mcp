package de.suitithub.share.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlin.time.Duration.Companion.seconds

suspend fun apiError(block: suspend () -> Unit): ApiException {
    try {
        block()
    } catch (e: ApiException) {
        return e
    }
    fail("no ApiException")
    throw IllegalStateException()
}

class ApiTest {
    private lateinit var go: GoServer

    @Before fun setUp() {
        go = GoServer()
    }

    @After fun tearDown() = go.close()

    @Test fun pairsAndListsDevices() = runBlocking {
        val a = go.account("Pixel")
        val b = go.account("Tablet")
        assertEquals("Pixel", a.name)
        val devs = Api(a).devices()
        assertEquals(setOf("Pixel", "Tablet"), devs.map { it.name }.toSet())
        assertTrue(devs.all { it.type == DeviceType.ANDROID })
        assertEquals(b.identity.fingerprint, devs.single { it.id == b.deviceId }.certFingerprint)
    }

    @Test fun pairErrorsCarryStatus() = runBlocking {
        assertEquals(403, apiError { pair(go.url, "ZZZZZZ", "X", Identity.generate("X"), allowInsecureLocal = true) }.status)
        go.account("Pixel")
        assertEquals(409, apiError { go.account("Pixel") }.status)
    }

    @Test fun normalisesServerUrl() {
        assertEquals("https://x.example", normalizeServerUrl(" x.example/ "))
        assertEquals("https://x.example", normalizeServerUrl("https://x.example/"))
        assertEquals("https://x.example:8443/share", normalizeServerUrl("HTTPS://x.example:8443/share/?a=b#c"))
        assertEquals("http://10.0.2.2:8080", normalizeServerUrl("http://10.0.2.2:8080", allowInsecureLocal = true))
        for (bad in listOf("ftp://x", "", "http://x.example", "http://10.0.2.2:8080", "https://", "a b")) {
            assertThrows(bad, IllegalArgumentException::class.java) { normalizeServerUrl(bad) }
        }
        assertThrows(IllegalArgumentException::class.java) { normalizeServerUrl("http://x.example", allowInsecureLocal = true) }
    }

    @Test fun textOfferLifecycle() = runBlocking {
        val a = Api(go.account("Pixel"))
        val b = Api(go.account("Tablet"))
        val t = a.createTransfer(CreateTransferRequest(Kind.TEXT, text = "hällo", targets = listOf("Tablet"), broadcast = false)).transfer
        assertEquals("hällo", b.incoming().single().text)
        assertEquals(emptyList<String>(), b.accept(t.id).lanCandidates)
        assertEquals(Status.DELIVERED, a.transfer(t.id).deliveries.single().status)
        assertEquals(emptyList<Transfer>(), b.incoming())
    }

    @Test fun declineAndCancel() = runBlocking {
        val a = Api(go.account("Pixel"))
        val b = Api(go.account("Tablet"))
        val t = a.createTransfer(CreateTransferRequest(Kind.TEXT, text = "x", broadcast = true)).transfer
        assertEquals(t.id, a.outgoing().single().id)
        b.decline(t.id)
        assertEquals(409, apiError { b.accept(t.id) }.status)
        a.cancel(t.id)
        assertEquals(emptyList<Transfer>(), a.outgoing())
    }

    @Test fun unauthorisedIs401() = runBlocking {
        val a = go.account("Pixel")
        assertEquals(401, apiError { Api(a.copy(token = "nope")).devices() }.status)
    }

    @Test fun patchAndDeleteMe() = runBlocking {
        val a = Api(go.account("Pixel"))
        val code = a.pairCode()
        assertEquals(6, code.code.length)
        val b = Api(pair(go.url, code.code, "Tablet", Identity.generate("t"), allowInsecureLocal = true))
        a.updateMe(name = "Pixel 9", fcmToken = "f")
        assertEquals(setOf("Pixel 9", "Tablet"), b.devices().map { it.name }.toSet())
        assertEquals(409, apiError { a.updateMe(name = "tablet") }.status)
        a.deleteMe()
        assertEquals(listOf("Tablet"), b.devices().map { it.name })
        assertEquals(401, apiError { a.devices() }.status)
    }

    @Test fun errorMessageComesFromBody() = runBlocking {
        val a = go.account("Pixel")
        assertTrue(apiError { Api(a).transfer("nope") }.message!!.isNotBlank())
        MockWebServer().use { srv ->
            srv.start()
            val api = Api(a.copy(serverUrl = srv.url("/").toString().trimEnd('/')))
            srv.enqueue(MockResponse.Builder().code(502).addHeader("Content-Type", "text/html").body("<html>Bad gateway</html>").build())
            assertEquals("HTTP 502", apiError { api.devices() }.message)
            srv.enqueue(MockResponse.Builder().code(409).body("""{"error":"offer expired"}""").build())
            assertEquals("offer expired", apiError { api.accept("x") }.message)
            srv.enqueue(MockResponse.Builder().code(200).body("<html>login page</html>").build())
            try {
                api.devices()
                fail("html accepted")
            } catch (e: IOException) {
                // a captive portal answering 200 must be an ordinary IO error, never a crash
            }
        }
    }

    @Test fun cancellingTheCoroutineCancelsTheCall() = runBlocking {
        val a = go.account("Pixel")
        MockWebServer().use { srv ->
            srv.start()
            srv.enqueue(MockResponse.Builder().headersDelay(20, TimeUnit.SECONDS).build())
            val api = Api(a.copy(serverUrl = srv.url("/").toString().trimEnd('/')))
            val start = System.nanoTime()
            val job = launch(Dispatchers.Default) { api.devices() }
            delay(300)
            job.cancelAndJoin()
            assertTrue(System.nanoTime() - start < 5.seconds.inWholeNanoseconds)
        }
    }

    @Test fun manyRelayUploadsDoNotBlockOtherCalls() = runBlocking {
        val a = go.account("Pixel")
        MockWebServer().use { srv ->
            srv.start()
            val api = Api(a.copy(serverUrl = srv.url("/").toString().trimEnd('/')))
            repeat(8) { srv.enqueue(MockResponse.Builder().headersDelay(15, TimeUnit.SECONDS).build()) }
            val jobs = (1..8).map { launch(Dispatchers.Default) { try { api.relayUpload("r$it", 3) { ByteArray(3).inputStream() } } catch (_: CancellationException) { } } }
            repeat(8) { srv.takeRequest(5, TimeUnit.SECONDS) }
            srv.enqueue(MockResponse.Builder().body("[]").build())
            val devs = async(Dispatchers.Default) { api.devices() }
            kotlinx.coroutines.withTimeout(3000) { assertEquals(emptyList<Device>(), devs.await()) }
            jobs.forEach { it.cancelAndJoin() }
        }
    }
}
