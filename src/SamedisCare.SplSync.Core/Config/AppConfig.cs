using SamedisCare.SplSync.Core.Api;

namespace SamedisCare.SplSync.Core.Config;

/// <summary>
/// Top-level config object. Mirrors `config.yml.example` 1:1.
/// Same structure pattern as the reference `samedis-care-external-sync` AppConfig,
/// extended with: tenants[], actimed.*, branding.*, maintenance_kind_mapping.
/// </summary>
public class AppConfig
{
    public AuthConfig Auth { get; set; } = new();
    public SamedisConfig Samedis { get; set; } = new();
    public ActimedConfig Actimed { get; set; } = new();
    public BrandingConfig Branding { get; set; } = new();
    public List<TenantConfig> Tenants { get; set; } = new();
    public SyncConfig Sync { get; set; } = new();
    public List<MaintenanceKindMappingEntry> MaintenanceKindMapping { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public HttpConfig Http { get; set; } = new();
    public AppMetaConfig App { get; set; } = new();
}

/// <summary>App-level UI state that we persist alongside the regular config.</summary>
public class AppMetaConfig
{
    /// <summary>
    /// True once we have asked the user about Windows-Autostart. Prevents re-prompting on
    /// every launch even if they declined. The user can still toggle via the tray menu.
    /// </summary>
    public bool AutostartPrompted { get; set; } = false;
}

public class AuthConfig
{
    public string Uri { get; set; } = "https://ident.services";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

public class SamedisConfig
{
    public string Uri { get; set; } = "https://sync.samedis.care";
    public string ApiVersion { get; set; } = "v4";
}

public class ActimedConfig
{
    /// <summary>Absolute path to actimed3db.mdb on the customer's notebook.</summary>
    public string DatabasePath { get; set; } = "";

    /// <summary>Folder where Actimed-printed PDF protocols land (5.7).</summary>
    public string ProtocolPdfDir { get; set; } = "";

    /// <summary>Copy MDB to a temp file before reading, to avoid contention with a running Actimed.</summary>
    public bool UseLocalSnapshot { get; set; } = true;

    /// <summary>Default tester name to fall back to when an issue has no responsible_name.</summary>
    public string DefaultTesterName { get; set; } = "Servicetechniker";
}

public class BrandingConfig
{
    public string ServiceProviderName { get; set; } = "";
    public string LogoPath { get; set; } = "";
}

public class TenantConfig
{
    public string Name { get; set; } = "";
    public string SamedisTenantId { get; set; } = "";
    /// <summary>One Samedis tenant can map to one or more A3_CUST.CUST_ID rows.</summary>
    public List<int> ActimedCustIds { get; set; } = new();
    public bool Enabled { get; set; } = true;
}

public class SyncConfig
{
    /// <summary>How often to fetch open issues + inventory updates from Samedis (Download phase).</summary>
    public int DownloadIntervalMinutes { get; set; } = 15;

    /// <summary>How often to scan A3_FINISHED_TEST for new completed checks (Upload phase, Modus 2).</summary>
    public int UploadPollIntervalSeconds { get; set; } = 30;

    public bool RunOnNetworkReconnect { get; set; } = true;

    // ---- Download (Samedis -> Actimed) ----
    public bool DownloadInventories { get; set; } = true;
    public bool DownloadOpenIssues { get; set; } = true;

    // ---- Upload (Actimed -> Samedis) ----

    /// <summary>Master switch for any upload activity.</summary>
    public bool UploadFinishedIssues { get; set; } = true;

    /// <summary>
    /// Modus 1 — Pickup-Folder-Watcher. Techniker druckt sein Actimed-Prüfprotokoll als PDF
    /// in actimed.protocol_pdf_dir. Wir parsen die Prüfberichtsnummer aus dem Dateinamen,
    /// korrelieren mit A3_FINISHED_TEST und schliessen den Samedis-Vorgang inkl. PDF-Anhang ab.
    /// </summary>
    public bool UploadModePdfPickup { get; set; } = false;

    /// <summary>
    /// Modus 2 — DB-Polling auf neue A3_FINISHED_TEST. Aus den Test-Daten erzeugen wir lokal
    /// ein PNG-Wertenachweis-Bild und hängen es zeitnah an den Samedis-Vorgang.
    /// </summary>
    public bool UploadModePngOnCompletion { get; set; } = true;

    public bool CreateIssuesFromActimed { get; set; } = false;
    public bool CreateInventoriesFromActimed { get; set; } = false;
    public bool SetInventoryOperationStatusOnFailedMaintenance { get; set; } = false;

