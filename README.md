# share

Privates „Quick Share übers Internet“: Text, Links und Dateien zwischen eigenen Geräten senden – lokal direkt, sonst live durch den eigenen Server (ohne Speichern). Jeder Empfang muss bestätigt werden.

- Design: `docs/superpowers/specs/2026-11-23-share-design.md`
- Protokoll (für Apps): `docs/protocol.md`
- Server betreiben: `docs/deploy/README.md`
- Android-App: `docs/android.md`
- Windows-Programm: `docs/windows.md`

## Bauen

```sh
cd server
go test ./...
GOOS=linux CGO_ENABLED=0 go build -o bin/share-server ./cmd/share-server   # Heimserver
GOOS=windows CGO_ENABLED=0 go build -o bin/share.exe ./cmd/share           # CLI + MCP
```

## CLI

```sh
share pair https://<server> <CODE>            # einmalig (Code: share-server pair)
share devices
share send app-release.apk --to pixel         # wartet auf Bestätigung und Download
share send video.mp4 --all --minutes 5
share text "Hallo" --to desktop
share link https://example.org --all
```

Beim ersten `share send` fragt die Windows-Firewall nach – Zugriff im **privaten** Netzwerk erlauben, sonst geht alles über den Server.

## MCP für Agenten (z. B. Claude Code)

```sh
claude mcp add share -- C:\Pfad\zu\share.exe mcp
```

Werkzeuge: `list_devices`, `send_files`, `send_text`, `send_link`, `get_transfer_status`, `cancel_transfer`.
Angebote laufen höchstens 10 Minuten und nur, solange die Agenten-Sitzung läuft.
