package de.suitithub.share.core

import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLSocket
import kotlin.concurrent.thread
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/**
 * Minimal HTTPS server for running offers: GET /t/{id}/ping and GET /t/{id}/files/{n}, one request per connection.
 * lookup may block briefly; it is called on the connection's own thread.
 */
class LocalEndpoint(
    private val identity: Identity,
    private val preferredPort: Int = 47800,
    private val headerTimeout: Duration = 10.seconds,
    private val lookup: (String) -> Offer?,
) : AutoCloseable {
    private class Running(val server: ServerSocket, val timer: ScheduledExecutorService) {
        /** raw sockets and the offer they are serving ("" until authorised) */
        val sockets = ConcurrentHashMap<Socket, String>()
    }

    private var current: Running? = null

    val running: Boolean @Synchronized get() = current != null

    /** Starts listening (no-op if running) and returns the actual port. */
    @Synchronized fun start(): Int {
        current?.let { return it.server.localPort }
        val server = try {
            ServerSocket(preferredPort)
        } catch (_: IOException) {
            ServerSocket(0)
        }
        val r = Running(server, Executors.newSingleThreadScheduledExecutor { Thread(it, "endpoint-timer").apply { isDaemon = true } })
        current = r
        thread(name = "endpoint-accept", isDaemon = true) { acceptLoop(r) }
        return server.localPort
    }

    @Synchronized override fun close() {
        val r = current ?: return
        current = null
        r.server.close()
        r.timer.shutdownNow()
        r.sockets.keys.forEach { it.closeQuietly() }
    }

    /** Ends running downloads of one offer. */
    @Synchronized fun closeConnections(transferId: String) {
        current?.sockets?.forEach { (s, id) -> if (id == transferId) s.closeQuietly() }
    }

    private fun acceptLoop(r: Running) {
        while (true) {
            val raw = try {
                r.server.accept()
            } catch (_: IOException) {
                if (r.server.isClosed) return
                Thread.sleep(50) // e.g. out of file descriptors
                continue
            }
            if (r.sockets.size >= MAX_CONNECTIONS) {
                raw.closeQuietly()
                continue
            }
            r.sockets[raw] = ""
            thread(name = "endpoint-conn", isDaemon = true) {
                try {
                    serve(r, raw)
                } catch (_: IOException) {
                    // peer went away, junk, or closed by us
                } catch (_: RuntimeException) {
                    // a source failing unexpectedly must not take the app down
                } finally {
                    r.sockets.remove(raw)
                    raw.closeQuietly()
                }
            }
            if (r.server.isClosed) raw.closeQuietly() // lost the race against close()
        }
    }

    private fun serve(r: Running, raw: Socket) {
        // hard deadline for handshake and request; closing the raw socket cannot block, unlike closing the TLS layer
        val deadline = try {
            r.timer.schedule({ raw.closeQuietly() }, headerTimeout.inWholeMilliseconds, TimeUnit.MILLISECONDS)
        } catch (_: java.util.concurrent.RejectedExecutionException) {
            return
        }
        raw.tcpNoDelay = true
        val tls = identity.sslContext().socketFactory.createSocket(raw, null, raw.port, true) as SSLSocket
        tls.useClientMode = false
        val out = tls.outputStream
        val req = readRequest(tls.inputStream)
        if (req == null) {
            out.respond(431)
        } else {
            handle(r, raw, req, out) { deadline.cancel(false) }
        }
        out.flush()
        tls.close()
    }

    private class Req(val method: String, val path: String, val headers: Map<String, String>)

    /** null: header too large. */
    private fun readRequest(input: InputStream): Req? {
        val buf = ByteArray(MAX_HEADER)
        var n = 0
        while (true) {
            if (n == buf.size) return null
            val k = input.read(buf, n, buf.size - n)
            if (k < 0) throw IOException("closed")
            val from = maxOf(0, n - 3)
            n += k
            val end = (from..n - 4).firstOrNull { buf[it] == CR && buf[it + 1] == LF && buf[it + 2] == CR && buf[it + 3] == LF } ?: continue
            val lines = String(buf, 0, end, Charsets.ISO_8859_1).split("\r\n")
            val parts = lines[0].split(' ')
            val headers = lines.drop(1).filter { ':' in it }.associate { it.substringBefore(':').trim().lowercase() to it.substringAfter(':').trim() }
            return Req(parts[0], parts.getOrElse(1) { "" }, headers)
        }
    }

    private fun handle(r: Running, raw: Socket, req: Req, out: OutputStream, authorised: () -> Unit) {
        if (req.method != "GET") return out.respond(405)
        val m = PATH.matchEntire(req.path) ?: return out.respond(404)
        val id = m.groupValues[1]
        val offer = lookup(id)?.takeIf { it.transfer.kind == Kind.FILES } ?: return out.respond(404)
        val token = req.headers["authorization"].orEmpty().removePrefix("Bearer ")
        if (AccessToken.verify(offer.secret, id, token) == null) return out.respond(401)
        authorised()
        r.sockets[raw] = id
        if (m.groupValues[2] == "ping") return out.respond(204)
        val src = m.groupValues[3].toIntOrNull()?.let { offer.sources.getOrNull(it) } ?: return out.respond(404)
        if (!src.unchanged()) return out.respond(409)
        val size = src.item.size
        val range = req.headers["range"]
        val offset = if (range == null) 0 else RANGE.matchEntire(range)?.groupValues?.get(1)?.toLongOrNull()?.takeIf { it < size }
            ?: return out.respond(416, "Content-Range: bytes */$size\r\n")
        val input = try {
            src.openAt(offset)
        } catch (_: IOException) {
            return out.respond(410)
        }
        input.use {
            val length = size - offset
            val extra = "Content-Type: ${src.item.mime.filter { c -> c.code in 33..126 }.ifEmpty { "application/octet-stream" }}\r\nAccept-Ranges: bytes\r\n"
            if (range == null) out.respond(200, extra, length)
            else out.respond(206, extra + "Content-Range: bytes $offset-${size - 1}/$size\r\n", length)
            val buf = ByteArray(BUFFER)
            var left = length
            while (left > 0) {
                val n = it.read(buf, 0, minOf(left, buf.size.toLong()).toInt())
                if (n < 0) {
                    raw.closeQuietly() // never let a truncated file look complete
                    throw IOException("source shorter than offered")
                }
                out.write(buf, 0, n)
                left -= n
            }
        }
    }

    private fun OutputStream.respond(code: Int, extra: String = "", length: Long = 0) {
        val len = if (code == 204) "" else "Content-Length: $length\r\n"
        write("HTTP/1.1 $code ${REASONS[code]}\r\n${len}${extra}Connection: close\r\n\r\n".toByteArray(Charsets.ISO_8859_1))
    }

    private companion object {
        const val MAX_CONNECTIONS = 32
        const val MAX_HEADER = 16 * 1024
        const val CR = '\r'.code.toByte()
        const val LF = '\n'.code.toByte()
        val PATH = Regex("/t/([^/]+)/(ping|files/([^/]+))")
        val RANGE = Regex("bytes=(\\d+)-")
        val REASONS = mapOf(
            200 to "OK", 204 to "No Content", 206 to "Partial Content", 401 to "Unauthorized", 404 to "Not Found", 405 to "Method Not Allowed",
            409 to "Conflict", 410 to "Gone", 416 to "Range Not Satisfiable", 431 to "Request Header Fields Too Large",
        )
    }
}

private fun Socket.closeQuietly() {
    try {
        close()
    } catch (_: IOException) {
    }
}
