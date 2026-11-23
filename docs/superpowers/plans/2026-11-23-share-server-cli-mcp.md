# Share – Plan 1: Server, Go-Client, CLI und MCP – Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Vermittlungsserver (`share-server`), wiederverwendbare Go-Client-Bibliothek (Senden mit lokalem Endpunkt, Empfangen mit Wegwahl lokal/Relay, Fortsetzen) sowie `share`-CLI und stdio-MCP für Agenten.

**Architecture:** Der Server hält nur Geräte, Kopplungscodes und laufende Angebote in SQLite und verteilt Ereignisse per WebSocket bzw. FCM. Dateidaten fließen entweder direkt vom lokalen HTTPS-Endpunkt des Senders zum Empfänger (Zertifikat gepinnt, angebotsbezogenes Zugangstoken) oder werden vom Server live durchgereicht (Relay, nichts auf Platte). Die Go-Client-Bibliothek ist die Referenzimplementierung des Protokolls, die Android- und Windows-Plan später nachbauen.

**Tech Stack:** Go ≥ 1.23, `modernc.org/sqlite` (ohne cgo), `github.com/coder/websocket`, `github.com/modelcontextprotocol/go-sdk`, `firebase.google.com/go/v4`, `github.com/BurntSushi/toml`, `github.com/mdp/qrterminal/v3`, nur Standardbibliothek für HTTP/TLS.

**Spec:** `docs/superpowers/specs/2026-11-23-share-design.md`

**Folgepläne (nicht Teil dieses Plans):** Plan 2 Android-App, Plan 3 Windows-Programm. Beide implementieren das hier festgelegte Protokoll (`server/internal/protocol`, `docs/protocol.md`).

## Global Constraints

- Server speichert **keine Dateidaten**, auch nicht temporär; Relay nur über Arbeitsspeicher-Puffer.
- Maximale/Standard-Angebotsdauer: **10 Minuten** (`max_offer_minutes = 10`, Clients dürfen kürzer anfragen, nie länger).
- Text/Link im Angebot: max. **64 KB** (65536 Bytes UTF-8), sonst 413.
- Kopplungscode: einmalig, **10 Minuten** gültig.
- LAN-Probe: alle Kandidaten parallel, Zeitgrenze **1,5 s**.
- Lokaler Endpunkt Standard-Port **47800**, bei Belegung freier Port; gemeldet wird der tatsächliche.
- Agent-Geräte (`type = "agent"`) sind nie Ziel, auch nicht bei „an alle“.
- Annehmen gilt für das ganze Angebot.
- Ohne gültiges Geräte-Token ist außer `POST /v1/pair` nichts erreichbar (401).
- Nur angenommene Empfänger des jeweiligen Angebots erhalten Daten (lokal und Relay).
- Kein cgo (Windows-Build muss mit `CGO_ENABLED=0` gehen).
- Go-Modul liegt in `server/`, Modulpfad `share`.

## Review Focus

1. **Abbruch mitten in der Datei und Fortsetzen über den anderen Weg** (LAN fällt weg → Relay): Download setzt am Offset fort, Ergebnis byte-identisch. → Test in Task 13.
2. **Sender verschwindet während des Angebots** (Prozess beendet, WebSocket weg): Empfänger sehen `cancelled`, ein wartender Relay-Download endet nach 15 s mit 502 statt zu hängen. → Tests in Task 6 und Task 7.
3. **Gefährliche/kollidierende Dateinamen vom Sender** (`../x`, `a/b.txt`, `CON`, leer, vorhandene Datei): nur Basisname, bereinigt, Konfliktsuffix ` (1)`. → Test in Task 13.
4. **Zwei Geräte nehmen ein „an alle“-Angebot gleichzeitig an** und laden parallel vom selben Sender: beide vollständig. → Test in Task 14.
5. **Annehmen nach Phasenende / doppelt / Ablehnen nach Annehmen**: 409 bzw. idempotent (zweites Annehmen liefert dieselbe Antwort). → Test in Task 6.

---

## Dateistruktur

```
server/
  go.mod
  cmd/share-server/main.go        Dienst + Admin-Befehle
  cmd/share/main.go               CLI + `share mcp`
  internal/protocol/types.go      JSON-Typen, Status, Ereignisse (Vertrag für alle Clients)
  internal/protocol/token.go      HMAC-Zugangstoken (Server erzeugt, Sender prüft)
  internal/store/store.go         SQLite öffnen, Migration
  internal/store/devices.go       Geräte, Tokens, Kopplungscodes
  internal/store/transfers.go     Angebote, Zustellungen
  internal/server/server.go       Router, Auth-Middleware, Uhr
  internal/server/pair.go         Kopplung, Geräte-Endpunkte
  internal/server/hub.go          WebSocket-Ereignisse, Online-Status
  internal/server/transfers.go    Angebots-Endpunkte, Ablauf-Schleife
  internal/server/relay.go        Relay-Broker
  internal/server/notify.go       Notifier-Interface + FCM-Implementierung
  internal/tlsid/tlsid.go         Geräte-Zertifikat, Fingerabdruck, gepinnte TLS-Konfig
  internal/lanaddr/lanaddr.go     lokale Adressen ermitteln
  internal/client/api.go          HTTP-API-Client
  internal/client/config.go       Client-Konfiguration laden/speichern
  internal/client/events.go       WebSocket-Ereignisverbindung mit Wiederverbindung
  internal/client/sender.go       Offer-Manager, lokaler Endpunkt, Relay-Antwort
  internal/client/receiver.go     Annehmen, Wegwahl, Download, Fortsetzen, Ablage
  internal/client/names.go        Gerätenamen auflösen, Dateinamen bereinigen
  internal/e2e/e2e_test.go        Ende-zu-Ende-Tests
  internal/mcpserver/mcpserver.go MCP-Werkzeuge
docs/protocol.md                  Protokollbeschreibung für Plan 2/3
docs/deploy/Caddyfile, docs/deploy/share-server.service, docs/deploy/README.md
```

---

### Task 1: Projektgerüst und Protokolltypen

**Files:**
- Create: `.gitignore`, `server/go.mod`, `server/internal/protocol/types.go`, `server/internal/protocol/token.go`, `server/internal/protocol/types_test.go`

