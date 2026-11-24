package de.suitithub.share.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant

class ProtocolTest {
    private val secret = ByteArray(32) { 1 }
    private val goToken = "d1.9c4cc17b1fd7497fa6248a0cdccf057c9f63b1b4aa9547b5e06aa7457be89383"

    @Test fun decodesTransferWithOffsetAndNanoTimes() {
        val t = ShareJson.decodeFromString<Transfer>(
            """{"id":"a","sender_id":"s","kind":"files","files":[{"name":"x","size":3,"mime":"","mod_time":"2026-11-24T10:00:00.123456789+01:00"}],"broadcast":false,"created_at":"2026-11-24T09:00:00Z","expires_at":"2026-11-24T09:10:00Z","closed":false,"deliveries":null,"brandneu":1}""",
        )
        assertEquals(Instant.parse("2026-11-24T09:00:00.123456789Z"), t.files[0].modTime)
        assertEquals(emptyList<Delivery>(), t.deliveries)
        assertEquals(3L, t.totalSize)
    }

    @Test fun omitsUnsetOptionalFields() =
        assertEquals("""{"fcm_token":"t"}""", ShareJson.encodeToString(UpdateDeviceRequest(fcmToken = "t")))

    @Test fun encodesRequiredFieldsOfCreateRequest() {
        val json = ShareJson.encodeToString(CreateTransferRequest(kind = Kind.TEXT, text = "hi", broadcast = false, targets = listOf("x")))
        assertEquals("""{"kind":"text","text":"hi","targets":["x"],"broadcast":false}""", json)
    }

    @Test fun alwaysEncodesModTime() =
        assertTrue(ShareJson.encodeToString(FileItem("a", 1)).contains("\"mod_time\":\"1970-01-01T00:00:00Z\""))

    @Test fun decodesNullLanCandidates() = assertEquals(
        emptyList<String>(),
        ShareJson.decodeFromString<AcceptResponse>("""{"lan_candidates":null,"cert_fingerprint":"","access_token":"","relay_base":""}""").lanCandidates,
    )

    @Test fun decodesDeliveryWithUnknownPath() {
        val d = ShareJson.decodeFromString<Delivery>("""{"device_id":"d","status":"downloading","path":"teleport","bytes":5,"updated_at":"2026-11-24T09:00:00Z"}""")
        assertNull(d.path)
        assertEquals(Status.DOWNLOADING, d.status)
    }

    @Test fun accessTokenMatchesGoReference() = assertEquals(goToken, AccessToken.create(secret, "t1", "d1"))

    @Test fun verifyRejectsForeignTransferTamperedAndGarbage() {
        assertEquals("d1", AccessToken.verify(secret, "t1", goToken))
        assertNull(AccessToken.verify(secret, "t2", goToken))
        assertNull(AccessToken.verify(secret, "t1", goToken.dropLast(1) + "4"))
        for (bad in listOf("", ".", "d1.zz", "d1.", ".abcd", "d1.9c4")) assertNull(bad, AccessToken.verify(secret, "t1", bad))
    }

    @Test fun finalStatuses() {
        val final = setOf(Status.DELIVERED, Status.DECLINED, Status.MISSED, Status.CANCELLED, Status.FAILED)
        for (s in Status.entries) if (s in final) assertTrue("$s", s.isFinal) else assertFalse("$s", s.isFinal)
    }
}
