using System.Globalization;

namespace SamedisCare.SplSync.Core.Actimed;

/// <summary>
/// Helpers for the OLE-Auto-Date numbers Actimed stores in some columns
/// (e.g. TEST_DATE = 45278 = 2023-12-18) and for the German-comma decimals
/// in measurement values (132,3 instead of 132.3).
/// </summary>
public static class OaDate
{
    public static DateTime? TryParseOaDate(object? value)
    {
        if (value == null || value == DBNull.Value) return null;
        if (value is DateTime dt) return dt;
        if (value is double d) return DateTime.FromOADate(d);
        if (value is float f)  return DateTime.FromOADate(f);
        if (value is decimal m) return DateTime.FromOADate((double)m);
        if (value is int i)    return DateTime.FromOADate(i);
        if (value is long l)   return DateTime.FromOADate(l);

        var s = value.ToString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var dv))
            return DateTime.FromOADate(dv);
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            return parsed;
        return null;
    }

    public static double ToOaDate(DateTime dt) => dt.ToOADate();

    /// <summary>
    /// Parse a string that may use either '.' or ',' as decimal separator.
    /// Returns null if the string is null/empty/unparseable.
    /// </summary>
    public static double? TryParseDecimalLoose(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace(',', '.').Trim();
        return double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d : null;
    }

    /// <summary>Format a double in German locale (used for the Werteprotokoll PNG).</summary>
    public static string FormatDe(double value, int digits = 2)
        => value.ToString("F" + digits, CultureInfo.GetCultureInfo("de-DE"));
}