**Interfaces:**
- Produces (`package protocol`):
  - `type DeviceType string` mit `DeviceAndroid="android"`, `DeviceWindows="windows"`, `DeviceAgent="agent"`
  - `type Device struct { ID, Name string; Type DeviceType; Online bool; LastSeen time.Time; CertFingerprint string }` (JSON snake_case)
  - `type Kind string` mit `KindText="text"`, `KindLink="link"`, `KindFiles="files"`
  - `type FileItem struct { Name string; Size int64; MIME string; ModTime time.Time }`
  - `type Status string`: `waiting, accepted, downloading, delivered, declined, missed, cancelled, failed`; `func (s Status) Final() bool` (true für delivered, declined, missed, cancelled, failed)
  - `type Path string`: `PathLAN="lan"`, `PathRelay="relay"`
  - `type Delivery struct { DeviceID string; Status Status; Path Path; Bytes int64; UpdatedAt time.Time }`
  - `type Transfer struct { ID, SenderID string; Kind Kind; Text string; Message string; Files []FileItem; Broadcast bool; Targets []string; CreatedAt, ExpiresAt time.Time; Closed bool; Deliveries []Delivery }` – `Deliveries` nur für den Sender befüllt; `Text` für Empfänger befüllt
  - `type CreateTransferRequest struct { Kind Kind; Text, Message string; Files []FileItem; Targets []string; Broadcast bool; Minutes int; LANCandidates []string /* "ip:port" */ }`
  - `type CreateTransferResponse struct { Transfer Transfer; Secret string /* hex, 32 Byte */ }`
  - `type AcceptResponse struct { LANCandidates []string; CertFingerprint string; AccessToken string; RelayBase string /* "/v1/transfers/{id}/files/" */ }`
  - `type PairRequest struct { Code, Name string; Type DeviceType; CertFingerprint string }`, `type PairResponse struct { DeviceID, Token string }`
  - `type ProgressRequest struct { Bytes int64; Path Path }`
  - `type Event struct { Type string; Data json.RawMessage }` mit Typ-Konstanten `EvOfferNew="offer.new"` (Data: Transfer), `EvOfferRemoved="offer.removed"` (Data: `{transfer_id}`), `EvDeliveryUpdated="delivery.updated"` (Data: `{transfer_id, delivery}`), `EvTransferClosed="transfer.closed"` (Data: `{transfer_id}`), `EvRelayRequest="relay.request"` (Data: `RelayRequest`)
  - `type RelayRequest struct { RelayID, TransferID string; FileIndex int; Offset int64 }`
  - `func AccessToken(secret []byte, transferID, deviceID string) string` – `deviceID + "." + hex(HMAC-SHA256(secret, transferID+"|"+deviceID))`
  - `func VerifyAccessToken(secret []byte, transferID, token string) (deviceID string, ok bool)` – konstantzeitiger Vergleich

- [ ] **Step 1: Go installieren und Repo anlegen**

Run: `winget install --id GoLang.Go -e` (danach neue Shell), dann `go version` → Expected: `go version go1.2x ...`. `git init` im Repo-Wurzelordner; `.gitignore` mit `*.exe`, `/server/bin/`, `*.db`, `.remember/`. In `server/`: `go mod init share`.

- [ ] **Step 2: Failing Tests schreiben** (`types_test.go`)

```go
func TestStatusFinal(t *testing.T) {
    for _, s := range []Status{StatusDelivered, StatusDeclined, StatusMissed, StatusCancelled, StatusFailed} {
        if !s.Final() { t.Errorf("%s should be final", s) }
    }
    for _, s := range []Status{StatusWaiting, StatusAccepted, StatusDownloading} {
        if s.Final() { t.Errorf("%s should not be final", s) }
    }
}
func TestAccessTokenRoundTrip(t *testing.T) {
    sec := []byte("0123456789abcdef0123456789abcdef")
    tok := AccessToken(sec, "t1", "devA")
    id, ok := VerifyAccessToken(sec, "t1", tok)
    if !ok || id != "devA" { t.Fatal("valid token rejected") }
    if _, ok := VerifyAccessToken(sec, "t2", tok); ok { t.Fatal("token valid for other transfer") }
    if _, ok := VerifyAccessToken([]byte("other-secret-other-secret-000000"), "t1", tok); ok { t.Fatal("token valid with other secret") }
    if _, ok := VerifyAccessToken(sec, "t1", "devA.00"); ok { t.Fatal("garbage accepted") }
}
func TestTransferJSONSnakeCase(t *testing.T) {
    b, _ := json.Marshal(Transfer{SenderID: "x", ExpiresAt: time.Unix(0, 0).UTC()})
    if !bytes.Contains(b, []byte(`"sender_id":"x"`)) || !bytes.Contains(b, []byte(`"expires_at"`)) { t.Fatal(string(b)) }
}
```

- [ ] **Step 3:** `cd server && go test ./internal/protocol/` → Expected: FAIL (undefined).
- [ ] **Step 4:** Typen und Token-Funktionen implementieren.
- [ ] **Step 5:** `go test ./internal/protocol/` → PASS.
- [ ] **Step 6: Commit** – `git add -A && git commit -m "feat: project skeleton and protocol types"`

---

### Task 2: Store – Geräte, Tokens, Kopplungscodes

**Files:**
- Create: `server/internal/store/store.go`, `server/internal/store/devices.go`, `server/internal/store/devices_test.go`

**Interfaces:**
- Produces (`package store`):
  - `func Open(path string) (*Store, error)` (`":memory:"` für Tests; WAL; Migration beim Öffnen), `func (s *Store) Close() error`
  - `var ErrNotFound, ErrConflict, ErrInvalidCode error`
  - `func (s *Store) CreatePairCode(now time.Time) (string, error)` – 6 Zeichen aus `ABCDEFGHJKLMNPQRSTUVWXYZ23456789`, gültig 10 Min
  - `func (s *Store) RedeemPairCode(code string, now time.Time, name string, typ protocol.DeviceType, fp string) (deviceID, token string, err error)` – Code case-insensitiv, einmalig; Name eindeutig case-insensitiv (`ErrConflict`); Token 32 Zufallsbytes hex, gespeichert nur als SHA-256
  - `func (s *Store) DeviceByToken(token string) (protocol.Device, error)`
  - `func (s *Store) Devices() ([]protocol.Device, error)`, `func (s *Store) Device(id string) (protocol.Device, error)`
  - `func (s *Store) UpdateDevice(id string, name *string, fcmToken *string) error`, `func (s *Store) FCMToken(id string) (string, error)`
  - `func (s *Store) TouchDevice(id string, now time.Time) error`, `func (s *Store) DeleteDevice(id string) error`, `func (s *Store) DeviceByName(name string) (protocol.Device, error)`

