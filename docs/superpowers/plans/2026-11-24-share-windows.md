# Share – Plan 3: Windows-Programm – Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Windows-Programm im Infobereich, das Text, Links und Dateien sendet und (nach Bestätigung) empfängt, im Teilen-Menü und unter „Senden an“ erscheint und als signiertes MSIX installiert wird.

**Architecture:** `Share.Core` (reines .NET) ist die Portierung von `android/core`: gleiche Klassen, gleiches Verhalten, gleiche Tests gegen den echten Go-Server. `Share.App` (WPF) enthält nur Windows: Infobereich, Fenster, Benachrichtigungen, DPAPI, Teilen-Ziel. Alle Startwege (Autostart, „Senden an“, Teilen-Menü, Benachrichtigung) landen über eine Named Pipe in **einer** laufenden Instanz.

**Tech Stack:** .NET **10** (LTS bis 2028-11; SDK 10.0.404), WPF mit `ThemeMode="System"`, WinForms-`NotifyIcon`, `Microsoft.Toolkit.Uwp.Notifications` 7.1.3, `QRCoder` 1.9.0, `System.Security.Cryptography.ProtectedData` 10.0.15; Paketwerkzeuge aus `Microsoft.Windows.SDK.BuildTools` 10.0.28000.2957 (makeappx, signtool, makepri); Tests: xunit 2.9.3, Microsoft.NET.Test.Sdk 18.12.0, FlaUI.UIA3 5.0.0.

**Spec:** `docs/superpowers/specs/2026-11-23-share-design.md` – Vertrag: `docs/protocol.md` – Vorlage: `android/core`

## Global Constraints

- **.NET 10 statt 8** *(Abweichung vom Design: .NET 8 ist seit 2026-11-10 ohne Support)*. App: `net10.0-windows10.0.19041.0`, x64, self-contained (nicht getrimmt). Core: `net10.0`, ohne Windows-Abhängigkeit. `Nullable` und `TreatWarningsAsErrors` an.
- Ordner `windows/`, Namensräume `Share.Core`, `Share.App`; Programm `Share.exe`; Paketname `SuitIThub.Share`, Publisher `CN=Share (privat)`. Alle Texte deutsch.
- Nichts landet ohne Bestätigung auf dem Gerät. Annehmen gilt für das ganze Angebot. Angebote höchstens **10 Minuten**. Text/Link höchstens **65536 Bytes** UTF-8.
- Lokaler Endpunkt: Port **47800**, sonst frei; nur während eigener Datei-Angebote. LAN-Probe parallel, **1,5 s**, gepinnter Fingerabdruck, sonst Relay.
- Download-Ordner des Benutzers (`FOLDERID_Downloads`), Namenskonflikt → ` (1)`. Zielname exklusiv anlegen, dann Teildatei darüber verschieben.
- Token, Schlüssel und Zertifikat nur DPAPI-geschützt (CurrentUser) auf der Platte; nie in Logs.
- Server-URL nur `https://`; `http://` nur mit `--allow-local-http` und nur für `localhost`/`127.0.0.1` (Tests).
- Eine Instanz je Benutzer; die Pipe ist nur für denselben Benutzer erreichbar. Befehle über die Pipe können nur Fenster öffnen und Dateien **zur Auswahl vorlegen**, nie ohne Klick senden oder annehmen.
- Aus der Android-Abschlussprüfung übernommen: exakte Längenprüfung jeder Antwort, `Append(offset)`, Pausen ≤ 5 s, Ergebnis eigener Angebote und angenommene Texte bleiben sichtbar bis „Schließen“, Puffern früher Ereignisse, Aufräumen verwaister Angebote bei jedem Verbinden.

## Review Focus

1. **Datei wird von einem anderen Programm geöffnet, gelöscht oder überschrieben, während sie angeboten ist** (Build schreibt APK neu): Angebot liefert nie vermischte Bytes, meldet „geändert“; das andere Programm wird nicht blockiert. → Task 2, 6.
2. **Rechner schläft ein / Netz wechselt während einer Übertragung**: Energiesparen wird während Übertragungen verhindert; nach dem Aufwachen läuft der Download weiter. → Task 5, 14.
3. **Programm wird während eines Downloads beendet oder Windows neu gestartet**: beim nächsten Start geht es ab dem Teilstand weiter, fertige Dateien nicht erneut. → Task 8, 9.
4. **Senden über „Senden an“/Teilen-Menü, wenn das Programm nicht läuft, nicht gekoppelt ist, mit Ordnern, vielen oder temporären Dateien**: verständliche Meldung bzw. Auswahlfenster, nichts geht verloren. → Task 13, 15.
5. **Benachrichtigung anklicken nach Ablauf, doppelt, oder wenn Windows-Benachrichtigungen aus sind**: kein Absturz, „nicht mehr verfügbar“, Anfrage steht im Fenster und am Symbol. → Task 14.

