using System.Globalization;
using System.Text.Json;

namespace ToolCallsInspector.Core.Telemetry;

public static class AttributeExtensions
{
    public static string? GetString(this IReadOnlyDictionary<string, object?> attrs, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!attrs.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            var text = value switch
            {
                string s => s,
                IReadOnlyList<object?> list => string.Join(",", list.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };

            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return null;
    }

    public static long? GetLong(this IReadOnlyDictionary<string, object?> attrs, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!attrs.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            switch (value)
            {
                case long l: return l;
                case int i: return i;
                case double d: return (long)Math.Round(d);
                case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed): return parsed;
                case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedD): return (long)Math.Round(parsedD);
            }
        }

        return null;
    }

    public static double? GetDouble(this IReadOnlyDictionary<string, object?> attrs, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!attrs.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            switch (value)
            {
                case double d: return d;
                case long l: return l;
                case int i: return i;
                case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed): return parsed;
            }
        }

        return null;
    }

    public static string ToJson(this IReadOnlyDictionary<string, object?> attrs) =>
        JsonSerializer.Serialize(attrs);
}
