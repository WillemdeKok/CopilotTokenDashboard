using ToolCallsInspector.Core.Analysis;
using ToolCallsInspector.Core.Clients;
using ToolCallsInspector.Core.Storage;
using ToolCallsInspector.Core.Telemetry;
using K = ToolCallsInspector.Core.Normalization.GenAiKeys;

namespace ToolCallsInspector.Core.Normalization;

public interface ISpanNormalizer
{
    (SpanRecord Record, SpanContent Content) Normalize(RawSpan span, string sourceName);
}

/// <summary>
/// Maps OTel GenAI (gen_ai.*) and Copilot (github.copilot.*, copilot_chat.*) attributes onto the
/// normalized <see cref="SpanRecord"/>, then lets the matching <see cref="IClientProfile"/> enrich it.
/// </summary>
public sealed class SpanNormalizer(IClientCatalog clients, TimeProvider? timeProvider = null) : ISpanNormalizer
{
    private const int PromptPreviewLength = 4000;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public (SpanRecord Record, SpanContent Content) Normalize(RawSpan span, string sourceName)
    {
        var a = span.Attributes;
        var profile = clients.Resolve(span);
        var id = $"{span.TraceId}-{span.SpanId}";
        var category = profile.Categorize(span) ?? Categorize(span);

        var record = new SpanRecord
        {
            Id = id,
            TraceId = span.TraceId,
            SpanId = span.SpanId,
            ParentSpanId = string.IsNullOrEmpty(span.ParentSpanId) ? null : span.ParentSpanId,
            ClientId = profile.ClientId,
            ServiceName = span.ResourceAttributes.GetString("service.name"),
            SourceName = sourceName,
            Category = category,
            Name = span.Name,
            StartTime = span.StartTime,
            EndTime = span.EndTime,
            DurationMs = Math.Max(0, (span.EndTime - span.StartTime).TotalMilliseconds),
            StatusCode = span.StatusCode,
            StatusMessage = span.StatusMessage,
            // Shell tools (e.g. the Copilot CLI's powershell/bash) report success at span level even when the command failed.
            ErrorType = a.GetString(K.ErrorType)
                        ?? (category == SpanCategory.Tool && a.GetLong(K.ProcessExitCode) is { } exit && exit != 0 ? $"exit code {exit}" : null),
            ChatSessionId = a.GetString(K.ChatSessionId, K.CopilotSessionId),
            ParentChatSessionId = a.GetString(K.ParentChatSessionId),
            ConversationId = a.GetString(K.ConversationId),
            DebugLabel = a.GetString(K.DebugLogLabel),
            AgentName = a.GetString(K.AgentName),
            RequestModel = a.GetString(K.RequestModel),
            ResponseModel = a.GetString(K.ResponseModel),
            InputTokens = a.GetLong(K.InputTokens),
            OutputTokens = a.GetLong(K.OutputTokens),
            CacheReadTokens = a.GetLong(K.CacheReadTokens),
            CacheCreationTokens = a.GetLong(K.CacheCreationTokens),
            ReasoningTokens = a.GetLong(K.ReasoningTokens, K.ReasoningTokensLegacy),
            MaxPromptTokens = a.GetLong(K.MaxPromptTokens),
            MaxOutputTokens = a.GetLong(K.RequestMaxTokens),
            TimeToFirstTokenMs = a.GetDouble(K.TimeToFirstToken, K.CopilotTimeToFirstToken),
            FinishReasons = a.GetString(K.FinishReasons),
            ToolName = a.GetString(K.ToolName),
            ToolCallId = a.GetString(K.ToolCallId),
            ToolType = a.GetString(K.ToolType),
            HookType = a.GetString(K.HookType),
            HookDecision = a.GetString(K.CopilotHookDecision),
            ReceivedAt = _time.GetUtcNow(),
        };

        var content = new SpanContent
        {
            Id = id,
            InputMessages = a.GetString(K.InputMessages),
            OutputMessages = a.GetString(K.OutputMessages),
            SystemInstructions = a.GetString(K.SystemInstructions),
            ToolDefinitions = a.GetString(K.ToolDefinitions),
            ToolArguments = a.GetString(K.ToolCallArguments),
            ToolResult = a.GetString(K.ToolCallResult),
            UserRequest = a.GetString(K.UserRequest),
            ReasoningContent = a.GetString(K.ReasoningContent),
            AttributesJson = a.ToJson(),
            ResourceJson = span.ResourceAttributes.ToJson(),
            EventsJson = System.Text.Json.JsonSerializer.Serialize(span.Events.Select(e => new { e.Name, e.Time, e.Attributes })),
        };

        // Client-specific mapping first, so the derived fields below see (and never overwrite) its values.
        profile.Enrich(span, record, content);

        if (string.IsNullOrEmpty(record.ToolName) && record.Category == SpanCategory.Tool)
        {
            record.ToolName = StripPrefix(span.Name, "execute_tool ");
        }

        if (string.IsNullOrEmpty(record.AgentName) && record.Category == SpanCategory.Agent)
        {
            record.AgentName = StripPrefix(span.Name, "invoke_agent ");
        }

        if (record.Category == SpanCategory.Agent)
        {
            var prompt = record.UserPrompt
                         ?? GenAiMessageParser.FirstUserText(content.InputMessages)
                         ?? span.Events.FirstOrDefault(e => e.Name == "user_message")?.Attributes.GetString("content");
            record.UserPrompt = Truncate(GenAiMessageParser.CleanPrompt(prompt), PromptPreviewLength);
        }

        record.HasContent = content.InputMessages is not null || content.OutputMessages is not null
                            || content.ToolArguments is not null || content.ToolResult is not null;

        // Child sessions (sub-agents, title generation, ...) roll up into their parent chat session.
        record.SessionKey ??= record.ParentChatSessionId
                              ?? record.ChatSessionId
                              ?? (record.Category == SpanCategory.Agent ? record.ConversationId : null);

        return (record, content);
    }

    private static SpanCategory Categorize(RawSpan span)
    {
        var op = span.Attributes.GetString(K.OperationName);
        op ??= span.Name.Split(' ', 2)[0];
        return op switch
        {
            K.Operations.Chat or "text_completion" or "generate_content" => SpanCategory.LlmCall,
            K.Operations.InvokeAgent or "create_agent" => SpanCategory.Agent,
            K.Operations.ExecuteTool => SpanCategory.Tool,
            K.Operations.ExecuteHook => SpanCategory.Hook,
            _ => SpanCategory.Other,
        };
    }

    private static string StripPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max] + "…";
}
