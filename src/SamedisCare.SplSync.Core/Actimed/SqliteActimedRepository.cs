using Microsoft.Data.Sqlite;

namespace SamedisCare.SplSync.Core.Actimed;

/// <summary>
/// SQLite-backed mirror of the Actimed schema. Used for unit tests and macOS dev.
/// Expects column names to match the `mdb-export` CSV headers (lower-cased via csv2sqlite.py),
/// e.g. dev_id, dev_inventoryno, etc.
///
/// Not all writes are fully implemented yet — this is a working skeleton for the Read side
/// (which the Upload pipeline depends on) and stub Inserts for tests against the Download pipeline.
/// </summary>
public class SqliteActimedRepository : IActimedRepository
{
    private readonly string _connectionString;

    public SqliteActimedRepository(string sqliteFilePath)
    {
        _connectionString = $"Data Source={sqliteFilePath};Mode=ReadWrite";
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    // ===== READ =====

    public IReadOnlyList<ActimedFinishedTest> GetFinishedTestsSince(DateTime since, IEnumerable<int> custIds)
    {
        var custList = custIds?.ToList() ?? new List<int>();
        if (custList.Count == 0) return Array.Empty<ActimedFinishedTest>();

        // Join A3_FINISHED_TEST with A3_DEV to get CUST_ID.
        var sinceOa = since.ToOADate();
        var custInClause = string.Join(",", custList);

        // ACHTUNG: Spaltennamen test_pruefberichtsnummer / test_pruefergebnis spiegeln das echte
        // SQLite-Schema (csv2sqlite.py uebernimmt die mdb-export-Header lower-cased) — keine Umlaute,
        // sonst werden die Spalten zur Laufzeit nicht gefunden.
        const string sqlTemplate = @"
            SELECT t.test_id, t.dev_id, t.test_date, t.tester_name, t.pvs_name,
                   t.test_pruefberichtsnummer, t.test_pruefergebnis,
                   t.last_test_date, t.next_test_date, t.memo, t.modifytime
            FROM   a3_finished_test t
            JOIN   a3_dev d ON d.dev_id = t.dev_id
            WHERE  d.cust_id IN ({0})
              AND  t.modifytime > {1}
            ORDER BY t.modifytime ASC;";

        var sql = string.Format(sqlTemplate, custInClause, sinceOa.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var results = new List<ActimedFinishedTest>();
        using var conn = Open();
        using var cmd = new SqliteCommand(sql, conn);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            results.Add(new ActimedFinishedTest(
                TestId: GetString(rdr, "test_id"),
                DevId: GetInt(rdr, "dev_id"),
                TestDate: OaDate.TryParseOaDate(rdr["test_date"]) ?? DateTime.MinValue,
                TesterName: GetString(rdr, "tester_name"),
                PvsName: GetString(rdr, "pvs_name"),
                Pruefberichtsnummer: GetString(rdr, "test_pruefberichtsnummer"),
                Pruefergebnis: GetString(rdr, "test_pruefergebnis"),
                LastTestDate: OaDate.TryParseOaDate(rdr["last_test_date"]),
                NextTestDate: OaDate.TryParseOaDate(rdr["next_test_date"]),
                Memo: GetNullableString(rdr, "memo"),
                ModifyTime: OaDate.TryParseOaDate(rdr["modifytime"]) ?? DateTime.MinValue
            ));
        }
        return results;
    }

    public IReadOnlyList<ActimedFinishedTestItem> GetTestItemsForTest(string testId)
    {
        const string sql = @"
            SELECT test_id, test_item_id, item_id, ws_dscr, func_name, test_item_success
            FROM   a3_finished_test_item
            WHERE  test_id = $tid
            ORDER BY item_id ASC;";

        var results = new List<ActimedFinishedTestItem>();
        using var conn = Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("$tid", testId);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            results.Add(new ActimedFinishedTestItem(
                TestId: GetString(rdr, "test_id"),
                TestItemId: GetString(rdr, "test_item_id"),
                ItemId: GetInt(rdr, "item_id"),
                WsDscr: GetString(rdr, "ws_dscr"),
                FuncName: GetString(rdr, "func_name"),
                Success: GetBool(rdr, "test_item_success")
            ));
        }
        return results;
    }

