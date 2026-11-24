package de.suitithub.share.core

import okhttp3.OkHttpClient
import okhttp3.tls.HeldCertificate
import java.security.KeyStore
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocketFactory
import javax.net.ssl.X509TrustManager

/** The device's self-signed TLS certificate; its fingerprint is what peers pin. */
class Identity private constructor(private val held: HeldCertificate) {
    val certPem: String = held.certificatePem()
    val keyPem: String = held.privateKeyPkcs8Pem()
    val fingerprint: String = fingerprint(held.certificate)

    fun serverSocketFactory(): SSLServerSocketFactory {
        val pw = CharArray(0)
        val ks = KeyStore.getInstance(KeyStore.getDefaultType()).apply {
            load(null, null)
            setKeyEntry("device", held.keyPair.private, pw, arrayOf(held.certificate))
        }
        val kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply { init(ks, pw) }
        return SSLContext.getInstance("TLS").apply { init(kmf.keyManagers, null, SecureRandom()) }.serverSocketFactory
    }

    companion object {
        fun generate(name: String): Identity {
            val now = System.currentTimeMillis()
            // the name is cosmetic; keep it free of characters that are special in distinguished names
            val cn = "share " + name.filter { it.isLetterOrDigit() || it == ' ' || it == '-' }.take(40).trim()
            return Identity(
                HeldCertificate.Builder()
                    .ecdsa256()
                    .commonName(cn.trim())
                    .validityInterval(now - TimeUnit.HOURS.toMillis(1), now + TimeUnit.DAYS.toMillis(20 * 365L))
                    .build(),
            )
        }

        fun load(certPem: String, keyPem: String) = Identity(HeldCertificate.decode(certPem + keyPem))
    }
}

internal fun fingerprint(cert: X509Certificate): String =
    MessageDigest.getInstance("SHA-256").digest(cert.encoded).toHex()

/** Accepts only a peer whose leaf certificate has exactly this fingerprint; host name and chain are irrelevant. */
fun OkHttpClient.Builder.pinTo(fingerprint: String): OkHttpClient.Builder {
    val tm = PinningTrustManager(fingerprint)
    val ctx = SSLContext.getInstance("TLS").apply { init(null, arrayOf(tm), SecureRandom()) }
    return sslSocketFactory(ctx.socketFactory, tm).hostnameVerifier { _, _ -> true }
}

private class PinningTrustManager(private val expected: String) : X509TrustManager {
    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = chain?.firstOrNull() ?: throw CertificateException("no certificate")
        if (fingerprint(leaf) != expected) throw CertificateException("certificate fingerprint mismatch")
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) =
        throw CertificateException("client certificates are not used")

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}
