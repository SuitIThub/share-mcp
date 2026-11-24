package de.suitithub.share.core

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import okio.Buffer
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.io.IOException
import java.io.OutputStream
import java.time.Instant
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

class ReceiverTest {
    @get:Rule val tmp = TemporaryFolder()
    private lateinit var go: GoServer
    private lateinit var a: Api
    private lateinit var b: Api
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private lateinit var s: Sender
    private lateinit var store: DirStore
    private var events: Job? = null
    private var lan: (Int) -> List<String> = { listOf("127.0.0.1:$it") }

    @Before fun setUp() = runBlocking {
        go = GoServer()
        a = Api(go.account("Pixel"))
        b = Api(go.account("Tablet"))
        s = Sender(a, endpointPort = 0, candidates = { lan(it) })
        store = DirStore(tmp.newFolder("dl"))
        connect()
    }

    @After fun tearDown() {
        s.close()
        scope.cancel()
        go.close()
    }

    private suspend fun connect() {
        events = scope.launch { a.events(20.milliseconds).collect { s.onEvent(it, scope) } }
        withTimeout(5000) { while (!b.devices().single { it.name == "Pixel" }.online) delay(10) }
    }

    private fun file(size: Int, name: String = "f.bin") = FileOnDisk(tmp.newFile(name).apply { writeBytes(randomBytes(size, name.hashCode())) })

    private fun receiver(st: DownloadStore = store) = Receiver(b, st, probeTimeout = 500.milliseconds, idleTimeout = 5.seconds, backoffBase = 20.milliseconds)

    private suspend fun offer(vararg src: FileSource) = s.offerFiles(src.toList(), listOf("Tablet"), false)

    private suspend fun status(t: Transfer) = b.transfer(t.id).deliveries.single()

    private fun content(ref: String) = File(ref).readBytes()

    @Test fun lanDownloadIsIdenticalAndReportsLan() = runBlocking {
        val f1 = file(3_000_000, "a.bin")
        val f2 = file(10, "b.bin")
        val t = offer(f1, f2)
        val seen = CopyOnWriteArrayList<Pair<Long, Path>>()
        val refs = receiver().download(t) { n, p -> seen += n to p }
        assertArrayEquals(f1.file.readBytes(), content(refs[0]))
        assertArrayEquals(f2.file.readBytes(), content(refs[1]))
        assertEquals(3_000_010L, seen.last().first)
        assertTrue(seen.all { it.second == Path.LAN })
        assertEquals(seen.map { it.first }.sorted(), seen.map { it.first })
        val d = status(t)
        assertEquals(Status.DELIVERED, d.status)
        assertEquals(Path.LAN, d.path)
        assertEquals(emptyList<String>(), store.leftovers())
    }

    @Test fun fallsBackToRelayWhenProbeFails() = runBlocking {
        lan = { listOf("192.0.2.1:1", "127.0.0.1:1") }
        val f = file(1_000_000)
        val t = offer(f)
        val start = System.nanoTime()
        val refs = receiver().download(t) { _, p -> assertEquals(Path.RELAY, p) }
        assertTrue((System.nanoTime() - start) / 1e9 < 3.5)
        assertArrayEquals(f.file.readBytes(), content(refs.single()))
        assertEquals(Path.RELAY, status(t).path)
    }

    @Test fun oneReachableCandidateIsEnough() = runBlocking {
        lan = { listOf("192.0.2.1:1", "127.0.0.1:$it", "not an address") }
        val t = offer(file(1000))
        receiver().download(t) { _, p -> assertEquals(Path.LAN, p) }
        Unit
    }

    @Test fun wrongFingerprintNeverSendsTokenToLan() = runBlocking {
        IdentityTest.TlsEcho(Identity.generate("evil")).use { evil ->
            lan = { listOf("127.0.0.1:${evil.port}") }
            val f = file(1000)
            val refs = receiver().download(offer(f)) { _, p -> assertEquals(Path.RELAY, p) }
            assertArrayEquals(f.file.readBytes(), content(refs.single()))
            assertEquals(0, evil.requests)
        }
    }