    public IReadOnlyList<ActimedFinishedTestResult> GetTestResultsForTest(string testId)
    {
        const string sql = @"
            SELECT r.test_item_id, r.test_item_result_no, r.item_dscr, r.item_unit,
                   r.item_value, r.limit1, r.limit2, r.test_item_result_success
            FROM   a3_finished_test_item_result r
            JOIN   a3_finished_test_item i ON i.test_item_id = r.test_item_id
            WHERE  i.test_id = $tid
            ORDER BY r.test_item_id, r.test_item_result_no;";

        var results = new List<ActimedFinishedTestResult>();
        using var conn = Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("$tid", testId);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            results.Add(new ActimedFinishedTestResult(
                TestItemId: GetString(rdr, "test_item_id"),
                ResultNo: GetInt(rdr, "test_item_result_no"),
                ItemDscr: GetString(rdr, "item_dscr"),
                Unit: GetNullableString(rdr, "item_unit"),
                Value: GetNullableString(rdr, "item_value"),
                Limit1: GetNullableString(rdr, "limit1"),
                Limit2: GetNullableString(rdr, "limit2"),
                Success: GetBool(rdr, "test_item_result_success")
            ));
        }
        return results;
    }

    public ActimedDevice? GetDeviceById(int devId)
    {
        const string sql = @"
            SELECT dev_id, cust_id, dev_inventoryno, dev_serialno, dev_type_id,
                   location_id, status_id, nextactivity_id, dev_memo
            FROM   a3_dev
            WHERE  dev_id = $id
            LIMIT 1;";
        using var conn = Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("$id", devId);
        using var rdr = cmd.ExecuteReader();
        return rdr.Read() ? ReadDevice(rdr) : null;
    }

    public IReadOnlyList<ActimedCustomer> ListCustomers()
    {
        const string sql = @"
            SELECT cust_id, COALESCE(cust_short,'') AS cust_short,
                   COALESCE(cust_no,'') AS cust_no,
                   COALESCE(cust_name1,'') AS cust_name1,
                   cust_name2
            FROM   a3_cust
            ORDER BY cust_id ASC;";
        var results = new List<ActimedCustomer>();
        using var conn = Open();
        using var cmd = new SqliteCommand(sql, conn);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            results.Add(new ActimedCustomer(
                CustId: GetInt(rdr, "cust_id"),
                Short:  GetString(rdr, "cust_short"),
                CustNo: GetString(rdr, "cust_no"),
                Name1:  GetString(rdr, "cust_name1"),
                Name2:  GetNullableString(rdr, "cust_name2")));
        }
        return results;
    }

    public ActimedDevice? FindDeviceByInventoryNo(string inventoryNo, IEnumerable<int> custIds)
    {
        var custList = custIds?.ToList() ?? new List<int>();
        if (custList.Count == 0) return null;
        var custInClause = string.Join(",", custList);
        var sql = $@"
            SELECT dev_id, cust_id, dev_inventoryno, dev_serialno, dev_type_id,
                   location_id, status_id, nextactivity_id, dev_memo
            FROM   a3_dev
            WHERE  dev_inventoryno = $inv AND cust_id IN ({custInClause})
            LIMIT 1;";
        using var conn = Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("$inv", inventoryNo);
        using var rdr = cmd.ExecuteReader();
        return rdr.Read() ? ReadDevice(rdr) : null;
    }

    public ActimedManufacturer? FindManufacturerByName(string name)
    {
        const string sql = "SELECT manu_id, manu_name1, manu_name2 FROM a3_manuf WHERE manu_name1 = $n LIMIT 1;";
        using var conn = Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("$n", name);
        using var rdr = cmd.ExecuteReader();
        return rdr.Read()
            ? new ActimedManufacturer(GetInt(rdr, "manu_id"), GetString(rdr, "manu_name1"), GetNullableString(rdr, "manu_name2"))
            : null;
    }

    public ActimedDeviceKind? FindDeviceKindByName(string name)
    {
        const string sql = "SELECT dev_kind_id, dev_kind_name, dev_kind_dimdinr FROM a3_dev_kind WHERE TRIM(dev_kind_name) = $n LIMIT 1;";
        using var conn = Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("$n", name);
        using var rdr = cmd.ExecuteReader();
        return rdr.Read()
            ? new ActimedDeviceKind(GetInt(rdr, "dev_kind_id"), GetString(rdr, "dev_kind_name"), GetNullableString(rdr, "dev_kind_dimdinr"))
            : null;
    }

    public ActimedDeviceType? FindDeviceTypeByName(string name)
    {
        const string sql = "SELECT dev_type_id, manu_id, dev_kind_id, dev_type_name, dev_type_modell FROM a3_dev_type WHERE dev_type_name = $n LIMIT 1;";
        using var conn = Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("$n", name);
        using var rdr = cmd.ExecuteReader();
        return rdr.Read()
            ? new ActimedDeviceType(GetInt(rdr, "dev_type_id"), GetInt(rdr, "manu_id"), GetInt(rdr, "dev_kind_id"),
                GetString(rdr, "dev_type_name"), GetString(rdr, "dev_type_modell"))
            : null;
    }

    public ActimedActivityKind? FindActivityKindByName(string kindName)
    {
        const string sql = "SELECT kind_id, kind_name, kind_dscr FROM a3_activity_kind WHERE TRIM(kind_name) = $n LIMIT 1;";
        using var conn = Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("$n", kindName.Trim());
        using var rdr = cmd.ExecuteReader();
        return rdr.Read()
            ? new ActimedActivityKind(GetInt(rdr, "kind_id"), GetString(rdr, "kind_name"), GetNullableString(rdr, "kind_dscr"))
            : null;
    }

    public ActimedActivity? FindActivityByKindId(int kindId)
    {
        const string sql = "SELECT activity_id, test_spec_id, kind_id, activity_interval, activity_name FROM a3_activity WHERE kind_id = $k LIMIT 1;";
        using var conn = Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("$k", kindId);
        using var rdr = cmd.ExecuteReader();
        return rdr.Read()
            ? new ActimedActivity(GetInt(rdr, "activity_id"), GetInt(rdr, "test_spec_id"), GetInt(rdr, "kind_id"),
                GetInt(rdr, "activity_interval"), GetString(rdr, "activity_name"))
            : null;
    }

    public ActimedActivity? FindActivityByName(string activityName)
    {
        if (string.IsNullOrWhiteSpace(activityName)) return null;
        const string sql = "SELECT activity_id, test_spec_id, kind_id, activity_interval, activity_name FROM a3_activity WHERE activity_name = $n LIMIT 1;";
        using var conn = Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("$n", activityName);
        using var rdr = cmd.ExecuteReader();
        return rdr.Read()
            ? new ActimedActivity(GetInt(rdr, "activity_id"), GetInt(rdr, "test_spec_id"), GetInt(rdr, "kind_id"),
                GetInt(rdr, "activity_interval"), GetString(rdr, "activity_name"))
            : null;
    }

    // ===== WRITE — minimal SQLite-side stubs (full impl on the OleDb side) =====

    public int InsertManufacturer(ActimedManufacturer manu)
        => InsertWithMaxIdPlusOne("a3_manuf", "manu_id",
            new Dictionary<string, object?>
            {
                ["manu_name1"] = manu.Name1,
                ["manu_name2"] = manu.Name2 ?? ""
            });

    public int InsertDeviceKind(ActimedDeviceKind kind)
        => InsertWithMaxIdPlusOne("a3_dev_kind", "dev_kind_id",
            new Dictionary<string, object?>
            {
                ["dev_kind_name"] = kind.Name,
                ["dev_kind_dimdinr"] = kind.DimdiNr ?? ""
            });

    public int InsertDeviceType(ActimedDeviceType type)
        => InsertWithMaxIdPlusOne("a3_dev_type", "dev_type_id",
            new Dictionary<string, object?>
            {
                ["manu_id"] = type.ManuId,
                ["dev_kind_id"] = type.DevKindId,
                ["dev_type_name"] = type.Name,
                ["dev_type_modell"] = type.Modell
            });

    public int InsertDevice(ActimedDevice device)
        => InsertWithMaxIdPlusOne("a3_dev", "dev_id",
            new Dictionary<string, object?>
            {
                ["cust_id"]         = device.CustId,
                ["dev_inventoryno"] = device.InventoryNo,
                ["dev_serialno"]    = device.SerialNo,
                ["dev_type_id"]     = device.DevTypeId,
                ["location_id"]     = device.LocationId ?? 1,
                ["status_id"]       = device.StatusId   ?? 2,
                ["cost_centre_id"]  = 1,
                ["resp_person_id"]  = 1,
                ["supplier_id"]     = 1,
                ["nextactivity_id"] = device.NextActivityId ?? 0,
                ["dev_memo"]        = device.Memo ?? "created_by=spl-sync"
            });

    public int InsertActivity(ActimedActivity activity)
        => InsertWithMaxIdPlusOne("a3_activity", "activity_id",
            new Dictionary<string, object?>
            {
                ["test_spec_id"] = activity.TestSpecId,
                ["kind_id"] = activity.KindId,
                ["activity_interval"] = activity.IntervalMonths,
                ["activity_name"] = activity.Name
            });

    public bool RepairDeviceForeignKeys(int devId)
    {
        using var conn = Open();
        int loc, sta, cc, rp, sup;
        using (var read = new SqliteCommand(
            "SELECT location_id, status_id, cost_centre_id, resp_person_id, supplier_id FROM a3_dev WHERE dev_id = $id LIMIT 1;",
            conn))
        {
            read.Parameters.AddWithValue("$id", devId);
            using var rdr = read.ExecuteReader();
            if (!rdr.Read()) return false;
            loc = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
            sta = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
            cc  = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2);
            rp  = rdr.IsDBNull(3) ? 0 : rdr.GetInt32(3);
            sup = rdr.IsDBNull(4) ? 0 : rdr.GetInt32(4);
        }

        if (loc != 0 && sta != 0 && cc != 0 && rp != 0 && sup != 0) return false;

        using var upd = new SqliteCommand(
            "UPDATE a3_dev SET location_id=$loc, status_id=$sta, cost_centre_id=$cc, resp_person_id=$rp, supplier_id=$sup WHERE dev_id=$id;",
            conn);
        upd.Parameters.AddWithValue("$loc", loc == 0 ? 1 : loc);
        upd.Parameters.AddWithValue("$sta", sta == 0 ? 2 : sta);
        upd.Parameters.AddWithValue("$cc",  cc  == 0 ? 1 : cc);
        upd.Parameters.AddWithValue("$rp",  rp  == 0 ? 1 : rp);
        upd.Parameters.AddWithValue("$sup", sup == 0 ? 1 : sup);
        upd.Parameters.AddWithValue("$id",  devId);
        upd.ExecuteNonQuery();
        return true;
    }

    public bool UpsertIsActDev(ActimedIsActDev assignment)
    {
        const string findSql = "SELECT 1 FROM a3_is_act_dev WHERE dev_id = $d AND activity_id = $a LIMIT 1;";
        const string updateSql = @"UPDATE a3_is_act_dev SET act_dev_next = $next, act_dev_last = $last, tester_id = $t WHERE dev_id = $d AND activity_id = $a;";
        const string insertSql = @"INSERT INTO a3_is_act_dev (dev_id, activity_id, act_dev_next, act_dev_last, tester_id) VALUES ($d, $a, $next, $last, $t);";

        using var conn = Open();
        using var find = new SqliteCommand(findSql, conn);
        find.Parameters.AddWithValue("$d", assignment.DevId);
        find.Parameters.AddWithValue("$a", assignment.ActivityId);
        var exists = find.ExecuteScalar() != null;

        using var cmd = new SqliteCommand(exists ? updateSql : insertSql, conn);
        cmd.Parameters.AddWithValue("$d", assignment.DevId);
        cmd.Parameters.AddWithValue("$a", assignment.ActivityId);
        cmd.Parameters.AddWithValue("$next", assignment.Next.HasValue ? (object)assignment.Next.Value.ToOADate() : DBNull.Value);
        cmd.Parameters.AddWithValue("$last", assignment.Last.HasValue ? (object)assignment.Last.Value.ToOADate() : DBNull.Value);
        cmd.Parameters.AddWithValue("$t", assignment.TesterId);
        cmd.ExecuteNonQuery();
        return !exists; // true = newly inserted, false = updated
    }

    // ===== Internals =====

    private int InsertWithMaxIdPlusOne(string table, string idColumn, IDictionary<string, object?> columns)
    {
        using var conn = Open();
        using var maxCmd = new SqliteCommand($"SELECT COALESCE(MAX({idColumn}), 0) + 1 FROM {table};", conn);
        var nextId = Convert.ToInt32(maxCmd.ExecuteScalar());

        var allCols = new List<string> { idColumn };
        var allParams = new List<string> { "$id" };
        var i = 0;
        var values = new Dictionary<string, object?> { ["$id"] = nextId };
        foreach (var kv in columns)
        {
            var pname = "$p" + (i++);
            allCols.Add(kv.Key);
            allParams.Add(pname);
            values[pname] = kv.Value ?? DBNull.Value;
        }

        var sql = $"INSERT INTO {table} ({string.Join(",", allCols)}) VALUES ({string.Join(",", allParams)});";
        using var cmd = new SqliteCommand(sql, conn);
        foreach (var (pname, pval) in values)
            cmd.Parameters.AddWithValue(pname, pval ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return nextId;
    }

    private static ActimedDevice ReadDevice(SqliteDataReader rdr) => new(
        DevId: GetInt(rdr, "dev_id"),
        CustId: GetInt(rdr, "cust_id"),
        InventoryNo: GetString(rdr, "dev_inventoryno"),
        SerialNo: GetString(rdr, "dev_serialno"),
        DevTypeId: GetInt(rdr, "dev_type_id"),
        LocationId: GetNullableInt(rdr, "location_id"),
        StatusId: GetNullableInt(rdr, "status_id"),
        NextActivityId: GetNullableInt(rdr, "nextactivity_id"),
        Memo: GetNullableString(rdr, "dev_memo")
    );

    private static int GetInt(SqliteDataReader rdr, string col)
    {
        var v = rdr[col];
        if (v == null || v == DBNull.Value) return 0;
        return Convert.ToInt32(v);
    }

    private static int? GetNullableInt(SqliteDataReader rdr, string col)
    {
        var v = rdr[col];
        if (v == null || v == DBNull.Value) return null;
        return Convert.ToInt32(v);
    }

    private static string GetString(SqliteDataReader rdr, string col)
    {
        var v = rdr[col];
        return v == null || v == DBNull.Value ? string.Empty : Convert.ToString(v) ?? string.Empty;
    }

    private static string? GetNullableString(SqliteDataReader rdr, string col)
    {
        var v = rdr[col];
        return v == null || v == DBNull.Value ? null : Convert.ToString(v);
    }

    private static bool GetBool(SqliteDataReader rdr, string col)
    {
        var v = rdr[col];
        if (v == null || v == DBNull.Value) return false;
        if (v is bool b) return b;
        if (v is long l) return l != 0;
        if (v is int i) return i != 0;
        var s = v.ToString();
        return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
    }
}
