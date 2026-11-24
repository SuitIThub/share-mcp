package de.suitithub.share.data

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.os.ParcelFileDescriptor
import android.provider.MediaStore
import de.suitithub.share.core.DownloadStore
import de.suitithub.share.core.FileItem
import de.suitithub.share.core.Part
import de.suitithub.share.core.safeFileName
import java.io.FileNotFoundException
import java.io.IOException
import java.io.OutputStream

/**
 * Downloads directly into the public Download folder. Incomplete files are "pending" MediaStore rows, invisible to
 * other apps; which row belongs to which file of which offer is remembered in preferences.
 */
class MediaStoreDownloads(context: Context, prefsName: String = "downloads") : DownloadStore {
    private val cr = context.contentResolver
    private val prefs = context.getSharedPreferences(prefsName, Context.MODE_PRIVATE)

    override fun part(transferId: String, index: Int, item: FileItem): Part = Row("$transferId-$index", item)

    override fun forget(transferId: String) = drop { it == transferId }

    override fun retain(transferIds: Set<String>) = drop { it !in transferIds }

    private fun drop(which: (String) -> Boolean) {
        val edit = prefs.edit()
        for ((key, value) in prefs.all) {
            if (!which(key.substringBeforeLast('-'))) continue
            if (value is String && !value.startsWith(DONE)) tryDelete(Uri.parse(value))
            edit.remove(key)
        }
        edit.commit()
    }

    private fun tryDelete(uri: Uri) {
        try {
            cr.delete(uri, null, null)
        } catch (_: RuntimeException) {
            // already gone, or no longer ours
        }
    }

    private inner class Row(private val key: String, private val item: FileItem) : Part {
        private val name = safeFileName(item.name)

        override val finishedRef: String?
            get() = prefs.getString(key, null)?.takeIf { it.startsWith(DONE) }?.removePrefix(DONE)?.takeIf { length(Uri.parse(it)) != null }

        private fun pending(): Uri? = prefs.getString(key, null)?.takeIf { !it.startsWith(DONE) }?.let { Uri.parse(it) }

        override fun size(): Long = pending()?.let { length(it) } ?: 0

        private fun length(uri: Uri): Long? = try {
            cr.openFileDescriptor(uri, "r")?.use { it.statSize }
        } catch (_: FileNotFoundException) {
            null
        } catch (_: RuntimeException) { // not ours (any more)
            null
        }

        override fun append(): OutputStream = io {
            val uri = pending()?.takeIf { length(it) != null } ?: create()
            val pfd = cr.openFileDescriptor(uri, "rw") ?: throw IOException("cannot open $name")
            val out = ParcelFileDescriptor.AutoCloseOutputStream(pfd)
            try {
                out.channel.position(out.channel.size())
            } catch (e: IOException) {
                out.close()
                throw e
            }
            out
        }

        private fun create(): Uri {
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, name)
                // by extension, not what the sender claims: a mismatch would make MediaStore append another extension
                put(MediaStore.Downloads.MIME_TYPE, mimeOf(name))
                put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS)
                put(MediaStore.Downloads.IS_PENDING, 1)
            }
            val uri = cr.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: throw IOException("cannot create $name")
            store(uri.toString())
            return uri
        }

        private fun store(value: String) {
            if (!prefs.edit().putString(key, value).commit()) throw IOException("cannot store state")
        }

        override fun reset() {
            pending()?.let { tryDelete(it) }
            prefs.edit().remove(key).commit()
        }

        override fun finish(): String = io {
            // a row without a file (empty download) would vanish on publishing
            val uri = pending()?.takeIf { length(it) != null } ?: create().also { cr.openFileDescriptor(it, "rw")?.close() }
            if (cr.update(uri, ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }, null, null) != 1) {
                throw IOException("cannot finish $name")
            }
            store(DONE + uri)
            uri.toString()
        }

        private fun <T> io(block: () -> T): T = try {
            block()
        } catch (e: RuntimeException) { // MediaStore reports most problems as unchecked exceptions
            throw IOException(e)
        }
    }

    private companion object {
        const val DONE = "done:"
    }
}
