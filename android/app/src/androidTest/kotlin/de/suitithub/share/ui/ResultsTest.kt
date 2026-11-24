package de.suitithub.share.ui

import android.Manifest
import android.app.DownloadManager
import android.app.Notification
import android.app.PendingIntent
import android.content.ClipboardManager
import android.content.Intent
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.rule.GrantPermissionRule
import de.suitithub.share.TestEnv
import de.suitithub.share.TestEnv.eventually
import de.suitithub.share.core.Download
import de.suitithub.share.core.FileItem
import de.suitithub.share.core.Kind
import de.suitithub.share.core.Path
import de.suitithub.share.core.Phase
import de.suitithub.share.core.Transfer
import de.suitithub.share.service.Notifications
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.time.Instant

@RunWith(AndroidJUnit4::class)
class ResultsTest {
    @get:Rule val permission: GrantPermissionRule = GrantPermissionRule.grant(Manifest.permission.POST_NOTIFICATIONS)
    private val ctx = TestEnv.ctx
    private val app = TestEnv.app
    private val n = Notifications(ctx)

    @After fun clean() {
        app.setAccount(null)
        TestEnv.notifications.cancelAll()
    }

    private fun clip(): String {
        var s = ""
        // the clipboard is readable only for the focused app
        ActivityScenario.launch(MainActivity::class.java).use { sc ->
            eventually("focus") {
                var f = false
                sc.onActivity { f = it.hasWindowFocus() }
                f
            }
            sc.onActivity { s = it.getSystemService(ClipboardManager::class.java).primaryClip?.getItemAt(0)?.text.toString() }
        }
        return s
    }

    private fun actions(tag: String): List<String> {
        eventually("posted") { TestEnv.active(tag) != null }
        return TestEnv.active(tag)!!.notification.actions.orEmpty().map { it.title.toString() }
    }

    private fun receive(kind: Kind, text: String): String {
        val peer = TestEnv.Peer.create()
        val e = runBlocking { TestEnv.pairApp() }
        val t = runBlocking { peer.engine.offerText(kind, text, listOf(e.api.account.name), false) }
        runBlocking { e.accept(t.id) }
        eventually("told") { TestEnv.active("r:" + t.id) != null }
        peer.engine.close()
        return t.id
    }

    @Test fun acceptedTextIsInClipboard() {
        val id = receive(Kind.TEXT, "geheimer text 123")
        assertTrue(TestEnv.titles().single().endsWith("kopiert"))
        assertEquals(emptyList<String>(), actions("r:$id"))
        assertEquals("geheimer text 123", clip())
    }

    @Test fun acceptedLinkOffersOpen() {
        val id = receive(Kind.LINK, "https://example.org/x")
        assertEquals(listOf("Öffnen"), actions("r:$id"))
        assertEquals("https://example.org/x", clip())
    }

    @Test fun dangerousLinksAreOnlyCopied() {
        for ((i, bad) in listOf("intent://x#Intent;end", "javascript:alert(1)", "file:///etc/hosts", "content://x/y").withIndex()) {
            n.text(Transfer("l$i", "s", Kind.LINK, text = bad, expiresAt = Instant.now()), "x")
            assertEquals(bad, emptyList<String>(), actions("r:l$i"))
        }
    }

    private fun done(vararg files: String, id: String = "d") = Download(
        Transfer(id, "s", Kind.FILES, files = files.map { FileItem(it, 1) }, expiresAt = Instant.now()),
        Phase.DONE, files.size.toLong(), Path.LAN, files.map { "content://media/external/downloads/1" },
    )

    @Test fun apkResultHasInstallIntent() {
        assertTrue(Results.isApk(done("app.apk")))
        assertTrue(Results.isApk(done("APP.APK")))
        assertFalse(Results.isApk(done("a.apk", "b.apk")))
        assertFalse(Results.isApk(done("app.apk.txt")))
        val i = Results.openIntent(done("app.apk"))
        assertEquals(Intent.ACTION_VIEW, i.action)
        assertEquals("application/vnd.android.package-archive", i.type)
        assertTrue(i.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0)
        n.result(done("app.apk"))
        assertEquals(listOf("Installieren"), actions("r:d"))
    }

    @Test fun fileResultOpensOnTap() {
        n.result(done("a.pdf"))
        eventually("posted") { TestEnv.active("r:d") != null }
        val x = TestEnv.active("r:d")!!.notification
        val open = PendingIntent.getActivity(ctx, 0, Results.openIntent(done("a.pdf")).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_NO_CREATE)
        assertEquals(open, x.contentIntent)
        assertTrue(open != null && x.flags and Notification.FLAG_AUTO_CANCEL != 0)
        assertEquals("application/pdf", Results.openIntent(done("a.pdf")).type)
    }

    @Test fun severalFilesOpenTheDownloadsList() =
        assertEquals(DownloadManager.ACTION_VIEW_DOWNLOADS, Results.openIntent(done("a", "b")).action)

    @Test fun unknownTypeOpenShowsHint() = assertFalse(Results.open(ctx, done("x.voelligunbekannt123")))

    /** The system's installer, opened through our notification intent, must be able to read the APK. */
    @Test fun installerOpensTheDownloadedApk() {
        val store = de.suitithub.share.data.MediaStoreDownloads(ctx, "results-test")
        val apk = java.io.File(ctx.applicationInfo.sourceDir)
        val part = store.part("apk", 0, FileItem("selbst.apk", apk.length()))
        part.append().use { o -> apk.inputStream().use { it.copyTo(o) } }
        val ref = part.finish()
        store.forget("apk")
        val d = done("selbst.apk").copy(refs = listOf(ref))
        try {
            assertTrue(Results.open(ctx, d))
            val dev = androidx.test.uiautomator.UiDevice.getInstance(androidx.test.platform.app.InstrumentationRegistry.getInstrumentation())
            // either "allow from this source" or the install dialog itself, both by the package installer / settings
            val shown = dev.wait(androidx.test.uiautomator.Until.hasObject(androidx.test.uiautomator.By.textContains("Share")), 10_000)
            val pkg = dev.currentPackageName
            dev.pressBack()
            assertTrue("$shown $pkg", pkg.contains("packageinstaller") || pkg.contains("settings"))
        } finally {
            ctx.contentResolver.delete(android.net.Uri.parse(ref), null, null)
        }
    }
}
