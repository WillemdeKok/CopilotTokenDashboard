using System.Text.Json;
using ToolCallsInspector.Core.Telemetry;

namespace ToolCallsInspector.Ingestion.Otlp;

/// <summary>Parses OTLP/JSON <c>ExportTraceServiceRequest</c> payloads (the format VS Code's otlp-http exporter sends).</summary>
public static class OtlpJsonTraceParser
{
    public static IReadOnlyList<RawSpan> Parse(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        using var doc = JsonDocument.ParseValue(ref reader);
        return Parse(doc.RootElement);
    }

    public static IReadOnlyList<RawSpan> Parse(JsonElement root)
    {
        var spans = new List<RawSpan>();
        if (!TryGet(root, "resourceSpans", out var resourceSpans) || resourceSpans.ValueKind != JsonValueKind.Array)
        {
            return spans;
        }

        foreach (var rs in resourceSpans.EnumerateArray())
        {
            var resourceAttrs = TryGet(rs, "resource", out var resource) && TryGet(resource, "attributes", out var ra)
                ? ParseAttributes(ra)
                : new Dictionary<string, object?>();

            if (!TryGet(rs, "scopeSpans", out var scopeSpans) && !TryGet(rs, "instrumentationLibrarySpans", out scopeSpans))
            {
                continue;
            }

            foreach (var ss in scopeSpans.EnumerateArray())
            {
                string? scopeName = null;
                if ((TryGet(ss, "scope", out var scope) || TryGet(ss, "instrumentationLibrary", out scope)) && TryGet(scope, "name", out var sn))
                {
                    scopeName = sn.GetString();
                }

                if (!TryGet(ss, "spans", out var spanArray))
                {
                    continue;
                }

                foreach (var s in spanArray.EnumerateArray())
                {
                    spans.Add(ParseSpan(s, resourceAttrs, scopeName));
                }
            }
        }

        return spans;
    }

    private static RawSpan ParseSpan(JsonElement s, IReadOnlyDictionary<string, object?> resourceAttrs, string? scopeName)
    {
        var events = new List<RawSpanEvent>();
        if (TryGet(s, "events", out var evs) && evs.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in evs.EnumerateArray())
            {
                events.Add(new RawSpanEvent
                {
                    Name = TryGet(e, "name", out var en) ? en.GetString() ?? string.Empty : string.Empty,
                    Time = ToTime(TryGet(e, "timeUnixNano", out var et) ? et : default),
                    Attributes = TryGet(e, "attributes", out var ea) ? ParseAttributes(ea) : new Dictionary<string, object?>(),
                });
            }
        }

        int statusCode = 0;
        string? statusMessage = null;
        if (TryGet(s, "status", out var status))
        {
            if (TryGet(status, "code", out var code))
            {
                statusCode = code.ValueKind == JsonValueKind.Number ? code.GetInt32() : code.GetString() switch
                {
                    "STATUS_CODE_OK" => 1,
                    "STATUS_CODE_ERROR" => 2,
                    _ => 0,
                };
            }

            if (TryGet(status, "message", out var msg))
            {
                statusMessage = msg.GetString();
            }
        }

        return new RawSpan
        {
            TraceId = OtlpIds.Normalize(TryGet(s, "traceId", out var tid) ? tid.GetString() : null) ?? "unknown",
            SpanId = OtlpIds.Normalize(TryGet(s, "spanId", out var sid) ? sid.GetString() : null) ?? Guid.NewGuid().ToString("N")[..16],
            ParentSpanId = OtlpIds.Normalize(TryGet(s, "parentSpanId", out var pid) ? pid.GetString() : null),
            Name = TryGet(s, "name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
            Kind = TryGet(s, "kind", out var kind) && kind.ValueKind == JsonValueKind.Number ? kind.GetInt32() : 0,
            StartTime = ToTime(TryGet(s, "startTimeUnixNano", out var st) ? st : default),
            EndTime = ToTime(TryGet(s, "endTimeUnixNano", out var et2) ? et2 : default),
            StatusCode = statusCode,
            StatusMessage = statusMessage,
            ScopeName = scopeName,
            Attributes = TryGet(s, "attributes", out var attrs) ? ParseAttributes(attrs) : new Dictionary<string, object?>(),
            ResourceAttributes = resourceAttrs,
            Events = events,
        };
    }

    internal static Dictionary<string, object?> ParseAttributes(JsonElement array)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var kv in array.EnumerateArray())
        {
            if (TryGet(kv, "key", out var key) && key.GetString() is { } k)
            {
                result[k] = TryGet(kv, "value", out var value) ? ParseAnyValue(value) : null;
            }
        }

        return result;
    }

    private static object? ParseAnyValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var prop in value.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "stringValue":
                    return prop.Value.GetString();
                case "boolValue":
                    return prop.Value.ValueKind == JsonValueKind.True || (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() == "true");
                case "intValue":
                    return prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetInt64() : long.TryParse(prop.Value.GetString(), out var l) ? l : null;
                case "doubleValue":
                    return prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetDouble() : double.TryParse(prop.Value.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
                case "bytesValue":
                    return prop.Value.GetString();
                case "arrayValue":
                    return TryGet(prop.Value, "values", out var values) && values.ValueKind == JsonValueKind.Array
                        ? values.EnumerateArray().Select(ParseAnyValue).ToList()
                        : new List<object?>();
                case "kvlistValue":
                    return TryGet(prop.Value, "values", out var kvs) ? ParseAttributes(kvs) : new Dictionary<string, object?>();
            }
        }

        return null;
    }

    private static DateTimeOffset ToTime(JsonElement element)
    {
        long nanos = element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out var n) ? n : (long)element.GetDouble(),
            JsonValueKind.String => long.TryParse(element.GetString(), out var n) ? n : 0,
            _ => 0,
        };
        return OtlpIds.FromUnixNanos(nanos);
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return value.ValueKind != JsonValueKind.Null;
        }

        value = default;
        return false;
    }
}

internal static class OtlpIds
{
    /// <summary>OTLP/JSON uses hex ids; some emitters send base64. Normalizes both to lowercase hex.</summary>
    public static string? Normalize(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        if (id.All(Uri.IsHexDigit))
        {
            return id.All(c => c == '0') ? null : id.ToLowerInvariant();
        }

        try
        {
            var bytes = Convert.FromBase64String(id);
            return bytes.All(b => b == 0) ? null : Convert.ToHexStringLower(bytes);
        }
        catch (FormatException)
        {
            return id;
        }
    }

    public static string? FromBytes(ReadOnlySpan<byte> bytes) =>
        bytes.IsEmpty || !bytes.ContainsAnyExcept((byte)0) ? null : Convert.ToHexStringLower(bytes);

    public static DateTimeOffset FromUnixNanos(long nanos) =>
        nanos <= 0 ? DateTimeOffset.UnixEpoch : DateTimeOffset.UnixEpoch.AddTicks(nanos / 100);

    public static DateTimeOffset FromUnixNanos(ulong nanos) => FromUnixNanos((long)Math.Min(nanos, long.MaxValue));
}