    /** Starts a download, waits until at least `bytes` arrived, then kills it. */
    private suspend fun interruptAfter(t: Transfer, bytes: Long) {
        var got = 0L
        val job = scope.launch { receiver().download(t) { n, _ -> got = n } }
        withTimeout(10_000) { while (got < bytes) delay(5) }
        job.cancelAndJoin()
    }

    @Test fun resumesAfterNewInstance() = runBlocking {
        val f = file(4_000_000)
        val t = offer(SlowSource(f, 100_000, 10))
        interruptAfter(t, 2_000_000)
        val have = store.part(t.id, 0, f.item).size()
        assertTrue("$have", have in 1_500_000..3_999_999)
        val again = DirStore(File(tmp.root, "dl"))
        val refs = receiver(again).download(b.transfer(t.id)) { n, _ -> assertTrue(n >= have) }
        assertEquals(have, f.opened.last())
        assertArrayEquals(f.file.readBytes(), content(refs.single()))
    }

    @Test fun finishedFilesAreNotReloaded() = runBlocking {
        val f1 = file(1000, "a.bin")
        val f2 = file(2_000_000, "b.bin")
        val t = offer(f1, SlowSource(f2, 50_000, 10))
        interruptAfter(t, 500_000)
        assertEquals(1, f1.opened.size)
        val refs = receiver(DirStore(File(tmp.root, "dl"))).download(t) { n, _ -> assertTrue(n > 1000) }
        assertEquals(1, f1.opened.size)
        assertArrayEquals(f1.file.readBytes(), content(refs[0]))
        assertArrayEquals(f2.file.readBytes(), content(refs[1]))
        assertEquals(listOf("a.bin", "b.bin"), store.files())
    }

    @Test fun endpointLostMidFileContinuesViaRelay() = runBlocking {
        val f = file(6_000_000)
        val t = offer(SlowSource(f, 100_000, 10))
        val paths = CopyOnWriteArrayList<Path>()
        val dl = async(Dispatchers.IO) { receiver().download(t) { n, p -> if (paths.lastOrNull() != p) paths += p; if (n > 1_000_000 && p == Path.LAN) s.stopEndpointForTest() } }
        val refs = withTimeout(30_000) { dl.await() }
        assertArrayEquals(f.file.readBytes(), content(refs.single()))
        assertEquals(listOf(Path.LAN, Path.RELAY), paths.toList())
        assertEquals(Status.DELIVERED, status(t).status)
    }

    @Test fun changedSourceFailsAndReports() = runBlocking {
        val f1 = file(10, "a")
        val f = file(1000, "b")
        val t = offer(f1, f)
        f.file.writeBytes(ByteArray(5))
        try {
            receiver().download(t) { _, _ -> }
            fail()
        } catch (e: DownloadFailedException) {
            assertTrue(e.userMessage, "geändert" in e.userMessage)
        }
        assertEquals(Status.FAILED, status(t).status)
        assertEquals(listOf("a"), store.files()) // the complete file stays
        assertEquals(emptyList<String>(), store.leftovers())
    }

    @Test fun writeErrorFailsAndLeavesNothing() = runBlocking {
        val full = object : DownloadStore by store {
            override fun part(transferId: String, index: Int, item: FileItem): Part {
                val p = store.part(transferId, index, item)
                return object : Part by p {
                    override fun append(offset: Long): OutputStream {
                        val out = p.append(offset)
                        return object : OutputStream() {
                            var n = 0
                            override fun write(b: Int) = throw IOException("ENOSPC")
                            override fun write(b: ByteArray, off: Int, len: Int) {
                                if (n + len > 400_000) throw IOException("write failed: ENOSPC (No space left on device)")
                                n += len
                                out.write(b, off, len)
                            }

                            override fun close() = out.close()
                        }
                    }
                }
            }
        }
        val t = offer(file(1_000_000))
        try {
            receiver(full).download(t) { _, _ -> }
            fail()
        } catch (e: DownloadFailedException) {
            assertTrue(e.userMessage, "peicher" in e.userMessage)
        }
        assertEquals(Status.FAILED, status(t).status)
        assertEquals(emptyList<String>(), store.leftovers())
    }

