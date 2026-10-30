using System.Text.Json;

namespace ToolCallsInspector.Core.Analysis;

public enum MessagePartKind
{
    Text,
    ToolCall,
    ToolCallResponse,
    Reasoning,
    Other,
}

public sealed record ChatMessagePart(MessagePartKind Kind, string Content, string? ToolCallId = null, string? ToolName = null);

public sealed record ChatMessage(string Role, IReadOnlyList<ChatMessagePart> Parts, string? FinishReason = null)
{
    public string Text => string.Join("\n", Parts.Where(p => p.Kind == MessagePartKind.Text).Select(p => p.Content));
}

/// <summary>
/// Parses OTel GenAI message JSON (<c>gen_ai.input.messages</c> / <c>gen_ai.output.messages</c>).
/// Content may be truncated by the emitter, in which case parsing fails and <c>null</c> is returned.
/// </summary>
public static partial class GenAiMessageParser
{
    public static IReadOnlyList<ChatMessage>? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var messages = new List<ChatMessage>();
            foreach (var msg in doc.RootElement.EnumerateArray())
            {
                if (msg.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var role = msg.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "unknown";
                var finish = msg.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                var parts = new List<ChatMessagePart>();

                if (msg.TryGetProperty("parts", out var partsEl) && partsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in partsEl.EnumerateArray())
                    {
                        parts.Add(ParsePart(part));
                    }
                }
                else if (msg.TryGetProperty("content", out var content))
                {
                    parts.Add(new ChatMessagePart(MessagePartKind.Text, AsText(content)));
                }

                messages.Add(new ChatMessage(role, parts, finish));
            }

            return messages;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Extracts the first text of the first user message (used as the interaction prompt).</summary>
    public static string? FirstUserText(string? json) =>
        TryParse(json)?.FirstOrDefault(m => m.Role == "user")?.Text;

    /// <summary>
    /// The most recent user prompt in an LLM request (requests replay the whole conversation, so this is the current turn's prompt),
    /// skipping user messages that only contain client-injected context.
    /// </summary>
    public static string? LastUserPrompt(string? json) =>
        TryParse(json)?.Where(m => m.Role == "user").Select(m => CleanPrompt(m.Text)).LastOrDefault(t => !string.IsNullOrWhiteSpace(t));

    /// <summary>
    /// Removes context blocks that clients inject around the user's words (e.g. the Copilot CLI's
    /// <c>&lt;current_datetime&gt;</c> and <c>&lt;system_reminder&gt;</c>), so prompts read as typed.
    /// Also drops a trailing block that was cut off by preview truncation.
    /// </summary>
    public static string? CleanPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || !prompt.Contains('<'))
        {
            return prompt;
        }

        // Injected context blocks are dropped, but blocks that wrap what the user actually typed keep their text.
        var cleaned = InjectedBlock().Replace(prompt, m => UserContentTags.Contains(m.Groups["tag"].Value)
            ? " " + m.Groups["body"].Value + " "
            : " ");
        cleaned = TruncatedTrailingBlock().Replace(cleaned, string.Empty);
        cleaned = Whitespace().Replace(cleaned, " ").Trim();
        return cleaned.Length == 0 ? prompt : cleaned;
    }

    private static readonly HashSet<string> UserContentTags = new(StringComparer.Ordinal) { "user_message", "user_query", "user_request" };

    [System.Text.RegularExpressions.GeneratedRegex(@"<(?<tag>[a-z][a-z0-9_-]*)(\s[^>]*)?>(?<body>[\s\S]*?)</\k<tag>>")]
    private static partial System.Text.RegularExpressions.Regex InjectedBlock();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s<[a-z][a-z0-9_-]*(\s[^>]*)?>[^<]*(<[a-z][a-z0-9_-]*>[^<]*)*…?$")]
    private static partial System.Text.RegularExpressions.Regex TruncatedTrailingBlock();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex Whitespace();

    private static ChatMessagePart ParsePart(JsonElement part)
    {
        var type = part.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        string? Prop(string name) => part.TryGetProperty(name, out var v) ? AsText(v) : null;
        // The Copilot CLI tags MCP tool results with the server and tool; its tool spans use "{server}-{tool}".
        string? McpName() => Prop("mcp_tool_name") is { Length: > 0 } tool
            ? Prop("mcp_server_name") is { Length: > 0 } server ? $"{server}-{tool}" : tool
            : null;

        return type switch
        {
            "text" => new ChatMessagePart(MessagePartKind.Text, Prop("content") ?? string.Empty),
            "tool_call" => new ChatMessagePart(MessagePartKind.ToolCall, Prop("arguments") ?? string.Empty, Prop("id"), Prop("name") ?? McpName()),
            "tool_call_response" => new ChatMessagePart(MessagePartKind.ToolCallResponse, Prop("response") ?? Prop("content") ?? string.Empty, Prop("id"), Prop("name") ?? McpName()),
            "reasoning" => new ChatMessagePart(MessagePartKind.Reasoning, Prop("content") ?? string.Empty),
            _ => new ChatMessagePart(MessagePartKind.Other, part.GetRawText()),
        };
    }

    private static string AsText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => element.GetRawText(),
    };
}
