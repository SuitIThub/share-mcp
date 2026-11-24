# Share – Plan 2: Android-App – Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Native Android-App, die Text, Links und Dateien sendet und (nach Bestätigung) empfängt, als Teilen-Ziel dient und per Firebase geweckt wird.

**Architecture:** Zwei Gradle-Module. `:core` ist reines Kotlin/JVM: Protokoll, API, Ereignisse, lokaler HTTPS-Endpunkt, Sender, Empfänger, `Engine` mit beobachtbarem Zustand – getestet auf der JVM gegen den **echten Go-Server**. `:app` enthält nur Android: Keystore, `content://`-Quellen, MediaStore, Vordergrunddienst, Benachrichtigungen, FCM, Compose-Oberfläche.

**Tech Stack:** AGP 9.5.2 (eingebautes Kotlin), Gradle 9.6.1, Kotlin 2.4.21 (Plugins `plugin.compose`, `plugin.serialization`), Compose BOM 2026.11.01, activity-compose 1.13.0, core-ktx 1.19.1, lifecycle-runtime-compose 2.11.1, OkHttp + okhttp-tls 5.5.1, kotlinx-serialization-json 1.13.0, kotlinx-coroutines 1.11.0, firebase-messaging 26.0.2, google-services-Plugin 4.5.0, zxing-android-embedded 4.3.0; Tests: JUnit 4, kotlinx-coroutines-test, androidx.test.

**Spec:** `docs/superpowers/specs/2026-11-23-share-design.md` – Protokollvertrag: `docs/protocol.md` – Go-Referenz: `server/internal/client`

## Global Constraints

- `minSdk 29`, `targetSdk 36`, `compileSdk 37`, JDK 17 zum Bauen (`JAVA_HOME` zeigt auf dem Rechner auf JDK 8 → für jeden Gradle-Aufruf `JAVA_HOME="/c/Program Files/Eclipse Adoptium/jdk-17.0.17.10-hotspot"` setzen). *(minSdk 29 statt der vorgeschlagenen 26: MediaStore-Downloads ohne Speicherberechtigung. targetSdk 36: ab Ziel 37 wird eine Laufzeitberechtigung für das lokale Netz Pflicht.)*
- Paket/applicationId `de.suitithub.share`; alle Texte deutsch in `res/values/strings.xml`.
- `:core` importiert nichts aus `android.*`/`androidx.*`.
- Nichts landet ohne Bestätigung auf dem Gerät. Annehmen gilt für das ganze Angebot.
- Angebotsdauer höchstens **10 Minuten**. Text/Link höchstens **65536 Bytes** UTF-8.
- Lokaler Endpunkt: bevorzugt Port **47800**, sonst frei; nur aktiv, solange ein eigenes Datei-Angebot offen ist.
- LAN-Probe parallel, **1,5 s**; gepinnter SHA-256-Fingerabdruck des Leaf-Zertifikats; sonst Relay.
- Backoff 1 s … 16 s, solange die Zustellung lebt; `/fail` nur bei Endgültigem (409 vom Sender, Schreibfehler, Nutzerabbruch).
- Token, Schlüssel und Zertifikat liegen nur verschlüsselt auf dem Gerät (AES-GCM, Schlüssel im Android Keystore); `allowBackup=false`. Nie in Logs.
- Server-URL nur `https://`; `http://` nur im Debug-Build und nur für `10.0.2.2`, `localhost`, `127.0.0.1`.
- Die App baut und läuft **ohne** `google-services.json` (dann kein Push; Hinweis in den Einstellungen).
- Kein Verlauf, keine Dateiverwaltung, ein Hauptbildschirm.

## Review Focus

1. **App-Prozess wird während eines großen Downloads beendet**: beim nächsten Öffnen lädt er ab dem Teilstand weiter, fertige Dateien desselben Angebots werden nicht erneut geladen. → Tests in Task 8 und 12.
2. **Geteilte `content://`-Quelle ohne Größe, ohne Änderungszeit oder nicht spulbar** (Cloud-Fotos, Messenger): Senden klappt trotzdem, notfalls über eine temporäre Kopie. → Test in Task 12.
3. **Netzwechsel während der Übertragung** (WLAN → Mobilfunk): Download läuft über den anderen Weg ab dem Offset weiter, Angebot des Senders überlebt ein kurzes Trennen. → Tests in Task 7 und 10.
4. **Annehmen/Ablehnen doppelt, nach Ablauf oder nach Rückzug getippt**: kein Absturz, Benachrichtigung verschwindet, Meldung „nicht mehr verfügbar“. → Tests in Task 9 und 13.
5. **Benachrichtigungen verweigert, kein Firebase, kein Speicherplatz**: App bleibt bedienbar, Angebote stehen in der App, ein Schreibfehler endet als `failed` mit verständlicher Meldung und ohne Teildatei-Reste. → Tests in Task 8, 13 und 14.

---

## Dateistruktur

```
android/
  settings.gradle.kts, build.gradle.kts, gradle.properties, gradle/libs.versions.toml, gradlew(.bat)
  core/src/main/kotlin/de/suitithub/share/core/
    Protocol.kt         Wire-Typen, InstantSerializer, AccessToken
    Names.kt            safeFileName, uniqueName
    LanAddresses.kt     lanCandidates
    PairUri.kt          QR-/Deep-Link-Inhalt lesen und bauen
    Identity.kt         Geräte-Zertifikat, Fingerabdruck, Pinning
    Api.kt              REST-Aufrufe, ApiException, Relay
    Events.kt           WebSocket als Flow
    LocalEndpoint.kt    minimaler HTTPS-Server
    Files.kt            FileSource, DownloadStore, Part
    Sender.kt  Receiver.kt  Engine.kt
  core/src/test/kotlin/…   Tests + GoServer.kt (startet echten share-server), TestFiles.kt
  app/src/main/kotlin/de/suitithub/share/
    ShareApp.kt         Application, EngineHolder
    data/SecureStore.kt  data/ContentSource.kt  data/MediaStoreDownloads.kt
    service/TransferService.kt  service/ActionReceiver.kt  service/Notifications.kt  service/PushService.kt
    ui/MainActivity.kt  ui/MainScreen.kt  ui/PairScreen.kt  ui/SettingsScreen.kt  ui/ShareActivity.kt  ui/Format.kt  ui/Theme.kt
  app/src/test/…  app/src/androidTest/…  app/src/debug/…
docs/android.md         Bauen, Signieren, Firebase, Prüfliste am echten Gerät
```

