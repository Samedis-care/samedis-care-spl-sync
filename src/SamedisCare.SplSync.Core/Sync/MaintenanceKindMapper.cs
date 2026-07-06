using System.Text.RegularExpressions;
using SamedisCare.SplSync.Core.Config;

namespace SamedisCare.SplSync.Core.Sync;

/// <summary>Resultat eines Mapping-Lookups: die zu nutzende Tätigkeitsart und (falls konfiguriert)
/// die konkrete A3_ACTIVITY mit der hinterlegten Prüfvorschrift bzw. — für das Auto-Anlegen —
/// die zu verwendende Prüfvorschrift (TEST_SPEC) und das Prüfintervall.</summary>
public record MaintenanceKindMatch(
    string ActimedKind,
    string? ActimedActivityName,
    string? ActimedTestSpecName = null,
    int? ActimedTestSpecId = null,
    int? IntervalMonths = null);

/// <summary>
/// Maps free-form Samedis service/title strings to a curated Actimed
/// A3_ACTIVITY_KIND.KIND_NAME (e.g. "MPBe_§11_STK/DGUV V3"). Implements 5.5
/// from CLAUDE.md: first matching regex wins; entry with null `Match` is the default.
/// Liefert zusätzlich optional einen ActivityName (= A3_ACTIVITY.ACTIVITY_NAME), damit der
/// DownloadEngine die existierende Tätigkeit mit ihrer Prüfvorschrift wiederverwenden kann.
/// </summary>
public class MaintenanceKindMapper
{
    private readonly List<(Regex? Regex, MaintenanceKindMatch Result)> _rules = new();
    private readonly MaintenanceKindMatch _default;

    public MaintenanceKindMapper(IEnumerable<MaintenanceKindMappingEntry> entries)
    {
        MaintenanceKindMatch defaultMatch = new("MPBe_§7_Wartung/Inspektion", null);
        foreach (var e in entries)
        {
            var result = new MaintenanceKindMatch(
                e.ActimedKind, e.ActimedActivityName,
                e.ActimedTestSpecName, e.ActimedTestSpecId, e.ActimedActivityIntervalMonths);

            if (string.IsNullOrWhiteSpace(e.Match))
            {
                if (!string.IsNullOrWhiteSpace(e.ActimedKind)) defaultMatch = result;
                continue;
            }
            try
            {
                _rules.Add((new Regex(e.Match, RegexOptions.IgnoreCase | RegexOptions.Compiled), result));
            }
            catch (ArgumentException)
            {
                // Invalid regex — treat as literal substring match.
                var literal = Regex.Escape(e.Match);
                _rules.Add((new Regex(literal, RegexOptions.IgnoreCase | RegexOptions.Compiled), result));
            }
        }
        _default = defaultMatch;
    }

    /// <summary>
    /// Liefert das passende Mapping-Resultat (nie null). ActimedActivityName ist optional.
    /// </summary>
    public MaintenanceKindMatch Resolve(string? maintenanceType, string? title, IEnumerable<string>? services)
    {
        var combined = string.Join(" | ",
            new[] { maintenanceType, title, services == null ? "" : string.Join(",", services) }
            .Where(s => !string.IsNullOrWhiteSpace(s))!);

        foreach (var (regex, result) in _rules)
        {
            if (regex != null && regex.IsMatch(combined))
                return result;
        }
        return _default;
    }

    /// <summary>Backward-compat: liefert nur den Kind-Namen.</summary>
    public string Map(string? maintenanceType, string? title, IEnumerable<string>? services)
        => Resolve(maintenanceType, title, services).ActimedKind;
}
