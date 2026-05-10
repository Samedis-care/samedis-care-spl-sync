# CLAUDE.md — samedis-care-spl-sync

> Projektkontext, Datenmodell und Architekturvorgaben für die Entwicklung eines
> .NET-Sync-Tools zwischen **SPL Actimed** und **Samedis.care**.
>
> Zielgruppe dieser Datei: Claude (und Entwickler:innen), wenn sie in diesem
> Repository neue Code-Artefakte erzeugen oder bestehende ändern.

---

## 1. Ziel des Projekts

Ein Service-Dienstleister im medizintechnischen Prüfgeschäft setzt parallel zwei
Systeme ein:

- **SPL Actimed** (S.P.L. Elektronik, MS-Access basiert, lokal auf
  einem Windows-Notebook) — wird vor Ort, ggf. **offline**, zur Durchführung der
  Prüfungen und zur Steuerung der SPL-Messgeräte (Sicherheitstester, GM-800,
  DP300/DP600 …) genutzt.
- **Samedis.care** — Cloud-Bestandsverwaltung der Kunden des Dienstleisters
  (Mandanten). API-Endpoint: `https://sync.samedis.care`.

Es soll ein **C#/.NET 8 Programm** entstehen, das auf demselben Notebook wie
Actimed läuft und

1. eine **Liste von Mandanten** verwaltet (jeder Mandant = ein eigener
   Samedis-`tenant_id`),
2. **pro Mandant Inventarstamm- und Prüfauftragsdaten von Samedis nach Actimed
   herunterzieht** (Download),
3. nach Durchführung der Prüfung in Actimed die **Ergebnisse zurück an Samedis**
   liefert (Upload), inklusive eines Anhangs am Issue (siehe 5.4).

Use-Case-Klammer: Der Techniker führt die Prüfung evtl. offline durch. Sobald
das Notebook wieder online ist, soll der Sync automatisch nachziehen — sowohl
Download (neue Aufträge holen) als auch Upload (Ergebnisse zurückspielen).

Auslieferung: **eine Windows-EXE mit Tray-Icon + Hintergrund-Worker**, alle
Sync-Logik läuft im Tray-Prozess. Kein separater Dienst — Begründung: der
Techniker ist eingeloggt während er arbeitet, der Lock-Dialog gegen Actimed
muss modal im selben Prozess sein, und ein zweiter Worker (Service +
Tray-Viewer) hätte nur IPC-Komplexität gebracht ohne praktischen Mehrwert.

Das Tray-Icon zeigt den Sync-Status an (idle, running, ok, warn, error) und
blendet bei Fehlern oder benötigter Nutzeraktion (Login abgelaufen, Mapping
unklar, Lock-Konflikt) einen Dialog ein.

---

## 2. Vergleichsprojekt (Lesetipp, kein Fork)

`https://github.com/Samedis-care/samedis-care-external-sync` (Open Source, MIT,
.NET 8, Console). Davon **wiederverwenden**:

- Authentifizierungsfluss gegen `auth.uri` (siehe Abschnitt 6).
- HTTP-/Retry-/Proxy-Layer (`Samedis.cs` → `Authenticate`, `RequestData`).
- JSON-API-Modelle (`Tasks.cs`, `Inventories.cs`, `DeviceModels.cs`,
  `DeviceTypes.cs`, `Tenant.cs`, …) und der `FilterBuilder` für
  `gridfilter`-Anfragen.
- Upload-Flow für Issue-Dokumente (`PostTaskDocumentUpload` mit Fallback-Feldern
  `data[document]` → `data[file]` → `data[image]`).
- Build-Kommando für eine Single-File-EXE:
  `dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true`.

**Unterschiede** zu jenem Projekt (das ist der Kern dieses Repos):

| Aspekt | external-sync (Referenz) | dieses Projekt |
| --- | --- | --- |
| Datenquelle / -senke | CSV-Dateien (`<to_samedis>/tasks.csv` …) | **MS-Access-DB** `actimed3db.mdb` direkt lesen *und schreiben* |
| Mandanten | **ein** `samedis.tenant_id` pro Lauf | **n** Mandanten pro Lauf (Liste, GUI-konfigurierbar) |
| Sync-Richtung | uni-direktional (Down oder Up je Ressource) | **bi-direktional**: Inventare + offene Prüfungen Samedis → Actimed; abgeschlossene Prüfungen + Anhang Actimed → Samedis |
| Sync-Fokus | Inventories, Tasks, Locations, Models, … | **Inventar-Kern (read)** + **Issues vom Typ `maintenance`** + Anhang |
| Trigger | manuell / cron | Tray-EXE (oder Dienst), Intervall + „On reconnect" |
| UI | nur YAML-Config | **GUI** + **Tray-Icon mit Status & Fehler-Dialog** |
| Offline | nicht relevant | erstklassig: Outbox/Inbox-Queue + Resume, wenn Online |

---

## 3. Datenquellen auf dem Notebook

### 3.1 Actimed-Datenbank

- **Datei:** `actimed3db.mdb` (MS Access). Im Repo unter
  `spl_data/actimed3db.mdb` (Schnappschuss zu Entwicklungszwecken).
- **Schema-Familie:** `A3_*`-Tabellen (Stammdaten + Prüfungen) plus
  `TEST_SPEC_*`, `EVAL_ITEM`, `PARAM_ITEM`, `PARAM_DSCR_ITEM`,
  `FUNC_DEV_FUNC_TEST*`, `WS_DEV_FUNC_TEST` (Prüfvorschriften).
- **Production-Pfad** (typisch): irgendwo unter dem Actimed-Installationsverzeichnis.
  Pfad muss konfigurierbar sein; nichts Hardcoded.
- **Schreibzugriff**: Wir legen in Actimed neue Datensätze an, insbesondere
  `A3_DEV` (falls Inventar fehlt) und `A3_IS_ACT_DEV` (geplante Prüfung am
  Gerät). Schreibzugriff geht **nur**, wenn Actimed die Datei nicht exklusiv
  geöffnet hat. **Strategie**: Wir versuchen den Write direkt — fängt der
  OleDb-Provider die Sperre ab (typische Fehler: „File already in use",
  `OleDbException` mit HRESULT `0x80004005`, oder Lock-Konflikt auf
  `actimed3db.ldb`), zeigt das Tray-Icon einen Dialog an: „Actimed bitte kurz
  schließen, damit der Sync fortfahren kann". Sobald der User das Schließen
  bestätigt hat, retryen wir den Write. Kein präventives Lock-Probing — wir
  vertrauen dem Fehlerpfad, das ist robuster als Datei-System-Heuristiken.

### 3.2 Zugriff aus .NET (Windows = Produktion)

Jet/ACE OLE-DB ist 32-/64-bit-zickig. Empfohlene Optionen, in Reihenfolge der
Robustheit:

1. **Microsoft.ACE.OLEDB.16.0** über `System.Data.OleDb` — setzt installierten
   Access Database Engine 2016 Redistributable (passend zur .NET-Bitness)
   voraus.
2. **MDBTools-basierte CLI** als Fallback in CI/Linux nicht relevant — wir
   liefern Windows-only aus.
3. **Read-only kopierte Snapshot-Datei** für Lesepfade: Bevor gelesen wird,
   lokale Kopie ziehen, damit ein parallel laufender Actimed-Prozess nicht
   gestört wird (Actimed öffnet die DB exklusiv, je nach Workgroup-Setup).
   Schreibpfade müssen aber zwingend auf die Original-DB gehen.

