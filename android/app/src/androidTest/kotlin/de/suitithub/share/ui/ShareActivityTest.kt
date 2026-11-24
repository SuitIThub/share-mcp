package de.suitithub.share.ui

import android.Manifest
import android.content.Intent
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.hasScrollToNodeAction
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.v2.createEmptyComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollToNode
import androidx.lifecycle.Lifecycle
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.rule.GrantPermissionRule
import de.suitithub.share.TestEnv
import de.suitithub.share.TestEnv.eventually
import de.suitithub.share.TestProvider
import de.suitithub.share.core.Kind
import de.suitithub.share.service.TransferService
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ShareActivityTest {
    @get:Rule val permission: GrantPermissionRule = GrantPermissionRule.grant(Manifest.permission.POST_NOTIFICATIONS)
    @get:Rule val ui = createEmptyComposeRule()
    private val ctx = TestEnv.ctx
    private lateinit var peer: TestEnv.Peer
    private var scenario: ActivityScenario<ShareActivity>? = null

    @Before fun setUp() {
        peer = TestEnv.Peer.create()
        runBlocking { TestEnv.pairApp() }
    }

    @After fun tearDown() {
        scenario?.close()
        TestEnv.app.setAccount(null)
        eventually("service stopped") { !TransferService.running }
        peer.engine.close()
        TestEnv.notifications.cancelAll()
    }

    private fun launch(i: Intent) {
        scenario = ActivityScenario.launch(i.setClass(ctx, ShareActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }

    private fun text(s: String) = Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, s)

    private fun waitFor(m: androidx.compose.ui.test.SemanticsMatcher) = ui.waitUntil(15_000) { ui.onAllNodes(m).fetchSemanticsNodes().isNotEmpty() }

    private fun chooseAndSend(target: String = "target:" + peer.account.deviceId) {
        // the device list grows with every test
        waitFor(hasScrollToNodeAction())
        eventually("devices") { TestEnv.app.engine.value!!.state.value.devices.any { it.id == peer.account.deviceId } }
        ui.waitForIdle()
        ui.onNode(hasScrollToNodeAction()).performScrollToNode(hasTestTag(target))
        ui.onNodeWithTag("shareSend").assertIsNotEnabled()
        ui.onNodeWithTag(target).performClick()
        ui.onNodeWithTag("shareSend").performClick()
    }

    private fun senderApp(vararg uris: android.net.Uri) = ctx.startActivity(
        Intent().setClassName(ctx.packageName + ".test", "de.suitithub.share.TestSenderActivity")
            .putStringArrayListExtra("uris", ArrayList(uris.map { it.toString() })).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
    )

    /** The permission was given to our activity only, and the sharing app is gone by the time the file is read. */
    @Test fun shareFromOtherAppSends() {
        val name = "grant-${System.nanoTime()}.bin"
        val a = TestProvider.uri(TestProvider.GUARDED, "plain", 1_500_000, name)
        val b = TestProvider.uri(TestProvider.GUARDED, "pipe", 200_000, "pipe-$name")
        senderApp(a, b)
        chooseAndSend()
        eventually("offered") { peer.engine.state.value.incoming.isNotEmpty() }
        Thread.sleep(1000)
        runBlocking { peer.engine.accept(peer.engine.state.value.incoming.single().id) }
        eventually("received", 30) { peer.store.files.size == 2 }
        assertArrayEquals(TestProvider.bytes(1_500_000, name), peer.store.files[name])
        assertArrayEquals(TestProvider.bytes(200_000, "pipe-$name"), peer.store.files["pipe-$name"])
    }

    @Test fun sharesTextToTheChosenDevice() {
        launch(text("hallo"))
        chooseAndSend()
        eventually("offered") { peer.engine.state.value.incoming.map { it.text } == listOf("hallo") }
        eventually("closed") { scenario!!.state == Lifecycle.State.DESTROYED }
        assertFalse(TransferService.running)
    }

    @Test fun offersLinkToAll() {
        launch(text("https://example.org"))
        chooseAndSend("target:all")
        eventually("offered") { peer.engine.state.value.incoming.singleOrNull()?.let { it.kind == Kind.LINK && it.broadcast } == true }
    }

    @Test fun notPairedShowsHint() {
        TestEnv.app.setAccount(null)
        launch(text("x"))
        waitFor(hasText("zuerst gekoppelt", substring = true))
    }

    @Test fun nothingToShareShowsHint() {
        launch(Intent(Intent.ACTION_SEND).setType("*/*"))
        waitFor(hasText("Hier gibt es nichts zu senden"))
        ui.onNodeWithTag("shareClose").performClick()
        eventually("closed") { scenario!!.state == Lifecycle.State.DESTROYED }
    }

    @Test fun rejectedShowsHint() {
        launch(Intent(Intent.ACTION_SEND).setType("*/*").putExtra(Intent.EXTRA_STREAM, android.net.Uri.parse("file:///etc/hosts")))
        waitFor(hasText("kann nicht gesendet werden", substring = true))
    }

    @Test fun tooLongTextShowsHint() {
        launch(text("a".repeat(70_000)))
        waitFor(hasText("höchstens 64 KB", substring = true))
    }

    @Test fun withoutConnectionTheErrorIsTold() {
        TestEnv.app.setAccount(null)
        runBlocking { TestEnv.pairApp() }
        launch(Intent(Intent.ACTION_SEND).setType("*/*").putExtra(Intent.EXTRA_STREAM, TestProvider.uri("denied", 1, "geheim.txt")))
        chooseAndSend()
        eventually("told") { "Senden fehlgeschlagen" in TestEnv.titles() }
        assertEquals(emptyList<String>(), peer.engine.state.value.incoming.map { it.id })
        assertTrue(true)
    }
}
