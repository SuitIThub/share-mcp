# Android-App

Voraussetzungen zum Bauen: JDK 17, Android-SDK (Plattform 37.0, Build-Tools ≥ 36), Go (nur für die Tests). Läuft ab Android 10.

## Bauen

```sh
cd android
echo "sdk.dir=C:/Users/<du>/AppData/Local/Android/Sdk" > local.properties   # falls ANDROID_HOME nicht gesetzt ist
export JAVA_HOME="/c/Program Files/Eclipse Adoptium/jdk-17.0.17.10-hotspot" # JDK 17
./gradlew :app:assembleRelease
```

Ergebnis: `app/build/outputs/apk/release/app-release.apk` (ohne Schlüssel: `app-release-unsigned.apk`, lässt sich nicht installieren).

### Schlüssel (einmalig)

```sh
keytool -genkeypair -v -keystore share.jks -alias share -keyalg RSA -keysize 4096 -validity 10000
```

`android/keystore.properties` (wird nicht eingecheckt):

```properties
storeFile=share.jks
storePassword=…
keyAlias=share
keyPassword=…
```

Schlüssel gut sichern: Updates müssen mit demselben Schlüssel signiert sein.

## Push (Firebase, optional)

Ohne Firebase funktioniert alles, aber Angebote sind nur bei geöffneter App sichtbar.

1. In der Firebase-Konsole ein Projekt anlegen, Android-App mit Paketname `de.suitithub.share` hinzufügen.
2. `google-services.json` nach `android/app/` legen und neu bauen.
3. Projekteinstellungen → Dienstkonten → neuen privaten Schlüssel erzeugen, auf den Server legen und in der Server-Konfiguration als `firebase_credentials` eintragen (siehe `docs/deploy/README.md`).
4. Die App meldet ihr Push-Token beim nächsten Start von selbst. Einstellungen zeigen „Push ist eingerichtet“.

Über Google läuft nur die Angebots-ID, nie Inhalt.

## Einrichten auf dem Handy

1. APK installieren (Quelle erlauben).
2. Auf dem Server `share-server pair` (oder in einem gekoppelten Gerät „Gerät hinzufügen“), QR-Code scannen.
3. Benachrichtigungen erlauben.
4. Empfohlen: Akku-Optimierung für „Share“ ausschalten (Einstellungen → Apps → Share → Akku → Uneingeschränkt), sonst verzögern manche Hersteller den Push.

## Tests

```sh
./gradlew :core:test :app:testDebugUnitTest   # Kern gegen den echten Go-Server; go im PATH, sonst -PgoBin=…
./connected-test.sh                           # auf Emulator: startet selbst einen share-server auf Port 18080
```

Der Build-Typ `minified` entspricht `release` (R8), darf aber mit einem Testserver über `http://10.0.2.2` sprechen.

## Prüfliste am echten Handy

Im Emulator nicht prüfbar:

- [ ] Push bei geschlossener App und ausgeschaltetem Bildschirm
- [ ] Annehmen direkt vom Sperrbildschirm
- [ ] 2-GB-Datei im WLAN: Anzeige „lokal“, volle Geschwindigkeit
- [ ] 2-GB-Datei über Mobilfunk: Anzeige „über Server“
- [ ] WLAN mitten im Download ausschalten: läuft über den Server weiter
- [ ] Vom Handy senden, Bildschirm aus: Download beim Empfänger läuft durch
- [ ] Teilen aus Galerie, Google Fotos (nur in der Cloud liegendes Bild), Chrome (Link), Dateien (mehrere)
- [ ] Hotspot des Handys: PC empfängt lokal
- [ ] `share send app.apk --to <handy>` vom PC, danach „Installieren“
- [ ] App während eines Downloads wegwischen und wieder öffnen: lädt ab dem Teilstand weiter
