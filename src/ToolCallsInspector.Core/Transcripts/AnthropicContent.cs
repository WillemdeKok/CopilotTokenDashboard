using System.Text;
using System.Text.Json;

namespace ToolCallsInspector.Core.Transcripts;

/// <summary>Converts Anthropic Messages API content (as used by Claude Code transcripts and request bodies) to transcript messages.</summary>
internal static class AnthropicContent
{
    /// <summary>
    /// Adds a user message's text and tool results. Tool results become separate <c>tool</c> messages, named from
    /// <paramref name="toolNames"/>; the ids they reference are added to <paramref name="ids"/>.
    /// </summary>
    public static void ReadUser(JsonElement message, List<TranscriptMessage> messages, IReadOnlyDictionary<string, string> toolNames, List<string?>? ids = null)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            if (content.GetString() is { Length: > 0 } text)
            {
                messages.Add(new TranscriptMessage("user", [new TranscriptPart("text", text)]));
            }

            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (Str(block, "type") == "tool_result")
            {
                var id = Str(block, "tool_use_id");
                ids?.Add(id);
                var name = id is not null && toolNames.TryGetValue(id, out var known) ? known : null;
                messages.Add(new TranscriptMessage("tool", [new TranscriptPart("tool_call_response", BlockText(block), id, name)]));
            }
            else if (BlockText(block) is { Length: > 0 } text)
            {
                if (messages.Count > 0 && messages[^1].Role == "user")
                {
                    messages[^1].Parts.Add(new TranscriptPart("text", text));
                }
                else
                {
                    messages.Add(new TranscriptMessage("user", [new TranscriptPart("text", text)]));
                }
            }
        }
    }

    /// <summary>Adds an assistant message's text and tool calls, recording each tool call's name in <paramref name="toolNames"/>.</summary>
    public static void ReadAssistant(JsonElement message, List<TranscriptMessage> messages, Dictionary<string, string> toolNames, List<string?>? ids = null)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return;
        }

        var parts = new List<TranscriptPart>();
        if (content.ValueKind == JsonValueKind.String)
        {
            if (content.GetString() is { Length: > 0 } text)
            {
                parts.Add(new TranscriptPart("text", text));
            }
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                switch (block.ValueKind == JsonValueKind.Object ? Str(block, "type") : null)
                {
                    case "text" when Str(block, "text") is { Length: > 0 } text:
                        parts.Add(new TranscriptPart("text", text));
                        break;

                    case "tool_use" or "server_tool_use":
                        var id = Str(block, "id");
                        var name = Str(block, "name");
                        ids?.Add(id);
                        if (id is not null && name is not null)
                        {
                            toolNames[id] = name;
                        }

                        parts.Add(new TranscriptPart("tool_call", block.TryGetProperty("input", out var input) ? input.GetRawText() : null, id, name));
                        break;

                    // Thinking blocks of earlier turns are stripped from the context by the API, so they are left out.
                }
            }
        }

        if (parts.Count > 0)
        {
            messages.Add(new TranscriptMessage("assistant", parts));
        }
    }

    /// <summary>The <c>system</c> request parameter: a string or a list of text blocks.</summary>
    public static string? SystemText(JsonElement system)
    {
        if (system.ValueKind == JsonValueKind.String)
        {
            return system.GetString();
        }

        if (system.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var block in system.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object && Str(block, "text") is { Length: > 0 } part)
            {
                if (text.Length > 0) text.Append("\n\n");
                text.Append(part);
            }
        }

        return text.Length > 0 ? text.ToString() : null;
    }

    /// <summary>Text of a content block; a tool result's content is a string or a list of blocks.</summary>
    public static string? BlockText(JsonElement block)
    {
        switch (Str(block, "type"))
        {
            case "text":
                return Str(block, "text");
            case "image":
                return "[image]";
            case "document":
                return "[document]";
            case "tool_result":
                if (!block.TryGetProperty("content", out var content))
                {
                    return string.Empty;
                }

                if (content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString();
                }

                if (content.ValueKind != JsonValueKind.Array)
                {
                    return string.Empty;
                }

                var text = new StringBuilder();
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && BlockText(item) is { Length: > 0 } part)
                    {
                        if (text.Length > 0) text.Append('\n');
                        text.Append(part);
                    }
                }

                return text.ToString();
            default:
                return null;
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
