# samedis-care-spl-sync

.NET 9 Sync-Tool zwischen **SPL Actimed** (MS-Access basiert, lokal auf einem Windows-Notebook) und **Samedis.care** (Cloud-Bestandsverwaltung, `https://sync.samedis.care`).

Vollständige Anforderungen, Datenmodell und Sync-Logik:
siehe [`CLAUDE.md`](./CLAUDE.md).

## Solution-Layout

```
src/
  SamedisCare.SplSync.Core/        Library: API-Modelle, HTTP, Mapping, Renderer (cross-platform)
  SamedisCare.SplSync.Tray/        WPF-Tray-EXE mit In-Process-Worker (Windows-only)
tests/
  SamedisCare.SplSync.Core.Tests/  xUnit, läuft auch auf macOS
tools/
  debloat-windev-vm.ps1            PowerShell-Skript zum Aufräumen einer Windows-Test-VM
spl_data/                          Beispiel-DB, CSV-Exports, Hersteller-Doku (read-only)
```

## Build

```sh
# Restore + Tests (cross-platform)
dotnet restore
dotnet test tests/SamedisCare.SplSync.Core.Tests

# Tray-EXE (Windows-only Single-File-EXE)
dotnet publish src/SamedisCare.SplSync.Tray -c Release -r win-x64 \
    --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

Auf macOS/Linux baut der `Core` mitsamt Tests problemlos. Das `Tray`-Projekt
ist WPF und damit Windows-only — Build dort nur per `dotnet publish -r win-x64`
(crossbuild geht, debuggen nicht).

## Sync-Modi (Kurzfassung)

Das Tool unterstützt zwei Upload-Modi. Sie sind **unabhängige Schalter** und
können beliebig kombiniert werden — kein Entweder-Oder.

| Schalter | Trigger | Was passiert |
| --- | --- | --- |
| `upload_mode_pdf_pickup` | PDF erscheint im `protocol_pdf_dir` | FileSystemWatcher liest das PDF, korreliert über die Prüfberichtsnummer im Dateinamen mit `A3_FINISHED_TEST`, und hängt das PDF an den Samedis-Vorgang an. **Existiert kein passendes Issue, wird es angelegt** — der Druck ist eine explizite Techniker-Aktion. |
| `upload_mode_png_on_completion` | Neue Zeile in `A3_FINISHED_TEST` | DB-Polling alle ~30 s. Aus den Messwerten wird lokal ein PNG-Wertenachweis-Bild gerendert und an den Samedis-Vorgang gehängt. **Hier wird kein Issue angelegt** — wenn keins existiert, wird der Test still übersprungen, weil der DB-Eintrag allein keine eindeutige Absicht "in Samedis dokumentieren" ausdrückt. |

### Sinnvolle Kombinationen

| PDF-Watcher | PNG-Polling | Anwendungsfall |
| --- | --- | --- |
| an | aus | **Modus 1 pur**: Techniker arbeitet komplett in Actimed, druckt das vom Hersteller vorgesehene Prüfprotokoll, das geht 1:1 ans Samedis-Issue. Geeignet für QM-/Audit-Setups, die das Originalprotokoll archivieren wollen. |
| aus | an | **Modus 2 pur**: Techniker arbeitet in der Samedis-Mobile/Web-Oberfläche, in Actimed läuft nur die Messgerät-Steuerung. PNG-Wertenachweis fliegt automatisch ans Issue, kein Druck nötig. Voraussetzung: das Issue ist über den Download-Pfad bereits in Samedis angelegt. |
| an | an | **Beides**: PNG kommt sofort (~30 s nach DB-Eintrag), PDF kommt später, sobald der Techniker druckt. Issue trägt am Ende beide Anhänge. Sinnvoll, wenn man sowohl die Live-Anzeige im Mobile als auch das vollständige Hersteller-PDF im Vorgang haben will. |
| aus | aus | Nur Download läuft. Upload-Pfade sind komplett deaktiviert. |

Daneben läuft permanent der Download-Pfad: Inventare + offene Maintenance-Issues
von Samedis nach Actimed, im Intervall `sync.download_interval_minutes`. Der
Download legt bei Bedarf neue Geräte in `A3_DEV` an und plant pro Issue eine
`A3_IS_ACT_DEV`-Zeile ein, damit der Techniker die Prüfung in Actimed wie
gewohnt starten kann.

## Datenmodell in Actimed (Crashkurs)

Bevor man die Sync-Logik versteht, hilft ein kurzer Blick auf die
Datenstruktur, die Actimed in seiner MS-Access-DB pflegt. Die Hierarchie ist:

```
Mandant (Kunde)                   ─── A3_CUST            wer besitzt das Gerät
   └── Inventar (Prüfobjekt)      ─── A3_DEV             konkrete Maschine vor Ort
          ├── Gerätetyp           ─── A3_DEV_TYPE        Modell (z.B. "DP300")
          │     ├── Hersteller    ─── A3_MANUF           Firma
          │     └── Geräteart     ─── A3_DEV_KIND        DIMDI-Kategorie ("Defibrillator")
          ├── Standort            ─── A3_LOCATION
          └── geplante Prüfungen  ─── A3_IS_ACT_DEV      welche Tätigkeit, wann fällig
                  │
                  └── Tätigkeit   ─── A3_ACTIVITY        verbindet Wartungsart mit Prüfvorschrift
                          ├── Tätigkeitsart  ─── A3_ACTIVITY_KIND   z.B. "MPBe_§11_STK/DGUV V3"
                          └── Prüfvorschrift ─── TEST_SPEC          das Rezept
                                  └── Prüfschritte ── TEST_SPEC_ITEM ─→ WS_DEV_FUNC_TEST (Arbeitsschritte)
                                                                              └── FUNC_DEV_FUNC_TEST (Funktion = Messgerät-Befehl)

