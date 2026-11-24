package de.suitithub.share.core

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.time.Instant
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

fun testEngine(api: Api, store: DownloadStore, scope: CoroutineScope, now: () -> Instant = Instant::now) = Engine(
    api, store, scope,
    EngineOptions(
        endpointPort = 0, candidates = { listOf("127.0.0.1:$it") }, probeTimeout = 500.milliseconds, idleTimeout = 5.seconds,
        backoffBase = 20.milliseconds, linger = 50.milliseconds, tick = 50.milliseconds, now = now,
    ),
)

class EngineTest {
    @get:Rule val tmp = TemporaryFolder()
    private lateinit var go: GoServer
    private lateinit var a: Api // the other device, driven by hand
    private lateinit var acc: Account
    private lateinit var store: DirStore
    private lateinit var e: Engine
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val notices = CopyOnWriteArrayList<Notice>()
    @Volatile private var clock: () -> Instant = Instant::now

    @Before fun setUp() = runBlocking {
        go = GoServer()
        a = Api(go.account("Pixel"))
        acc = go.account("Tablet")
        store = DirStore(tmp.newFolder("dl"))
        e = newEngine()
    }

    @After fun tearDown() {
        e.close()
        scope.cancel()
        go.close()
    }

    private suspend fun newEngine(): Engine {
        val x = testEngine(Api(acc), store, scope) { clock() }
        scope.launch { x.notices.collect { notices += it } }
        while (x.noticeSubscribers == 0) delay(5)
        return x
    }

    private suspend fun eventually(what: String, seconds: Int = 10, cond: suspend () -> Boolean) {
        val end = System.nanoTime() + seconds * 1_000_000_000L
        while (!cond()) {
            if (System.nanoTime() > end) throw AssertionError("timeout: $what; state=${e.state.value} notices=$notices")
            delay(10)
        }
    }

    private suspend fun connected() {
        e.hold()
        withTimeout(5000) { e.state.first { it.connected && it.devices.isNotEmpty() } }
    }

    private suspend fun offerText(text: String = "hi", kind: Kind = Kind.TEXT) =
        a.createTransfer(CreateTransferRequest(kind, text, targets = listOf("Tablet"), broadcast = false)).transfer

    private fun file(size: Int, name: String = "f.bin") = FileOnDisk(tmp.newFile(name).apply { writeBytes(randomBytes(size)) })

    private var other: Engine? = null

    /** A second engine on the Pixel account that can serve files. */
    private suspend fun pixel(): Engine = other ?: testEngine(a, DirStore(tmp.newFolder("pixel")), scope).also {
        it.hold()
        other = it
        eventually("pixel online") { Api(acc).devices().single { d -> d.name == "Pixel" }.online }
    }

    private suspend fun status(t: Transfer) = a.transfer(t.id).deliveries.single().status

    @Test fun devicesExcludeSelfAndAgents() = runBlocking {
        val p = ProcessBuilder(GoServer.shareExe.path, "-config", File(tmp.root, "cli.json").path, "pair", "-name", "Bot", go.url, go.pairCode()).start()
        assertEquals(0, p.waitFor())
        connected()
        assertEquals(listOf("Pixel"), e.state.value.devices.map { it.name })
        val bot = a.devices().single { it.name == "Bot" }
        assertEquals("Bot", e.deviceName(bot.id))
        assertEquals("Unbekanntes Gerät", e.deviceName("nope"))
    }

    @Test fun incomingAppearsAndDisappears() = runBlocking {
        connected()
        val t = offerText()
        eventually("arrived") { e.state.value.incoming.map { it.id } == listOf(t.id) }
        eventually("notice") { notices.any { it is Notice.OfferArrived && it.transfer.id == t.id } }
        a.cancel(t.id)
        eventually("gone") { e.state.value.incoming.isEmpty() && Notice.OfferGone(t.id) in notices }
    }

    @Test fun offersFromBeforeConnectingAppear() = runBlocking {
        val t = offerText()
        connected()
        eventually("arrived") { e.state.value.incoming.map { it.id } == listOf(t.id) }
        e.refresh()
        assertEquals(1, notices.count { it is Notice.OfferArrived })
    }