**Befehle** (in `android/`, `JAVA_HOME` wie oben): Core `./gradlew :core:test`, App-Unit `./gradlew :app:testDebugUnitTest`, Gerät `./gradlew :app:connectedDebugAndroidTest`, Paket `./gradlew :app:assembleDebug`.

---

### Task 1: Gradle-Gerüst und Protokolltypen

**Files:** Create: Gradle-Dateien, `.gitignore`-Ergänzung (`android/.gradle/`, `android/**/build/`, `android/local.properties`, `android/keystore.properties`, `*.jks`, `android/app/google-services.json`), `core/…/Protocol.kt`; Test: `core/…/ProtocolTest.kt`

**Interfaces – Produces:** Package `de.suitithub.share.core`; `@Serializable`-Klassen mit denselben Feldern wie `server/internal/protocol/types.go` (Kotlin camelCase, `@SerialName` snake_case): `Device`, `FileItem`, `Delivery`, `Transfer` (`totalSize: Long`), `CreateTransferRequest`, `CreateTransferResponse`, `AcceptResponse`, `PairRequest/Response`, `PairCodeResponse`, `UpdateDeviceRequest`, `ProgressRequest`, `Event(type: String, data: JsonElement)`, `TransferRef`, `DeliveryUpdate`, `RelayRequest`; Enums `DeviceType`, `Kind`, `Status` (`isFinal`), `Path` (`LAN`, `RELAY`); Zeiten als `java.time.Instant`; `val ShareJson: Json`; `object AccessToken { fun create(secret: ByteArray, transferId: String, deviceId: String): String; fun verify(secret: ByteArray, transferId: String, token: String): String? }`; `const val MAX_TEXT_BYTES = 65536`.

- [ ] **Step 1: Gerüst** – `:core` (`kotlin("jvm")`, JVM-Ziel 17) anlegen; Wrapper auf 9.6.1. `./gradlew :core:test` → `BUILD SUCCESSFUL`.
- [ ] **Step 2: Failing tests**

```kotlin
@Test fun decodesTransferWithOffsetAndNanoTimes() {
    val t = ShareJson.decodeFromString<Transfer>("""{"id":"a","sender_id":"s","kind":"files","files":[{"name":"x","size":3,"mime":"","mod_time":"2026-11-24T10:00:00.123456789+01:00"}],"broadcast":false,"created_at":"2026-11-24T09:00:00Z","expires_at":"2026-11-24T09:10:00Z","closed":false,"deliveries":null,"brandneu":1}""")
    assertEquals(Instant.parse("2026-11-24T09:00:00.123456789Z"), t.files[0].modTime)
    assertEquals(emptyList<Delivery>(), t.deliveries); assertEquals(3L, t.totalSize)
}
@Test fun omitsUnsetOptionalFields() = assertEquals("""{"fcm_token":"t"}""", ShareJson.encodeToString(UpdateDeviceRequest(fcmToken = "t")))
@Test fun decodesNullLanCandidates() = assertEquals(emptyList<String>(), ShareJson.decodeFromString<AcceptResponse>("""{"lan_candidates":null,"cert_fingerprint":"","access_token":"","relay_base":""}""").lanCandidates)
@Test fun accessTokenMatchesGoReference()  // fester Vektor, einmalig mit protocol.AccessToken erzeugt: secret = 32×0x01, "t1", "d1"
@Test fun verifyRejectsForeignTransferTamperedAndGarbage() // verify(...) == null für: anderes transferId, letztes Hex-Zeichen geändert, "", ".", "d1.zz"; == "d1" für das echte
@Test fun finalStatuses() // DELIVERED, DECLINED, MISSED, CANCELLED, FAILED final; WAITING, ACCEPTED, DOWNLOADING nicht
```
- [ ] **Step 3: RED** – `./gradlew :core:test --tests '*ProtocolTest'` → Übersetzungsfehler „Unresolved reference“.
- [ ] **Step 4: Implement** – `ShareJson`: `ignoreUnknownKeys`, `coerceInputValues`, `explicitNulls=false`, `encodeDefaults=false`; Pflichtfelder einer Anfrage ohne Standardwert. `coerceInputValues` deckt auch unbekannte Enum-Werte mit Standard ab: `Kind`/`Status` ohne Standard dürfen werfen. Vergleich mit `MessageDigest.isEqual`.
- [ ] **Step 5: GREEN** – gleicher Befehl → PASS. **Step 6: Commit** `feat(android): gradle scaffold and protocol types`

### Task 2: Namen, LAN-Adressen, Kopplungs-URI

**Files:** Create: `Names.kt`, `LanAddresses.kt`, `PairUri.kt`; Test: `NamesTest.kt`, `LanAddressesTest.kt`, `PairUriTest.kt`

**Interfaces – Produces:** `fun safeFileName(name: String): String`; `fun uniqueName(name: String, exists: (String) -> Boolean): String`; `data class NetIf(val name: String, val address: InetAddress)`; `fun filterLan(ifs: List<NetIf>, port: Int): List<String>`; `fun lanCandidates(port: Int): List<String>`; `data class PairUri(val url: String, val code: String) { override fun toString(): String; companion { fun parse(s: String): PairUri? } }`.

- [ ] **Step 1: Failing tests** (Werte wie Go-Tests in `server/internal/client/names_test.go`, alle Fälle übernehmen)

