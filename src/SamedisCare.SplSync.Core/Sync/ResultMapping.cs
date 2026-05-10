namespace SamedisCare.SplSync.Core.Sync;

/// <summary>
/// Maps Actimed's free-form `TEST_Prüfergebnis` to Samedis' `test_result` enum.
/// Same set as in the reference repo (Tasks.cs::TryNormalizeTestResult).
/// </summary>
public static class ResultMapping
{
    /// <summary>Returns one of "passed" | "passed_conditionally" | "not_passed", or null if unmappable.</summary>
    public static string? FromActimed(string? pruefergebnis)
    {
        if (string.IsNullOrWhiteSpace(pruefergebnis)) return null;

        var normalized = pruefergebnis.Trim().ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss")
            .Replace(".", "").Replace(" ", "_");

        return normalized switch
        {
            "bestanden" or "ok" or "io" or "i_o" or "passed"          => "passed",
            "bedingt_bestanden" or "bedingt" or "unter_vorbehalt"
                or "passed_conditionally"                              => "passed_conditionally",
            "nicht_bestanden" or "durchgefallen" or "nicht_io"
                or "nicht_i_o" or "not_passed" or "failed"             => "not_passed",
            _ => null
        };
    }
}