    @Test fun acceptTextYieldsTextReceived() = runBlocking {
        connected()
        val t = offerText("hällo")
        eventually("arrived") { e.state.value.incoming.isNotEmpty() }
        listOf(async { e.accept(t.id) }, async { e.accept(t.id) }).awaitAll()
        eventually("text") { notices.filterIsInstance<Notice.TextReceived>().map { it.transfer.text } == listOf("hällo") }
        assertTrue(e.state.value.incoming.isEmpty())
        assertEquals(Status.DELIVERED, status(t))
    }

    /** From a notification the engine may know nothing about the offer yet. */
    @Test fun acceptWithoutConnectionToEventsWorks() = runBlocking {
        val t = offerText("x")
        e.accept(t.id)
        eventually("text") { notices.any { it is Notice.TextReceived } }
        assertEquals(Status.DELIVERED, status(t))
    }

    @Test fun acceptFilesRunsToDone() = runBlocking {
        connected()
        val f = file(2_000_000)
        val t = pixel().offerFiles(listOf(SlowSource(f, 100_000, 10)), listOf("Tablet"), false)
        eventually("arrived") { e.state.value.incoming.isNotEmpty() }
        assertFalse(e.hasWork.value)
        e.accept(t.id)
        eventually("running") { e.state.value.downloads.singleOrNull()?.let { it.phase == Phase.RUNNING && it.bytes > 0 && it.path == Path.LAN } == true }
        assertTrue(e.hasWork.value)
        eventually("done") { e.state.value.downloads.single().phase == Phase.DONE }
        val d = e.state.value.downloads.single()
        assertEquals(2_000_000L, d.bytes)
        assertArrayEquals(f.file.readBytes(), File(d.refs.single()).readBytes())
        eventually("idle") { !e.hasWork.value }
        eventually("notice") { notices.filterIsInstance<Notice.DownloadDone>().size == 1 }
        eventually("sender closed") { pixel().state.value.outgoing.isEmpty() }
        e.dismiss(t.id)
        assertTrue(e.state.value.downloads.isEmpty())
    }

    @Test fun doubleAcceptStartsOneDownload() = runBlocking {
        connected()
        val f = file(500_000)
        val t = pixel().offerFiles(listOf(f), listOf("Tablet"), false)
        eventually("arrived") { e.state.value.incoming.isNotEmpty() }
        (1..5).map { async(Dispatchers.IO) { e.accept(t.id) } }.awaitAll()
        eventually("done") { e.state.value.downloads.single().phase == Phase.DONE }
        e.accept(t.id)
        delay(300)
        assertEquals(1, f.opened.size)
        assertEquals(listOf("f.bin"), store.files())
        assertFalse(notices.any { it is Notice.Unavailable })
    }

    @Test fun acceptAfterWithdrawIsUnavailable() = runBlocking {
        val text = offerText()
        val files = a.createTransfer(CreateTransferRequest(Kind.FILES, files = listOf(FileItem("x", 5)), targets = listOf("Tablet"), broadcast = false)).transfer
        a.cancel(text.id)
        a.cancel(files.id)
        for (id in listOf(text.id, files.id, "unknown")) {
            e.accept(id)
            eventually("unavailable $id") { Notice.Unavailable(id) in notices }
        }
        assertTrue(e.state.value.downloads.isEmpty())
    }

    @Test fun acceptOfExpiredKnownOfferIsUnavailable() = runBlocking {
        connected()
        val files = pixel().offerFiles(listOf(file(5)), listOf("Tablet"), false)
        eventually("arrived") { e.state.value.incoming.isNotEmpty() }
        go.stop() // the engine does not learn about the withdrawal ...
        pixel().close()
        go.restart()
        Api(a.account).cancel(files.id) // pixel() has closed its own api
        e.accept(files.id)
        eventually("unavailable") { Notice.Unavailable(files.id) in notices }
        assertTrue(e.state.value.incoming.isEmpty() && e.state.value.downloads.isEmpty())
        assertFalse(notices.any { it is Notice.DownloadFailed })
    }

    @Test fun declineAfterWithdrawIsUnavailable() = runBlocking {
        val t = offerText()
        a.cancel(t.id)
        e.decline(t.id)
        e.decline("unknown")
        eventually("unavailable") { Notice.Unavailable(t.id) in notices && Notice.Unavailable("unknown") in notices }
    }

