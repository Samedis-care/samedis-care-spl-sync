using System.Data;
using System.Data.OleDb;
using System.Runtime.Versioning;
using SamedisCare.SplSync.Core.Sync;

namespace SamedisCare.SplSync.Core.Actimed;

/// <summary>
/// Production-side Actimed repository, talks to actimed3db.mdb via Microsoft.ACE.OLEDB.16.0.
/// Compiles cross-platform (so we can publish for win-x64 from macOS), but is decorated with
/// [SupportedOSPlatform("windows")] and only callable on Windows. Callers gate at runtime via
/// OperatingSystem.IsWindows() before instantiating.
///
/// Lock handling: when ACE returns the typical "file already in use" / 0x80004005,
/// we wrap it as ActimedLockedException. The caller (SyncWorker) is responsible for
/// pausing the sync, surfacing the tray dialog, and retrying after the user closes Actimed.
/// </summary>
[SupportedOSPlatform("windows")]
public class OleDbActimedRepository : IActimedRepository, IMaintenanceCapable
{
    private readonly string _connectionString;

    public OleDbActimedRepository(string mdbPath)
    {
        // ACE 16 expects a Windows-style path; passing forward slashes also works.
        _connectionString = $"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={mdbPath};Persist Security Info=False;";
    }

    private OleDbConnection Open()
    {
        var conn = new OleDbConnection(_connectionString);
        try
        {
            conn.Open();
            return conn;
        }
        catch (System.InvalidOperationException ex) when (IsAceMissing(ex))
        {
            conn.Dispose();
            throw new AceProviderMissingException(ex);
        }
        catch (OleDbException ex) when (IsAceMissing(ex))
        {
            conn.Dispose();
            throw new AceProviderMissingException(ex);
        }
        catch (OleDbException ex) when (IsLockError(ex))
        {
            conn.Dispose();
            throw new ActimedLockedException(
                "Actimed-Datenbank ist exklusiv geöffnet. Bitte Actimed kurz schließen und erneut versuchen.",
                ex);
        }
    }

