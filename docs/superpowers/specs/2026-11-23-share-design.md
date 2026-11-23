# Share – privates Datei-Senden zwischen eigenen Geräten (Design)

Stand: 2026-11-23

## Zweck und Warum

Ein privates „Quick Share übers Internet“ für eine einzige Person und ihre Geräte. Android-Handys und Windows-PCs schicken sich gegenseitig Text, Links und Dateien (auch mehrere GB). KI-Agenten können über einen MCP ebenfalls senden, z. B. eine frisch gebaute APK direkt aufs Handy. Ein selbst gehosteter Server **vermittelt nur**: Er kennt die Geräte, verteilt Angebote und Adressen. Die Daten gehen **direkt von Gerät zu Gerät, lokal zuerst** (Geschwindigkeit, Bandbreite). Nur wenn keine direkte Verbindung möglich ist, reicht der Server die Daten live durch, ohne sie zu speichern. Jeder Empfang muss auf dem Zielgerät bestätigt werden. Die Oberfläche ist minimal, der Fokus liegt auf Funktion.

## Ziele

- Senden von Text, Links und Dateien (einzeln oder mehrere) zwischen eigenen Geräten.
- Große Dateien (mehrere GB), fortsetzbar nach Verbindungsabbruch.
- Übertragung **lokal zuerst**: Sind Sender und Empfänger im selben Netz, fließen die Daten direkt zwischen ihnen.
- Zwei Sendearten:
  - **gezielt** an ein oder mehrere ausgewählte Geräte;
  - **anbieten** an alle: jedes Gerät, das während der Angebotsphase annimmt, bekommt es.
- Empfang wird immer angefragt und muss bestätigt werden.
- Geräte registrieren sich nach einmaliger Kopplung automatisch als Sender und Empfänger.
- Android-App und Windows-Programm sind jeweils **Teilen-Ziel** im System.
- MCP für KI-Agenten zum gezielten Senden oder Anbieten.

## Nicht-Ziele (v1)

- **Speichern auf dem Server** jeglicher Art (auch nicht kurzzeitig). Ist der Empfänger in der Angebotsphase nicht erreichbar, kommt nichts an.
- Direkte Internetverbindung zwischen Geräten in verschiedenen Netzen (NAT-Durchdringung/Hole Punching). Kann später ergänzt werden; v1 reicht in diesem Fall über den Server durch.
- iOS, macOS, Linux-Clients (außer der CLI), Web-Oberfläche.
- Mehrere Benutzer bzw. Teilen mit anderen Personen.
- Ordner-Übertragung mit Struktur.
- Historie/Archiv vergangener Übertragungen.

## Anti-Ziele (wäre ein Fehlschlag, auch wenn es technisch funktioniert)

- Der Server wird zum Speicher oder läuft unnötig als Datenpfad, obwohl beide Geräte im selben Netz sind.
- Etwas landet ohne Bestätigung auf einem Gerät.
- Wer die URL kennt, kann ein Gerät registrieren, Angebote sehen oder Dateien abgreifen.
- Ein Gerät im lokalen Netz kann ohne Berechtigung Dateien vom lokalen Endpunkt eines Senders laden.
- Die Oberfläche wächst zu einer vollwertigen App mit Menüs, Historie, Ordnerverwaltung usw.
- Ein abgebrochener Multi-GB-Transfer muss von vorn beginnen.

## Rahmenbedingungen

- Server läuft auf privatem Ubuntu-Heimserver, öffentlich erreichbar über einen **playit-HTTPS-Tunnel (Pro-Abo)** mit von playit vergebener URL; **Caddy** davor terminiert TLS für diese URL. (Beim Einrichten prüfen, dass Caddy für die playit-URL ein Zertifikat beziehen kann; andernfalls TLS-Variante anpassen – berührt das Design nicht.)
- Android-Push über **Firebase Cloud Messaging**. Der Nutzer legt das Firebase-Projekt bei Bedarf selbst an (`google-services.json` für die App, Service-Account-JSON für den Server).
- Agenten laufen typischerweise auf dem Windows-PC (z. B. Claude Code) und haben die zu sendenden Dateien lokal.
- Verteilung: Android-App als selbst signierte APK (Sideload, kein Play Store). Windows-Programm als selbst signiertes MSIX mit Installationsskript, das das Zertifikat einmalig vertrauenswürdig macht (MSIX nötig für Teilen-Menü, Autostart und Firewall-Regel). *(Entscheidung des Designers.)*

