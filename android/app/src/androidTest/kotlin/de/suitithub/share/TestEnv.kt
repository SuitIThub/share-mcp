package de.suitithub.share

import android.app.Notification
import android.app.NotificationManager
import android.content.Context
import android.service.notification.StatusBarNotification
import androidx.test.platform.app.InstrumentationRegistry
import de.suitithub.share.core.Account
import de.suitithub.share.core.Api
import de.suitithub.share.core.DownloadStore
import de.suitithub.share.core.Engine
import de.suitithub.share.core.EngineOptions
import de.suitithub.share.core.FileItem
import de.suitithub.share.core.FileSource
import de.suitithub.share.core.Identity
import de.suitithub.share.core.Part
import de.suitithub.share.core.pair
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.runBlocking
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import kotlin.time.Duration.Companion.milliseconds

/** Talks to the share-server started by connected-test.sh. */
object TestEnv {
    private val args = InstrumentationRegistry.getArguments()
    val server: String = args.getString("server") ?: error("run ./connected-test.sh")
    val ctx: Context = InstrumentationRegistry.getInstrumentation().targetContext
    val app: ShareApp = ctx.shareApp
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var n = 0

    private val boot: Api by lazy { runBlocking { Api(pair(server, args.getString("code")!!, "Boot", Identity.generate("Boot"), allowInsecureLocal = true)) } }

    fun code(): String = runBlocking { boot.pairCode().code }

    @Synchronized fun uniqueName(prefix: String) = "$prefix${++n}"

    suspend fun account(prefix: String): Account {
        val name = uniqueName(prefix)
        return pair(server, code(), name, Identity.generate(name), allowInsecureLocal = true)
    }

    /** Pairs the app itself with a fresh account. */
    suspend fun pairApp(): Engine {
        app.setAccount(account("App"))
        return app.engine.value!!
    }

    val notifications: NotificationManager get() = ctx.getSystemService(NotificationManager::class.java)

    fun active(tag: String): StatusBarNotification? = notifications.activeNotifications.firstOrNull { it.tag == tag }
    fun titles(): List<String> = notifications.activeNotifications.map { it.notification.extras.getCharSequence(Notification.EXTRA_TITLE).toString() }

    fun eventually(what: String, seconds: Int = 15, cond: () -> Boolean) {
        val end = System.nanoTime() + seconds * 1_000_000_000L
        while (!cond()) {
            if (System.nanoTime() > end) throw AssertionError("timeout: $what; ${titles()} ${app.engine.value?.state?.value}")
            Thread.sleep(30)
        }
    }

    /** Another device, living in this process. */
    class Peer private constructor(val engine: Engine, val store: MemoryStore) {
        val account get() = engine.api.account

        companion object {
            fun create(prefix: String = "Peer"): Peer = runBlocking {
                val store = MemoryStore()
                val e = Engine(Api(account(prefix)), store, scope, EngineOptions(endpointPort = 0, candidates = { listOf("127.0.0.1:$it") }, backoffBase = 100.milliseconds))
                e.hold()
                eventually("peer connected") { e.state.value.connected }
                Peer(e, store)
            }
        }
    }

    class Bytes(private val data: ByteArray, name: String, private val delayMs: Long = 0) : FileSource {
        override val item = FileItem(name, data.size.toLong(), "application/octet-stream", Instant.EPOCH)
        override fun unchanged() = true
        override fun openAt(offset: Long): InputStream {
            val input = data.inputStream(offset.toInt(), data.size - offset.toInt())
            return if (delayMs == 0L) input else object : InputStream() {
                override fun read() = input.read()
                override fun read(b: ByteArray, off: Int, len: Int): Int {
                    Thread.sleep(delayMs)
                    return input.read(b, off, minOf(len, 50_000))
                }
            }
        }
    }

    class MemoryStore : DownloadStore {
        val files = ConcurrentHashMap<String, ByteArray>()
        private val parts = ConcurrentHashMap<String, ByteArrayOutputStream>()

        override fun part(transferId: String, index: Int, item: FileItem) = object : Part {
            private val buf get() = parts.getOrPut("$transferId-$index") { ByteArrayOutputStream() }
            override val finishedRef: String? get() = null
            override fun size() = buf.size().toLong()
            override fun append(): OutputStream = buf
            override fun reset() = buf.reset()
            override fun finish(): String {
                files[item.name] = buf.toByteArray()
                return item.name
            }
        }

        override fun forget(transferId: String) {
            parts.keys.removeIf { it.startsWith("$transferId-") }
        }

        override fun retain(transferIds: Set<String>) = Unit
    }
}
