# share – Protokoll (v1)

Vertrag für alle Clients (Go-Referenz: `server/internal/client`, Typen: `server/internal/protocol`).
Alle JSON-Felder in `snake_case`, Zeiten RFC 3339 (UTC). Fehlerantworten: `{"error": "…"}`.

## Begriffe

| Begriff | Bedeutung |
|---|---|
| Gerät | `type`: `android` \| `windows` \| `agent`. Agenten sind nie Ziel. |
| Angebot (Transfer) | `kind`: `text` \| `link` \| `files`. Gezielt (`targets`) oder an alle (`broadcast`). Läuft max. 10 Min (`expires_at`). |
| Zustellung | Status je Empfänger: `waiting` → `accepted` → `downloading` → `delivered`; oder `declined`, `missed`, `cancelled`, `failed`. |
| Weg | `lan` (direkt vom Sender) oder `relay` (live durch den Server, nichts wird gespeichert). |

## Authentifizierung

Jede Anfrage außer `POST /v1/pair` trägt `Authorization: Bearer <geräte-token>`, sonst 401.
WebSocket: gleicher Header (oder `?token=` als Ausweichweg).

## Kopplung

QR-Inhalt: `share://pair?code=<CODE>&url=<server-url>`. Code: 6 Zeichen aus `ABCDEFGHJKLMNPQRSTUVWXYZ23456789`, einmalig, 10 Min gültig, Groß-/Kleinschreibung egal.

1. Client erzeugt ein selbst signiertes TLS-Zertifikat (ECDSA P-256) und berechnet den **Fingerabdruck** = SHA-256 über das DER-Zertifikat, Hex klein (64 Zeichen).
2. `POST /v1/pair`
   ```json
   {"code":"N5EAD5","name":"Pixel 8","type":"android","cert_fingerprint":"ab12…"}
   ```
   → `200 {"device_id":"…","token":"…"}` · `400` Name/Typ ungültig · `403` Code ungültig/abgelaufen · `409` Name vergeben
3. Token und Schlüssel sicher speichern.

Neuen Code für ein weiteres Gerät: `POST /v1/pair/codes` → `{"code":"…","expires_at":"…"}`.

## Geräte

- `GET /v1/devices` → `[{"id","name","type","online","last_seen","cert_fingerprint"}]`
- `PATCH /v1/devices/me` `{"name"?: "…", "fcm_token"?: "…"}` → 204 · 409 Name vergeben
- `DELETE /v1/devices/me` → 204 (eigene offene Angebote werden abgebrochen)

## Ereignisse (WebSocket `GET /v1/events`)

Server → Client, Textnachrichten `{"type": "...", "data": {...}}`. Clientnachrichten werden ignoriert. Server pingt alle 30 s.
Nach jedem (Wieder-)Verbinden: `GET /v1/transfers?role=incoming|outgoing` neu laden (Ereignisse während der Trennung gehen verloren).

| type | an | data |
|---|---|---|
| `offer.new` | Empfänger | Transfer (ohne `deliveries`) |
| `offer.removed` | Empfänger | `{"transfer_id"}` – zurückgezogen oder verpasst: Benachrichtigung entfernen |
| `delivery.updated` | Sender | `{"transfer_id","delivery":{"device_id","status","path","bytes","updated_at"}}` |
| `transfer.closed` | Sender | `{"transfer_id"}` – Angebot beendet, lokalen Endpunkt dafür schließen |
| `relay.request` | Sender | `{"relay_id","transfer_id","file_index","offset"}` – siehe Relay |

**Android/FCM:** Datennachricht (keine Notification-Payload), Priorität hoch, TTL 10 Min, `data = {"type": "offer.new"|"offer.removed", "transfer_id": "…"}`. Die App lädt dann `GET /v1/transfers/{id}` bzw. entfernt die Benachrichtigung. FCM-Token per `PATCH /v1/devices/me` melden; ungültige Tokens löscht der Server selbst.

**Sender müssen online bleiben:** Bei Datei-Angeboten braucht der Sender eine offene Ereignisverbindung. Ist er länger als 30 s getrennt, bricht der Server seine Datei-Angebote ab (Empfänger: `cancelled`).

## Senden

`POST /v1/transfers`
```json
{
  "kind": "files",
  "files": [{"name":"app.apk","size":52428800,"mime":"application/vnd.android.package-archive","mod_time":"…"}],
  "targets": ["Pixel 8"],          // Gerätenamen (Groß/klein egal) oder IDs; ODER
  "broadcast": false,              // true = alle Nicht-Agent-Geräte außer mir
  "message": "optional",
  "minutes": 10,                   // 0 oder >10 → 10
  "lan_candidates": ["192.168.1.20:47800"]
}
```
Text/Link: `{"kind":"text"|"link","text":"…", "targets"|"broadcast"}` – max. 65 536 Bytes (sonst 413), kein Datenweg nötig.

