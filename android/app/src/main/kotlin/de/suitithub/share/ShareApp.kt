package de.suitithub.share

import android.app.Application
import android.content.Context
import de.suitithub.share.core.Account
import de.suitithub.share.core.Api
import de.suitithub.share.core.Engine
import de.suitithub.share.data.ContentSource
import de.suitithub.share.data.MediaStoreDownloads
import de.suitithub.share.data.SecureStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

class ShareApp : Application() {
    /** Lives as long as the process. */
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    private lateinit var store: SecureStore
    private val _engine = MutableStateFlow<Engine?>(null)

    /** Null while the device is not paired. */
    val engine: StateFlow<Engine?> = _engine.asStateFlow()

    override fun onCreate() {
        super.onCreate()
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
        _engine.value?.close()
        _engine.value = a?.let { Engine(Api(it), MediaStoreDownloads(this), scope) }
    }
}

val Context.shareApp: ShareApp get() = applicationContext as ShareApp