- [ ] **Step 1: Failing Tests**

```go
func TestPairCodeSingleUseAndExpiry(t *testing.T) {
    s := mustOpen(t); now := time.Now()
    code, _ := s.CreatePairCode(now)
    if len(code) != 6 { t.Fatal(code) }
    id, tok, err := s.RedeemPairCode(strings.ToLower(code), now, "Pixel", protocol.DeviceAndroid, "fp")
    if err != nil || id == "" || tok == "" { t.Fatal(err) }
    if _, _, err := s.RedeemPairCode(code, now, "Other", protocol.DeviceAndroid, "fp"); !errors.Is(err, ErrInvalidCode) { t.Fatal("reuse allowed") }
    c2, _ := s.CreatePairCode(now)
    if _, _, err := s.RedeemPairCode(c2, now.Add(11*time.Minute), "Late", protocol.DeviceAndroid, "fp"); !errors.Is(err, ErrInvalidCode) { t.Fatal("expired code accepted") }
}
func TestDuplicateNameRejected(t *testing.T) { /* zweites Gerät "pixel" nach "Pixel" → ErrConflict */ }
func TestDeviceByToken(t *testing.T) { /* richtiges Token → Gerät; falsches → ErrNotFound; nach DeleteDevice → ErrNotFound */ }
```

- [ ] **Step 2:** `go test ./internal/store/` → FAIL.
- [ ] **Step 3:** Implementieren (Tabellen `devices(id, name, name_lc UNIQUE, type, token_hash UNIQUE, cert_fp, fcm_token, last_seen)`, `pair_codes(code PK, expires_at)`).
- [ ] **Step 4:** `go test ./internal/store/` → PASS.
- [ ] **Step 5: Commit** – `feat(store): devices and pairing codes`

---

### Task 3: Store – Angebote und Zustellungen

**Files:**
- Create: `server/internal/store/transfers.go`, `server/internal/store/transfers_test.go`

**Interfaces:**
- Consumes: Task 1 Typen, Task 2 `Store`.
- Produces:
  - `func (s *Store) CreateTransfer(t protocol.Transfer, secret []byte, lanCandidates []string, recipients []string) error` – legt je Empfänger eine Zustellung `waiting` an
  - `func (s *Store) Transfer(id string) (protocol.Transfer, error)` (mit Deliveries), `func (s *Store) TransferSecret(id string) ([]byte, error)`, `func (s *Store) LANCandidates(id string) ([]string, error)`
  - `func (s *Store) Incoming(deviceID string) ([]protocol.Transfer, error)` – nicht geschlossene Angebote mit eigener Zustellung, die nicht final ist
  - `func (s *Store) Outgoing(senderID string) ([]protocol.Transfer, error)` – nicht geschlossene eigene Angebote
  - `func (s *Store) Delivery(transferID, deviceID string) (protocol.Delivery, error)`
  - `func (s *Store) SetDelivery(transferID, deviceID string, st protocol.Status, path protocol.Path, bytes int64, now time.Time) error`
  - `func (s *Store) CloseTransfer(id string) error`
  - `func (s *Store) OpenTransfers() ([]protocol.Transfer, error)` (für Ablauf-Schleife)
  - `func (s *Store) PurgeClosedBefore(t time.Time) error` (Metadaten 1 h nach Schließen löschen)

- [ ] **Step 1: Failing Tests** – `TestIncomingOnlyNonFinal` (Empfänger sieht Angebot; nach `SetDelivery(declined)` nicht mehr), `TestOutgoingHasDeliveries` (2 Empfänger → `len(Deliveries)==2`, beide `waiting`), `TestClosedTransferHidden` (nach `CloseTransfer` weder Incoming noch Outgoing), `TestPurge` (geschlossenes Angebot nach `PurgeClosedBefore(now+2h)` → `Transfer` liefert `ErrNotFound`).
- [ ] **Step 2:** FAIL. **Step 3:** Implementieren (Tabellen `transfers(..., files_json, secret, lan_json, closed_at)`, `deliveries(transfer_id, device_id, status, path, bytes, updated_at, PK(transfer_id, device_id))`). **Step 4:** PASS.
- [ ] **Step 5: Commit** – `feat(store): transfers and deliveries`

---

### Task 4: Server – Gerüst, Auth, Kopplung, Geräte

**Files:**
- Create: `server/internal/server/server.go`, `server/internal/server/pair.go`, `server/internal/server/pair_test.go`, `server/internal/server/testutil_test.go`

**Interfaces:**
- Consumes: Store (Task 2).
- Produces:
  - `type Config struct { MaxOfferMinutes int; Now func() time.Time; Notifier Notifier; RelayWait time.Duration /* Standard 15s */ }`
  - `func New(st *store.Store, cfg Config) *Server`; `func (s *Server) Handler() http.Handler`; `func (s *Server) Run(ctx context.Context)` (Hintergrund-Schleifen, ab Task 6)
  - `type Notifier interface { Notify(ctx context.Context, fcmToken string, data map[string]string) error }` (Datei `notify.go`, hier nur Interface + `NopNotifier`)
  - Endpunkte: `POST /v1/pair` (ohne Auth) → 200 `PairResponse`, 400 bei ungültigem Typ/leerem Namen, 403 bei ungültigem Code, 409 bei Namenskonflikt; `POST /v1/pair/codes` → `{"code":"…","expires_at":…}`; `GET /v1/devices` → `[]Device` (mit `Online` aus dem Hub, bis Task 5 immer false); `PATCH /v1/devices/me` `{name?, fcm_token?}`; `DELETE /v1/devices/me`
  - Test-Helfer (`testutil_test.go`): `newTestServer(t) (*httptest.Server, *Server, *fakeClock)`, `pairDevice(t, ts, name string, typ protocol.DeviceType) (id, token string)`

