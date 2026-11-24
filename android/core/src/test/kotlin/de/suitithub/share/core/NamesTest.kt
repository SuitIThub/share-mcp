package de.suitithub.share.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class NamesTest {
    @Test fun safeNames() {
        mapOf(
            "../x" to "x", "../evil.txt" to "evil.txt", "a/b.txt" to "b.txt", "a\\b.txt" to "b.txt",
            "CON" to "_CON", "con.txt" to "_con.txt", "lpt1.log" to "_lpt1.log", "" to "file", ".." to "file",
            "a:b?.txt" to "a_b_.txt", "x:y?.txt" to "x_y_.txt", "x. " to "x", "  .hidden. " to ".hidden",
            "app.apk" to "app.apk", "a\u0000b\nc" to "a_b_c", "ümläut ß.txt" to "ümläut ß.txt",
        ).forEach { (i, o) -> assertEquals(o, safeFileName(i)) }
    }

    @Test fun longNameKeepsExtensionAndNeverSplitsSurrogates() {
        val out = safeFileName("😀".repeat(150) + ".txt")
        assertTrue(out.toByteArray().size <= 200)
        assertTrue(out, out.endsWith(".txt"))
        assertEquals(out, String(out.toByteArray())) // unpaired surrogates would not survive
        val ascii = safeFileName("a".repeat(300) + ".apk")
        assertEquals(200, ascii.length)
        assertTrue(ascii.endsWith(".apk"))
    }

    @Test fun uniqueNames() {
        assertEquals("a.txt", uniqueName("a.txt") { false })
        assertEquals("a (1).txt", uniqueName("a.txt") { it == "a.txt" })
        assertEquals("a (2).txt", uniqueName("a.txt") { it == "a.txt" || it == "a (1).txt" })
        assertEquals("noext (1)", uniqueName("noext") { it == "noext" })
        assertEquals(".hidden (1)", uniqueName(".hidden") { it == ".hidden" })
    }
}
