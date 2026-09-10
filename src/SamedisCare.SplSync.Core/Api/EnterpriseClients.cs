using Newtonsoft.Json;
using SamedisCare.Api.Http;
using SamedisCare.Helper.Logging;

namespace SamedisCare.SplSync.Core.Api;

/// <summary>
/// Die Kundenliste einer Service-Welt: welche Mandanten der Dienstleister betreut.
///
/// <c>GET /api/{v}/enterprise/tenants/{dienstleister}/clients</c>
///
/// Das ist das Gegenstück zu <c>/user/tenants</c> im direkten Zugriff. Der Unterschied ist
/// nicht kosmetisch: ein externer Dienstleister ist in aller Regel <b>kein Mitglied</b> der
/// Kundenmandanten und taucht dort auch nicht in der Nutzerliste auf — seine Kunden sieht
/// er ausschliesslich über diesen Endpunkt.
/// </summary>
public static class EnterpriseClients
{
    public class Attributes
    {
        [JsonProperty("id")] public string? Id { get; set; }
        [JsonProperty("name")] public string? Name { get; set; }
        [JsonProperty("name2")] public string? Name2 { get; set; }
        [JsonProperty("town")] public string? Town { get; set; }
        [JsonProperty("short_name")] public string? ShortName { get; set; }
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
        [JsonProperty("data")] public List<Data>? Data { get; set; }
        [JsonProperty("meta")] public Meta? Meta { get; set; }
    }

    /// <summary>Ein Kunde der Service-Welt, reduziert auf das, was die Oberfläche zeigt.</summary>
    public record Client(string TenantId, string Name);

    public static IReadOnlyList<Client> List(
        RequestData samedis, string apiVersion, string enterpriseTenantId, ISyncLog log)
    {
        if (string.IsNullOrWhiteSpace(enterpriseTenantId))
            throw new InvalidOperationException(
                "samedis.enterprise_tenant_id ist nicht gesetzt — ohne die Service-Welt gibt es keine Kundenliste.");

        var clients = new List<Client>();
        var page = 1;
        const int pageLimit = 200;

        while (true)
        {
            var resource = $"/api/{apiVersion}/enterprise/tenants/{enterpriseTenantId}/clients" +
                           $"?page[number]={page}&page[limit]={pageLimit}";

            var response = samedis.Get(resource);
            if (samedis.StatusCode is < 200 or >= 300)
                throw new InvalidOperationException(
                    $"GET {resource} -> HTTP {samedis.StatusCode}. " +
                    "Hat der Account Zugriff auf diese Service-Welt, und ist das Modul " +
                    "'samedis-care-enterprise' für den Mandanten aktiv?");

            var batch = JsonConvert.DeserializeObject<Root>(response)?.Data ?? new List<Data>();
            foreach (var c in batch)
            {
                var id = c.Id ?? c.Attributes?.Id;
                if (string.IsNullOrWhiteSpace(id)) continue;

                var name = new[] { c.Attributes?.Name, c.Attributes?.Name2 }
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .DefaultIfEmpty(id)
                    .First()!;

                clients.Add(new Client(id, name));
            }

            if (batch.Count < pageLimit) break;
            page++;
        }

        log.Info($"Service-Welt {enterpriseTenantId}: {clients.Count} Kundenmandant(en).");
        return clients;
    }
}
