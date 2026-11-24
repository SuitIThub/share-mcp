package de.suitithub.share.service

import android.app.Service
import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.Uri
import android.net.wifi.WifiManager
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.ServiceCompat
import de.suitithub.share.R
import de.suitithub.share.core.ApiException
import de.suitithub.share.core.Engine
import de.suitithub.share.core.EngineState
import de.suitithub.share.data.ContentSource
import de.suitithub.share.data.SourceException
import de.suitithub.share.shareApp
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.IOException

/**
 * Keeps the process alive while files are offered or downloaded. Also runs the notification actions and the opening of
 * shared items, whose temporary read permission is bound to this service.
 */
class TransferService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var operations = 0
    private var lastStartId = 0

    /** The system does not let us run (any longer); only opening the app helps. */
    private var refused = false
    private var watching: Job? = null
    private var engine: Engine? = null
    private var held: AutoCloseable? = null
    private var wake: PowerManager.WakeLock? = null
    private var wifi: WifiManager.WifiLock? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        running = true
        starts++
        wake = getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "share:transfer").apply { acquire(MAX_WAKE_MS) }
        @Suppress("DEPRECATION") // the low latency mode works only with the screen on
        wifi = applicationContext.getSystemService(WifiManager::class.java).createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "share:transfer").apply { acquire() }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        lastStartId = startId
        val e = shareApp.engine.value
        try {
            ServiceCompat.startForeground(
                this, Notifications.PROGRESS, shareApp.notifications.progress(e?.state?.value ?: EngineState()), ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC,
            )
        } catch (ex: IllegalStateException) { // not allowed from the background (any more)
            refused = true
            shareApp.tell(getString(R.string.open_app_to_continue))
            stopSelf(startId)
            return START_NOT_STICKY
        }
        shareApp.notifications.removeMessage(getString(R.string.open_app_to_continue))
        if (e == null) {
            stopIfIdle()
            return START_NOT_STICKY
        }
        operations++ // watching must not find the service idle before the action has started
        watch(e)
        val id = intent?.data?.schemeSpecificPart.orEmpty()
        when (intent?.action) {
            ACCEPT -> operation {
                e.accept(id)
                shareApp.notifications.removeOffer(id)
            }
            CANCEL -> operation {
                e.cancelDownload(id)
                e.cancelOutgoing(id)
            }
            OFFER -> operation(getString(R.string.send_failed)) { offer(e, intent) }
        }
        operations--
        stopIfIdle()
        return START_NOT_STICKY
    }

    private fun operation(title: String? = null, block: suspend () -> Unit) {
        operations++
        scope.launch {
            try {
                withContext(Dispatchers.Default) { block() }
            } catch (ex: IOException) {
                val text = when (ex) {
                    is SourceException -> ex.message.orEmpty()
                    is ApiException -> getString(R.string.server_refused, ex.message)
                    else -> getString(R.string.no_connection)
                }
                if (title != null) shareApp.tell(title, text) else shareApp.tell(text)
            } finally {
                operations--
                stopIfIdle()
            }
        }
    }

    private suspend fun offer(e: Engine, intent: Intent) {
        val clip = intent.clipData ?: return
        val uris = (0 until clip.itemCount).mapNotNull { clip.getItemAt(it).uri }
        val sources = ArrayList<ContentSource>()
        try {
            withContext(Dispatchers.IO) { uris.mapTo(sources) { ContentSource.open(this@TransferService, it) } }
        } catch (ex: Throwable) {
            sources.forEach { it.release() }
            throw ex
        }
        // from here on the engine releases the sources
        e.offerFiles(sources, intent.getStringArrayListExtra(TARGETS).orEmpty(), intent.getBooleanExtra(BROADCAST, false), intent.getStringExtra(MESSAGE).orEmpty())
    }

    /** Follows the engine: updates the notification and stops when there is nothing left to do. */
    private fun watch(e: Engine) {
        if (engine === e) return
        watching?.cancel()
        held?.close()
        engine = e
        held = e.hold()
        watching = scope.launch {
            launch { shareApp.engine.first { it !== e }.let { new -> scope.launch { engineChanged(new) } } }
            launch { e.hasWork.collect { if (!it) stopIfIdle() } }
            var shown: EngineState? = null
            while (true) {
                val s = e.state.value
                if (e.busy && (s != shown || s.outgoing.isNotEmpty())) {
                    shown = s
                    shareApp.notifications.updateProgress(s)
                }
                delay(1000)
            }
        }
    }

    /** The device was unpaired or paired again: the old engine's transfers are over. */
    private fun engineChanged(new: Engine?) {
        if (new != null) {
            watch(new)
        } else {
            watching?.cancel()
            held?.close()
            engine = null
        }
        stopIfIdle()
    }

    private fun stopIfIdle() {
        val e = shareApp.engine.value
        if (operations > 0 || (e != null && e === engine && e.busy)) return
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        stopSelf(lastStartId)
    }

    /** Android 15+ limits data sync services to some hours a day. */
    override fun onTimeout(startId: Int, fgsType: Int) {
        refused = true
        shareApp.tell(getString(R.string.open_app_to_continue))
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onDestroy() {
        running = false
        held?.close()
        scope.cancel()
        wake?.let { if (it.isHeld) it.release() }
        wifi?.let { if (it.isHeld) it.release() }
        super.onDestroy()
        // work may have begun while the service was already stopping
        if (!refused) shareApp.ensureService()
    }

    companion object {
        private const val ACCEPT = "de.suitithub.share.ACCEPT"
        private const val CANCEL = "de.suitithub.share.CANCEL"
        private const val OFFER = "de.suitithub.share.OFFER"
        private const val TARGETS = "targets"
        private const val BROADCAST = "broadcast"
        private const val MESSAGE = "message"
        private const val MAX_WAKE_MS = 6 * 60 * 60 * 1000L

        @Volatile var running = false
            private set

        /** How often the service was created (tests). */
        @Volatile var starts = 0
            private set

        private fun intent(context: Context, action: String, id: String) =
            Intent(context, TransferService::class.java).setAction(action).setData(Uri.fromParts("offer", id, null))

        fun acceptIntent(context: Context, id: String) = intent(context, ACCEPT, id)
        fun cancelIntent(context: Context, id: String) = intent(context, CANCEL, id)

        /** Offers shared items; hands the caller's temporary read permission on to the service. */
        fun offer(context: Context, uris: List<Uri>, targets: List<String>, broadcast: Boolean, message: String) {
            val clip = ClipData.newRawUri("files", uris.first())
            uris.drop(1).forEach { clip.addItem(ClipData.Item(it)) }
            val i = Intent(context, TransferService::class.java).setAction(OFFER)
                .putStringArrayListExtra(TARGETS, ArrayList(targets)).putExtra(BROADCAST, broadcast).putExtra(MESSAGE, message)
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            i.clipData = clip
            try {
                context.startForegroundService(i)
            } catch (_: SecurityException) { // an item we were never given access to
                context.shareApp.tell(context.getString(R.string.send_failed), context.getString(R.string.share_rejected))
            }
        }

        /** Makes sure the service runs while the engine has work. */
        fun keepAlive(context: Context) {
            if (!running) context.startForegroundService(Intent(context, TransferService::class.java))
        }
    }
}
