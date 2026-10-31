using System.Text;
using System.Text.Json;
using ToolCallsInspector.Core.Normalization;
using ToolCallsInspector.Core.Storage;

namespace ToolCallsInspector.Core.Transcripts;

/// <summary>Replaces the partial prompt captured in telemetry with the full prompt from a local session log.</summary>
public sealed class TranscriptEnricher(ITranscriptSource source)
{
    public IReadOnlyDictionary<string, SpanContent> Enrich(IReadOnlyDictionary<string, SpanContent> content)
    {
        Dictionary<string, SpanContent>? enriched = null;
        foreach (var (id, item) in content)
        {
            if (item.TranscriptPath is not null || (item.InputMessages is null && item.OutputMessages is null)
                || ReadIds(item.AttributesJson) is not ({ } conversationId, { } responseId)
                || source.TryGetPrompt(conversationId, responseId) is not { } prompt)
            {
                continue;
            }

            enriched ??= new Dictionary<string, SpanContent>(content, StringComparer.Ordinal);
            enriched[id] = new SpanContent
            {
                Id = item.Id,
                InputMessages = prompt.InputMessagesJson,
                OutputMessages = item.OutputMessages,
                SystemInstructions = item.SystemInstructions ?? prompt.SystemPrompt,
                ToolDefinitions = item.ToolDefinitions,
                ToolArguments = item.ToolArguments,
                ToolResult = item.ToolResult,
                UserRequest = item.UserRequest,
                ReasoningContent = item.ReasoningContent,
                AttributesJson = item.AttributesJson,
                ResourceJson = item.ResourceJson,
                EventsJson = item.EventsJson,
                TranscriptPath = prompt.SourcePath,
                TelemetryInputMessages = item.InputMessages,
            };
        }

        return enriched ?? content;
    }

    /// <summary>Raw session-log lines for an LLM call or tool call span, or null when there is no local log for it.</summary>
    public TranscriptEvents? TryGetEvents(SpanRecord span, string? attributesJson)
    {
        var (conversationId, responseId) = ReadIds(attributesJson);
        conversationId = span.ConversationId ?? conversationId;
        var callId = span.Category switch
        {
            SpanCategory.LlmCall => responseId,
            SpanCategory.Tool => span.ToolCallId,
            _ => null,
        };

        return conversationId is null || callId is null ? null : source.TryGetEvents(conversationId, callId);
    }

    /// <summary>Reads the conversation and response ids from the flat attribute object without materializing it.</summary>
    private static (string? ConversationId, string? ResponseId) ReadIds(string? attributesJson)
    {
        if (string.IsNullOrEmpty(attributesJson))
        {
            return (null, null);
        }

        string? conversationId = null, responseId = null;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(attributesJson));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return (null, null);
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isConversation = reader.ValueTextEquals(GenAiKeys.ConversationId);
                var isResponse = !isConversation && reader.ValueTextEquals(GenAiKeys.ResponseId);
                reader.Read();
                if (reader.TokenType == JsonTokenType.String && (isConversation || isResponse))
                {
                    if (isConversation) conversationId = reader.GetString();
                    else responseId = reader.GetString();

                    if (conversationId is not null && responseId is not null)
                    {
                        break;
                    }
                }
                else
                {
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
        }

        return (conversationId, responseId);
    }
}
