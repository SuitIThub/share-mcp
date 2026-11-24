package de.suitithub.share.ui

import android.app.DownloadManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import de.suitithub.share.core.Download
import de.suitithub.share.core.safeFileName
import de.suitithub.share.data.mimeOf

/** What can be done with a finished download. */
object Results {
    private const val APK = "application/vnd.android.package-archive"

    fun isApk(d: Download) = d.refs.size == 1 && mimeOf(safeFileName(d.transfer.files[0].name)) == APK

    /** Opens the file (for an APK: the system's install dialog), or the Downloads list if there are several. */
    fun openIntent(d: Download): Intent = if (d.refs.size != 1) Intent(DownloadManager.ACTION_VIEW_DOWNLOADS) else
        Intent(Intent.ACTION_VIEW)
            .setDataAndType(Uri.parse(d.refs[0]), mimeOf(safeFileName(d.transfer.files[0].name)))
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)

    /** False if no app can open it. */
    fun open(context: Context, d: Download): Boolean = try {
        context.startActivity(openIntent(d).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        true
    } catch (_: android.content.ActivityNotFoundException) {
        false
    } catch (_: SecurityException) {
        false
    }
}
