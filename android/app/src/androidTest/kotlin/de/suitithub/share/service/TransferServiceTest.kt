package de.suitithub.share.service

import android.Manifest
import android.net.Uri
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.rule.GrantPermissionRule
import de.suitithub.share.TestEnv
import de.suitithub.share.TestEnv.eventually
import de.suitithub.share.TestProvider
import de.suitithub.share.core.Kind
import de.suitithub.share.core.Phase
import de.suitithub.share.core.Status
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import kotlin.random.Random

@RunWith(AndroidJUnit4::class)
class TransferServiceTest {
    @get:Rule val permission: GrantPermissionRule = GrantPermissionRule.grant(Manifest.permission.POST_NOTIFICATIONS)
    private val ctx = TestEnv.ctx
    private val app = TestEnv.app
    private lateinit var peer: TestEnv.Peer

    @Before fun setUp() {
        peer = TestEnv.Peer.create()
        runBlocking { TestEnv.pairApp() }
    }

    @After fun tearDown() {
        eventuallyIdle()
        app.setAccount(null)
        peer.engine.close()
        TestEnv.notifications.cancelAll()
    }

    private fun eventuallyIdle() = eventually("service stopped") { !TransferService.running }

    private val engine get() = app.engine.value!!
    private val appName get() = engine.api.account.name

    private fun offerToApp(data: ByteArray, name: String = "svc.bin", delayMs: Long = 0) =
        runBlocking { peer.engine.offerFiles(listOf(TestEnv.Bytes(data, name, delayMs)), listOf(appName), false) }

    private fun read(ref: String) = ctx.contentResolver.openInputStream(Uri.parse(ref))!!.use { it.readBytes() }
    private fun delete(ref: String) = ctx.contentResolver.delete(Uri.parse(ref), null, null)

    @Test fun offerServiceSendsAndStopsWhenDone() {
        val uri = TestProvider.uri("once", 2_000_000, "svc-${System.nanoTime()}.bin")
        TransferService.offer(ctx, listOf(uri), listOf(peer.account.deviceId), false, "für dich")
        eventually("offered") { peer.engine.state.value.incoming.isNotEmpty() }
        val t = peer.engine.state.value.incoming.single()
        assertEquals("für dich", t.message)
        eventually("progress notification") { "Biete an: ${uri.lastPathSegment}" in TestEnv.titles() }
        assertTrue(TransferService.running)
        runBlocking { peer.engine.accept(t.id) }
        eventually("received", 30) { peer.store.files.containsKey(uri.lastPathSegment) }
        assertArrayEquals(TestProvider.bytes(2_000_000, uri.lastPathSegment), peer.store.files[uri.lastPathSegment])
        eventuallyIdle()
        eventually("notification gone") { TestEnv.titles().none { it.startsWith("Biete an") } }
    }

    @Test fun unreadableOfferIsReported() {
        TransferService.offer(ctx, listOf(TestProvider.uri("denied", 10, "geheim.txt")), listOf(peer.account.deviceId), false, "")
        eventually("message") { TestEnv.notifications.activeNotifications.any { "geheim.txt" in it.notification.extras.getCharSequence("android.text").toString() } }
        eventuallyIdle()
        assertTrue(peer.engine.state.value.incoming.isEmpty())
    }

    @Test fun oneUnreadableFileOffersNothing() {
        val ok = TestProvider.uri("plain", 10, "ok.txt")
        TransferService.offer(ctx, listOf(ok, TestProvider.uri("denied", 10, "geheim.txt")), listOf(peer.account.deviceId), false, "")
        eventually("message") { "Senden fehlgeschlagen" in TestEnv.titles() }
        eventuallyIdle()
        assertTrue(engine.state.value.outgoing.isEmpty())
    }

    @Test fun cancelActionEndsTheOffer() {
        TransferService.offer(ctx, listOf(TestProvider.uri("plain", 10, "c.txt")), listOf(peer.account.deviceId), false, "")
        eventually("offered") { peer.engine.state.value.incoming.isNotEmpty() }
        eventually("notification") { "Biete an: c.txt" in TestEnv.titles() }
        TestEnv.notifications.activeNotifications.single { it.notification.extras.getCharSequence("android.title").toString() == "Biete an: c.txt" }
            .notification.actions.single().actionIntent.send()
        eventually("withdrawn") { peer.engine.state.value.incoming.isEmpty() }
        eventuallyIdle()
    }

    /** Posts the offer notification like push does and returns its actions. */
    private fun notify(id: String) = runBlocking {
        val t = engine.offerDetails(id)!!
        app.notifications.offer(t, engine.deviceName(t.senderId))
        eventually("posted") { TestEnv.active(id) != null }
        TestEnv.active(id)!!.notification.actions
    }

