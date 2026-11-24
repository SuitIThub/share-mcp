package de.suitithub.share.ui

import android.net.Uri
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.test.SemanticsMatcher
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertIsOff
import androidx.compose.ui.test.assertIsOn
import androidx.compose.ui.test.hasScrollToNodeAction
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.StateRestorationTester
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollToNode
import androidx.compose.ui.test.performTextInput
import androidx.test.ext.junit.runners.AndroidJUnit4
import de.suitithub.share.core.Delivery
import de.suitithub.share.core.Device
import de.suitithub.share.core.DeviceType
import de.suitithub.share.core.Download
import de.suitithub.share.core.EngineState
import de.suitithub.share.core.FileItem
import de.suitithub.share.core.Kind
import de.suitithub.share.core.Path
import de.suitithub.share.core.Phase
import de.suitithub.share.core.Status
import de.suitithub.share.core.Transfer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.time.Instant

@RunWith(AndroidJUnit4::class)
class MainScreenTest {
    @get:Rule val ui = createComposeRule()
    private val calls = ArrayList<String>()
    private var state by mutableStateOf(EngineState())

    private val actions = object : MainActions {
        override fun accept(id: String) { calls += "accept $id" }
        override fun decline(id: String) { calls += "decline $id" }
        override fun cancelOutgoing(id: String) { calls += "cancelOutgoing $id" }
        override fun cancelDownload(id: String) { calls += "cancelDownload $id" }
        override fun dismiss(id: String) { calls += "dismiss $id" }
        override fun sendFiles(uris: List<Uri>, targets: List<String>, broadcast: Boolean) { calls += "files $uris $targets $broadcast" }
        override fun sendText(text: String, targets: List<String>, broadcast: Boolean) { calls += "text $text $targets $broadcast" }
        override fun openResult(d: Download) { calls += "open ${d.transfer.id}" }
        override fun openSettings() { calls += "settings" }
    }

    private val desk = Device("d1", "Desktop", DeviceType.WINDOWS, online = true)
    private val tab = Device("d2", "Tablet", DeviceType.ANDROID)
    private val names = mapOf("d1" to "Desktop", "d2" to "Tablet", "me" to "Ich")

    private fun files(id: String, vararg f: Pair<String, Long>, message: String = "", sender: String = "d1", deliveries: List<Delivery> = emptyList()) = Transfer(
        id, sender, Kind.FILES, message = message, files = f.map { FileItem(it.first, it.second) }, expiresAt = Instant.now().plusSeconds(480), deliveries = deliveries,
    )

    private fun show(s: EngineState) {
        state = s
        ui.setContent { ShareTheme { MainScreen(state, actions) } }
    }

    private fun scrollTo(m: SemanticsMatcher) = ui.onNode(hasScrollToNodeAction()).performScrollToNode(m)

    private fun click(text: String) {
        scrollTo(hasText(text))
        ui.onNodeWithText(text).performClick()
    }

    @Test fun showsIncomingWithDetails() {
        show(EngineState(true, devices = listOf(desk), names = names, incoming = listOf(files("t1", "a.txt" to 1024, "b.bin" to 2048, message = "schau mal"))))
        ui.onNodeWithText("2 Dateien von Desktop").assertIsDisplayed()
        ui.onNodeWithText("noch 8 Min").assertIsDisplayed()
        ui.onNodeWithText("2 Dateien von Desktop").performClick()
        ui.onNodeWithText("a.txt · 1,0 KB").assertIsDisplayed()
        ui.onNodeWithText("b.bin · 2,0 KB").assertIsDisplayed()
        ui.onNodeWithText("schau mal").assertIsDisplayed()
        assertTrue(calls.isEmpty())
    }

    @Test fun acceptAndDeclineCallActions() {
        show(EngineState(true, names = names, incoming = listOf(files("t1", "a.txt" to 1), files("t2", "b.txt" to 1))))
        ui.onNodeWithTag("accept:t1").performClick()
        ui.onNodeWithTag("decline:t2").performClick()
        assertEquals(listOf("accept t1", "decline t2"), calls)
    }

    @Test fun detailsCanAccept() {
        show(EngineState(true, names = names, incoming = listOf(files("t1", "a.txt" to 1))))
        ui.onNodeWithText("Datei von Desktop").performClick()
        ui.onNodeWithTag("detailAccept").performClick()
        assertEquals(listOf("accept t1"), calls)
    }

    @Test fun detailsCloseWhenTheOfferIsGone() {
        show(EngineState(true, names = names, incoming = listOf(files("t1", "a.txt" to 1))))
        ui.onNodeWithText("Datei von Desktop").performClick()
        state = EngineState(true, names = names)
        ui.waitForIdle()
        ui.onNodeWithTag("detailAccept").assertDoesNotExist()
    }

    @Test fun showsProgressPathAndPerReceiverStatus() {
        show(
            EngineState(
                true, devices = listOf(desk, tab), names = names,
                outgoing = listOf(
                    files(
                        "o1", "app.apk" to 1000, sender = "me",
                        deliveries = listOf(Delivery("d1", Status.DOWNLOADING, Path.LAN, 500), Delivery("d2", Status.DECLINED), Delivery("zz", Status.WAITING)),
                    ),
                ),
                downloads = listOf(Download(files("d", "video.mp4" to 1000), Phase.RUNNING, 430, Path.RELAY)),
            ),
        )
        ui.onNodeWithText("Desktop: lädt (lokal) 50 %").assertIsDisplayed()
        ui.onNodeWithText("Tablet: abgelehnt").assertIsDisplayed()
        ui.onNodeWithText("Unbekanntes Gerät: wartet").assertIsDisplayed()
        scrollTo(hasText("43 % · über Server"))
        ui.onNodeWithTag("cancel:o1").performClick()
        ui.onNodeWithTag("cancel:d").performClick()
        assertEquals(listOf("cancelOutgoing o1", "cancelDownload d"), calls)
    }