---

## Dateistruktur

```
windows/
  Share.slnx, Directory.Build.props, global.json, nuget.config, .gitignore
  src/Share.Core/   Protocol.cs Names.cs LanAddresses.cs PairUri.cs Identity.cs Api.cs Events.cs
                    Files.cs DiskFile.cs DiskDownloads.cs LocalEndpoint.cs Sender.cs Receiver.cs Engine.cs
  src/Share.App/    Program.cs App.xaml(.cs) AppHost.cs AccountStore.cs Ipc.cs StartArgs.cs Tray.cs
                    Notifier.cs ShareTarget.cs Windows.cs(P/Invoke) Format.cs
                    MainWindow.xaml(.cs) MainModel.cs SendWindow.xaml(.cs) SendModel.cs
                    PairWindow.xaml(.cs) SettingsWindow.xaml(.cs)
  tests/Share.Core.Tests/  tests/Share.App.Tests/  tests/Share.Ui.Tests/ (FlaUI)
  packaging/        AppxManifest.xml assets/ build.ps1 Install.ps1 Uninstall.ps1
docs/windows.md
```

**Befehle** (in `windows/`): `dotnet test tests/Share.Core.Tests`, `dotnet test tests/Share.App.Tests`; `dotnet` ist das portable SDK 10 aus Task 1. Go muss im PATH sein (`GO_BIN` sonst); fehlt es, schlägt der Testlauf fehl.

**Portierung heißt:** jede `@Test`-Funktion der genannten Kotlin-Datei erhält eine gleichnamige xunit-Methode (PascalCase) mit denselben Werten und Erwartungen; jede öffentliche Kotlin-Signatur ihr C#-Gegenstück (`suspend fun x(): T` → `Task<T> XAsync(CancellationToken)`, `Flow` → `IAsyncEnumerable`, `StateFlow` → unveränderlicher Zustand mit `event Action? Changed`, `Instant` → `DateTimeOffset`). Abweichungen stehen beim Task.

---

### Task 1: SDK, Projektgerüst, Protokolltypen

**Files:** Create: Gerüst, `Protocol.cs`; Test: `ProtocolTests.cs` (Portierung `ProtocolTest.kt`)

**Interfaces – Produces:** Records wie in `Protocol.kt`; `static class ShareJson { JsonSerializerOptions Options }` (snake_case, Nullfelder weglassen, unbekannte Felder ignorieren, `null`-Listen → leer, ungültige Zeit → `JsonException`, unbekannter `path` → `null`); `AccessToken.Create/Verify`.

- [ ] **Step 1:** SDK 10.0.404 portabel nach `%LOCALAPPDATA%\Programs\dotnet10` (offizielles `dotnet-install.ps1`, Prüfsumme/Signatur prüfen; kein Admin, PATH bleibt unverändert). `dotnet --version` → `10.0.404`. `.gitattributes`/`.gitignore` ergänzen.
- [ ] **Step 2: Failing tests** – Portierung, zusätzlich `WritesExactCreateJson`: `{"kind":"text","text":"hi","targets":["x"],"broadcast":false}`. **Step 3: RED** – `CS0246`. **Step 4: Implement**, **Step 5: GREEN**, **Step 6: Commit** `feat(windows): scaffold and protocol types`

### Task 2: Namen, LAN-Adressen, Kopplungs-URI, Dateiquelle

**Files:** Create: `Names.cs`, `LanAddresses.cs`, `PairUri.cs`, `Files.cs`, `DiskFile.cs`; Test: `NamesTests.cs`, `LanAddressesTests.cs`, `PairUriTests.cs`, `DiskFileTests.cs`

**Interfaces – Produces:** wie Kotlin; dazu `sealed class DiskFile : IFileSource, IDisposable { static DiskFile Open(string path, bool deleteOnRelease = false); }` – hält ein `SafeFileHandle` (`FileShare.ReadWrite | FileShare.Delete`) für die ganze Angebotsdauer, liest mit `RandomAccess.Read`; `SourceException` mit deutscher Meldung.

