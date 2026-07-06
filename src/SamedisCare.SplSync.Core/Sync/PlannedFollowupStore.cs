namespace SamedisCare.SplSync.Core.Sync;

/// <summary>
/// Merkt sich, für welche abgeschlossene Prüfung (A3_FINISHED_TEST.TEST_ID) bereits eine
/// Folgemaßnahme in Samedis angelegt wurde. Idempotenz-Schutz: der Upload-Poll-Loop läuft alle
/// ~30 s und beide Upload-Modi (PNG/PDF) rufen denselben Kern auf — ohne diese Sperre würde bei
/// jedem Durchlauf eine neue geplante Prüfung erzeugt.
/// </summary>
public class PlannedFollowupStore
{
    private readonly StateDb _state;

    public PlannedFollowupStore(StateDb state) => _state = state;

    public bool Exists(string tenantId, string sourceTestId)
    {
        using var conn = _state.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM planned_followup WHERE tenant_id = $t AND source_test_id = $s LIMIT 1;";
        cmd.Parameters.AddWithValue("$t", tenantId);
        cmd.Parameters.AddWithValue("$s", sourceTestId);
        return cmd.ExecuteScalar() != null;
    }

    public void Record(string tenantId, string sourceTestId, string? createdIssueId)
    {
        using var conn = _state.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO planned_followup (tenant_id, source_test_id, created_issue_id)
            VALUES ($t, $s, $i)
            ON CONFLICT(tenant_id, source_test_id) DO UPDATE SET
                created_issue_id = excluded.created_issue_id;";
        cmd.Parameters.AddWithValue("$t", tenantId);
        cmd.Parameters.AddWithValue("$s", sourceTestId);
        cmd.Parameters.AddWithValue("$i", (object?)createdIssueId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