```kotlin
@Test fun safeNames() { mapOf("../x" to "x", "a/b.txt" to "b.txt", "a\\b.txt" to "b.txt", "CON" to "_CON", "con.txt" to "_con.txt", "" to "file", ".." to "file", "a:b?.txt" to "a_b_.txt", "x. " to "x").forEach { (i, o) -> assertEquals(o, safeFileName(i)) } }
@Test fun longNameKeepsExtensionAndNeverSplitsSurrogates() // "😀"×150 + ".txt": UTF-8 ≤ 200 Bytes, endet auf ".txt", keine ungepaarten Surrogate
@Test fun uniqueNames() // "a.txt" belegt → "a (1).txt"; auch das belegt → "a (2).txt"; ".hidden" belegt → ".hidden (1)"
@Test fun filterKeepsOnlyPrivateWifiLikeAddresses() // wlan0 192.168.1.5 ja; ap0 10.1.1.1 ja; rmnet_data0 10.2.3.4, ccmni0, tun0, wg0, v4-rmnet, ppp0, dummy0 nein; 8.8.8.8, 127.0.0.1, 172.32.0.1, IPv6 nein → ["192.168.1.5:47800","10.1.1.1:47800"]
@Test fun pairUriRoundTripAndRejects() // parse("share://pair?code=N5EAD5&url=https%3A%2F%2Fx.example%3A443") ok; "https://…", "share://other?…", fehlender code/url → null; code wird großgeschrieben
```
- [ ] **Step 2: RED**, **Step 3: Implement** (zusätzlich zur Go-Liste ausgeschlossen: `rmnet`, `ccmni`, `ppp`, `clat`, `v4-`, `dummy`, `p2p` bleibt erlaubt), **Step 4: GREEN**, **Step 5: Commit** `feat(android): names, lan addresses, pair uri`

### Task 3: Identität und Pinning

**Files:** Create: `Identity.kt`; Test: `IdentityTest.kt`

**Interfaces – Produces:** `class Identity private constructor(val certPem: String, val keyPem: String, val fingerprint: String) { companion { fun generate(name: String): Identity; fun load(certPem: String, keyPem: String): Identity }; fun serverSocketFactory(): SSLServerSocketFactory }`; `fun OkHttpClient.Builder.pinTo(fingerprint: String): OkHttpClient.Builder`.

- [ ] **Step 1: Failing tests**

```kotlin
@Test fun fingerprintIsSha256OfDer() // 64 Hex klein, gleich MessageDigest über das DER aus certPem
@Test fun survivesSaveAndLoad() // load(generate.certPem, keyPem).fingerprint unverändert
@Test fun certIsLongLivedP256() // EC, 256 Bit, notAfter > jetzt + 19 Jahre, notBefore < jetzt
@Test fun pinnedClientAcceptsOnlyExactFingerprint() // TLS-Server mit Identity A: pinTo(A) verbindet über 127.0.0.1; pinTo(B) wirft SSLException (IOException)
@Test fun negotiatesTls12OrNewer()
```
- [ ] **Step 2: RED**, **Step 3: Implement** mit `HeldCertificate.Builder().ecdsa256()` (`commonName("share $name")`, Gültigkeit 20 Jahre ab jetzt −1 h). `pinTo`: eigener `X509TrustManager` (nur Leaf-Fingerabdruck), `hostnameVerifier { _, _ -> true }`. **Step 4: GREEN**, **Step 5: Commit** `feat(android): device identity and pinning`

### Task 4: API-Client und Go-Server-Testgerüst

**Files:** Create: `Api.kt`; Test: `GoServer.kt`, `ApiTest.kt`; Modify: `core/build.gradle.kts`

**Interfaces – Produces:**
- `data class Account(val serverUrl: String, val deviceId: String, val token: String, val name: String, val identity: Identity)`
- `class ApiException(val status: Int, message: String) : IOException`
- `class Api(val account: Account, baseClient: OkHttpClient = OkHttpClient())` mit suspend-Funktionen: `devices()`, `updateMe(name: String? = null, fcmToken: String? = null)`, `deleteMe()`, `pairCode()`, `createTransfer(r)`, `incoming()`, `outgoing()`, `transfer(id)`, `cancel(id)`, `accept(id)`, `decline(id)`, `progress(id, bytes, path)`, `delivered(id)`, `fail(id)`, `relayDownload(id, n, offset): Response` (prüft 200 bei Offset 0, 206 sonst; andere Codes → `ApiException`), `relayUpload(relayId, length: Long, open: () -> InputStream)`
- `suspend fun pair(serverUrl: String, code: String, name: String, identity: Identity, client: OkHttpClient = OkHttpClient()): Account`
- Test: `class GoServer : AutoCloseable { val url: String; fun pairCode(): String; suspend fun account(name: String): Account; fun stop(); fun restart(); companion val shareExe: File }`

- [ ] **Step 1: Testgerüst** – Gradle-Task `buildGo` (Exec: `go build -o core/build/go/ ./cmd/share-server ./cmd/share` in `../server`; `go` aus `-PgoBin`, sonst `GO_BIN`, sonst `go`), `test` hängt davon ab; Pfad als System-Property. `GoServer` schreibt eine TOML-Datei (`listen=127.0.0.1:<freier Port>`, `db_path` im Temp-Ordner), startet `share-server -config <f> serve`, wartet bis der Port antwortet, `pairCode()` liest `Code:\s+(\S+)` aus `share-server -config <f> pair`. Fehlt Go, schlägt der Build fehl (kein stilles Überspringen).
- [ ] **Step 2: Failing tests**

```kotlin
@Test fun pairsAndListsDevices() // zwei Konten; devices() enthält beide, Typ ANDROID, Fingerabdruck stimmt
@Test fun pairErrorsCarryStatus() // falscher Code → ApiException 403; gleicher Name → 409
@Test fun normalisesServerUrl() // "x.example/" → "https://x.example"; "https://x.example/" → ohne Schrägstrich; "ftp://x" → IllegalArgumentException
@Test fun textOfferLifecycle() // create(TEXT, targets B) → incoming(B) hat es → accept(B) → transfer(id).deliveries[0].status == DELIVERED
@Test fun unauthorisedIs401() // falsches Token → ApiException 401
@Test fun errorMessageComesFromBody() // message enthält den Text aus {"error": …}; HTML-Fehlerseite eines Proxys (502, text/html) → message "HTTP 502"
@Test fun patchAndDeleteMe()
```
- [ ] **Step 3: RED**, **Step 4: Implement** – normale Aufrufe: connect 10 s, call 30 s; Relay-Client ohne Call-Timeout, Lese-Timeout Download 30 s, Upload 0, `retryOnConnectionFailure(false)`, Body `isOneShot`, 256-KB-Puffer. Abbruch der Coroutine ruft `Call.cancel()`. **Step 5: GREEN**, **Step 6: Commit** `feat(android): api client tested against go server`