    @Test fun cancelledOfferStopsRetrying() = runBlocking {
        val t = offer(SlowSource(file(5_000_000), 50_000, 20))
        val dl = scope.async { runCatching { receiver().download(t) { n, _ -> if (n > 200_000) scope.launch { s.cancel(t.id) } } } }
        val r = withTimeout(10_000) { dl.await() }
        assertTrue("$r", r.exceptionOrNull() is OfferEndedException)
        assertEquals(emptyList<String>(), store.leftovers())
    }

    @Test fun expiredOrUnknownOffersEnd() = runBlocking {
        val t = offer(file(10))
        s.cancel(t.id)
        for (x in listOf(t, t.copy(id = "nope"))) {
            assertTrue(runCatching { receiver().download(x) { _, _ -> } }.exceptionOrNull() is OfferEndedException)
        }
    }

    @Test fun senderGoneRetriesWhileAlive() = runBlocking {
        lan = { emptyList() }
        val f = file(1000)
        val t = offer(f)
        events!!.cancelAndJoin() // sender offline: relay answers 502
        withTimeout(5000) { while (b.devices().single { it.name == "Pixel" }.online) delay(10) }
        val dl = async(Dispatchers.IO) { receiver().download(t) { _, _ -> } }
        delay(500)
        assertTrue(dl.isActive)
        connect()
        val refs = withTimeout(10_000) { dl.await() }
        assertArrayEquals(f.file.readBytes(), content(refs.single()))
    }

    @Test fun serverOutageDuringDownloadIsSurvived() = runBlocking {
        val f = file(4_000_000)
        val t = offer(SlowSource(f, 100_000, 20))
        var got = 0L
        val dl = async(Dispatchers.IO) { receiver().download(t) { n, _ -> got = n } }
        while (got < 500_000) delay(5)
        go.restart()
        val refs = withTimeout(20_000) { dl.await() }
        assertArrayEquals(f.file.readBytes(), content(refs.single()))
        assertEquals(Status.DELIVERED, status(t).status)
    }

    @Test fun emptyAndHostileNames() = runBlocking {
        val empty = file(0, "empty.txt")
        val evil = FileOnDisk(tmp.newFile("x").apply { writeText("x") }, name = "../evil")
        val same = FileOnDisk(tmp.newFile("y").apply { writeText("y") }, name = "evil")
        receiver().download(offer(empty, evil, same)) { _, _ -> }
        assertEquals(listOf("empty.txt", "evil", "evil (1)"), store.files())
    }

    @Test fun coroutineCancelKeepsPartForResume() = runBlocking {
        val t = offer(SlowSource(file(3_000_000), 50_000, 10))
        val start = System.nanoTime()
        interruptAfter(t, 300_000)
        assertTrue((System.nanoTime() - start) / 1e9 < 5)
        assertEquals(1, store.leftovers().size)
        assertEquals(Status.DOWNLOADING, status(t).status)
    }

    @Test fun oversizedPartStartsOver() = runBlocking {
        val f = file(1000)
        val t = offer(f)
        store.part(t.id, 0, f.item).append(0).use { it.write(ByteArray(2000)) }
        val refs = receiver().download(t) { _, _ -> }
        assertArrayEquals(f.file.readBytes(), content(refs.single()))
    }

    // --- scripted server ---

