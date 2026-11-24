package de.suitithub.share.service

import android.Manifest
import android.app.Notification
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.rule.GrantPermissionRule
import de.suitithub.share.TestEnv
import de.suitithub.share.core.Delivery
import de.suitithub.share.core.Download
import de.suitithub.share.core.EngineState
import de.suitithub.share.core.FileItem
import de.suitithub.share.core.Kind
import de.suitithub.share.core.Path
import de.suitithub.share.core.Phase
import de.suitithub.share.core.Status
import de.suitithub.share.core.Transfer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.time.Instant

@RunWith(AndroidJUnit4::class)
class NotificationsTest {
    @get:Rule val permission: GrantPermissionRule = GrantPermissionRule.grant(Manifest.permission.POST_NOTIFICATIONS)
    private val ctx = TestEnv.ctx
    private val n = Notifications(ctx)

    @After fun clean() = TestEnv.notifications.cancelAll()

    private fun offer(id: String = "t1", seconds: Long = 300, kind: Kind = Kind.FILES) = Transfer(
        id, "s", kind, text = "https://x", files = if (kind == Kind.FILES) listOf(FileItem("app.apk", 1536)) else emptyList(),
        expiresAt = Instant.now().plusSeconds(seconds),
    )

    private fun Notification.title() = extras.getCharSequence(Notification.EXTRA_TITLE).toString()
    private fun Notification.text() = extras.getCharSequence(Notification.EXTRA_TEXT).toString()

    @Test fun offerHasAcceptDeclineAndTimeout() {
        n.offer(offer(), "Desktop")
        TestEnv.eventually("posted") { TestEnv.active("t1") != null }
        val x = TestEnv.active("t1")!!.notification
        assertEquals("Datei von Desktop", x.title())
        assertEquals("app.apk, 1,5 KB", x.text())
        assertEquals(listOf("Ablehnen", "Annehmen"), x.actions.map { it.title.toString() })
        assertTrue("${x.timeoutAfter}", x.timeoutAfter in 250_000..300_000)
        assertTrue(x.actions.all { it.actionIntent.isImmutable })
        n.offer(offer(), "Desktop")
        n.offer(offer("t2"), "Desktop")
        TestEnv.eventually("second") { TestEnv.active("t2") != null }
        assertEquals(1, TestEnv.notifications.activeNotifications.count { it.tag == "t1" })
        n.removeOffer("t1")
        TestEnv.eventually("removed") { TestEnv.active("t1") == null }
        assertNotNull(TestEnv.active("t2"))
    }

    @Test fun actionsOfDifferentOffersDiffer() {
        n.offer(offer("t1"), "x")
        n.offer(offer("t2"), "x")
        TestEnv.eventually("both") { TestEnv.active("t1") != null && TestEnv.active("t2") != null }
        val a = TestEnv.active("t1")!!.notification.actions
        val b = TestEnv.active("t2")!!.notification.actions
        for (i in 0..1) assertTrue(a[i].actionIntent != b[i].actionIntent)
    }

    @Test fun expiredOffersAreNotPosted() {
        n.offer(offer(seconds = -1), "x")
        Thread.sleep(300)
        assertNull(TestEnv.active("t1"))
    }

    @Test fun noPermissionDoesNotCrash() {
        val ui = InstrumentationRegistry.getInstrumentation().uiAutomation
        fun sh(c: String) = android.os.ParcelFileDescriptor.AutoCloseInputStream(ui.executeShellCommand(c)).use { it.readBytes() }
        sh("appops set ${ctx.packageName} POST_NOTIFICATION ignore")
        try {
            n.offer(offer(), "x")
            n.result(Download(offer(), Phase.DONE))
            n.text(offer(kind = Kind.LINK), "x")
            n.message("hallo")
        } finally {
            sh("appops set ${ctx.packageName} POST_NOTIFICATION allow")
        }
    }

    @Test fun failureHintsStayAndDoNotReplaceEachOther() {
        n.message("Senden fehlgeschlagen", "a")
        n.message("Senden fehlgeschlagen", "b")
        n.unavailable()
        TestEnv.eventually("all three") { TestEnv.titles().size == 3 }
        val all = TestEnv.notifications.activeNotifications.map { it.notification }
        assertEquals(2, all.count { it.title() == "Senden fehlgeschlagen" && it.timeoutAfter !in 1..3_600_000L })
        assertTrue(all.single { it.title() == "Angebot nicht mehr verfügbar" }.timeoutAfter > 0)
    }

    private fun outgoing(id: String = "o1") = offer(id).copy(deliveries = listOf(Delivery("d", Status.DOWNLOADING, Path.LAN, 10)))

    @Test fun progressOfASingleOffer() {
        val x = n.progress(EngineState(outgoing = listOf(outgoing())))
        assertEquals("Biete an: app.apk", x.title())
        assertEquals("noch 5 Min", x.text())
        assertEquals(listOf("Abbrechen"), x.actions.map { it.title.toString() })
        assertTrue(x.flags and Notification.FLAG_ONGOING_EVENT != 0)
    }

    @Test fun progressOfAnExpiredOfferStillDelivering() =
        assertEquals("wird übertragen", n.progress(EngineState(outgoing = listOf(outgoing().copy(expiresAt = Instant.now().minusSeconds(9))))).text())

    @Test fun progressOfASingleDownload() {
        val x = n.progress(EngineState(downloads = listOf(Download(offer(), Phase.RUNNING, 768, Path.RELAY), Download(offer("old"), Phase.DONE))))
        assertEquals("app.apk", x.title())
        assertEquals("50 % · über Server", x.text())
        assertEquals(50, x.extras.getInt(Notification.EXTRA_PROGRESS))
        assertEquals(listOf("Abbrechen"), x.actions.map { it.title.toString() })
    }

    @Test fun progressOfSeveral() {
        val x = n.progress(EngineState(outgoing = listOf(outgoing(), offer("text", kind = Kind.TEXT)), downloads = listOf(Download(offer("d"), Phase.RUNNING, 768))))
        assertEquals("2 Übertragungen", x.title()) // texts need no service
        assertNull(x.actions)
    }

    @Test fun progressWithNothingToShow() = assertEquals("Share", n.progress(EngineState()).title())

    @Test fun resultTexts() {
        n.result(Download(offer("d1"), Phase.DONE, 1536, Path.LAN, listOf("content://x")))
        n.result(Download(offer("d2"), Phase.FAILED, error = "Speicher voll"))
        TestEnv.eventually("posted") { TestEnv.active("r:d1") != null && TestEnv.active("r:d2") != null }
        assertEquals("app.apk empfangen", TestEnv.active("r:d1")!!.notification.title())
        assertEquals("app.apk fehlgeschlagen", TestEnv.active("r:d2")!!.notification.title())
        assertEquals("Speicher voll", TestEnv.active("r:d2")!!.notification.text())
    }
}