- [ ] **Step 1: Failing tests** – Portierung dreier Kotlin-Dateien (LAN zusätzlich: Adaptername „vEthernet (WSL)“, „Hyper-V“, „Bluetooth“ ausgeschlossen) und

```csharp
[Fact] void ReadsFromAnyOffsetInParallel()
[Fact] void OtherProgramsMayWriteRenameAndDelete()      // während offen: File.WriteAllBytes, File.Move, File.Delete werfen nicht
[Fact] void InPlaceChangeIsDetected()                   // Unchanged() == false nach Schreiben gleicher Länge
[Fact] void ReplacedFileKeepsServingTheOldContent()     // Move neuer Datei darüber → alter Inhalt, Unchanged() == true
[Fact] void DirectoryMissingAndLockedAreReadable()      // SourceException, Text enthält Dateiname; Ordner: „Ordner können nicht gesendet werden“
[Fact] void ReadAfterReleaseIsIOException(); [Fact] void DeleteOnReleaseRemovesTheCopy()
```
- [ ] **Step 2: RED**, **Step 3: Implement**, **Step 4: GREEN**, **Step 5: Commit** `feat(windows): names, lan addresses, pair uri, disk files`

### Task 3: Identität und Pinning

**Files:** Create: `Identity.cs`; Test: `IdentityTests.cs` (Portierung `IdentityTest.kt`)

**Interfaces – Produces:** `Identity.Generate(string)`, `Identity.Load(certPem, keyPem)`, `CertPem`, `KeyPem`, `Fingerprint`, `X509Certificate2 ServerCertificate` (SslStream-tauglich, d. h. nicht ephemer), `Pinning.CreateHandler(string fingerprint): SocketsHttpHandler`.

