# share-server betreiben (Ubuntu, Caddy, playit)

## 1. Bauen (auf dem Entwicklungsrechner)

```sh
cd server
GOOS=linux GOARCH=amd64 CGO_ENABLED=0 go build -o bin/share-server ./cmd/share-server
```

`bin/share-server` nach `/usr/local/bin/share-server` auf den Heimserver kopieren.

## 2. Installieren

```sh
sudo useradd --system --home /var/lib/share-server --shell /usr/sbin/nologin share
sudo mkdir -p /etc/share-server
sudo tee /etc/share-server/config.toml >/dev/null <<'CFG'
listen = "127.0.0.1:8080"
db_path = "/var/lib/share-server/share.db"
public_url = "https://<deine-playit-adresse>"
max_offer_minutes = 10
# firebase_credentials = "/etc/share-server/firebase.json"
CFG
sudo cp share-server.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now share-server
```

Der Server speichert nur Geräte und Angebots-Metadaten (SQLite). Dateien liegen nie auf dem Server.

## 3. Caddy

`Caddyfile` aus diesem Ordner nach `/etc/caddy/Caddyfile` übernehmen, Domain anpassen, `sudo systemctl reload caddy`.
Wichtig: `flush_interval -1` und keine Timeouts – sonst brechen große Relay-Übertragungen und WebSockets ab.

## 4. playit

HTTPS-Tunnel so einrichten, dass er auf Caddy (Port 443 des Heimservers) zeigt.

**Prüfpunkt:** Caddy muss für die playit-Adresse ein Zertifikat bekommen. Nach dem Start:

```sh
journalctl -u caddy | grep -i certificate
curl -sI https://<deine-playit-adresse>/v1/devices   # erwartet: HTTP/2 401
```

Wenn playit TLS selbst terminiert und Caddy kein Zertifikat beziehen kann: in der Caddyfile die Adresse als `http://<adresse>` eintragen und den playit-Tunnel auf Caddys Port 80 zeigen lassen (TLS endet dann bei playit).

## 5. Firebase (optional, für Android-Benachrichtigungen)

1. Firebase-Projekt anlegen, Android-App mit dem Paketnamen der App hinzufügen, `google-services.json` für den App-Build sichern.
2. Projekteinstellungen → Dienstkonten → neuen privaten Schlüssel erzeugen → als `/etc/share-server/firebase.json` ablegen (`chown share`, `chmod 600`).
3. `firebase_credentials` in der Konfiguration eintragen, Dienst neu starten. Log zeigt „firebase push enabled“.

## 6. Geräte koppeln

```sh
sudo -u share share-server -config /etc/share-server/config.toml pair
```

zeigt QR-Code und Code (10 Minuten gültig). Geräte verwalten:

```sh
sudo -u share share-server -config /etc/share-server/config.toml devices ls
sudo -u share share-server -config /etc/share-server/config.toml devices rm "Altes Handy"
```