- [ ] **Step 1: Failing Tests** – `TestUnauthenticatedRejected` (`GET /v1/devices` ohne/mit falschem Bearer → 401), `TestPairFlow` (Code über `store.CreatePairCode`, `POST /v1/pair` → Token; `GET /v1/devices` mit Token listet das Gerät), `TestPairedDeviceCanCreateCode` (`POST /v1/pair/codes` → 6-stelliger Code, mit dem ein zweites Gerät koppeln kann), `TestDeleteMe` (danach 401).
- [ ] **Step 2:** FAIL. **Step 3:** Implementieren (`http.ServeMux` mit Go-1.22-Mustern, Middleware setzt Gerät in Context und ruft `TouchDevice`). **Step 4:** PASS.
- [ ] **Step 5: Commit** – `feat(server): auth, pairing, device endpoints`

---

### Task 5: Server – Ereignis-Hub (WebSocket)

**Files:**
- Create: `server/internal/server/hub.go`, `server/internal/server/hub_test.go`

**Interfaces:**
- Produces:
  - `GET /v1/events` (WebSocket, Bearer-Auth per Header oder `?token=`), Server schickt JSON-`protocol.Event`s, Ping alle 30 s
  - `func (h *hub) Send(deviceID string, ev protocol.Event) bool` (false = nicht verbunden), `func (h *hub) Online(deviceID string) bool`, `func (h *hub) OnDisconnect(fn func(deviceID string))`
  - Ein Gerät darf mehrere Verbindungen haben; `Send` geht an alle.

- [ ] **Step 1: Failing Tests** – `TestEventDelivered` (Client verbindet, `hub.Send` → Client liest Event mit gleichem Typ), `TestOnlineFlag` (`GET /v1/devices` zeigt `online:true` während verbunden, `false` nach Schließen – mit `eventually`-Helfer bis 2 s), `TestDisconnectCallback`.
- [ ] **Step 2:** FAIL. **Step 3:** Implementieren mit `github.com/coder/websocket`. **Step 4:** PASS.
- [ ] **Step 5: Commit** – `feat(server): websocket event hub`

---

### Task 6: Server – Angebote, Annehmen, Ablauf

**Files:**
- Create: `server/internal/server/transfers.go`, `server/internal/server/transfers_test.go`

**Interfaces:**
- Consumes: Store (Task 3), Hub (Task 5), `protocol.AccessToken`.
- Produces – Endpunkte:
  - `POST /v1/transfers` `CreateTransferRequest` → 201 `CreateTransferResponse`. Regeln: `Minutes` 0 oder > Max → Max; Text/Link > 65536 Bytes → 413; `files` ohne Dateien → 400; Ziele unbekannt, Agent oder Sender selbst → 400; Broadcast → alle Nicht-Agent-Geräte außer Sender. Nach Anlage: `offer.new` an jeden Empfänger (Hub; zusätzlich Notifier für Android mit FCM-Token, Daten `{type:"offer.new", transfer_id}`).
  - `GET /v1/transfers?role=incoming|outgoing`, `GET /v1/transfers/{id}` (nur Sender oder Empfänger, sonst 404)
  - `DELETE /v1/transfers/{id}` (nur Sender) → offene Zustellungen `cancelled`, Angebot geschlossen, `offer.removed` an Empfänger, `transfer.closed` an Sender.
  - `POST /v1/transfers/{id}/accept` → `AcceptResponse` (Zugangstoken = `AccessToken(secret, id, receiverID)`, `RelayBase`). 409 wenn abgelaufen, geschlossen oder Zustellung final ≠ angenommen; zweites Annehmen → 200 mit gleicher Antwort. Für text/link: Zustellung direkt `delivered`.
  - `POST /v1/transfers/{id}/decline` → 409 wenn bereits angenommen/final.
  - `POST /v1/transfers/{id}/progress` `ProgressRequest` → Status `downloading`; `POST /v1/transfers/{id}/delivered` → `delivered`.
  - Jede Zustandsänderung → `delivery.updated` an Sender.
  - **Sender-Disconnect:** Hat der Sender nach `OnDisconnect` 30 s lang keine Verbindung, werden seine offenen Angebote wie `DELETE` behandelt.
  - **Ablauf-Schleife** (`Run`, alle 5 s, Uhr aus `Config.Now`): nach `ExpiresAt` → `waiting` → `missed` (+ `offer.removed`); `accepted`/`downloading` ohne Fortschrittsmeldung seit 2 Min → `failed`. Angebot schließen (`transfer.closed` an Sender), wenn alle Zustellungen final sind (gezielt auch vor Ablauf; Broadcast erst nach Ablauf). Metadaten-Purge 1 h nach Schließen.
  - `func (s *Server) tick(ctx context.Context)` – ein Durchlauf der Schleife (für Tests direkt aufrufbar).

- [ ] **Step 1: Failing Tests**

```go
func TestCreateTargetedNotifiesReceiver(t *testing.T)   // Empfänger-WebSocket erhält offer.new mit gleicher Transfer-ID
func TestBroadcastExcludesAgentsAndSender(t *testing.T) // Sender Agent + 2 Android + 1 Agent → genau 2 Zustellungen
func TestMinutesClamped(t *testing.T)                   // Minutes: 60 → ExpiresAt == now+10min
func TestTextTooLarge(t *testing.T)                     // 65537 Bytes → 413; 65536 → 201
func TestAcceptReturnsValidToken(t *testing.T)          // VerifyAccessToken(secret aus CreateResponse, id, tok) → receiverID
func TestAcceptRules(t *testing.T) {
    // zweites accept → 200 mit identischem AccessToken
    // decline nach accept → 409
    // accept nach clock.Advance(11*time.Minute) + tick → 409, Status missed
}
func TestTargetedClosesWhenAllFinal(t *testing.T)       // 1 Ziel, decline → Sender erhält transfer.closed
func TestBroadcastStaysOpenUntilExpiry(t *testing.T)    // decline durch alle → noch offen; nach Ablauf+tick geschlossen
func TestCancelNotifiesReceivers(t *testing.T)          // DELETE → Empfänger erhält offer.removed, Zustellung cancelled
func TestSenderDisconnectCancels(t *testing.T)          // Review Focus 2: Sender-WS schließen, clock +31s, tick → Zustellungen cancelled
func TestStalledDownloadFails(t *testing.T)             // accept, progress, clock +3min (nach Ablauf), tick → failed
func TestNotifierCalledForAndroid(t *testing.T)         // fakeNotifier erhält offer.new für Gerät mit FCM-Token
```