- [ ] **Step 1: Failing tests** – zusätzlich `LoadsKeysWrittenByKotlinAndGo` (fester PEM-Text: PKCS#8 und SEC1). **Step 2: RED**, **Step 3: Implement**, **Step 4: GREEN**, **Step 5: Commit** `feat(windows): device identity and pinning`

### Task 4: API-Client und Go-Server-Testgerüst

**Files:** Create: `Api.cs`; Test: `GoServer.cs`, `ApiTests.cs` (Portierung `ApiTest.kt`, MockWebServer → kleiner `TcpListener`-Skriptserver `ScriptedServer.cs`)

**Interfaces – Produces:** wie Kotlin; `Pairing.PairAsync(…, DeviceType.Windows)`, `NormalizeServerUrl`, `ApiException(int Status)`; Relay-Download prüft Länge, `Content-Range` und folgt keinen Weiterleitungen. `GoServer` baut `share-server`/`share` einmal je Testlauf nach `tests/bin/go`.

- [ ] **Step 1: Failing tests**, **Step 2: RED**, **Step 3: Implement** (`SocketsHttpHandler`, `PooledConnectionLifetime` 5 min, Aufrufe 30 s, Streams ohne Gesamtlimit, Upload mit fester `Content-Length`, 256-KB-Puffer, HTTP/2-Ping 20 s), **Step 4: GREEN**, **Step 5: Commit** `feat(windows): api client tested against go server`

### Task 5: Ereignisse

**Files:** Create: `Events.cs`; Test: `EventsTests.cs` (Portierung `EventsTest.kt`)

- [ ] **Step 1: Failing tests** – zusätzlich `SilentConnectionIsDetected`: Gegenstelle antwortet nicht mehr → `Disconnected` binnen Keepalive-Zeit (Test: 500 ms). **Step 2: RED**, **Step 3: Implement** (`ClientWebSocket`, `KeepAliveInterval` 15 s, `KeepAliveTimeout` 15 s, 401 über `HttpStatusCode`), **Step 4: GREEN**, **Step 5: Commit** `feat(windows): event stream`

### Task 6: Lokaler HTTPS-Endpunkt

**Files:** Create: `LocalEndpoint.cs`; Test: `LocalEndpointTests.cs` (Portierung `LocalEndpointTest.kt`)

- [ ] **Step 1: Failing tests** – zusätzlich `FileRewrittenDuringDownloadNeverMixesBytes` (Download läuft langsam, Datei wird gleich groß überschrieben → Verbindung bricht ab oder Ergebnis ist durchgehend alt; nie Mischung) und `CurlCompatible` (`curl.exe -k` lädt die Datei mit `-C -` fort). **Step 2: RED**, **Step 3: Implement** – `TcpListener` + `SslStream`, harte Frist je Verbindung, höchstens 32, vor jedem Puffer `Unchanged()`. **Step 4: GREEN**, **Step 5: Commit** `feat(windows): local https endpoint`

### Task 7: Sender

**Files:** Create: `Sender.cs`; Test: `SenderTests.cs` (Portierung `SenderTest.kt`)

- [ ] Steps 1–5 wie oben; Commit `feat(windows): sender`

### Task 8: Empfänger und Ablage

**Files:** Create: `Receiver.cs`, `DiskDownloads.cs`; Test: `ReceiverTests.cs` (Portierung `ReceiverTest.kt`), `DiskDownloadsTests.cs`

**Interfaces – Produces:** `sealed class DiskDownloads(string directory, string stateFile) : IDownloadStore`.

- [ ] **Step 1: Failing tests** – Portierung, dazu

```csharp
[Fact] void PartSurvivesNewInstance(); [Fact] void NameConflictGetsSuffix(); [Fact] void FortyDownloadsOfTheSameName()
[Fact] void TwoParallelFinishesNeverOverwrite(); [Fact] void HostileAndReservedNames()   // "CON.txt", "a:b", "..\\x", 300 Zeichen
[Fact] void ForgetRemovesOnlyThatOffer(); [Fact] void FinishedRefIsGoneWhenTheUserDeletedTheFile()
[Fact] void AppendCutsOffWhatIsBehindTheOffset(); [Fact] void AppendBehindTheEndIsBusy()
[Fact] void CorruptStateFileStartsEmpty(); [Fact] void MissingDirectoryIsCreated(); [Fact] void LockedTargetIsIOException()
```
- [ ] **Step 2: RED**, **Step 3: Implement** (Teildatei `<name>.<tid8>-<n>.part` im Zielordner; Zustand atomar als JSON), **Step 4: GREEN**, **Step 5: Commit** `feat(windows): receiver and downloads folder`

### Task 9: Engine

**Files:** Create: `Engine.cs`; Test: `EngineTests.cs` (Portierung `EngineTest.kt`)

- [ ] Steps wie oben; `Engine.StateChanged` kommt auf beliebigen Threads. Commit `feat(windows): engine`

### Task 10: Zusammenspiel

**Files:** Test: `InteropTests.cs` (Portierung `InteropTest.kt`), `KotlinInteropTests.cs`

- [ ] **Step 1:** Portierung. Dazu `KotlinCoreReceivesFromWindows` und `WindowsReceivesFromKotlinCore`: kleines Kotlin-Testprogramm `android/core/src/test/…/PeerMain.kt` (Argumente: URL, Code, Name, Modus `send <datei> <ziel>`/`receive <ordner>`), gestartet über Gradle-Task `:core:peer`. LAN und Relay, 20 MB, byte-identisch. **Step 2:** alles grün; Funde wie in Plan 2 erst als Test nachstellen. **Step 3: Commit** `test(windows): interop with go and kotlin`

### Task 11: Programmgerüst: Konto, eine Instanz, Infobereich

**Files:** Create: `Program.cs`, `App.xaml`, `AppHost.cs`, `AccountStore.cs`, `Ipc.cs`, `StartArgs.cs`, `Tray.cs`, `Windows.cs`; Test: `AccountStoreTests.cs`, `IpcTests.cs`, `StartArgsTests.cs`

**Interfaces – Produces:** `AccountStore(string file) { Account? Load(); void Save(Account); void Clear(); }`; `abstract record Command { Show; Send(paths, temporary); SendText(text); Accept(id); Decline(id) }`; `Ipc.TrySend(Command): bool` / `Ipc.Listen(Func<Command, Task>)`; `StartArgs.Parse(string[])` (`--background`, `--send <pfade…>`, `--data-dir`, `--allow-local-http`, nackte Pfade = senden); `AppHost` (Engine-Lebenszeit wie `ShareApp`, `Tell`, `Announce`).

- [ ] **Step 1: Failing tests** – `RoundTrip`, `NoPlaintextOnDisk`, `CorruptFileLoadsAsNull`; `SecondInstanceForwardsAndExits`, `ParallelStartsYieldOneInstance`, `GarbageOnThePipeIsIgnored`, `HugeMessageIsRefused` (> 1 MB), `HungFirstInstanceDoesNotBlockForever` (2 s); `Accept`/`Decline` **nicht** über die Pipe annehmbar (nur intern); Argumente inkl. Umlaute, Leerzeichen, 200 Pfade. **Step 2: RED**, **Step 3: Implement** – Symbolmenü: „Öffnen“, „Dateien senden…“, „Beenden“; Beenden mit laufenden Übertragungen fragt nach; offene Anfragen zeigen Punkt am Symbol. **Step 4: GREEN**, **Step 5: Commit** `feat(windows): app shell, single instance, tray`

### Task 12: Hauptfenster

**Files:** Create: `MainWindow.xaml`, `MainModel.cs`, `Format.cs`; Test: `FormatTests.cs` (Portierung `FormatTest.kt`), `MainModelTests.cs`

```
┌ Share ─────────────────── ⚙ ─ □ ✕ ┐
│ verbunden                          │
│ Offene Anfragen                    │
│  Datei von Pixel                   │
│  app.apk, 12,0 MB · noch 8 Min     │
│              [Ablehnen] [Annehmen] │
│ Laufend                            │
│  ↑ video.mp4  Pixel: lädt (lokal)… │
│ Senden an                          │
│  [x] Pixel ●   [ ] Tablet          │
│  [ ] Allen anbieten                │
│  [Dateien…] [Text/Link]            │
│  Dateien hierher ziehen / Strg+V   │
└────────────────────────────────────┘
```

**Interfaces – Produces:** `MainModel(AppHost host)` mit `ObservableCollection`s und Befehlen; kein Code im Fenster außer Ziehen, Einfügen, Dialoge.

- [ ] **Step 1: Failing tests** – dieselben Fälle wie `MainScreenTest.kt` (außer Rotation), auf Modellebene; dazu `ListsUpdateInPlace` (Auswahl und Fokus bleiben bei Fortschritt), `PasteTextImageAndFiles` (Dateiliste → Dateien, Text → Text/Link, Bild → PNG-Datei), `DropFolderShowsMessage`, `StateFromWorkerThreadsIsMarshalled`. **Step 2: RED**, **Step 3: Implement** – Schließen versteckt ins Symbol (einmaliger Hinweis). **Step 4: GREEN**, **Step 5: Commit** `feat(windows): main window`

### Task 13: Kopplung, Einstellungen, Sendefenster

**Files:** Create: `PairWindow.xaml`, `SettingsWindow.xaml`, `SendWindow.xaml`, `SendModel.cs`; Test: `PairModelTests.cs`, `SendModelTests.cs`

- [ ] **Step 1: Failing tests** – Fälle aus `PairScreenTest.kt` ohne Kamera (Einfügen eines `share://pair?…`-Textes füllt die Felder), `QrContentIsThePairUri`, `RenameReachesTheServer`, `UnpairWithoutServer`; Sendefenster: `OpensHandlesImmediately` (Quelle darf danach verschwinden), `NotPairedKeepsThePayload` (nach dem Koppeln geht es weiter), `FoldersAreReportedOthersOffered`, `WaitsForDevicesAfterColdStart`, `CancelReleasesEverything`, `SecondSendGetsItsOwnWindow`. **Step 2: RED**, **Step 3: Implement** – Einstellungen: Server, Gerätename, „Gerät hinzufügen“ (Code, QR, Ablaufzeit), „Mit Windows starten“, Benachrichtigungen aus → Hinweis, „Abmelden“. **Step 4: GREEN**, **Step 5: Commit** `feat(windows): pairing, settings, send window`

### Task 14: Benachrichtigungen, Zwischenablage, Energie

**Files:** Create: `Notifier.cs`; Modify: `AppHost.cs`; Test: `NotifierTests.cs`, `AppHostTests.cs`

**Interfaces – Produces:** `interface INotifier { Offer; RemoveOffer; Result; Text; Ended; Message }`, `ToastNotifier` (Tag = Angebots-ID, `ExpirationTime` = Ablauf), `ToastArgs.Parse/Build`.

- [ ] **Step 1: Failing tests** – `ToastXmlHasAcceptDecline`, `TextIsEscaped` (`<&"`), `ArgsRoundTrip`, `UnknownArgsIgnored`, `StaleActionsAreHarmless`, `OnlyWebLinksOpen`, `ExecutableResultsAreShownNotRun` („Öffnen“ bei ausführbaren Dateien zeigt die Datei im Explorer), `OffersUnseenAreAnnouncedWhenTheWindowHides`, `SleepIsPreventedOnlyWhileBusy`, `ResumeFromSleepReconnectsAtOnce`, `ClipboardBusyIsRetried`, `NotificationsOffStillShowTrayDot`. **Step 2: RED**, **Step 3: Implement** – Text/Link: Zwischenablage + Hinweis „kopiert“, Link zusätzlich „Öffnen“. **Step 4: GREEN**, **Step 5: Commit** `feat(windows): notifications, clipboard, power`

### Task 15: MSIX, Teilen-Ziel, Autostart, Firewall, „Senden an“

**Files:** Create: `packaging/*`, `ShareTarget.cs`; Test: `ManifestTests.cs`, `ShareTargetTests.cs`

- [ ] **Step 1: Failing tests** – Manifest: Teilen-Ziel (`SupportsAnyFileType`, `StorageItems`, `Text`, `WebLink`, `Bitmap`), Startaufgabe `--background`, Firewall-Regel (eingehend, TCP, `private`, `Share.exe`), COM-/Toast-Aktivierung mit gleicher CLSID wie im Code, Protokoll `share`, Ausführungsalias `share-senden.exe`, Publisher = Zertifikat. `ShareTarget`-Abbildung hinter `IShareData`: Dateien mit Pfad, Dateien ohne Pfad (Strom → temporäre Kopie, nach dem Angebot gelöscht), Text, Link, Bild, leer → Meldung, Fehler beim Lesen → `ReportError`. `SharedFilesAreOpenBeforeReportCompleted`.
- [ ] **Step 2: RED**, **Step 3: Implement** – Teilen-Prozess liest die Daten, übergibt sie an die laufende Instanz (startet sie sonst) und meldet erst nach deren Bestätigung `ReportCompleted`. `share://pair`-Links füllen nur das Kopplungsfenster.
- [ ] **Step 4: `build.ps1`** – veröffentlicht, erzeugt Symbole/`resources.pri`, packt, signiert (Zertifikat in `Cert:\CurrentUser\My`, wird beim ersten Mal erzeugt), legt `Share.msix`, `Share.cer`, `Install.ps1`, `Uninstall.ps1` nach `windows/dist`. **`Install.ps1`** – importiert das Zertifikat einmalig (erhöht) nach `LocalMachine\TrustedPeople`, installiert das Paket, legt die „Senden an“-Verknüpfung auf den Alias an. `Uninstall.ps1` macht alles rückgängig.
- [ ] **Step 5: Prüfung am Rechner** (nach Zustimmung des Nutzers) – Paket **lose registriert** (Entwicklermodus ist an) als `SuitIThub.Share.Dev`: Start, Teilen-Menü erkennt das Ziel, Alias, Startaufgabe, Toast mit Knöpfen; danach entfernen. `makeappx pack` + `signtool verify` mit dem erzeugten Zertifikat.
- [ ] **Step 6: GREEN**, **Step 7: Commit** `feat(windows): msix package, share target, autostart`

### Task 16: UI-Durchlauf, Doku, Gesamtlauf

**Files:** Create: `tests/Share.Ui.Tests`, `docs/windows.md`; Modify: `README.md`, `docs/protocol.md` (nur Klarstellungen)

- [ ] **Step 1: FlaUI-Test** gegen das veröffentlichte Programm (`--data-dir`, `--allow-local-http`, eigener Go-Server): koppeln, Datei von der Go-CLI annehmen, Text annehmen, Datei per `--send` an ein zweites Gerät anbieten und über lokalen Endpunkt und Relay laden, zweite Instanz, Beenden. Bildschirmfotos nach `tests/bin/shots`.
- [ ] **Step 2: `docs/windows.md`** – Bauen, Installieren, Zertifikat sichern, Aktualisieren, Deinstallieren; **Prüfliste**: Teilen aus Explorer/Fotos/Edge, „Senden an“, Benachrichtigung bei gesperrtem Bildschirm, Neustart → Autostart, Firewall: Handy lädt lokal vom PC, 5-GB-Datei, Ruhezustand während Übertragung, Update über alte Version.
- [ ] **Step 3: Gesamtlauf** – alle `dotnet test`, `go test ./...`, `./gradlew :core:test` grün. **Step 4: Commit** `feat(windows): ui run, docs`

---

## Nicht prüfbar ohne den Nutzer

Installation des signierten Pakets (Zertifikatsimport braucht Adminrechte), Firewall-Regel mit echtem zweitem Gerät, Teilen aus echten Programmen, Verhalten nach Neustart. Dafür die Prüfliste.
