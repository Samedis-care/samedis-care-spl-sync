using System.Data;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SamedisCare.SplSync.Core.Api;

/// <summary>
/// Small grab-bag of helpers. Only what we actually need from the reference repo
/// (vs. its larger Helper.cs which also had file/CSV utilities we don't use here).
/// </summary>
public static class Helper
{
    /// <summary>Extract `data[0].id` (or `data.id` if data is a single object) from a JSON:API response.</summary>
    public static string? ExtractDataId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var root = JsonConvert.DeserializeObject<JObject>(json);
            var data = root?["data"];
            if (data == null) return null;
            if (data is JArray arr)
                return arr.FirstOrDefault()?["id"]?.ToString();
            return data["id"]?.ToString();
        }
        catch { return null; }
    }

    public static bool TryParseInt(string? value, out int parsed)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);

    /// <summary>
    /// Boolean parser that accepts true/false, yes/no, ja/nein, 1/0 — same set as the reference.
    /// </summary>
    public static bool TryParseBool(string? value, out bool parsed)
    {
        parsed = false;
        if (string.IsNullOrWhiteSpace(value)) return false;
        switch (value.Trim().ToLowerInvariant())
        {
            case "true": case "yes": case "ja": case "1":
                parsed = true;  return true;
            case "false": case "no": case "nein": case "0":
                parsed = false; return true;
            default:
                return false;
        }
    }

    public static string GetRowValue(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column)) return string.Empty;
        var v = row[column];
        return v == null || v == DBNull.Value ? string.Empty : (v.ToString() ?? string.Empty);
    }

    public static void AddStringAttribute(IDictionary<string, object> attributes, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        attributes[key] = value;
    }

    /// <summary>
    /// Newtonsoft converter that accepts both `data: {...}` (single) and `data: [...]` (array)
    /// — JSON:API responses sometimes return one or many.
    /// </summary>
    public class SingleOrArrayConverter<T> : JsonConverter
    {
        public override bool CanConvert(System.Type objectType) => objectType == typeof(List<T>);

        public override object ReadJson(JsonReader reader, System.Type objectType, object? existingValue, JsonSerializer serializer)
        {
            var token = JToken.Load(reader);
            if (token.Type == JTokenType.Array)
                return token.ToObject<List<T>>(serializer) ?? new List<T>();
            var single = token.ToObject<T>(serializer);
            return single == null ? new List<T>() : new List<T> { single };
        }

        public override bool CanWrite => false;

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
            => throw new NotImplementedException();
    }
}
