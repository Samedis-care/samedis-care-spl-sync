using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SamedisCare.SplSync.Core.Api;

/// <summary>
/// GET /api/{api_version}/user/tenants/{tenant_id}
///
/// We need at least `use_extended_device_locations` to know whether to interpret
/// device_location as ID (standard mode) or as a Building/Floor/Room hierarchy
/// (property mode). For our maintenance-issues sync the property-mode bit only
/// matters indirectly when we later resolve inventory locations.
///
/// Adapted from Samedis-care/samedis-care-external-sync `Tenant.cs`.
/// </summary>
public class Tenant
{
    public class Attributes
    {
        [JsonProperty("id")] public string? Id { get; set; }
        [JsonProperty("tenant_id")] public string? TenantId { get; set; }
        [JsonProperty("name")] public string? Name { get; set; }
        [JsonProperty("default_locale")] public string? DefaultLocale { get; set; }
        [JsonProperty("language")] public string? Language { get; set; }
        [JsonProperty("required_inventory_fields")] public List<string>? RequiredInventoryFields { get; set; }

        [JsonProperty("use_extended_device_locations", NullValueHandling = NullValueHandling.Ignore)]
        public bool UseExtendedDeviceLocations { get; set; } = false;

        [JsonProperty("use_profit_centers", NullValueHandling = NullValueHandling.Ignore)]
        public bool UseProfitCenters { get; set; } = false;

        [JsonExtensionData] public IDictionary<string, JToken>? AdditionalSettings { get; set; }
    }

    public class Data
    {
        [JsonProperty("id")] public string? Id { get; set; }
        [JsonProperty("type")] public string? Type { get; set; }
        [JsonProperty("attributes")] public Attributes? Attributes { get; set; }
    }

    public class Root
    {
        [JsonProperty("data")] public Data? Data { get; set; }
    }

    public class Settings
    {
        public string TenantId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool UseExtendedDeviceLocations { get; init; }
        public bool UseProfitCenters { get; init; }
        public string LocationMode => UseExtendedDeviceLocations ? "property" : "standard";
    }

    public static Settings GetSettings(RequestData client, string apiVersion, string tenantId, ISyncLog log)
    {
        var resource = $"/api/{apiVersion}/user/tenants/{tenantId}";
        var response = client.Get(resource);

        if (client.StatusCode is < 200 or >= 300 || string.IsNullOrWhiteSpace(response))
        {
            log.Warn($"Tenant settings request failed ({client.StatusCode}). Falling back to defaults.");
            return new Settings { TenantId = tenantId };
        }

        var root = JsonConvert.DeserializeObject<Root>(response);
        var attributes = root?.Data?.Attributes;
        if (attributes == null)
        {
            log.Warn("Tenant settings response had no attributes; falling back to defaults.");
            return new Settings { TenantId = tenantId };
        }

        return new Settings
        {
            TenantId = attributes.TenantId ?? attributes.Id ?? root?.Data?.Id ?? tenantId,
            Name = attributes.Name ?? string.Empty,
            UseExtendedDeviceLocations = attributes.UseExtendedDeviceLocations,
            UseProfitCenters = attributes.UseProfitCenters
        };
    }

    /// <summary>
    /// Lists all tenants the authenticated sync-user has access to.
    ///
    /// Samedis-care exposes the user's accessible tenants via GET /api/{v}/get_current_user,
    /// which returns
    ///     { "data": { "current_user": { "id": "...", "tenants": [ {id, name, short_name, full_name, ...}, ... ] } } }.
    ///
    /// We try that endpoint first; a couple of generic JSON:API fallbacks are kept in
    /// case a different Samedis deployment uses a different path. The first endpoint
    /// that returns 2xx with parseable tenants wins, all attempts get logged for diagnostics.
    /// </summary>
    public static IReadOnlyList<UserTenantSummary> ListUserTenants(
        RequestData client,
        string apiVersion,
        ISyncLog log)
    {
        var candidates = new[]
        {
            // Primary — samedis.care convention
            $"/api/{apiVersion}/get_current_user",
            // Generic JSON:API style (kept as fallback for non-samedis instances)
            $"/api/{apiVersion}/user/tenants?page[number]=1&page[limit]=200",
            $"/api/{apiVersion}/current_user/tenants?page[number]=1&page[limit]=200"
        };

        foreach (var resource in candidates)
        {
            log.Info($"ListUserTenants: trying GET {resource}");
            var response = client.Get(resource);
            var status = client.StatusCode;
            var bodyPreview = string.IsNullOrEmpty(response) ? "<empty>" :
                response.Length > 400 ? response.Substring(0, 400) + "..." : response;

            log.Info($"  -> HTTP {status}");
            log.Debug($"  -> body: {bodyPreview}");

            if (status is < 200 or >= 300 || string.IsNullOrWhiteSpace(response))
                continue;

            var parsed = TryParseTenants(response, log);
            if (parsed.Count > 0)
            {
                log.Info($"  -> {parsed.Count} tenant(s) parsed from {resource}");
                return parsed;
            }
            log.Warn($"  -> response 2xx but no tenants extracted; trying next variant");
        }

        log.Warn("ListUserTenants: no endpoint variant returned tenants. " +
                 "Check that the sync-user has at least one tenant assigned, " +
                 "or override the endpoint path in code.");
        return Array.Empty<UserTenantSummary>();
    }

