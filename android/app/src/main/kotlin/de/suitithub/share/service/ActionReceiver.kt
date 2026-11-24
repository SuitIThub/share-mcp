package de.suitithub.share.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.net.Uri
import de.suitithub.share.R
import de.suitithub.share.shareApp
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import java.io.IOException

/** Notification buttons that need no foreground service. */
class ActionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val app = context.shareApp
        val id = intent.data?.schemeSpecificPart ?: return
        if (intent.action != DECLINE) return
        val engine = app.engine.value ?: return app.notifications.removeOffer(id)
        val pending = goAsync()
        app.scope.launch {
            try {
                withTimeoutOrNull(8_000) { engine.decline(id) } ?: throw IOException("timeout")
                app.notifications.removeOffer(id)
            } catch (_: IOException) {
                app.tell(app.getString(R.string.no_connection))
            } finally {
                pending.finish()
            }
        }
    }

    companion object {
        private const val DECLINE = "de.suitithub.share.DECLINE"

        fun declineIntent(context: Context, id: String): Intent =
            Intent(context, ActionReceiver::class.java).setAction(DECLINE).setData(Uri.fromParts("offer", id, null))
    }
}
