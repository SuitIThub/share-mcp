package de.suitithub.share.ui

import de.suitithub.share.core.Kind
import de.suitithub.share.core.Path
import de.suitithub.share.core.Status
import de.suitithub.share.core.Transfer
import java.time.Duration
import java.time.Instant
import java.util.Locale

/** Texts shared by notifications and screens. */
object Format {
    fun size(bytes: Long): String {
        if (bytes < 1024) return "$bytes B"
        var v = bytes / 1024.0
        val units = listOf("KB", "MB", "GB", "TB")
        var i = 0
        while (v >= 1023.95 && i < units.lastIndex) {
            v /= 1024
            i++
        }
        return String.format(Locale.GERMANY, "%.1f %s", v, units[i])
    }

    fun remaining(expires: Instant, now: Instant): String {
        val s = Duration.between(now, expires).seconds
        return when {
            s <= 0 -> "abgelaufen"
            s <= 60 -> "noch $s s"
            else -> "noch ${(s + 59) / 60} Min"
        }
    }

    fun offerTitle(t: Transfer, sender: String): String = when (t.kind) {
        Kind.TEXT -> "Text von $sender"
        Kind.LINK -> "Link von $sender"
        Kind.FILES -> if (t.files.size == 1) "Datei von $sender" else "${t.files.size} Dateien von $sender"
    }

    fun offerBody(t: Transfer): String {
        val body = when (t.kind) {
            Kind.TEXT, Kind.LINK -> shorten(t.text, 100)
            Kind.FILES ->
                if (t.files.size == 1) "${t.files[0].name}, ${size(t.totalSize)}"
                else "${t.files.size} Dateien, ${size(t.totalSize)}: " + shorten(t.files.take(50).joinToString(", ") { it.name }, 200)
        }
        return if (t.message.isBlank()) body else "$body\n${shorten(t.message, 200)}"
    }

    fun shortName(t: Transfer): String = when (t.kind) {
        Kind.TEXT -> "Text"
        Kind.LINK -> "Link"
        Kind.FILES -> t.files.firstOrNull()?.name.orEmpty() + if (t.files.size > 1) " +${t.files.size - 1}" else ""
    }

    /** 100 only when complete. */
    fun percent(done: Long, total: Long): Int = if (done >= total) 100 else (done.toDouble() / total * 100).toInt().coerceIn(0, 99)

    fun path(p: Path?): String = when (p) {
        Path.LAN -> "lokal"
        Path.RELAY -> "über Server"
        null -> ""
    }

    fun status(s: Status, p: Path?): String = when (s) {
        Status.WAITING -> "wartet"
        Status.ACCEPTED -> "angenommen"
        Status.DOWNLOADING -> if (p == null) "lädt" else "lädt (${path(p)})"
        Status.DELIVERED -> "zugestellt"
        Status.DECLINED -> "abgelehnt"
        Status.MISSED -> "verpasst"
        Status.CANCELLED -> "abgebrochen"
        Status.FAILED -> "fehlgeschlagen"
    }

    /** Links of any other scheme are only copied, never opened. */
    fun isWebLink(s: String): Boolean = Regex("https?://\\S+", RegexOption.IGNORE_CASE).matches(s)

    /** At most max code points. */
    private fun shorten(s: String, max: Int): String =
        if (s.codePointCount(0, s.length) <= max) s else s.substring(0, s.offsetByCodePoints(0, max)) + "…"
}
