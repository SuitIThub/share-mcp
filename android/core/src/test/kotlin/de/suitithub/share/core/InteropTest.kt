package de.suitithub.share.core

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit

/** The Kotlin client against the Go CLI and against itself, through the real server. */
class InteropTest {
    @get:Rule val tmp = TemporaryFolder()
    private lateinit var go: GoServer
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val engines = ArrayList<Engine>()
    private val notices = CopyOnWriteArrayList<Notice>()

    @Before fun setUp() {
        go = GoServer()
    }

    @After fun tearDown() {
        engines.forEach { it.close() }
        scope.cancel()
        go.close()
    }

    private suspend fun eventually(what: String, seconds: Int = 20, cond: suspend () -> Boolean) {
        val end = System.nanoTime() + seconds * 1_000_000_000L
        while (!cond()) {
            if (System.nanoTime() > end) throw AssertionError("timeout: $what; ${engines.map { it.state.value }} $notices")
            delay(10)
        }
    }

    private suspend fun engine(name: String, account: Account? = null): Engine {
        val e = testEngine(Api(account ?: go.account(name)), DirStore(tmp.newFolder("dl-$name")), scope)
        engines += e
        scope.launch { e.notices.collect { notices += it } }
        e.hold()
        eventually("$name connected") { e.state.value.connected }
        return e
    }

    private fun dir(name: String) = File(tmp.root, "dl-$name")

    private fun cli(vararg args: String): Process {
        val cmd = listOf(GoServer.shareExe.path, "-config", File(tmp.root, "cli.json").path) + args
        return ProcessBuilder(cmd).redirectErrorStream(true).redirectOutput(File(tmp.root, "cli.log")).start()
    }

    private fun Process.exit(): Int {
        assertTrue("cli hangs: " + File(tmp.root, "cli.log").readText(), waitFor(30, TimeUnit.SECONDS))
        return exitValue()
    }

    private fun pairCli() = assertEquals(0, cli("pair", "-name", "Bot", go.url, go.pairCode()).exit())

    private fun file(size: Int, name: String) = tmp.newFile(name).apply { writeBytes(randomBytes(size, name.hashCode())) }

    private suspend fun acceptFirst(e: Engine): Transfer {
        eventually("offer") { e.state.value.incoming.isNotEmpty() }
        return e.state.value.incoming.first().also { e.accept(it.id) }
    }

    private suspend fun done(e: Engine, seconds: Int = 30): Download {
        eventually("done", seconds) { e.state.value.downloads.singleOrNull()?.let { it.phase != Phase.RUNNING } == true }
        return e.state.value.downloads.single().also { assertEquals(it.error, Phase.DONE, it.phase) }
    }

    @Test fun goCliSendsFilesOverLan() = runBlocking {
        val pix = engine("Pixel")
        pairCli()
        val big = file(20_000_000, "a.bin")
        val empty = file(0, "leer.txt")
        val uml = file(1000, "ümläut ß.txt")
        val p = cli("send", big.path, empty.path, uml.path, "--to", "pix")
        acceptFirst(pix)
        val d = done(pix)
        assertEquals("Bot", pix.deviceName(d.transfer.senderId))
        assertEquals(Path.LAN, d.path)
        assertArrayEquals(big.readBytes(), File(dir("Pixel"), "a.bin").readBytes())
        assertEquals(0, File(dir("Pixel"), "leer.txt").length())
        assertArrayEquals(uml.readBytes(), File(dir("Pixel"), "ümläut ß.txt").readBytes())
        assertEquals(0, p.exit())
    }

    @Test fun goCliSendsTextAndLink() = runBlocking {
        val pix = engine("Pixel")
        pairCli()
        assertEquals(0, cli("text", "Hallo Welt", "--to", "Pixel").exit())
        assertEquals(0, cli("link", "https://example.org/a?b=c", "--all").exit())
        repeat(2) { n ->
            eventually("offers") { pix.state.value.incoming.size == 2 - n }
            acceptFirst(pix)
        }
        eventually("received") { notices.filterIsInstance<Notice.TextReceived>().size == 2 }
        val got = notices.filterIsInstance<Notice.TextReceived>().map { it.transfer.kind to it.transfer.text }.toSet()
        assertEquals(setOf(Kind.TEXT to "Hallo Welt", Kind.LINK to "https://example.org/a?b=c"), got)
    }

