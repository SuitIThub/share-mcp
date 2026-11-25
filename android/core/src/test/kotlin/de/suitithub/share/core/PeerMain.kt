package de.suitithub.share.core

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import java.io.File
import kotlin.system.exitProcess

/**
 * A Kotlin device for the Windows client's interop tests.
 * usage: url code name lan|relay send <file> <target>  |  url code name lan|relay receive <dir>
 */
fun main(args: Array<String>): Unit = runBlocking {
    val (url, code, name, mode, what) = args
    val api = Api(pair(url, code, name, Identity.generate(name), allowInsecureLocal = true))
    val dir = File(if (what == "receive") args[5] else System.getProperty("java.io.tmpdir"), "kotlin-$name").apply { mkdirs() }
    val e = Engine(
        api, DirStore(dir), CoroutineScope(SupervisorJob() + Dispatchers.Default),
        EngineOptions(endpointPort = 0, candidates = { if (mode == "lan") listOf("127.0.0.1:$it") else emptyList() }),
    )
    e.hold()
    withTimeout(60_000) {
        e.state.first { it.connected && it.devices.isNotEmpty() }
        println("READY")
        if (what == "send") {
            val t = e.offerFiles(listOf(FileOnDisk(File(args[5]))), listOf(args[6]), false)
            val ended = e.state.first { s -> s.sent.any { it.id == t.id } }.sent.single()
            println("ENDED ${ended.deliveries.single().status} ${ended.deliveries.single().path}")
        } else {
            val t = e.state.first { it.incoming.isNotEmpty() }.incoming.first()
            e.accept(t.id)
            val d = e.state.first { s -> s.downloads.any { it.phase != Phase.RUNNING } }.downloads.single()
            println("ENDED ${d.phase} ${d.path} ${d.refs.singleOrNull()}")
        }
        delay(500)
    }
    e.close()
    exitProcess(0)
}
