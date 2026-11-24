package de.suitithub.share.core

import java.io.InputStream

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