    @Test fun broadcastToTwoEngines() = runBlocking {
        val sender = engine("Desk")
        val x = engine("X")
        val y = engine("Y")
        val z = engine("Z")
        val lateAccount = go.account("Late")
        val f = file(8_000_000, "b.bin")
        val t = sender.offerFiles(listOf(FileOnDisk(f)), emptyList(), true)
        eventually("all see it") { listOf(x, y, z).all { it.state.value.incoming.size == 1 } }
        z.decline(t.id)
        x.accept(t.id)
        y.accept(t.id)
        done(x)
        done(y)
        assertArrayEquals(f.readBytes(), File(dir("X"), "b.bin").readBytes())
        assertArrayEquals(f.readBytes(), File(dir("Y"), "b.bin").readBytes())
        eventually("sender knows") {
            sender.state.value.outgoing.single().deliveries.associate { sender.deviceName(it.deviceId) to it.status } ==
                mapOf("X" to Status.DELIVERED, "Y" to Status.DELIVERED, "Z" to Status.DECLINED, "Late" to Status.WAITING)
        }
        // a device that comes online late still gets it
        val late = engine("Late", lateAccount)
        eventually("late sees it") { late.state.value.incoming.size == 1 }
    }

    @Test fun switchesPathMidTransfer() = runBlocking {
        val sender = engine("Desk")
        val x = engine("X")
        val f = file(64_000_000, "c.bin")
        sender.offerFiles(listOf(SlowSource(FileOnDisk(f), 1_000_000, 15)), listOf("X"), false)
        acceptFirst(x)
        eventually("lan running") { x.state.value.downloads.single().let { it.bytes > 5_000_000 && it.path == Path.LAN } }
        sender.loseLanForTest()
        val d = done(x, 60)
        assertEquals(Path.RELAY, d.path)
        assertArrayEquals(f.readBytes(), File(dir("X"), "c.bin").readBytes())
    }

    @Test fun senderDropsConnectionBriefly() = runBlocking {
        val x = engine("X")
        val acc = go.account("Desk")
        val f = file(3_000_000, "d.bin")
        val desk = Sender(Api(acc), endpointPort = 0, candidates = { emptyList() })
        var events = scope.launch { Api(acc).events().collect { desk.onEvent(it, scope) } }
        desk.offerFiles(listOf(SlowSource(FileOnDisk(f), 100_000, 30)), listOf("X"), false)
        acceptFirst(x)
        eventually("relay running") { x.state.value.downloads.single().let { it.bytes > 500_000 && it.path == Path.RELAY } }
        events.cancel()
        delay(5000)
        events = scope.launch { Api(acc).events().collect { desk.onEvent(it, scope) } }
        done(x)
        assertArrayEquals(f.readBytes(), File(dir("X"), "d.bin").readBytes())
        desk.close()
        events.cancel()
    }

    @Test fun serverRestartMidLanDownload() = runBlocking {
        val sender = engine("Desk")
        val x = engine("X")
        val f = file(20_000_000, "e.bin")
        val t = sender.offerFiles(listOf(SlowSource(FileOnDisk(f), 500_000, 50)), listOf("X"), false)
        acceptFirst(x)
        eventually("running") { x.state.value.downloads.single().bytes > 2_000_000 }
        go.restart()
        val d = done(x)
        assertEquals(Path.LAN, d.path)
        assertArrayEquals(f.readBytes(), File(dir("X"), "e.bin").readBytes())
        eventually("delivered") { sender.api.transfer(t.id).deliveries.single().status == Status.DELIVERED }
        eventually("sender closed") { sender.state.value.outgoing.isEmpty() }
    }

    @Test fun serverRestartMidRelayDownload() = runBlocking {
        val x = engine("X")
        val acc = go.account("Desk")
        val f = file(6_000_000, "g.bin")
        val desk = Sender(Api(acc), endpointPort = 0, candidates = { emptyList() })
        val events = scope.launch { Api(acc).events().collect { desk.onEvent(it, scope) } }
        desk.offerFiles(listOf(SlowSource(FileOnDisk(f), 200_000, 40)), listOf("X"), false)
        acceptFirst(x)
        eventually("running") { x.state.value.downloads.single().bytes > 1_000_000 }
        go.restart()
        done(x)
        assertArrayEquals(f.readBytes(), File(dir("X"), "g.bin").readBytes())
        desk.close()
        events.cancel()
    }

    @Test fun cancelWhileDownloading() = runBlocking {
        val sender = engine("Desk")
        val x = engine("X")
        val t = sender.offerFiles(listOf(SlowSource(FileOnDisk(file(20_000_000, "h.bin")), 200_000, 50)), listOf("X"), false)
        acceptFirst(x)
        eventually("running") { x.state.value.downloads.single().bytes > 1_000_000 }
        sender.cancelOutgoing(t.id)
        eventually("ended") { x.state.value.downloads.single().phase == Phase.FAILED }
        assertEquals(emptyList<String>(), dir("X").listFiles()!!.filter { it.isFile }.map { it.name })
    }
}
