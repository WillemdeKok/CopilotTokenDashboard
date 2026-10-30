using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolCallsInspector.Web.Services;

public static class JsonPretty
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Pretty-prints JSON when possible; returns the input unchanged otherwise (e.g. truncated payloads).</summary>
    public static string? Format(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var trimmed = value.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return value;
        }

        try
        {
            return JsonNode.Parse(value)?.ToJsonString(Indented) ?? value;
        }
        catch (JsonException)
        {
            return value;
        }
    }
}