    @Test fun acceptActionDownloadsWithUiClosed() {
        val data = Random(3).nextBytes(3_000_000)
        val t = offerToApp(data)
        notify(t.id)[1].actionIntent.send()
        eventually("done", 30) { engine.state.value.downloads.singleOrNull()?.phase == Phase.DONE }
        eventually("offer notification gone") { TestEnv.active(t.id) == null }
        eventually("result") { "svc.bin empfangen" in TestEnv.titles() }
        val ref = engine.state.value.downloads.single().refs.single()
        assertArrayEquals(data, read(ref))
        delete(ref)
        eventuallyIdle()
    }

    @Test fun acceptedDownloadRunsInForeground() {
        val t = offerToApp(Random(3).nextBytes(3_000_000), "slow.bin", delayMs = 60)
        notify(t.id)[1].actionIntent.send()
        eventually("running") { TransferService.running && "slow.bin" in TestEnv.titles() }
        eventually("done", 30) { engine.state.value.downloads.single().phase == Phase.DONE }
        delete(engine.state.value.downloads.single().refs.single())
    }

    @Test fun declineActionWorks() {
        val t = offerToApp(ByteArray(10))
        val actions = notify(t.id)
        actions[0].actionIntent.send()
        eventually("declined") { runBlocking { peer.engine.api.transfer(t.id) }.deliveries.single().status == Status.DECLINED }
        eventually("gone") { TestEnv.active(t.id) == null }
        assertFalse(TransferService.running)
    }

    @Test fun downloadStartedFromTheUiGetsTheService() = runBlocking {
        val t = offerToApp(Random(3).nextBytes(1_000_000), "ui.bin", delayMs = 60)
        engine.accept(t.id)
        eventually("service") { TransferService.running }
        eventually("done", 30) { engine.state.value.downloads.single().phase == Phase.DONE }
        delete(engine.state.value.downloads.single().refs.single())
        Unit
    }

    @Test fun staleActionsAreHarmless() {
        val t = offerToApp(ByteArray(10))
        val actions = notify(t.id)
        runBlocking { peer.engine.cancelOutgoing(t.id) }
        actions[1].actionIntent.send()
        actions[1].actionIntent.send()
        eventually("told") { "Angebot nicht mehr verfügbar" in TestEnv.titles() }
        eventually("gone") { TestEnv.active(t.id) == null }
        eventuallyIdle()
        actions[0].actionIntent.send()
        ctx.startForegroundService(TransferService.cancelIntent(ctx, "unknown"))
        ctx.startForegroundService(TransferService.acceptIntent(ctx, "unknown"))
        Thread.sleep(500)
        eventuallyIdle()
        app.setAccount(null)
        actions[1].actionIntent.send()
        actions[0].actionIntent.send()
        ctx.startForegroundService(TransferService.cancelIntent(ctx, t.id))
        TransferService.offer(ctx, listOf(TestProvider.uri("plain", 10, "x")), listOf("y"), false, "")
        Thread.sleep(500)
        eventuallyIdle()
    }

    @Test fun offersAnnounceThemselvesOnlyWhileHidden() {
        val hold = engine.hold()
        eventually("connected") { engine.state.value.connected }
        val t1 = runBlocking { peer.engine.offerText(Kind.TEXT, "hallo", listOf(appName), false) }
        eventually("notified") { TestEnv.active(t1.id) != null }
        runBlocking { peer.engine.cancelOutgoing(t1.id) }
        eventually("removed") { TestEnv.active(t1.id) == null }
        val shown = app.uiShown()
        val t2 = runBlocking { peer.engine.offerText(Kind.TEXT, "hallo", listOf(appName), false) }
        eventually("arrived") { engine.state.value.incoming.any { it.id == t2.id } }
        Thread.sleep(300)
        assertNull(TestEnv.active(t2.id))
        shown.close()
        hold.close()
    }

    @Test fun unpairingWhileOfferingStopsTheService() {
        TransferService.offer(ctx, listOf(TestProvider.uri("plain", 10, "weg.txt")), listOf(peer.account.deviceId), false, "")
        eventually("offered") { peer.engine.state.value.incoming.isNotEmpty() && "Biete an: weg.txt" in TestEnv.titles() }
        app.setAccount(null)
        eventuallyIdle()
        eventually("notification gone") { "Biete an: weg.txt" !in TestEnv.titles() }
    }

