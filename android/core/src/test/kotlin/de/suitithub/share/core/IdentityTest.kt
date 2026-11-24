package de.suitithub.share.core

import okhttp3.OkHttpClient
import okhttp3.Request
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException
import java.security.MessageDigest
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.security.interfaces.ECPublicKey
import java.time.Duration
import java.time.Instant
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket
import kotlin.concurrent.thread

class IdentityTest {
    private fun cert(i: Identity) =
        CertificateFactory.getInstance("X.509").generateCertificate(i.certPem.byteInputStream()) as X509Certificate

    @Test fun fingerprintIsSha256OfDer() {
        val i = Identity.generate("Pixel")
        assertTrue(i.fingerprint, Regex("[0-9a-f]{64}").matches(i.fingerprint))
        assertEquals(MessageDigest.getInstance("SHA-256").digest(cert(i).encoded).toHex(), i.fingerprint)
    }

    @Test fun survivesSaveAndLoad() {
        val i = Identity.generate("Pixel")
        assertEquals(i.fingerprint, Identity.load(i.certPem, i.keyPem).fingerprint)
    }

    @Test fun certIsLongLivedP256() {
        val c = cert(Identity.generate("Ümläut \"x\", O=y"))
        assertEquals(256, (c.publicKey as ECPublicKey).params.order.bitLength())
        assertTrue(c.notAfter.toInstant() > Instant.now() + Duration.ofDays(19 * 365))
        assertTrue(c.notBefore.toInstant() < Instant.now())
    }

    @Test fun pinnedClientAcceptsOnlyExactFingerprint() {
        val a = Identity.generate("a")
        val b = Identity.generate("b")
        TlsEcho(a).use { srv ->
            val ok = OkHttpClient.Builder().pinTo(a.fingerprint).build()
            ok.newCall(Request.Builder().url("https://127.0.0.1:${srv.port}/").build()).execute().use { assertEquals(204, it.code) }
            assertEquals(1, srv.requests)
            assertTrue(srv.protocols.all { it == "TLSv1.2" || it == "TLSv1.3" })
            val bad = OkHttpClient.Builder().pinTo(b.fingerprint).build()
            assertThrows(IOException::class.java) { bad.newCall(Request.Builder().url("https://127.0.0.1:${srv.port}/").build()).execute() }
            val plain = OkHttpClient()
            assertThrows(IOException::class.java) { plain.newCall(Request.Builder().url("https://127.0.0.1:${srv.port}/").build()).execute() }
            assertEquals(1, srv.requests)
        }
    }

    @Test fun negotiatesTls12OrNewer() {
        val socket = Identity.generate("a").serverSocketFactory().createServerSocket(0) as SSLServerSocket
        socket.use { assertFalse(it.enabledProtocols.any { p -> p == "TLSv1" || p == "TLSv1.1" || p == "SSLv3" }) }
    }

    /** Minimal TLS server answering every request with 204. */
    class TlsEcho(id: Identity) : AutoCloseable {
        private val server = id.serverSocketFactory().createServerSocket(0) as SSLServerSocket
        val port get() = server.localPort
        @Volatile var requests = 0
        val protocols = java.util.concurrent.CopyOnWriteArrayList<String>()

        init {
            thread(isDaemon = true) {
                while (true) {
                    val s = try { server.accept() as SSLSocket } catch (_: IOException) { break }
                    try {
                        s.soTimeout = 2000
                        val r = s.inputStream.bufferedReader()
                        if (r.readLine() != null) {
                            while (!r.readLine().isNullOrEmpty()) continue
                            requests++
                            protocols += s.session.protocol
                            s.outputStream.write("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n".toByteArray())
                        }
                        s.close()
                    } catch (_: IOException) {
                    }
                }
            }
        }

        override fun close() = server.close()
    }
}
