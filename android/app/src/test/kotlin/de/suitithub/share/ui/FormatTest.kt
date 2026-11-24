package de.suitithub.share.ui

import de.suitithub.share.core.FileItem
import de.suitithub.share.core.Kind
import de.suitithub.share.core.Transfer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant

class FormatTest {
    private val now = Instant.parse("2026-11-24T10:00:00Z")

    private fun files(vararg f: Pair<String, Long>, message: String = "") =
        Transfer("t", "s", Kind.FILES, message = message, files = f.map { FileItem(it.first, it.second) }, expiresAt = now)

    private fun text(s: String, kind: Kind = Kind.TEXT, message: String = "") = Transfer("t", "s", kind, text = s, message = message, expiresAt = now)

    @Test fun sizes() {
        mapOf(
            0L to "0 B", 1L to "1 B", 1023L to "1023 B", 1024L to "1,0 KB", 1536L to "1,5 KB", 1024L * 1024 - 1 to "1,0 MB",
            12L * 1024 * 1024 to "12,0 MB", 5L * 1024 * 1024 * 1024 to "5,0 GB", 3L * 1024 * 1024 * 1024 * 1024 to "3,0 TB",
        ).forEach { (n, s) -> assertEquals(s, Format.size(n)) }
    }

    @Test fun remaining() {
        assertEquals("noch 9 Min", Format.remaining(now.plusSeconds(490), now))
        assertEquals("noch 10 Min", Format.remaining(now.plusSeconds(600), now))
        assertEquals("noch 2 Min", Format.remaining(now.plusSeconds(61), now))
        assertEquals("noch 60 s", Format.remaining(now.plusSeconds(60), now))
        assertEquals("noch 40 s", Format.remaining(now.plusSeconds(40), now))
        assertEquals("abgelaufen", Format.remaining(now, now))
        assertEquals("abgelaufen", Format.remaining(now.minusSeconds(5), now))
    }

    @Test fun bodies() {
        assertEquals("3 Dateien, 12,0 MB: a.txt, b.txt, c.txt", Format.offerBody(files("a.txt" to 4L * 1024 * 1024, "b.txt" to 4L * 1024 * 1024, "c.txt" to 4L * 1024 * 1024)))
        assertEquals("app.apk, 1,5 KB", Format.offerBody(files("app.apk" to 1536)))
        assertEquals("app.apk, 1,5 KB\nneue Version", Format.offerBody(files("app.apk" to 1536, message = "neue Version")))
        assertEquals("Hallo", Format.offerBody(text("Hallo")))
        assertEquals("a".repeat(100), Format.offerBody(text("a".repeat(100))))
        assertEquals("a".repeat(100) + "…", Format.offerBody(text("a".repeat(101))))
        assertEquals("https://x", Format.offerBody(text("https://x", Kind.LINK)))
        val emoji = Format.offerBody(text("a".repeat(99) + "😀😀"))
        assertEquals("a".repeat(99) + "😀…", emoji)
        val many = Format.offerBody(files(*(1..500).map { "datei$it.txt" to 1L }.toTypedArray()))
        assertTrue(many, many.length < 300 && many.startsWith("500 Dateien, 500 B: datei1.txt, ") && many.endsWith("…"))
    }

    @Test fun titles() {
        assertEquals("Datei von Desktop", Format.offerTitle(files("a" to 1), "Desktop"))
        assertEquals("2 Dateien von Desktop", Format.offerTitle(files("a" to 1, "b" to 1), "Desktop"))
        assertEquals("Text von Desktop", Format.offerTitle(text("x"), "Desktop"))
        assertEquals("Link von Desktop", Format.offerTitle(text("x", Kind.LINK), "Desktop"))
    }

    @Test fun names() {
        assertEquals("app.apk", Format.shortName(files("app.apk" to 1)))
        assertEquals("a.txt +2", Format.shortName(files("a.txt" to 1, "b" to 1, "c" to 1)))
        assertEquals("Text", Format.shortName(text("x")))
        assertEquals("Link", Format.shortName(text("x", Kind.LINK)))
    }

    @Test fun percent() {
        assertEquals(0, Format.percent(0, 100))
        assertEquals(43, Format.percent(43_500, 100_000))
        assertEquals(99, Format.percent(999, 1000)) // 100 only when complete
        assertEquals(100, Format.percent(1000, 1000))
        assertEquals(100, Format.percent(0, 0))
        assertEquals(50, Format.percent(Long.MAX_VALUE / 2, Long.MAX_VALUE))
    }

    @Test fun httpTextIsSentAsLink() {
        for (l in listOf("https://x.de", " https://x.de/a?b=c#d ")) assertEquals(Kind.LINK, Format.kindOf(l))
        for (t in listOf("hallo", "schau: https://x.de", "https://x.de und mehr", "x.de", "")) assertEquals(t, Kind.TEXT, Format.kindOf(t))
    }

    @Test fun onlyWebLinksOpen() {
        for (ok in listOf("https://x.de", "http://x", "HTTPS://X")) assertTrue(ok, Format.isWebLink(ok))
        for (bad in listOf("intent://x", "javascript:alert(1)", "file:///x", "content://x", "x.de", "", "https://", "https://a b")) assertTrue(bad, !Format.isWebLink(bad))
    }
}
