package de.suitithub.share.service

import android.Manifest
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.rule.GrantPermissionRule
import de.suitithub.share.TestEnv
import de.suitithub.share.TestEnv.eventually
import de.suitithub.share.core.Account
import de.suitithub.share.core.Identity
import de.suitithub.share.core.Kind
import de.suitithub.share.core.Transfer
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class PushServiceTest {
    @get:Rule val permission: GrantPermissionRule = GrantPermissionRule.grant(Manifest.permission.POST_NOTIFICATIONS)
    private val app = TestEnv.app
    private val push = PushHandler(app)
    private lateinit var peer: TestEnv.Peer

    @Before fun setUp() {
        TestEnv.notifications.cancelAll()
        peer = TestEnv.Peer.create()
        runBlocking { TestEnv.pairApp() }
    }

    @After fun tearDown() {
        app.setAccount(null)
        peer.engine.close()
        TestEnv.notifications.cancelAll()
    }

    private fun offer(): Transfer = runBlocking { peer.engine.offerText(Kind.TEXT, "hallo", listOf(app.engine.value!!.api.account.name), false) }

    private fun message(type: String, id: String) = runBlocking { push.onMessage(mapOf("type" to type, "transfer_id" to id)) }

    private fun quiet() {
        Thread.sleep(300)
        assertEquals(emptyList<String>(), TestEnv.titles())
    }

    @Test fun offerNewShowsNotification() {
        val t = offer()
        message("offer.new", t.id)
        eventually("shown") { "Text von ${peer.account.name}" in TestEnv.titles() }
        assertFalse("push needs no connection of its own", app.engine.value!!.state.value.connected)
    }

    @Test fun offerRemovedClearsIt() {
        val t = offer()
        message("offer.new", t.id)
        eventually("shown") { TestEnv.active(t.id) != null }
        message("offer.removed", t.id)
        eventually("gone") { TestEnv.active(t.id) == null }
        assertTrue(app.engine.value!!.state.value.incoming.isEmpty())
    }

    @Test fun alreadyGoneOfferShowsNothing() {
        val t = offer()
        runBlocking { peer.engine.cancelOutgoing(t.id) }
        message("offer.new", t.id)
        message("offer.new", "unknown")
        quiet()
    }

    @Test fun answeredOfferShowsNothing() {
        val t = offer()
        runBlocking { app.engine.value!!.decline(t.id) }
        message("offer.new", t.id)
        quiet()
    }

    @Test fun unknownTypeIgnored() {
        message("offer.future", offer().id)
        runBlocking {
            push.onMessage(emptyMap())
            push.onMessage(mapOf("type" to "offer.new"))
        }
        quiet()
    }

    @Test fun withoutAccountIgnored() {
        val t = offer()
        app.setAccount(null)
        message("offer.new", t.id)
        message("offer.removed", t.id)
        assertFalse(runBlocking { push.onToken("x") })
        quiet()
    }

    @Test fun visibleUiShowsNoNotificationButListsTheOffer() {
        val t = offer()
        app.uiShown().use {
            message("offer.new", t.id)
            quiet()
        }
        assertEquals(listOf(t.id), app.engine.value!!.state.value.incoming.map { it.id })
    }

    @Test fun duplicateFromSocketAndPushShowsOnce() {
        app.engine.value!!.hold().use {
            eventually("connected") { app.engine.value!!.state.value.connected }
            val t = offer()
            eventually("by socket") { TestEnv.active(t.id) != null }
            message("offer.new", t.id)
            Thread.sleep(300)
            assertEquals(1, TestEnv.titles().size)
        }
    }

    @Test fun unreachableServerShowsGenericHint() {
        app.setAccount(Account("https://127.0.0.1:1", "d", "t", "n", Identity.generate("n")))
        message("offer.new", "x")
        eventually("hint") { "Neues Angebot" in TestEnv.titles() }
        assertTrue(TestEnv.notifications.activeNotifications.single().notification.timeoutAfter in 500_000..600_000)
        message("offer.removed", "x")
        eventually("hint gone") { TestEnv.titles().isEmpty() }
    }

    @Test fun tokenIsReported() = assertTrue(runBlocking { push.onToken("token-1") })

    @Test fun withoutFirebaseAppStarts() {
        assertFalse(pushAvailable(TestEnv.ctx))
        assertNull(runBlocking { push.currentToken() })
    }
}