    /// <summary>
    /// Tolerant parser. Accepts:
    ///   A) { "data": { "current_user": { "tenants": [ {id, name, short_name, full_name, ...} ] } } }
    ///      — samedis.care `get_current_user` shape
    ///   B) { "data": [ { id, type, attributes:{ name, tenant_id, ... } } ] }
    ///      — generic JSON:API list
    ///   C) { "data": { id, attributes:{ name, tenants:[ ... ] } } }
    ///      — JSON:API user resource with embedded tenants
    ///   D) { "data": { id, relationships:{ tenants:{ data:[...] } } }, "included":[...] }
    ///      — JSON:API with included resources
    /// </summary>
    private static List<UserTenantSummary> TryParseTenants(string responseJson, ISyncLog log)
    {
        var found = new List<UserTenantSummary>();
        try
        {
            var root = JObject.Parse(responseJson);
            var data = root["data"];
            if (data == null) return found;

            // Case A: samedis.care get_current_user shape
            var currentUserTenants = data["current_user"]?["tenants"] as JArray;
            if (currentUserTenants != null)
            {
                foreach (var item in currentUserTenants) TryAddTenant(item, found);
                return found;
            }

            // Case B: top-level array
            if (data is JArray arr)
            {
                foreach (var item in arr) TryAddTenant(item, found);
                return found;
            }

            // Case C: embedded under attributes
            var attr = data["attributes"] as JObject;
            if (attr != null)
            {
                var embedded = attr["tenants"] as JArray
                            ?? attr["accessible_tenants"] as JArray
                            ?? attr["user_tenants"] as JArray;
                if (embedded != null)
                {
                    foreach (var item in embedded) TryAddTenant(item, found);
                    return found;
                }
            }

            // Case D: JSON:API relationships
            var relTenants = data["relationships"]?["tenants"]?["data"] as JArray;
            if (relTenants != null)
            {
                foreach (var item in relTenants) TryAddTenant(item, found);
                var included = root["included"] as JArray;
                if (included != null)
                {
                    var nameById = new Dictionary<string, string>();
                    foreach (var inc in included)
                    {
                        var t = inc["type"]?.ToString();
                        if (t != "tenants" && t != "tenant") continue;
                        var id = inc["id"]?.ToString();
                        var name = inc["attributes"]?["full_name"]?.ToString()
                                ?? inc["attributes"]?["name"]?.ToString();
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                            nameById[id] = name!;
                    }
                    for (var i = 0; i < found.Count; i++)
                    {
                        if (string.IsNullOrEmpty(found[i].Name) && nameById.TryGetValue(found[i].TenantId, out var n))
                            found[i] = found[i] with { Name = n };
                    }
                }
                return found;
            }
        }
        catch (Exception ex)
        {
            log.Warn($"TryParseTenants: {ex.Message}");
        }
        return found;
    }

    /// <summary>
    /// Extracts id + display name from a tenant JSON entry. Tolerant against the two main
    /// shapes: flat (id/name at top-level, samedis.care style) and JSON:API (id at top,
    /// attributes nested). For display, prefer full_name -> short_name -> name -> tenant_name.
    /// </summary>
    private static void TryAddTenant(JToken item, List<UserTenantSummary> sink)
    {
        var attr = item["attributes"] ?? item;
        var id = item["id"]?.ToString() ?? attr["id"]?.ToString() ?? attr["tenant_id"]?.ToString();
        var name = attr["full_name"]?.ToString()
                ?? attr["name"]?.ToString()
                ?? attr["short_name"]?.ToString()
                ?? attr["tenant_name"]?.ToString()
                ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(id))
            sink.Add(new UserTenantSummary(id!, name));
    }
}

/// <summary>Lightweight DTO for the tenant-mapping dialog.</summary>
public record UserTenantSummary(string TenantId, string Name);