    @Test fun declineRemovesAndReports() = runBlocking {
        connected()
        val t = offerText()
        eventually("arrived") { e.state.value.incoming.isNotEmpty() }
        e.decline(t.id)
        assertTrue(e.state.value.incoming.isEmpty())
        assertEquals(Status.DECLINED, status(t))
    }

    @Test fun resumesConfirmedDownloadsOnConnect() = runBlocking {
        val f = file(1000)
        val t = pixel().offerFiles(listOf(f), listOf("Tablet"), false)
        Api(acc).accept(t.id) // confirmed by a process that has died since
        connected()
        eventually("done") { e.state.value.downloads.singleOrNull()?.phase == Phase.DONE }
        assertArrayEquals(f.file.readBytes(), File(e.state.value.downloads.single().refs.single()).readBytes())
    }

    @Test fun waitingOffersAreNeverAutoAccepted() = runBlocking {
        val t = pixel().offerFiles(listOf(file(10)), listOf("Tablet"), false)
        connected()
        e.refresh()
        e.offerDetails(t.id)
        delay(500)
        assertEquals(Status.WAITING, status(t))
        assertTrue(e.state.value.downloads.isEmpty())
        assertEquals(emptyList<String>(), store.files())
    }

    @Test fun cancelDownloadReportsFailedAndDiscards() = runBlocking {
        connected()
        val t = pixel().offerFiles(listOf(SlowSource(file(5_000_000), 50_000, 20)), listOf("Tablet"), false)
        e.accept(t.id)
        eventually("running") { (e.state.value.downloads.singleOrNull()?.bytes ?: 0) > 100_000 }
        e.cancelDownload(t.id)
        eventually("failed") { status(t) == Status.FAILED }
        eventually("clean") { store.leftovers().isEmpty() && e.state.value.downloads.isEmpty() }
        e.refresh()
        assertTrue(e.state.value.downloads.isEmpty())
    }

    @Test fun offlineCancelIsNotResumedLater() = runBlocking {
        connected()
        val t = pixel().offerFiles(listOf(SlowSource(file(5_000_000), 50_000, 20)), listOf("Tablet"), false)
        e.accept(t.id)
        eventually("running") { (e.state.value.downloads.singleOrNull()?.bytes ?: 0) > 100_000 }
        go.stop()
        e.cancelDownload(t.id)
        delay(200)
        go.restart()
        eventually("failed on server", 30) { runCatching { status(t) == Status.FAILED }.getOrDefault(false) }
        assertTrue(e.state.value.downloads.isEmpty())
    }

    @Test fun failedDownloadIsShown() = runBlocking {
        connected()
        val f = file(1000)
        val t = pixel().offerFiles(listOf(f), listOf("Tablet"), false)
        f.file.writeBytes(ByteArray(3))
        e.accept(t.id)
        eventually("failed") { e.state.value.downloads.singleOrNull()?.phase == Phase.FAILED }
        assertTrue("geändert" in e.state.value.downloads.single().error!!)
        eventually("notice") { notices.any { it is Notice.DownloadFailed } }
        eventually("idle") { !e.hasWork.value }
    }

    @Test fun withdrawnWhileDownloadingIsShownAsFailed() = runBlocking {
        connected()
        val t = pixel().offerFiles(listOf(SlowSource(file(5_000_000), 50_000, 20)), listOf("Tablet"), false)
        e.accept(t.id)
        eventually("running") { (e.state.value.downloads.singleOrNull()?.bytes ?: 0) > 100_000 }
        pixel().cancelOutgoing(t.id)
        eventually("failed") { e.state.value.downloads.singleOrNull()?.phase == Phase.FAILED }
    }

    @Test fun crashingStoreFailsTheDownloadOnly() = runBlocking {
        e.close()
        val broken = object : DownloadStore by store {
            override fun part(transferId: String, index: Int, item: FileItem): Part = throw SecurityException("no access")
        }
        e = testEngine(Api(acc), broken, scope)
        connected()
        val t = pixel().offerFiles(listOf(file(10)), listOf("Tablet"), false)
        e.accept(t.id)
        eventually("failed") { e.state.value.downloads.singleOrNull()?.phase == Phase.FAILED }
        eventually("reported") { status(t) == Status.FAILED }
    }

