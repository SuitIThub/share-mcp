package de.suitithub.share.data

import android.net.Uri
import android.provider.MediaStore
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import de.suitithub.share.core.FileItem
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import kotlin.random.Random

@RunWith(AndroidJUnit4::class)
class MediaStoreDownloadsTest {
    private val ctx = InstrumentationRegistry.getInstrumentation().targetContext
    private val cr = ctx.contentResolver
    private fun store() = MediaStoreDownloads(ctx, "downloads-test")
    private val bytes = Random(2).nextBytes(5000)

    @Before @After fun clean() {
        store().retain(emptySet())
        cr.delete(MediaStore.Downloads.EXTERNAL_CONTENT_URI, "${MediaStore.Downloads.DISPLAY_NAME} LIKE 'mstest%'", null)
    }

    private fun content(ref: String) = cr.openInputStream(Uri.parse(ref))!!.use { it.readBytes() }

    private fun names(): List<String> = cr.query(
        MediaStore.Downloads.EXTERNAL_CONTENT_URI, arrayOf(MediaStore.Downloads.DISPLAY_NAME, MediaStore.Downloads.RELATIVE_PATH),
        "${MediaStore.Downloads.DISPLAY_NAME} LIKE 'mstest%'", null, MediaStore.Downloads.DISPLAY_NAME,
    )!!.use { c -> generateSequence { if (c.moveToNext()) c.getString(1) + c.getString(0) else null }.toList() }

    private fun download(name: String, data: ByteArray = bytes, tid: String = "t1", n: Int = 0): String {
        val p = store().part(tid, n, FileItem(name, data.size.toLong(), "text/html"))
        p.append(0).use { it.write(data) }
        return p.finish()
    }

    @Test fun partSurvivesNewInstance() {
        val item = FileItem("mstest.bin", 5000)
        val p = store().part("t1", 0, item)
        assertEquals(0L, p.size())
        p.append(0).use { it.write(bytes, 0, 1000) }
        assertEquals(1000L, p.size())
        val q = store().part("t1", 0, item)
        assertEquals(1000L, q.size())
        assertNull(q.finishedRef)
        q.append(1000).use { it.write(bytes, 1000, 4000) }
        assertArrayEquals(bytes, content(q.finish()))
        assertEquals(listOf("Download/mstest.bin"), names())
    }

    @Test fun pendingIsInvisibleUntilFinish() {
        val p = store().part("t1", 0, FileItem("mstest.txt", 5000))
        p.append(0).use { it.write(bytes) }
        assertEquals(emptyList<String>(), names())
        p.finish()
        assertEquals(listOf("Download/mstest.txt"), names())
    }

    @Test fun nameConflictGetsSuffix() {
        val a = download("mstest a.txt", "1".toByteArray(), "t1")
        val b = download("mstest a.txt", "2".toByteArray(), "t2")
        assertNotEquals(a, b)
        assertEquals(listOf("Download/mstest a (1).txt", "Download/mstest a.txt"), names().sorted())
        assertEquals("1", content(a).decodeToString())
        assertEquals("2", content(b).decodeToString())
    }

    @Test fun twoPendingWithTheSameNameBothSurvive() {
        val p = store().part("t1", 0, FileItem("mstest.txt", 1))
        val q = store().part("t1", 1, FileItem("mstest.txt", 1))
        p.append(0).use { it.write(1) }
        q.append(0).use { it.write(2) }
        val r1 = p.finish()
        val r2 = q.finish()
        assertEquals(1, content(r1)[0].toInt())
        assertEquals(2, content(r2)[0].toInt())
        assertEquals(2, names().size)
    }

    @Test fun manyDownloadsOfTheSameName() {
        val refs = (1..40).map { download("mstest.apk", "$it".toByteArray(), "t$it") }
        assertEquals(40, refs.toSet().size)
        assertEquals(40, names().size)
        assertEquals((1..40).map { "$it" }, refs.map { content(it).decodeToString() })
        assertTrue(names().toString(), names().all { it.endsWith(".apk") })
    }

    @Test fun appendCutsOffWhatIsBehindTheOffset() {
        val p = store().part("t1", 0, FileItem("mstest.txt", 3))
        p.append(0).use { it.write("abcdef".toByteArray()) }
        p.append(2).use { it.write("X".toByteArray()) }
        assertEquals(3L, p.size())
        assertEquals("abX", content(p.finish()).decodeToString())
    }

