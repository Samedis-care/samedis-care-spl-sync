using SamedisCare.SplSync.Core.Api;
using Newtonsoft.Json;

using SamedisCare.Api.Common;
using SamedisCare.Api.Http;
using SamedisCare.Helper.Logging;

namespace SamedisCare.SplSync.Core.Api;

/// <summary>
/// JSON:API model for /api/{version}/{tenant_scope}/issues.
/// Renamed from `Tasks.cs` in the reference repo because the public Samedis term
/// is "issue" — and we only ever deal with `issue_type=maintenance` here.
///
/// Note: this is the wire-level model. The mapping from A3_FINISHED_TEST → these
/// attributes lives in Sync/UploadEngine.cs.
/// </summary>
public class Issues
{
    public class Attributes
    {
        [JsonProperty("id")] public string? Id { get; set; }
        [JsonProperty("tenant_id")] public string? TenantId { get; set; }
        [JsonProperty("inventory_id")] public string? InventoryId { get; set; }
        [JsonProperty("inventory_device_number")] public string? InventoryDeviceNumber { get; set; }

        [JsonProperty("issue_number")] public int? IssueNumber { get; set; }
        [JsonProperty("issue_type")] public string? IssueType { get; set; }
        [JsonProperty("status")] public string? Status { get; set; }

        [JsonProperty("external_id")] public string? ExternalId { get; set; }
        [JsonProperty("title")] public string? Title { get; set; }

        [JsonProperty("date")] public string? Date { get; set; }
        [JsonProperty("done_at")] public string? DoneAt { get; set; }
        [JsonProperty("due_on")] public string? DueOn { get; set; }

        /// <summary>
        /// (nur bei issue_type=maintenance) Die zum Zeitpunkt der Vorgangs-Erstellung aus dem
        /// Inventar gecachten Service-Intervalle. Betrag (<see cref="ServiceInterval.Value"/>) plus
        /// Einheit (<see cref="ServiceInterval.Unit"/>: day/week/month/year, Default month). Wird
        /// über IntervalConversion in Monate umgerechnet, weil Actimed ACTIVITY_INTERVAL nur in
        /// Monaten führt. Quelle: samedis-public.yaml, Schema with_service_intervals.
        /// </summary>
        [JsonProperty("with_service_intervals")] public List<ServiceInterval>? WithServiceIntervals { get; set; }

        [JsonProperty("maintenance_type")] public string? MaintenanceType { get; set; }
        [JsonProperty("maintenance_performer")] public string? MaintenancePerformer { get; set; }
        [JsonProperty("maintenance_passed")] public bool? MaintenancePassed { get; set; }
        [JsonProperty("services")] public List<string>? Services { get; set; }

        [JsonProperty("responsible_id")] public string? ResponsibleId { get; set; }
        [JsonProperty("responsible_name")] public string? ResponsibleName { get; set; }

        [JsonProperty("test_result")] public string? TestResult { get; set; }
        [JsonProperty("test_comment")] public string? TestComment { get; set; }

        [JsonProperty("inventory_operation_status")] public string? InventoryOperationStatus { get; set; }

        [JsonProperty("created_at")] public string? CreatedAt { get; set; }
        [JsonProperty("updated_at")] public string? UpdatedAt { get; set; }
    }

    /// <summary>
    /// Ein Eintrag aus with_service_intervals (Schema in samedis-public.yaml). Ein Vorgang kann
    /// mehrere haben (z. B. maintenance + inspection); die Auswahl des passenden Eintrags macht
    /// der DownloadEngine (bevorzugt category=maintenance, Feinabgleich über label/services).
    /// </summary>
    public class ServiceInterval
    {
        /// <summary>Kategorie des Intervalls: "maintenance" oder "inspection".</summary>
        [JsonProperty("category")] public string? Category { get; set; }

        /// <summary>Label zur Identifikation (Sprache des Haupt-Mandanten), z. B. "STK".</summary>
        [JsonProperty("label")] public string? Label { get; set; }

        /// <summary>Anzahl der Einheiten.</summary>
        [JsonProperty("value")] public int? Value { get; set; }

        /// <summary>Einheit: day | week | month | year (Default month).</summary>
        [JsonProperty("unit")] public string? Unit { get; set; }
    }

    public class Data
    {
        [JsonProperty("id")] public string? Id { get; set; }
        [JsonProperty("type")] public string? Type { get; set; }
        [JsonProperty("attributes")] public Attributes? Attributes { get; set; }
    }

    public class Meta
    {
        [JsonProperty("total")] public int? Total { get; set; }
    }

    public class Root
    {
        [JsonProperty("data")]
        [JsonConverter(typeof(JsonApi.SingleOrArrayConverter<Data>))]
        public List<Data>? Data { get; set; }

        [JsonProperty("meta")] public Meta? Meta { get; set; }
    }

    /// <summary>
    /// Wraps an attributes-dictionary into the Samedis-erwartete Request-Struktur:
    ///   { "data": { "title": "...", "due_on": "...", ... } }
    ///
    /// Wichtig: Samedis nutzt NICHT die strikte JSON:API-Form mit { data: { type, attributes } }.
    /// Der Server validiert direkt auf params[:data][:title], wie ein curl mit
    /// `-F "data[title]=..."` (multipart/form-data) das auch tut. Wenn man die Felder in
    /// einen 'attributes'-Subkey packt, ignoriert der Server sie still und es kommt
    /// "Title cannot be blank" / "Due on cannot be blank" zurueck — selbst wenn alles gesetzt war.
    /// </summary>
    public static string BuildEnvelope(IDictionary<string, object> attributes)
    {
        var envelope = new { data = attributes };
        return JsonConvert.SerializeObject(envelope);
    }
}
