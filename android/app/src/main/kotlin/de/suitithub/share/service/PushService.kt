package de.suitithub.share.service

import android.content.Context
import com.google.firebase.FirebaseApp
import com.google.firebase.messaging.FirebaseMessaging
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import de.suitithub.share.ShareApp
import de.suitithub.share.shareApp
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeout
import java.io.IOException
import kotlin.coroutines.resume

/** False if the app was built without google-services.json. */
fun pushAvailable(context: Context): Boolean = FirebaseApp.getApps(context).isNotEmpty()

/** Wakes the app for new offers. The message carries only the offer's ID; the details come from our own server. */
class PushService : FirebaseMessagingService() {
    override fun onMessageReceived(message: RemoteMessage) = runBlocking { PushHandler(shareApp).onMessage(message.data) }

    override fun onNewToken(token: String) {
        runBlocking { PushHandler(shareApp).onToken(token) }
    }
}

class PushHandler(private val app: ShareApp) {
    suspend fun onMessage(data: Map<String, String>) {
        val engine = app.engine.value ?: return
        val id = data["transfer_id"] ?: return
        when (data["type"]) {
            "offer.new" -> try {
                val t = withTimeout(15_000) { engine.offerDetails(id) } ?: return
                if (!app.uiVisible) app.announce(engine, t)
            } catch (_: IOException) {
                app.notifications.hint(id)
            } catch (_: kotlinx.coroutines.TimeoutCancellationException) {
                app.notifications.hint(id)
            }
            "offer.removed" -> {
                engine.forgetOffer(id)
                app.notifications.removeOffer(id)
                app.notifications.removeHint(id)
            }
        }
    }

    /** Reports the token to the server; on failure the next connection tries again. */
    suspend fun onToken(token: String): Boolean {
        val engine = app.engine.value ?: return false
        return try {
            withTimeout(15_000) { engine.api.updateMe(fcmToken = token) }
            true
        } catch (_: IOException) {
            false
        } catch (_: kotlinx.coroutines.TimeoutCancellationException) {
            false
        }
    }

    suspend fun currentToken(): String? {
        if (!pushAvailable(app)) return null
        return suspendCancellableCoroutine { c ->
            FirebaseMessaging.getInstance().token.addOnCompleteListener { c.resume(if (it.isSuccessful) it.result else null) }
        }
    }
}