### Task 5: Ereignisse

**Files:** Create: `Events.kt`; Test: `EventsTest.kt`

**Interfaces – Produces:** `sealed interface ServerEvent { Connected; Disconnected; Unauthorised; OfferNew(Transfer); OfferRemoved(id); DeliveryUpdated(DeliveryUpdate); TransferClosed(id); Relay(RelayRequest) }`; `fun Api.events(backoffBase: Duration = 1.seconds): Flow<ServerEvent>` (kalt, verbindet neu mit 1 s … 30 s, endet bei Abbruch; `Unauthorised` bei HTTP 401, danach Ende).

- [ ] **Step 1: Failing tests**

```kotlin
@Test fun receivesOfferNew()           // Connected, dann OfferNew mit Text
@Test fun reconnectsAfterServerRestart() // GoServer.stop(); restart() → Disconnected, dann erneut Connected
@Test fun revokedTokenEndsFlow()       // deleteMe() über zweite Api-Instanz, dann GoServer.restart() → Unauthorised als letztes Element
@Test fun unknownOrBrokenMessagesAreIgnored() // MockWebServer: {"type":"neu","data":1}, "kaputt", dann offer.removed → nur OfferRemoved
```
- [ ] **Step 2: RED**, **Step 3: Implement** (OkHttp-WebSocket in `callbackFlow`, `pingInterval` 20 s, unbegrenzter Kanal), **Step 4: GREEN**, **Step 5: Commit** `feat(android): event stream`

### Task 6: Lokaler HTTPS-Endpunkt

**Files:** Create: `Files.kt`, `LocalEndpoint.kt`; Test: `TestFiles.kt`, `LocalEndpointTest.kt`

**Interfaces – Produces:**
- `interface FileSource { val item: FileItem; fun unchanged(): Boolean; fun openAt(offset: Long): InputStream; fun release() {} }`
- `class Offer(val transfer: Transfer, val secret: ByteArray, val sources: List<FileSource>)`
- `class LocalEndpoint(identity: Identity, preferredPort: Int = 47800, lookup: (String) -> Offer?) : AutoCloseable { fun start(): Int; val running: Boolean; fun closeConnections(transferId: String) }`
- Test: `class FileOnDisk(file: File) : FileSource`

- [ ] **Step 1: Failing tests** (OkHttp mit `pinTo`, rohe TLS-Sockets für Unfug)

```kotlin
@Test fun pingAndFullDownload()          // 204; 200 mit Content-Length und identischen Bytes (3 MB Zufall)
@Test fun rangeResume()                  // bytes=1000- → 206, Content-Range "bytes 1000-N/M"; bytes=M- und bytes=1-2 → 416
@Test fun authRules()                    // ohne/fremdes Token → 401; unbekanntes Angebot → 404; Dateiindex 9, -1, "x" → 404
@Test fun changedFileIs409()
@Test fun emptyFile()                    // Größe 0 → 200, Content-Length 0
@Test fun otherMethodsAndPaths()         // POST → 405; "/", "/t/../x" → 404
@Test fun abuseDoesNotKillServer()       // Müll statt TLS, 100-KB-Headerzeile (→ 431/Trennen), stumme Verbindung: danach klappt ping weiterhin, stumme wird nach headerTimeout (Test: 300 ms) getrennt
@Test fun fallsBackToFreePort()          // 47800 vom Test belegt → anderer Port > 0
@Test fun fourParallelDownloads()        // alle byte-identisch
@Test fun closeEndsRunningDownloads()    // close() während langsamen Lesens → Client bekommt IOException < 2 s
```
- [ ] **Step 2: RED**, **Step 3: Implement** – ein Thread je Verbindung (höchstens 32, weitere sofort schließen), eine Anfrage je Verbindung (`Connection: close`), Headerlimit 16 KB, Headerzeit 10 s, TLS-Handshake im Verbindungs-Thread. `lookup` mit Wartezeit (40 × 50 ms), weil Empfänger benachrichtigt werden, bevor `createTransfer` zurückkehrt. **Step 4: GREEN**, **Step 5: Commit** `feat(android): local https endpoint`

### Task 7: Sender

**Files:** Create: `Sender.kt`; Test: `SenderTest.kt`

**Interfaces – Produces:** `class Sender(api: Api, endpointPort: Int = 47800, candidates: (Int) -> List<String> = ::lanCandidates) { val offers: StateFlow<Map<String, Transfer>>; suspend fun offerFiles(sources: List<FileSource>, targets: List<String>, broadcast: Boolean, message: String = "", minutes: Int = 10): Transfer; suspend fun offerText(kind: Kind, text: String, targets: List<String>, broadcast: Boolean): Transfer; suspend fun cancel(id: String); suspend fun onEvent(e: ServerEvent, scope: CoroutineScope); fun close() }` – `offers` enthält eigene offene Angebote mit aktuellen `deliveries`; jedes Ende ruft `FileSource.release()` genau einmal.

- [ ] **Step 1: Failing tests**

```kotlin
@Test fun endpointRunsOnlyWhileFileOffersOpen()   // Text: kein Endpunkt; Datei: Port offen; nach cancel: zu
@Test fun relayRequestUploadsFromOffset()         // Empfänger-Api.relayDownload(id,0,1000) → Rest byte-identisch
@Test fun changedFileIsNotUploaded()              // Relay endet mit 502 (RelayWait), kein Byte
@Test fun deliveryUpdatesReachOffers()            // nach decline des Empfängers steht DECLINED in offers bzw. Angebot geschlossen
@Test fun closedWhileDisconnectedIsDetectedOnReconnect() // Ereignisse anhalten, Empfänger lehnt ab, Connected einspeisen → offers leer, Endpunkt zu
@Test fun orphanedFileOffersAreCancelledOnFirstConnect() // Angebot per Api ohne Sender angelegt; neuer Sender + erstes Connected → Empfänger sieht CANCELLED
@Test fun orphanCleanupNeverHitsOwnNewOffer()     // offerFiles läuft gleichzeitig mit erstem Connected → Angebot bleibt offen
@Test fun tooLongTextFailsBeforeNetwork()         // 65537 Bytes → IllegalArgumentException
@Test fun releaseCalledOnceOnEveryEnd()           // cancel, TransferClosed, fehlgeschlagenes create
```
- [ ] **Step 2: RED**, **Step 3: Implement** (Mutex um Anlegen und Waisen-Aufräumen; `pending`-Zähler wie Go). **Step 4: GREEN**, **Step 5: Commit** `feat(android): sender`

