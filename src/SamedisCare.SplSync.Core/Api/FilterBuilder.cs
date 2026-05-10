using System.Globalization;
using System.Text;
using System.Web;
using Newtonsoft.Json;

namespace SamedisCare.SplSync.Core.Api;

/// <summary>
/// Builds Samedis-style `gridfilter=...` JSON payloads, URL-encoded.
///
/// API-Schema laut public.yaml und der Wiki-Doku
/// (https://github.com/Samedis-care/samedis-care-community/wiki/03---API-Gridfilter):
///
///   {
///     "&lt;field&gt;": {
///       "filterType": "date" | "number" | "text" | "bool" | "set",   // Feld-Datentyp
///       "type":       "equals" | "greaterThan" | "contains" | ... ,  // Vergleichsoperator
///       // Werte je nach filterType:
///       //   date          -> "dateFrom" (und optional "dateTo" für inRange)
///       //   number/text/  -> "filter"   (und optional "filterTo" für inRange)
///       //     bool/set
///     }
///   }
///
/// Wichtig: `filterType` und `type` waren in einer fruehen Version dieser Klasse
/// vertauscht; die API antwortete dann mit
///   { "msg": { "error": "gridfilter_error", "message": "Grid filter error" } }.
///
/// Example:
///   var b = new FilterBuilder();
///   b.Add("updated_at", FilterBuilder.FilterType.GreaterThan, FilterBuilder.Type.Date, DateTime.UtcNow.AddDays(-1));
///   b.Add("device_number", FilterBuilder.FilterType.Equals, FilterBuilder.Type.Text, "12345");
///   var url = baseResource + "?gridfilter=" + b.Get();
/// </summary>
public class FilterBuilder
{
    public enum FilterType
    {
        Equals,
        NotEqual,
        Contains,
        NotContains,
        StartsWith,
        EndsWith,
        GreaterThan,
        LessThan,
        InRange
    }

    public enum Type
    {
        Text,
        Number,
        Date,
        Bool,
        Set
    }

    private readonly Dictionary<string, object> _filters = new();

    public void Clear() => _filters.Clear();

    public void Add(string field, FilterType filterType, Type valueType, object value)
    {
        var entry = new Dictionary<string, object>
        {
            ["filterType"] = MapType(valueType),    // <-- field type, e.g. "date"
            ["type"]       = MapFilter(filterType)  // <-- comparator, e.g. "greaterThan"
        };

        var formatted = FormatValue(value, valueType);

        // Date filters use dateFrom/dateTo; everything else uses 'filter' (and 'filterTo' for ranges).
        if (valueType == Type.Date)
            entry["dateFrom"] = formatted;
        else
            entry["filter"] = formatted;

        _filters[field] = entry;
    }

    public string Get()
    {
        if (_filters.Count == 0) return "{}";       // Samedis-Webview verwendet '{}' wenn leer
        var json = JsonConvert.SerializeObject(_filters);
        return HttpUtility.UrlEncode(json);
    }

    private static string MapType(Type t) => t switch
    {
        Type.Text   => "text",
        Type.Number => "number",
        Type.Date   => "date",
        Type.Bool   => "bool",
        Type.Set    => "set",
        _           => "text"
    };

    private static string MapFilter(FilterType f) => f switch
    {
        FilterType.Equals       => "equals",
        FilterType.NotEqual     => "notEqual",
        FilterType.Contains     => "contains",
        FilterType.NotContains  => "notContains",
        FilterType.StartsWith   => "startsWith",
        FilterType.EndsWith     => "endsWith",
        FilterType.GreaterThan  => "greaterThan",
        FilterType.LessThan     => "lessThan",
        FilterType.InRange      => "inRange",
        _                       => "equals"
    };

    /// <summary>
    /// Formats a value to the textual form expected inside the gridfilter JSON.
    /// Dates: yyyy-MM-dd (the API documentation shows date-level granularity).
    /// </summary>
    private static object FormatValue(object value, Type valueType)
    {
        return valueType switch
        {
            Type.Date when value is DateTime dt        => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Type.Date when value is DateTimeOffset dto => dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Type.Number when value is IFormattable n   => n.ToString(null, CultureInfo.InvariantCulture),
            _                                          => value
        };
    }
}