→ `201 {"transfer": {…inkl. deliveries…}, "secret": "<64 hex>"}` · `400` ungültige Ziele/Daten

Das `secret` (32 Byte) behält der Sender: damit prüft er Zugangstokens am lokalen Endpunkt.

Weitere Aufrufe:
- `GET /v1/transfers?role=outgoing` – eigene offene Angebote mit Zustellungen
- `GET /v1/transfers/{id}` – Sender sieht alle Zustellungen, Empfänger nur die eigene
- `DELETE /v1/transfers/{id}` – zurückziehen (nur Sender) → 204

Angebotsende: gezielt, sobald alle Zustellungen final sind; an alle erst nach `expires_at`. Downloads, die vor `expires_at` angenommen wurden, dürfen danach fertig laufen; ohne Fortschrittsmeldung für 2 Min nach Ablauf → `failed`.

## Lokaler Endpunkt (Sender)

- HTTPS mit dem Geräte-Zertifikat, Port bevorzugt **47800**, sonst frei; in `lan_candidates` stehen alle privaten IPv4-Adressen (10/8, 172.16/12, 192.168/16) mit dem tatsächlichen Port, ohne virtuelle/VPN-Schnittstellen.
- Nur aktiv, solange ein Datei-Angebot offen ist.
- `GET /t/{transfer_id}/ping` → 204
- `GET /t/{transfer_id}/files/{n}` → 200 bzw. 206 bei `Range: bytes=N-`
- Auth: `Authorization: Bearer <access_token>` → sonst 401. Unbekanntes Angebot → 404. Datei seit dem Anbieten geändert (Größe oder Änderungszeit) → 409.

**Zugangstoken:** `access_token = <empfänger-device-id> + "." + hex(HMAC-SHA256(secret, transfer_id + "|" + empfänger-device-id))`. Der Server stellt es beim Annehmen aus; der Sender prüft es mit seinem `secret` (konstantzeitiger Vergleich).

## Empfangen

1. Angebot anzeigen (`offer.new`, FCM oder `GET /v1/transfers?role=incoming`).
2. `POST /v1/transfers/{id}/accept` → 
   ```json
   {"lan_candidates":["192.168.1.20:47800"],"cert_fingerprint":"ab12…","access_token":"…","relay_base":"/v1/transfers/{id}/files/"}
   ```
   Text/Link: Zustellung ist sofort `delivered`, Antwort ist leer; Inhalt steht in `transfer.text`.
   `409` abgelaufen/abgebrochen. Erneutes Annehmen eines bereits angenommenen Angebots liefert dieselbe Antwort (zum Fortsetzen nach Neustart, auch nach Ablauf).
   Ablehnen: `POST /v1/transfers/{id}/decline` → 204 (nur aus `waiting`, sonst 409).
3. **Wegwahl** je Datei: alle `lan_candidates` parallel `GET https://<c>/t/{id}/ping` mit Token und **gepinntem Fingerabdruck** (Zertifikatskette nicht prüfen, nur Fingerabdruck des Leaf-Zertifikats), Zeitgrenze 1,5 s. Erste 204 gewinnt → `lan`. Sonst `relay`.
4. Download in `<name>.<tid8>-<n>.part`, fortsetzen mit `Range: bytes=<partgröße>-`. Bei Abbruch neu wählen (Weg darf wechseln). Nach 5 Fehlversuchen ohne Fortschritt: `POST /v1/transfers/{id}/fail`.
5. Fortschritt höchstens 1×/s: `POST /v1/transfers/{id}/progress {"bytes": <gesamt>, "path": "lan"|"relay"}`.
6. Fertig, wenn Teildatei = `size` → umbenennen: Basisname bereinigt (Pfadanteile weg, `<>:"|?*` und Steuerzeichen → `_`, Leerzeichen/Punkte am Ende weg, reservierte Windows-Namen `CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9` mit `_` davor, leer → `file`), bei Konflikt ` (1)`, ` (2)` … vor der Endung.
7. Nach allen Dateien: `POST /v1/transfers/{id}/delivered`.

## Relay

Empfänger: `GET /v1/transfers/{id}/files/{n}` (optional `Range: bytes=N-`; andere Range-Formen → 416).
Voraussetzung: eigene Zustellung `accepted`/`downloading` (sonst 403), Angebot offen (sonst 410).
Der Server schickt `relay.request` an den Sender und wartet bis 15 s; ohne Antwort → 502.

Sender: auf `relay.request` die Datei ab `offset` öffnen und als Rohdaten senden:
`POST /v1/relay/{relay_id}` (Body = Bytes ab Offset). 403 = nicht der Sender, 410 = Relay unbekannt/abgebrochen.
Ist die Datei verändert, nicht hochladen.

Der Server reicht den Strom ohne Zwischenspeicherung weiter (200 bzw. 206 mit `Content-Range`).