### Task 8: Empfänger

**Files:** Create: `Receiver.kt`; Modify: `Files.kt`; Test: `ReceiverTest.kt`, `TestFiles.kt`

**Interfaces – Produces:**
- `interface Part { val finishedRef: String?; fun size(): Long; fun append(): OutputStream; fun reset(); fun finish(): String; fun discard() }`
- `interface DownloadStore { fun part(transferId: String, index: Int, item: FileItem): Part; fun forget(transferId: String); fun retain(transferIds: Set<String>) }` – `retain` verwirft Teilstände aller anderen Angebote
- `class Receiver(api: Api, store: DownloadStore, probeTimeout: Duration = 1500.milliseconds, idleTimeout: Duration = 30.seconds, backoffBase: Duration = 1.seconds) { suspend fun download(t: Transfer, onProgress: (bytes: Long, path: Path) -> Unit): List<String> }` – ruft `accept` (idempotent), lädt, meldet `delivered`; wirft `OfferEndedException` oder `DownloadFailedException(userMessage)`.
- Test: `class DirStore(dir: File) : DownloadStore` (Zustand übersteht neue Instanz)

- [ ] **Step 1: Failing tests** (Sender aus Task 7 am echten Go-Server)

```kotlin
@Test fun lanDownloadIsIdenticalAndReportsLan()
@Test fun fallsBackToRelayWhenProbeFails()         // Kandidat 192.0.2.1:1 → Relay, < probeTimeout + 3 s
@Test fun wrongFingerprintNeverSendsTokenToLan()   // fremder TLS-Server als Kandidat zählt 0 HTTP-Anfragen; Download über Relay ok
@Test fun resumesAfterNewInstance()                // Abbruch bei ~50 %, neue Receiver+DirStore-Instanz → Angefordertes Range beginnt beim Teilstand; identisch
@Test fun finishedFilesAreNotReloaded()            // 2 Dateien, Abbruch in Datei 2 → zweite Runde fordert nur Index 1 an; Ergebnis enthält beide Refs
@Test fun endpointLostMidFileContinuesViaRelay()
@Test fun changedSourceFailsAndReports()           // 409 → DownloadFailedException, Zustellung FAILED
@Test fun writeErrorFailsAndLeavesNothing()        // append() wirft IOException("ENOSPC") → FAILED, discard() gerufen
@Test fun cancelledOfferStopsRetrying()            // OfferEndedException, Teil verworfen
@Test fun senderGoneRetriesWhileAlive()            // Relay 502 zweimal (Sender kurz weg), dann ok
@Test fun emptyAndHostileNames()                   // Größe 0 ok; "../evil" landet als "evil" im Zielordner
@Test fun lostDeliveredResponseIsRetried()         // MockWebServer: /delivered erst 500, dann 204 → Erfolg
@Test fun coroutineCancelKeepsPartForResume()
@Test fun deliveredFollowsProgressAtMostOncePerSecond()
```
- [ ] **Step 2: RED**, **Step 3: Implement** (Ablauf und Fehlerklassen wie `receiver.go`; Fortschritt asynchron, darf das Laden nicht bremsen; `have > size` → `reset()`). **Step 4: GREEN**, **Step 5: Commit** `feat(android): receiver with lan-first and resume`

### Task 9: Engine

**Files:** Create: `Engine.kt`; Test: `EngineTest.kt`

**Interfaces – Produces:**

```kotlin
data class Download(val transfer: Transfer, val phase: Phase, val bytes: Long, val path: Path?, val refs: List<String>, val error: String?)  // Phase: RUNNING, DONE, FAILED
data class EngineState(val connected: Boolean, val unauthorised: Boolean, val devices: List<Device>, val incoming: List<Transfer>, val outgoing: List<Transfer>, val downloads: List<Download>)
sealed interface Notice { OfferArrived(t); OfferGone(id); DownloadDone(d); DownloadFailed(d); TextReceived(t); Unavailable(id) }
class Engine(val api: Api, store: DownloadStore, scope: CoroutineScope, /* Sender/Receiver-Parameter */) {
  val state: StateFlow<EngineState>; val notices: SharedFlow<Notice>
  val hasWork: StateFlow<Boolean>   // offene eigene Datei-Angebote oder RUNNING-Downloads
  fun hold(): AutoCloseable         // Ereignisverbindung offen, solange mindestens ein Halter oder hasWork
  suspend fun refresh(); suspend fun offerFiles(…): Transfer; suspend fun offerText(…): Transfer
  suspend fun accept(id: String); suspend fun decline(id: String); suspend fun cancelOutgoing(id: String)
  fun cancelDownload(id: String); fun dismiss(id: String); fun deviceName(id: String): String
  suspend fun offerDetails(id: String): Transfer?   // null, wenn nicht mehr wartend
  fun close()
}
```
`devices` ohne eigenes Gerät und ohne Agenten.

- [ ] **Step 1: Failing tests**

```kotlin
@Test fun incomingAppearsAndDisappears()        // OfferArrived + incoming; Rückzug → OfferGone, leer
@Test fun acceptTextYieldsTextReceived()
@Test fun acceptFilesRunsToDone()               // incoming → downloads RUNNING → DONE mit refs; hasWork true → false
@Test fun doubleAcceptStartsOneDownload()       // zwei parallele accept → genau ein Download
@Test fun acceptAfterExpiryIsUnavailable()      // Notice.Unavailable, kein Wurf, aus incoming entfernt
@Test fun declineAfterWithdrawIsUnavailable()
@Test fun resumesConfirmedDownloadsOnConnect()  // Zustellung ist ACCEPTED ohne lokalen Download → läuft automatisch an
@Test fun waitingOffersAreNeverAutoAccepted()   // refresh/Connected lädt WAITING nicht herunter
@Test fun cancelDownloadReportsFailedAndDiscards()
@Test fun connectionFollowsHoldersAndWork()     // ohne Halter nicht verbunden; hold() → verbunden; close → getrennt; offene Datei-Offerte hält ohne Halter
@Test fun unauthorisedIsVisible()
@Test fun staleParts()                          // nach erfolgreichem refresh: store.retain(noch offene bestätigte IDs); bei Netzfehler wird nichts verworfen
@Test fun expiredOffersLeaveIncomingWithoutServer() // expires_at erreicht, offline → verschwindet
```
- [ ] **Step 2: RED**, **Step 3: Implement**, **Step 4: GREEN**, **Step 5: Commit** `feat(android): engine`

