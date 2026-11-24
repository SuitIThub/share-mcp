package de.suitithub.share.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.transformLatest
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.job
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.IOException
import java.time.Instant
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds
import kotlin.time.toJavaDuration

enum class Phase { RUNNING, DONE, FAILED }

/** An accepted file offer. */
data class Download(
    val transfer: Transfer,
    val phase: Phase = Phase.RUNNING,
    val bytes: Long = 0,
    val path: Path? = null,
    val refs: List<String> = emptyList(),
    val error: String? = null,
)

data class EngineState(
    val connected: Boolean = false,
    /** the token was revoked; the device has to be paired again */
    val unauthorised: Boolean = false,
    /** possible targets: without this device and without agents */
    val devices: List<Device> = emptyList(),
    /** names of all known devices by ID, for display */
    val names: Map<String, String> = emptyMap(),
    /** offers waiting for our answer */
    val incoming: List<Transfer> = emptyList(),
    val outgoing: List<Transfer> = emptyList(),
    val downloads: List<Download> = emptyList(),
)

sealed interface Notice {
    data class OfferArrived(val transfer: Transfer) : Notice
    data class OfferGone(val id: String) : Notice
    data class DownloadDone(val download: Download) : Notice
    data class DownloadFailed(val download: Download) : Notice

    /** An accepted text or link. */
    data class TextReceived(val transfer: Transfer) : Notice

    /** The user answered an offer that no longer exists. */
    data class Unavailable(val id: String) : Notice
}

class EngineOptions(
    val endpointPort: Int = 47800,
    val candidates: (Int) -> List<String> = ::lanCandidates,
    val probeTimeout: Duration = 1500.milliseconds,
    val idleTimeout: Duration = 30.seconds,
    val backoffBase: Duration = 1.seconds,
    /** how long the connection stays open after the last holder is gone */
    val linger: Duration = 2.seconds,
    val tick: Duration = 1.seconds,
    /** how often the device list is reloaded while someone is watching */
    val deviceRefresh: Duration = 15.seconds,
    val now: () -> Instant = Instant::now,
)

