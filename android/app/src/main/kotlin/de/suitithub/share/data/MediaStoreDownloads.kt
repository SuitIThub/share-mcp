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
import de.suitithub.share.core.StorageBusyException
import de.suitithub.share.core.safeFileName
import java.io.FileNotFoundException
import java.io.IOException
import java.io.OutputStream
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter

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

        /** Null only if the row is really gone; anything else that keeps us from reading it is temporary. */
        private fun length(uri: Uri): Long? = try {
            cr.openFileDescriptor(uri, "r")?.use { it.statSize } ?: throw StorageBusyException("no descriptor")
        } catch (e: FileNotFoundException) {
            if (exists(uri)) throw StorageBusyException("cannot open $name", e) else null
        } catch (e: RuntimeException) {
            if (exists(uri)) throw StorageBusyException("cannot open $name", e) else null
        }

        private fun exists(uri: Uri): Boolean = try {
            @Suppress("DEPRECATION") // its replacement needs API 30
            cr.query(MediaStore.setIncludePending(uri), arrayOf(MediaStore.Downloads._ID), null, null, null)?.use { it.moveToFirst() }
                ?: throw StorageBusyException("no answer from the media provider")
        } catch (e: RuntimeException) {
            throw StorageBusyException("no answer from the media provider", e)
        }

        override fun append(offset: Long): OutputStream = io {
            val known = pending()?.takeIf { length(it) != null }
            if (known == null && offset > 0) throw StorageBusyException("$name is gone")
            val pfd = cr.openFileDescriptor(known ?: create(), "rw") ?: throw IOException("cannot open $name")
            val out = ParcelFileDescriptor.AutoCloseOutputStream(pfd)
            try {
                if (out.channel.size() < offset) throw StorageBusyException("$name is shorter than expected")
                out.channel.truncate(offset)
                out.channel.position(offset)
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
            cr.openFileDescriptor(uri, "rw")?.close() // a row without a file could neither be read nor published
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
            val uri = pending()?.takeIf { length(it) != null } ?: create()
            try {
                publish(uri, null)
            } catch (_: IllegalStateException) {
                publish(uri, timestamped()) // MediaStore gives up after "name (32)"
            }
            store(DONE + uri)
            uri.toString()
        }

        private fun publish(uri: Uri, newName: String?) {
            val values = ContentValues().apply {
                put(MediaStore.Downloads.IS_PENDING, 0)
                if (newName != null) put(MediaStore.Downloads.DISPLAY_NAME, newName)
            }
            if (cr.update(uri, values, null, null) != 1) throw IOException("cannot finish $name")
        }

        private fun timestamped(): String {
            val dot = name.lastIndexOf('.').takeIf { it > 0 } ?: name.length
            val now = LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyy-MM-dd HH-mm-ss-SSS"))
            return name.substring(0, dot) + " ($now)" + name.substring(dot)
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
