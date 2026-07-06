SamedisCare.SplSync - Deployment- und Konfigurationshandbuch
============================================================

Diese Datei buendelt alle Installations- und Konfigurationsdetails fuer den
Betrieb auf dem Windows-Notebook. Hintergrund zum Datenmodell und zur
Sync-Logik (inkl. Flussdiagramme) steht in der ausfuehrlichen README.md im
Quell-Repository.


INHALT DES PAKETS
=================
  SamedisCare.SplSync.Tray.exe   Single-File EXE (self-contained .NET 8, win-x64)
  SamedisCare.SplSync.Tray.pdb   Debug-Symbole (optional, fuer Crash-Reports)
  config.yml.example             Template-Config, vor erstem Start ausfuellen
  README.txt                     Diese Datei


1. VORAUSSETZUNG (PFLICHT): ACE OLE-DB x64
==========================================
Auf dem Windows-Notebook muss der x64-OLE-DB-Provider fuer Access installiert
sein, sonst kann die EXE die actimed3db.mdb nicht oeffnen. Actimed bringt nur
die 32-bit-Engine mit; die x64-EXE braucht die 64-bit-Variante daneben.

  1. Download:
     https://www.microsoft.com/en-us/download/details.aspx?id=54920

  2. Stille Installation (Pflicht, sonst bricht der grafische Installer wegen
     der vorhandenen 32-bit-Engine ab):

         AccessDatabaseEngine_X64.exe /quiet

  3. Verifizieren, dass der Provider registriert ist:

         powershell -ExecutionPolicy Bypass -Command "
           (New-Object System.Data.OleDb.OleDbEnumerator).GetElements() |
             Where-Object { $_.SOURCES_NAME -like '*ACE*' } |
             Select-Object SOURCES_NAME, SOURCES_DESCRIPTION"

     Erscheint 'Microsoft.ACE.OLEDB.16.0', ist alles bereit.

Fehlt der Provider, meldet die App beim ersten DB-Zugriff einen klaren Hinweis
mit Download-Link (HRESULT 0x80131501 / "Provider nicht registriert").


2. SCHNELLSTART
===============
  1. Diesen Ordner an einen festen Ort kopieren, z.B. C:\spl-sync\
     (Pfad danach nicht mehr verschieben - siehe Autostart, Abschnitt 8).
  2. EXE einmal per Doppelklick starten. Beim ersten Start:
       - Willkommens-Hinweis
       - config.yml wird aus der eingebetteten Vorlage neben der EXE angelegt
       - das Hauptfenster oeffnet direkt den Reiter "Einstellungen"
       - es folgt die Autostart-Frage (Abschnitt 8)
  3. Einstellungen ausfuellen (Abschnitt 3), Mandanten zuordnen (Abschnitt 4),
     Wartungsart-Mapping pflegen (Abschnitt 6), speichern.
  4. Fertig - der Tray-Worker laeuft im Hintergrund. Status ueber das Tray-Icon.

Die GUI und die config.yml editieren dieselbe Datei; ein reiner Texteditor
(config.yml.example -> config.yml umbenennen) funktioniert alternativ auch.


3. EINSTELLUNGEN-REITER (config.yml Felder)
===========================================
  Authentifizierung:
    - auth.uri             Auth-/Ident-Endpoint (Default passt meist)
    - auth.client_id       Sync-Account (E-Mail), vom Samedis-Admin
    - auth.client_secret   Secret (wird nach dem Speichern DPAPI-verschluesselt)
  Samedis API:
    - samedis.uri          https://sync.samedis.care (Default)
    - samedis.api_version  z.B. v4
  Actimed:
    - actimed.database_path       Pfad zur actimed3db.mdb (Datei-Browser)
    - actimed.protocol_pdf_dir    Ausgabeordner des PDF-Druckers (Modus 1)
    - actimed.use_local_snapshot  vor dem Lesen Kopie ziehen (Default true)
    - actimed.default_tester_name Fallback-Pruefername
  Branding (fuer die PNG-Werteprotokolle):
    - branding.service_provider_name
    - branding.logo_path
  HTTP / Proxy (optional):
    - http.proxy / http.proxy_username / http.proxy_password
    - http.valid_certificate

Nach "Speichern" werden client_secret und proxy_password sofort mit der
Windows-DPAPI verschluesselt (siehe Abschnitt 9).


4. MANDANTEN ZUORDNEN
=====================
Button "Mandanten zuordnen..." (Hauptfenster/Tray): fragt die Samedis-API nach
allen Tenants des Sync-Users, laedt parallel die Kundenliste aus A3_CUST der
Actimed-DB und laesst die Zuordnung per Klick zusammenstellen. Ergebnis in
config.yml:

  tenants:
    - name: "Mandant 1"
      samedis_tenant_id: "<24-stellige ObjectId aus Samedis>"
      actimed_cust_ids: [4, 7]     # eine oder mehrere CUST_ID
      enabled: true