### Task 10: Zusammenspiel mit Go-CLI und zwischen zwei Engines

**Files:** Test: `InteropTest.kt`

- [ ] **Step 1: Tests**

```kotlin
@Test fun goCliSendsFilesOverLan()   // share.exe -config … send a.bin leer.txt "ümläut ß.txt" --to pix → Engine nimmt an, byte-identisch, Pfad LAN, Exitcode 0
@Test fun goCliSendsTextAndLink()
@Test fun broadcastToTwoEngines()    // beide erhalten identisch, ein dritter lehnt ab
@Test fun switchesPathMidTransfer()  // 64 MB, lokalen Endpunkt während des Ladens schließen → fertig über RELAY, identisch
@Test fun senderDropsConnectionBriefly() // Ereignisverbindung des Senders 5 s weg während Relay-Download → kommt an
@Test fun serverRestartMidLanDownload()  // GoServer.restart() während LAN-Download → kommt an, Zustellung DELIVERED
@Test fun cancelWhileDownloading()
```
- [ ] **Step 2:** `./gradlew :core:test` → alles PASS; gefundene Fehler zuerst als Test im zuständigen Task-Testfile nachstellen, dann beheben. Muss dafür der Go-Server geändert werden, gehört ein Go-Test dazu und `go test ./...` muss grün sein. **Step 3: Commit** `test(android): interop with go cli`

### Task 11: App-Modul, Emulator, sicherer Speicher

**Files:** Create: `app/build.gradle.kts`, `AndroidManifest.xml`, `ShareApp.kt`, `data/SecureStore.kt`, `ui/MainActivity.kt` (leer), `ui/Theme.kt`, `res/*` (Symbol: adaptiv, Vektor), `app/src/debug/res/xml/network_security_config.xml`; Test: `androidTest/…/SecureStoreTest.kt`

**Interfaces – Produces:** `class SecureStore(context: Context) { fun load(): Account?; fun save(a: Account); fun clear() }`; `class ShareApp : Application { val engine: StateFlow<Engine?>; fun setAccount(a: Account?) }` (baut/schließt die Engine); `val Context.shareApp`.

- [ ] **Step 1: Emulator** (nach Zustimmung des Nutzers) – Android-cmdline-tools in das vorhandene SDK laden, Systemabbild `system-images;android-36;google_apis;x86_64`, AVD `share36` mit `ANDROID_AVD_HOME=H:\Android\avd`. `adb shell getprop sys.boot_completed` → `1`.
- [ ] **Step 2: Failing tests** – `roundTrip`, `fileContainsNoPlaintextToken` (Dateibytes enthalten weder Token noch „PRIVATE KEY“), `corruptFileLoadsAsNull` (kein Absturz → Neukopplung), `clearRemoves`.
- [ ] **Step 3: RED** (`connectedDebugAndroidTest`), **Step 4: Implement** – Keystore-AES-Schlüssel `share-config`, GCM, Datei `filesDir/account.bin`. Manifest: `allowBackup=false`, `dataExtractionRules` schließt alles aus. google-services-Plugin nur anwenden, wenn `app/google-services.json` existiert. **Step 5: GREEN**, **Step 6: Commit** `feat(android): app module and secure store`

### Task 12: `content://`-Quellen und MediaStore-Downloads

**Files:** Create: `data/ContentSource.kt`, `data/MediaStoreDownloads.kt`; Test: `androidTest/…/ContentSourceTest.kt`, `…/MediaStoreDownloadsTest.kt`, `androidTest/…/TestProvider.kt` (eigener Provider: normale Datei, ohne Größe, Pipe, wirft SecurityException)

**Interfaces – Produces:** `class ContentSource private constructor : FileSource { companion { fun open(context: Context, uri: Uri): ContentSource } }` (wirft `SourceException` mit deutschem Text); `class MediaStoreDownloads(context: Context) : DownloadStore` (`finish()` liefert die `content://`-URI).

- [ ] **Step 1: Failing tests**

```kotlin
@Test fun readsNameSizeMimeAndSeeks()
@Test fun missingSizeFallsBackToTempCopy()      // Größe = Kopiegröße; nach release() ist die Kopie weg
@Test fun unseekablePipeFallsBackToTempCopy()   // openAt(1000) liefert die richtigen Bytes
@Test fun missingModTimeComparesSizeOnly()
@Test fun noPermissionIsReadableError()
@Test fun partSurvivesNewInstance()             // 1000 Bytes anhängen, neue Instanz: size()==1000, weiter anhängen, finish → Inhalt stimmt
@Test fun pendingIsInvisibleUntilFinish()       // Abfrage ohne Pending findet nichts, danach Datei in Download/
@Test fun nameConflictGetsSuffix()              // zweimal "a.txt" → "a.txt" und "a (1).txt", beide Inhalte erhalten
@Test fun apkKeepsNameAndMime()                 // "app.apk" bleibt "app.apk" (kein ".apk.bin"), MIME application/vnd.android.package-archive
@Test fun finishedRefSurvivesNewInstance(); @Test fun discardRemovesRow(); @Test fun forgetClearsState()
@Test fun staleStateWithDeletedRowRestarts()    // Zeile extern gelöscht → size()==0, append legt neu an
```
- [ ] **Step 2: RED**, **Step 3: Implement** – Zeile mit `IS_PENDING=1` in `MediaStore.Downloads`, Zuordnung `<tid>-<n>` → URI + fertig-Merker in eigener SharedPreferences-Datei; Anhängen über `openFileDescriptor(uri,"rw")` und Position ans Ende. `retain` löscht verwaiste Pending-Zeilen. Temp-Kopien in `cacheDir/send/`, beim App-Start ohne offene Angebote geleert. **Step 4: GREEN**, **Step 5: Commit** `feat(android): content sources and mediastore downloads`