    /// <summary>
    /// Detects the "Provider nicht registriert" failure mode that occurs when Microsoft.ACE.OLEDB.16.0
    /// is not installed. HRESULT 0x80131501 (.NET InvalidOperationException for COM activation failure)
    /// or message-text match.
    /// </summary>
    private static bool IsAceMissing(Exception ex)
    {
        if ((uint)ex.HResult == 0x80131501u) return true;
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("Microsoft.ACE.OLEDB", StringComparison.OrdinalIgnoreCase)
            && (msg.Contains("not registered", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("nicht registriert", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLockError(OleDbException ex)
    {
        // ACE/Jet wraps lock errors with HRESULT 0x80004005 ("Unspecified error") plus a message
        // like "The Microsoft Access database engine cannot open or write to the file '...'.
        //       It is already opened exclusively by another user, or you need permission to view and write its data."
        // We match heuristically on HRESULT + message keywords.
        var hrMatches = (uint)ex.HResult == 0x80004005u;
        var msg = ex.Message ?? string.Empty;
        var msgMatches = msg.Contains("already opened exclusively", StringComparison.OrdinalIgnoreCase)
                      || msg.Contains("It is already in use", StringComparison.OrdinalIgnoreCase)
                      || msg.Contains("could not lock file", StringComparison.OrdinalIgnoreCase)
                      || msg.Contains("file already in use", StringComparison.OrdinalIgnoreCase);
        return hrMatches || msgMatches;
    }

    // ===== READ =====

    public IReadOnlyList<ActimedFinishedTest> GetFinishedTestsSince(DateTime since, IEnumerable<int> custIds)
    {
        var custList = custIds?.ToList() ?? new List<int>();
        if (custList.Count == 0) return Array.Empty<ActimedFinishedTest>();
        var custIn = string.Join(",", custList);

        // ACHTUNG: Spaltennamen TEST_Pruefberichtsnummer / TEST_Pruefergebnis sind die echten
        // MDB-Schema-Bezeichner — nicht durch Umlaute ersetzen, sonst findet ACE die Spalten nicht.
        var sql = $@"
            SELECT t.TEST_ID, t.DEV_ID, t.TEST_DATE, t.TESTER_NAME, t.PVS_NAME,
                   t.TEST_Pruefberichtsnummer, t.TEST_Pruefergebnis,
                   t.LAST_TEST_DATE, t.NEXT_TEST_DATE, t.MEMO, t.MODIFYTIME
            FROM   A3_FINISHED_TEST t
            INNER JOIN A3_DEV d ON d.DEV_ID = t.DEV_ID
            WHERE  d.CUST_ID IN ({custIn})
              AND  t.MODIFYTIME > ?
            ORDER BY t.MODIFYTIME ASC;";

        var results = new List<ActimedFinishedTest>();
        using var conn = Open();
        using var cmd = new OleDbCommand(sql, conn);
        cmd.Parameters.Add(new OleDbParameter("?", since.ToOADate()));
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            results.Add(new ActimedFinishedTest(
                TestId: GetString(rdr, "TEST_ID"),
                DevId: GetInt(rdr, "DEV_ID"),
                TestDate: OaDate.TryParseOaDate(rdr["TEST_DATE"]) ?? DateTime.MinValue,
                TesterName: GetString(rdr, "TESTER_NAME"),
                PvsName: GetString(rdr, "PVS_NAME"),
                Pruefberichtsnummer: GetString(rdr, "TEST_Pruefberichtsnummer"),
                Pruefergebnis: GetString(rdr, "TEST_Pruefergebnis"),
                LastTestDate: OaDate.TryParseOaDate(rdr["LAST_TEST_DATE"]),
                NextTestDate: OaDate.TryParseOaDate(rdr["NEXT_TEST_DATE"]),
                Memo: GetNullableString(rdr, "MEMO"),
                ModifyTime: OaDate.TryParseOaDate(rdr["MODIFYTIME"]) ?? DateTime.MinValue
            ));
        }
        return results;
    }

    public IReadOnlyList<ActimedFinishedTestItem> GetTestItemsForTest(string testId)
    {
        const string sql = @"
            SELECT TEST_ID, TEST_ITEM_ID, ITEM_ID, WS_DSCR, FUNC_NAME, TEST_ITEM_SUCCESS
            FROM   A3_FINISHED_TEST_ITEM
            WHERE  TEST_ID = ?
            ORDER BY ITEM_ID ASC;";

        var results = new List<ActimedFinishedTestItem>();
        using var conn = Open();
        using var cmd = new OleDbCommand(sql, conn);
        cmd.Parameters.Add(new OleDbParameter("?", testId));
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            results.Add(new ActimedFinishedTestItem(
                TestId: GetString(rdr, "TEST_ID"),
                TestItemId: GetString(rdr, "TEST_ITEM_ID"),
                ItemId: GetInt(rdr, "ITEM_ID"),
                WsDscr: GetString(rdr, "WS_DSCR"),
                FuncName: GetString(rdr, "FUNC_NAME"),
                Success: GetBool(rdr, "TEST_ITEM_SUCCESS")
            ));
        }
        return results;
    }

    public IReadOnlyList<ActimedFinishedTestResult> GetTestResultsForTest(string testId)
    {
        const string sql = @"
            SELECT r.TEST_ITEM_ID, r.TEST_ITEM_RESULT_No, r.ITEM_DSCR, r.ITEM_UNIT,
                   r.ITEM_VALUE, r.LIMIT1, r.LIMIT2, r.TEST_ITEM_RESULT_SUCCESS
            FROM   A3_FINISHED_TEST_ITEM_RESULT r
            INNER JOIN A3_FINISHED_TEST_ITEM i ON i.TEST_ITEM_ID = r.TEST_ITEM_ID
            WHERE  i.TEST_ID = ?
            ORDER BY r.TEST_ITEM_ID, r.TEST_ITEM_RESULT_No;";

        var results = new List<ActimedFinishedTestResult>();
        using var conn = Open();
        using var cmd = new OleDbCommand(sql, conn);
        cmd.Parameters.Add(new OleDbParameter("?", testId));
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            results.Add(new ActimedFinishedTestResult(
                TestItemId: GetString(rdr, "TEST_ITEM_ID"),
                ResultNo: GetInt(rdr, "TEST_ITEM_RESULT_No"),
                ItemDscr: GetString(rdr, "ITEM_DSCR"),
                Unit: GetNullableString(rdr, "ITEM_UNIT"),
                Value: GetNullableString(rdr, "ITEM_VALUE"),
                Limit1: GetNullableString(rdr, "LIMIT1"),
                Limit2: GetNullableString(rdr, "LIMIT2"),
                Success: GetBool(rdr, "TEST_ITEM_RESULT_SUCCESS")
            ));
        }
        return results;
    }

    public ActimedDevice? GetDeviceById(int devId) =>
        FindDeviceWhere("DEV_ID = ?", new OleDbParameter("?", devId));

    public IReadOnlyList<ActimedCustomer> ListCustomers()
    {
        const string sql = @"
            SELECT CUST_ID, CUST_SHORT, CUST_NO, CUST_NAME1, CUST_NAME2
            FROM   A3_CUST
            ORDER BY CUST_ID ASC;";
        var results = new List<ActimedCustomer>();
        using var conn = Open();
        using var cmd = new OleDbCommand(sql, conn);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            results.Add(new ActimedCustomer(
                CustId: GetInt(rdr, "CUST_ID"),
                Short:  GetString(rdr, "CUST_SHORT"),
                CustNo: GetString(rdr, "CUST_NO"),
                Name1:  GetString(rdr, "CUST_NAME1"),
                Name2:  GetNullableString(rdr, "CUST_NAME2")));
        }
        return results;
    }

