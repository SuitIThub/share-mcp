package de.suitithub.share.core

import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.io.RandomAccessFile
import java.nio.channels.Channels
import java.time.Instant
import java.util.concurrent.atomic.AtomicInteger
import kotlin.random.Random

fun randomBytes(size: Int, seed: Int = 1): ByteArray = Random(seed).nextBytes(size)

/** A file on disk as offer source. */
class FileOnDisk(val file: File, name: String = file.name) : FileSource {
    override val item = FileItem(name, file.length(), "application/octet-stream", Instant.ofEpochMilli(file.lastModified()))
    val released = AtomicInteger()
    val opened = java.util.concurrent.CopyOnWriteArrayList<Long>()
    override fun unchanged() = file.length() == item.size && Instant.ofEpochMilli(file.lastModified()) == item.modTime
    override fun openAt(offset: Long): InputStream {
        opened += offset
        val raf = RandomAccessFile(file, "r")
        raf.seek(offset)
        return Channels.newInputStream(raf.channel)
    }

    override fun release() {
        released.incrementAndGet()
    }
}

/** Serves bytes slowly so tests can interrupt a transfer in the middle. */
class SlowSource(private val inner: FileSource, private val chunk: Int, private val delayMs: Long) : FileSource by inner {
    override fun openAt(offset: Long): InputStream {
        val input = inner.openAt(offset)
        return object : InputStream() {
            override fun read(): Int = input.read()
            override fun read(b: ByteArray, off: Int, len: Int): Int {
                Thread.sleep(delayMs)
                return input.read(b, off, minOf(len, chunk))
            }

            override fun close() = input.close()
        }
    }
}

/** Downloads into a directory; parts and markers live in dir/.parts. */
class DirStore(private val dir: File) : DownloadStore {
    private val parts = File(dir, ".parts").apply { mkdirs() }

    override fun part(transferId: String, index: Int, item: FileItem) = object : Part {
        private val part = File(parts, "$transferId-$index.part")
        private val marker = File(parts, "$transferId-$index.done")
        override val finishedRef get() = marker.takeIf { it.exists() }?.readText()
        override fun size() = part.length()
        override fun append(): OutputStream = FileOutputStream(part, true)
        override fun reset() {
            part.delete()
        }

        override fun finish(): String {
            part.createNewFile()
            val target = File(dir, uniqueName(safeFileName(item.name)) { File(dir, it).exists() })
            check(part.renameTo(target))
            marker.writeText(target.path)
            return target.path
        }
    }

    override fun forget(transferId: String) {
        parts.listFiles()!!.filter { it.name.startsWith("$transferId-") }.forEach { it.delete() }
    }

    override fun retain(transferIds: Set<String>) {
        parts.listFiles()!!.filter { it.name.substringBeforeLast('-') !in transferIds }.forEach { it.delete() }
    }

    fun leftovers() = parts.listFiles()!!.map { it.name }
    fun files() = dir.listFiles()!!.filter { it.isFile }.map { it.name }.sorted()
}