    private fun withDevices() = EngineState(true, devices = listOf(desk, tab), names = names)

    @Test fun sendDisabledWithoutTarget() {
        show(withDevices())
        scrollTo(hasTestTag("sendText"))
        ui.onNodeWithTag("sendText").assertIsNotEnabled()
        ui.onNodeWithTag("sendFiles").assertIsNotEnabled()
        scrollTo(hasTestTag("target:d2"))
        ui.onNodeWithTag("target:d2").performClick()
        scrollTo(hasTestTag("sendText"))
        ui.onNodeWithTag("sendText").assertIsEnabled()
        ui.onNodeWithTag("sendFiles").assertIsEnabled()
    }

    private fun text(s: String) {
        scrollTo(hasTestTag("sendText"))
        ui.onNodeWithTag("sendText").performClick()
        ui.onNodeWithTag("text").performTextInput(s)
        ui.onNodeWithTag("sendTextNow").performClick()
    }

    @Test fun sendsTextToSeveralTargets() {
        show(withDevices())
        ui.onNodeWithTag("target:d1").performClick()
        ui.onNodeWithTag("target:d2").performClick()
        text("hallo")
        assertEquals(listOf("text hallo [d1, d2] false"), calls)
    }

    @Test fun allClearsSingleTargets() {
        show(withDevices())
        ui.onNodeWithTag("target:d1").performClick()
        ui.onNodeWithTag("target:all").performClick()
        ui.onNodeWithTag("target:d1").assertIsOff()
        text("x")
        assertEquals(listOf("text x [] true"), calls)
        ui.onNodeWithTag("target:d1").performClick()
        ui.onNodeWithTag("target:all").assertIsOff()
        ui.onNodeWithTag("target:d1").assertIsOn()
    }

    @Test fun vanishedDeviceIsNoTargetAnyMore() {
        show(withDevices())
        ui.onNodeWithTag("target:d2").performClick()
        state = state.copy(devices = listOf(desk))
        ui.waitForIdle()
        scrollTo(hasTestTag("sendText"))
        ui.onNodeWithTag("sendText").assertIsNotEnabled()
    }

    @Test fun emptyTextCannotBeSent() {
        show(withDevices())
        ui.onNodeWithTag("target:d1").performClick()
        scrollTo(hasTestTag("sendText"))
        ui.onNodeWithTag("sendText").performClick()
        ui.onNodeWithTag("sendTextNow").assertIsNotEnabled()
        ui.onNodeWithTag("text").performTextInput("   ")
        ui.onNodeWithTag("sendTextNow").assertIsNotEnabled()
    }

    @Test fun finishedApkOffersInstall() {
        show(EngineState(true, names = names, downloads = listOf(Download(files("t1", "app.apk" to 1), Phase.DONE, 1, Path.LAN, listOf("content://x")))))
        click("Installieren")
        assertEquals(listOf("open t1"), calls)
    }

    @Test fun finishedFileOffersOpen() {
        show(EngineState(true, names = names, downloads = listOf(Download(files("t1", "a.pdf" to 1), Phase.DONE, 1, Path.LAN, listOf("content://x")))))
        click("Öffnen")
        ui.onNodeWithTag("dismiss:t1").performClick()
        assertEquals(listOf("open t1", "dismiss t1"), calls)
    }

    @Test fun failedShowsMessageAndDismiss() {
        show(EngineState(true, names = names, downloads = listOf(Download(files("t1", "a.pdf" to 1), Phase.FAILED, error = "Speicher voll"))))
        ui.onNodeWithText("Speicher voll").assertIsDisplayed()
        ui.onNodeWithTag("dismiss:t1").performClick()
        assertEquals(listOf("dismiss t1"), calls)
    }

    @Test fun emptyState() {
        show(EngineState())
        ui.onNodeWithText("verbinde …").assertIsDisplayed()
        ui.onNodeWithText("Keine offenen Anfragen").assertIsDisplayed()
        ui.onNodeWithText("Noch kein anderes Gerät gekoppelt").assertIsDisplayed()
        ui.onNodeWithTag("settings").performClick()
        assertEquals(listOf("settings"), calls)
    }

    @Test fun manyEntriesScroll() {
        show(
            EngineState(
                true, devices = (1..30).map { Device("x$it", "Gerät $it", DeviceType.ANDROID) }, names = names,
                incoming = (1..30).map { files("t$it", "f$it" to 1) },
            ),
        )
        scrollTo(hasTestTag("accept:t30"))
        ui.onNodeWithTag("accept:t30").performClick()
        scrollTo(hasTestTag("target:x30"))
        ui.onNodeWithTag("target:x30").performClick()
        assertEquals(listOf("accept t30"), calls)
    }

    @Test fun survivesRotation() {
        val restore = StateRestorationTester(ui)
        state = withDevices()
        restore.setContent { ShareTheme { MainScreen(state, actions) } }
        ui.onNodeWithTag("target:d2").performClick()
        scrollTo(hasTestTag("sendText"))
        ui.onNodeWithTag("sendText").performClick()
        ui.onNodeWithTag("text").performTextInput("halb fertig")
        restore.emulateSavedInstanceStateRestore()
        ui.onNodeWithTag("text").assertIsDisplayed()
        ui.onNodeWithTag("sendTextNow").performClick()
        assertEquals(listOf("text halb fertig [d2] false"), calls)
    }
}
