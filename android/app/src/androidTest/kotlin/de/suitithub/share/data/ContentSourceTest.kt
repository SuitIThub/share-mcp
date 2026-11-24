package de.suitithub.share.data

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import de.suitithub.share.TestProvider
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.IOException
import java.util.concurrent.Executors

@RunWith(AndroidJUnit4::class)
class ContentSourceTest {
    private val ctx = InstrumentationRegistry.getInstrumentation().targetContext
    private val temp = ContentSource.tempDir(ctx)
    private val name = "bild 1.jpg"
    private val size = 300_000
    private val bytes = TestProvider.bytes(size, name)

    @Before fun setUp() {
        temp.deleteRecursively()
        touch("fresh")
    }

    private fun touch(mode: String) = ctx.contentResolver.openFileDescriptor(TestProvider.uri(mode, size, name), "r")!!.close()

    private fun open(mode: String, n: String = name) = ContentSource.open(ctx, TestProvider.uri(mode, size, n))

    private fun tempFiles() = temp.listFiles().orEmpty().size

    private fun check(s: ContentSource) {
        assertEquals(size.toLong(), s.item.size)
        assertArrayEquals(bytes, s.openAt(0).use { it.readBytes() })
        assertArrayEquals(bytes.copyOfRange(1000, bytes.size), s.openAt(1000).use { it.readBytes() })
        assertEquals(0, s.openAt(size.toLong()).use { it.readBytes() }.size)
        assertTrue(s.unchanged())
    }

    @Test fun readsNameSizeMimeAndSeeks() {
        val s = open("plain")
        assertEquals(name, s.item.name)
        assertEquals("image/jpeg", s.item.mime)
        check(s)
        assertEquals(0, tempFiles())
        s.release()
    }

    @Test fun missingOrWrongSizeUsesTheRealOne() {
        for (m in listOf("nosize", "wrongsize", "nullcursor")) open(m).let {
            check(it)
            it.release()
        }
        assertEquals(0, tempFiles())
    }

    @Test fun missingNameIsDerived() {
        val s = open("noname", "12345")
        assertEquals("12345.jpg", s.item.name)
        s.release()
    }

    @Test fun unseekablePipeFallsBackToTempCopy() {
        val s = open("pipe")
        assertEquals(1, tempFiles())
        check(s)
        s.release()
        assertEquals(0, tempFiles())
        s.release()
    }

    @Test fun parallelReadersDoNotDisturbEachOther() {
        val s = open("plain")
        val pool = Executors.newFixedThreadPool(8)
        (0 until 8).map { i -> pool.submit<ByteArray> { s.openAt(i * 1000L).use { it.readBytes() } } }
            .forEachIndexed { i, f -> assertArrayEquals(bytes.copyOfRange(i * 1000, bytes.size), f.get()) }
        pool.shutdown()
        s.release()
    }

    @Test fun changedContentIsDetected() {
        val s = open("plain")
        Thread.sleep(20)
        touch("change")
        assertFalse(s.unchanged())
        s.release()
    }

    @Test fun staysReadableWithoutPermission() {
        val s = open("once", "once-${System.nanoTime()}.bin")
        assertEquals(size, s.openAt(0).use { it.readBytes() }.size)
        assertEquals(size, s.openAt(0).use { it.readBytes() }.size)
        s.release()
    }

    @Test fun readingAfterReleaseIsAnIoError() {
        val s = open("plain")
        s.release()
        assertThrows(IOException::class.java) { s.openAt(0).use { it.readBytes() } }
        assertFalse(s.unchanged())
    }

    @Test fun errorsAreReadable() {
        for (mode in listOf("denied", "broken", "missing")) {
            val e = assertThrows(mode, SourceException::class.java) { open(mode) }
            assertTrue(e.message, e.message!!.contains(name) && e.message!!.contains("gelesen"))
        }
        val noGrant = TestProvider.uri(TestProvider.GUARDED, "plain", size, name)
        for (uri in listOf(noGrant, android.net.Uri.fromFile(File(ctx.filesDir, "account.bin")), android.net.Uri.parse("content://no.such.provider/x"))) {
            assertThrows(SourceException::class.java) { ContentSource.open(ctx, uri) }
        }
        assertEquals(0, tempFiles())
    }
}
