package de.suitithub.share.core

import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import okhttp3.Call
import okhttp3.Callback
import okhttp3.Dispatcher
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okio.BufferedSink
import java.io.IOException
import java.io.InputStream
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds
import kotlin.time.toJavaDuration

/** A paired device. */
data class Account(val serverUrl: String, val deviceId: String, val token: String, val name: String, val identity: Identity) {
    // never leak the token through logs
    override fun toString() = "Account($serverUrl, $deviceId, $name)"
}

/** The server answered with an error status. */
class ApiException(val status: Int, message: String) : IOException(message)

private val localHosts = setOf("10.0.2.2", "localhost", "127.0.0.1")

/** Adds https:// if missing and strips trailing slash, query and fragment; http only for local test servers. */
fun normalizeServerUrl(input: String, allowInsecureLocal: Boolean = false): String {
    val raw = input.trim()
    require(raw.isNotEmpty() && raw.none { it.isWhitespace() }) { "invalid server url" }
    val url = (if ("://" in raw) raw else "https://$raw").toHttpUrl()
    require(url.isHttps || (allowInsecureLocal && url.host in localHosts)) { "https required" }
    return url.newBuilder().username("").password("").query(null).fragment(null).build().toString().trimEnd('/')
}

/** Registers this device with a pairing code. */
suspend fun pair(
    serverUrl: String,
    code: String,
    name: String,
    identity: Identity,
    client: OkHttpClient = OkHttpClient(),
    allowInsecureLocal: Boolean = false,
): Account {
    val url = normalizeServerUrl(serverUrl, allowInsecureLocal)
    val http = Http(url, null, client)
    val r: PairResponse = http.json("POST", "/v1/pair", PairRequest(code.trim().uppercase(), name, DeviceType.ANDROID, identity.fingerprint))
    return Account(url, r.deviceId, r.token, name, identity)
}

/** REST calls of docs/protocol.md. */
class Api(val account: Account, baseClient: OkHttpClient = OkHttpClient()) {
    private val http = Http(account.serverUrl, account.token, baseClient)

    /** Shared client for the event connection. */
    internal val client get() = http.client
    internal fun request(path: String) = http.request(path)

    suspend fun devices(): List<Device> = http.json("GET", "/v1/devices")
    suspend fun updateMe(name: String? = null, fcmToken: String? = null) =
        http.unit("PATCH", "/v1/devices/me", ShareJson.encodeToString(UpdateDeviceRequest(name, fcmToken)))
    suspend fun deleteMe() = http.unit("DELETE", "/v1/devices/me")
    suspend fun pairCode(): PairCodeResponse = http.json("POST", "/v1/pair/codes")

    suspend fun createTransfer(r: CreateTransferRequest): CreateTransferResponse = http.json("POST", "/v1/transfers", r)
    suspend fun incoming(): List<Transfer> = http.json("GET", "/v1/transfers?role=incoming")
    suspend fun outgoing(): List<Transfer> = http.json("GET", "/v1/transfers?role=outgoing")
    suspend fun transfer(id: String): Transfer = http.json("GET", "/v1/transfers/$id")
    suspend fun cancel(id: String) = http.unit("DELETE", "/v1/transfers/$id")
    suspend fun accept(id: String): AcceptResponse = http.json("POST", "/v1/transfers/$id/accept")
    suspend fun decline(id: String) = http.unit("POST", "/v1/transfers/$id/decline")
    suspend fun progress(id: String, bytes: Long, path: Path) =
        http.unit("POST", "/v1/transfers/$id/progress", ShareJson.encodeToString(ProgressRequest(bytes, path)))
    suspend fun delivered(id: String) = http.unit("POST", "/v1/transfers/$id/delivered")
    suspend fun fail(id: String) = http.unit("POST", "/v1/transfers/$id/fail")

    internal fun relayCall(id: String, n: Int, offset: Long, readTimeout: Duration = 30.seconds): Call {
        val r = http.request("/v1/transfers/$id/files/$n")
        if (offset > 0) r.header("Range", "bytes=$offset-")
        return http.streaming.newBuilder().readTimeout(readTimeout.toJavaDuration()).build().newCall(r.build())
    }

    /** Opens the relay stream of file n from offset; the caller closes the response. */
    suspend fun relayDownload(id: String, n: Int, offset: Long): Response = relayCall(id, n, offset).await().checkRange(offset)