- [ ] **Step 2:** FAIL. **Step 3:** Implementieren. **Step 4:** `go test ./internal/server/` → PASS.
- [ ] **Step 5: Commit** – `feat(server): transfers, accept/decline, expiry`

---

### Task 7: Server – Relay-Broker

**Files:**
- Create: `server/internal/server/relay.go`, `server/internal/server/relay_test.go`

**Interfaces:**
- Produces:
  - `GET /v1/transfers/{id}/files/{n}` (Empfänger, Zustellung `accepted`/`downloading`, Angebot nicht `cancelled`) mit optionalem `Range: bytes=N-` (nur offenes Ende; andere Formen → 416). Server erzeugt `RelayID`, sendet `relay.request` an Sender, wartet bis `RelayWait` (15 s) auf dessen Upload, antwortet dann 200 bzw. 206 mit `Content-Length` (= Größe − Offset), `Content-Range`, `Content-Type` aus `FileItem.MIME`, und kopiert den Strom. Kein Upload binnen Frist / Sender offline → 502. Empfänger-Abbruch → Upload-Request des Senders wird abgebrochen.
  - `POST /v1/relay/{relayID}` (nur der Sender des Angebots) – Body ist der Bytestrom ab Offset; Antwort 200 nach vollständiger Weitergabe, 410 bei unbekannter/abgelaufener Relay-ID.
  - Kein Schreiben auf Platte; Kopie über `io.CopyBuffer` mit 256-KB-Puffer, Flush nach jedem Block.

- [ ] **Step 1: Failing Tests** – `TestRelayStreamsRange` (Fake-Sender lauscht auf `relay.request`, lädt `data[offset:]` hoch; Empfänger-`GET` mit `Range: bytes=1000-` bekommt 206 und exakt `data[1000:]`), `TestRelayRequiresAccept` (nicht angenommen → 403), `TestRelaySenderAbsent` (Review Focus 2: kein Sender-Upload, `RelayWait=200ms` → 502 innerhalb 1 s), `TestRelayWrongUploader` (anderes Gerät postet auf Relay-ID → 403), `TestRelayRangeUnsupported` (`bytes=0-10` → 416).
- [ ] **Step 2:** FAIL. **Step 3:** Implementieren. **Step 4:** PASS.
- [ ] **Step 5: Commit** – `feat(server): live relay without storage`

---

### Task 8: Server – FCM-Notifier

**Files:**
- Modify: `server/internal/server/notify.go`
- Create: `server/internal/server/notify_test.go`

**Interfaces:**
- Produces: `func NewFCMNotifier(ctx context.Context, credentialsPath string) (Notifier, error)` – sendet Datennachricht mit `Android.Priority = "high"`, TTL = 10 Min; bei Fehler `registration-token-not-registered` wird der Fehler `ErrTokenGone` zurückgegeben, worauf der Server das FCM-Token des Geräts löscht.

- [ ] **Step 1: Failing Test** – `TestTokenGoneClearsFCMToken` (Fake-Notifier gibt `ErrTokenGone` zurück → nach `offer.new` ist `store.FCMToken(id) == ""`).
- [ ] **Step 2:** FAIL. **Step 3:** Implementieren (`firebase.google.com/go/v4/messaging`; Server-Logik für `ErrTokenGone` in `transfers.go`). **Step 4:** PASS. (Echter FCM-Versand wird manuell mit Plan 2 geprüft.)
- [ ] **Step 5: Commit** – `feat(server): firebase notifier`

---

### Task 9: `share-server`-Binary, Konfiguration, Betriebsdoku

**Files:**
- Create: `server/cmd/share-server/main.go`, `server/cmd/share-server/config.go`, `server/cmd/share-server/config_test.go`, `docs/deploy/Caddyfile`, `docs/deploy/share-server.service`, `docs/deploy/README.md`

**Interfaces:**
- Produces:
  - Konfig (TOML, Pfad per `-config`, Standard `/etc/share-server/config.toml`): `listen = "127.0.0.1:8080"`, `db_path = "/var/lib/share-server/share.db"`, `public_url` (für QR), `max_offer_minutes = 10`, `firebase_credentials = ""`.
  - `func loadConfig(path string) (Config, error)` mit Standardwerten; `max_offer_minutes` > 10 → auf 10 begrenzt.
  - Befehle: `share-server serve`, `share-server pair` (Code + QR mit Inhalt `share://pair?url=<public_url>&code=<code>` im Terminal), `share-server devices ls`, `share-server devices rm <name>`.
  - Caddyfile: `reverse_proxy 127.0.0.1:8080 { flush_interval -1 }`, keine `request_body`-Begrenzung, Timeouts für `/v1/relay/*`, `/v1/transfers/*/files/*`, `/v1/events` aus. README: Installation, systemd, playit-HTTPS-Tunnel → Caddy, Firebase-Service-Account, Prüfpunkt „Caddy bekommt Zertifikat für playit-URL“.

- [ ] **Step 1: Failing Test** – `TestConfigDefaultsAndClamp` (leere Datei → Standardwerte; `max_offer_minutes = 30` → 10).
- [ ] **Step 2:** FAIL. **Step 3:** Implementieren. **Step 4:** PASS; zusätzlich `GOOS=linux GOARCH=amd64 CGO_ENABLED=0 go build ./cmd/share-server` → erfolgreich.
- [ ] **Step 5: Manuell:** `go run ./cmd/share-server serve -config <tmp.toml>` und `go run ./cmd/share-server pair -config <tmp.toml>` → QR und Code erscheinen; `curl -s localhost:8080/v1/devices` → 401.
- [ ] **Step 6: Commit** – `feat: share-server binary and deployment docs`

---

### Task 10: Geräte-Zertifikat und lokale Adressen

**Files:**
- Create: `server/internal/tlsid/tlsid.go`, `server/internal/tlsid/tlsid_test.go`, `server/internal/lanaddr/lanaddr.go`, `server/internal/lanaddr/lanaddr_test.go`

