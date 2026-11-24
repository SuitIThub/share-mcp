package de.suitithub.share.core

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancelChildren
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import okhttp3.Call
import okhttp3.OkHttpClient
import okhttp3.Protocol
import okhttp3.Request
import okhttp3.Response
import java.io.IOException
import java.net.Proxy
import java.time.Instant
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds
import kotlin.time.TimeMark
import kotlin.time.TimeSource
import kotlin.time.toJavaDuration

/** The offer expired, was withdrawn or is otherwise over. */
class OfferEndedException(message: String) : IOException(message)

/** The download cannot succeed; userMessage is shown to the user. */
class DownloadFailedException(val userMessage: String, cause: Throwable? = null) : IOException(userMessage, cause)

/** Accepts a file offer and downloads it: LAN first, relay otherwise, resuming where it stopped. */
class Receiver(
    private val api: Api,
    private val store: DownloadStore,
    private val probeTimeout: Duration = 1500.milliseconds,
    private val idleTimeout: Duration = 30.seconds,
    private val backoffBase: Duration = 1.seconds,
    /** without any progress and contact to the server, this long after the offer's expiry */
    private val giveUpAfter: Duration = 10.minutes,
) {
    /**
     * Returns references to all files. Cancelling keeps the partial state for a later call.
     * onProgress gets the total received bytes and the current path.
     */
    suspend fun download(t: Transfer, onProgress: (Long, Path) -> Unit): List<String> = withContext(Dispatchers.IO) {
        try {
            val accepted = accept(t)
            val reporter = Reporter(t.id)
            val refs = coroutineScope {
                val reporting = launch { reporter.run() }
                val run = Run(t, accepted, reporter, onProgress)
                try {
                    run.all()
                } finally {
                    reporting.cancel()
                }
            }
            // without the server's answer the finished markers stay, so a later call only reports
            if (reportDelivered(t.id, reporter)) store.forget(t.id)
            refs
        } catch (e: OfferEndedException) {
            store.forget(t.id)
            throw e
        } catch (e: FatalException) {
            withContext(NonCancellable) { abort(t.id) }
            throw DownloadFailedException(e.userMessage, e.cause)
        }
    }

    /** Ends an accepted download for good: removes partial files and tells the server. */
    suspend fun abort(id: String) = withContext(Dispatchers.IO) {
        store.forget(id)
        withTimeoutOrNull(5.seconds) {
            try {
                api.fail(id)
            } catch (_: IOException) {
                // the server notices by itself
            }
        }
        Unit
    }

    private class FatalException(val userMessage: String, override val cause: Throwable? = null) : Exception(userMessage, cause)

    private suspend fun accept(t: Transfer): AcceptResponse {
        val retry = Backoff(t)
        while (true) {
            try {
                return api.accept(t.id)
            } catch (e: IOException) {
                when ((e as? ApiException)?.status) {
                    404, 409, 410 -> throw OfferEndedException(e.message.orEmpty())
                    401 -> throw DownloadFailedException("Dieses Gerät ist nicht mehr gekoppelt", e)
                }
                retry.pause(e)
            }
        }
    }

    /** Best effort: the files are complete, whatever the server says. */
    private suspend fun reportDelivered(id: String, reporter: Reporter): Boolean {
        withTimeoutOrNull(2.seconds) { reporter.reportPath() }
        var pause = backoffBase
        repeat(5) {
            try {
                api.delivered(id)
                return true
            } catch (e: IOException) {
                val status = (e as? ApiException)?.status
                if (status != null && status < 500) return true
                delay(pause)
                pause *= 2
            }
        }
        return false
    }

    /** Pauses between attempts; gives up when nothing has moved for long after the offer expired. */
    private inner class Backoff(private val t: Transfer) {
        private var pause = backoffBase
        private var lastContact: TimeMark = TimeSource.Monotonic.markNow()

        fun progressed() {
            pause = backoffBase
            contact()
        }

        fun contact() {
            lastContact = TimeSource.Monotonic.markNow()
        }

        suspend fun pause(cause: IOException) {
            if (lastContact.elapsedNow() > giveUpAfter && Instant.now() > t.expiresAt) {
                throw DownloadFailedException("Server nicht erreichbar", cause)
            }
            delay(pause)
            pause = minOf(pause * 2, backoffBase * 16)
        }
    }

    private inner class Run(val t: Transfer, val acc: AcceptResponse, val reporter: Reporter, val onProgress: (Long, Path) -> Unit) {
        private val lan: OkHttpClient? = if (acc.lanCandidates.isEmpty() || acc.certFingerprint.isEmpty()) null else OkHttpClient.Builder()
            .pinTo(acc.certFingerprint)
            .proxy(Proxy.NO_PROXY)
            .protocols(listOf(Protocol.HTTP_1_1))
            .connectTimeout(probeTimeout.toJavaDuration())
            .readTimeout(idleTimeout.toJavaDuration())
            .followRedirects(false)
            .build()

        suspend fun all(): List<String> {
            var done = 0L
            val refs = ArrayList<String>()
            for ((n, item) in t.files.withIndex()) {
                val part = store.part(t.id, n, item)
                refs += part.finishedRef ?: file(n, item, part, done)
                done += item.size
            }
            lan?.connectionPool?.evictAll()
            return refs
        }

        private suspend fun file(n: Int, item: FileItem, part: Part, before: Long): String {
            val retry = Backoff(t)
            while (true) {
                var have = local { part.size() }
                if (have > item.size) {
                    local { part.reset() }
                    have = 0
                }
                if (have == item.size) return local { part.finish() }
                try {
                    val base = probe()
                    val path = if (base != null) Path.LAN else Path.RELAY
                    val call = if (base != null) lanCall("$base/files/$n", have, idleTimeout) else api.relayCall(t.id, n, have, idleTimeout)
                    fetch(call, path, have, item.size, part) {
                        reporter.update(before + it, path)
                        onProgress(before + it, path)
                    }
                    if (local { part.size() } > have) retry.progressed()
                } catch (e: OfferEndedException) {
                    throw e
                } catch (e: DownloadFailedException) {
                    throw e
                } catch (e: IOException) {
                    currentCoroutineContext().ensureActive()
                    if (local { part.size() } > have) retry.progressed()
                    if (checkAlive()) retry.contact()
                    retry.pause(e)
                }
            }
        }

        /** Local storage errors are final; partial files are removed. */
        private suspend fun <T> local(block: () -> T): T = try {
            block()
        } catch (e: IOException) {
            throw FatalException("Speichern fehlgeschlagen (Speicher voll?)", e)
        }

        /** True: the server says the delivery goes on. False: no contact. Throws when it is over. */
        private suspend fun checkAlive(): Boolean {
            val now = try {
                api.transfer(t.id)
            } catch (e: ApiException) {
                if (e.status == 404) throw OfferEndedException("unknown offer")
                if (e.status == 401) throw DownloadFailedException("Dieses Gerät ist nicht mehr gekoppelt", e)
                return false
            } catch (_: IOException) {
                return false
            }
            if (now.closed) throw OfferEndedException("offer closed")
            now.deliveries.firstOrNull { it.status.isFinal }?.let { throw OfferEndedException("delivery ${it.status}") }
            return true
        }

        private fun lanCall(url: String, offset: Long, timeout: Duration): Call {
            val r = Request.Builder().url(url).header("Authorization", "Bearer ${acc.accessToken}")
            if (offset > 0) r.header("Range", "bytes=$offset-")
            return lan!!.newBuilder().readTimeout(timeout.toJavaDuration()).build().newCall(r.build())
        }

        /** Base URL of the first LAN candidate that answers, or null. */
        private suspend fun probe(): String? {
            if (lan == null) return null
            val bases = acc.lanCandidates.map { "https://$it/t/${t.id}" }
            return withTimeoutOrNull(probeTimeout) {
                coroutineScope {
                    val answers = Channel<String?>(bases.size)
                    for (base in bases) launch {
                        answers.send(
                            try {
                                lanCall("$base/ping", 0, probeTimeout).await { r -> base.takeIf { r.code == 204 } }
                            } catch (_: IOException) {
                                null
                            } catch (_: IllegalArgumentException) { // malformed candidate
                                null
                            },
                        )
                    }
                    var winner: String? = null
                    for (i in bases.indices) {
                        winner = answers.receive()
                        if (winner != null) break
                    }
                    coroutineContext.cancelChildren()
                    winner
                }
            }
        }

        private suspend fun fetch(call: Call, path: Path, offset: Long, size: Long, part: Part, progress: (Long) -> Unit) = cancellable(call) {
            val resp = call.await()
            if (path == Path.LAN && (resp.code == 409 || resp.code == 410)) {
                val changed = resp.code == 409
                resp.close()
                throw FatalException(if (changed) "Datei wurde beim Sender geändert" else "Datei ist beim Sender nicht mehr lesbar")
            }
            resp.checkRange(offset).use { copy(it, offset, size, part, progress) }
        }

        private suspend fun copy(resp: Response, offset: Long, size: Long, part: Part, progress: (Long) -> Unit) {
            val input = resp.body.byteStream()
            val out = local { part.append() }
            var closed = false
            try {
                val buf = ByteArray(BUFFER)
                var got = offset
                while (got < size) {
                    val n = input.read(buf, 0, minOf(buf.size.toLong(), size - got).toInt())
                    if (n < 0) throw IOException("stream ended early")
                    local { out.write(buf, 0, n) }
                    got += n
                    progress(got)
                }
                closed = true
                local { out.close() }
            } finally {
                if (!closed) {
                    try {
                        out.close()
                    } catch (_: IOException) {
                        // the size check decides what is left
                    }
                }
            }
        }
    }

    /** Runs a blocking transfer; cancelling the coroutine aborts the call. */
    private suspend fun cancellable(call: Call, block: suspend CoroutineScope.() -> Unit) = coroutineScope {
        val watch = launch(Dispatchers.Default) {
            try {
                awaitCancellation()
            } finally {
                call.cancel()
            }
        }
        try {
            block()
        } finally {
            watch.cancel()
        }
    }

    /** Reports progress to the server at most once per second, never slowing the download. */
    private inner class Reporter(private val id: String) {
        private val changed = Channel<Unit>(Channel.CONFLATED)
        @Volatile private var latest: Pair<Long, Path>? = null
        @Volatile private var sent: Pair<Long, Path>? = null

        fun update(bytes: Long, path: Path) {
            latest = bytes to path
            changed.trySend(Unit)
        }

        suspend fun run() {
            while (true) {
                changed.receive()
                flush()
                delay(1.seconds)
            }
        }

        /** The server fills in the bytes on delivery; only the path must have been reported once. */
        suspend fun reportPath() {
            if (sent?.second != latest?.second) flush()
        }

        private suspend fun flush() {
            val l = latest ?: return
            if (l == sent) return
            try {
                api.progress(id, l.first, l.second)
                sent = l
            } catch (_: IOException) {
                // purely informational
            }
        }
    }
}