    @Test fun unpairingWhileDownloadingStopsTheService() {
        val t = offerToApp(ByteArray(3_000_000), "weg.bin", delayMs = 100)
        notify(t.id)[1].actionIntent.send()
        eventually("running") { TransferService.running && "weg.bin" in TestEnv.titles() }
        app.setAccount(null)
        eventuallyIdle()
    }

    @Test fun serviceFollowsANewPairing() {
        TransferService.offer(ctx, listOf(TestProvider.uri("plain", 10, "alt.txt")), listOf(peer.account.deviceId), false, "")
        eventually("offered") { "Biete an: alt.txt" in TestEnv.titles() }
        runBlocking { TestEnv.pairApp() }
        TransferService.offer(ctx, listOf(TestProvider.uri("plain", 10, "neu.txt")), listOf(peer.account.deviceId), false, "")
        eventually("new one shown") { "Biete an: neu.txt" in TestEnv.titles() }
        runBlocking { engine.cancelOutgoing(engine.state.value.outgoing.single().id) }
        eventuallyIdle()
    }

    @Test fun serviceComesBackWhileThereIsWork() {
        TransferService.offer(ctx, listOf(TestProvider.uri("plain", 10, "bleib.txt")), listOf(peer.account.deviceId), false, "")
        eventually("offered") { peer.engine.state.value.incoming.isNotEmpty() }
        val first = TransferService.starts
        ctx.stopService(android.content.Intent(ctx, TransferService::class.java))
        eventually("restarted") { TransferService.starts > first && TransferService.running }
        runBlocking { engine.cancelOutgoing(engine.state.value.outgoing.single().id) }
    }

    /** Android ends data sync services after some hours; the advice to open the app must then help. */
    @Test fun openingTheAppRestartsAServiceThatTimedOut() {
        fun sh(c: String) = android.os.ParcelFileDescriptor.AutoCloseInputStream(
            androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().uiAutomation.executeShellCommand(c),
        ).use { it.readBytes().decodeToString() }
        // being on top resets the time used so far
        androidx.test.core.app.ActivityScenario.launch(de.suitithub.share.ui.MainActivity::class.java).use { eventually("shown") { app.uiVisible } }
        sh("device_config put activity_manager data_sync_fgs_timeout_duration 4000")
        try {
            TransferService.offer(ctx, listOf(TestProvider.uri("plain", 10, "lang.txt")), listOf(peer.account.deviceId), false, "")
            eventually("offered") { peer.engine.state.value.incoming.isNotEmpty() }
            eventually("timed out", 20) { !TransferService.running && "Share öffnen, um die Übertragung fortzusetzen" in TestEnv.titles() }
            sh("device_config delete activity_manager data_sync_fgs_timeout_duration")
            androidx.test.core.app.ActivityScenario.launch(de.suitithub.share.ui.MainActivity::class.java).use {
                eventually("running again") { TransferService.running }
            }
        } finally {
            sh("device_config delete activity_manager data_sync_fgs_timeout_duration")
            engine.state.value.outgoing.forEach { runBlocking { engine.cancelOutgoing(it.id) } }
        }
    }

    @Test fun offersSeenByNobodyAreAnnouncedWhenTheUiCloses() {
        val hold = engine.hold()
        eventually("connected") { engine.state.value.connected }
        val shown = app.uiShown()
        val t = runBlocking { peer.engine.offerText(Kind.TEXT, "hallo", listOf(appName), false) }
        eventually("arrived") { engine.state.value.incoming.any { it.id == t.id } }
        shown.close()
        eventually("announced") { TestEnv.active(t.id) != null }
        // dismissed by the user: not again
        TestEnv.notifications.cancel(t.id, Notifications.OFFER)
        app.uiShown().close()
        Thread.sleep(300)
        assertNull(TestEnv.active(t.id))
        hold.close()
    }

    @Test fun outcomeOfAnOfferIsAnnounced() {
        TransferService.offer(ctx, listOf(TestProvider.uri("plain", 10, "ergebnis.txt")), listOf(peer.account.deviceId), false, "")
        eventually("offered") { peer.engine.state.value.incoming.isNotEmpty() }
        runBlocking { peer.engine.decline(peer.engine.state.value.incoming.single().id) }
        eventually("told") { TestEnv.notifications.activeNotifications.any { it.notification.extras.getCharSequence("android.text").toString() == "${peer.account.name}: abgelehnt" } }
    }

    @Test fun answeringInTheUiRemovesTheNotification() {
        val t = runBlocking { peer.engine.offerText(Kind.TEXT, "hallo", listOf(appName), false) }
        notify(t.id)
        runBlocking { engine.decline(t.id) }
        eventually("gone") { TestEnv.active(t.id) == null }
    }
}