/** Everything the app does, as observable state. Safe to call from any thread. */
@OptIn(ExperimentalCoroutinesApi::class)
class Engine(
    val api: Api,
    private val store: DownloadStore,
    parent: CoroutineScope,
    private val opts: EngineOptions = EngineOptions(),
) {
    private val scope = CoroutineScope(parent.coroutineContext + SupervisorJob(parent.coroutineContext.job))
    private val sender = Sender(api, opts.endpointPort, opts.candidates)
    private val receiver = Receiver(api, store, opts.probeTimeout, opts.idleTimeout, opts.backoffBase)

    private val _state = MutableStateFlow(EngineState())
    val state: StateFlow<EngineState> = _state.asStateFlow()

    private val _notices = MutableSharedFlow<Notice>(extraBufferCapacity = 256, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    val notices: SharedFlow<Notice> = _notices.asSharedFlow()
    internal val noticeSubscribers get() = _notices.subscriptionCount.value

    private val holders = MutableStateFlow(0)
    private val lock = Any()
    private val jobs = HashMap<String, Job>() // running downloads
    private val answering = HashSet<String>() // texts being accepted
    private val aborted = HashSet<String>() // downloads the user cancelled; never resumed
    private val completed = HashSet<String>() // downloads that are complete here, whatever the server thinks
    private val refreshing = Mutex()

    /** Own file offers or running downloads: the app has to stay alive. */
    val hasWork: StateFlow<Boolean> = combine(sender.offers, _state) { offers, s ->
        offers.values.any { it.kind == Kind.FILES } || s.downloads.any { it.phase == Phase.RUNNING }
    }.distinctUntilChanged().stateIn(scope, SharingStarted.Eagerly, false)

    /** Like [hasWork], but never lags behind. */
    val busy: Boolean
        get() = sender.offers.value.values.any { it.kind == Kind.FILES } || _state.value.downloads.any { it.phase == Phase.RUNNING }

    init {
        scope.launch { sender.offers.collect { o -> _state.update { s -> s.copy(outgoing = o.values.sortedBy { it.createdAt }) } } }
        scope.launch {
            combine(holders, hasWork) { h, w -> h > 0 || w }
                .distinctUntilChanged()
                .transformLatest { active ->
                    if (!active) delay(opts.linger)
                    emit(active)
                }
                .distinctUntilChanged()
                .collectLatest { active ->
                    if (!active) return@collectLatest
                    try {
                        api.events(opts.backoffBase).collect { onEvent(it) }
                    } finally {
                        _state.update { it.copy(connected = false) }
                    }
                }
        }
        scope.launch { watchDevices() }
        scope.launch {
            while (true) {
                delay(opts.tick)
                expire()
            }
        }
    }

    private fun learnName(id: String) {
        if (id !in _state.value.names) scope.launch { runCatchingIo { loadDevices() } }
    }

    private suspend fun watchDevices() {
        while (true) {
            delay(opts.deviceRefresh)
            if (holders.value > 0 && _state.value.connected) runCatchingIo { loadDevices() }
        }
    }

    /** Keeps the event connection open until closed. */
    fun hold(): AutoCloseable {
        holders.update { it + 1 }
        val open = AtomicBoolean(true)
        return AutoCloseable { if (open.getAndSet(false)) holders.update { it - 1 } }
    }

    fun deviceName(id: String): String = _state.value.names[id] ?: "Unbekanntes Gerät"

    private suspend fun onEvent(e: ServerEvent) {
        sender.onEvent(e, scope)
        when (e) {
            ServerEvent.Connected -> {
                _state.update { it.copy(connected = true, unauthorised = false) }
                scope.launch {
                    try {
                        refresh()
                    } catch (_: IOException) {
                        // connection lost again
                    }
                }
            }
            ServerEvent.Disconnected -> _state.update { it.copy(connected = false) }
            ServerEvent.Unauthorised -> _state.update { it.copy(connected = false, unauthorised = true) }
            is ServerEvent.OfferNew -> {
                learnName(e.transfer.senderId)
                arrive(e.transfer, notify = true)
            }
            is ServerEvent.OfferRemoved -> leave(e.id)
            is ServerEvent.DeliveryUpdated -> learnName(e.update.delivery.deviceId)
            else -> Unit
        }
    }

    private fun arrive(t: Transfer, notify: Boolean) {
        if (t.expiresAt <= opts.now() || t.senderId == api.account.deviceId) return
        var added = false
        _state.update { s ->
            added = s.incoming.none { it.id == t.id } && s.downloads.none { it.transfer.id == t.id }
            if (added) s.copy(incoming = (s.incoming + t).sortedBy { it.createdAt }) else s
        }
        if (added && notify) _notices.tryEmit(Notice.OfferArrived(t))
    }

    /** Takes an offer off the list without a notice. */
    private fun take(id: String) = _state.update { s -> s.copy(incoming = s.incoming.filter { it.id != id }) }

    private fun leave(id: String) {
        take(id)
        _notices.tryEmit(Notice.OfferGone(id))
    }

    private suspend fun loadDevices() {
        val all = api.devices()
        _state.update { s ->
            s.copy(
                devices = all.filter { it.id != api.account.deviceId && it.type != DeviceType.AGENT }.sortedBy { it.name.lowercase() },
                names = all.associate { it.id to it.name },
            )
        }
    }

    /** Loads devices and open offers; resumes downloads the user confirmed earlier. Throws IOException. */
    suspend fun refresh() = refreshing.withLock {
        try {
            loadDevices()
            val before = _state.value.incoming.map { it.id }
            val waiting = ArrayList<Transfer>()
            val confirmed = ArrayList<Transfer>()
            for (t in api.incoming()) {
                when (ownStatus(t.id)) {
                    Status.WAITING -> waiting += t
                    Status.ACCEPTED, Status.DOWNLOADING -> if (t.kind == Kind.FILES) confirmed += t
                    else -> Unit
                }
            }
            val openIds = waiting.map { it.id }.toSet()
            // only those known before loading: an offer arriving meanwhile is not in the list yet
            before.filter { it !in openIds }.forEach { id ->
                if (_state.value.incoming.any { it.id == id }) leave(id)
            }
            waiting.forEach { arrive(it, notify = true) }
            for (t in confirmed) {
                when {
                    synchronized(lock) { t.id in aborted } -> runCatchingIo { api.fail(t.id) }
                    synchronized(lock) { t.id in completed } -> runCatchingIo { api.delivered(t.id) }
                    else -> startDownload(t)
                }
            }
            withContext(Dispatchers.IO) {
                synchronized(lock) { store.retain(confirmed.map { it.id }.toSet() + jobs.keys) }
            }
        } catch (e: ApiException) {
            if (e.status == 401) _state.update { it.copy(unauthorised = true) }
            throw e
        }
    }

    private suspend fun ownStatus(id: String): Status? = try {
        val t = api.transfer(id)
        t.deliveries.firstOrNull { it.deviceId == api.account.deviceId }?.status
    } catch (e: ApiException) {
        if (e.status == 404) null else throw e
    }

    /** An offer that waits for our answer, or null if there is none (any more). Throws IOException. */
    suspend fun offerDetails(id: String): Transfer? {
        val t = try {
            api.transfer(id)
        } catch (e: ApiException) {
            if (e.status == 404 || e.status == 403) return null
            throw e
        }
        val own = t.deliveries.firstOrNull { it.deviceId == api.account.deviceId }
        if (own?.status != Status.WAITING || t.closed || t.expiresAt <= opts.now()) return null
        if (t.senderId !in _state.value.names) runCatchingIo { loadDevices() }
        arrive(t, notify = false)
        return t
    }

    suspend fun offerFiles(
        sources: List<FileSource>,
        targets: List<String>,
        broadcast: Boolean,
        message: String = "",
        minutes: Int = 10,
    ): Transfer = hold().use { sender.offerFiles(sources, targets, broadcast, message, minutes) }

    suspend fun offerText(kind: Kind, text: String, targets: List<String>, broadcast: Boolean): Transfer =
        sender.offerText(kind, text, targets, broadcast)

    suspend fun cancelOutgoing(id: String) = sender.cancel(id)

    /** Accepts an offer. Files download in the background; throws IOException if the server cannot be reached. */
    suspend fun accept(id: String) {
        if (synchronized(lock) { id in jobs || id in completed || !answering.add(id) }) return
        try {
            val known = _state.value.incoming.firstOrNull { it.id == id }
            val t = known ?: offerDetails(id)
            if (t == null) {
                // answered before (e.g. by a process that has died): carry on instead of complaining
                if (ownStatus(id) in listOf(Status.ACCEPTED, Status.DOWNLOADING)) refresh() else unavailable(id)
                return
            }
            if (t.kind == Kind.FILES) {
                startDownload(t)
                return
            }
            try {
                api.accept(id)
            } catch (e: ApiException) {
                if (!e.gone) throw e
                unavailable(id)
                return
            }
            take(id)
            _notices.tryEmit(Notice.TextReceived(t))
        } catch (e: ApiException) {
            if (!e.gone) throw e
            unavailable(id)
        } finally {
            synchronized(lock) { answering -= id }
        }
    }

    suspend fun decline(id: String) {
        try {
            api.decline(id)
            take(id)
        } catch (e: ApiException) {
            if (!e.gone) throw e
            unavailable(id)
        }
    }

    private fun unavailable(id: String) {
        take(id)
        _notices.tryEmit(Notice.Unavailable(id))
    }

    private fun startDownload(t: Transfer) {
        val job = synchronized(lock) {
            if (t.id in jobs || t.id in completed || t.id in aborted) return
            val j = scope.launch(Dispatchers.IO, CoroutineStart.LAZY) { runDownload(t) }
            jobs[t.id] = j
            j
        }
        _state.update { s ->
            s.copy(incoming = s.incoming.filter { it.id != t.id }, downloads = s.downloads.filter { it.transfer.id != t.id } + Download(t))
        }
        job.start()
    }

    private suspend fun runDownload(t: Transfer) {
        var shown = 0L
        var shownPath: Path? = null
        try {
            val refs = receiver.download(t) { bytes, path ->
                // a fast LAN download reports hundreds of times per second
                val now = System.nanoTime()
                if (path != shownPath || now - shown > 100_000_000 || bytes == t.totalSize) {
                    shown = now
                    shownPath = path
                    updateDownload(t.id) { it.copy(bytes = bytes, path = path) }
                }
            }
            synchronized(lock) {
                completed += t.id
                jobs -= t.id
            }
            end(t.id, Notice::DownloadDone) { it.copy(phase = Phase.DONE, bytes = t.totalSize, refs = refs) }
        } catch (e: CancellationException) {
            synchronized(lock) { jobs -= t.id }
            throw e
        } catch (e: OfferEndedException) {
            synchronized(lock) { jobs -= t.id }
            val d = _state.value.downloads.firstOrNull { it.transfer.id == t.id }
            if (d == null || d.bytes == 0L) {
                _state.update { s -> s.copy(downloads = s.downloads.filter { it.transfer.id != t.id }) }
                unavailable(t.id)
            } else {
                end(t.id, Notice::DownloadFailed) { it.copy(phase = Phase.FAILED, error = "Angebot beendet") }
            }
        } catch (e: DownloadFailedException) {
            synchronized(lock) { jobs -= t.id }
            end(t.id, Notice::DownloadFailed) { it.copy(phase = Phase.FAILED, error = e.userMessage) }
        } catch (e: Exception) {
            // storage layers throw all kinds of things; one download must not take the app down
            receiver.abort(t.id)
            synchronized(lock) { jobs -= t.id }
            end(t.id, Notice::DownloadFailed) { it.copy(phase = Phase.FAILED, error = "Speichern fehlgeschlagen") }
        }
    }

    private fun updateDownload(id: String, f: (Download) -> Download) =
        _state.update { s -> s.copy(downloads = s.downloads.map { if (it.transfer.id == id) f(it) else it }) }

    private fun end(id: String, notice: (Download) -> Notice, f: (Download) -> Download) {
        updateDownload(id, f)
        _state.value.downloads.firstOrNull { it.transfer.id == id }?.let { _notices.tryEmit(notice(it)) }
    }

    /** Aborts a running download for good. */
    fun cancelDownload(id: String) {
        val job = synchronized(lock) {
            val j = jobs[id] ?: return
            aborted += id
            j
        }
        scope.launch {
            job.cancelAndJoin()
            _state.update { s -> s.copy(downloads = s.downloads.filter { it.transfer.id != id }) }
            receiver.abort(id)
        }
    }

    /** Removes a finished or failed download from the list. */
    fun dismiss(id: String) =
        _state.update { s -> s.copy(downloads = s.downloads.filter { it.transfer.id != id || it.phase == Phase.RUNNING }) }

    private fun expire() {
        val now = opts.now()
        _state.value.incoming.filter { it.expiresAt <= now }.forEach { leave(it.id) }
        if (!_state.value.connected) {
            // normally the server ends offers; without it they must not keep the phone awake forever
            val limit = now.minus(10.minutes.toJavaDuration())
            sender.offers.value.values.filter { it.expiresAt < limit }.forEach { sender.drop(it.id) }
        }
    }

    /** Simulates losing the local path (tests). */
    internal fun loseLanForTest() = sender.stopEndpointForTest()

    /** Ends everything locally. Running downloads keep their partial state. */
    fun close() {
        scope.cancel()
        sender.close()
        api.close()
    }

    private suspend fun runCatchingIo(block: suspend () -> Unit) {
        try {
            block()
        } catch (_: IOException) {
        }
    }
}

/** The offer does not exist (for us) any more. */
private val ApiException.gone get() = status == 403 || status == 404 || status == 409 || status == 410
