# Windows-Programm

Läuft ab Windows 10 2004 (Build 19041), 64 Bit. .NET ist im Paket enthalten.

## Installieren

`windows/dist` enthält nach dem Bauen alles Nötige:

1. `Install.ps1` mit Rechtsklick → „Mit PowerShell ausführen“ (oder `powershell -ExecutionPolicy Bypass -File Install.ps1`).
2. Beim ersten Mal fragt Windows nach Administratorrechten, um dem Signaturzertifikat zu vertrauen (es kommt nach „Vertrauenswürdige Personen“ des Computers, gilt also nur für Pakete, die mit diesem Zertifikat signiert sind).
3. Share startet. „Mit Server koppeln“: Adresse und Code eintragen, oder den Text `share://pair?…` in eines der Felder einfügen.

Das Programm startet mit Windows in den Infobereich (abschaltbar in den Einstellungen) und trägt eine Firewall-Regel für **private** Netzwerke ein. Ist dein Heimnetz in Windows als „Öffentlich“ eingestellt, laufen Sendungen vom PC über den Server.

Aktualisieren: neues Paket mit höherer Version bauen und `Install.ps1` noch einmal ausführen; die Kopplung bleibt. Entfernen: `Uninstall.ps1` (mit `-RemoveCertificate` auch das Vertrauen). Die Kopplung liegt im Paketordner und wird dabei gelöscht.

## Bauen

Nötig: .NET-SDK 10, PowerShell 7, für die Tests Go (und für den Kotlin-Vergleich JDK 17 in `JAVA_HOME_17`). Kein Visual Studio, kein Windows-SDK.

```powershell
cd windows
pwsh packaging/build.ps1 -Version 1.0.1.0     # bei jedem Update erhöhen
```

Das Signaturzertifikat (`CN=Share (privat)`) wird beim ersten Mal in „Eigene Zertifikate“ erzeugt. **Sichern**, sonst lassen sich keine Updates mehr bauen, die Windows als dasselbe Programm erkennt:

```powershell
$c = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=Share (privat)'
Export-PfxCertificate $c -FilePath share-signatur.pfx -Password (Read-Host -AsSecureString)
```

## Tests

```powershell
dotnet test tests/Share.Core.Tests    # Kern gegen Go-Server, Go-CLI und Kotlin-Kern
dotnet test tests/Share.App.Tests     # Programm ohne Fenster
pwsh packaging/build.ps1 -Loose; dotnet test tests/Share.Ui.Tests   # bedient das Programm auf dem Desktop
```

`Share.exe --data-dir <ordner> --allow-local-http` startet eine getrennte Testinstanz (eigene Kopplung, Downloads in diesem Ordner, keine Windows-Benachrichtigungen, `http` nur zu localhost).

## Verhalten

- Dateien landen im Download-Ordner. „Öffnen“ startet nie Programme oder Skripte, sondern zeigt sie im Ordner.
- Angebotene Dateien bleiben für andere Programme benutzbar. Wird eine Datei ersetzt, wird weiter die alte Fassung gesendet; wird sie verändert, bricht die Sendung ab.
- Solange übertragen wird, geht der PC nicht von selbst in den Ruhezustand.
- Protokoll zur Fehlersuche: `%LOCALAPPDATA%\Packages\SuitIThub.Share_…\LocalCache\Local\Share\share.log` (ohne Inhalte und Schlüssel).

## Prüfliste nach der Installation

- [ ] Teilen-Menü: Datei aus dem Explorer, Bild aus Fotos, Link aus Edge → „Share“
- [ ] Explorer: Rechtsklick → „Senden an“ → „Share“ (mehrere Dateien)
- [ ] Benachrichtigung mit „Annehmen“ bei verstecktem Fenster, auch bei beendetem Programm
- [ ] PC neu starten: Share ist im Infobereich und verbunden
- [ ] Handy lädt vom PC **lokal** (Firewall), PC lädt vom Handy lokal
- [ ] 5-GB-Datei in beide Richtungen
- [ ] Ruhezustand während eines Downloads, danach läuft er weiter
- [ ] Update: Paket mit höherer Version darüber installieren, Kopplung bleibt
