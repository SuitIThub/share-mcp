package de.suitithub.share.core

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
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
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import kotlin.time.Duration.Companion.milliseconds

class SenderTest {
    @get:Rule val tmp = TemporaryFolder()
    private lateinit var go: GoServer
    private lateinit var a: Api
    private lateinit var b: Api
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var lastPort = 0
    private lateinit var s: Sender

    @Before fun setUp() = runBlocking {
        go = GoServer()
        a = Api(go.account("Pixel"))
        b = Api(go.account("Tablet"))
        s = newSender()
    }

    @After fun tearDown() {
        s.close()
        scope.cancel()
        go.close()
    }

    private fun newSender() = Sender(a, endpointPort = 0, candidates = { lastPort = it; listOf("127.0.0.1:$it") })

    private fun file(size: Int, name: String = "f.bin") = FileOnDisk(tmp.newFile(name).apply { writeBytes(randomBytes(size)) })

    private fun portOpen(p: Int) = try {
        Socket().use { it.connect(InetSocketAddress("127.0.0.1", p), 300) }
        true
    } catch (e: IOException) {
        false
    }

    /** Connects the sender's events and waits until the server sees it online. */
    private suspend fun connect(sender: Sender = s) {
        scope.launch { a.events(20.milliseconds).collect { sender.onEvent(it, scope) } }
        withTimeout(5000) { while (!b.devices().single { it.name == "Pixel" }.online) delay(10) }
    }

    private suspend fun eventually(what: String, cond: suspend () -> Boolean) {
        val end = System.nanoTime() + 5_000_000_000
        while (!cond()) {
            if (System.nanoTime() > end) throw AssertionError("timeout: $what")
            delay(20)
        }
    }

    @Test fun endpointRunsOnlyWhileFileOffersOpen() = runBlocking {
        connect()
        s.offerText(Kind.TEXT, "hi", listOf("Tablet"), false)
        assertEquals(0, lastPort)
        val t = s.offerFiles(listOf(file(10)), listOf("Tablet"), false, message = "für dich")
        assertTrue(portOpen(lastPort))
        assertEquals("für dich", b.transfer(t.id).message)
        assertEquals(listOf("127.0.0.1:$lastPort"), b.accept(t.id).lanCandidates)
        assertEquals(2, s.offers.value.size)
        s.cancel(t.id)
        assertFalse(portOpen(lastPort))
        assertEquals(1, s.offers.value.size)
        assertEquals(Status.CANCELLED, b.transfer(t.id).deliveries.single().status)
    }

    @Test fun relayRequestUploadsFromOffset() = runBlocking {
        connect()
        val f = file(700_000)
        val t = s.offerFiles(listOf(file(3, "x"), f), listOf("Tablet"), false)
        b.accept(t.id)
        val full = b.relayDownload(t.id, 1, 0).use { it.body.bytes() }
        assertArrayEquals(f.file.readBytes(), full)
        val rest = b.relayDownload(t.id, 1, 1000).use { it.body.bytes() }
        assertArrayEquals(f.file.readBytes().copyOfRange(1000, 700_000), rest)
        assertEquals(listOf(0L, 1000L), f.opened.toList())
    }

    @Test fun changedFileIsNotUploaded() = runBlocking {
        connect()
        val f = file(1000)
        val t = s.offerFiles(listOf(f), listOf("Tablet"), false)
        b.accept(t.id)
        f.file.writeBytes(ByteArray(999))
        assertEquals(502, apiError { b.relayDownload(t.id, 0, 0) }.status)
        assertEquals(emptyList<Long>(), f.opened.toList())
    }

    @Test fun bogusRelayRequestsAreHarmless() = runBlocking {
        connect()
        val f = file(1000)
        val t = s.offerFiles(listOf(f), listOf("Tablet"), false)
        for (r in listOf(RelayRequest("r", "unknown"), RelayRequest("r", t.id, 7), RelayRequest("r", t.id, -1), RelayRequest("r", t.id, 0, 5000), RelayRequest("r", t.id, 0, -1), RelayRequest("gone", t.id, 0, 0))) {
            s.onEvent(ServerEvent.Relay(r), scope)
        }
        delay(300)
        b.accept(t.id)
        assertEquals(1000, b.relayDownload(t.id, 0, 0).use { it.body.bytes() }.size)
    }

    @Test fun deliveryUpdatesReachOffers() = runBlocking {
        connect()
        go.account("Desk") // second target keeps the offer open
        val t = s.offerFiles(listOf(file(10)), listOf("Tablet", "Desk"), false)
        b.decline(t.id)
        withTimeout(5000) {
            s.offers.first { o -> o[t.id]!!.deliveries.any { it.deviceId == b.account.deviceId && it.status == Status.DECLINED } }
        }
        assertEquals(2, s.offers.value[t.id]!!.deliveries.size)
    }

    @Test fun lastDeclineClosesTheOffer() = runBlocking {
        connect()
        val f = file(10)
        val t = s.offerFiles(listOf(f), listOf("Tablet"), false)
        b.decline(t.id)
        withTimeout(5000) { s.offers.first { it.isEmpty() } }
        eventually("released") { f.released.get() == 1 }
        eventually("port closed") { !portOpen(lastPort) }
    }

