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

    @Test fun answeringInTheUiRemovesTheNotification() {
        val t = runBlocking { peer.engine.offerText(Kind.TEXT, "hallo", listOf(appName), false) }
        notify(t.id)
        runBlocking { engine.decline(t.id) }
        eventually("gone") { TestEnv.active(t.id) == null }
    }
}