Durchgeführte Prüfungen           ─── A3_FINISHED_TEST       Header
   ├── Prüfschritte              ─── A3_FINISHED_TEST_ITEM   "Sichtprüfung i.O.?"
   └── Messwerte                 ─── A3_FINISHED_TEST_ITEM_RESULT  "0,49 MOhm"
```

**Begriffe in der Reihenfolge, in der ein Techniker damit zu tun hat:**

| Actimed-Begriff       | Tabelle                    | Was es ist |
| --------------------- | -------------------------- | --- |
| **Mandant / Kunde**   | `A3_CUST`                  | Eigentümer des Geräteparks. In unserer Welt = Samedis-Tenant. |
| **Hersteller**        | `A3_MANUF`                 | Firma, die das Gerät baut. |
| **Geräteart**         | `A3_DEV_KIND`              | Abstrakte DIMDI-Kategorie ("Defibrillator", "Blutdruckmessgerät") inkl. DIMDI-Nummer. |
| **Gerätetyp**         | `A3_DEV_TYPE`              | Konkretes Modell — verbindet Hersteller + Geräteart, hat Modellnamen. |
| **Inventar / Prüfobjekt** | `A3_DEV`               | Die echte Maschine vor Ort: Inventarnummer, Seriennummer, Standort, Mandant, Gerätetyp. |
| **Tätigkeitsart**     | `A3_ACTIVITY_KIND`         | Klasse von Wartungen: `MPBe_§11_STK/DGUV V3`, `MPBe_MTK_BDM`, … Im Sync wird dies aus dem Samedis-Issue-Titel/Service per Regex zugeordnet (`maintenance_kind_mapping`). |
| **Tätigkeit**         | `A3_ACTIVITY`              | Verknüpft eine Tätigkeitsart mit einer **Prüfvorschrift** und gibt Intervall (Monate) + Name. Eine Tätigkeitsart kann mehrere Tätigkeiten haben (verschiedene Vorschriften). |
| **Prüfvorschrift**    | `TEST_SPEC` + `TEST_SPEC_ITEM` | Das Rezept: welche Schritte werden in welcher Reihenfolge geprüft. Bringt Actimed mit oder der Dienstleister legt eigene an. |
| **Arbeitsschritt**    | `WS_DEV_FUNC_TEST`         | Ein einzelner Schritt mit Beschreibung ("Gehäuse i.O.?", "Schutzleiterwiderstand messen"). Ruft eine **Funktion** auf. |
| **Funktion**          | `FUNC_DEV_FUNC_TEST`       | Die unterste Ebene — **was macht das Messgerät tatsächlich?** Eine Funktion identifiziert eine Messung wie "VDE 0701-0702 Schutzleiterwiderstand"; ConActX übersetzt das in Befehle an den GM-300/GM-610/DP300/DP600/etc. |
| **Bewertung**         | `EVAL_ITEM`                | Pass/Fail-Kriterien pro Schritt (Limit 1 / Limit 2). |
| **Parameter**         | `PARAM_ITEM`               | Zusätzliche Parameter für eine Messung (z.B. Spannungswert für Ableitstrommessung). |
| **Geplante Prüfung**  | `A3_IS_ACT_DEV`            | Pro Inventar+Tätigkeit eine Fälligkeit (`ACT_DEV_NEXT`). Daher steht bei einem Gerät, welche Tätigkeiten wann dran sind. |
| **Durchgeführte Prüfung** | `A3_FINISHED_TEST` (+`_ITEM`, +`_ITEM_RESULT`) | Ergebnis-Header + Schritt-Status + Messwerte. Aus diesen drei Tabellen baut der Sync das PNG-Wertenachweis-Bild bzw. korreliert beim PDF-Pickup. |

### Wie eine Prüfung in Actimed gestartet wird

Aus Sicht des Technikers (laut Actimed-Handbuch Kap. 8 "Prüfablauf"):

1. **Daten → Prüfobjekt** öffnen — die Liste aller Inventare des/der Mandanten erscheint.
2. **Prüfobjekt aussuchen** (per Filter über Inventarnummer, Seriennummer oder Kunde).
3. **F2 oder Doppelklick** öffnet die Detail-Ansicht mit Reitern: *Prüfobjekt 1*, *Prüfobjekt 2*, *Tätigkeiten + Prüfberichte*, *Systemkomponente*.
4. Im Reiter **„Tätigkeiten + Prüfberichte"** stehen alle zugewiesenen Tätigkeiten mit nächster Fälligkeit (= `A3_IS_ACT_DEV`-Einträge), darunter die Historie der bereits gemachten Prüfberichte.
5. **Tätigkeit auswählen → „Prüfung" (Strg+P)** startet die zugehörige Prüfvorschrift.
6. Actimed lädt die `TEST_SPEC`, läuft die `TEST_SPEC_ITEM` der Reihe nach durch, ruft pro Schritt den Arbeitsschritt + Funktion. ConActX redet mit dem angeschlossenen Messgerät, lässt die Messwerte ein.
7. Am Ende speichert Actimed alles als `A3_FINISHED_TEST` + zugehörige Items + Results — und wenn der Techniker im richtigen Modus arbeitet, druckt er das PDF-Protokoll in den Pickup-Ordner.

### Was der Sync-Tool davon anfasst

- **Schreibt in Actimed**: `A3_MANUF`, `A3_DEV_KIND`, `A3_DEV_TYPE`, `A3_DEV` (für Inventar-Stammdaten), und `A3_ACTIVITY` + `A3_IS_ACT_DEV` (für die geplanten Prüfungen). Falls ein Hersteller/Geräteart/Modell aus Samedis nicht eindeutig auflösbar ist, wird der jeweilige `Unbekannt`-Default-Eintrag (ID=1 in jeder Actimed-Installation) verwendet, statt willkürlich neue Stubs zu erzeugen.
- **Liest aus Actimed**: `A3_DEV`, `A3_ACTIVITY_KIND`, `A3_ACTIVITY` (für ID-Auflösung) und nach abgeschlossenen Prüfungen `A3_FINISHED_TEST(_ITEM)(_RESULT)` für den Upload.
- **Lässt unangetastet**: `TEST_SPEC*`, `WS_DEV_FUNC_TEST`, `FUNC_DEV_FUNC_TEST*`, `EVAL_ITEM`, `PARAM_ITEM*`. Das ist die **Hersteller-Bibliothek + die manuelle Pflege durch den Dienstleister** — dort haben wir nichts zu suchen, weil das Gerätetreiber-Logik ist.

> **Was bei Modus 2 anders ist**: für die "Techniker arbeitet in Samedis"-Variante legt der Dienstleister sich eine **stark vereinfachte Prüfvorschrift** in Actimed an, die nur die Messgerät-Befehle enthält (kein Sichtprüfung, kein Memo, keine Bewertung), und mappt diese im `maintenance_kind_mapping` auf einen eigenen `A3_ACTIVITY_KIND`. Der Techniker schaltet so per Wahl der Tätigkeit zwischen "vollständige Actimed-Prüfung" und "nur Messung" um.

## Voraussetzungen auf dem Windows-Notebook

**Pflicht** (sonst läuft Tray.exe gar nicht):

- **Windows 10 oder 11**, x64.
- **Microsoft Access Database Engine 2016 Redistributable (x64)** —
  liefert den OLE-DB-Provider `Microsoft.ACE.OLEDB.16.0`, ohne den der
  Sync die `actimed3db.mdb` nicht öffnen kann.
  Download:
  [microsoft.com/en-us/download/details.aspx?id=54920](https://www.microsoft.com/en-us/download/details.aspx?id=54920)

  Da Actimed seine eigene 32-bit-ACE-Engine mitbringt, beschwert sich der
  grafische Installer mit „Office 32-bit erkannt, abbrechen". Mit dem
  `/quiet`-Flag legst du x64 sauber daneben:
  ```powershell
  .\AccessDatabaseEngine_X64.exe /quiet
  ```
  Verifizieren, dass der Provider registriert ist:
  ```powershell
  (New-Object System.Data.OleDb.OleDbEnumerator).GetElements() |
      Where-Object { $_.SOURCES_NAME -like "*ACE*" } |
      Select-Object SOURCES_NAME, SOURCES_DESCRIPTION
  ```

- **.NET 9 Runtime** ist **nicht** nötig — die Tray.exe wird als
  Self-Contained Single-File gepublished, bringt also alle .NET-Bibliotheken mit.

### PDF-Drucker für Modus 1 (PDF-Pickup)

Wenn du `upload_mode_pdf_pickup = true` benutzt, braucht der PDF-Watcher Dateien
im `protocol_pdf_dir`, deren **Dateiname die Prüfberichtsnummer enthält** (das
ist der Korrelations-Schlüssel zur `A3_FINISHED_TEST.TEST_Pruefberichtsnummer`).
Actimed setzt zwar laut *Extras → Optionen → "Name des Druckauftrags"* den
Druckjob-Namen auf `Inventarnummer_Pruefberichtsnummer`, aber **nicht jeder
PDF-Drucker übernimmt diesen Namen** in den Dateinamen.

> **Microsoft Print to PDF funktioniert NICHT.** Der eingebaute Windows-PDF-Drucker
> ignoriert den Druckjob-Namen komplett — der "Speichern unter"-Dialog kommt
> mit leerem Feld, der Techniker müsste die Prüfberichtsnummer jedes Mal
> manuell tippen. Das ist nicht praktikabel.

Empfehlung: **PDFCreator** (kostenlos, Open Source, https://www.pdfforge.org/pdfcreator)
mit einem **Auto-Save-Profil**. Einmalig einrichten:

1. PDFCreator installieren.
2. PDFCreator öffnen → **Profile bearbeiten**.
3. Im Profil:
   - **Speichern → Auto-Save** aktivieren
   - **Zielordner**: gleicher Pfad wie `actimed.protocol_pdf_dir`
     (z. B. `C:\Users\Public\SPL\Reports`)
   - **Dateiname**: `<Title>` (Token, der den Druckjob-Namen einsetzt)
   - **Format**: PDF, ohne Verschlüsselung, ohne OCR
4. **Aktionen → Nach Auto-Save → PDFCreator beenden** — sonst bleibt das
   PDFCreator-Fenster nach jedem Druck offen.
5. PDFCreator als Standarddrucker setzen oder beim Druckdialog auswählen.

Damit landet jedes Actimed-Protokoll automatisch unter
`<protocol_pdf_dir>\<Inventarnummer>_<Pruefberichtsnummer>.pdf`, ohne
Dialog, und der Watcher zieht es sofort.

Alternativen: **CutePDF Writer** (mit gepatchtem `cpw.ini` für festes
Ausgabeverzeichnis), **Bullzip PDF Printer**, **Foxit PDF Editor** (kommerziell).
Alle drei verstehen den Druckjob-Namen — anders als MS Print to PDF.

## Erstkonfiguration

Die EXE bringt eine Vorlage der `config.yml` als Embedded Resource mit. Du
musst **nichts** mitkopieren — beim ersten Start legt die App selbst
`config.yml` neben der EXE an und springt direkt in den Einstellungen-Dialog.

1. EXE einmal kopieren / installieren (`SamedisCare.SplSync.Tray.exe`).
2. EXE doppelklicken. Beim ersten Start:
   - kommt die Willkommens-MessageBox
   - wird `config.yml` aus der eingebetteten Vorlage angelegt
   - öffnet sich das Hauptfenster auf dem Reiter **Einstellungen**
3. Im **Einstellungen**-Reiter ausfüllen — alles per UI, kein YAML-Editor nötig:
   - **Authentifizierung**: Auth-URI, Client-ID, Client-Secret (vom Samedis-Admin)
   - **Samedis API**: Base-URI + API-Version (Defaults sind ok)
   - **Actimed**: Pfad zur `actimed3db.mdb` (mit Datei-Browser), PDF-Pickup-Ordner (mit Folder-Browser), Default-Pruefer
   - **Branding**: Dienstleister-Name + Logo für die PNG-Werteprotokolle
   - **Sync-Verhalten**: Intervalle, welche Modi (PDF-Pickup / PNG-on-Completion) aktiv sein sollen
   - **HTTP / Proxy**: optional Proxy-URL + Auth, TLS-Validierung
4. **Speichern** klicken. Der `client_secret` und `proxy_password` werden
   sofort mit DPAPI verschlüsselt in der `config.yml` abgelegt.
5. **Mandanten zuordnen**: oben „Mandanten zuordnen…" klickt einen Dialog auf,
   der die Samedis-API nach allen Tenants des Sync-Users fragt, parallel die
   Kundenliste aus `A3_CUST` der Actimed-DB lädt, und dich per Klick die
   Zuordnung zusammenklicken lässt.
6. **Wartungsart-Mapping** — eigener Tab im Hauptfenster mit Editor:
   Regex auf Samedis-Service → Actimed-Tätigkeitsart → Actimed-Tätigkeit
   (Prüfvorschrift). Kommt mit sinnvollen Defaults für DGUV V3, MTK BDM,
   Defi/AED, allgemeine Inspektion. **Wichtig**: Damit der Techniker die
   Prüfung in Actimed mit echten Schritten starten kann, muss pro Mapping-
   Zeile ein `Activity Name` gesetzt sein, der auf eine in Actimed
   gepflegte Tätigkeit mit Prüfvorschrift zeigt. Ausführliche Anleitung
   im Abschnitt **„Wartungsart-Mapping einrichten"** unten.
7. Beim ersten Start fragt die App außerdem, ob sie mit Windows automatisch
   gestartet werden soll — siehe „Autostart" unten.

> Falls die EXE woanders hin verschoben wird oder die `config.yml` versehentlich
> gelöscht wird, legt der nächste Start sie automatisch neu an und der
> Einstellungen-Dialog kommt wieder.

### Wartungsart-Mapping einrichten

Die Brücke zwischen Samedis und Actimed läuft über vier Ebenen. Wenn das
Mapping einmal sitzt, läuft alles automatisch — der Techniker bekommt das
in Actimed sichtbare „Tätigkeit + Prüfvorschrift", als hätte er es
selbst angelegt. Aber an den Übergängen muss alles exakt zusammenpassen,
sonst landet die Tätigkeit in Actimed mit Prüfvorschrift „Unbekannt"
(leerer Pruefablauf).

#### Die 4 Ebenen

```
Samedis-Issue                                Actimed
─────────────                                ───────
attributes.maintenance_type / title /        A3_ACTIVITY_KIND     (Tätigkeitsart)
attributes.services            ──Regex──►        z. B. "MPBe_§7_Wartung/Inspektion"
                                                       │
                                                       ├─► A3_ACTIVITY  (Tätigkeit)
                                                       │      z. B. "MPBe_§7_Wartung_Standard"
                                                       │
                                                       └─► TEST_SPEC    (Prüfvorschrift)
                                                              z. B. "VDE 0701 +
                                                              Sichtprüfung 12 Monate"