    @Test fun connectionFollowsHoldersAndWork() = runBlocking {
        delay(200)
        assertFalse(e.state.value.connected)
        val h1 = e.hold()
        val h2 = e.hold()
        eventually("connected") { e.state.value.connected }
        h1.close()
        h1.close() // closing twice must not release the other holder
        delay(300)
        assertTrue(e.state.value.connected)
        h2.close()
        eventually("disconnected") { !e.state.value.connected }
        eventually("server sees it") { !a.devices().single { it.name == "Tablet" }.online }
        val t = e.offerFiles(listOf(file(10)), listOf("Pixel"), false)
        eventually("connected by work") { e.state.value.connected }
        delay(300)
        assertTrue(e.state.value.connected)
        assertEquals(listOf(t.id), e.state.value.outgoing.map { it.id })
        e.cancelOutgoing(t.id)
        eventually("idle again") { !e.state.value.connected }
        e.offerText(Kind.TEXT, "x", listOf("Pixel"), false)
        delay(300)
        assertFalse("texts need no connection", e.hasWork.value)
    }

    @Test fun unauthorisedIsVisible() = runBlocking {
        connected()
        assertFalse(e.state.value.unauthorised)
        Api(acc).deleteMe()
        go.restart()
        eventually("unauthorised") { e.state.value.unauthorised && !e.state.value.connected }
    }

    @Test fun staleParts() = runBlocking {
        val item = FileItem("x", 100)
        store.part("old", 0, item).append().use { it.write(1) }
        go.stop()
        runCatching { e.refresh() }
        assertEquals(1, store.leftovers().size)
        go.restart()
        val t = a.createTransfer(CreateTransferRequest(Kind.FILES, files = listOf(item), targets = listOf("Tablet"), broadcast = false)).transfer
        Api(acc).accept(t.id)
        store.part(t.id, 0, item).append().use { it.write(1) }
        e.refresh()
        assertEquals(listOf("${t.id}-0.part"), store.leftovers())
    }

    @Test fun expiredOffersLeaveIncomingWithoutServer() = runBlocking {
        connected()
        val t = offerText()
        eventually("arrived") { e.state.value.incoming.isNotEmpty() }
        go.stop()
        val later = t.expiresAt.plusSeconds(1)
        clock = { later }
        eventually("gone") { e.state.value.incoming.isEmpty() && Notice.OfferGone(t.id) in notices }
    }

    @Test fun ownOffersEndLocallyWhenServerStaysAway() = runBlocking {
        connected()
        val f = file(10)
        val t = e.offerFiles(listOf(f), listOf("Pixel"), false)
        go.stop()
        eventually("disconnected") { !e.state.value.connected }
        delay(200)
        assertEquals(1, e.state.value.outgoing.size)
        val later = t.expiresAt.plusSeconds(11 * 60)
        clock = { later }
        eventually("ended") { e.state.value.outgoing.isEmpty() && f.released.get() == 1 }
    }

    @Test fun offerDetailsOnlyForWaitingOffers() = runBlocking {
        val t = offerText("x")
        assertEquals("x", assertNotNullAnd(e.offerDetails(t.id)).text)
        assertEquals("Pixel", e.deviceName(t.senderId))
        assertEquals(listOf(t.id), e.state.value.incoming.map { it.id })
        assertNull(e.offerDetails("unknown"))
        e.decline(t.id)
        assertNull(e.offerDetails(t.id))
        val w = offerText("y")
        a.cancel(w.id)
        assertNull(e.offerDetails(w.id))
    }

    private fun <T : Any> assertNotNullAnd(v: T?): T {
        assertNotNull(v)
        return v!!
    }

    @Test fun ownOffersAreNotIncoming() = runBlocking {
        connected()
        e.offerText(Kind.LINK, "https://x", emptyList(), true)
        delay(300)
        assertTrue(e.state.value.incoming.isEmpty())
        assertEquals(1, e.state.value.outgoing.size)
        assertNull(e.offerDetails(e.state.value.outgoing.single().id))
    }
}