    /** Streams the rest of a file (exactly length bytes) to a waiting relay download. */
    suspend fun relayUpload(relayId: String, length: Long, open: () -> InputStream) {
        val body = object : RequestBody() {
            override fun contentType() = "application/octet-stream".toMediaType()
            override fun contentLength() = length
            override fun isOneShot() = true
            override fun writeTo(sink: BufferedSink) {
                open().use { input ->
                    val buf = ByteArray(BUFFER)
                    var left = length
                    while (left > 0) {
                        val n = input.read(buf, 0, minOf(left, buf.size.toLong()).toInt())
                        if (n < 0) throw IOException("file shorter than offered")
                        sink.write(buf, 0, n)
                        left -= n
                    }
                }
            }
        }
        http.streaming.newCall(http.request("/v1/relay/$relayId").post(body).build()).await().use { it.check() }
    }

    /** Releases idle connections and threads. */
    fun close() {
        http.client.dispatcher.executorService.shutdown()
        http.client.connectionPool.evictAll()
    }
}

internal const val BUFFER = 256 * 1024

internal class Http(private val base: String, private val token: String?, baseClient: OkHttpClient) {
    val client: OkHttpClient = baseClient.newBuilder()
        // relay streams are calls to the same host; the default of 5 would starve everything else
        .dispatcher(Dispatcher().apply { maxRequests = 128; maxRequestsPerHost = 128 })
        .connectTimeout(10, TimeUnit.SECONDS)
        .pingInterval(20, TimeUnit.SECONDS)
        .build()
    private val calls = client.newBuilder().callTimeout(30, TimeUnit.SECONDS).build()
    val streaming: OkHttpClient = client.newBuilder()
        .retryOnConnectionFailure(false)
        .readTimeout(5, TimeUnit.MINUTES) // an upload is answered only after the receiver has taken everything
        .writeTimeout(1, TimeUnit.MINUTES)
        .build()

    fun request(path: String): Request.Builder =
        Request.Builder().url(base + path).apply { if (token != null) header("Authorization", "Bearer $token") }

    suspend fun raw(method: String, path: String, body: String? = null): String {
        val rb = body?.toRequestBody(JSON) ?: if (method == "GET" || method == "DELETE") null else ByteArray(0).toRequestBody()
        return calls.newCall(request(path).method(method, rb).build()).await { it.check().body.string() }
    }

    suspend fun unit(method: String, path: String, body: String? = null) {
        raw(method, path, body)
    }

    suspend inline fun <reified O> json(method: String, path: String): O = decode(raw(method, path))
    suspend inline fun <reified I, reified O> json(method: String, path: String, body: I): O =
        decode(raw(method, path, ShareJson.encodeToString(body)))

    inline fun <reified O> decode(s: String): O = try {
        ShareJson.decodeFromString<O>(s)
    } catch (e: IllegalArgumentException) { // SerializationException; e.g. a captive portal answering 200 with HTML
        throw IOException("unexpected answer from server", e)
    }

    private companion object {
        val JSON = "application/json".toMediaType()
    }
}

/** Runs the call; cancelling the coroutine cancels it. read runs on OkHttp's thread and the response is closed afterwards. */
internal suspend fun <T> Call.await(read: (Response) -> T): T = suspendCancellableCoroutine { cont ->
    cont.invokeOnCancellation { cancel() }
    enqueue(object : Callback {
        override fun onFailure(call: Call, e: IOException) {
            if (cont.isActive) cont.resumeWithException(e)
        }

        override fun onResponse(call: Call, response: Response) {
            val r = runCatching { response.use(read) }
            if (cont.isActive) r.fold({ cont.resume(it) }, { cont.resumeWithException(it) })
        }
    })
}

/** Like [await], but leaves the response open for the caller. */
internal suspend fun Call.await(): Response = suspendCancellableCoroutine { cont ->
    cont.invokeOnCancellation { cancel() }
    enqueue(object : Callback {
        override fun onFailure(call: Call, e: IOException) {
            if (cont.isActive) cont.resumeWithException(e)
        }

        override fun onResponse(call: Call, response: Response) {
            cont.resume(response) { _, r, _ -> r.close() }
        }
    })
}

/** Throws (and closes) if the status is not 2xx. */
internal fun Response.check(): Response {
    if (isSuccessful) return this
    val text = use { it.peekBody(4096).string() }
    val msg = try {
        ((ShareJson.parseToJsonElement(text) as? JsonObject)?.get("error") as? JsonPrimitive)?.contentOrNull
    } catch (_: SerializationException) {
        null
    }
    throw ApiException(code, msg?.takeIf { it.isNotBlank() } ?: "HTTP $code")
}

/** Accepts 200 for a full and 206 for a resumed download. */
internal fun Response.checkRange(offset: Long): Response {
    check()
    if (code == (if (offset > 0) 206 else 200)) return this
    close()
    throw IOException("unexpected status $code for offset $offset")
}
