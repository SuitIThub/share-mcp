package de.suitithub.share.data

import android.content.ContentResolver
import android.content.Context
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import android.system.ErrnoException
import android.system.Os
import android.system.OsConstants
import android.webkit.MimeTypeMap
import de.suitithub.share.core.FileItem
import de.suitithub.share.core.FileSource
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.time.Instant
import java.util.UUID
import java.util.concurrent.locks.ReentrantReadWriteLock
import kotlin.concurrent.read
import kotlin.concurrent.write

/** The shared item cannot be read; the message is shown to the user. */
class SourceException(message: String, cause: Throwable? = null) : IOException(message, cause)

/**
 * A content:// item. Its file descriptor stays open for the whole offer, so the temporary permission that came with
 * a share intent may expire. Items that are not plain files are copied to the cache first.
 */
class ContentSource private constructor(
    private val pfd: ParcelFileDescriptor,
    override val item: FileItem,
    private val tempCopy: File?,
) : FileSource {
    private var released = false

    /** Nobody may read while the descriptor is being closed: its number could be reused for another file. */
    private val lock = ReentrantReadWriteLock()

    override fun unchanged(): Boolean = lock.read {
        !released && try {
            Os.fstat(pfd.fileDescriptor).st_size == item.size && mtime(pfd) == item.modTime
        } catch (_: ErrnoException) {
            false
        }
    }

    /** Positional reads, so any number of readers can share the descriptor. */
    override fun openAt(offset: Long): InputStream = object : InputStream() {
        private var pos = offset
        override fun read(): Int {
            val one = ByteArray(1)
            return if (read(one, 0, 1) < 0) -1 else one[0].toInt() and 0xff
        }

        override fun read(b: ByteArray, off: Int, len: Int): Int {
            if (len == 0) return 0
            while (true) {
                try {
                    val n = lock.read {
                        if (released) throw IOException("released")
                        Os.pread(pfd.fileDescriptor, b, off, len, pos)
                    }
                    if (n <= 0) return -1
                    pos += n
                    return n
                } catch (e: ErrnoException) {
                    if (e.errno != OsConstants.EINTR) throw IOException(e)
                }
            }
        }
    }

    override fun release() = lock.write {
        if (released) return
        released = true
        pfd.close()
        tempCopy?.delete()
        Unit
    }

    companion object {
        /** Opens the item; may copy it, so not on the main thread. */
        fun open(context: Context, uri: Uri): ContentSource {
            val cr = context.contentResolver
            if (uri.scheme != ContentResolver.SCHEME_CONTENT) throw SourceException("Diese Art von Datei kann nicht gelesen werden")
            try {
                val mime = cr.getType(uri)
                val name = displayName(cr, uri, mime)
                val type = mime ?: mimeOf(name)
                plainFile(cr, uri)?.let { return ContentSource(it, item(name, type, it), null) }
                return copy(context, cr, uri, name, type)
            } catch (e: SourceException) {
                throw e
            } catch (e: Exception) { // providers throw whatever they like
                throw SourceException("„${uri.lastPathSegment?.substringAfterLast('/') ?: uri}“ kann nicht gelesen werden", e)
            }
        }

        /** Where temporary copies live. */
        fun tempDir(context: Context) = File(context.cacheDir, "send")

        private fun item(name: String, mime: String, pfd: ParcelFileDescriptor) =
            FileItem(name, Os.fstat(pfd.fileDescriptor).st_size, mime, mtime(pfd))

        private fun mtime(pfd: ParcelFileDescriptor): Instant =
            Os.fstat(pfd.fileDescriptor).st_mtim.let { Instant.ofEpochSecond(it.tv_sec, it.tv_nsec) }

        /** The descriptor if it is a seekable regular file. */
        private fun plainFile(cr: ContentResolver, uri: Uri): ParcelFileDescriptor? {
            val pfd = cr.openFileDescriptor(uri, "r") ?: throw SourceException("Datei kann nicht gelesen werden")
            val regular = try {
                OsConstants.S_ISREG(Os.fstat(pfd.fileDescriptor).st_mode) && Os.lseek(pfd.fileDescriptor, 0, OsConstants.SEEK_CUR) >= 0
            } catch (_: ErrnoException) {
                false
            }
            if (regular) return pfd
            pfd.close()
            return null
        }

        private fun copy(context: Context, cr: ContentResolver, uri: Uri, name: String, mime: String): ContentSource {
            val tmp = File(tempDir(context).apply { mkdirs() }, UUID.randomUUID().toString())
            try {
                (cr.openInputStream(uri) ?: throw SourceException("Datei kann nicht gelesen werden")).use { input ->
                    tmp.outputStream().use { input.copyTo(it, 256 * 1024) }
                }
                val pfd = ParcelFileDescriptor.open(tmp, ParcelFileDescriptor.MODE_READ_ONLY)
                return ContentSource(pfd, item(name, mime, pfd), tmp)
            } catch (e: Throwable) {
                tmp.delete()
                throw e
            }
        }

        private fun displayName(cr: ContentResolver, uri: Uri, mime: String?): String {
            val known = cr.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
                val i = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                if (i >= 0 && c.moveToFirst()) c.getString(i) else null
            }
            if (!known.isNullOrBlank()) return known
            val base = uri.lastPathSegment?.substringAfterLast('/')?.takeIf { it.isNotBlank() } ?: "datei"
            val ext = mime?.let { MimeTypeMap.getSingleton().getExtensionFromMimeType(it) }
            return if ('.' in base || ext == null) base else "$base.$ext"
        }
    }
}

/** MIME type by file extension. */
internal fun mimeOf(name: String): String =
    MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase()) ?: "application/octet-stream"
