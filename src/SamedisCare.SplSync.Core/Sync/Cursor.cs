using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SamedisCare.SplSync.Core.Sync;

/// <summary>
/// Per-tenant, per-kind ISO timestamp cursor. Backed by the sync_cursor table in StateDb.
/// Default fallback follows the reference repo: 2022-01-01T00:00:00.000+01:00.
/// </summary>
public class Cursor
{
    public const string KindDownload = "download";
    public const string KindUpload = "upload";

    private static readonly DateTimeOffset Default =
        new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.FromHours(1));

    private readonly StateDb _state;

    public Cursor(StateDb state) => _state = state;

    public DateTimeOffset Get(string tenantId, string kind)
    {
        using var conn = _state.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_run_iso FROM sync_cursor WHERE tenant_id = $t AND kind = $k LIMIT 1;";
        cmd.Parameters.AddWithValue("$t", tenantId);
        cmd.Parameters.AddWithValue("$k", kind);
        var raw = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(raw)) return Default;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed : Default;
    }

    public void Set(string tenantId, string kind, DateTimeOffset value)
    {
        using var conn = _state.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO sync_cursor (tenant_id, kind, last_run_iso)
            VALUES ($t, $k, $v)
            ON CONFLICT(tenant_id, kind) DO UPDATE SET last_run_iso = excluded.last_run_iso;";
        cmd.Parameters.AddWithValue("$t", tenantId);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$v", value.ToString("o", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }
}
