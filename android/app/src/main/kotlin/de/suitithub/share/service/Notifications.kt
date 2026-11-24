package de.suitithub.share.service

import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import de.suitithub.share.R
import de.suitithub.share.core.Download
import de.suitithub.share.core.EngineState
import de.suitithub.share.core.Kind
import de.suitithub.share.core.Phase
import de.suitithub.share.core.Transfer
import de.suitithub.share.ui.Format
import de.suitithub.share.ui.MainActivity
import de.suitithub.share.ui.Results
import java.time.Duration
import java.time.Instant

class Notifications(private val context: Context) {
    private val nm = NotificationManagerCompat.from(context)

    init {
        nm.createNotificationChannels(
            listOf(
                NotificationChannel(OFFERS, context.getString(R.string.channel_offers), NotificationManager.IMPORTANCE_HIGH),
                NotificationChannel(TRANSFERS, context.getString(R.string.channel_transfers), NotificationManager.IMPORTANCE_LOW),
                NotificationChannel(RESULTS, context.getString(R.string.channel_results), NotificationManager.IMPORTANCE_DEFAULT),
            ),
        )
    }

    val enabled get() = nm.areNotificationsEnabled()

    /** Without the permission the system drops it; the offer is still listed in the app. */
    @SuppressLint("MissingPermission")
    private fun post(tag: String?, id: Int, n: Notification) = nm.notify(tag, id, n)

    private fun builder(channel: String) = NotificationCompat.Builder(context, channel)
        .setSmallIcon(R.drawable.ic_stat)
        .setContentIntent(PendingIntent.getActivity(context, 0, Intent(context, MainActivity::class.java), FLAGS))

    private fun activity(i: Intent) = PendingIntent.getActivity(context, 0, i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), FLAGS)

    fun offer(t: Transfer, sender: String) {
        val left = Duration.between(Instant.now(), t.expiresAt).toMillis()
        if (left <= 0) return
        val body = Format.offerBody(t)
        post(
            t.id, OFFER,
            builder(OFFERS)
                .setContentTitle(Format.offerTitle(t, sender))
                .setContentText(body)
                .setStyle(NotificationCompat.BigTextStyle().bigText(body))
                .setSubText(Format.remaining(t.expiresAt, Instant.now()))
                .setCategory(NotificationCompat.CATEGORY_MESSAGE)
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .setTimeoutAfter(left)
                .setOnlyAlertOnce(true)
                .addAction(0, context.getString(R.string.decline), PendingIntent.getBroadcast(context, 0, ActionReceiver.declineIntent(context, t.id), FLAGS))
                .addAction(0, context.getString(R.string.accept), PendingIntent.getForegroundService(context, 0, TransferService.acceptIntent(context, t.id), FLAGS))
                .build(),
        )
    }

    fun removeOffer(id: String) = nm.cancel(id, OFFER)

    fun result(d: Download) {
        val name = Format.shortName(d.transfer)
        val b = builder(RESULTS).setAutoCancel(true)
        if (d.phase == Phase.DONE) {
            b.setContentTitle(context.getString(R.string.received, name))
            b.setContentText(listOf(Format.size(d.transfer.totalSize), Format.path(d.path)).filter { it.isNotEmpty() }.joinToString(" · "))
            val open = activity(Results.openIntent(d))
            b.setContentIntent(open)
            if (Results.isApk(d)) b.addAction(0, context.getString(R.string.install), open)
        } else {
            b.setContentTitle(context.getString(R.string.failed, name))
            b.setContentText(d.error.orEmpty())
        }
        post("r:" + d.transfer.id, RESULT, b.build())
    }

    /** An accepted text or link, already in the clipboard. */
    fun text(t: Transfer, sender: String) {
        val b = builder(RESULTS).setAutoCancel(true)
            .setContentTitle(context.getString(if (t.kind == Kind.LINK) R.string.link_copied else R.string.text_copied, sender))
            .setContentText(Format.offerBody(t))
        if (t.kind == Kind.LINK && Format.isWebLink(t.text)) {
            b.addAction(0, context.getString(R.string.open), activity(Intent(Intent.ACTION_VIEW, Uri.parse(t.text))))
        }
        post("r:" + t.id, RESULT, b.build())
    }

    fun unavailable() = message(context.getString(R.string.unavailable))

    fun message(title: String, text: String = "") =
        post(null, MESSAGE, builder(RESULTS).setAutoCancel(true).setContentTitle(title).setContentText(text).setStyle(NotificationCompat.BigTextStyle().bigText(text)).setTimeoutAfter(60_000).build())

    /** The foreground service's notification. */
    fun progress(s: EngineState): Notification {
        val offers = s.outgoing.filter { it.kind == Kind.FILES }
        val downloads = s.downloads.filter { it.phase == Phase.RUNNING }
        val b = builder(TRANSFERS).setOngoing(true).setOnlyAlertOnce(true).setCategory(NotificationCompat.CATEGORY_PROGRESS)
        if (offers.size + downloads.size > 0) b.setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
        when {
            offers.size + downloads.size > 1 -> b.setContentTitle(context.getString(R.string.transfers, offers.size + downloads.size))
            offers.size == 1 -> {
                val t = offers[0]
                val now = Instant.now()
                b.setContentTitle(context.getString(R.string.offering, Format.shortName(t)))
                b.setContentText(if (t.expiresAt > now) Format.remaining(t.expiresAt, now) else context.getString(R.string.transferring))
                b.addCancel(t.id)
            }
            downloads.size == 1 -> {
                val d = downloads[0]
                val p = Format.percent(d.bytes, d.transfer.totalSize)
                b.setContentTitle(Format.shortName(d.transfer))
                b.setContentText(listOf("$p %", Format.path(d.path)).filter { it.isNotEmpty() }.joinToString(" · "))
                b.setProgress(100, p, false)
                b.addCancel(d.transfer.id)
            }
            else -> b.setContentTitle(context.getString(R.string.app_name))
        }
        return b.build()
    }

    private fun NotificationCompat.Builder.addCancel(id: String) =
        addAction(0, context.getString(R.string.cancel), PendingIntent.getForegroundService(context, 0, TransferService.cancelIntent(context, id), FLAGS))

    fun updateProgress(s: EngineState) = post(null, PROGRESS, progress(s))

    companion object {
        const val OFFER = 1
        const val RESULT = 2
        const val PROGRESS = 3
        const val MESSAGE = 4
        private const val OFFERS = "offers"
        private const val TRANSFERS = "transfers"
        private const val RESULTS = "results"
        private const val FLAGS = PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
    }
}