### Task 13: Benachrichtigungen und Vordergrunddienst

**Files:** Create: `service/Notifications.kt`, `service/TransferService.kt`, `service/ActionReceiver.kt`, `ui/Format.kt`; Test: `test/…/FormatTest.kt`, `androidTest/…/NotificationsTest.kt`, `…/TransferServiceTest.kt`

**Interfaces – Produces:** `object Format { fun size(bytes: Long): String; fun remaining(expires: Instant, now: Instant): String; fun offerTitle(t: Transfer, sender: String): String; fun offerBody(t: Transfer): String }`; `class Notifications(context) { fun offer(t: Transfer, sender: String); fun removeOffer(id: String); fun result(d: Download); fun text(t: Transfer, sender: String); fun unavailable(); fun progress(s: EngineState): Notification }`; `TransferService` mit Aktionen `ACCEPT(id)`, `OFFER(uris als ClipData, targets, broadcast, message)`, `CANCEL(id)`; `ActionReceiver` mit `DECLINE(id)`, `COPY(id)`.

- [ ] **Step 1: Failing tests**

```kotlin
@Test fun sizes()      // 0 → "0 B", 1536 → "1,5 KB", 5 GiB → "5,0 GB"
@Test fun remaining()  // 8:10 → "noch 9 Min", 40 s → "noch 40 s", vorbei → "abgelaufen"
@Test fun bodies()     // 3 Dateien → "3 Dateien, 12,0 MB: a.txt, b.txt, c.txt"; Text → erste 100 Zeichen + "…" (ohne zerteilte Surrogate); Nachricht wird angehängt
@Test fun offerHasAcceptDeclineAndTimeout()     // Aktionen „Annehmen“/„Ablehnen“, timeoutAfter ≈ Restzeit; gleiche ID → eine Benachrichtigung
@Test fun noPermissionDoesNotCrash()            // POST_NOTIFICATIONS entzogen: offer() ok
@Test fun offerServiceKeepsUriReadable()        // Grant über Intent-ClipData, aufrufende Activity beendet → Datei wird vollständig gesendet
@Test fun acceptActionDownloadsWithUiClosed()
@Test fun staleActionsAreHarmless()             // ACCEPT/DECLINE/CANCEL mit unbekannter ID, ohne Konto, doppelt → kein Absturz, Dienst endet, „nicht mehr verfügbar“
@Test fun serviceStopsWhenIdle()
```
- [ ] **Step 2: RED**, **Step 3: Implement** – `foregroundServiceType="dataSync"`, `startForeground` sofort in `onStartCommand`, `START_NOT_STICKY`, Teil-WakeLock und WifiLock solange `hasWork`, `onTimeout` (API 35) → Dienst sauber beenden. `ForegroundServiceStartNotAllowedException` → Hinweis „App öffnen, um fortzufahren“. Kanäle: „Angebote“ (hoch), „Übertragungen“ (niedrig), „Ergebnisse“ (normal). Sendebenachrichtigung: „Biete an: app.apk – noch 8 Min“ mit „Abbrechen“. Angebots-Benachrichtigungen nur, wenn kein Hauptbildschirm sichtbar ist. Alle PendingIntents unveränderlich, Komponenten nicht exportiert. **Step 4: GREEN**, **Step 5: Commit** `feat(android): notifications and transfer service`

### Task 14: Firebase-Push

**Files:** Create: `service/PushService.kt`; Test: `androidTest/…/PushServiceTest.kt`

**Interfaces – Produces:** `class PushHandler(app: ShareApp) { suspend fun onMessage(data: Map<String, String>); suspend fun onToken(token: String) }`; `fun pushAvailable(context): Boolean` (`FirebaseApp.getApps(context).isNotEmpty()`); Token-Abgleich bei jedem Engine-Start.

- [ ] **Step 1: Failing tests** – `offerNewShowsNotification`, `offerRemovedClearsIt`, `alreadyGoneOfferShowsNothing` (404 bzw. nicht mehr `WAITING`), `unknownTypeIgnored`, `withoutAccountIgnored`, `withoutFirebaseAppStarts` (kein Absturz, `pushAvailable == false`), `duplicateFromSocketAndPushShowsOnce`.
- [ ] **Step 2: RED**, **Step 3: Implement** (Verarbeitung ≤ 15 s; Server nicht erreichbar → allgemeine Benachrichtigung „Neues Angebot – App öffnen“), **Step 4: GREEN**, **Step 5: Commit** `feat(android): firebase push`

### Task 15: Kopplung und Einstellungen

**Files:** Create: `ui/PairScreen.kt`, `ui/SettingsScreen.kt`; Modify: `MainActivity.kt`, Manifest; Test: `androidTest/…/PairScreenTest.kt`

**Interfaces – Consumes:** `pair`, `PairUri`, `ShareApp.setAccount`. **Produces:** `@Composable PairScreen(prefill: PairUri?, onPaired: (Account) -> Unit)`, `@Composable SettingsScreen(engine: Engine, onBack: () -> Unit)`.

- [ ] **Step 1: Failing tests** (Compose-UI) – `pairsAgainstServer` (Go-Server auf dem Host, `10.0.2.2`, URL/Code als Instrumentierungsargumente), `wrongCodeShowsGermanError` („Code ungültig oder abgelaufen“), `nameTakenShowsError`, `deepLinkOnlyPrefills` (`share://pair?…` füllt Felder, koppelt **nicht** von selbst und überschreibt kein bestehendes Konto), `qrShowsPairUri`, `unpairReturnsToPairScreen`.
- [ ] **Step 2: RED**, **Step 3: Implement** – Felder Server-URL, Code, Gerätename (Vorgabe `Build.MODEL`), „QR scannen“ (zxing-embedded). Einstellungen: Server-URL (nur Anzeige), Gerätename ändern, „Gerät hinzufügen“ (Code + QR + Ablaufzeit), Push-Status, Benachrichtigungsberechtigung, „Abmelden“ mit Rückfrage. Widerrufenes Token (`unauthorised`) → Kopplungsbildschirm mit Hinweis. Abmelden ohne Server möglich (lokal löschen, Hinweis). **Step 4: GREEN**, **Step 5: Commit** `feat(android): pairing and settings`

