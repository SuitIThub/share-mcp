package de.suitithub.share.core

import java.io.IOException
import java.io.InputStream
import java.io.OutputStream

/** Something that can be offered: readable from any offset as long as the offer lasts. */
interface FileSource {
    val item: FileItem

    /** False if the content no longer matches what was offered. */
    fun unchanged(): Boolean

    fun openAt(offset: Long): InputStream

    /** Called exactly once when the offer has ended. */
    fun release() {}
}

/** An open offer of this device. */
class Offer(val transfer: Transfer, val secret: ByteArray, val sources: List<FileSource>)

/** The storage cannot be used right now; worth another try, unlike other storage errors. */
class StorageBusyException(message: String, cause: Throwable? = null) : IOException(message, cause)

/** One file being downloaded. Its state survives a restart of the process. */
interface Part {
    /** Reference to the finished file, or null while it is incomplete. */
    val finishedRef: String?

    /** Bytes stored so far; 0 only if there really is nothing. Throws [StorageBusyException]. */
    fun size(): Long

    /** Continues at exactly this offset: anything stored behind it is cut off. Throws [StorageBusyException] if less is stored. */
    fun append(offset: Long): OutputStream

    /** Drops the stored bytes. */
    fun reset()

    /** Moves the complete file to its final place and returns a reference to it. */
    fun finish(): String
}

/** Where downloads go. */
interface DownloadStore {
    fun part(transferId: String, index: Int, item: FileItem): Part

    /** Discards incomplete parts and all state of this offer. */
    fun forget(transferId: String)

    /** [forget]s every offer except these. */
    fun retain(transferIds: Set<String>)
}