## Lösungswege (betrachtet)

### Technik
1. **Server + CLI/MCP in Go, Android nativ (Kotlin), Windows nativ (C#/.NET)** – *gewählt.* Plattform-Integration (FCM, Teilen-Ziel, Benachrichtigungen mit Aktionen, Hintergrunddienste, APK-Installation, Infobereich, „Senden an“) mit Bordmitteln am zuverlässigsten; die UI ist bewusst minimal, doppelt schreiben kostet wenig.
2. Flutter für Android + Windows – UI nur einmal, aber die System-Integration braucht trotzdem Plattformcode; unter Windows holpriger.
3. Kotlin Multiplatform – schweres Windows-Programm (JVM), schwächste Windows-Integration.

### Datenweg
Frühere Variante „Server lagert zwischen“ wurde zugunsten von P2P verworfen (Nutzerwunsch: Server nur als Vermittler).

1. **Lokal direkt, sonst live durch den Server durchgereicht** – *gewählt.* Einfach, zuverlässig, deckt den Hauptfall (lokal, schnell) voll ab; Durchreichen ohne Speichern nur, wenn es nicht anders geht.
2. WebRTC für alles (lokal, Hole Punching, TURN-Relay) – unterwegs öfter direkt, aber in drei Sprachen deutlich aufwendiger, braucht zusätzlichen UDP-Tunnel für TURN, bei großen Dateien oft langsamer.
3. Wie 1 plus eigener Hole-Punching-Versuch – am meisten Eigenbau, fehleranfällig. Kann später auf 1 aufgesetzt werden.

### Push
Android wird per Firebase geweckt (Nutzerentscheidung), statt Dauerverbindung oder selbst gehostetem Push.

### MCP
Läuft **lokal neben dem Agenten** (stdio), nicht auf dem Server: Der Agent hat die Dateien lokal, und der Sender muss während der Angebotsphase selbst die Daten bereitstellen.

## Gesamtaufbau

```
             Steuerung (HTTPS/WebSocket)                      ┌─> FCM (Google) ─> Android weckt auf
 Android ─┐                                                   │
 Windows ─┼── HTTPS ──> playit ──> Caddy ──> share-server (Go)┤  SQLite: Geräte, Kopplungscodes,
 CLI/MCP ─┘                                                   │  laufende Angebote (Metadaten)
                                                              └─ Relay: Datenstrom live durchreichen

 Daten, wenn im selben Netz:   Empfänger ──HTTPS (Zertifikat gepinnt)──> lokaler Endpunkt des Senders
 Daten, sonst:                 Empfänger <── share-server (Relay, kein Speichern) <── Sender
```

Monorepo:
- `server/` – Go-Modul mit zwei Binaries: `share-server` (Dienst + Admin-Befehle) und `share` (CLI + `share mcp` als stdio-MCP-Server). Gemeinsame Go-Bibliothek für Protokoll und Client-Logik.
- `android/` – Kotlin, Jetpack Compose.
- `windows/` – C#/.NET 8, WPF, MSIX-Paket.
- `docs/` – Design, Protokoll, Betrieb (systemd-Unit, Caddyfile-Beispiel, playit-Hinweise).

## Kernbegriffe

- **Gerät**: Typ `android` | `windows` | `agent`. Hat ID, Name, Geräte-Token, eigenes selbst signiertes TLS-Zertifikat (Fingerabdruck beim Server hinterlegt), optional FCM-Token, aktuelle lokale Adressen, online/zuletzt gesehen.
  - Agent-Geräte sind *nur Sender* und erscheinen nicht als Ziel. *(Entscheidung des Designers.)*
- **Angebot (Transfer)**: Art `text` | `link` | `files`; bei Dateien eine Liste (Name, Größe, MIME-Typ); optionale Nachricht; Modus `gezielt` (Geräteliste) oder `an alle`; Angebotsphase mit Endzeit.
- **Zustellung**: pro Angebot und Empfänger: `wartet` → `angenommen` → `lädt (lokal | über Server)` → `zugestellt`; oder `abgelehnt`, `verpasst` (Angebotsphase vorbei), `abgebrochen` (Sender hat zurückgezogen), `fehlgeschlagen`.

## Abläufe

### Kopplung (Registrierung)
- Ein neues Gerät braucht einmalig einen **Kopplungscode** (kurz, einmalig, 10 Min gültig), erzeugt per:
  - `share-server pair` auf dem Server (Code + QR im Terminal), oder
  - „Gerät hinzufügen“ in einer bereits gekoppelten App (Code + QR).
- Das neue Gerät erzeugt sein TLS-Schlüsselpaar und sendet Server-URL, Code, Gerätename, Typ und Zertifikats-Fingerabdruck. Der Server gibt ein dauerhaftes Geräte-Token zurück. Token und Schlüssel werden sicher gespeichert (Android Keystore, Windows DPAPI, CLI im Benutzerprofil).
- Ohne gültiges Token ist außer der Kopplung nichts erreichbar.
- Geräte entfernen: `share-server devices rm <name>`; ein Gerät kann sich in seinen Einstellungen selbst abmelden.

### Senden (Angebot)
1. Sender meldet das Angebot beim Server an: Metadaten, Ziele/„an alle“, seine **lokalen Adressen** (IP:Port aller aktiven Netzwerkschnittstellen, ohne Loopback/VPN-Adressen soweit erkennbar).
2. Text und Links (bis 64 KB) reisen **im Angebot selbst** über den Server (nur im Arbeitsspeicher/der Datenbank bis Phasenende). Kein Datenweg nötig. *(Entscheidung des Designers.)*
3. Bei Dateien startet der Sender seinen **lokalen Endpunkt** (HTTPS mit eigenem Zertifikat) und hält ihn für die Angebotsphase offen.
4. **Angebotsphase**: Standard und Maximum **10 Minuten**, endet früher, wenn der Nutzer abbricht oder (bei gezielt) alle Ziele einen Endstatus haben. Bei „an alle“ läuft sie die volle Zeit (bzw. bis Abbruch); Geräte, die während der Phase online kommen, sehen das Angebot ebenfalls.
5. Downloads, die innerhalb der Phase begonnen haben, dürfen danach zu Ende laufen. Ein Fortsetzen nach Abbruch ist möglich, solange der Sender noch erreichbar ist und die Datei unverändert ist. *(Entscheidung des Designers.)*
6. Der Sender sieht den Status pro Empfänger (inkl. ob lokal oder über Server) und kann jederzeit abbrechen.
7. **Der Sender muss während der Phase online bleiben**: Android hält dafür einen Vordergrunddienst mit Benachrichtigung („Biete an: app.apk – noch 8 Min“, Knopf „Abbrechen“); Windows läuft ohnehin im Infobereich; CLI/MCP siehe unten.

### Empfangen
1. Benachrichtigung: Absender, Art, Name(n), Gesamtgröße, Anzahl, ggf. Nachricht, bei Text/Link eine Vorschau (~100 Zeichen), Restzeit. Knöpfe **Annehmen** / **Ablehnen**.
2. Annehmen gilt für das ganze Angebot (alle Dateien oder keine). *(Entscheidung des Designers.)*
3. Wegwahl je Datei-Angebot:
   - Empfänger holt vom Server die lokalen Adressen und den Zertifikats-Fingerabdruck des Senders plus ein **angebotsbezogenes Zugangstoken**.
   - Er probiert alle Adressen parallel mit kurzer Zeitgrenze (z. B. 1,5 s); TLS mit gepinntem Fingerabdruck, Token im Request. Erste erfolgreiche Verbindung gewinnt → **lokal**.
   - Gelingt keine → **über Server** (Relay).
4. Ziel beim Annehmen:
   - Dateien → Download-Ordner (Android: MediaStore Downloads; Windows: `%USERPROFILE%\Downloads`). Namenskonflikte → Suffix ` (1)`.
   - Text → Zwischenablage, Hinweis „kopiert“.
   - Link → Zwischenablage, Hinweis bietet „Öffnen“.
   - APK (Android) → nach Download Installationsdialog anbieten.
5. Downloads fortsetzbar (HTTP-Range, lokal wie über Relay). Wechsel des Wegs beim Fortsetzen ist erlaubt (z. B. Netz gewechselt). Vollständigkeit über Größe; Transportintegrität über TLS. *(Entscheidung des Designers: keine Vorab-Prüfsumme, damit das Angebot bei Multi-GB-Dateien sofort startet.)*
6. Verworfene Benachrichtigungen sind kein Problem: offene Angebote sind in der App jederzeit sichtbar.

### Relay (Durchreichen über den Server)
- Empfänger fordert beim Server `GET /v1/transfers/{id}/files/{n}` mit optionalem `Range` an.
- Server schickt dem Sender über dessen Steuerverbindung (WebSocket) eine Relay-Anforderung (Relay-ID, Datei, Bereich).
- Sender öffnet `POST /v1/relay/{relay-id}` und streamt den Bereich; der Server reicht die Bytes direkt in die Antwort an den Empfänger weiter. **Nichts wird auf die Platte geschrieben**; nur kleine Puffer im Arbeitsspeicher, Gegendruck über die Verbindungen.
- Ist der Sender nicht erreichbar, schlägt der Download fehl (mit Wiederholung, solange die Phase läuft).
- Hinweis: Relay-Daten laufen zweimal über die Heimleitung (rein und raus) und durch den Tunnel.

### Echtzeit-Steuerung
- Alle Clients halten **während sie aktiv sind** (offenes Angebot, laufender Transfer, App im Vordergrund, Windows-Programm läuft, MCP läuft) eine WebSocket-Verbindung `/v1/events`; Wiederverbindung automatisch; beim Verbinden werden offene Angebote nachgeladen. Dabei melden sie auch ihre aktuellen lokalen Adressen.
- **Android im Hintergrund** wird per FCM-Datennachricht mit hoher Priorität geweckt („neues Angebot“, „Angebot zurückgezogen“), holt Details per API und zeigt bzw. entfernt die Benachrichtigung. Ohne konfiguriertes Firebase sieht Android Angebote nur bei geöffneter App.

## Server (`share-server`, Go)

- Eine Binary als systemd-Dienst hinter Caddy (keine Body-Größenbegrenzung, Timeouts großzügig bzw. aus für Relay- und WebSocket-Pfade; Pufferung in Caddy für Relay-Pfade aus).
- Persistenz: SQLite (Geräte, Kopplungscodes, Angebote und Zustellstatus – Angebots-Metadaten werden nach Phasenende plus kurzer Frist, z. B. 1 h, gelöscht). Keine Dateiablage.
- Konfiguration per Datei: Listen-Adresse, Datenbankpfad, maximale Angebotsdauer (Standard 10 Min), Pfad zum Firebase-Service-Account (optional).
- REST/JSON-API unter `/v1` mit Bearer-Token, grob:
  - `POST /v1/pair`, `POST /v1/pair/codes`
  - `GET /v1/devices`, `PATCH /v1/devices/me` (Name, FCM-Token, lokale Adressen), `DELETE /v1/devices/me`
  - `POST /v1/transfers` (anlegen), `GET /v1/transfers?role=incoming|outgoing`, `GET /v1/transfers/{id}`, `DELETE /v1/transfers/{id}` (abbrechen)
  - `POST /v1/transfers/{id}/accept` (liefert Adressen, Fingerabdruck, Zugangstoken), `/decline`, `/progress`, `/delivered`
  - `GET /v1/transfers/{id}/files/{n}` (Relay-Download, Range), `POST /v1/relay/{relay-id}` (Relay-Upload des Senders)
  - `GET /v1/events` (WebSocket)
- Zugangstoken für lokale Endpunkte: vom Server pro (Angebot, Empfänger) ausgestellt, für den Sender prüfbar (z. B. signiert mit einem Schlüssel, den der Sender beim Anlegen erhält, oder der Sender bekommt die gültigen Tokens über die Steuerverbindung). Genaue Form dem Bauenden überlassen, Anforderung: nur angenommene Empfänger dieses Angebots kommen an die Daten.
- Admin-Befehle: `share-server pair`, `share-server devices [ls|rm]`.

## Lokaler Endpunkt (in allen Clients)

- HTTPS-Server mit dem gerätefeste Zertifikat, Port konfigurierbar (Standard fest, z. B. 47800, Ausweichen auf freien Port erlaubt – gemeldet wird der tatsächliche).
- Nur aktiv, solange ein Datei-Angebot läuft oder ausgeliefert wird.
- Liefert ausschließlich Dateien laufender Angebote, nur mit gültigem Zugangstoken, mit Range-Unterstützung.
- Windows: Firewall-Regel über das MSIX-Paket (für private Netzwerke). Android: läuft im Vordergrunddienst des Angebots.

## CLI und MCP (`share`)

- Eine Go-Binary für Windows (nebenbei Linux/macOS).
- `share pair <server-url> <code>` – einmalig koppeln (Typ `agent`), speichert Konfiguration im Benutzerprofil.
- `share send <dateien…> --to <gerät>[,<gerät>] | --all [--minutes N]`, `share text "…"`, `share link <url>`, `share devices`. `share send` läuft im Vordergrund, zeigt Status pro Empfänger und endet, wenn die Phase vorbei und alle Downloads fertig sind (Strg+C bricht ab).
- `share mcp` – MCP-Server über stdio. Angebote laufen im Hintergrund des MCP-Prozesses (der während der Agenten-Sitzung ohnehin läuft). Werkzeuge:
  - `list_devices` – Ziele mit Name, Typ, online/zuletzt gesehen
  - `send_files(paths[], targets[] | all, message?, minutes?)` – kehrt sofort mit Angebots-ID zurück
  - `send_text(text, targets[] | all)`, `send_link(url, targets[] | all)`
  - `get_transfer_status(transfer_id, wait_seconds?)` – Status pro Empfänger, optional kurz auf Änderung warten
  - `cancel_transfer(transfer_id)`
- Endet der MCP-Prozess, enden seine Angebote (Empfänger sehen „abgebrochen“).
- Gerätenamen werden tolerant aufgelöst (Groß-/Kleinschreibung, eindeutiges Präfix).

## Android-App (Kotlin, Compose)

- **Ein Bildschirm**:
  - Geräteliste (Auswahl als Ziel, online-Anzeige);
  - **Offene Anfragen** (eingehend, mit Details: Absender, Dateien mit Namen/Größen, Restzeit; Annehmen/Ablehnen; Antippen → kleine Detailansicht);
  - **Laufend** (ein- und ausgehend, Fortschritt, Weg lokal/Server, Status pro Empfänger, Abbrechen).
- **Senden aus der App**: Datei(en) wählen, Text/Link einfügen, Ziel(e) oder „an alle“.
- **Teilen-Ziel**: `ACTION_SEND` / `ACTION_SEND_MULTIPLE` für Dateien (`*/*`) und Text/Links (`text/plain`) → kleines Auswahlblatt für Ziel(e) → senden.
- **Einstellungen**: Server-URL, Gerätename, Kopplung (QR scannen/Code eingeben), „Gerät hinzufügen“ (Code/QR anzeigen), abmelden.
- Senden und Empfangen laufen in einem Vordergrunddienst mit Fortschritts-Benachrichtigung, auch bei geschlossener App.
- Achtung für den Bauenden: Über das Teilen-Menü erhaltene `content://`-URIs müssen für die ganze Angebotsphase lesbar bleiben (Berechtigung an den Dienst weitergeben bzw. dauerhaft übernehmen, wo möglich); Kopieren großer Dateien in den App-Speicher nur als Notlösung.
- APK-Installation über den System-Installationsdialog (`REQUEST_INSTALL_PACKAGES`).

## Windows-Programm (C#/.NET 8, WPF, MSIX)

- Läuft im **Infobereich**, startet mit Windows (MSIX-Startup-Task), nur eine Instanz.
- **Ein Fenster** (Klick aufs Symbol) mit denselben Bereichen wie Android; Senden per Dateiauswahl, Hineinziehen oder Einfügen von Text/Link.
- **Windows-Benachrichtigungen** mit Annehmen/Ablehnen.
- Senden über: Windows-**Teilen-Menü** (Share-Target im MSIX), **„Senden an“** (Verknüpfung im SendTo-Ordner, übergibt an die laufende Instanz), **Ziehen auf das Fenster**. Danach kleine Zielauswahl.
- Einstellungen wie Android; Token und Schlüssel per DPAPI geschützt.

## Was dem Bauenden überlassen bleibt

- Konkrete Bibliotheken innerhalb der gewählten Sprachen (WebSocket, HTTPS-Server auf Android, Toast-/Tray-Bibliotheken usw.).
- Datenbankschema, JSON-Formate, Fehlercodes, genaue Form der Zugangstokens (unter der genannten Anforderung).
- Genaues Layout der Oberflächen im Rahmen „ein Fenster, minimal“.
- Zeitgrenzen, Wiederholungsstrategie, Puffergrößen, Standard-Port.
- Mindestversionen (Vorschlag: Android 8 / API 26; Windows 10 21H2+).
