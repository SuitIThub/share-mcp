package de.suitithub.share.core

import java.io.File
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.nio.file.Files
import java.util.concurrent.TimeUnit

/** Runs the real share-server binary (built by the buildGo task) on a free local port. */
class GoServer : AutoCloseable {
    private val dir = Files.createTempDirectory("share-go").toFile()
    private val port = ServerSocket(0).use { it.localPort }
    private val config = File(dir, "config.toml").apply {
        writeText("listen = \"127.0.0.1:$port\"\ndb_path = \"${File(dir, "share.db").path.replace('\\', '/')}\"\n")
    }
    private var process: Process? = null
    val url = "http://127.0.0.1:$port"
    val log = File(dir, "server.log")

    init {
        start()
    }

    private fun start() {
        val p = ProcessBuilder(serverExe.path, "-config", config.path, "serve")
            .redirectErrorStream(true).redirectOutput(ProcessBuilder.Redirect.appendTo(log)).start()
        process = p
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(15)
        while (true) {
            try {
                Socket().use { it.connect(InetSocketAddress("127.0.0.1", port), 200) }
                return
            } catch (e: java.io.IOException) {
                check(p.isAlive && System.nanoTime() < deadline) { "share-server did not start: " + log.readText() }
                Thread.sleep(20)
            }
        }
    }

    fun stop() {
        process?.destroyForcibly()?.waitFor()
        process = null
    }

    fun restart() {
        stop()
        start()
    }

    fun pairCode(): String {
        val p = ProcessBuilder(serverExe.path, "-config", config.path, "pair").redirectErrorStream(true).start()
        val out = p.inputStream.bufferedReader().readText()
        check(p.waitFor() == 0) { out }
        return Regex("Code:\\s+(\\S+)").find(out)!!.groupValues[1]
    }

    suspend fun account(name: String): Account = pair(url, pairCode(), name, Identity.generate(name), allowInsecureLocal = true)

    override fun close() {
        stop()
        dir.deleteRecursively()
    }

    companion object {
        private val goDir = File(System.getProperty("share.go.dir"))
        private fun exe(name: String) = File(goDir, "$name.exe").takeIf { it.exists() } ?: File(goDir, name)
        val serverExe get() = exe("share-server")
        val shareExe get() = exe("share")
    }
}