    private fun scripted(handler: (String) -> MockResponse, block: suspend (Receiver, Transfer, List<String>) -> Unit) = runBlocking {
        val seen = CopyOnWriteArrayList<String>()
        MockWebServer().use { srv ->
            srv.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse {
                    val p = request.url.encodedPath.removePrefix("/v1/transfers/t1")
                    seen += request.method + p
                    return handler(p)
                }
            }
            srv.start()
            val api = Api(b.account.copy(serverUrl = srv.url("/").toString().trimEnd('/')))
            val t = Transfer("t1", "s", Kind.FILES, files = listOf(FileItem("f", 5)), expiresAt = Instant.now().plusSeconds(600))
            block(Receiver(api, store, 100.milliseconds, 2.seconds, 10.milliseconds, giveUpAfter = 300.milliseconds), t, seen)
        }
    }

    private fun ok(body: String = "") = MockResponse.Builder().body(body).build()
    private fun code(c: Int) = MockResponse.Builder().code(c).build()

    @Test fun lostDeliveredResponseIsRetried() {
        var n = 0
        scripted({ p ->
            when (p) {
                "/accept" -> ok("{}")
                "/files/0" -> ok("hello")
                "/delivered" -> if (n++ == 0) code(500) else code(204)
                else -> code(204)
            }
        }) { r, t, seen ->
            assertEquals(1, r.download(t) { _, _ -> }.size)
            assertEquals(2, seen.count { it == "POST/delivered" })
        }
    }

    @Test fun completeFilesCountAsSuccessEvenIfServerMovedOn() = scripted({ p ->
        when (p) {
            "/accept" -> ok("{}")
            "/files/0" -> ok("hello")
            "/delivered" -> code(409) // e.g. the first answer got lost
            else -> code(204)
        }
    }) { r, t, seen ->
        assertEquals("hello", File(r.download(t) { _, _ -> }.single()).readText())
        assertEquals(1, seen.count { it == "POST/delivered" })
    }

    @Test fun proxyErrorsOnRelayAreRetriedWhileAlive() {
        var n = 0
        scripted({ p ->
            when (p) {
                "/accept" -> ok("{}")
                "/files/0" -> when (n++) { 0 -> code(404); 1 -> code(403); 2 -> code(503); else -> ok("hello") }
                "" -> ok("""{"id":"t1","sender_id":"s","kind":"files","expires_at":"2099-01-01T00:00:00Z","deliveries":[{"device_id":"x","status":"accepted"}]}""")
                else -> code(204)
            }
        }) { r, t, _ -> assertEquals("hello", File(r.download(t) { _, _ -> }.single()).readText()) }
    }

    /** e.g. a maintenance or login page answered with 200 by a proxy. */
    @Test fun answersOfTheWrongLengthAreNeverStored() {
        var n = 0
        scripted({ p ->
            when (p) {
                "/accept" -> ok("{}")
                "/files/0" -> when (n++) {
                    0 -> ok("<html>maintenance</html>")
                    1 -> ok("hi")
                    2 -> MockResponse.Builder().chunkedBody("<html>", 3).build()
                    3 -> MockResponse.Builder().code(302).addHeader("Location", "/login").build()
                    else -> ok("hello")
                }
                "/login" -> ok("login")
                "" -> ok("""{"id":"t1","sender_id":"s","kind":"files","expires_at":"2099-01-01T00:00:00Z","deliveries":[{"device_id":"x","status":"accepted"}]}""")
                else -> code(204)
            }
        }) { r, t, _ ->
            assertEquals("hello", File(r.download(t) { _, _ -> }.single()).readText())
            assertEquals(5, n)
        }
    }

    @Test fun partialAnswerFromTheWrongOffsetIsNotStored() {
        var n = 0
        scripted({ p ->
            when (p) {
                "/accept" -> ok("{}")
                "/files/0" -> MockResponse.Builder().code(206).body(if (n == 0) "xxx" else "llo")
                    .addHeader("Content-Range", if (n++ == 0) "bytes 0-2/5" else "bytes 2-4/5").build()
                "" -> ok("""{"id":"t1","sender_id":"s","kind":"files","expires_at":"2099-01-01T00:00:00Z","deliveries":[{"device_id":"x","status":"accepted"}]}""")
                else -> code(204)
            }
        }) { r, t, _ ->
            store.part("t1", 0, t.files[0]).append(0).use { it.write("he".toByteArray()) }
            assertEquals("hello", File(r.download(t) { _, _ -> }.single()).readText())
        }
    }

    /** The store briefly cannot see its own part (e.g. Android's media provider restarts). */
    @Test fun busyStorageIsRetriedAndNeverMixesOffsets() = runBlocking {
        val f = file(500_000)
        val t = offer(f)
        store.part(t.id, 0, f.item).append(0).use { it.write(f.file.readBytes(), 0, 200_000) }
        var sizes = 0
        var appends = 0
        val flaky = object : DownloadStore by store {
            override fun part(transferId: String, index: Int, item: FileItem): Part {
                val p = store.part(transferId, index, item)
                return object : Part by p {
                    override fun size(): Long = when (sizes++) {
                        0 -> throw StorageBusyException("busy")
                        1 -> 0 // wrongly empty: append(0) must cut the old bytes off
                        else -> p.size()
                    }

                    override fun append(offset: Long): OutputStream = if (appends++ == 1) throw StorageBusyException("busy") else p.append(offset)
                }
            }
        }
        val refs = withTimeout(10_000) { receiver(flaky).download(t) { _, _ -> } }
        assertArrayEquals(f.file.readBytes(), content(refs.single()))
    }

    @Test fun unreachableServerLongAfterExpiryGivesUpButKeepsThePart() = scripted({ p ->
        when (p) {
            "/accept" -> ok("{}")
            "/files/0" -> MockResponse.Builder().body(Buffer().writeUtf8("hel")).setHeader("Content-Length", "5").build()
            else -> code(503)
        }
    }) { r, t, _ ->
        val e = withTimeout(10_000) { runCatching { r.download(t.copy(expiresAt = Instant.now().minusSeconds(1))) { _, _ -> } }.exceptionOrNull() }
        assertTrue("$e", e is DownloadFailedException)
        assertEquals(1, store.leftovers().size)
    }

    @Test fun revokedTokenEndsAtOnce() = scripted({ code(401) }) { r, t, seen ->
        assertTrue(runCatching { r.download(t) { _, _ -> } }.exceptionOrNull() is DownloadFailedException)
        assertEquals(listOf("POST/accept"), seen)
    }

    @Test fun deliveredFollowsProgressAtMostOncePerSecond() {
        val progress = CopyOnWriteArrayList<Long>()
        scripted({ p ->
            when (p) {
                "/accept" -> ok("{}")
                "/files/0" -> MockResponse.Builder().body("hello").throttleBody(1, 500, java.util.concurrent.TimeUnit.MILLISECONDS).build()
                "/progress" -> { progress += System.nanoTime(); code(204) }
                else -> code(204)
            }
        }) { r, t, seen ->
            r.download(t) { _, _ -> }
            assertTrue("$progress", progress.size in 2..5)
            // all but the closing one are at least ~1 s apart
            progress.zipWithNext().dropLast(1).forEach { (x, y) -> assertTrue((y - x) / 1e9 > 0.9) }
            assertEquals("POST/delivered", seen.last())
        }
    }

    @Test fun slowProgressReportsDoNotSlowTheDownload() = scripted({ p ->
        when (p) {
            "/accept" -> ok("{}")
            "/files/0" -> ok("hello")
            "/progress" -> MockResponse.Builder().code(204).headersDelay(3, java.util.concurrent.TimeUnit.SECONDS).build()
            else -> code(204)
        }
    }) { r, t, _ ->
        val start = System.nanoTime()
        r.download(t) { _, _ -> }
        assertTrue((System.nanoTime() - start) / 1e9 < 2.5)
    }
}
