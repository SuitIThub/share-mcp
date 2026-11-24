@file:OptIn(ExperimentalSerializationApi::class)

package de.suitithub.share.core

import kotlinx.serialization.EncodeDefault
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.KSerializer
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.PrimitiveSerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import java.security.MessageDigest
import java.time.Instant
import java.time.OffsetDateTime
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/** Wire format; counterpart of server/internal/protocol and docs/protocol.md. */
val ShareJson = Json {
    ignoreUnknownKeys = true
    coerceInputValues = true
    explicitNulls = false
    encodeDefaults = false
}

/** Maximum size of a text/link payload in UTF-8 bytes. */
const val MAX_TEXT_BYTES = 65536

/** RFC 3339 incl. offsets and nanoseconds (Go sends local offsets for file times). */
object InstantSerializer : KSerializer<Instant> {
    override val descriptor = PrimitiveSerialDescriptor("Instant", PrimitiveKind.STRING)
    override fun serialize(encoder: Encoder, value: Instant) = encoder.encodeString(value.toString())
    override fun deserialize(decoder: Decoder): Instant = OffsetDateTime.parse(decoder.decodeString()).toInstant()
}

@Serializable
enum class DeviceType {
    @SerialName("android") ANDROID,
    @SerialName("windows") WINDOWS,
    @SerialName("agent") AGENT,
}

@Serializable
data class Device(
    val id: String,
    val name: String,
    val type: DeviceType,
    val online: Boolean = false,
    @SerialName("last_seen") @Serializable(InstantSerializer::class) val lastSeen: Instant = Instant.EPOCH,
    @SerialName("cert_fingerprint") val certFingerprint: String = "",
)

@Serializable
enum class Kind {
    @SerialName("text") TEXT,
    @SerialName("link") LINK,
    @SerialName("files") FILES,
}

@Serializable
enum class Status {
    @SerialName("waiting") WAITING,
    @SerialName("accepted") ACCEPTED,
    @SerialName("downloading") DOWNLOADING,
    @SerialName("delivered") DELIVERED,
    @SerialName("declined") DECLINED,
    @SerialName("missed") MISSED,
    @SerialName("cancelled") CANCELLED,
    @SerialName("failed") FAILED;

    /** No further transition possible. */
    val isFinal get() = this == DELIVERED || this == DECLINED || this == MISSED || this == CANCELLED || this == FAILED
}

@Serializable
enum class Path {
    @SerialName("lan") LAN,
    @SerialName("relay") RELAY,
}

@Serializable
data class FileItem(
    val name: String,
    val size: Long,
    val mime: String = "",
    @EncodeDefault
    @SerialName("mod_time") @Serializable(InstantSerializer::class) val modTime: Instant = Instant.EPOCH,
)

@Serializable
data class Delivery(
    @SerialName("device_id") val deviceId: String,
    val status: Status,
    val path: Path? = null,
    val bytes: Long = 0,
    @SerialName("updated_at") @Serializable(InstantSerializer::class) val updatedAt: Instant = Instant.EPOCH,
)

@Serializable
data class Transfer(
    val id: String,
    @SerialName("sender_id") val senderId: String,
    val kind: Kind,
    val text: String = "",
    val message: String = "",
    val files: List<FileItem> = emptyList(),
    val broadcast: Boolean = false,
    val targets: List<String> = emptyList(),
    @SerialName("created_at") @Serializable(InstantSerializer::class) val createdAt: Instant = Instant.EPOCH,
    @SerialName("expires_at") @Serializable(InstantSerializer::class) val expiresAt: Instant,
    val closed: Boolean = false,
    val deliveries: List<Delivery> = emptyList(),
) {
    val totalSize: Long get() = files.sumOf { it.size }
}

@Serializable
data class CreateTransferRequest(
    val kind: Kind,
    val text: String = "",
    val message: String = "",
    val files: List<FileItem> = emptyList(),
    val targets: List<String> = emptyList(),
    val broadcast: Boolean,
    val minutes: Int = 0,
    @SerialName("lan_candidates") val lanCandidates: List<String> = emptyList(),
)

@Serializable
data class CreateTransferResponse(val transfer: Transfer, val secret: String)

@Serializable
data class AcceptResponse(
    @SerialName("lan_candidates") val lanCandidates: List<String> = emptyList(),
    @SerialName("cert_fingerprint") val certFingerprint: String = "",
    @SerialName("access_token") val accessToken: String = "",
    @SerialName("relay_base") val relayBase: String = "",
)

@Serializable
data class PairRequest(
    val code: String,
    val name: String,
    val type: DeviceType,
    @SerialName("cert_fingerprint") val certFingerprint: String,
)

@Serializable
data class PairResponse(@SerialName("device_id") val deviceId: String, val token: String)

@Serializable
data class PairCodeResponse(
    val code: String,
    @SerialName("expires_at") @Serializable(InstantSerializer::class) val expiresAt: Instant,
)

@Serializable
data class UpdateDeviceRequest(
    val name: String? = null,
    @SerialName("fcm_token") val fcmToken: String? = null,
)

@Serializable
data class ProgressRequest(val bytes: Long, val path: Path)

@Serializable
data class Event(val type: String, val data: JsonElement = JsonNull)

@Serializable
data class TransferRef(@SerialName("transfer_id") val transferId: String)

@Serializable
data class DeliveryUpdate(@SerialName("transfer_id") val transferId: String, val delivery: Delivery)

@Serializable
data class RelayRequest(
    @SerialName("relay_id") val relayId: String,
    @SerialName("transfer_id") val transferId: String,
    @SerialName("file_index") val fileIndex: Int = 0,
    val offset: Long = 0,
)

/** Per-offer, per-receiver token presented to the sender's local endpoint. */
object AccessToken {
    fun create(secret: ByteArray, transferId: String, deviceId: String): String =
        deviceId + "." + mac(secret, transferId, deviceId).toHex()

    /** Returns the receiver's device ID, or null if the token is invalid. */
    fun verify(secret: ByteArray, transferId: String, token: String): String? {
        val i = token.lastIndexOf('.')
        if (i <= 0) return null
        val deviceId = token.substring(0, i)
        val got = token.substring(i + 1).hexToBytesOrNull() ?: return null
        return deviceId.takeIf { MessageDigest.isEqual(got, mac(secret, transferId, deviceId)) }
    }

    private fun mac(secret: ByteArray, transferId: String, deviceId: String): ByteArray =
        Mac.getInstance("HmacSHA256").run {
            init(SecretKeySpec(secret, "HmacSHA256"))
            doFinal("$transferId|$deviceId".toByteArray())
        }
}

internal fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it) }

internal fun String.hexToBytesOrNull(): ByteArray? {
    if (length % 2 != 0) return null
    val out = ByteArray(length / 2)
    for (i in out.indices) {
        val hi = Character.digit(this[2 * i], 16)
        val lo = Character.digit(this[2 * i + 1], 16)
        if (hi < 0 || lo < 0) return null
        out[i] = (hi * 16 + lo).toByte()
    }
    return out
}
