namespace SamedisCare.SplSync.Core.Sync;

/// <summary>
/// Rechnet ein Samedis-Wartungsintervall (Betrag + Einheit) in ganze Monate um, weil Actimed
/// ACTIVITY_INTERVAL ausschließlich in Monaten führt.
///
/// Die erkannten Einheit-Tokens decken die gängigen englischen und deutschen Schreibweisen ab
/// (day/week/month/year, Tag/Woche/Monat/Jahr, Singular/Plural, Kürzel d/w/m/y). Falls die
/// samedis.care-API andere/zusätzliche Werte liefert, hier in <see cref="ParseUnit"/> ergänzen —
/// das ist die einzige Stelle, die angepasst werden muss.
///
/// Tag/Woche werden auf den nächsten Monat gerundet (Actimed kann nichts Feineres), Minimum 1 Monat.
/// </summary>
public static class IntervalConversion
{
    public enum Unit { Unknown, Day, Week, Month, Year }

    public static Unit ParseUnit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Unit.Unknown;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "d": case "day": case "days": case "tag": case "tage": case "täglich": case "taeglich":
                return Unit.Day;
            case "w": case "week": case "weeks": case "woche": case "wochen": case "wöchentlich": case "woechentlich":
                return Unit.Week;
            case "m": case "mon": case "month": case "months": case "monat": case "monate": case "monatlich":
                return Unit.Month;
            case "y": case "a": case "year": case "years": case "jahr": case "jahre": case "jährlich": case "jaehrlich":
                return Unit.Year;
            default:
                return Unit.Unknown;
        }
    }

    /// <summary>
    /// Liefert das Intervall in ganzen Monaten (mindestens 1), oder null wenn kein verwertbarer
    /// Betrag vorliegt. Eine unbekannte/fehlende Einheit wird als Monate interpretiert (samedis.care
    /// verwendet Monate als häufigste Einheit) — der Aufrufer loggt den Rohwert zur Kontrolle.
    /// </summary>
    public static int? ToMonths(int? value, string? unit)
    {
        if (value is not int v || v <= 0) return null;

        var months = ParseUnit(unit) switch
        {
            Unit.Day  => (int)Math.Round(v / 30.0, MidpointRounding.AwayFromZero),
            Unit.Week => (int)Math.Round(v * 7 / 30.0, MidpointRounding.AwayFromZero),
            Unit.Year => v * 12,
            _         => v, // Month + Unknown
        };

        return Math.Max(1, months);
    }

    /// <summary>
    /// Ganze Monate zwischen zwei Daten (Tag wird ignoriert), nie negativ. Wird beim Upload
    /// genutzt, um aus TEST_DATE → NEXT_TEST_DATE das Actimed-Prüfintervall abzuleiten, das an
    /// Samedis übertragen wird (Actimed berechnet NEXT_TEST_DATE = TEST_DATE + Intervall).
    /// </summary>
    public static int MonthsBetween(DateTime from, DateTime to)
        => Math.Max(0, (to.Year - from.Year) * 12 + (to.Month - from.Month));
}
