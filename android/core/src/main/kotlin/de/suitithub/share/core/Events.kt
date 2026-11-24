package de.suitithub.share.core

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.buffer
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.flow.flow
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

sealed interface ServerEvent {
    data object Connected : ServerEvent
    data object Disconnected : ServerEvent

    /** The token is no longer valid; nothing follows. */
    data object Unauthorised : ServerEvent
    data class OfferNew(val transfer: Transfer) : ServerEvent
    data class OfferRemoved(val id: String) : ServerEvent
    data class DeliveryUpdated(val update: DeliveryUpdate) : ServerEvent
    data class TransferClosed(val id: String) : ServerEvent
    data class Relay(val request: RelayRequest) : ServerEvent
}

/** Event stream; reconnects with growing pauses (base … 5 s) until cancelled or the token is rejected. */
fun Api.events(backoffBase: Duration = 1.seconds): Flow<ServerEvent> = flow {
    var backoff = backoffBase
    while (true) {
        var connected = false
        var unauthorised = false
        connectOnce().collect {
            if (it == ServerEvent.Connected) connected = true
            if (it == ServerEvent.Unauthorised) unauthorised = true
            emit(it)
        }
        if (unauthorised) return@flow
        if (connected) {
            emit(ServerEvent.Disconnected)
            backoff = backoffBase
        }
        delay(backoff)
        backoff = nextPause(backoff)
    }
}

/** The server cancels our file offers after 30 s without connection, so pauses stay well below that. */
internal fun nextPause(pause: Duration): Duration = minOf(pause * 2, 5.seconds)

private fun Api.connectOnce(): Flow<ServerEvent> = callbackFlow {
    val ws = client.newWebSocket(
        request("/v1/events").build(),
        object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                trySend(ServerEvent.Connected)
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                parseEvent(text)?.let { trySend(it) }
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(1000, null)
                close()
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                if (response?.code == 401) trySend(ServerEvent.Unauthorised)
                close()
            }
        },
    )
    awaitClose { ws.cancel() }
}.buffer(Channel.UNLIMITED)

/** Unknown types and malformed messages yield null. */
private fun parseEvent(text: String): ServerEvent? = try {
    val e = ShareJson.decodeFromString<Event>(text)
    when (e.type) {
        "offer.new" -> ServerEvent.OfferNew(ShareJson.decodeFromJsonElement(Transfer.serializer(), e.data))
        "offer.removed" -> ServerEvent.OfferRemoved(ShareJson.decodeFromJsonElement(TransferRef.serializer(), e.data).transferId)
        "transfer.closed" -> ServerEvent.TransferClosed(ShareJson.decodeFromJsonElement(TransferRef.serializer(), e.data).transferId)
        "delivery.updated" -> ServerEvent.DeliveryUpdated(ShareJson.decodeFromJsonElement(DeliveryUpdate.serializer(), e.data))
        "relay.request" -> ServerEvent.Relay(ShareJson.decodeFromJsonElement(RelayRequest.serializer(), e.data))
        else -> null
    }
} catch (_: IllegalArgumentException) {
    null
}