```

| Ebene | Wo gepflegt | Was |
| --- | --- | --- |
| **Samedis-Issue** (`maintenance_type` / `title` / `services`) | Samedis-Web | freier String, was der Auftrag tun soll |
| **A3_ACTIVITY_KIND** (Tätigkeitsart) | Actimed | Klasse von Wartungen, die der Hersteller mitliefert oder der Dienstleister anlegt |
| **A3_ACTIVITY** (Tätigkeit) | Actimed | konkrete „Variante" innerhalb einer Tätigkeitsart, mit Intervall + verknüpfter Prüfvorschrift |
| **TEST_SPEC** (Prüfvorschrift) | Actimed | das Rezept = Schritte + Limits + Messgerät-Funktionen |

#### Schritt-für-Schritt-Anleitung für ein neues Mapping

**Schritt 1 — In Actimed: Tätigkeitsart identifizieren oder anlegen**

Öffne in Actimed: *Stammdaten → Tätigkeitsarten* (oder
`Daten → Tätigkeitsart` je nach Version).

- Existiert bereits eine passende Tätigkeitsart, schreibe dir den
  **`KIND_NAME` exakt** auf — inklusive Sonderzeichen `§`. Typische
  Beispiele:
  - `MPBe_§11_STK/DGUV V3`
  - `MPBe_§7_Wartung/Inspektion`
  - `MPBe_MTK_BDM`
  - `MPBe_STK Defi (AED)`
- Fehlt eine: neue anlegen.

> Der `KIND_NAME` muss **zeichengetreu** ins Mapping als `Actimed Kind`.
> Ein Tippfehler oder `§` als ASCII-Sequenz `\xa7` führt zum
> Fehler `kind '...' nicht in A3_ACTIVITY_KIND gefunden`.

**Schritt 2 — In Actimed: Tätigkeit mit Prüfvorschrift anlegen**

In Actimed: *Stammdaten → Tätigkeiten*. Wenn die zur Tätigkeitsart
passende Tätigkeit (mit deiner Prüfvorschrift) noch fehlt, jetzt
anlegen:

- **Tätigkeitsart**: die aus Schritt 1 gewählte
- **Prüfvorschrift**: die `TEST_SPEC`, die der Techniker beim Start der
  Prüfung durchlaufen soll (= Schritte, Messgerät-Funktionen, Limits)
- **Intervall**: in Monaten (12, 24, 48 …)
- **ACTIVITY_NAME**: ein eindeutiger Name, z. B.
  `MPBe_STK_Defi_AED_DP-300` oder `MPBe_§7_Wartung_Standard`. Diesen
  Namen brauchst du gleich.

> Eine Tätigkeitsart kann mehrere Tätigkeiten haben — z. B.
> verschiedene `MPBe_STK Defi (AED)`-Tätigkeiten, die je nach Defi-Modell
> eine andere Prüfvorschrift nutzen. Wenn du im Mapping einen
> spezifischen `Activity Name` setzt, holt der Sync genau die passende
> Variante.

**Schritt 3 — In Samedis: Service-/Wartungstyp-String festlegen**

Im Samedis-Web werden offene Wartungen als **Issue** mit den
Attributen `maintenance_type`, `title` und `services[]` angelegt
(manuell oder per Vorlage). Der Sync greift alle drei Felder ab und
matched sie kombiniert per Regex.

In der Praxis reicht es, dass im **Issue-Titel** oder einer **Service-
Bezeichnung** ein eindeutiges Stichwort steht, das der Regex matcht. Beispiele:

- Titel `STK nach DGUV V3`           → matcht `(?i)dguv\s*v?3|stk.*§\s*11`
- Titel `MTK Blutdruckmessung 24M`   → matcht `(?i)mtk.*bdm|blutdruck`
- Service `Defi-AED-Prüfung`         → matcht `(?i)defi.*aed`
- Titel `§7 Wartung & Inspektion`    → matcht `(?i)wartung|inspektion`

> Wenn du als Dienstleister mehrere Kunden mit unterschiedlichen
> Title-Konventionen hast, sammle die typischen Stichwörter in den
> Regex-Patterns mit `|` als Oder.

**Schritt 4 — Im Tool: Mapping-Tab pflegen**

Hauptfenster → Tab **„Wartungsart-Mapping"**. Pro Wartungsart eine Zeile:

| Match (Regex) | Actimed Kind | Activity Name (Pruefvorschrift) |
| --- | --- | --- |
| `(?i)dguv\s*v?3\|stk.*§\s*11` | `MPBe_§11_STK/DGUV V3` | `MPBe_STK_HF_emed-100-014` |
| `(?i)mtk.*bdm\|blutdruck` | `MPBe_MTK_BDM` | `MPBe_Prüfung_MTK_BDM` |
| `(?i)defi.*aed` | `MPBe_STK Defi (AED)` | `MPBe_STK_Defi_AED_DP-300` |
| `(?i)wartung\|inspektion` | `MPBe_§7_Wartung/Inspektion` | `MPBe_§7_Wartung_Standard` |
| (leer = Fallback) | `MPBe_§7_Wartung/Inspektion` | `MPBe_§7_Wartung_Standard` |

- **Match (Regex)**: erste passende Zeile gewinnt. Leer = Default-Fallback.
  Case-insensitive (`(?i)` als Prefix).
- **Actimed Kind**: muss exakt einem `KIND_NAME` aus
  `A3_ACTIVITY_KIND` entsprechen.
- **Activity Name**: optional aber **dringend empfohlen**. Muss exakt einem
  `ACTIVITY_NAME` aus `A3_ACTIVITY` entsprechen. Ohne diesen Eintrag
  legt der Sync entweder eine vorhandene Tätigkeit zur Tätigkeitsart
  willkürlich aus, oder, wenn keine existiert, **skipt das Issue**
  mit einer Fehlermeldung im Tab „Letzte Meldungen". Vorher hatte er
  notdürftig eine Tätigkeit mit Prüfvorschrift „Unbekannt" angelegt —
  das machen wir bewusst nicht mehr, weil das nur das Problem verschleiert.

Nach Edit: **„Speichern"** klicken. Der Sync-Worker übernimmt die neuen
Regeln beim nächsten Tick (max. `download_interval_minutes` warten,
oder direkt mit oben **„Jetzt synchronisieren"** anstoßen).

#### Häufige Fehler und Diagnose

Alle Fehlermeldungen erscheinen im Tab **„Letzte Meldungen"** — das
ist die zentrale Diagnose-Quelle, sobald etwas am Mapping schiefläuft.

| Symptom in „Letzte Meldungen" | Ursache | Lösung |
| --- | --- | --- |
| `kind '...' nicht in A3_ACTIVITY_KIND gefunden` | `Actimed Kind`-Wert im Mapping passt nicht zu einem `KIND_NAME` in Actimed (Tippfehler, `§` vs `paragraph`, Leerzeichen). | `KIND_NAME` aus *Stammdaten → Tätigkeitsarten* exakt rauskopieren. |
| `actimed_activity_name '...' nicht in A3_ACTIVITY gefunden` | `Activity Name` im Mapping zeigt auf eine Tätigkeit, die in Actimed nicht existiert. | Entweder die Tätigkeit in Actimed anlegen (Schritt 2) oder den Namen im Mapping korrigieren. |
| `Keine A3_ACTIVITY (Tätigkeit) für KIND_NAME='...' in Actimed vorhanden und im Mapping ist kein 'actimed_activity_name' gesetzt` | Mapping ohne `Activity Name`, und die Tätigkeitsart hat keine einzige Tätigkeit. | Tätigkeit anlegen (Schritt 2) und ihren Namen im Mapping als `Activity Name` setzen. |
| Tätigkeit erscheint in Actimed, aber Prüfvorschrift = `Unbekannt`, Pruefdialog leer | Sync hat (in alter Version) eine Stub-Tätigkeit mit `TEST_SPEC_ID=1` angelegt. | Eintrag in Actimed manuell entfernen, Mapping um `Activity Name` ergänzen, Cursor zurücksetzen, neu syncen. |
| Issue wird gar nicht heruntergeladen (`0 Aufträge`) | Cursor steht hinter dem `updated_at` des Issues, oder Issue ist auf `done`. | Settings → „Cursor zurücksetzen" oder das Issue in Samedis kurz auf „Pending" setzen. |
| Falsche Tätigkeitsart wird gewählt (z. B. Defi-Issue landet bei Wartung) | Regex matcht in der falschen Reihenfolge — die spezifischere Regel steht weiter unten als der Fallback. | Im Mapping-Tab die spezifischen Regeln **vor** allgemeinere Regeln ziehen (erste passende gewinnt). |

#### Ein konkretes Beispiel von Anfang bis Ende

Ein Defi-Hersteller liefert AEDs vom Typ Philips HeartStart. Der
Dienstleister will diese alle 24 Monate prüfen.

1. **In Actimed**, *Tätigkeitsart*: existiert bereits `MPBe_STK Defi (AED)`.
2. **In Actimed**, *Tätigkeit*: lege an —
   - Tätigkeitsart: `MPBe_STK Defi (AED)`
   - Prüfvorschrift: `MPBe_STK_Defi_AED_DP-300` (eigene `TEST_SPEC` mit Defi-Schritten + Schock-Energie-Messung am DP-300)
   - Intervall: 24
   - ACTIVITY_NAME: `MPBe_STK_Defi_AED_DP-300`
3. **In Samedis**, neues Issue:
   - inventory: HeartStart-Inventar
   - title: `Defi STK 24M`
   - services: `["Defi-AED-Prüfung"]`
   - maintenance_type: `maintenance`
   - due_on: 2027-05-01
4. **Im Tool**, Tab „Wartungsart-Mapping":
   - Match: `(?i)defi.*aed`
   - Actimed Kind: `MPBe_STK Defi (AED)`
   - Activity Name: `MPBe_STK_Defi_AED_DP-300`
   - **Speichern**.

Beim nächsten Sync-Tick (oder direkt per „Jetzt synchronisieren"):

- Sync zieht das Issue per `filter[status]=not_done&filter[issue_type]=maintenance`.
- Mapper matcht `Defi-AED-Prüfung` → Tätigkeitsart `MPBe_STK Defi (AED)`.
- `FindActivityByName('MPBe_STK_Defi_AED_DP-300')` liefert die Tätigkeit
  inklusive ihrer `TEST_SPEC_ID`.
- Eintrag in `A3_IS_ACT_DEV` für das Inventar mit `ACT_DEV_NEXT = 2027-05-01`.

In Actimed sieht der Techniker beim Inventar im Reiter
„Tätigkeiten + Prüfberichte" eine Zeile:

| Prüfvorschrift | Nächste Prüfg. | Tätigkeit |
| --- | --- | --- |
| MPBe_STK_Defi_AED_DP-300 | 01.05.2027 | MPBe_STK Defi (AED) |

`Strg+P` → Actimed lädt die `TEST_SPEC` und arbeitet die
Defi-Schritte am angeschlossenen DP-300 ab.

### Wartung & Reset (im Einstellungen-Reiter)

Drei Buttons, die typische Reparatur-Operationen direkt aus der UI ausführen
— ohne SQL-Tool gegen die `actimed3db.mdb`:

- **Reparatur-Lauf jetzt starten**: läuft alle vom Sync angelegten Inventare
  durch und setzt fehlende `LOCATION_ID`/`STATUS_ID`/etc. auf die
  „Unbekannt"-Defaults (ID=1 bzw. STATUS_ID=2). User-eigene Werte werden
  nicht überschrieben.
- **Cursor zurücksetzen (alle aktiven Mandanten)**: löscht die Download-/Upload-
  Cursor in der `state.sqlite`, damit der nächste Sync alles neu zieht. Bietet
  direkt im Anschluss einen Yes/No-Dialog „Jetzt synchronisieren?" an.
- **Sync-Inventare löschen…**: räumt alle vom Sync angelegten `A3_DEV`-Einträge
  weg (erkannt am `DEV_Memo='created_by=spl-sync...'`), inkl. der zugehörigen
  `A3_IS_ACT_DEV`-Pruefplanungen. User-eigene Inventare bleiben erhalten.

Alle drei respektieren das Actimed-Lock — wenn Actimed offen ist, kommt der
übliche „Bitte schließen"-Dialog.

### Autostart mit Windows

Beim ersten Programmstart fragt das Tool einmalig:

> Soll SamedisCare SplSync bei jedem Windows-Start automatisch geladen werden?

- **Ja** legt einen Eintrag unter
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SamedisCare.SplSync` an,
  der die EXE beim Login startet. Per-User-Eintrag, kein Admin nötig.
