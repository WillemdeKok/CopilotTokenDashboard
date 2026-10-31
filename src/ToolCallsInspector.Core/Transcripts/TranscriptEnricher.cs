using System.Text;
using System.Text.Json;
using ToolCallsInspector.Core.Normalization;
using ToolCallsInspector.Core.Storage;

namespace ToolCallsInspector.Core.Transcripts;

/// <summary>
/// Replaces the partial prompt captured in telemetry with the full prompt from a local log. Each client has its own
/// <see cref="ITranscriptSource"/>s; conversation ids are session GUIDs, so the sources holding a log are unambiguous.
/// Sources are asked in registration order, so an exact source (request bodies) can precede a reconstructing one (transcript).
/// </summary>
public sealed class TranscriptEnricher(params IEnumerable<ITranscriptSource> sources)
{
    private readonly ITranscriptSource[] _sources = [.. sources];

    /// <summary>The source with raw log lines for the conversation, or null.</summary>
    public ITranscriptSource? SourceFor(string? conversationId) =>
        string.IsNullOrEmpty(conversationId) ? null : Array.Find(_sources, s => s.ProvidesEvents && s.HasTranscript(conversationId));

    private TranscriptPrompt? TryGetPrompt(string conversationId, string responseId)
    {
        foreach (var source in _sources)
        {
            if (source.HasTranscript(conversationId) && source.TryGetPrompt(conversationId, responseId) is { } prompt)
            {
                return prompt;
            }
        }

        return null;
    }

    public IReadOnlyDictionary<string, SpanContent> Enrich(IReadOnlyDictionary<string, SpanContent> content)
    {
        Dictionary<string, SpanContent>? enriched = null;
        foreach (var (id, item) in content)
        {
            // Only LLM calls match: the transcript is keyed by the response id of each model call.
            if (item.TranscriptPath is not null
                || ReadIds(item.AttributesJson) is not ({ } conversationId, { } responseId)
                || TryGetPrompt(conversationId, responseId) is not { } prompt)
            {
                continue;
            }

            enriched ??= new Dictionary<string, SpanContent>(content, StringComparer.Ordinal);
            enriched[id] = new SpanContent
            {
                Id = item.Id,
                InputMessages = prompt.InputMessagesJson,
                OutputMessages = item.OutputMessages ?? prompt.OutputMessagesJson,
                SystemInstructions = item.SystemInstructions ?? prompt.SystemPrompt,
                ToolDefinitions = item.ToolDefinitions ?? prompt.ToolDefinitionsJson,
                ToolArguments = item.ToolArguments,
                ToolResult = item.ToolResult,
                UserRequest = item.UserRequest,
                ReasoningContent = item.ReasoningContent,
                AttributesJson = item.AttributesJson,
                ResourceJson = item.ResourceJson,
                EventsJson = item.EventsJson,
                TranscriptPath = prompt.SourcePath,
                TranscriptSource = prompt.SourceName,
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

        return callId is null ? null : SourceFor(conversationId)?.TryGetEvents(conversationId!, callId);
    }

    /// <summary>
    /// Reads the conversation and response ids from the flat attribute object without materializing it.
    /// Clients without <c>gen_ai.conversation.id</c> (Claude Code) identify the conversation with <c>session.id</c>, and the call with <c>request_id</c>.
    /// </summary>
    private const string ClaudeRequestId = "request_id";

    private static (string? ConversationId, string? ResponseId) ReadIds(string? attributesJson)
    {
        if (string.IsNullOrEmpty(attributesJson))
        {
            return (null, null);
        }

        string? conversationId = null, sessionId = null, responseId = null, requestId = null;
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
                var isSession = !isConversation && reader.ValueTextEquals(GenAiKeys.SessionId);
                var isResponse = !isConversation && !isSession && reader.ValueTextEquals(GenAiKeys.ResponseId);
                var isRequest = !isConversation && !isSession && !isResponse && reader.ValueTextEquals(ClaudeRequestId);
                reader.Read();
                if (reader.TokenType == JsonTokenType.String && (isConversation || isSession || isResponse || isRequest))
                {
                    if (isConversation) conversationId = reader.GetString();
                    else if (isSession) sessionId = reader.GetString();
                    else if (isResponse) responseId = reader.GetString();
                    else requestId = reader.GetString();

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

        return (conversationId ?? sessionId, responseId ?? requestId);
    }
}