**Interfaces:**
- Produces:
  - `func Generate(name string) (certPEM, keyPEM []byte, err error)` – ECDSA P-256, selbst signiert, 20 Jahre gültig
  - `func Fingerprint(certDER []byte) string` – SHA-256 hex, Kleinbuchstaben
  - `func ServerConfig(certPEM, keyPEM []byte) (*tls.Config, error)`
  - `func PinnedClientConfig(fingerprint string) *tls.Config` – `InsecureSkipVerify` + `VerifyPeerCertificate`, das exakt den Fingerabdruck des Leaf-Zertifikats prüft
  - `package lanaddr`: `func Candidates(port int) []string` – IPv4 aus 10/8, 172.16/12, 192.168/16 auf aktiven Nicht-Loopback-Schnittstellen; Schnittstellen, deren Name (case-insensitiv) `vethernet`, `virtualbox`, `vmware`, `docker`, `tailscale`, `zerotier`, `wg`, `tun`, `tap` enthält, ausgeschlossen. `func filter(ifs []iface) []string` als testbarer Kern.

- [ ] **Step 1: Failing Tests** – `TestPinnedAcceptsOwnCert` / `TestPinnedRejectsOtherCert` (TLS-Server mit Zertifikat A; Client gepinnt auf A verbindet, gepinnt auf B → Handshake-Fehler), `TestFilter` (Eingaben: `{"Ethernet", 192.168.1.5}`, `{"vEthernet (WSL)", 172.20.0.1}`, `{"Tailscale", 100.64.0.2}`, `{"WLAN", 8.8.8.8}` → nur `"192.168.1.5:47800"`).
- [ ] **Step 2:** FAIL. **Step 3:** Implementieren. **Step 4:** PASS.
- [ ] **Step 5: Commit** – `feat: device certificates and LAN address detection`

---

### Task 11: Client – API, Konfiguration, Ereignisse, Namen

**Files:**
- Create: `server/internal/client/api.go`, `server/internal/client/config.go`, `server/internal/client/events.go`, `server/internal/client/names.go`, `server/internal/client/names_test.go`, `server/internal/client/api_test.go`

**Interfaces:**
- Produces (`package client`):
  - `type Config struct { ServerURL, DeviceID, Token string; CertPEM, KeyPEM []byte }`; `func LoadConfig(path string) (Config, error)`; `func (c Config) Save(path string) error` (Datei-Rechte 0600); `func DefaultConfigPath() string` (= `os.UserConfigDir()/share/config.json`)
  - `func Pair(ctx context.Context, serverURL, code, name string, typ protocol.DeviceType) (Config, error)` – erzeugt Zertifikat (tlsid), koppelt
  - `type API struct{...}`; `func NewAPI(cfg Config) *API`; Methoden: `Devices(ctx) ([]protocol.Device, error)`, `CreateTransfer(ctx, protocol.CreateTransferRequest) (protocol.CreateTransferResponse, error)`, `Transfer(ctx, id)`, `Incoming(ctx)`, `Outgoing(ctx)`, `Cancel(ctx, id) error`, `Accept(ctx, id) (protocol.AcceptResponse, error)`, `Decline(ctx, id) error`, `Progress(ctx, id string, p protocol.ProgressRequest) error`, `Delivered(ctx, id) error`, `RelayDownload(ctx, id string, n int, offset int64) (io.ReadCloser, error)`, `RelayUpload(ctx, relayID string, r io.Reader) error`, `CreatePairCode(ctx) (string, error)`
  - `type APIError struct { Status int; Msg string }`
  - `func (a *API) Events(ctx context.Context) <-chan protocol.Event` – verbindet `/v1/events`, Wiederverbindung mit Backoff 1 s → max. 30 s, Kanal schließt bei `ctx.Done()`
  - `names.go`: `func ResolveTargets(devs []protocol.Device, names []string) ([]string, error)` – erst exakt (case-insensitiv), dann eindeutiges Präfix; Agenten ausgeschlossen; Fehler nennt mehrdeutige/unbekannte Namen. `func SafeFileName(name string) string`. `func UniquePath(dir, name string) string`.

- [ ] **Step 1: Failing Tests**

```go
func TestResolveTargets(t *testing.T) {
    devs := []protocol.Device{{ID: "1", Name: "Pixel 8", Type: "android"}, {ID: "2", Name: "Pixel Tablet", Type: "android"}, {ID: "3", Name: "Desktop", Type: "windows"}, {ID: "4", Name: "Agent", Type: "agent"}}
    // "desktop" → ["3"]; "pixel 8" → ["1"]; "desk" → ["3"]; "pixel" → Fehler (mehrdeutig); "agent" → Fehler; "foo" → Fehler
}
func TestSafeFileName(t *testing.T) { // Review Focus 3
    // "../evil.txt"→"evil.txt"; "a/b.txt"→"b.txt"; `a\b.txt`→"b.txt"; "CON"→"_CON"; "con.txt"→"_con.txt"; ""→"file"; "x:y?.txt"→"x_y_.txt"; "  .hidden. "→".hidden"
}
func TestUniquePath(t *testing.T) { // vorhandene "a.txt" → "a (1).txt"; vorhandene "a.txt","a (1).txt" → "a (2).txt"
}
func TestAPIAgainstServer(t *testing.T) { // httptest mit server.New; Pair, Devices, CreateTransfer, Accept funktionieren
}
```

- [ ] **Step 2:** FAIL. **Step 3:** Implementieren. **Step 4:** PASS.
- [ ] **Step 5: Commit** – `feat(client): api, config, events, name helpers`

---

### Task 12: Client – Senden (Offer-Manager, lokaler Endpunkt, Relay)

**Files:**
- Create: `server/internal/client/sender.go`, `server/internal/client/sender_test.go`

