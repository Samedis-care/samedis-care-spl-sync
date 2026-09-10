using System.Text.RegularExpressions;

namespace SamedisCare.SplSync.Core.Config;

/// <summary>
/// Frühzeitige, testbare Prüfung der Mandanten-Konfiguration. Fängt insbesondere den
/// Beispiel-Platzhalter <c>samedis_tenant_id: "&lt;...&gt;"</c> ab: die Tenant-ID landet
/// als Ordnername im Scratch-/State-Pfad, und Zeichen wie <c>&lt;</c>/<c>&gt;</c> sind unter
/// Windows verboten — sonst wirft <c>Directory.CreateDirectory</c> nur ein kryptisches
/// „Die Syntax für den Dateinamen … ist falsch".
/// </summary>
public static class ConfigValidation
{
    // Samedis-Tenant-IDs sind 24-stellige Mongo-ObjectIds (vgl. Kommentar in config.yml.example).
    private static readonly Regex ObjectIdShape = new("^[0-9a-fA-F]{24}$", RegexOptions.Compiled);

    /// <summary>
    /// Liefert eine lesbare deutsche Problembeschreibung für <paramref name="t"/> oder
    /// <c>null</c>, wenn die <c>samedis_tenant_id</c> in Ordnung ist.
    /// </summary>
    public static string? ValidateTenantId(TenantConfig t)
    {
        var id = t.SamedisTenantId;

        if (string.IsNullOrWhiteSpace(id))
            return "samedis_tenant_id fehlt";

        // Enthält Platzhalter-Klammern oder ein Zeichen, das nicht in einen Ordnernamen darf.
        if (id.IndexOf('<') >= 0 || id.IndexOf('>') >= 0 ||
            id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "samedis_tenant_id enthält Platzhalter/ungültige Zeichen ('<...>' aus der Beispiel-Config?)";

        if (!ObjectIdShape.IsMatch(id))
            return "samedis_tenant_id ist keine gültige 24-stellige Samedis-ID";

        return null;
    }

    /// <summary>
    /// Prüft den Zugriffsweg (<c>samedis.access_mode</c>). Die häufigste Fehlkonfiguration ist
    /// eine Service-Welt ohne <c>enterprise_tenant_id</c> — dann laufen alle Aufrufe gegen einen
    /// Pfad mit leerem Segment und der Server antwortet 404, was nach „keine Daten" aussieht
    /// statt nach „falsch konfiguriert". Liefert eine Liste lesbarer Probleme (leer = alles gut).
    /// </summary>
    public static IReadOnlyList<string> ValidateAccessMode(AppConfig cfg)
    {
        var problems = new List<string>();
        var mode = cfg.Samedis.AccessMode?.Trim() ?? "";

        if (!AccessModes.All.Contains(mode))
        {
            problems.Add(
                $"samedis.access_mode '{mode}' ist unbekannt. Erlaubt: " +
                string.Join(" | ", AccessModes.All));
            return problems;
        }

        if (!cfg.Samedis.IsEnterprise)
        {
            if (!string.IsNullOrWhiteSpace(cfg.Samedis.EnterpriseTenantId))
                problems.Add(
                    "samedis.enterprise_tenant_id ist gesetzt, access_mode steht aber auf 'tenant' — " +
                    "der Wert bleibt wirkungslos.");
            return problems;
        }

        var id = cfg.Samedis.EnterpriseTenantId;
        if (string.IsNullOrWhiteSpace(id))
            problems.Add("samedis.enterprise_tenant_id fehlt — bei access_mode 'enterprise' ist sie Pflicht.");
        else if (ValidateTenantId(new TenantConfig { SamedisTenantId = id }) is { } p)
            problems.Add($"samedis.enterprise_tenant_id: {p}");

        return problems;
    }
}
