package de.suitithub.share.core

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import kotlin.time.Duration.Companion.milliseconds

class EventsTest {
    private lateinit var go: GoServer

    @Before fun setUp() {
        go = GoServer()
    }

    @After fun tearDown() = go.close()

    @Test fun receivesOfferNew() = runBlocking {
        val a = Api(go.account("Pixel"))
        val b = Api(go.account("Tablet"))
        val ch = Channel<ServerEvent>(Channel.UNLIMITED)
        val job = launch(Dispatchers.Default) { b.events().collect { ch.send(it) } }
        withTimeout(5000) {
            assertEquals(ServerEvent.Connected, ch.receive())
            while (!a.devices().single { it.name == "Tablet" }.online) kotlinx.coroutines.delay(10)
            val t = a.createTransfer(CreateTransferRequest(Kind.TEXT, text = "hi", targets = listOf("Tablet"), broadcast = false)).transfer
            assertEquals("hi", (ch.receive() as ServerEvent.OfferNew).transfer.text)
            a.cancel(t.id)
            assertEquals(ServerEvent.OfferRemoved(t.id), ch.receive())
        }
        job.cancel()
    }

    @Test fun senderSeesDeliveryAndClose() = runBlocking {
        val a = Api(go.account("Pixel"))
        val b = Api(go.account("Tablet"))
        val ch = Channel<ServerEvent>(Channel.UNLIMITED)
        val job = launch(Dispatchers.Default) { a.events().collect { ch.send(it) } }
        withTimeout(5000) {
            assertEquals(ServerEvent.Connected, ch.receive())
            while (!b.devices().single { it.name == "Pixel" }.online) kotlinx.coroutines.delay(10)
            val t = a.createTransfer(CreateTransferRequest(Kind.TEXT, text = "hi", targets = listOf("Tablet"), broadcast = false)).transfer
            b.decline(t.id)
            assertEquals(Status.DECLINED, (ch.receive() as ServerEvent.DeliveryUpdated).update.delivery.status)
            assertEquals(ServerEvent.TransferClosed(t.id), ch.receive())
        }
        job.cancel()
    }

    @Test fun reconnectsAfterServerRestart() = runBlocking {
        val b = Api(go.account("Tablet"))
        val ch = Channel<ServerEvent>(Channel.UNLIMITED)
        val job = launch(Dispatchers.Default) { b.events(20.milliseconds).collect { ch.send(it) } }
        withTimeout(10000) {
            assertEquals(ServerEvent.Connected, ch.receive())
            go.stop()
            assertEquals(ServerEvent.Disconnected, ch.receive())
            go.restart()
            assertEquals(ServerEvent.Connected, ch.receive()) // failed attempts in between emit nothing
        }
        job.cancel()
    }

    @Test fun revokedTokenEndsFlow() = runBlocking {
        val acc = go.account("Tablet")
        val ch = Channel<ServerEvent>(Channel.UNLIMITED)
        val job = launch(Dispatchers.Default) { Api(acc).events(20.milliseconds).collect { ch.send(it) } }
        withTimeout(10000) {
            assertEquals(ServerEvent.Connected, ch.receive())
            Api(acc).deleteMe()
            go.restart()
            assertEquals(ServerEvent.Disconnected, ch.receive())
            assertEquals(ServerEvent.Unauthorised, ch.receive())
            job.join()
        }
    }

    @Test fun neverConnectedIsNotReportedAsDisconnected() = runBlocking {
        val acc = go.account("Tablet")
        go.stop()
        val got = java.util.concurrent.CopyOnWriteArrayList<ServerEvent>()
        val job = launch(Dispatchers.Default) { Api(acc).events(10.milliseconds).collect { got += it } }
        kotlinx.coroutines.delay(300)
        job.cancel()
        assertTrue(got.toString(), got.isEmpty())
    }

    @Test fun unknownOrBrokenMessagesAreIgnored() = runBlocking {
        val acc = go.account("Tablet")
        MockWebServer().use { srv ->
            srv.start()
            srv.enqueue(
                MockResponse.Builder().webSocketUpgrade(object : WebSocketListener() {
                    override fun onOpen(webSocket: WebSocket, response: Response) {
                        webSocket.send("""{"type":"neu","data":1}""")
                        webSocket.send("kaputt")
                        webSocket.send("""{"type":"offer.new","data":{"id":5}}""")
                        webSocket.send("""{"type":"offer.removed"}""")
                        webSocket.send("""{"type":"offer.removed","data":{"transfer_id":"t9"}}""")
                        webSocket.close(1000, null)
                    }
                }).build(),
            )
            srv.enqueue(MockResponse.Builder().code(401).build())
            val api = Api(acc.copy(serverUrl = srv.url("/").toString().trimEnd('/')))
            withTimeout(5000) {
                assertEquals(
                    listOf(ServerEvent.Connected, ServerEvent.OfferRemoved("t9"), ServerEvent.Disconnected, ServerEvent.Unauthorised),
                    api.events(10.milliseconds).toList(),
                )
            }
        }
    }
}