**Interfaces:**
- Consumes: API (Task 11), tlsid, lanaddr, `protocol.VerifyAccessToken`.
- Produces:
  - `type Sender struct{...}`; `func NewSender(api *API, cfg Config, opts SenderOptions) *Sender`; `type SenderOptions struct { Port int /* 0 = 47800 */; Candidates func(port int) []string /* nil = lanaddr.Candidates; Tests: 127.0.0.1 oder leer */ }`
  - `func (s *Sender) Run(ctx context.Context) error` – hält Ereignisverbindung, beantwortet `relay.request`, entfernt Angebote bei `transfer.closed`; startet den lokalen Endpunkt bei Bedarf und stoppt ihn, wenn kein Datei-Angebot mehr läuft
  - `func (s *Sender) OfferFiles(ctx context.Context, paths []string, targets []string, broadcast bool, message string, minutes int) (protocol.Transfer, error)` – stat jede Datei (Größe, ModTime, MIME per Endung), meldet Angebot mit LAN-Kandidaten
  - `func (s *Sender) OfferText(ctx context.Context, kind protocol.Kind, text string, targets []string, broadcast bool) (protocol.Transfer, error)`
  - `func (s *Sender) Updates() <-chan protocol.Event` – `delivery.updated` und `transfer.closed` für eigene Angebote
  - Lokaler Endpunkt (HTTPS, Geräte-Zertifikat): `GET /t/{id}/ping` → 204; `GET /t/{id}/files/{n}` mit `Range: bytes=N-` → 200/206. Auth: `Authorization: Bearer <AccessToken>` via `VerifyAccessToken(secret, id, …)`; sonst 401; unbekanntes/geschlossenes Angebot → 404; Datei seit Angebot verändert (Größe/ModTime) → 409.
  - Relay-Antwort: bei `relay.request` Datei öffnen, auf Offset springen, `api.RelayUpload` streamen; bei veränderter Datei nicht hochladen.

- [ ] **Step 1: Failing Tests** – `TestLocalEndpointRequiresToken` (ohne/mit fremdem Token → 401, gültiges Token → 204 auf ping), `TestLocalEndpointRange` (`bytes=5-` → 206, Inhalt `data[5:]`), `TestLocalEndpointModifiedFile` (Datei nach Angebot ändern → 409), `TestEndpointStopsWhenClosed` (nach `transfer.closed` → Verbindung abgelehnt oder 404), `TestRelayResponder` (Server aus Task 7 + Sender: Relay-Download liefert die Datei).
- [ ] **Step 2:** FAIL. **Step 3:** Implementieren. **Step 4:** PASS.
- [ ] **Step 5: Commit** – `feat(client): sender with local endpoint and relay responder`

---

### Task 13: Client – Empfangen (Wegwahl, Fortsetzen, Ablage)

**Files:**
- Create: `server/internal/client/receiver.go`, `server/internal/client/receiver_test.go`

**Interfaces:**
- Consumes: API, `tlsid.PinnedClientConfig`, `SafeFileName`, `UniquePath`.
- Produces:
  - `type Receiver struct{...}`; `func NewReceiver(api *API, downloadDir string) *Receiver`
  - `func (r *Receiver) Accept(ctx context.Context, t protocol.Transfer, progress func(bytes int64, path protocol.Path)) (paths []string, err error)` – für text/link: gibt `nil, nil` zurück (Inhalt steht in `t.Text`); für Dateien: `api.Accept`, dann je Datei:
    1. `choosePath`: alle `LANCandidates` parallel `GET https://<c>/t/{id}/ping` mit gepinntem Zertifikat und Token, Zeitgrenze 1,5 s, erster Erfolg gewinnt; sonst Relay.
    2. Download nach `<downloadDir>/<SafeFileName>.part` mit `Range: bytes=<Größe der .part>-`.
    3. Bei Fehler: neu `choosePath`, Backoff 1 s/2 s/4 s/8 s/16 s; nach 5 aufeinanderfolgenden Fehlschlägen ohne Fortschritt → Fehler.
    4. Fertig, wenn `.part`-Größe = `FileItem.Size` → umbenennen auf `UniquePath(downloadDir, SafeFileName(name))`.
    - `api.Progress` höchstens 1×/s; nach allen Dateien `api.Delivered`.
  - `func (r *Receiver) Decline(ctx context.Context, id string) error`
  - `func (r *Receiver) Incoming(ctx context.Context) ([]protocol.Transfer, error)`
  - Option für Tests: `func (r *Receiver) SetProbeTimeout(d time.Duration)`

- [ ] **Step 1: Failing Tests**

```go
func TestPrefersLAN(t *testing.T)            // Sender mit Kandidat 127.0.0.1 → Progress meldet PathLAN, Datei identisch
func TestFallsBackToRelay(t *testing.T)      // Sender ohne Kandidaten → PathRelay, Datei identisch
func TestUnreachableCandidateFallsBack(t *testing.T) // Kandidat 127.0.0.1:1 (geschlossen) → Relay, Gesamtzeit < 3 s
func TestResumeSwitchesPath(t *testing.T) {  // Review Focus 1
    // 8-MB-Datei; lokaler Endpunkt wird nach ~3 MB geschlossen (Testhook im Sender);
    // Download setzt über Relay fort; Ergebnis byte-identisch (sha256 gleich);
    // Relay-Request beginnt mit Offset > 0
}
func TestSanitizedAndUniqueNames(t *testing.T) { // Review Focus 3: Angebot mit Name "../evil.txt", Zieldatei existiert → "evil (1).txt" im Download-Ordner, nichts außerhalb
}
func TestTextAcceptDelivers(t *testing.T)    // Text-Angebot → Accept gibt nil zurück, Sender sieht delivered
```

- [ ] **Step 2:** FAIL. **Step 3:** Implementieren. **Step 4:** PASS.
- [ ] **Step 5: Commit** – `feat(client): receiver with LAN-first path selection and resume`

---

### Task 14: Ende-zu-Ende-Tests

**Files:**
- Create: `server/internal/e2e/e2e_test.go`

**Interfaces:**
- Consumes: alles bisherige; echter `server.New` über `httptest`, echte Sender/Receiver.

- [ ] **Step 1: Tests schreiben**
  - `TestBroadcastConcurrentReceivers` (Review Focus 4): Agent bietet 20-MB-Datei an alle an; zwei Empfänger (android, windows) nehmen gleichzeitig an (Goroutinen), einer per LAN, einer erzwungen per Relay (leere Kandidatenliste für ihn per Probe-Timeout 1 ms); beide Dateien byte-identisch; Sender-Updates zeigen beide `delivered`; Angebot bleibt bis Ablauf offen.
  - `TestTargetedLifecycle`: gezielt an 1 Gerät, Annahme, Download, Sender erhält `transfer.closed`.
  - `TestCancelDuringDownload`: Sender bricht mitten im Download ab → Empfänger-`Accept` liefert Fehler, `.part` bleibt nicht als fertige Datei liegen.
  - `TestDeclineAndMissed`: Ablehnen → `declined`; anderes Gerät reagiert nicht, Uhr +11 Min → `missed`.