WICHTIG: samedis_tenant_id muss die echte 24-stellige Samedis-ID sein. Bleibt
der Beispiel-Platzhalter "<...>" stehen, wird dieser Mandant beim Sync
uebersprungen (Meldung im Reiter "Letzte Meldungen"); die uebrigen laufen weiter.


5. SYNC-MODI (UPLOAD)
=====================
Zwei unabhaengige Upload-Modi, beliebig kombinierbar:

  sync.upload_mode_pdf_pickup        Modus 1: FileSystemWatcher auf
                                     actimed.protocol_pdf_dir. Der Techniker
                                     druckt das Actimed-Pruefprotokoll als PDF;
                                     es wird an den Samedis-Vorgang gehaengt.
                                     Existiert kein Vorgang, wird er angelegt.
  sync.upload_mode_png_on_completion Modus 2: DB-Polling (~30s). Aus den
                                     Messwerten wird ein PNG-Wertenachweis
                                     gerendert und angehaengt. Legt KEINEN
                                     Vorgang an - er muss per Download existieren.

Kombinationen:
  Modus1 an / Modus2 aus   Techniker arbeitet komplett in Actimed, druckt PDF.
  Modus1 aus / Modus2 an   Techniker arbeitet in Samedis-Mobile, Actimed nur Messung.
  beide an                 PNG kommt sofort, PDF spaeter; Vorgang traegt beide.
  beide aus                Nur der Download-Pfad laeuft.

PDF-Drucker fuer Modus 1 (einmalig in Actimed einrichten):
  - Extras -> Optionen -> Druckauftragsname so setzen, dass er die
    Pruefberichtsnummer enthaelt (z.B. Inventarnummer_Pruefberichtsnummer).
  - Einen PDF-Drucker mit festem Ausgabeordner (= protocol_pdf_dir) ohne
    "Speichern unter"-Dialog nutzen (z.B. Microsoft Print to PDF mit
    Default-Pfad oder PDFCreator mit Auto-Save-Profil).


6. WARTUNGSART-MAPPING
======================
Bruecke: Samedis-Issue (services/title/maintenance_type) --Regex--> Actimed-
Taetigkeitsart (A3_ACTIVITY_KIND) --> Taetigkeit (A3_ACTIVITY) mit
Pruefvorschrift (TEST_SPEC). Editor im Reiter "Wartungsart-Mapping"; pro Zeile:

  Match (Regex)   erste passende Zeile gewinnt; leer = Fallback; (?i) = ohne
                  Gross-/Kleinschreibung
  Actimed Kind    muss exakt einem KIND_NAME aus A3_ACTIVITY_KIND entsprechen
                  (zeichengetreu, inkl. Sonderzeichen wie §)
  Activity Name   Variante A (empfohlen): Verweis auf eine vorhandene
                  A3_ACTIVITY mit hinterlegter Pruefvorschrift
  Test Spec Name  Variante B: Name einer TEST_SPEC; nur wirksam mit
                  sync.create_activities_from_mapping=true UND wenn noch keine
                  Taetigkeit existiert -> der Sync legt sie dann selbst an

Beispiel (config.yml):
  maintenance_kind_mapping:
    - match: "(?i)dguv\s*v?3|stk.*§\s*11"
      actimed_kind: "MPBe_§11_STK/DGUV V3"
      actimed_activity_name: "MPBe_STK_HF_emed-100-014"
    - match: "(?i)mtk.*bdm|blutdruck"
      actimed_kind: "MPBe_MTK_BDM"
      actimed_activity_name: "MPBe_Prüfung_MTK_BDM"
    - match: "(?i)defi.*aed"
      actimed_kind: "MPBe_STK Defi (AED)"
      actimed_activity_name: "MPBe_STK_Defi_AED_DP-300"
    - actimed_kind: "MPBe_§7_Wartung/Inspektion"   # Fallback (ohne match)

Ist weder Activity Name noch (Variante B) Test Spec nutzbar und existiert keine
Taetigkeit zur Taetigkeitsart, wird das Issue mit Meldung uebersprungen - es
wird bewusst KEINE Stub-Taetigkeit mit Pruefvorschrift "Unbekannt" angelegt.

Hinweis: In Actimed gibt es keine automatische Zuordnung Wartungsart ->
Pruefvorschrift. Die richtige TEST_SPEC haengt von der elektrischen
Geraeteklasse ab (SKI/SKII x B/BF/CF). Bei elektrisch gemischten Geraeten unter
einer Wartungsart ist Variante A sauberer als Variante B.


