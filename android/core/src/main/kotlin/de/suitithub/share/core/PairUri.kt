package de.suitithub.share.core

import java.net.URLDecoder
import java.net.URLEncoder

/** Content of the pairing QR code: share://pair?code=…&url=… */
data class PairUri(val url: String, val code: String) {
    override fun toString() = PREFIX + "code=" + enc(code) + "&url=" + enc(url)

    companion object {
        private const val PREFIX = "share://pair?"

        fun parse(s: String): PairUri? {
            if (!s.startsWith(PREFIX)) return null
            val q = try {
                s.removePrefix(PREFIX).split('&').associate { it.substringBefore('=') to URLDecoder.decode(it.substringAfter('=', ""), "UTF-8") }
            } catch (_: IllegalArgumentException) {
                return null
            }
            val code = q["code"].orEmpty().trim().uppercase()
            val url = q["url"].orEmpty().trim()
            return if (code.isEmpty() || url.isEmpty()) null else PairUri(url, code)
        }

        private fun enc(s: String) = URLEncoder.encode(s, "UTF-8")
    }
}