    public ActimedDevice? FindDeviceByInventoryNo(string inventoryNo, IEnumerable<int> custIds)
    {
        if (string.IsNullOrWhiteSpace(inventoryNo)) return null;
        var custList = custIds?.ToList() ?? new List<int>();
        if (custList.Count == 0) return null;
        var custIn = string.Join(",", custList);
        var clause = $"DEV_InventoryNo = ? AND CUST_ID IN ({custIn})";
        return FindDeviceWhere(clause, MakeStringParam(inventoryNo));
    }

    private ActimedDevice? FindDeviceWhere(string whereClause, params OleDbParameter[] parameters)
    {
        var sql = $@"
            SELECT TOP 1 DEV_ID, CUST_ID, DEV_InventoryNo, DEV_SerialNo, DEV_TYPE_ID,
                   LOCATION_ID, STATUS_ID, NEXTACTIVITY_ID, DEV_Memo
            FROM   A3_DEV
            WHERE  {whereClause};";
        using var conn = Open();
        using var cmd = new OleDbCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.Add(p);
        using var rdr = cmd.ExecuteReader();
        return rdr.Read() ? ReadDevice(rdr) : null;
    }

    public ActimedManufacturer? FindManufacturerByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        const string sql = "SELECT TOP 1 MANU_ID, MANU_NAME1, MANU_NAME2 FROM A3_MANUF WHERE MANU_NAME1 = ?;";
        using var conn = Open();
        using var cmd = new OleDbCommand(sql, conn);
        cmd.Parameters.Add(MakeStringParam(name));
        using var rdr = cmd.ExecuteReader();
        return rdr.Read()
            ? new ActimedManufacturer(GetInt(rdr, "MANU_ID"), GetString(rdr, "MANU_NAME1"), GetNullableString(rdr, "MANU_NAME2"))
            : null;
    }

    public ActimedDeviceKind? FindDeviceKindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        const string sql = "SELECT TOP 1 DEV_KIND_ID, DEV_KIND_NAME, DEV_KIND_DIMDINR FROM A3_DEV_KIND WHERE DEV_KIND_NAME = ?;";
        using var conn = Open();
        using var cmd = new OleDbCommand(sql, conn);
        cmd.Parameters.Add(MakeStringParam(name));
        using var rdr = cmd.ExecuteReader();
        return rdr.Read()
            ? new ActimedDeviceKind(GetInt(rdr, "DEV_KIND_ID"), GetString(rdr, "DEV_KIND_NAME"), GetNullableString(rdr, "DEV_KIND_DIMDINR"))
            : null;
    }

    public ActimedDeviceType? FindDeviceTypeByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        const string sql = "SELECT TOP 1 DEV_TYPE_ID, MANU_ID, DEV_KIND_ID, DEV_TYPE_NAME, DEV_TYPE_Modell FROM A3_DEV_TYPE WHERE DEV_TYPE_NAME = ?;";
        using var conn = Open();
        using var cmd = new OleDbCommand(sql, conn);
        cmd.Parameters.Add(MakeStringParam(name));
        using var rdr = cmd.ExecuteReader();
        return rdr.Read()
            ? new ActimedDeviceType(GetInt(rdr, "DEV_TYPE_ID"), GetInt(rdr, "MANU_ID"), GetInt(rdr, "DEV_KIND_ID"),
                GetString(rdr, "DEV_TYPE_NAME"), GetString(rdr, "DEV_TYPE_Modell"))
            : null;
    }

    public ActimedActivityKind? FindActivityKindByName(string kindName)
    {
        if (string.IsNullOrWhiteSpace(kindName)) return null;
        const string sql = "SELECT TOP 1 KIND_ID, KIND_NAME, KIND_DSCR FROM A3_ACTIVITY_KIND WHERE TRIM(KIND_NAME) = ?;";
        using var conn = Open();
        using var cmd = new OleDbCommand(sql, conn);
        cmd.Parameters.Add(MakeStringParam(kindName.Trim()));
        using var rdr = cmd.ExecuteReader();
        return rdr.Read()
            ? new ActimedActivityKind(GetInt(rdr, "KIND_ID"), GetString(rdr, "KIND_NAME"), GetNullableString(rdr, "KIND_DSCR"))
            : null;
    }

    /// <summary>
    /// ACE OLE-DB ist beim Inferieren von String-Parameter-Typen empfindlich. Wenn der Parameter
    /// implizit als VARIANT/NTEXT durchgereicht wird, kommt es bei manchen Spaltentypen zum Fehler
    /// "Data type mismatch in criteria expression". Setzen wir VarWChar mit expliziter Size, geht's.
    /// Size 255 ist ACE-konform fuer alle CHAR/TEXT-Spalten der A3_*-Tabellen (alles unter Memo-Limit).
    /// </summary>
    private static OleDbParameter MakeStringParam(string value)
    {
        // OleDbParameter mit Konstruktor (name, type, size). Die Position bestimmt das ?-Mapping.
        return new OleDbParameter("?", OleDbType.VarWChar, 255) { Value = value ?? string.Empty };
    }

    public ActimedActivity? FindActivityByKindId(int kindId)
    {
        const string sql = "SELECT TOP 1 ACTIVITY_ID, TEST_SPEC_ID, KIND_ID, ACTIVITY_INTERVAL, ACTIVITY_NAME FROM A3_ACTIVITY WHERE KIND_ID = ?;";
        using var conn = Open();
        using var cmd = new OleDbCommand(sql, conn);
        cmd.Parameters.Add(BuildParam("KIND_ID", kindId));
        using var rdr = cmd.ExecuteReader();
        return rdr.Read()
            ? new ActimedActivity(GetInt(rdr, "ACTIVITY_ID"), GetInt(rdr, "TEST_SPEC_ID"), GetInt(rdr, "KIND_ID"),
                GetInt(rdr, "ACTIVITY_INTERVAL"), GetString(rdr, "ACTIVITY_NAME"))
            : null;
    }

    public ActimedActivity? FindActivityByName(string activityName)
    {
        if (string.IsNullOrWhiteSpace(activityName)) return null;
        const string sql = "SELECT TOP 1 ACTIVITY_ID, TEST_SPEC_ID, KIND_ID, ACTIVITY_INTERVAL, ACTIVITY_NAME FROM A3_ACTIVITY WHERE ACTIVITY_NAME = ?;";
        using var conn = Open();
        using var cmd = new OleDbCommand(sql, conn);
        cmd.Parameters.Add(MakeStringParam(activityName));
        using var rdr = cmd.ExecuteReader();
        return rdr.Read()
            ? new ActimedActivity(GetInt(rdr, "ACTIVITY_ID"), GetInt(rdr, "TEST_SPEC_ID"), GetInt(rdr, "KIND_ID"),
                GetInt(rdr, "ACTIVITY_INTERVAL"), GetString(rdr, "ACTIVITY_NAME"))
            : null;
    }

    // ===== WRITE =====
    // We use MAX(id)+1 inside a single transaction — Actimed's tables don't have AutoIncrement.
    // TODO (offene Recherche-Punkte 9.1): an einer realen Kunden-DB verifizieren, dass das stimmt.

    public int InsertManufacturer(ActimedManufacturer manu) =>
        InsertWithMaxIdPlusOne("A3_MANUF", "MANU_ID", new (string, object?)[]
        {
            ("MANU_NAME1", manu.Name1),
            ("MANU_NAME2", manu.Name2 ?? string.Empty)
        });

    public int InsertDeviceKind(ActimedDeviceKind kind) =>
        InsertWithMaxIdPlusOne("A3_DEV_KIND", "DEV_KIND_ID", new (string, object?)[]
        {
            ("DEV_KIND_NAME", kind.Name),
            ("DEV_KIND_DIMDINR", kind.DimdiNr ?? string.Empty)
        });

    public int InsertDeviceType(ActimedDeviceType type) =>
        InsertWithMaxIdPlusOne("A3_DEV_TYPE", "DEV_TYPE_ID", new (string, object?)[]
        {
            ("MANU_ID", type.ManuId),
            ("DEV_KIND_ID", type.DevKindId),
            ("DEV_TYPE_NAME", type.Name),
            ("DEV_TYPE_Modell", type.Modell)
        });

    public int InsertDevice(ActimedDevice device) =>
        InsertWithMaxIdPlusOne("A3_DEV", "DEV_ID", new (string, object?)[]
        {
            ("CUST_ID",         device.CustId),
            ("DEV_InventoryNo", device.InventoryNo),
            ("DEV_SerialNo",    device.SerialNo),
            ("DEV_TYPE_ID",     device.DevTypeId),
            // FK-Defaults: alle auf den 'Unbekannt'-Eintrag ID=1 zeigen, der bei jeder
            // Actimed-Installation vorhanden ist. Mit FK=0 würde Actimed-UI das Inventar
            // im Standardfilter nicht anzeigen, weil dann auf nicht-existente Stammdaten
            // verwiesen wird.
            ("LOCATION_ID",     device.LocationId    ?? 1),  // 1 = "Unbekannter Standort"
            ("STATUS_ID",       device.StatusId      ?? 2),  // 2 = "Ungeprüft" (passt für frisch importierte Geraete)
            ("COST_CENTRE_ID",  1),                          // 1 = "Unbekannte Kostenstelle"
            ("RESP_PERSON_ID",  1),                          // 1 = "Unbekannter Verantwortlicher"
            ("SUPPLIER_ID",     1),                          // 1 = "Unbekannter Lieferant"
            ("NEXTACTIVITY_ID", device.NextActivityId ?? 0), // wird von Actimed selbst gepflegt
            ("DEV_Memo",        device.Memo ?? "created_by=spl-sync"),
            ("MODIFYTIME",      DateTime.Now),                // OleDb konvertiert nach OA-Date
            ("MODIFYTYPE",      1)                            // 1 = neu eingefügt
        });

    public int InsertActivity(ActimedActivity activity) =>
        InsertWithMaxIdPlusOne("A3_ACTIVITY", "ACTIVITY_ID", new (string, object?)[]
        {
            ("TEST_SPEC_ID", activity.TestSpecId),
            ("KIND_ID", activity.KindId),
            ("ACTIVITY_INTERVAL", activity.IntervalMonths),
            ("ACTIVITY_NAME", activity.Name)
        });

    public bool RepairDeviceForeignKeys(int devId)
    {
        // Strategie: erst die aktuellen FK-Werte lesen (aelterere Builds koennten 0 oder NULL
        // geschrieben haben), dann nur die Spalten überschreiben, die wirklich kaputt sind.
        // Wir wollen keine vom User manuell zugewiesenen Standorte/Status überschreiben.
        using var conn = Open();

        int loc, sta, cc, rp, sup;
        using (var read = new OleDbCommand(
            "SELECT LOCATION_ID, STATUS_ID, COST_CENTRE_ID, RESP_PERSON_ID, SUPPLIER_ID FROM A3_DEV WHERE DEV_ID = ?;",
            conn))
        {
            read.Parameters.Add(BuildParam("DEV_ID", devId));
            using var rdr = read.ExecuteReader();
            if (!rdr.Read()) return false; // Geraet existiert nicht
            loc = ReadIntOrZero(rdr, 0);
            sta = ReadIntOrZero(rdr, 1);
            cc  = ReadIntOrZero(rdr, 2);
            rp  = ReadIntOrZero(rdr, 3);
            sup = ReadIntOrZero(rdr, 4);
        }

        var anyBroken = loc == 0 || sta == 0 || cc == 0 || rp == 0 || sup == 0;
        if (!anyBroken) return false;

        try
        {
            using var upd = new OleDbCommand(
                "UPDATE A3_DEV SET " +
                "LOCATION_ID = ?, STATUS_ID = ?, COST_CENTRE_ID = ?, RESP_PERSON_ID = ?, SUPPLIER_ID = ?, " +
                "MODIFYTIME = ?, MODIFYTYPE = ? " +
                "WHERE DEV_ID = ?;",
                conn);
            upd.Parameters.Add(BuildParam("LOCATION_ID",    loc == 0 ? 1 : loc));   // 1 = "Unbekannter Standort"
            upd.Parameters.Add(BuildParam("STATUS_ID",      sta == 0 ? 2 : sta));   // 2 = "Ungeprüft"
            upd.Parameters.Add(BuildParam("COST_CENTRE_ID", cc  == 0 ? 1 : cc));
            upd.Parameters.Add(BuildParam("RESP_PERSON_ID", rp  == 0 ? 1 : rp));
            upd.Parameters.Add(BuildParam("SUPPLIER_ID",    sup == 0 ? 1 : sup));
            upd.Parameters.Add(BuildParam("MODIFYTIME",     DateTime.Now));
            upd.Parameters.Add(BuildParam("MODIFYTYPE",     2));                    // 2 = MODIFYTYPE Update
            upd.Parameters.Add(BuildParam("DEV_ID",         devId));
            upd.ExecuteNonQuery();
            return true;
        }
        catch (OleDbException ex) when (IsLockError(ex))
        {
            throw new ActimedLockedException("Actimed-Datenbank gesperrt während RepairDeviceForeignKeys.", ex);
        }
    }

    private static int ReadIntOrZero(OleDbDataReader rdr, int idx)
    {
        var v = rdr[idx];
        if (v == null || v == DBNull.Value) return 0;
        return Convert.ToInt32(v);
    }

    // -------------------------------------------------------------------------
    // IMaintenanceCapable: Bulk-Operationen, die der User aus den Settings auslöst.
    // -------------------------------------------------------------------------

    public IReadOnlyList<int> GetSyncCreatedDeviceIds(IList<int> custIds)
    {
        if (custIds == null || custIds.Count == 0) return Array.Empty<int>();

        // ACE OLE-DB Wildcard-Verhalten ist tueckisch: bei JET-Modus ist es '*', bei ANSI-92 '%'.
        // Welcher Modus aktiv ist, haengt vom Connection-String ab. Statt LIKE nutzen wir die
        // Access-Funktion INSTR() — die liefert die Position 1, wenn der String am Anfang steht.
        // Das ist eindeutig und unabhaengig vom SQL-Mode. (Doku: ACE unterstuetzt InStr() in
        // SELECT-Predicates seit Office 2003.)
        var custInClause = string.Join(",", custIds);
        var sql = $"SELECT DEV_ID FROM A3_DEV WHERE CUST_ID IN ({custInClause}) " +
                  "AND INSTR(DEV_Memo, 'created_by=spl-sync') = 1;";

        using var conn = Open();
        using var cmd = new OleDbCommand(sql, conn);
        using var rdr = cmd.ExecuteReader();
        var result = new List<int>();
        while (rdr.Read()) result.Add(Convert.ToInt32(rdr[0]));
        return result;
    }

    public int DeleteSyncCreatedDevices(IList<int> custIds)
    {
        if (custIds == null || custIds.Count == 0) return 0;

        var custInClause = string.Join(",", custIds);
        // Defensiv: vor dem DELETE alle abhängigen Einträge in A3_IS_ACT_DEV mitnehmen,
        // sonst koennte Actimed "verwaiste" Prüfplanungen anzeigen, die ins Leere zeigen.
        // INSTR() statt LIKE — robust gegen JET/ANSI-Wildcard-Modus.
        var deleteIsActDevSql =
            $"DELETE FROM A3_IS_ACT_DEV WHERE DEV_ID IN " +
            $"(SELECT DEV_ID FROM A3_DEV WHERE CUST_ID IN ({custInClause}) " +
            $"AND INSTR(DEV_Memo, 'created_by=spl-sync') = 1);";
        var deleteDevSql =
            $"DELETE FROM A3_DEV WHERE CUST_ID IN ({custInClause}) " +
            $"AND INSTR(DEV_Memo, 'created_by=spl-sync') = 1;";

        using var conn = Open();
        try
        {
            using (var cmd1 = new OleDbCommand(deleteIsActDevSql, conn))
                cmd1.ExecuteNonQuery();
            using var cmd2 = new OleDbCommand(deleteDevSql, conn);
            return cmd2.ExecuteNonQuery();
        }
        catch (OleDbException ex) when (IsLockError(ex))
        {
            throw new ActimedLockedException("Actimed-Datenbank gesperrt während DeleteSyncCreatedDevices.", ex);
        }
    }

    public bool UpsertIsActDev(ActimedIsActDev a)
    {
        const string findSql = "SELECT COUNT(*) FROM A3_IS_ACT_DEV WHERE DEV_ID = ? AND ACTIVITY_ID = ?;";
        const string updateSql = "UPDATE A3_IS_ACT_DEV SET ACT_DEV_NEXT = ?, ACT_DEV_LAST = ?, TESTER_ID = ? WHERE DEV_ID = ? AND ACTIVITY_ID = ?;";
        const string insertSql = "INSERT INTO A3_IS_ACT_DEV (DEV_ID, ACTIVITY_ID, ACT_DEV_NEXT, ACT_DEV_LAST, TESTER_ID) VALUES (?, ?, ?, ?, ?);";

        using var conn = Open();
        using var find = new OleDbCommand(findSql, conn);
        find.Parameters.Add(new OleDbParameter("?", a.DevId));
        find.Parameters.Add(new OleDbParameter("?", a.ActivityId));
        var count = Convert.ToInt32(find.ExecuteScalar() ?? 0);

        try
        {
            if (count > 0)
            {
                using var cmd = new OleDbCommand(updateSql, conn);
                cmd.Parameters.Add(new OleDbParameter("?", a.Next.HasValue ? a.Next.Value.ToOADate() : (object)DBNull.Value));
                cmd.Parameters.Add(new OleDbParameter("?", a.Last.HasValue ? a.Last.Value.ToOADate() : (object)DBNull.Value));
                cmd.Parameters.Add(new OleDbParameter("?", a.TesterId));
                cmd.Parameters.Add(new OleDbParameter("?", a.DevId));
                cmd.Parameters.Add(new OleDbParameter("?", a.ActivityId));
                cmd.ExecuteNonQuery();
                return false;
            }
            else
            {
                using var cmd = new OleDbCommand(insertSql, conn);
                cmd.Parameters.Add(new OleDbParameter("?", a.DevId));
                cmd.Parameters.Add(new OleDbParameter("?", a.ActivityId));
                cmd.Parameters.Add(new OleDbParameter("?", a.Next.HasValue ? a.Next.Value.ToOADate() : (object)DBNull.Value));
                cmd.Parameters.Add(new OleDbParameter("?", a.Last.HasValue ? a.Last.Value.ToOADate() : (object)DBNull.Value));
                cmd.Parameters.Add(new OleDbParameter("?", a.TesterId));
                cmd.ExecuteNonQuery();
                return true;
            }
        }
        catch (OleDbException ex) when (IsLockError(ex))
        {
            throw new ActimedLockedException("Actimed-Datenbank gesperrt während UpsertIsActDev.", ex);
        }
    }

    private int InsertWithMaxIdPlusOne(string table, string idColumn, (string Name, object? Value)[] columns)
    {
        using var conn = Open();
        try
        {
            // ACE OLE-DB akzeptiert KEIN NZ() (das ist VBA, nur in Access selbst verfügbar).
            // Wir fragen MAX() ab und behandeln NULL/leer-Tabelle in C#.
            using var maxCmd = new OleDbCommand($"SELECT MAX({idColumn}) FROM {table};", conn);
            var raw = maxCmd.ExecuteScalar();
            var nextId = (raw == null || raw == DBNull.Value) ? 1 : Convert.ToInt32(raw) + 1;

            var allCols = new List<string> { idColumn };
            var allMarks = new List<string> { "?" };
            foreach (var (name, _) in columns) { allCols.Add(name); allMarks.Add("?"); }
            var sql = $"INSERT INTO {table} ({string.Join(",", allCols)}) VALUES ({string.Join(",", allMarks)});";

            using var cmd = new OleDbCommand(sql, conn);
            cmd.Parameters.Add(BuildParam(idColumn, nextId));
            foreach (var (colName, value) in columns)
                cmd.Parameters.Add(BuildParam(colName, value));
            cmd.ExecuteNonQuery();
            return nextId;
        }
        catch (OleDbException ex) when (IsLockError(ex))
        {
            throw new ActimedLockedException($"Actimed-Datenbank gesperrt während INSERT in {table}.", ex);
        }
    }

    /// <summary>
    /// Baut einen OleDbParameter mit explizitem Typ. ACE OLE-DB inferiert die Typen sonst aus
    /// dem .NET-Wert, was bei nullable values (DBNull) und bestimmten Spaltentypen zu
    /// "Data type mismatch in criteria expression" fuehren kann. Wir matchen hier die typischen
    /// A3_*-Spaltentypen. Spaltenname dient nur fuers Debugging — der Parameter wird positional
    /// gebunden, nicht ueber den Namen.
    /// </summary>
    private static OleDbParameter BuildParam(string colName, object? value)
    {
        var p = new OleDbParameter(colName, OleDbType.Variant);

        if (value is null || value == DBNull.Value)
        {
            // Defaulttyp Variant fuer NULL — ACE akzeptiert das beim INSERT problemlos.
            p.Value = DBNull.Value;
            return p;
        }

        switch (value)
        {
            case int i:
                p.OleDbType = OleDbType.Integer;
                p.Value = i;
                break;
            case long l:
                p.OleDbType = OleDbType.BigInt;
                p.Value = l;
                break;
            case bool b:
                p.OleDbType = OleDbType.Boolean;
                p.Value = b;
                break;
            case DateTime dt:
                // OleDb erwartet OA-Date als Double bei expliziter Typisierung — sicherer ist
                // Date direkt: ACE konvertiert dann selbst.
                p.OleDbType = OleDbType.Date;
                p.Value = dt;
                break;
            case double d:
                p.OleDbType = OleDbType.Double;
                p.Value = d;
                break;
            case string s:
                // Memo-Spalten (>255) brauchen LongVarWChar, sonst VarWChar mit explizitem Size.
                if (s.Length > 255)
                {
                    p.OleDbType = OleDbType.LongVarWChar;
                    p.Size = s.Length;
                }
                else
                {
                    p.OleDbType = OleDbType.VarWChar;
                    p.Size = 255;
                }
                p.Value = s;
                break;
            default:
                p.Value = value;
                break;
        }
        return p;
    }

    private static ActimedDevice ReadDevice(OleDbDataReader rdr) => new(
        DevId: GetInt(rdr, "DEV_ID"),
        CustId: GetInt(rdr, "CUST_ID"),
        InventoryNo: GetString(rdr, "DEV_InventoryNo"),
        SerialNo: GetString(rdr, "DEV_SerialNo"),
        DevTypeId: GetInt(rdr, "DEV_TYPE_ID"),
        LocationId: GetNullableInt(rdr, "LOCATION_ID"),
        StatusId: GetNullableInt(rdr, "STATUS_ID"),
        NextActivityId: GetNullableInt(rdr, "NEXTACTIVITY_ID"),
        Memo: GetNullableString(rdr, "DEV_Memo")
    );

    private static int GetInt(OleDbDataReader rdr, string col)
    {
        var v = rdr[col];
        if (v == null || v == DBNull.Value) return 0;
        return Convert.ToInt32(v);
    }

    private static int? GetNullableInt(OleDbDataReader rdr, string col)
    {
        var v = rdr[col];
        if (v == null || v == DBNull.Value) return null;
        return Convert.ToInt32(v);
    }

    private static string GetString(OleDbDataReader rdr, string col)
    {
        var v = rdr[col];
        return v == null || v == DBNull.Value ? string.Empty : Convert.ToString(v) ?? string.Empty;
    }

    private static string? GetNullableString(OleDbDataReader rdr, string col)
    {
        var v = rdr[col];
        return v == null || v == DBNull.Value ? null : Convert.ToString(v);
    }

    private static bool GetBool(OleDbDataReader rdr, string col)
    {
        var v = rdr[col];
        if (v == null || v == DBNull.Value) return false;
        if (v is bool b) return b;
        return Convert.ToInt64(v) != 0;
    }
}
