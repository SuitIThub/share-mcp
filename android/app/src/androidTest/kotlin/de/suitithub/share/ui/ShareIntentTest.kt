package de.suitithub.share.ui

import android.content.Intent
import android.net.Uri
import android.text.SpannableString
import androidx.test.ext.junit.runners.AndroidJUnit4
import de.suitithub.share.TestEnv
import de.suitithub.share.core.Kind
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ShareIntentTest {
    private val a = Uri.parse("content://x/a")
    private val b = Uri.parse("content://x/b")

    private fun parse(i: Intent) = parseShare(i) { it == "own" }
    private fun send(type: String = "*/*") = Intent(Intent.ACTION_SEND).setType(type)
    private fun multiple(vararg u: Uri?) = Intent(Intent.ACTION_SEND_MULTIPLE).setType("*/*").putParcelableArrayListExtra(Intent.EXTRA_STREAM, arrayListOf(*u))

    @Test fun singleFile() = assertEquals(SharePayload.Files(listOf(a), ""), parse(send("image/png").putExtra(Intent.EXTRA_STREAM, a)))

    @Test fun multipleFiles() = assertEquals(SharePayload.Files(listOf(a, b), ""), parse(multiple(a, b)))

    @Test fun plainTextIsText() = assertEquals(SharePayload.Text(Kind.TEXT, "hallo welt"), parse(send("text/plain").putExtra(Intent.EXTRA_TEXT, "hallo welt")))

    @Test fun styledTextIsText() = assertEquals(SharePayload.Text(Kind.TEXT, "fett"), parse(send("text/plain").putExtra(Intent.EXTRA_TEXT, SpannableString("fett"))))

    @Test fun urlIsLink() = assertEquals(
        SharePayload.Text(Kind.LINK, "https://x.de"),
        parse(send("text/plain").putExtra(Intent.EXTRA_TEXT, " https://x.de\n").putExtra(Intent.EXTRA_SUBJECT, "Titel")),
    )

    @Test fun textWithFileIsFiles() = assertEquals(
        SharePayload.Files(listOf(a), "dazu"),
        parse(send("text/plain").putExtra(Intent.EXTRA_STREAM, a).putExtra(Intent.EXTRA_TEXT, "dazu")),
    )

    @Test fun nullAndDuplicateUris() = assertEquals(SharePayload.Files(listOf(a, b), ""), parse(multiple(a, null, b, a)))

    @Test fun clipDataCounts() {
        val i = send()
        i.clipData = android.content.ClipData.newRawUri("", a)
        assertEquals(SharePayload.Files(listOf(a), ""), parse(i))
    }

    /** For text shares the clip data is only a preview picture. */
    @Test fun previewPictureOfATextIsIgnored() {
        val i = send("text/plain").putExtra(Intent.EXTRA_TEXT, "https://x.de").putExtra(Intent.EXTRA_TITLE, "Titel")
        i.clipData = android.content.ClipData.newRawUri("preview", a)
        assertEquals(SharePayload.Text(Kind.LINK, "https://x.de"), parse(i))
    }

    @Test fun emptyIntent() {
        for (i in listOf(send(), send("text/plain").putExtra(Intent.EXTRA_TEXT, "  "), multiple(), Intent(Intent.ACTION_MAIN), Intent(), send().putExtra(Intent.EXTRA_STREAM, "kein Uri"))) {
            assertEquals(SharePayload.Empty, parse(i))
        }
    }

    @Test fun fileSchemeRejected() {
        for (bad in listOf("file:///data/data/de.suitithub.share/files/account.bin", "content://own/secret", "http://x/y", "android.resource://x/1")) {
            assertEquals(bad, SharePayload.Rejected, parse(send().putExtra(Intent.EXTRA_STREAM, Uri.parse(bad))))
            assertEquals(bad, SharePayload.Rejected, parse(multiple(a, Uri.parse(bad))))
        }
    }

    @Test fun tooLongText() {
        assertEquals(SharePayload.TooLong, parse(send("text/plain").putExtra(Intent.EXTRA_TEXT, "ä".repeat(32769))))
        assertTrue(parse(send("text/plain").putExtra(Intent.EXTRA_TEXT, "a".repeat(65536))) is SharePayload.Text)
    }

    @Test fun appIsOfferedByTheSystem() {
        val pm = TestEnv.ctx.packageManager
        for (i in listOf(send("image/png"), send("text/plain"), Intent(Intent.ACTION_SEND_MULTIPLE).setType("video/mp4"), send("application/octet-stream"))) {
            assertTrue("$i", pm.queryIntentActivities(i.setPackage(TestEnv.ctx.packageName), 0).any { it.activityInfo.name.endsWith("ShareActivity") })
        }
    }
}