    @Test fun appendBehindTheEndIsRefused() {
        val p = store().part("t1", 0, FileItem("mstest.txt", 9))
        p.append(0).use { it.write("abc".toByteArray()) }
        org.junit.Assert.assertThrows(de.suitithub.share.core.StorageBusyException::class.java) { p.append(4) }
        assertEquals(3L, p.size())
        org.junit.Assert.assertThrows(de.suitithub.share.core.StorageBusyException::class.java) { store().part("t9", 0, FileItem("mstest9.txt", 9)).append(4) }
    }

    @Test fun apkKeepsNameAndMime() {
        val ref = download("mstest.apk")
        assertEquals(listOf("Download/mstest.apk"), names())
        assertEquals("application/vnd.android.package-archive", cr.getType(Uri.parse(ref)))
    }

    @Test fun namesAreKeptWhateverTheMimeType() {
        val all = listOf("mstest", "mstest.foo", "mstest.tar.gz", "mstest.JPG", "mstest.mp4", "mstest.html", "mstest ü ß (x).pdf")
        all.forEachIndexed { i, n -> download(n, n = i) }
        assertEquals(all.map { "Download/$it" }.sorted(), names().sorted())
    }

    @Test fun hostileNamesAreCleaned() {
        download("../../mstest:evil?.txt")
        assertEquals(listOf("Download/mstest_evil_.txt"), names())
    }

    @Test fun emptyFile() {
        val ref = store().part("t1", 0, FileItem("mstest.txt", 0)).finish()
        assertEquals(0, content(ref).size)
        assertEquals(listOf("Download/mstest.txt"), names())
    }

    @Test fun finishedRefSurvivesNewInstance() {
        val ref = download("mstest.txt")
        val p = store().part("t1", 0, FileItem("mstest.txt", 5000))
        assertEquals(ref, p.finishedRef)
        store().forget("t1")
        assertNull(store().part("t1", 0, FileItem("mstest.txt", 5000)).finishedRef)
        assertArrayEquals(bytes, content(ref)) // the user's file stays
    }

    @Test fun finishedButDeletedByTheUserIsLoadedAgain() {
        val ref = download("mstest.txt")
        cr.delete(Uri.parse(ref), null, null)
        assertNull(store().part("t1", 0, FileItem("mstest.txt", 5000)).finishedRef)
    }

    private fun pendingCount(): Int = cr.query(
        MediaStore.setIncludePending(MediaStore.Downloads.EXTERNAL_CONTENT_URI), null, "${MediaStore.Downloads.DISPLAY_NAME} LIKE 'mstest%'", null, null,
    )!!.use { it.count }

    @Test fun forgetRemovesOnlyThatOffer() {
        store().part("t1", 0, FileItem("mstest1.txt", 9)).append(0).use { it.write(1) }
        store().part("t2", 0, FileItem("mstest2.txt", 9)).append(0).use { it.write(1) }
        store().part("t1b", 0, FileItem("mstest3.txt", 9)).append(0).use { it.write(1) }
        assertEquals(3, pendingCount())
        store().forget("t1")
        assertEquals(2, pendingCount())
        store().retain(setOf("t2"))
        assertEquals(1, pendingCount())
        assertEquals(1L, store().part("t2", 0, FileItem("mstest2.txt", 9)).size())
    }

    @Test fun resetStartsOver() {
        val p = store().part("t1", 0, FileItem("mstest.txt", 2))
        p.append(0).use { it.write(bytes) }
        p.reset()
        assertEquals(0L, p.size())
        p.append(0).use { it.write("ok".toByteArray()) }
        assertEquals("ok", content(p.finish()).decodeToString())
    }

    @Test fun staleStateWithDeletedRowRestarts() {
        val item = FileItem("mstest.txt", 5000)
        store().part("t1", 0, item).append(0).use { it.write(bytes, 0, 100) }
        cr.delete(MediaStore.setIncludePending(MediaStore.Downloads.EXTERNAL_CONTENT_URI), "${MediaStore.Downloads.DISPLAY_NAME} LIKE 'mstest%'", null)
        val p = store().part("t1", 0, item)
        assertEquals(0L, p.size())
        p.append(0).use { it.write(bytes) }
        assertArrayEquals(bytes, content(p.finish()))
        assertTrue(names().isNotEmpty())
    }
}
