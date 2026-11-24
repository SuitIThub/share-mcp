package de.suitithub.share.ui

import android.content.Intent
import android.net.Uri
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.v2.createEmptyComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performTextClearance
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.test.performTextReplacement
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import de.suitithub.share.TestEnv
import de.suitithub.share.core.Account
import de.suitithub.share.core.Api
import de.suitithub.share.core.Identity
import de.suitithub.share.core.PairUri
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class PairScreenTest {
    @get:Rule val ui = createEmptyComposeRule()
    private val app = TestEnv.app
    private var scenario: ActivityScenario<MainActivity>? = null

    @Before fun setUp() {
        app.setAccount(null)
        android.os.ParcelFileDescriptor.AutoCloseInputStream(
            androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().uiAutomation
                .executeShellCommand("pm grant ${TestEnv.ctx.packageName} android.permission.POST_NOTIFICATIONS"),
        ).use { it.readBytes() }
    }

    @After fun tearDown() {
        scenario?.close()
        app.setAccount(null)
    }

    private fun launch(intent: Intent = Intent(TestEnv.ctx, MainActivity::class.java)) {
        scenario = ActivityScenario.launch(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }

    private fun fill(url: String, code: String, name: String) {
        ui.onNodeWithTag("url").performTextReplacement(url)
        ui.onNodeWithTag("code").performTextReplacement(code)
        ui.onNodeWithTag("name").performTextReplacement(name)
        ui.onNodeWithTag("pair").performScrollTo().performClick()
    }

    private fun waitText(text: String, substring: Boolean = false) =
        ui.waitUntil(10_000) { ui.onAllNodesWithText(text, substring = substring).fetchSemanticsNodes().isNotEmpty() }

    @Test fun pairsAgainstServer() {
        launch()
        val name = TestEnv.uniqueName("Ui")
        fill(TestEnv.server, TestEnv.code().lowercase(), name)
        ui.waitUntil(10_000) { app.engine.value != null }
        assertEquals(name, app.engine.value!!.api.account.name)
        assertTrue(runBlocking { app.engine.value!!.api.devices() }.any { it.name == name })
    }

    @Test fun wrongCodeShowsGermanError() {
        launch()
        fill(TestEnv.server, "ZZZZZZ", "X")
        waitText("Code ungültig oder abgelaufen")
        assertNull(app.engine.value)
    }

    @Test fun nameTakenShowsError() {
        val taken = runBlocking { TestEnv.account("Taken") }.name
        launch()
        fill(TestEnv.server, TestEnv.code(), taken)
        waitText("Dieser Gerätename ist schon vergeben")
    }

    @Test fun badAddressAndUnreachableServer() {
        launch()
        fill("http://example.org", "ABCDEF", "X")
        waitText("Adresse ungültig – sie muss mit https:// beginnen")
        fill("https://127.0.0.1:1", "ABCDEF", "X")
        waitText("Server nicht erreichbar")
    }

    @Test fun incompleteFormCannotBeSent() {
        launch()
        ui.onNodeWithTag("pair").performScrollTo().assertIsNotEnabled()
        ui.onNodeWithTag("url").performTextInput("https://x")
        ui.onNodeWithTag("code").performTextInput("ABC")
        ui.onNodeWithTag("name").performTextClearance()
        ui.onNodeWithTag("pair").assertIsNotEnabled()
    }

    private fun deepLink(u: PairUri) = Intent(Intent.ACTION_VIEW, Uri.parse(u.toString())).setPackage(TestEnv.ctx.packageName)

    @Test fun deepLinkOnlyPrefills() {
        launch(deepLink(PairUri("https://x.example", "ABC123")))
        ui.onNodeWithTag("url").assertTextContains("https://x.example")
        ui.onNodeWithTag("code").assertTextContains("ABC123")
        Thread.sleep(500)
        assertNull(app.engine.value)
    }

    @Test fun deepLinkNeverReplacesAnExistingPairing() {
        val e = runBlocking { TestEnv.pairApp() }
        launch(deepLink(PairUri("https://evil.example", "ABC123")))
        ui.waitForIdle()
        assertEquals(0, ui.onAllNodesWithText("https://evil.example").fetchSemanticsNodes().size)
        assertEquals(e, app.engine.value)
    }

    @Test fun revokedDeviceGetsThePairScreen() {
        val e = runBlocking { TestEnv.pairApp() }
        runBlocking { Api(e.api.account).deleteMe() }
        launch()
        waitText("nicht mehr gekoppelt", substring = true)
        val name = TestEnv.uniqueName("Again")
        fill(TestEnv.server, TestEnv.code(), name)
        ui.waitUntil(10_000) { app.engine.value?.api?.account?.name == name }
    }

    // --- settings ---

    private fun openSettings() {
        runBlocking { TestEnv.pairApp() }
        launch()
        ui.waitUntil(10_000) { ui.onAllNodes(androidx.compose.ui.test.hasTestTag("settings")).fetchSemanticsNodes().isNotEmpty() }
        ui.onNodeWithTag("settings").performClick()
    }

    @Test fun addDeviceShowsWorkingCode() {
        openSettings()
        ui.onNodeWithTag("addDevice").performScrollTo().performClick()
        ui.waitUntil(10_000) { ui.onAllNodes(androidx.compose.ui.test.hasTestTag("newCode")).fetchSemanticsNodes().isNotEmpty() }
        val node = ui.onNodeWithTag("newCode").fetchSemanticsNode()
        val code = node.config[androidx.compose.ui.semantics.SemanticsProperties.Text].single().text
        ui.onNodeWithTag("qr").performScrollTo().assertIsDisplayed()
        runBlocking {
            val acc: Account = de.suitithub.share.core.pair(TestEnv.server, code, TestEnv.uniqueName("New"), Identity.generate("n"), allowInsecureLocal = true)
            assertTrue(acc.token.isNotEmpty())
        }
    }

    @Test fun qrContentIsThePairUri() {
        val u = PairUri("https://x.example:8443", "ABC123")
        val bmp = qrBitmap(u.toString(), 300)
        val px = IntArray(bmp.width * bmp.height).also { bmp.getPixels(it, 0, bmp.width, 0, 0, bmp.width, bmp.height) }
        val src = com.google.zxing.RGBLuminanceSource(bmp.width, bmp.height, px)
        val text = com.google.zxing.MultiFormatReader().decode(com.google.zxing.BinaryBitmap(com.google.zxing.common.HybridBinarizer(src))).text
        assertEquals(u, PairUri.parse(text))
    }

    @Test fun renameReachesTheServerAndSurvives() {
        openSettings()
        val name = TestEnv.uniqueName("Renamed")
        ui.onNodeWithTag("deviceName").performTextReplacement(name)
        ui.onNodeWithTag("rename").performClick()
        ui.waitUntil(10_000) { runBlocking { app.engine.value!!.api.devices() }.any { it.name == name } }
        ui.waitUntil(10_000) { de.suitithub.share.data.SecureStore(TestEnv.ctx).load()?.name == name }
    }

    @Test fun unpairAsksAndReturnsToPairScreen() {
        openSettings()
        val acc = app.engine.value!!.api.account
        ui.onNodeWithTag("unpair").performScrollTo().performClick()
        assertTrue(app.engine.value != null)
        ui.onNode(hasText("Abmelden") and androidx.compose.ui.test.hasAnyAncestor(androidx.compose.ui.test.isDialog())).performClick()
        ui.waitUntil(15_000) { app.engine.value == null }
        ui.onNodeWithTag("url").assertIsDisplayed()
        assertTrue(runBlocking { runCatching { Api(acc).devices() } }.isFailure)
    }

    @Test fun unpairWithoutServerWorksLocally() {
        app.setAccount(Account("https://127.0.0.1:1", "d", "t", "n", Identity.generate("n")))
        launch()
        ui.onNodeWithTag("settings").performClick()
        ui.onNodeWithTag("unpair").performScrollTo().performClick()
        ui.onNode(hasText("Abmelden") and androidx.compose.ui.test.hasAnyAncestor(androidx.compose.ui.test.isDialog())).performClick()
        ui.waitUntil(15_000) { app.engine.value == null }
    }

    @Test fun withoutFirebaseSettingsSayWhatThatMeans() {
        openSettings()
        ui.onNodeWithText("nicht eingerichtet", substring = true).performScrollTo().assertIsDisplayed()
    }
}
