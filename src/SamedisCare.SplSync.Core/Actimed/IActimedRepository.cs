namespace SamedisCare.SplSync.Core.Actimed;

/// <summary>
/// Abstraction over the Actimed data store. Two implementations:
///  - OleDbActimedRepository  → production on Windows, talks to actimed3db.mdb via ACE OLE-DB
///  - SqliteActimedRepository → tests / macOS dev, talks to a SQLite mirror built from CSV exports
///
/// Goal: every piece of mapping/sync logic in Core/Sync/ depends only on this interface,
/// so it can be unit-tested on macOS against SQLite.
/// </summary>
public interface IActimedRepository
{
    // ===== READ — for the Upload pipeline =====

    /// <summary>Fetch finished tests modified since `since` (exclusive), filtered to the given customer IDs.</summary>
    IReadOnlyList<ActimedFinishedTest> GetFinishedTestsSince(DateTime since, IEnumerable<int> custIds);

    IReadOnlyList<ActimedFinishedTestItem> GetTestItemsForTest(string testId);
    IReadOnlyList<ActimedFinishedTestResult> GetTestResultsForTest(string testId);

    ActimedDevice? GetDeviceById(int devId);

    // ===== READ — lookup/cache helpers =====

    /// <summary>List all customers (= A3_CUST rows). Used by the tenant-mapping dialog.</summary>
    IReadOnlyList<ActimedCustomer> ListCustomers();

    ActimedDevice? FindDeviceByInventoryNo(string inventoryNo, IEnumerable<int> custIds);
    ActimedManufacturer? FindManufacturerByName(string name);
    ActimedDeviceKind? FindDeviceKindByName(string name);
    ActimedDeviceType? FindDeviceTypeByName(string name);
    ActimedActivityKind? FindActivityKindByName(string kindName);
    ActimedActivity? FindActivityByKindId(int kindId);

    /// <summary>
    /// Liefert die A3_ACTIVITY per ID (u. a. für das Prüfintervall ACTIVITY_INTERVAL beim Upload),
    /// oder null wenn keine existiert.
    /// </summary>
    ActimedActivity? GetActivityById(int activityId);

    /// <summary>
    /// Sucht eine A3_ACTIVITY mit exaktem ACTIVITY_NAME-Match. Liefert null wenn keine existiert —
    /// der Caller (DownloadEngine) kann dann eine UnmappedKindException werfen, weil das ein
    /// Konfigurationsfehler ist (Mapping verweist auf nicht existente Tätigkeit).
    /// </summary>
    ActimedActivity? FindActivityByName(string activityName);

    /// <summary>
    /// Sucht eine TEST_SPEC (Prüfvorschrift) per exaktem Namen. Wird gebraucht, wenn der Sync
    /// eine neue A3_ACTIVITY anlegen soll und das Wartungsart-Mapping die Prüfvorschrift per
    /// Name referenziert. Liefert null, wenn keine passt.
    /// </summary>
    ActimedTestSpec? FindTestSpecByName(string name);

    /// <summary>Liefert die TEST_SPEC per ID, oder null wenn keine existiert.</summary>
    ActimedTestSpec? GetTestSpecById(int testSpecId);

    // ===== WRITE — for the Download pipeline =====
    // All write methods may throw ActimedLockedException if Actimed has the DB exclusively open.

    int InsertManufacturer(ActimedManufacturer manu);
    int InsertDeviceKind(ActimedDeviceKind kind);
    int InsertDeviceType(ActimedDeviceType type);
    int InsertDevice(ActimedDevice device);
    int InsertActivity(ActimedActivity activity);

    /// <summary>
    /// Repariert einen vom Sync angelegten A3_DEV-Eintrag, der mit FK=0/NULL in der DB liegt.
    /// Setzt LOCATION_ID/STATUS_ID/COST_CENTRE_ID/RESP_PERSON_ID/SUPPLIER_ID auf die
    /// Default-Stammdaten-IDs (1 bzw. 2 für STATUS = "Ungeprüft"), aber NUR für Spalten,
    /// die aktuell 0 oder NULL sind — manuell vom User gesetzte Werte werden nicht überschrieben.
    /// Liefert true, wenn tatsaechlich Spalten geupdated wurden.
    /// </summary>
    /// <remarks>
    /// Hintergrund: aeltere Builds des Sync-Tools (vor dem FK-Default-Fix) haben Devices ohne
    /// Default-FKs geschrieben. Solche Datensaetze sind in der Actimed-UI unsichtbar, weil sie
    /// im Standardfilter auf nicht existente Stammdaten verweisen. Dieser Repair-Pfad wird vom
    /// DownloadEngine bei jedem Lauf für existierende, vom Sync angelegte Devices aufgerufen.
    /// </remarks>
    bool RepairDeviceForeignKeys(int devId);

    /// <summary>
    /// Upsert an A3_IS_ACT_DEV row (= the planned next check for a device).
    /// Returns true if a new row was inserted, false if an existing row was updated.
    /// </summary>
    bool UpsertIsActDev(ActimedIsActDev assignment);
}

/// <summary>
/// Thrown when the underlying MDB is exclusively locked (typical: Actimed running).
/// The caller is expected to surface this to the user via the tray dialog and retry
/// after the user confirms that Actimed has been closed.
/// </summary>
public class ActimedLockedException : Exception
{
    public ActimedLockedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Thrown when the Microsoft.ACE.OLEDB.16.0 provider is not registered on the local machine.
/// Treated separately from ActimedLockedException because the user's remediation is different:
/// they need to install the Access Database Engine 2016 Redistributable (x64), not close Actimed.
/// </summary>
public class AceProviderMissingException : Exception
{
    public const string DownloadUrl = "https://www.microsoft.com/en-us/download/details.aspx?id=54920";

    public AceProviderMissingException(Exception? inner = null)
        : base(
            "Der OLE-DB-Provider Microsoft.ACE.OLEDB.16.0 ist nicht installiert.\n" +
            "Bitte 'Microsoft Access Database Engine 2016 Redistributable (x64)' installieren:\n" +
            "  " + DownloadUrl + "\n" +
            "Stille Installation (wegen 32-bit Office/Actimed parallel):\n" +
            "  AccessDatabaseEngine_X64.exe /quiet",
            inner)
    { }
}
