package de.suitithub.share.data

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import de.suitithub.share.core.Account
import de.suitithub.share.core.Identity
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class SecureStoreTest {
    private val ctx = InstrumentationRegistry.getInstrumentation().targetContext
    private val store = SecureStore(ctx, "account-test.bin")
    private val file = File(ctx.filesDir, "account-test.bin")
    private val acc = Account("https://x.example", "dev1", "SECRET-TOKEN-123", "Pixel ü", Identity.generate("Pixel"))

    @Before @After fun clean() {
        file.delete()
    }

    @Test fun roundTrip() {
        assertNull(store.load())
        store.save(acc)
        val got = SecureStore(ctx, "account-test.bin").load()!!
        assertEquals(acc.serverUrl, got.serverUrl)
        assertEquals(acc.deviceId, got.deviceId)
        assertEquals(acc.token, got.token)
        assertEquals(acc.name, got.name)
        assertEquals(acc.identity.fingerprint, got.identity.fingerprint)
    }

    @Test fun overwriteKeepsTheLatest() {
        store.save(acc)
        store.save(acc.copy(name = "Neu"))
        assertEquals("Neu", store.load()!!.name)
    }

    @Test fun fileContainsNoPlaintextToken() {
        store.save(acc)
        val raw = file.readBytes().toString(Charsets.ISO_8859_1)
        assertFalse("SECRET-TOKEN" in raw)
        assertFalse("PRIVATE KEY" in raw)
        assertFalse("x.example" in raw)
    }

    @Test fun corruptFileLoadsAsNull() {
        for (bytes in listOf(ByteArray(0), ByteArray(5), ByteArray(500) { 3 })) {
            file.writeBytes(bytes)
            assertNull(store.load())
        }
        store.save(acc)
        file.writeBytes(file.readBytes().also { it[it.size - 1] = (it[it.size - 1] + 1).toByte() })
        assertNull(store.load())
    }

    @Test fun clearRemoves() {
        store.save(acc)
        store.clear()
        assertFalse(file.exists())
        assertNull(store.load())
        store.clear()
        assertTrue(true)
    }
}