- [ ] **Step 2:** `go test ./internal/e2e/ -race` → Tests, die Lücken aufdecken, schlagen fehl; Ursache in der betroffenen Komponente beheben (mit Regressions-Test dort).
- [ ] **Step 3:** `go test ./... -race` → alle PASS.
- [ ] **Step 4: Protokolldoku** `docs/protocol.md` schreiben: Endpunkte, JSON-Beispiele, Ereignisse, Token-Format, lokaler Endpunkt, Wegwahl, Relay – als Vertrag für Plan 2/3 (aus den Typen in `protocol` und den Handlern abgeleitet).
- [ ] **Step 5: Commit** – `test: end-to-end scenarios; docs: protocol`

---

### Task 15: `share`-CLI

**Files:**
- Create: `server/cmd/share/main.go`, `server/cmd/share/commands.go`, `server/cmd/share/commands_test.go`

**Interfaces:**
- Consumes: `client` (Tasks 11–13).
- Produces – Befehle (Konfig aus `client.DefaultConfigPath()`, überschreibbar mit `-config`):
  - `share pair <server-url> <code> [-name <name>]` (Standard-Name: Hostname + " Agent")
  - `share devices` – Tabelle Name, Typ, online
  - `share send <dateien…> (--to a,b | --all) [--minutes N] [--message M]` – läuft im Vordergrund, zeigt Status pro Empfänger (Zeile pro Gerät: Status, Weg, Prozent), endet bei `transfer.closed`; Strg+C → `Cancel` und Ende. Exit-Code 0, wenn mind. eine Zustellung `delivered`, sonst 1.
  - `share text "<text>" (--to … | --all)`, `share link <url> (--to … | --all)` – wie `send`, ohne lokalen Endpunkt
  - `share mcp` – startet Task 16
- `func run(ctx context.Context, args []string, stdout, stderr io.Writer) int` als testbarer Einstieg.

- [ ] **Step 1: Failing Tests** – `TestSendRequiresTarget` (weder `--to` noch `--all` → Exit 2, Hilfetext), `TestSendEndToEnd` (httptest-Server, gekoppelter Empfänger nimmt per `Receiver` an; `run(send …)` gibt 0 zurück und meldet `delivered`), `TestUnknownTargetMessage` (`--to foo` → Exit 2, Meldung nennt „foo“).
- [ ] **Step 2:** FAIL. **Step 3:** Implementieren (Standardbibliothek `flag`, Unterbefehle von Hand). **Step 4:** PASS; `GOOS=windows CGO_ENABLED=0 go build -o bin/share.exe ./cmd/share` → erfolgreich.
- [ ] **Step 5: Commit** – `feat: share CLI`

---

### Task 16: MCP-Server (`share mcp`)

**Files:**
- Create: `server/internal/mcpserver/mcpserver.go`, `server/internal/mcpserver/mcpserver_test.go`
- Modify: `server/cmd/share/commands.go` (Unterbefehl `mcp`)

**Interfaces:**
- Consumes: `client.Sender`, `client.API`, `client.ResolveTargets`.
- Produces: `func New(api *client.API, sender *client.Sender) *mcp.Server`; Transport stdio. Werkzeuge (Eingaben JSON-Schema, Ausgaben als Text + strukturiert):
  - `list_devices()` → `[{name, type, online, last_seen}]` (ohne Agenten)
  - `send_files(paths: string[], targets?: string[], all?: bool, message?: string, minutes?: int)` → `{transfer_id, expires_at, recipients}`; genau eines von `targets`/`all` erforderlich; relative Pfade relativ zum Arbeitsverzeichnis; fehlende Datei → Werkzeugfehler mit Pfad
  - `send_text(text, targets?, all?)`, `send_link(url, targets?, all?)` → `{transfer_id}`
  - `get_transfer_status(transfer_id, wait_seconds?: int /* max 120 */)` → `{closed, expires_at, deliveries:[{device, status, path, percent}]}`; mit `wait_seconds` wartet es auf die nächste Änderung oder das Zeitlimit
  - `cancel_transfer(transfer_id)`
  - Werkzeugbeschreibungen erwähnen: Empfänger muss bestätigen; Angebot läuft max. 10 Min und nur solange diese MCP-Sitzung läuft.

- [ ] **Step 1: Failing Tests** – mit In-Memory-Transport des go-sdk und httptest-Server: `TestListDevicesExcludesAgents`, `TestSendFilesNeedsExactlyOneTargetMode` (beides/keins → Werkzeugfehler), `TestSendFilesAndStatus` (Empfänger nimmt an und lädt; `get_transfer_status(wait_seconds=10)` liefert schließlich `delivered`), `TestMissingFile` (Fehlertext enthält den Pfad).
- [ ] **Step 2:** FAIL. **Step 3:** Implementieren. **Step 4:** `go test ./... -race` → PASS.
- [ ] **Step 5: Manuelle Prüfung:** Server lokal starten, `share pair`, in Claude Code eintragen (`claude mcp add share -- <pfad>\share.exe mcp`), `list_devices` aufrufen → Antwort ohne Fehler.
- [ ] **Step 6: Commit** – `feat: MCP server for agents`

---

## Selbstprüfung (gegen Spec)

- Kopplung/Code/QR/Entfernen → Tasks 2, 4, 9, 15. Gerät abmelden → Task 4.
- Gezielt/an alle, 10 Min, Abbruch, früheres Ende → Task 6. Text/Link im Angebot ≤ 64 KB → Task 6.
- Lokal zuerst + Relay ohne Speichern + Fortsetzen → Tasks 7, 12, 13. Zugangstoken nur für Angenommene → Tasks 1, 6, 12.
- Downloads dürfen nach Phasenende fertig laufen → Task 6 (Stall-Regel) + Task 12 (Endpunkt bis `transfer.closed`).
- FCM-Weckruf → Tasks 6, 8. WebSocket-Ereignisse/Online → Task 5.
- Metadaten nach 1 h gelöscht → Tasks 3, 6.
- CLI und MCP inkl. tolerante Namen → Tasks 11, 15, 16.
- Caddy/systemd/playit → Task 9.
- Android/Windows: bewusst Plan 2/3, Protokollvertrag aus Task 14.
