package de.suitithub.share

import android.app.Application
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.Handler
import android.os.Looper
import android.widget.Toast
import de.suitithub.share.core.Account
import de.suitithub.share.core.Api
import de.suitithub.share.core.Engine
import de.suitithub.share.core.Notice
import de.suitithub.share.core.Phase
import de.suitithub.share.data.ContentSource
import de.suitithub.share.data.MediaStoreDownloads
import de.suitithub.share.data.SecureStore
import de.suitithub.share.service.Notifications
import de.suitithub.share.service.TransferService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

class ShareApp : Application() {
    /** Lives as long as the process. */
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    lateinit var notifications: Notifications
        private set

    private lateinit var store: SecureStore
    private val _engine = MutableStateFlow<Engine?>(null)
    private var observer: Job? = null
    private val visible = AtomicInteger()

    /** Null while the device is not paired. */
    val engine: StateFlow<Engine?> = _engine.asStateFlow()

    override fun onCreate() {
        super.onCreate()
        notifications = Notifications(this)
        store = SecureStore(this)
        ContentSource.tempDir(this).deleteRecursively() // no offer survives the process
        start(store.load())
    }

    /** Pairs (or with null: unpairs) this device. */
    @Synchronized fun setAccount(a: Account?) {
        if (a != null) store.save(a) else store.clear()
        start(a)
    }

    @Synchronized private fun start(a: Account?) {
        observer?.cancel()
        _engine.value?.close()
        val e = a?.let { Engine(Api(it), MediaStoreDownloads(this), scope) }
        // undispatched: nothing may happen before we listen
        observer = e?.let { scope.launch(start = CoroutineStart.UNDISPATCHED) { observe(it) } }
        _engine.value = e
    }

    private suspend fun observe(e: Engine) = kotlinx.coroutines.coroutineScope {
        launch(start = CoroutineStart.UNDISPATCHED) { e.notices.collect { onNotice(e, it) } }
        launch {
            var before = emptySet<String>()
            e.state.map { s -> s.incoming.map { it.id }.toSet() }.distinctUntilChanged().collect { now ->
                (before - now).forEach { notifications.removeOffer(it) }
                before = now
            }
        }
        launch {
            e.hasWork.collect { work ->
                if (work) try {
                    TransferService.keepAlive(this@ShareApp)
                } catch (_: IllegalStateException) { // not allowed from the background; it works as long as the process lives
                    notifications.message(getString(R.string.open_app_to_continue))
                }
            }
        }
    }

    private fun onNotice(e: Engine, n: Notice) {
        when (n) {
            is Notice.OfferArrived -> if (!uiVisible) notifications.offer(n.transfer, e.deviceName(n.transfer.senderId))
            is Notice.OfferGone -> notifications.removeOffer(n.id)
            is Notice.DownloadDone -> if (!uiVisible) notifications.result(n.download)
            is Notice.DownloadFailed -> if (!uiVisible) notifications.result(n.download)
            is Notice.TextReceived -> {
                copy(n.transfer.text)
                if (uiVisible) toast(getString(R.string.copied)) else notifications.text(n.transfer, e.deviceName(n.transfer.senderId))
            }
            is Notice.Unavailable -> {
                notifications.removeOffer(n.id)
                tell(getString(R.string.unavailable))
            }
        }
    }

    fun copy(text: String) =
        getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText(getString(R.string.app_name), text))

    val uiVisible get() = visible.get() > 0

    /** A screen that shows offers itself is visible until closed. */
    fun uiShown(): AutoCloseable {
        visible.incrementAndGet()
        val open = AtomicBoolean(true)
        return AutoCloseable { if (open.getAndSet(false)) visible.decrementAndGet() }
    }

    /** Tells the user something, wherever they are. */
    fun tell(title: String, text: String = "") {
        if (uiVisible || !notifications.enabled) toast(if (text.isEmpty()) title else "$title: $text") else notifications.message(title, text)
    }

    private fun toast(text: String) = Handler(Looper.getMainLooper()).post { Toast.makeText(this, text, Toast.LENGTH_LONG).show() }
}

val Context.shareApp: ShareApp get() = applicationContext as ShareApp
