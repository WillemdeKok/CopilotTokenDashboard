using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ToolCallsInspector.Core.Analysis;
using ToolCallsInspector.Core.Storage;

namespace ToolCallsInspector.Web.Services;

/// <summary>One top-level part of a reconstructed request.</summary>
/// <param name="Key">Property name in the combined request JSON.</param>
/// <param name="Json">Pretty-printed value.</param>
/// <param name="Tokens">o200k estimate of the captured text.</param>
public sealed record RequestSection(string Key, string Label, string Json, int Chars, int Tokens, string? Note);

/// <summary>Everything that was sent with one LLM call, assembled into a single provider-neutral JSON document.</summary>
public sealed record FullRequest(IReadOnlyList<RequestSection> Sections, string Json, bool HasFullHistory);

public static class FullRequestBuilder
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static FullRequest Build(SpanRecord span, SpanContent content, ITokenCounter counter)
    {
        var fullHistory = content.TranscriptPath is not null;
        var messageCount = GenAiMessageParser.TryParse(content.InputMessages)?.Count;
        var toolCount = Parse(content.ToolDefinitions) is JsonArray tools ? tools.Count : (int?)null;

        var request = new JsonObject { ["model"] = span.RequestModel ?? span.Model };
        if (span.MaxOutputTokens is { } maxTokens)
        {
            request["max_tokens"] = maxTokens;
        }

        var sections = new List<RequestSection>();
        Add("system", "System prompt", content.SystemInstructions, null);
        Add("tools", toolCount is { } t ? $"Tool schemas · {t}" : "Tool schemas", content.ToolDefinitions, null);
        Add("messages", messageCount is { } m ? $"Messages · {m}" : "Messages", content.InputMessages, fullHistory
            ? "Full history, rebuilt from the local Copilot session log."
            : "As captured in telemetry. Some clients (e.g. Copilot CLI) only send the messages added during the current turn.");

        return new FullRequest(sections, request.ToJsonString(Indented), fullHistory);

        void Add(string key, string label, string? raw, string? note)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var value = Parse(raw) ?? JsonValue.Create(raw);
            request[key] = value;
            sections.Add(new RequestSection(key, label, value.ToJsonString(Indented), raw.Length, counter.Count(raw), note));
        }
    }

    /// <summary>Parses JSON arrays/objects; null for plain text or truncated payloads, which are kept as strings.</summary>
    private static JsonNode? Parse(string? raw)
    {
        var trimmed = raw?.TrimStart();
        if (string.IsNullOrEmpty(trimmed) || (trimmed[0] != '[' && trimmed[0] != '{'))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(raw!);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