- **Nein** lässt es weg. In beiden Fällen wird `app.autostart_prompted: true`
  in der `config.yml` gespeichert, damit die Frage nicht jedes Mal kommt.

Später ändern: Rechtsklick auf das Tray-Icon → **„Mit Windows starten"**.
Der Menüeintrag hat ein Häkchen, das den aktuellen Zustand anzeigt; ein Klick
darauf toggelt zwischen aktiv und inaktiv. So kann der Techniker auch nach dem
ersten „Nein" jederzeit nachträglich aktivieren.

> Wenn die EXE später an einen anderen Pfad verschoben wird, zeigt der
> Autostart-Eintrag noch auf den alten Ort und Windows kann die App nicht mehr
> starten — in dem Fall einfach im Tray-Menü Autostart einmal aus- und wieder
> einschalten, das schreibt den aktuellen EXE-Pfad neu in die Registry.

### Secrets in der `config.yml`

`auth.client_secret` und `http.proxy_password` werden beim Setup **im Klartext**
eingetragen. Sobald die App das erste Mal die Config speichert (z.B. nach dem
„Mandanten zuordnen…"-Dialog), werden diese Felder mit der Windows-DPAPI
verschlüsselt und als `enc:<base64>` zurück in die `config.yml` geschrieben.

Was das praktisch bedeutet:

- Beim ersten Editieren ruhig den Klartext eintippen — passt schon.
- Nach dem ersten Save sieht die Datei z.B. so aus:
  ```yaml
  auth:
    client_secret: "enc:AQAAANCMnd8BFdERjHo..."
  ```
- Die App entschlüsselt das beim Laden automatisch.
- Der DPAPI-Schlüssel ist an die **lokale Maschine** gebunden
  (`DataProtectionScope.LocalMachine`). Heißt: die `config.yml` lässt sich
  nicht auf einen anderen Rechner kopieren — dort schlägt die Entschlüsselung
  fehl. Auf demselben Rechner kann jeder Prozess (auch ein anderer User-Account)
  das Secret wieder lesen — der Schutz richtet sich gegen *Augen-Schnüffelei*
  (Backups, Mail-Anhang, Screenshot), nicht gegen einen Admin auf der Maschine.
- Wenn du das Secret später ändern musst: einfach wieder Klartext rein und
  speichern, die App verschlüsselt automatisch beim nächsten Save.

## Lock-Verhalten

Wenn der Sync gegen die `actimed3db.mdb` schreiben muss, während Actimed sie
exklusiv geöffnet hat, fängt der OleDb-Provider eine `ActimedLockedException`.
Der Tray zeigt dann einen Modal-Dialog „Actimed bitte schließen" — der
Techniker schließt Actimed kurz, klickt „Weiter", und der Sync zieht durch.
Beim nächsten Öffnen von Actimed durch den User passiert nichts Schlimmes;
der nächste Schreibversuch fängt den Lock erneut ab.

## Lizenz / Urheber

Teile von `Core/Api/` (Authenticate, RequestData, FilterBuilder-Pattern,
Tenant-Settings) sind eng angelehnt an das öffentliche
[samedis-care-external-sync](https://github.com/Samedis-care/samedis-care-external-sync)
(MIT). Eigene Anpassungen für Multi-Tenant- und Actimed-Schreibpfad sind unter
derselben Lizenz vorgesehen.