> **Pflicht-Voraussetzung beim Kunden**: der `Microsoft.ACE.OLEDB.16.0`-Provider
> muss als **x64-Version** installiert sein. Actimed selbst bringt nur die
> 32-bit-Variante mit, die unsere x64-Tray.exe nicht nutzen kann. Daher zusätzlich:
>
> ```powershell
> # Download: https://www.microsoft.com/en-us/download/details.aspx?id=54920
> .\AccessDatabaseEngine_X64.exe /quiet
> ```
>
> Das `/quiet`-Flag ist entscheidend, weil der grafische Installer sonst die
> bestehende 32-bit-Office-/Actimed-Engine sieht und mit „abbrechen" reagiert.
> Stille Installation legt x64 sauber daneben.
>
> Verifikation: `(New-Object System.Data.OleDb.OleDbEnumerator).GetElements()`
> muss einen Eintrag mit `Microsoft.ACE.OLEDB.16.0` listen.
>
> Im Tray-Code prüfen wir das beim ersten Connect-Versuch und werfen bei
> fehlendem Provider eine klare Meldung mit Download-Link aus (HRESULT
> `0x80131501` + Message „Provider nicht registriert").

> Hinweis: Die Beispiel-`.mdb` im Repo ist Stand 2025-09; Schema kann zwischen
> Actimed-Versionen variieren (das Verzeichnis enthält `actimed3db_v427.zip` als
> historische Version). Beim Lesen tolerant gegen fehlende Spalten sein.

### 3.3 Entwickeln auf macOS / Linux (Dev-Only)

Auf macOS gibt es keinen offiziellen MS-Access-Client. Für Entwicklung und
Schema-Inspektion stehen folgende Wege offen — **nichts davon geht in die
Auslieferung**, das Tool selbst läuft nur auf Windows:

- **MDBTools** (Open Source, CLI) — empfohlen für Schema-Dump und Read-Only-
  Tests:
  ```sh
  brew install mdbtools
  mdb-tables  spl_data/actimed3db.mdb
  mdb-schema  spl_data/actimed3db.mdb mysql > schema.sql
  mdb-export  spl_data/actimed3db.mdb A3_FINISHED_TEST > A3_FINISHED_TEST.csv
  ```
  Die im Repo unter `spl_data/actimed_export_csv/` liegenden CSVs sind genau
  so erzeugt — sie sind also **kein Live-Datenfeed**, sondern ein Snapshot,
  der auf Mac/Linux als Ersatz für DB-Zugriff dient.
- **DBeaver + UCanAccess-Treiber** (Java/JDBC) — gut, wenn man eine grafische
  Inspektion möchte. UCanAccess kann auch schreiben, allerdings nicht
  zwingend kompatibel mit ACE-Sperren — nicht für Produktionspfade nutzen.
- **LibreOffice Base** (über JDBC/UCanAccess konfigurieren) — pragmatisch für
  ad-hoc-Inspektion.
- **SQLite-Spiegel** via `csv2sqlite.py` (siehe `spl_data/actimed_export_csv/`)
  — ideal für Unit-Tests des Mapping-Codes; das `Core`-Projekt sollte den
  Datenzugriff hinter einem `IActimedRepository`-Interface verstecken, das
  sowohl gegen `Microsoft.ACE.OLEDB.16.0` als auch gegen die SQLite-Kopie
  laufen kann (Produktion vs. Tests/macOS).

Für **Schreib-Tests** auf macOS reicht der SQLite-Pfad — die produktive
OleDb-Schreibroute wird dann auf einem Windows-Notebook (echt oder VM) verifiziert.

### 3.4 Vorhandene CSV-Exporte (für Tests/Reverse-Engineering)

Unter `spl_data/actimed_export_csv/` liegt ein Voll-Dump der für das Sync
relevanten Tabellen plus `csv2sqlite.py`, das daraus eine SQLite-DB baut. Das
ist nicht Teil der Auslieferung, aber **die Quelle der Wahrheit fürs
Datenmodell-Mapping**, solange wir keinen Live-Zugriff auf eine echte Actimed-DB
haben.

`spl_data/SPL/*.pdf` sind die Original-Handbücher von SPL. Sie zerfallen in zwei Klassen:

- **Software-/Daten-Doku** — potenziell relevant, wenn Detailfragen zu Actimed
  selbst auftauchen:
  - `actimed_d_m.pdf` — Hauptsoftware-Handbuch (Datenmodell, Druck/PDF-Export-
    Schnittstelle für 5.7).
  - `export_import_d_m.pdf` — beschreibt SPL.DAT-Format und etwaige offizielle
    Import-/Export-Wege.
  - `designer_m.pdf` — Prüfplan-Designer, erklärt `TEST_SPEC*`/`A3_ACTIVITY`-
    Zusammenhänge.
  - `loyhutz_d_m.pdf`, `cx_d_m.pdf`, `cxtm_d_m.pdf`, `synactx_d_m.pdf` —
    allgemein/Softwarevarianten.
- **Messgeräte-Doku** — für die Sync-Logik **nicht relevant**, beschreibt nur
  Hardware-Bedienung: `dp300_d_m.pdf`, `dp600_d_m.pdf`, `es300_d_m.pdf`,
  `gm100_d_m.pdf`, `hsd_d_m.pdf`, `sicherheitstester_anhang_d_m.pdf`,
  `kurzanleitung022.pdf`.

> Stand 2026-05-06: die Software-Doku-PDFs wurden noch **nicht** systematisch
> ausgewertet — bei konkreten Detailfragen (PDF-Export-CLI, Import-Format,
> Prüfvorschriften-Modell) gezielt im jeweiligen PDF nachschauen.

---

## 4. Wichtigste Tabellen & Felder in Actimed

### 4.0 Datenmodell-Hierarchie

```
A3_CUST  (Mandant)
   └── A3_DEV  (Inventar)
          ├── A3_DEV_TYPE  (Modell)
          │     ├── A3_MANUF       (Hersteller)
          │     └── A3_DEV_KIND    (Geräteart, DIMDI-Kategorie)
          ├── A3_LOCATION  (Standort)
          └── A3_IS_ACT_DEV  (geplante Prüfungen)
                 │
                 └── A3_ACTIVITY  (Tätigkeit, Intervall + Name)
                        ├── A3_ACTIVITY_KIND  (Tätigkeitsart, z.B. "MPBe_§11_STK/DGUV V3")
                        └── TEST_SPEC         (Prüfvorschrift = Recipe)
                               └── TEST_SPEC_ITEM
                                      └── WS_DEV_FUNC_TEST  (Arbeitsschritt)
                                             └── FUNC_DEV_FUNC_TEST  (Funktion = Messgerät-Befehl)

A3_FINISHED_TEST  (durchgeführte Prüfung, Header)
   ├── A3_FINISHED_TEST_ITEM       (Schritt-Status)
   └── A3_FINISHED_TEST_ITEM_RESULT (Messwert pro Schritt)
```

**Trennlinie zwischen "Sync schreibt" und "Sync lässt in Ruhe":**

| Tabelle | Sync-Verhalten |
| --- | --- |
| `A3_CUST` | **Read-only**. Mandanten legt der Dienstleister manuell an, das Sync-Tool liefert nur die `CUST_ID` aus der Tenant-Mapping-Config. |
| `A3_MANUF`, `A3_DEV_KIND`, `A3_DEV_TYPE` | **Write (mit Default-Fallback)**. Bei jedem Inventar-Sync auflösen oder anlegen — falls Samedis keine eindeutigen Werte liefert, wird der `Unbekannt`-Default-Eintrag (ID=1) verwendet, statt willkürlich neue Stub-Datensätze zu erzeugen. |
| `A3_DEV` | **Write**. Inventare aus Samedis werden hier angelegt/aktualisiert. Memo-Spalte trägt `created_by=spl-sync` für Nachvollziehbarkeit. |
| `A3_LOCATION`, `A3_TESTER` | **Read-only** (vorerst). Beim ersten Pilot-Kunden klären, ob wir Standorte und Prüfer aus Samedis übernehmen sollen — siehe Roadmap §9.1. |
| `A3_ACTIVITY_KIND` | **Read-only**. Die Tätigkeitsart-Stammdaten kommen mit der Actimed-Installation oder werden vom Dienstleister gepflegt. Der `maintenance_kind_mapping`-Mechanismus mappt Samedis-Wartungstexte auf existierende `KIND_ID`s. |
| `A3_ACTIVITY` | **Write (lazy)**. Falls für eine `KIND_ID` noch keine Tätigkeit existiert, wird beim ersten Bedarf eine angelegt. Die `TEST_SPEC_ID` ist hier der heikle Punkt — siehe unten. |
| `A3_IS_ACT_DEV` | **Write**. Pro offenem Samedis-Issue ein Eintrag mit Fälligkeit (`ACT_DEV_NEXT`). |
| `A3_FINISHED_TEST(_ITEM)(_RESULT)` | **Read-only**. Quelle für den Upload-Pfad und das PNG-Wertenachweis-Rendering. |
| `TEST_SPEC`, `TEST_SPEC_ITEM`, `WS_DEV_FUNC_TEST`, `FUNC_DEV_FUNC_TEST*`, `EVAL_ITEM`, `PARAM_ITEM*`, `PARAM_DSCR_ITEM` | **Read-only / nicht angerührt**. Das ist die Hersteller-Bibliothek + manuelle Pflege durch den Dienstleister. Dort haben wir nichts zu suchen — das ist Geräte-Treiber-Logik. |
| `A3_IS_CUST_DEV` | Verwirrender Name; in den Daten teilweise als Key/Value-Container missbraucht. Wir lassen die Tabelle in Ruhe, primäre Mandantenzuordnung steht in `A3_DEV.CUST_ID`. |

> **Knackpunkt `A3_ACTIVITY.TEST_SPEC_ID`**: wenn das Sync-Tool eine neue
> Tätigkeit für eine bislang unverknüpfte Wartungsart anlegen muss, braucht es
> die ID einer existierenden Prüfvorschrift. Aktuell setzen wir
> `TEST_SPEC_ID = 1` als Platzhalter — das ist falsch und muss beim ersten
> Pilot-Kunden konkret werden. Geplant: das `maintenance_kind_mapping` um ein
> optionales Feld `actimed_test_spec_id` (oder `actimed_test_spec_name`)
> erweitern, sodass der Dienstleister pro Wartungsart explizit angibt, welche
> Prüfvorschrift verwendet werden soll. Default bleibt: existierende
> `A3_ACTIVITY` mit passender `KIND_ID` finden, statt eine neue zu erzeugen.

### 4.1 Wie eine Prüfung in Actimed gestartet wird

Aus Sicht des Technikers (Actimed-Handbuch Kap. 8):

1. `Daten → Prüfobjekt` → Liste aller Inventare.
2. Inventar suchen, F2/Doppelklick → Detail-Dialog.
3. Reiter „Tätigkeiten + Prüfberichte" → die `A3_IS_ACT_DEV`-Einträge des Geräts mit Fälligkeit, plus Prüfberichts-Historie.
4. Tätigkeit auswählen → `Strg+P` startet die Prüfung.
5. Actimed lädt die `TEST_SPEC` der hinterlegten `A3_ACTIVITY`, läuft `TEST_SPEC_ITEM` durch, ConActX redet pro `FUNC_ID` mit dem Messgerät, Werte fließen in die Maske.
6. Speichern → neue `A3_FINISHED_TEST` + Items + Results. `A3_DEV.NEXTACTIVITY_ID` zeigt auf die nächste fällige Tätigkeit (= ihr Eintrag in `A3_IS_ACT_DEV` mit kleinster `ACT_DEV_NEXT`).
7. Falls Modus 1 aktiv: Techniker druckt das Protokoll als PDF in den Pickup-Folder, der Watcher hängt es als Anhang ans Samedis-Issue.

Das `A3_DEV.NEXTACTIVITY_ID` ist die Convenience-Cache-Spalte, die Actimed selbst pflegt, sobald eine Prüfung gespeichert wird. Wir müssen sie beim Anlegen eines `A3_DEV` nicht setzen — leer (oder 0) ist OK, Actimed berechnet sie beim ersten Öffnen des Inventars.

### 4.2 Wichtigste Tabellen & Felder in Actimed

Spaltennamen direkt aus den CSV-Headern (siehe `spl_data/actimed_export_csv/`).
„Mandant" = `A3_CUST`-Eintrag.

| Actimed-Tabelle | Bedeutung | Schlüsselspalten |
| --- | --- | --- |
| `A3_CUST` | **Mandant / Kunde des Dienstleisters** | `CUST_ID`, `CUST_SHORT`, `CUST_NO`, `CUST_NAME1`, `CUST_NAME2` |
| `A3_DEV` | **Asset / Gerät** | `DEV_ID`, `CUST_ID`, `DEV_InventoryNo` (= `inventory_number` / `device_number` in Samedis), `DEV_SerialNo`, `DEV_TYPE_ID`, `LOCATION_ID`, `STATUS_ID`, `NEXTACTIVITY_ID` |
| `A3_DEV_TYPE` | Gerätetyp | `DEV_TYPE_ID`, `MANU_ID`, `DEV_TYPE_NAME`, `DEV_TYPE_Modell`, `DEV_KIND_ID` |
| `A3_DEV_KIND` | Geräteart (DIMDI) | `DEV_KIND_ID`, `DEV_KIND_NAME`, `DEV_KIND_DIMDINR` |
| `A3_MANUF` | Hersteller | `MANU_ID`, `MANU_NAME1`, `MANU_NAME2` |
| `A3_LOCATION` | Standort | `LOCATION_ID`, `LOCATION_NAME` |
| `A3_TESTER` | Prüfer (intern/extern) | `TESTER_ID`, `TESTER_NAME` |
| `A3_ACTIVITY_KIND` | Prüfungsart-Katalog (z. B. `MPBe_STK_Defi_AED_DP-300`, `DGUV V3`) | `KIND_ID`, `KIND_NAME`, `KIND_DSCR` |
| `A3_ACTIVITY` | Konkret hinterlegte Prüfung pro Spec | `ACTIVITY_ID`, `TEST_SPEC_ID`, `KIND_ID`, `ACTIVITY_INTERVAL` (Monate), `ACTIVITY_NAME` |
| `A3_IS_ACT_DEV` | Zuordnung Aktivität → Gerät, mit nächster/letzter Fälligkeit (= **geplante Prüfung**) | `DEV_ID`, `ACTIVITY_ID`, `ACT_DEV_NEXT`, `ACT_DEV_LAST`, `TESTER_ID` |
| `A3_FINISHED_TEST` | **Abgeschlossene Prüfung (= Issue in Samedis)** | `TEST_ID`, `DEV_ID`, `TEST_DATE`, `TESTER_NAME`, `TEST_Pruefberichtsnummer`, `TEST_Pruefergebnis`, `LAST_TEST_DATE`, `NEXT_TEST_DATE`, `PVS_NAME` (Prüfvorschrift-Name = entspricht meist `KIND_NAME`) |
| `A3_FINISHED_TEST_ITEM` | Einzelschritte einer Prüfung | `TEST_ID`, `TEST_ITEM_ID`, `WS_DSCR`, `FUNC_NAME`, `TEST_ITEM_SUCCESS` |
| `A3_FINISHED_TEST_ITEM_RESULT` | Messwerte je Schritt | `TEST_ITEM_RESULT_No`, `TEST_ITEM_ID`, `ITEM_DSCR`, `ITEM_UNIT`, `ITEM_VALUE`, `LIMIT1`, `LIMIT2`, `TEST_ITEM_RESULT_SUCCESS` |
| `A3_DEV_STATUS` | Geräte-Status | `STATUS_ID`, `STATUS_NAME`, `STATUS_DSCR` |
| `A3_IS_CUST_DEV` | (Verwirrend) Mapping Mandant ↔ Gerät; in den Daten teilweise als generischer Key/Value-Container missbraucht — vorsichtig nutzen, primäre Mandantenzuordnung steht in `A3_DEV.CUST_ID`. | — |

**Datumsfelder** in der MDB sind häufig OLE-Auto-Date (z. B. `45278` =
2023-12-18). Beim Einlesen via `OleDbDataReader` sind sie meist schon
`DateTime`; wenn aus CSV gelesen wird, ist Konvertierung über
`DateTime.FromOADate(double)` nötig. Beim Schreiben analog zurück konvertieren.

**Kommawerte** (z. B. Messwerte in `ITEM_VALUE`) verwenden im CSV-Export das
deutsche Komma (`132,3` statt `132.3`). Beim Lesen und beim Erzeugen des
Werteprotokolls (PNG) oder API-Payloads konsequent normalisieren — intern in
`double` mit InvariantCulture rechnen, bei der Anzeige im Werteprotokoll
wieder `de-DE` formatieren.

---

## 5. Sync-Logik

Der Techniker arbeitet je nach Kunden-Setup in einem von zwei Workflow-Modi.
Beide haben den **gleichen Download-Pfad** und unterscheiden sich nur darin,
**wann und wie das Ergebnis nach Samedis hochgeladen wird**. Beide Modi können
zudem gleichzeitig aktiviert sein — der Vorgang in Samedis erhält dann sowohl
das vom Techniker gedruckte PDF als auch ein automatisch generiertes
Wertenachweis-PNG.

### 5.0 Die zwei Workflow-Modi

**Modus 1 — Techniker arbeitet primär in Actimed**

1. Sync-Tool lädt offene Prüfaufträge + Inventarstammdaten von Samedis nach
   Actimed (5.2 + 5.3).
2. Techniker führt die Prüfung in Actimed komplett durch, speichert,
   **druckt das Prüfprotokoll als PDF** in einen konfigurierten Pickup-Ordner.
3. Watcher (5.4.1) erkennt das neue PDF, parst die Prüfberichtsnummer aus dem
   Dateinamen, korreliert sie mit `A3_FINISHED_TEST.TEST_Pruefberichtsnummer`,
   liest die Test-Daten (Prüfergebnis, Kommentar, Tester) und schließt den
   Samedis-Vorgang inkl. PDF-Anhang ab.

Trigger: **Datei taucht im Pickup-Ordner auf** (FileSystemWatcher, ereignisgetrieben).

**Modus 2 — Techniker arbeitet parallel in Samedis (Mobile/Web)**

1. Sync-Tool lädt offene Prüfaufträge + Inventarstammdaten (5.2 + 5.3).
2. Techniker arbeitet die Prüfvorschrift in der Samedis-Web/Mobile-Oberfläche
   ab. Parallel benutzt er in Actimed eine **stark vereinfachte Prüfvorschrift**,
   die ausschließlich die vom Messgerät durchzuführenden Schritte enthält.
3. Sobald die Messung in Actimed gespeichert ist, erzeugt das Sync-Tool aus
   den DB-Einträgen lokal ein **Wertenachweis-PNG** (Logo + Tabelle der
   Schritte mit Soll/Ist/OK), schließt den Samedis-Vorgang ab und hängt das
   PNG an. Das Bild taucht zeitnah in der Samedis-Mobile-Ansicht auf, sodass
   der Techniker direkt am Mobilgerät weiterarbeiten kann.

Trigger: **DB-Polling** (`Sync.UploadPollIntervalSeconds`, Default 30 s).

### 5.1 Gesamtbild

```
                        +------------------+
                        |   Samedis.care   |
                        | (Master fuer     |
                        |  Inventare und   |
                        |  Pflichtprfgen)  |
                        +---+----------+---+
                            |          ^
            DOWNLOAD        |          |  UPLOAD
   alle DownloadInterval-   |          |  Modus 1: PDF (FileWatcher)
   Minutes                  |          |  Modus 2: PNG (DB-Polling, kurz)
   inventories +            |          |
   open maintenance         v          |
   issues per tenant   +-----------------+
                       |  spl-sync.Tray  |
                       |  In-Process     |
                       |  Worker + Watch |
                       |  SQLite-State   |
                       +-+---------+-----+
                         |         ^ ^
                  WRITE  |         | |  READ
   A3_DEV / A3_IS_ACT_DEV|         | |  A3_FINISHED_TEST* (Polling)
                         v         | |
                      +---------------+ |
                      | Actimed (.mdb)| |
                      +---------------+ |
                                        |
                       FILE-EVENT       |
                       neue PDF         |
                      +---------------+ |
                      | Pickup-Folder |-+
                      | (PDF-Drucker  |
                      |  von Actimed) |
                      +---------------+
```

Schritt für Schritt:

1. **Download (Samedis → Actimed)** — pro aktivem Mandant, alle
   `download_interval_minutes`:
   1. Inventar-Kern lesen (siehe 5.2): `inventories` + zugehörige
      `device_models`, `device_types`, `device_manufacturers`. In Actimed
      `A3_MANUF`, `A3_DEV_TYPE`, `A3_DEV_KIND`, `A3_DEV` ggf. anlegen oder
      aktualisieren.
   2. Offene/geplante Prüfungen lesen (siehe 5.3):
      `issues?filter[status]=_new,pending,in_progress&filter[issue_type]=maintenance`.
      Pro Issue ein passendes `A3_IS_ACT_DEV` mit `ACT_DEV_NEXT` und
      `ACTIVITY_ID` einstellen. **Die Wartungsart (z. B. „DGUV V3") wird auf
      eine `A3_ACTIVITY_KIND` gemappt**, damit Actimed bei Start der Prüfung
      automatisch die richtige Prüfvorschrift wählt (siehe 5.5).

2. **Ausführung in Actimed** — passiert *außerhalb* unseres Tools. Modus 1:
   komplette Prüfung in Actimed. Modus 2: nur die Messung. Ergebnisse landen
   in `A3_FINISHED_TEST` + `A3_FINISHED_TEST_ITEM` +
   `A3_FINISHED_TEST_ITEM_RESULT`.

3. **Upload (Actimed → Samedis)** — siehe 5.4. Trigger je nach aktivem Modus:
   FileSystemWatcher (Modus 1) oder DB-Polling (Modus 2).

### 5.1.1 Schreib-Strategie Samedis → Actimed

Eine offizielle API/SDK/COM-Schnittstelle für Drittsoftware **gibt es nicht**
(geprüft gegen `actimed_d_m.pdf` und `export_import_d_m.pdf` — die dort
beschriebenen Import/Export-Funktionen sind ausschließlich für den
Sicherheitstester-Hardware-Datenaustausch und für ACTIMED↔ACTIMED-Client/Server-
Synchronisation gedacht, nicht als Eintrittspunkt für externe Systeme).

**Wir gehen direkt auf die `actimed3db.mdb` via `Microsoft.ACE.OLEDB.16.0`**
und führen `INSERT`/`UPDATE` auf `A3_MANUF`, `A3_DEV_KIND`, `A3_DEV_TYPE`,
`A3_DEV`, `A3_ACTIVITY`, `A3_IS_ACT_DEV` aus. Das Schema ist überschaubar
genug, dass eine eigene Mapping-Schicht weniger Aufwand ist, als das
proprietäre `*.dei`/`*.exp`-Datenaustauschformat des Herstellers zu
reverse-engineeren.

**Lock-Behandlung** (Kap. 7.4 im Actimed-Handbuch — „Datensatzsperren
aufheben"): kein präventives Lock-Probing. Wir setzen den Write ab; fängt der
OleDb-Provider eine exklusive Sperre ab (typische Symptome:
`OleDbException` mit HRESULT `0x80004005`, „File already in use", oder
Konflikt auf `actimed3db.ldb`), dann:

1. Sync pausiert, Job bleibt in der Outbox auf `pending`.
2. Tray-Icon springt auf gelb, Toast-Notification + Modal-Dialog:
   „Actimed bitte kurz schließen, damit der Sync fortfahren kann."
3. User schließt Actimed → klickt im Dialog „Weiter".
4. Wir retryen den Write; bei Erfolg läuft der Sync ohne weiteren Eingriff
   bis zum Ende durch.
5. Falls der User Actimed wieder öffnet, bevor der Sync fertig ist, fängt
   die nächste Operation den Lock ab — Loop wiederholt sich.

Vorteil dieser Strategie: keine Heuristik auf Datei-System-Ebene
(`actimed3db.ldb` ist nicht zuverlässig — kann „verwaist" liegen bleiben),
keine Wartezeit auf falsche Annahmen. Der OleDb-Fehler ist die einzig
verbindliche Quelle der Wahrheit.

### 5.2 Download: Inventar-Kern (Samedis → Actimed)

Pro Mandant ziehen wir genau die Felder, die Actimed minimal braucht, um eine
Prüfung an einem Gerät zu starten:

| Samedis-Feld (`inventories.attributes`) | Actimed-Ziel | Bemerkung |
| --- | --- | --- |
| `device_number` / `inventory_number` | `A3_DEV.DEV_InventoryNo` | Primärer Match-Key. |
| `serial_number` | `A3_DEV.DEV_SerialNo` | |
| `device_model_title` | `A3_DEV_TYPE.DEV_TYPE_NAME` (+ `DEV_TYPE_Modell`) | Modell anlegen, falls neu. |
| `device_model_current_responsible_manufacturer` / `manufacturer` | `A3_MANUF.MANU_NAME1` | Hersteller anlegen, falls neu. |
| `device_type_title` | `A3_DEV_KIND.DEV_KIND_NAME` | „Geräteart" — Mapping siehe 5.5. |
| `device_location_title` (optional) | `A3_LOCATION.LOCATION_NAME` | Falls Property-Mode in Samedis aus ist; sonst nur best-effort als String. |
| `tenant_id` (über `actimed_cust_ids` der Config) | `A3_DEV.CUST_ID` | Aus Mandanten-Mapping. |

Algorithmus: Für jedes Samedis-Inventar in dieser Reihenfolge nachschlagen,
sonst anlegen — `MANU` → `DEV_KIND` → `DEV_TYPE` → `DEV`. IDs kommen aus
`MAX(<id>)+1` der jeweiligen Tabelle (Actimed nutzt durchgehend Integer-IDs,
keine Auto-Inkrement-Spalten). Beim ersten echten Schreibzugriff diese
Annahme an einer realen Kunden-DB verifizieren.

> Wir betrachten Samedis hier als Master für den Inventar-Kern. Ändert ein
> User in Actimed Inventardaten manuell, **gewinnt beim nächsten Download
> Samedis** — wir blenden eine Warnung im Tray-Log ein, schreiben aber nicht
> zurück. Wer Inventardaten in Actimed gepflegt sehen will, pflegt sie in
> Samedis.

### 5.3 Download: geplante Prüfungen (Samedis → Actimed)

Pro Mandant:

```
GET /api/{api_version}/{tenant_scope}/issues
   ?filter[issue_type]=maintenance
   &filter[status]=_new,pending,in_progress
   &gridfilter={"updated_at":{"type":"date","filterType":"greaterThan","value":"<lastrun>"}}
```

Pro gefundenem Issue:

1. `inventory_id` → `device_number` → in Actimed `A3_DEV` über
   `DEV_InventoryNo` finden. Falls fehlt: Inventar wurde in Samedis angelegt,
   ohne dass 5.2 lief → Sync stoppt diesen Job, Tray-Dialog meldet „Inventar
   fehlt in Actimed, jetzt synchronisieren?".
2. Wartungsart-Mapping (siehe 5.5): aus
   `attributes.maintenance_type` / `services` / `title` →
   `A3_ACTIVITY_KIND.KIND_ID`. Daraus eine konkrete `A3_ACTIVITY` (anlegen,
   falls noch nicht vorhanden, mit passender `TEST_SPEC_ID`).
3. `A3_IS_ACT_DEV`-Eintrag schreiben/aktualisieren mit:
   - `DEV_ID`
   - `ACTIVITY_ID`
   - `ACT_DEV_NEXT` ← `attributes.due_on` (oder heute, falls leer)
   - `TESTER_ID` ← Default-Prüfer aus Config oder `responsible_name`-Lookup
4. **Issue-ID merken**: in lokaler SQLite-Tabelle
   `issue_link(samedis_issue_id, samedis_external_id, actimed_dev_id,
   actimed_activity_id, planned_due, downloaded_at)`. Diese Tabelle ist die
   Brücke für den späteren Upload (5.4), weil `A3_FINISHED_TEST` selbst keine
   Samedis-Issue-ID kennt.

### 5.4 Upload: abgeschlossene Prüfung (Actimed → Samedis)

Beide Modi rufen denselben **gemeinsamen Kern** auf: das Samedis-Issue wird
gefunden, mit `status=done` plus den DB-Daten gepatcht (PUT), und ein Anhang
wird als separater POST an `/issues/{id}/uploads` gehängt. Unterschied ist
nur **Trigger** und **Anhang-Typ**.

**Gemeinsamer Kern** (in `UploadEngine.ProcessTest`):

1. **Korrelation zum Samedis-Issue**:
   - Primär: über `issue_link`-Tabelle (5.3) anhand `DEV_ID` +
     `ACTIVITY_ID`.
   - Fallback: Samedis-Suche per
     `gridfilter[external_id]=<TEST_Pruefberichtsnummer>` — falls der Issue
     schon einmal angefasst wurde, hat er diese `external_id`.
   - Wenn nichts greift: neues Issue **nur** anlegen, wenn die Config das
     explizit erlaubt (`create_issues_from_actimed: true`). Default `false`.

2. **PUT** auf das Issue mit:
   ```
   status               "done"
   external_id          <TEST_Pruefberichtsnummer>
   done_at              yyyy-MM-dd  <- TEST_DATE
   date                 yyyy-MM-dd  <- TEST_DATE
   responsible_name     TESTER_NAME
   maintenance_performer TESTER_NAME
   maintenance_type     "maintenance"
   services             [PVS_NAME]   (sonst ["maintenance"])
   title                PVS_NAME
   test_result          passed | passed_conditionally | not_passed
   test_comment         (aus MEMO, optional)
   inventory_operation_status "limited_use"  (nur wenn not_passed UND Config)
   ```

3. **Anhang hochladen**, je nach Trigger.

Mapping `TEST_Pruefergebnis → test_result`:
`bestanden | ok | i.O. → passed`,
`bedingt bestanden | unter Vorbehalt → passed_conditionally`,
`nicht bestanden | durchgefallen | nicht i.O. → not_passed`.

#### 5.4.1 Modus 1 — Trigger via Pickup-Folder (PDF)

Der Watcher (`PdfPickupWatcher`) horcht via `FileSystemWatcher` auf
`actimed.protocol_pdf_dir` und reagiert auf `*.pdf`-Dateien. Pro Event:

1. **Datei-Stabilität abwarten** — manche PDF-Drucker schreiben in Stages.
   Wir warten, bis die Größe ~750 ms konstant bleibt UND die Datei lesbar
   geöffnet werden kann (max. 20 s Timeout).
2. **Prüfberichtsnummer extrahieren** aus dem Dateinamen (Regex `\d{12,}`,
   Actimed-Format z. B. `202312181612401313`).
3. **Test in Actimed finden** durch Vergleich mit
   `A3_FINISHED_TEST.TEST_Pruefberichtsnummer`. Suchfenster: 30 Tage rückwärts.
4. Wenn gefunden: **gemeinsamer Kern** + PDF als `data[document]` an
   `/issues/{id}/uploads` (Fallbacks: `data[file]`, `data[image]`).
5. Wenn der Test in Actimed noch nicht da ist (selten): überspringen, nächste
   Polling-Runde von Modus 2 holt's eh ein, falls auch der aktiv ist.

Voraussetzung beim Kunden, einmalig in Actimed einzustellen:

- `Extras → Optionen → Name des Druckauftrags = Inventarnummer_Prüfberichtsnummer`
  (Actimed-Handbuch Kap. 7.3) — damit Druck-Dateinamen die
  `Pruefberichtsnummer` enthalten.
- PDF-Drucker mit festem Output-Verzeichnis, der ohne weiteres
  „Speichern unter"-Dialog automatisch in `protocol_pdf_dir` schreibt
  (`PDFCreator` mit Auto-Save-Profil; alternativ `Microsoft Print to PDF`
  mit konfiguriertem Default-Pfad).

#### 5.4.2 Modus 2 — Trigger via DB-Polling (PNG)

Eine kurze Polling-Schleife alle `upload_poll_interval_seconds` (Default 30 s):

1. `MAX(MODIFYTIME)` der bisher hochgeladenen Tests als Cursor lesen.
2. Aus `A3_FINISHED_TEST` alle neueren Zeilen ziehen (gefiltert auf
   `CUST_ID IN (...)` der konfigurierten Mandanten-Mapping).
3. Pro Test:
   - **gemeinsamer Kern** (siehe oben).
   - PNG-Wertenachweis lokal rendern (5.6) aus
     `A3_FINISHED_TEST_ITEM` + `A3_FINISHED_TEST_ITEM_RESULT`.
   - PNG als `data[image]` an `/issues/{id}/uploads` hochladen — bei PNGs
     bevorzugt, weil Samedis Bild-Uploads direkt ins finale PDF-Protokoll
     einbettet.
4. Cursor auf `MAX(MODIFYTIME)` der erfolgreich verarbeiteten Tests vorrücken.

**Latenz**: Schritt-Trigger bis Bild im Samedis-Vorgang typisch 30-60 s.
Wenn der Techniker schneller sein will, `upload_poll_interval_seconds` auf 5-10 s.

#### 5.4.3 Beide Modi gleichzeitig

Wenn `upload_mode_pdf_pickup` UND `upload_mode_png_on_completion` aktiv sind:

- Modus 2 hängt das PNG **sofort** an, sobald der Test in der DB landet.
- Modus 1 hängt das PDF **später** an, sobald der Techniker druckt.

Das ist explizit erlaubt — Samedis speichert Uploads unabhängig, der Vorgang
trägt am Ende beide Anhänge. Der `status=done`-PUT wird zweimal abgesetzt mit
identischem Inhalt; das ist idempotent und unproblematisch.

### 5.5 Wartungsart-Mapping (Samedis ↔ Actimed)

Samedis hat ein freies `services`-String-Array und meist einen Issue-Titel
wie „STK nach DGUV V3" oder „MTK BDM 24 Monate". Actimed kennt kuratierte
`A3_ACTIVITY_KIND.KIND_NAME`-Werte (z. B. `MPBe_§11_STK/DGUV V3`,
`MPBe_MTK_BDM`, `MPBe_STK Defi (AED)`, `MPBe_§7_Wartung/Inspektion`).

Damit Actimed beim Start der Prüfung automatisch die richtige Prüfvorschrift
zieht, brauchen wir eine **Mapping-Tabelle in der Config**:

```yaml
maintenance_kind_mapping:
  # erste Treffer-Regex gewinnt; case-insensitive auf
  # services + title + maintenance_type kombiniert
  - match: "(?i)dguv\\s*v?3|stk.*§11"
    actimed_kind: "MPBe_§11_STK/DGUV V3"
  - match: "(?i)mtk.*bdm|blutdruck"
    actimed_kind: "MPBe_MTK_BDM"
  - match: "(?i)defi.*aed"
    actimed_kind: "MPBe_STK Defi (AED)"
  - match: "(?i)wartung|inspektion|§\\s*7"
    actimed_kind: "MPBe_§7_Wartung/Inspektion"
  default: "MPBe_§7_Wartung/Inspektion"
```

Die GUI ermöglicht Editieren der Mapping-Liste und zeigt — nach einem
Probelauf — welche Samedis-Issues auf welchen `KIND_NAME` gemappt würden.
Unzuordenbare Issues landen im Tray-Dialog als „bitte Mapping ergänzen".

### 5.6 Werteprotokoll als PNG (primär)

Samedis baut sein finales PDF-Issue-Protokoll selbst aus den Issue-Attributen
und bettet **Bilder** ein, die per `/issues/{id}/uploads` hochgeladen wurden.
Daher ist die kostengünstigste und am besten integrierte Variante:

- Wir lesen aus Actimed:
  - `A3_FINISHED_TEST` (Header)
  - `A3_FINISHED_TEST_ITEM` (Prüfschritt-Zeilen, `WS_DSCR`, `FUNC_NAME`,
    `TEST_ITEM_SUCCESS`)
  - `A3_FINISHED_TEST_ITEM_RESULT` (Messwerte: `ITEM_DSCR`, `ITEM_UNIT`,
    `ITEM_VALUE`, `LIMIT1`, `LIMIT2`, `TEST_ITEM_RESULT_SUCCESS`)
- Wir rendern lokal ein PNG mit:
  - **Header**: Logo des Dienstleisters (aus Config-Pfad), Titel
    „Werteprotokoll", Prüfberichtsnummer, Gerät, Prüfdatum, Prüfer.
  - **Tabelle** mit Spalten: `Schritt | Soll-Bereich | Einheit | Ist-Wert | OK?`.
    Eine Zeile pro `A3_FINISHED_TEST_ITEM_RESULT`-Eintrag.
    Soll-Bereich = `LIMIT1 … LIMIT2` (oder „—" wenn beide gleich/0).
    OK-Spalte = grünes Häkchen / rotes Kreuz aus `TEST_ITEM_RESULT_SUCCESS`.
  - **Footer**: Gesamtergebnis aus `TEST_Pruefergebnis`, optional QR-Code mit
    Issue-Link.
- Renderer: **SkiaSharp** (`SkiaSharp.Views.Desktop`) ist die solide Wahl —
  schnell, plattformunabhängig, produziert deterministische PNGs ohne Druck-
  Treiber-Frickelei. Schriftgrößen so wählen, dass das PNG bei A4-Druck noch
  lesbar ist (Zielauflösung 1654 × 2339 Pixel = 200 dpi @ A4).
- Speichern als `<external_id>_werteprotokoll.png` und hochladen:
  ```
  POST /api/{api_version}/{tenant_scope}/issues/{issue_id}/uploads
  multipart/form-data
  data[name]    = "<external_id>_werteprotokoll.png"
  data[image]   = @<pfad>     (PNG → image)
  ```
  Bei PNGs **bevorzugt direkt `data[image]`** (das Werteprotokoll *ist* das
  Bild, das Samedis ins finale PDF einbettet) — `data[document]` würde es
  unter Umständen als Anhang behandeln statt als eingebettete Bildseite.

### 5.7 Voll-PDF aus Actimed (alternativ/zusätzlich)

Optional kann der Kunde wollen, dass das **komplette Actimed-Druckprotokoll**
am Issue hängt (z. B. weil sein QM-System genau das vorschreibt). Dann
zusätzlich:

- **Voraussetzung beim Kunden** (manuell einmalig in Actimed einzustellen):
  - `Extras → Optionen → Name des Druckauftrags` auf
    `Inventarnummer_Prüfberichtsnummer` setzen (oder einen anderen
    deterministischen Namen, der die `TEST_Pruefberichtsnummer` enthält). Das
    ist eine vom Hersteller dokumentierte Option (siehe Actimed-Handbuch
    Kap. 7.3). Damit erzeugt Actimed beim Druck Dateinamen, die wir
    matchen können.
  - Als Drucker einen PDF-Drucker mit festem Output-Verzeichnis konfigurieren
    (z. B. `Microsoft Print to PDF` mit Default-Zielverzeichnis, `PDFCreator`
    mit Auto-Save-Profil oder `CutePDF Writer` mit gepatchtem Output-Pfad).
- Speicherort des Actimed-PDFs konfigurieren
  (`actimed.protocol_pdf_dir`, Default `Documents\Actimed\Reports`).
- Glob-Suche `*<TEST_Pruefberichtsnummer>*.pdf`. Sobald gefunden, hochladen:
  ```
  data[name]      = "<external_id>.pdf"
  data[document]  = @<pfad-zur-pdf>     (Fallbacks: data[file], data[image])
  ```
- Konfiguration steuert das Verhalten:
  ```yaml
  sync:
    upload_mode_pdf_pickup: true          # Modus 1 — PDF aus Actimed-Druck
    upload_mode_png_on_completion: false  # Modus 2 — PNG aus DB-Werten
  ```
- Wenn beide aktiv: PNG (Modus 2) wird sofort beim DB-Eintrag angehaengt,
  PDF (Modus 1) kommt spaeter dazu, sobald der Techniker druckt. Der Vorgang
  in Samedis traegt am Ende beide Anhaenge.

> Der Druck wird vom Techniker manuell ausgelöst — Actimed bietet keine
> CLI/COM-Schnittstelle für automatisches PDF-Erzeugen. Falls der Kunde nicht
> bereit ist, nach jeder Prüfung einen Druck-Schritt zu machen, ist Modus 2
> (PNG-Wertenachweis aus DB-Daten, 5.6) der einzige zuverlässige Weg.

### 5.8 Inkrementeller Cursor

- Pro Mandant zwei Dateien unter
  `%PROGRAMDATA%\SamedisCare\SplSync\state\<tenant_id>\`:
  - `lastrun_download.txt` — letzter Download-Zeitpunkt.
  - `lastrun_upload.txt` — letzter Upload-Zeitpunkt.
- Fallback bei fehlender/kaputter Datei: `2022-01-01T00:00:00.000+01:00`.
- Cursor-Quelle für Upload: `MAX(MODIFYTIME)` der `A3_FINISHED_TEST`-Zeilen,
  die in diesem Lauf hochgeladen wurden — als OLE-Auto-Date, vor dem Schreiben
  in ISO konvertieren.
- Cursor-Quelle für Download: `meta.updated_at` aus den Samedis-Antworten —
  nach erfolgreichem Persistieren in Actimed schreiben.

### 5.9 Offline-Verhalten / Outbox + Inbox

- **Outbox** (Upload, Actimed → Samedis): SQLite-Tabelle
  `outbox(id, tenant_id, test_id, payload_json, png_path, pdf_path, status,
  attempts, last_error, created_at, updated_at)`. Status-Werte: `pending`,
  `issue_synced`, `pdf_pending`, `synced`, `failed`, `skipped`.
- **Inbox** (Download, Samedis → Actimed): SQLite-Tabelle
  `inbox(id, tenant_id, samedis_issue_id, samedis_payload_json, status,
  attempts, last_error, created_at, updated_at)`. Status-Werte: `pending`,
  `applied`, `failed`, `skipped` (z. B. `unknown_inventory`).
- Vor jedem API-Call prüfen, ob Tokenholen geht; bei Netzwerkfehler:
  Job in lokaler State-DB als `pending` halten und beim nächsten Tick
  retryen.
- Idempotenz: `external_id = TEST_Pruefberichtsnummer` ist eindeutig pro Test
  → mehrfaches PUT/POST darf zu einem Update mappen, nicht zu Duplikaten. Falls
  die API beim POST mit `external_id` einen Duplikat-Fehler wirft: Lookup über
  `gridfilter[external_id]`, dann PUT.

### 5.10 Stamm- vs. Bewegungsdaten

- **In Samedis legen wir keine Inventories oder Issues an**, solange das nicht
  per Config explizit erlaubt ist (`create_issues_from_actimed`,
  `create_inventories_from_actimed` — beide Default `false`). Wenn etwas
  fehlt, geht das in den Tray-Dialog und wartet auf Useraktion.
- **In Actimed legen wir bei Bedarf an**, wenn Samedis ein Inventar liefert,
  das es lokal noch nicht gibt — sonst kann der Techniker keine Prüfung
  starten. Diese Logik ist konservativ: bei jedem Anlegen einen Eintrag
  `created_by=spl-sync` ins `A3_DEV.DEV_Memo` schreiben, damit Konflikte
  nachvollziehbar sind.

---

## 6. Samedis-API-Referenz (für diesen Zweck relevanter Ausschnitt)

### 6.1 Authentifizierung

```
POST {auth.uri}/api/v1/samedis.care/oauth/token
Content-Type: application/x-www-form-urlencoded

grant_type=password
email={client_id}
password={client_secret}
```

Antwort enthält in `meta.token` den **Bearer-Token** und in `meta.refresh_token`
den Refresh-Token. Auf jeden Folgeaufruf den Token als `Authorization: Bearer …`
mitgeben.

### 6.2 Tenant-Settings

```
GET /api/{api_version}/user/tenants/{tenant_id}
```

Liefert u. a. `attributes.use_extended_device_locations` und
`attributes.use_profit_centers`. Brauchen wir, um beim Inventar-Download
korrekt zu wissen, ob `device_location` als ID oder als
Building/Floor/Room-Hierarchie zu interpretieren ist.

### 6.3 Inventory-Lookup nach `device_number`

```
GET /api/{api_version}/{tenant_scope}/inventories
   ?page[number]=1
   &page[limit]=1
   &gridfilter={"device_number":{"type":"text","filterType":"equals","value":"<DEV_InventoryNo>"}}
```

(`gridfilter` ist URL-codiertes JSON; siehe `FilterBuilder.cs` der Referenz.)

Ergebnis: `data[0].id` ist die `inventory_id`.

### 6.4 Inventories-Liste (Download 5.2)

```
GET /api/{api_version}/{tenant_scope}/inventories
   ?page[number]=1&page[limit]=200
   &gridfilter={"updated_at":{"type":"date","filterType":"greaterThan","value":"<lastrun_download>"}}
```

Paginierung über `meta.total` + `page[number]++`. Felder, die uns
interessieren, stehen in 5.2.

### 6.5 Issues lesen (Download 5.3)

```
GET /api/{api_version}/{tenant_scope}/issues
   ?filter[issue_type]=maintenance
   &filter[status]=_new,pending,in_progress
   &page[number]=1&page[limit]=200
   &gridfilter={"updated_at":{"type":"date","filterType":"greaterThan","value":"<lastrun_download>"}}
```

### 6.6 Issue Upsert (Upload 5.4)

- **Suche existierenden Issue:**
  `GET /…/issues?gridfilter={"external_id":{…,"value":"<TEST_ID>"}}`
- **Anlegen** (nur, wenn `create_issues_from_actimed=true`):
  `POST /…/issues` mit Body `{"data":{"type":"issues","attributes":{…}}}`
- **Update:** `PUT /…/issues/{id}` mit demselben Schema.

Erwartete Attribute (Quelle: `Tasks.cs` der Referenz):

```
inventory_id         (string)        ← aus Inventory-Lookup / issue_link
issue_type           "maintenance"   ← konstant für unseren Use-Case
status               "_new" | "pending" | "in_progress" | "done"
external_id          (string)        ← TEST_Pruefberichtsnummer
maintenance_type     "maintenance"
maintenance_performer (string)       ← TESTER_NAME
services             string[]        ← [PVS_NAME] oder ["maintenance"]
title                (string)        ← PVS_NAME
date                 yyyy-MM-dd      ← TEST_DATE
done_at              yyyy-MM-dd      ← TEST_DATE
responsible_name     (string)        ← TESTER_NAME
test_comment         (string, opt.)
test_result          "passed" | "passed_conditionally" | "not_passed"
inventory_operation_status (opt.)    ← "limited_use" wenn fehlgeschlagen
```

Mapping `TEST_Pruefergebnis → test_result`:
`bestanden|ok|i.O. → passed`,
`bedingt bestanden → passed_conditionally`,
`nicht bestanden|durchgefallen|nicht i.O. → not_passed`.

### 6.7 Datei-Upload (PNG / PDF)

```
POST /api/{api_version}/{tenant_scope}/issues/{issue_id}/uploads
Content-Type: multipart/form-data

data[name]      = "<external_id>_werteprotokoll.png" | "<external_id>.pdf"
data[image]     = @<pfad-zum-png>      ← bevorzugt für PNG-Werteprotokolle
data[document]  = @<pfad-zur-pdf>      ← bevorzugt für Voll-PDF
                  (Fallbacks: data[file], data[image])
```

Wenn der File-Upload fehlschlägt, **Issue nicht zurückrollen** — separat
retryen (State `issue_synced`, `pdf_pending`).

### 6.8 Rate-Limits / Retry

`429 Too Many Requests` → `Retry-After`-Header respektieren (Referenz:
`HandleRetry` in `Samedis.cs`). Sonst: einfacher exponentieller Backoff,
maximal n Versuche, dann State = `failed` mit letzter Fehlermeldung.

---

## 7. Architektur des .NET-Projekts

### 7.1 Stack

- **.NET 8** (LTS), Sprache C# 12.
- **GUI / Tray**: WPF (`System.Windows.Forms.NotifyIcon` für Tray geht auch
  aus WPF-Apps; alternativ `H.NotifyIcon.Wpf`-Paket für moderneres Tray).
  Empfehlung WPF wegen sauberer Datenbindung; alternativ Avalonia, wenn
  später Multi-Plattform gewünscht.
- **HTTP**: `RestSharp` + `Newtonsoft.Json`, identisch zur Referenz, damit wir
  die HTTP-Bausteine 1:1 übernehmen können.
- **Access-Zugriff**: `System.Data.OleDb` mit `Microsoft.ACE.OLEDB.16.0`.
  Über ein `IActimedRepository`-Interface gekapselt, sodass Tests gegen
  SQLite (siehe 3.3) laufen können.
- **Lokale State-DB / Outbox + Inbox**: SQLite (`Microsoft.Data.Sqlite`).
- **PNG-Erzeugung**: `SkiaSharp` (`SkiaSharp.Views.Desktop` falls Schriften
  via System nötig; sonst nur `SkiaSharp` core mit eingebetteten TTFs).
- **PDF-Erzeugung (Fallback, falls Actimed-PDF nicht verfügbar)**: QuestPDF
  (MIT/Community).
- **Worker im Tray-Prozess**: drei parallele Schleifen in `InProcessSyncHost`:
  Download-Loop (langes Intervall), Upload-Polling-Loop (kurzes Intervall, für
  Modus 2), und ein FileSystemWatcher (für Modus 1). Kein separater Dienst.
- **FileSystemWatcher**: `System.IO.FileSystemWatcher` für Modus 1, mit
  Datei-Stabilitäts-Check (Größe konstant für 750ms + lesbar öffnen) bevor
  ein Upload getriggert wird.

### 7.2 Projekt-Layout

```
src/
  SamedisCare.SplSync.Core/        # API-Modelle, HTTP, Mapping (testbar, ohne UI)
    Api/
      Authenticate.cs
      RequestData.cs
      FilterBuilder.cs
      Tenant.cs
      Issues.cs                    # Tasks.cs umbenannt: bei uns geht's um Issues
      Inventories.cs
      Helper.cs
      Logging.cs
      HttpSettings.cs
    Actimed/
      IActimedRepository.cs        # abstrahiert OleDb vs. SQLite
      OleDbActimedRepository.cs    # Produktion (Windows-only zur Laufzeit)
      SqliteActimedRepository.cs   # Tests / macOS-Dev
      ActimedDtos.cs
      OaDate.cs                    # FromOADate Helper, deutsche Komma-Norm.
    Sync/
      DownloadEngine.cs            # 5.2 + 5.3
      UploadEngine.cs              # 5.4 — RunPngOnCompletion + RunPdfPickup
      PdfPickupWatcher.cs          # 5.4.1 — FileSystemWatcher um protocol_pdf_dir
      WerteprotokollRenderer.cs    # SkiaSharp -> PNG (5.6)
      MaintenanceKindMapper.cs     # 5.5
      ResultMapping.cs             # TEST_Pruefergebnis -> test_result
      Cursor.cs / IssueLink.cs     # SQLite-State (StateDb)
      StateDb.cs                   # state.sqlite Schema + Open
      SyncContext.cs               # tenant-spezifische Composition
    Config/
      AppConfig.cs                 # Multi-Tenant-Config inkl. Modus-Schalter
      ConfigStore.cs               # YAML laden+schreiben + DPAPI-Secrets

  SamedisCare.SplSync.Tray/        # Tray-EXE, WPF Konfig-Fenster, Worker
    App.xaml(.cs)                  # Tray-Icon (NotifyIcon), Lifecycle
    InProcessSyncHost.cs           # 3 Loops: Download, Upload-Poll, PdfWatcher
    TrayStatus.cs                  # gemeinsamer Status-State
    Views/MainWindow.xaml          # Status, Mandanten, Mapping, Fehler
    Dialogs/LockDialog.xaml        # "Actimed bitte schliessen"

tests/
  SamedisCare.SplSync.Core.Tests/  # xUnit, läuft auf macOS gegen SQLite
```

Es gibt **keinen** Service-EXE. Der ursprünglich vorgesehene
`SamedisCare.SplSync.Service` wurde entfernt — siehe Begründung in der
Auslieferungs-Anmerkung in Kap. 1.

### 7.3 Tray-Verhalten

- **Status-Symbole** im Tray:
  - grau: idle / nichts zu tun
  - blau, animiert: läuft gerade
  - grün: letzter Lauf erfolgreich
  - gelb: Warnungen (z. B. einzelne Skips)
  - rot: Fehler — Klick öffnet Hauptfenster mit Fehlerliste
- **Kontextmenü**: „Jetzt synchronisieren", „Letzten Lauf öffnen",
  „Konfiguration", „Logs öffnen", „Beenden".
- **Push-Dialoge** (Toast-Notification + ggf. Modal-Window) bei:
  - Token abgelaufen / Anmeldung fehlgeschlagen → Re-Login-Dialog.
  - ACE-OLEDB-Treiber fehlt → Hinweis mit Download-Link.
  - Inventar in Samedis vorhanden, in Actimed fehlt und Auto-Anlegen ist aus.
  - Wartungsart-Mapping konnte ein Issue keinem `KIND_NAME` zuordnen.
  - Mehrdeutiges Issue beim Upload (mehrere Kandidaten).
- **Stilles Verhalten** sonst: keine Toasts, nur Tray-Farbe + Hauptfenster.

### 7.4 Konfigurationsdatei

Die Referenz nutzt `config.yml`. Wir bleiben kompatibel, **erweitern** aber um
eine **Mandanten-Liste**, Wartungsart-Mapping und Anhänge-Schalter. Vorschlag:

```yaml
auth:
  uri: "https://ident.services"
  client_id: "sync-account@dienstleister.example"
  client_secret: "***"

samedis:
  uri: "https://sync.samedis.care"
  api_version: "v4"

actimed:
  database_path: "C:\\Program Files (x86)\\Actimed3\\actimed3db.mdb"
  protocol_pdf_dir: "C:\\Users\\Public\\Documents\\Actimed\\Reports"
  use_local_snapshot: true        # Datei vor Read kopieren, dann lesen
  default_tester_name: "Servicetechniker"

branding:
  service_provider_name: "Mein Dienstleister GmbH"
  logo_path: "C:\\ProgramData\\SamedisCare\\SplSync\\logo.png"

tenants:
  - name: "Mandant 1"
    samedis_tenant_id: "<24-stelliger ObjectId aus Samedis>"
    actimed_cust_ids: [4, 7]         # CUST_ID(s) in Actimed
    enabled: true
  - name: "Mandant 2"
    samedis_tenant_id: "<…>"
    actimed_cust_ids: [5]
    enabled: true

sync:
  download_interval_minutes: 15
  upload_poll_interval_seconds: 30
  run_on_network_reconnect: true
  download_inventories: true
  download_open_issues: true
  upload_finished_issues: true        # Master-Schalter

  # Upload-Modi (5.4.1 / 5.4.2)
  upload_mode_pdf_pickup: false       # Modus 1 — FileWatcher auf protocol_pdf_dir
  upload_mode_png_on_completion: true # Modus 2 — DB-Polling + PNG-Wertenachweis

  # Erlaubt es, in Samedis neue Issues anzulegen, falls in Actimed eine
  # Prüfung existiert, die in Samedis nie geplant wurde. Default: nein.
  create_issues_from_actimed: false
  create_inventories_from_actimed: false
  set_inventory_operation_status_on_failed_maintenance: false

maintenance_kind_mapping:
  - match: "(?i)dguv\\s*v?3|stk.*§11"
    actimed_kind: "MPBe_§11_STK/DGUV V3"
  - match: "(?i)mtk.*bdm|blutdruck"
    actimed_kind: "MPBe_MTK_BDM"
  - match: "(?i)defi.*aed"
    actimed_kind: "MPBe_STK Defi (AED)"
  - match: "(?i)wartung|inspektion|§\\s*7"
    actimed_kind: "MPBe_§7_Wartung/Inspektion"
  default: "MPBe_§7_Wartung/Inspektion"

logging:
  level: 1     # 0 off, 1 info, 2 debug
  mode: 3      # 0 none, 1 console, 2 file, 3 console+file
  directory: "%PROGRAMDATA%\\SamedisCare\\SplSync\\logs"

http:
  valid_certificate: true
  proxy: ""
  proxy_username: ""
  proxy_password: ""
```

Die GUI editiert dieselbe Datei. Bei Bedarf zweite Auslieferungsform als JSON
unterstützen — primär bleibt YAML, weil die Referenz das so macht und
Operations-Kollegen es bereits kennen.

**Secret-Handling**: `client_secret` und `proxy_password` werden, sobald via
GUI gesetzt, mit DPAPI (`ProtectedData.Protect(…, DataProtectionScope.LocalMachine)`)
verschlüsselt und in der YAML als `enc:<base64>` abgelegt. Beim Lesen
transparent entschlüsseln. Plain-Werte werden weiter akzeptiert für
manuelle/erste Konfiguration.

### 7.5 Mandanten-Mapping

Eine zentrale Designfrage: ein Actimed-`CUST_ID` ↔ ein Samedis-`tenant_id`?
In der Praxis kann ein Mandant in Actimed mehrere `CUST_ID`-Einträge haben
(historisch, Standorte). Daher `actimed_cust_ids: int[]` pro Tenant. `A3_DEV`-
Zeilen werden über `CUST_ID IN (…)` selektiert; beim Anlegen aus Samedis
nehmen wir den **ersten** Eintrag der Liste (mit Kommentar im Memo, dass das
Mapping ggf. korrigiert werden muss).

### 7.6 Logging & Observability

- Strukturiertes Logging via `Serilog` in Datei (JSON) + Konsole.
- Alle API-Calls loggen mit Resource, Status, ms; **niemals** Token oder
  Secrets unmaskiert (siehe `Redact()` in der Referenz).
- Pro Mandanten-Sync-Lauf eine Zusammenfassung: total, neu erstellt,
  aktualisiert, übersprungen, fehlgeschlagen, PNGs/PDFs hochgeladen.
- Tray-Hauptfenster zeigt diese Zusammenfassungen als chronologische Liste,
  mit Klick-zu-Detail.

### 7.7 Build & Verteilung

```
dotnet publish src/SamedisCare.SplSync.Tray -c Release -r win-x64 \
    --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

→ Ergebnis: eine EXE `SamedisCare.SplSync.Tray.exe`. Sie ist die einzige
Auslieferung. State-DB (`state.sqlite`) und Logs liegen unter
`%PROGRAMDATA%\SamedisCare\SplSync\`. Cross-Compile von macOS aus funktioniert.

Für die Auto-Start-Erfahrung: `Tray.exe` einmal manuell starten und dann in
der Windows-Aufgabenplanung oder im Autostart-Ordner verlinken — der
Techniker hat das Tray-Icon dann nach jedem Login automatisch.

Optional später ein MSI/MSIX-Installer (WiX); im ersten Schritt reicht ein
ZIP + Verknüpfung in den Autostart-Ordner.

---

## 8. Code-Konventionen für dieses Repo

- **Sprache der Doku & Commits:** Deutsch erlaubt, Code-Bezeichner und API-
  Felder bleiben **englisch** und in `snake_case` für JSON, `PascalCase` für
  C#-Properties (Newtonsoft `[JsonProperty("snake_case")]`).
- **HTTP-Bausteine** lehnen sich namentlich an die Referenz an (`Authenticate`,
  `RequestData`, `FilterBuilder`, `Helper`), damit Code-Reviewer:innen, die das
  Referenzprojekt kennen, sich sofort zurechtfinden.
- **Keine Wegwerf-Skripte** im Repo-Root: alles unter `src/`, `tests/`,
  `tools/`, `spl_data/` (read-only Beispiele) oder `docs/`.
- **Keine echten Credentials** committen — die Beispiel-`config.yml` ist
  immer `config.yml.example` und liegt unter `src/SamedisCare.SplSync.Tray/`.

---

## 9. Was hier (noch) **nicht** gebaut wird

- Kein Sync für `requests`, `trainings`, `departments`, `locations` als
  separater Pfad — das macht weiter `samedis-care-external-sync`.
- Kein **Schreiben in Samedis** für Stammdaten (Inventories, DeviceModels,
  Locations) — nur Lesen für 5.2.
- Keine Bearbeitung von Prüfvorschriften (`TEST_SPEC*`) in Actimed — wir
  legen lediglich `A3_ACTIVITY`-Verknüpfungen zu existierenden Specs an.
- Kein Sync von `A3_FINISHED_TEST_ITEM_RESULT` als strukturierte Messwerte
  in eigene Samedis-Felder — die Detail-Ergebnisse landen ausschließlich im
  Werteprotokoll-PNG (5.6) und/oder Actimed-Voll-PDF (5.7).
- **Kein DEE-/DEI-/EXP-basierter Datenaustausch** — der Hersteller-Pfad
  (Actimed-Handbuch Kap. 7.6/7.7) ist proprietär, formatlos dokumentiert und
  setzt manuelle Import-Trigger in der Actimed-UI voraus. Wir gehen direkt
  auf die MDB (siehe 5.1.1).

---

## 9.1 Offene Recherche-Punkte

Diese Punkte stehen aus Mangel an Zugang zu einer realen Actimed-Installation
noch offen. Alle sind beim Onboarding des ersten Pilot-Kunden zu klären:

- **PDF-Druck-Setup** beim Kunden (für 5.7): welcher PDF-Drucker, welches
  Output-Verzeichnis, ob `Inventarnummer_Prüfberichtsnummer` als Druckauftrag-
  Name aktiv ist (Actimed Extras → Optionen). Nur relevant, wenn der Kunde
  den Voll-PDF-Anhang neben dem PNG-Werteprotokoll möchte.
- **Konkrete OleDb-Fehlersignatur**, die Actimed wirft, wenn die DB
  exklusiv offen ist: HRESULT-Code, Message-Text, Wiederhol-Verhalten. Wird
  beim ersten Pilot-Kunden eingefangen, damit der Lock-Detection-Pfad in
  5.1.1 sauber matchen kann.
- **ID-Vergabe in Actimed**: ist `MAX(<id>)+1` über die A3_*-Tabellen wirklich
  die Konvention, oder gibt es Counter-Tabellen (vergleichbar Hibernate
  `hibernate_sequence`)? Beim ersten Insert verifizieren.
- **Mandanten-Mapping**: ein Samedis-Tenant kann an mehrere Actimed-`CUST_ID`
  hängen — gibt es Kunden, die das brauchen, oder reicht 1:1?

---

## 10. Anhang: Datei-Inventar im Repo

| Pfad | Was es ist |
| --- | --- |
| `spl_data/actimed3db.mdb` | Beispiel-Access-DB (Stand 09/2025) |
| `spl_data/actimed3db_v427.zip` | Ältere Version (4.27) als Referenz |
| `spl_data/actimed_export_csv/*.csv` | Voll-Export der relevanten A3_*-Tabellen, mit `mdb-export` von macOS aus erzeugt |
| `spl_data/actimed_export_csv/csv2sqlite.py` | Hilfs-Skript: CSVs → SQLite (für lokale Analyse + Tests) |
| `spl_data/actimed_export_csv/*.sqlite` | Daraus erzeugte SQLite-Snapshot-DB |
| `spl_data/SPL/actimed_d_m.pdf` | Actimed-Software-Handbuch (Datenmodell, PDF-Export-Schnittstelle) |
| `spl_data/SPL/export_import_d_m.pdf` | Doku zu Import/Export-Formaten in Actimed |
| `spl_data/SPL/designer_m.pdf` | Prüfplan-Designer (TEST_SPEC, A3_ACTIVITY) |
| `spl_data/SPL/{loyhutz,cx,cxtm,synactx}_d_m.pdf` | Allgemein-/Software-Varianten-Handbücher |
| `spl_data/SPL/{dp300,dp600,es300,gm100,hsd,sicherheitstester_anhang,kurzanleitung022}*.pdf` | Messgeräte-Bedienungsanleitungen — für Sync nicht relevant |
| `spl_data/SPL/acc_*.zip` | Accessoires-Doku (DE/EN) |
| `spl_data/image *.png` | Screenshots Actimed-UI (Referenz fürs Mapping) |
| `spl_data/59107959-actimed3db.csv.xlsx` | Excel-Variante des CSV-Dumps |

---

_Stand: 2026-05-06_