    /** The receiver may answer before our create call has returned. */
    @Test fun closeBeforeCreateReturnsIsNotLost() = runBlocking {
        connect()
        scope.launch {
            b.events().collect { if (it is ServerEvent.OfferNew) b.decline(it.transfer.id) }
        }
        withTimeout(5000) { while (!a.devices().single { it.name == "Tablet" }.online) delay(10) }
        repeat(40) { s.offerText(Kind.TEXT, "x$it", listOf("Tablet"), false) }
        eventually("all closed") { s.offers.value.isEmpty() }
    }

    @Test fun closedWhileDisconnectedIsDetectedOnReconnect() = runBlocking {
        val f = file(10)
        val t = s.offerFiles(listOf(f), listOf("Tablet"), false)
        b.decline(t.id)
        assertEquals(1, s.offers.value.size)
        s.onEvent(ServerEvent.Connected, scope)
        withTimeout(5000) { s.offers.first { it.isEmpty() } }
        eventually("port closed") { !portOpen(lastPort) }
        assertEquals(1, f.released.get())
    }

    @Test fun reconnectRefreshesDeliveries() = runBlocking {
        go.account("Desk")
        val t = s.offerFiles(listOf(file(10)), listOf("Tablet", "Desk"), false)
        b.decline(t.id)
        s.onEvent(ServerEvent.Connected, scope)
        withTimeout(5000) { s.offers.first { o -> o[t.id]!!.deliveries.any { it.status == Status.DECLINED } } }
        Unit
    }

    @Test fun orphanedFileOffersAreCancelledOnFirstConnect() = runBlocking {
        val req = CreateTransferRequest(Kind.FILES, files = listOf(FileItem("x", 5)), targets = listOf("Tablet"), broadcast = false)
        val orphan = a.createTransfer(req).transfer
        val text = a.createTransfer(CreateTransferRequest(Kind.TEXT, text = "still there", targets = listOf("Tablet"), broadcast = false)).transfer
        s.onEvent(ServerEvent.Connected, scope)
        eventually("cancelled") { b.transfer(orphan.id).deliveries.single().status == Status.CANCELLED }
        assertEquals(Status.WAITING, b.transfer(text.id).deliveries.single().status)
        withTimeout(5000) { s.offers.first { it.keys == setOf(text.id) } } // texts survive a restart and stay cancellable
        s.cancel(text.id)
        assertEquals(Status.CANCELLED, b.transfer(text.id).deliveries.single().status)
    }

    @Test fun orphanCleanupNeverHitsOwnNewOffer() = runBlocking {
        repeat(15) { i ->
            val offer = async(Dispatchers.IO) { s.offerFiles(listOf(file(10, "f$i")), listOf("Tablet"), false) }
            launch(Dispatchers.IO) { s.onEvent(ServerEvent.Connected, scope) }
            s.onEvent(ServerEvent.Connected, scope)
            val t = offer.await()
            delay(50)
            assertEquals(Status.WAITING, b.transfer(t.id).deliveries.single().status)
            assertTrue(t.id in s.offers.value)
        }
    }

    @Test fun tooLongTextFailsBeforeNetwork() {
        go.stop()
        for (bad in listOf("a".repeat(65537), "ä".repeat(32769), "", "  ")) {
            assertThrows(IllegalArgumentException::class.java) { runBlocking { s.offerText(Kind.TEXT, bad, listOf("Tablet"), false) } }
        }
        assertThrows(IllegalArgumentException::class.java) { runBlocking { s.offerFiles(emptyList(), listOf("Tablet"), false) } }
    }

    @Test fun releaseCalledOnceOnEveryEnd() = runBlocking {
        connect()
        val f1 = file(10, "1")
        val t1 = s.offerFiles(listOf(f1), listOf("Tablet"), false)
        s.cancel(t1.id)
        s.cancel(t1.id)
        s.onEvent(ServerEvent.TransferClosed(t1.id), scope)
        assertEquals(1, f1.released.get())

        val f2 = file(10, "2")
        assertTrue(apiError { s.offerFiles(listOf(f2), listOf("Nobody"), false) }.status == 400)
        assertEquals(1, f2.released.get())
        eventually("endpoint closed") { !portOpen(lastPort) }

        val f3 = file(10, "3")
        s.offerFiles(listOf(f3), listOf("Tablet"), false)
        s.close()
        assertEquals(1, f3.released.get())
        assertFalse(portOpen(lastPort))
    }

    @Test fun cancelWithoutNetworkStillEndsLocally() = runBlocking {
        val f = file(10)
        val t = s.offerFiles(listOf(f), listOf("Tablet"), false)
        go.stop()
        s.cancel(t.id)
        assertTrue(s.offers.value.isEmpty())
        assertEquals(1, f.released.get())
        // the server learns about it on reconnect
        go.restart()
        s.onEvent(ServerEvent.Connected, scope)
        eventually("cancelled") { b.transfer(t.id).deliveries.single().status == Status.CANCELLED }
    }
}