### Task 16: Hauptbildschirm

**Files:** Create: `ui/MainScreen.kt`; Test: `androidTest/…/MainScreenTest.kt`

**Interfaces – Produces:** `@Composable MainScreen(state: EngineState, actions: MainActions)`; `interface MainActions { accept(id); decline(id); cancelOutgoing(id); cancelDownload(id); dismiss(id); sendFiles(uris, targets, broadcast); sendText(text, targets, broadcast); openResult(ref); openSettings() }`.

```
┌ Share                         ⚙ ┐
│ ● verbunden                      │
│ OFFENE ANFRAGEN                  │
│  Desktop · 2 Dateien, 1,2 GB     │
│  noch 8 Min  [Ablehnen][Annehmen]│
│ LAUFEND                          │
│  ↑ app.apk → Pixel  lädt (lokal) │
│  ↓ video.mp4  43 % · über Server │
│ SENDEN AN                        │
│  (•) Desktop ●  ( ) Tablet ○     │
│  [ An alle ]                     │
│  [Dateien…]  [Text/Link senden]  │
└──────────────────────────────────┘
```

- [ ] **Step 1: Failing tests** – `showsIncomingWithDetails` (Absender, Namen, Größen, Restzeit; Antippen zeigt alle Dateien und Nachricht), `acceptAndDeclineCallActions`, `showsProgressPathAndPerReceiverStatus` (deutsche Status gemäß Spec, „lokal“/„über Server“), `sendDisabledWithoutTarget`, `allClearsSingleTargets`, `finishedApkOffersInstall` („Installieren“), `finishedFileOffersOpen`, `failedShowsMessageAndDismiss`, `httpTextIsSentAsLink` (`https://x` → LINK, sonst TEXT; Leerraum am Rand entfernt), `emptyState`, `survivesRotation`.
- [ ] **Step 2: RED**, **Step 3: Implement** – Dateiwahl `OpenMultipleDocuments` mit `takePersistableUriPermission`, Freigabe bei Angebotsende; Senden startet `TransferService.OFFER`. `hold()` zwischen `onStart` und `onStop`. Restzeit tickt sekündlich. Benachrichtigungsberechtigung beim ersten Start erfragen. **Step 4: GREEN**, **Step 5: Commit** `feat(android): main screen`

### Task 17: Teilen-Ziel

**Files:** Create: `ui/ShareActivity.kt`; Modify: Manifest; Test: `test/…/ShareIntentTest.kt`, `androidTest/…/ShareActivityTest.kt`

**Interfaces – Produces:** `sealed interface SharePayload { Files(uris); Text(kind, text); Empty }`; `fun parseShare(intent: Intent): SharePayload`.

- [ ] **Step 1: Failing tests** – `singleFile`, `multipleFiles`, `plainTextIsText`, `urlIsLink`, `textWithFileIsFiles` (EXTRA_STREAM gewinnt, Text wird Nachricht), `emptyIntent` → Hinweis und Ende, `nullAndDuplicateUris` gefiltert, `fileSchemeRejected` (`file://` sowie URIs der eigenen App-Provider werden abgelehnt), `tooLongText` → verständliche Meldung, `notPairedShowsHint`, `shareFromOtherAppSends` (Ende-zu-Ende mit TestProvider, Ziel gewählt, Activity schließt, Angebot läuft).
- [ ] **Step 2: RED**, **Step 3: Implement** – durchscheinende Activity mit Blatt unten: Geräte (Mehrfachauswahl) + „An alle“ + „Senden“; URIs per ClipData + `FLAG_GRANT_READ_URI_PERMISSION` an den Dienst. **Step 4: GREEN**, **Step 5: Commit** `feat(android): share target`

### Task 18: Ergebnisse, Freigabe-Build, Doku

**Files:** Create: `docs/android.md`; Modify: `Notifications.kt`, `TransferService.kt`, `app/build.gradle.kts`, `README.md`; Test: `androidTest/…/ResultsTest.kt`

- [ ] **Step 1: Failing tests** – `acceptedTextIsInClipboard` (Hinweis „kopiert“), `acceptedLinkOffersOpen` (nur `http`/`https` bekommen „Öffnen“; `intent:`/`javascript:`/`file:` nur Kopie), `apkResultHasInstallIntent` (ACTION_VIEW, APK-MIME, Lesefreigabe), `unknownTypeOpenShowsHint` (keine passende App → Hinweis statt Absturz).
- [ ] **Step 2: RED**, **Step 3: Implement**, **Step 4: GREEN**
- [ ] **Step 5: Release** – `signingConfig` aus `keystore.properties` (fehlt sie → unsigniert mit Hinweis), R8 an. `./gradlew :app:assembleRelease` → `BUILD SUCCESSFUL`; die mit Testschlüssel signierte APK auf dem Emulator: koppeln, Datei empfangen, Datei senden (prüft R8-Regeln für Serialisierung).
- [ ] **Step 6: `docs/android.md`** – Bauen, Schlüssel erzeugen, Firebase-Projekt (App `de.suitithub.share`, `google-services.json`, Service-Account für den Server), Installation, Akku-Optimierung; **Prüfliste am echten Handy**: Push bei geschlossener App, Annehmen vom Sperrbildschirm, 2-GB-Datei im WLAN (lokal) und über Mobilfunk (Relay), WLAN mitten im Download aus, Bildschirm aus während Senden, Teilen aus Galerie/Fotos-Cloud/Chrome, APK installieren, `share send` vom PC.
- [ ] **Step 7: Gesamtlauf** – `./gradlew :core:test :app:testDebugUnitTest :app:connectedDebugAndroidTest :app:lintDebug` und `go test ./...` → grün. **Step 8: Commit** `feat(android): results, release build, docs`

---

## Nicht prüfbar ohne den Nutzer

Echter FCM-Versand (Firebase-Projekt), LAN-Pfad beim Senden vom Handy (Emulator sitzt hinter NAT), herstellerspezifische Akku-Regeln, Caddy/playit. Dafür gibt es die Prüfliste in `docs/android.md`.
