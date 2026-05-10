using Microsoft.Data.Sqlite;

namespace SamedisCare.SplSync.Core.Sync;

/// <summary>
/// Local SQLite state DB. Holds the inbox (download jobs), outbox (upload jobs),
/// and the issue_link mapping table that bridges Samedis issue IDs to the
/// Actimed DEV_ID/ACTIVITY_ID pair (because A3_FINISHED_TEST itself doesn't
/// know about Samedis IDs).
///
/// Schema is created idempotently on first call.
/// </summary>
public class StateDb
{
    public string ConnectionString { get; }

    public StateDb(string sqlitePath)
    {
        var dir = Path.GetDirectoryName(sqlitePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        ConnectionString = $"Data Source={sqlitePath};Mode=ReadWriteCreate";
        EnsureSchema();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    private void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS inbox (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id           TEXT NOT NULL,
                samedis_issue_id    TEXT NOT NULL,
                samedis_payload_json TEXT NOT NULL,
                status              TEXT NOT NULL DEFAULT 'pending',
                attempts            INTEGER NOT NULL DEFAULT 0,
                last_error          TEXT,
                created_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(tenant_id, samedis_issue_id)
            );

            CREATE TABLE IF NOT EXISTS outbox (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id           TEXT NOT NULL,
                test_id             TEXT NOT NULL,
                payload_json        TEXT NOT NULL,
                png_path            TEXT,
                pdf_path            TEXT,
                status              TEXT NOT NULL DEFAULT 'pending',
                attempts            INTEGER NOT NULL DEFAULT 0,
                last_error          TEXT,
                created_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(tenant_id, test_id)
            );

            CREATE TABLE IF NOT EXISTS issue_link (
                id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id               TEXT NOT NULL,
                samedis_issue_id        TEXT NOT NULL,
                samedis_external_id     TEXT,
                actimed_dev_id          INTEGER NOT NULL,
                actimed_activity_id     INTEGER NOT NULL,
                planned_due             TEXT,
                downloaded_at           TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(tenant_id, samedis_issue_id)
            );

            CREATE TABLE IF NOT EXISTS sync_cursor (
                tenant_id           TEXT NOT NULL,
                kind                TEXT NOT NULL,    -- 'download' | 'upload'
                last_run_iso        TEXT NOT NULL,
                PRIMARY KEY (tenant_id, kind)
            );

            CREATE INDEX IF NOT EXISTS idx_inbox_status  ON inbox(status, tenant_id);
            CREATE INDEX IF NOT EXISTS idx_outbox_status ON outbox(status, tenant_id);
            CREATE INDEX IF NOT EXISTS idx_link_dev      ON issue_link(tenant_id, actimed_dev_id, actimed_activity_id);
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Löscht den Cursor-Eintrag für einen Mandanten + Kind. Der nächste Sync-Lauf
    /// startet dann ab dem Konfig-Default-Datum (effektiv: ein Voll-Resync).
    /// </summary>
    public void ResetCursor(string tenantId, string kind)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sync_cursor WHERE tenant_id = $t AND kind = $k;";
        cmd.Parameters.AddWithValue("$t", tenantId);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.ExecuteNonQuery();
    }
}