7. WEITERE SYNC-SCHALTER (config.yml, Abschnitt "sync:")
========================================================
  download_interval_minutes             Download-Takt in Minuten (Default 15)
  upload_poll_interval_seconds          Modus-2-Polltakt (Default 30)
  run_on_network_reconnect              bei Reconnect sofort syncen
  download_inventories                  Inventare herunterladen
  download_open_issues                  offene Auftraege herunterladen
  upload_finished_issues                Master-Schalter Upload
  create_issues_from_actimed            in Samedis Vorgang anlegen (Default false)
  create_inventories_from_actimed       in Samedis Inventar anlegen (Default false)
  set_inventory_operation_status_on_failed_maintenance
                                        bei "nicht bestanden" Inventar auf
                                        limited_use setzen (Default false)
  create_activities_from_mapping        fehlende Taetigkeit in Actimed selbst
                                        anlegen (braucht Test Spec Name im
                                        Mapping; Default false)
  create_planned_issue_after_completion nach Abschluss die geplante
                                        Folgemassnahme in Samedis anlegen
                                        (due_on = Pruefdatum + Intervall),
                                        idempotent pro Test (Default false)

Intervall-Umrechnung: Actimed fuehrt Monate, Samedis Betrag + Einheit
(day/week/month/year). Der Sync rechnet automatisch um - im Download beim Anlegen
einer Taetigkeit, im Upload aus A3_ACTIVITY.ACTIVITY_INTERVAL (ersatzweise
abgeleitet aus Pruefdatum -> naechstem Pruefdatum).

Hinweis zu create_planned_issue_after_completion: Falls das Samedis-Backend die
Folgemassnahme bei Abschluss bereits selbst erzeugt, wuerde dieser Schalter
Duplikate anlegen. Beim ersten echten Upload pruefen, ob genau EINE
Folgemassnahme entsteht; wenn Samedis sie selbst anlegt, den Schalter aus lassen.


8. AUTOSTART MIT WINDOWS
========================
Beim ersten Start fragt die App, ob sie bei jedem Windows-Login automatisch
starten soll (Registry HKCU\Software\Microsoft\Windows\CurrentVersion\Run,
per-User, kein Admin noetig). Spaeter per Rechtsklick auf das Tray-Icon ->
"Mit Windows starten" umschaltbar. Manuell alternativ: Win+R -> shell:startup ->
Verknuepfung zur EXE hineinlegen. Wird die EXE verschoben, Autostart einmal
aus- und wieder einschalten (schreibt den neuen Pfad in die Registry).


9. SECRETS IN DER config.yml
============================
auth.client_secret und http.proxy_password werden beim Setup im Klartext
eingetragen und beim ersten Speichern mit der Windows-DPAPI verschluesselt
(Form enc:<base64>). Der Schluessel ist an die lokale Maschine gebunden
(LocalMachine-Scope) - die config.yml laesst sich daher NICHT auf einen anderen
Rechner kopieren. Zum Aendern einfach wieder Klartext eintragen und speichern.


10. WARTUNG & RESET (Reiter Einstellungen)
==========================================
  Reparatur-Lauf         setzt fehlende Fremdschluessel (LOCATION_ID/STATUS_ID/
                         ...) der vom Sync angelegten Geraete auf Defaults
                         (ID=1 bzw. STATUS_ID=2); User-Werte bleiben erhalten.
  Cursor zuruecksetzen   loescht Download-/Upload-Cursor -> naechster Sync zieht
                         alles neu. Bietet danach "Jetzt synchronisieren?" an.
  Sync-Inventare loeschen entfernt alle vom Sync angelegten A3_DEV
                         (DEV_Memo=created_by=spl-sync) inkl. A3_IS_ACT_DEV.
Alle drei respektieren das Actimed-Lock (Abschnitt 11).


11. ACTIMED-LOCK
================
Muss der Sync in die actimed3db.mdb schreiben, waehrend Actimed sie exklusiv
offen hat, erscheint ein Modal-Dialog "Actimed bitte schliessen". Actimed kurz
schliessen, "Weiter" klicken - der Sync zieht durch. Danach kann Actimed wieder
geoeffnet werden; der naechste Schreibzugriff faengt den Lock erneut ab.


12. DIAGNOSE
============
Der Reiter "Letzte Meldungen" ist die zentrale Diagnosequelle (Mapping-Fehler,
uebersprungene Issues, Upload-Ergebnisse, Zusammenfassungen pro Lauf).
Tray-Icon-Farben: grau=idle, blau=laeuft, gruen=ok, gelb=Warnungen, rot=Fehler
(Klick oeffnet das Hauptfenster).


SPEICHERORTE ZUR LAUFZEIT
=========================
Alles liegt relativ zum EXE-Ordner - das Tool ist damit portabel: den EXE-Ordner
kopieren nimmt Konfiguration, Logs, State-DB und Cache mit. Der EXE-Ordner muss
beschreibbar sein (z.B. C:\spl-sync\, NICHT unter C:\Program Files\).

Beim Start prueft die App, ob der EXE-Ordner beschreibbar ist. Ist er es nicht
(z.B. unter C:\Program Files\), erscheint ein klarer Hinweis, den Ordner an einen
beschreibbaren Ort zu verschieben.

  Config:     <exe-ordner>\config.yml
  Logs:       <exe-ordner>\logs\
  State-DB:   <exe-ordner>\state\state.sqlite
  PNG-Cache:  <exe-ordner>\scratch\<tenant_id>\
