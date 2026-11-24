package de.suitithub.share.core

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.runInterruptible
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.IOException

/** Offers files, texts and links; serves files from the local endpoint or uploads them to the relay. */
class Sender(
    private val api: Api,
    endpointPort: Int = 47800,
    private val candidates: (Int) -> List<String> = ::lanCandidates,
) {
    private val lock = Any()
    private val own = HashMap<String, Offer>()
    private val relays = HashMap<String, MutableSet<Job>>()
    private val early = ArrayList<ServerEvent>() // events for offers whose create call has not returned yet
    private var pending = 0
    private var pendingFiles = 0
    private var closed = false

    /** Held while an offer is being created, so the orphan cleanup cannot mistake it for an orphan. */
    private val creating = Mutex()

    private val endpoint = LocalEndpoint(api.account.identity, endpointPort, lookup = ::lookup)

    private val _offers = MutableStateFlow<Map<String, Transfer>>(emptyMap())

    /** Our open offers with their current deliveries. */
    val offers: StateFlow<Map<String, Transfer>> = _offers.asStateFlow()

    suspend fun offerFiles(
        sources: List<FileSource>,
        targets: List<String>,
        broadcast: Boolean,
        message: String = "",
        minutes: Int = 10,
    ): Transfer {
        require(sources.isNotEmpty()) { "no files" }
        var registered = false
        try {
            return create(true) {
                val lan = withContext(Dispatchers.IO) {
                    try {
                        candidates(endpoint.start())
                    } catch (_: IOException) {
                        emptyList() // relay only
                    }
                }
                val r = api.createTransfer(
                    CreateTransferRequest(Kind.FILES, "", message, sources.map { it.item }, targets, broadcast, minutes.coerceIn(1, 10), lan),
                )
                register(r, sources).also { registered = true }
            }
        } finally {
            if (!registered) sources.forEach { it.release() }
        }
    }

    suspend fun offerText(kind: Kind, text: String, targets: List<String>, broadcast: Boolean): Transfer {
        require(text.isNotBlank()) { "empty text" }
        require(text.toByteArray().size <= MAX_TEXT_BYTES) { "text too long" }
        return create(false) {
            register(api.createTransfer(CreateTransferRequest(kind, text, targets = targets, broadcast = broadcast)), emptyList())
        }
    }

    private suspend fun create(files: Boolean, block: suspend () -> Transfer): Transfer {
        synchronized(lock) {
            pending++
            if (files) pendingFiles++
        }
        try {
            return creating.withLock { block() }
        } finally {
            synchronized(lock) {
                pending--
                if (files) pendingFiles--
                if (pending == 0) early.clear()
                stopEndpointIfIdle()
            }
        }
    }

    private fun register(r: CreateTransferResponse, sources: List<FileSource>): Transfer {
        val t = r.transfer
        val replay = synchronized(lock) {
            if (closed) throw IOException("closed")
            own[t.id] = Offer(t, r.secret.hexToBytesOrNull() ?: ByteArray(0), sources)
            _offers.update { it + (t.id to t) }
            early.filter { it.transferId() == t.id }.also { early.removeAll(it) }
        }
        replay.forEach { apply(it) }
        return t
    }

    /** Withdraws an offer. Ends locally even without a connection; the server then learns about it on reconnect. */
    suspend fun cancel(id: String) {
        remove(id)
        try {
            api.cancel(id)
        } catch (_: IOException) {
            // unknown/closed on the server, or no connection (see onConnected)
        }
    }

    /** Ends an offer locally only. */
    fun drop(id: String) = remove(id)

    suspend fun onEvent(e: ServerEvent, scope: CoroutineScope) {
        when (e) {
            is ServerEvent.Connected -> scope.launch {
                try {
                    onConnected()
                } catch (_: IOException) {
                    // connection lost again; the next Connected repeats this
                }
            }
            is ServerEvent.Relay -> serveRelay(e.request, scope)
            is ServerEvent.DeliveryUpdated, is ServerEvent.TransferClosed -> apply(e)
            else -> Unit
        }
    }

    private fun apply(e: ServerEvent) {
        val id = e.transferId() ?: return
        synchronized(lock) {
            if (id !in own) {
                if (pending > 0) early += e
                return
            }
        }
        when (e) {
            is ServerEvent.DeliveryUpdated -> updateDelivery(id, e.update.delivery)
            is ServerEvent.TransferClosed -> remove(id)
            else -> Unit
        }
    }

    private fun updateDelivery(id: String, d: Delivery) = _offers.update { m ->
        val t = m[id] ?: return@update m
        val known = t.deliveries.any { it.deviceId == d.deviceId }
        m + (id to t.copy(deliveries = if (known) t.deliveries.map { if (it.deviceId == d.deviceId) d else it } else t.deliveries + d))
    }

    /** Recovers what happened while disconnected. */
    private suspend fun onConnected() = creating.withLock {
        val known = synchronized(lock) { own.keys.toSet() }
        val open = api.outgoing().associateBy { it.id }
        for (id in known) {
            val t = open[id]
            if (t == null) remove(id) else _offers.update { if (id in it) it + (id to t) else it }
        }
        for (t in open.values) {
            if (t.id in known) continue
            if (t.kind == Kind.FILES) {
                // left over from a killed process or an offline cancel: nobody can serve these files any more
                api.cancel(t.id)
            } else {
                synchronized(lock) {
                    if (closed) return@withLock
                    own[t.id] = Offer(t, ByteArray(0), emptyList())
                    _offers.update { it + (t.id to t) }
                }
            }
        }
    }

    /** Waits briefly for an offer that may still be registering: receivers hear about it before our create call returns. */
    private fun lookup(id: String): Offer? {
        repeat(40) {
            synchronized(lock) {
                own[id]?.let { return it }
                if (pending == 0) return null
            }
            Thread.sleep(50)
        }
        return null
    }

    private fun serveRelay(r: RelayRequest, scope: CoroutineScope) {
        val job = scope.launch(Dispatchers.IO) {
            try {
                val offer = runInterruptible { lookup(r.transferId) } ?: return@launch
                val src = offer.sources.getOrNull(r.fileIndex) ?: return@launch
                if (r.offset !in 0..src.item.size || !src.unchanged()) return@launch
                api.relayUpload(r.relayId, src.item.size - r.offset) { src.openAt(r.offset) }
            } catch (_: IOException) {
                // the receiver resumes
            }
        }
        synchronized(lock) { relays.getOrPut(r.transferId) { HashSet() } += job }
        job.invokeOnCompletion {
            synchronized(lock) {
                relays[r.transferId]?.let { jobs ->
                    jobs -= job
                    if (jobs.isEmpty()) relays -= r.transferId
                }
            }
        }
    }

    private fun remove(id: String) {
        val (offer, jobs) = synchronized(lock) {
            val o = own.remove(id) ?: return
            _offers.update { it - id }
            endpoint.closeConnections(id)
            stopEndpointIfIdle()
            o to relays.remove(id).orEmpty().toList()
        }
        jobs.forEach { it.cancel() }
        offer.sources.forEach { it.release() }
    }

    private fun stopEndpointIfIdle() {
        if (pendingFiles == 0 && own.values.none { it.transfer.kind == Kind.FILES }) endpoint.close()
    }

    /** Ends all offers locally. */
    fun close() {
        val ids = synchronized(lock) {
            closed = true
            own.keys.toList()
        }
        ids.forEach { remove(it) }
        endpoint.close()
    }
}

private fun ServerEvent.transferId(): String? = when (this) {
    is ServerEvent.DeliveryUpdated -> update.transferId
    is ServerEvent.TransferClosed -> id
    else -> null
}