    /// <summary>
    /// Erlaubt dem Download, eine fehlende A3_ACTIVITY (Tätigkeit) selbst anzulegen, wenn für die
    /// gemappte KIND_ID noch keine existiert. Voraussetzung: der passende
    /// maintenance_kind_mapping-Eintrag gibt die Prüfvorschrift explizit an
    /// (actimed_test_spec_name oder actimed_test_spec_id). Ohne gültige Prüfvorschrift wird nichts
    /// angelegt (kein TEST_SPEC=1/"Unbekannt"), das Issue bleibt offen mit klarer Meldung. Default false.
    /// </summary>
    public bool CreateActivitiesFromMapping { get; set; } = false;

    /// <summary>
    /// Legt nach dem Abschluss einer Prüfung (Upload) in Samedis automatisch die geplante
    /// Folgemaßnahme an: neues maintenance-Issue mit due_on = Prüfdatum + Intervall (Monate aus
    /// A3_ACTIVITY bzw. abgeleitet). Idempotent pro abgeschlossenem Test. Default false.
    /// </summary>
    public bool CreatePlannedIssueAfterCompletion { get; set; } = false;
}

public class MaintenanceKindMappingEntry
{
    /// <summary>Regex (case-insensitive). First match wins. If null, this is the default fallback entry.</summary>
    public string? Match { get; set; }

    /// <summary>Pflicht: A3_ACTIVITY_KIND.KIND_NAME exakt (inkl. § und Co.).</summary>
    public string ActimedKind { get; set; } = "";

    /// <summary>
    /// Optional aber dringend empfohlen: A3_ACTIVITY.ACTIVITY_NAME — die konkrete Tätigkeit mit
    /// hinterlegter Prüfvorschrift. Ist es gesetzt, wird diese existierende Tätigkeit
    /// wiederverwendet (muss dann existieren, sonst Fehler).
    /// </summary>
    public string? ActimedActivityName { get; set; }

    /// <summary>
    /// Optional: TEST_SPEC.NAME der Prüfvorschrift, die verwendet werden soll, wenn der Sync
    /// eine fehlende A3_ACTIVITY für diese Wartungsart selbst anlegt
    /// (nur wirksam bei <see cref="SyncConfig.CreateActivitiesFromMapping"/>=true und wenn keine
    /// Tätigkeit für die KIND_ID existiert). Alternativ per ID über
    /// <see cref="ActimedTestSpecId"/>. TEST_SPEC=1 ("Unbekannt") wird abgelehnt.
    /// </summary>
    public string? ActimedTestSpecName { get; set; }

    /// <summary>Optional: TEST_SPEC.TEST_SPEC_ID direkt (Vorrang vor <see cref="ActimedTestSpecName"/>).</summary>
    public int? ActimedTestSpecId { get; set; }

    /// <summary>Optional: Prüfintervall in Monaten für die neu angelegte Tätigkeit. Default 12.</summary>
    public int? ActimedActivityIntervalMonths { get; set; }
}

public class LoggingConfig
{
    /// <summary>0 off, 1 info, 2 debug.</summary>
    public int Level { get; set; } = 1;
    /// <summary>0 none, 1 console, 2 file, 3 console+file.</summary>
    public int Mode { get; set; } = 3;
    /// <summary>Log-Verzeichnis. Relativer Pfad (Default "logs") wird relativ zum EXE-Ordner aufgelöst.</summary>
    public string Directory { get; set; } = "logs";
}

public class HttpConfig
{
    public bool ValidCertificate { get; set; } = true;
    public string Proxy { get; set; } = "";
    public string ProxyUsername { get; set; } = "";
    public string ProxyPassword { get; set; } = "";

    /// <summary>Hard request timeout in seconds. Default 30. Set to 0 to disable (not recommended).</summary>
    public int TimeoutSeconds { get; set; } = 30;

    public HttpSettings ToSettings() => new()
    {
        ValidateCertificate = ValidCertificate,
        Proxy = string.IsNullOrWhiteSpace(Proxy) ? null : Proxy,
        ProxyUsername = string.IsNullOrWhiteSpace(ProxyUsername) ? null : ProxyUsername,
        ProxyPassword = string.IsNullOrWhiteSpace(ProxyPassword) ? null : ProxyPassword,
        TimeoutSeconds = TimeoutSeconds <= 0 ? 30 : TimeoutSeconds
    };
}
