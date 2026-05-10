using Microsoft.Data.Sqlite;

namespace SamedisCare.SplSync.Core.Sync;

/// <summary>Bridge table between Samedis issue IDs and Actimed DEV/ACTIVITY pairs.</summary>
public record IssueLinkRow(
    string TenantId,
    string SamedisIssueId,
    string? SamedisExternalId,
    int ActimedDevId,
    int ActimedActivityId,
    DateTimeOffset? PlannedDue,
    DateTimeOffset DownloadedAt);

public class IssueLinkStore
{
    private readonly StateDb _state;

    public IssueLinkStore(StateDb state) => _state = state;

    public void Upsert(IssueLinkRow row)
    {
        using var conn = _state.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO issue_link (tenant_id, samedis_issue_id, samedis_external_id, actimed_dev_id, actimed_activity_id, planned_due, downloaded_at)
            VALUES ($t, $sid, $eid, $dev, $act, $due, $dl)
            ON CONFLICT(tenant_id, samedis_issue_id) DO UPDATE SET
                samedis_external_id = excluded.samedis_external_id,
                actimed_dev_id      = excluded.actimed_dev_id,
                actimed_activity_id = excluded.actimed_activity_id,
                planned_due         = excluded.planned_due,
                downloaded_at       = excluded.downloaded_at;";
        cmd.Parameters.AddWithValue("$t",   row.TenantId);
        cmd.Parameters.AddWithValue("$sid", row.SamedisIssueId);
        cmd.Parameters.AddWithValue("$eid", (object?)row.SamedisExternalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dev", row.ActimedDevId);
        cmd.Parameters.AddWithValue("$act", row.ActimedActivityId);
        cmd.Parameters.AddWithValue("$due", row.PlannedDue.HasValue ? (object)row.PlannedDue.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("$dl",  row.DownloadedAt.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Findet den juengsten issue_link fuer ein Device — unabhaengig von der ActivityId.
    /// Praktisch fuer den Upload-Pfad, wo wir aus A3_FINISHED_TEST nicht mehr wissen, welche
    /// A3_ACTIVITY genau gelaufen ist (die A3_IS_ACT_DEV-Planung wurde beim Speichern der
    /// Pruefung nicht zwingend verbraucht). Wenn pro Device nur ein offenes Maintenance-Issue
    /// existiert (Normalfall), liefert das genau einen Treffer.
    /// </summary>
    public IssueLinkRow? FindByDev(string tenantId, int devId)
    {
        using var conn = _state.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT samedis_issue_id, samedis_external_id, actimed_activity_id, planned_due, downloaded_at
            FROM   issue_link
            WHERE  tenant_id = $t AND actimed_dev_id = $d
            ORDER BY downloaded_at DESC
            LIMIT 1;";
        cmd.Parameters.AddWithValue("$t", tenantId);
        cmd.Parameters.AddWithValue("$d", devId);
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read()) return null;

        DateTimeOffset? plannedDue = null;
        var rawDue = rdr["planned_due"] as string;
        if (!string.IsNullOrWhiteSpace(rawDue) && DateTimeOffset.TryParse(rawDue, out var parsed))
            plannedDue = parsed;

        var dl = DateTimeOffset.TryParse(rdr["downloaded_at"] as string, out var dlp) ? dlp : DateTimeOffset.UtcNow;

        return new IssueLinkRow(
            TenantId: tenantId,
            SamedisIssueId: rdr["samedis_issue_id"] as string ?? string.Empty,
            SamedisExternalId: rdr["samedis_external_id"] as string,
            ActimedDevId: devId,
            ActimedActivityId: Convert.ToInt32(rdr["actimed_activity_id"] ?? 0),
            PlannedDue: plannedDue,
            DownloadedAt: dl);
    }

    public IssueLinkRow? FindByDevAndActivity(string tenantId, int devId, int activityId)
    {
        using var conn = _state.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT samedis_issue_id, samedis_external_id, planned_due, downloaded_at
            FROM   issue_link
            WHERE  tenant_id = $t AND actimed_dev_id = $d AND actimed_activity_id = $a
            ORDER BY downloaded_at DESC
            LIMIT 1;";
        cmd.Parameters.AddWithValue("$t", tenantId);
        cmd.Parameters.AddWithValue("$d", devId);
        cmd.Parameters.AddWithValue("$a", activityId);
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read()) return null;

        DateTimeOffset? plannedDue = null;
        var rawDue = rdr["planned_due"] as string;
        if (!string.IsNullOrWhiteSpace(rawDue) && DateTimeOffset.TryParse(rawDue, out var parsed))
            plannedDue = parsed;

        var dl = DateTimeOffset.TryParse(rdr["downloaded_at"] as string, out var dlp) ? dlp : DateTimeOffset.UtcNow;

        return new IssueLinkRow(
            TenantId: tenantId,
            SamedisIssueId: rdr["samedis_issue_id"] as string ?? string.Empty,
            SamedisExternalId: rdr["samedis_external_id"] as string,
            ActimedDevId: devId,
            ActimedActivityId: activityId,
            PlannedDue: plannedDue,
            DownloadedAt: dl);
    }
}
