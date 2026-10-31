namespace ToolCallsInspector.Core.Storage;

public enum SpanCategory
{
    Other = 0,
    Agent = 1,
    LlmCall = 2,
    Tool = 3,
    Hook = 4,
}

/// <summary>
/// Normalized, query-friendly span metadata. Large payloads live in <see cref="SpanContent"/>.
/// </summary>
public sealed class SpanRecord
{
    public required string Id { get; set; }
    public required string TraceId { get; set; }
    public required string SpanId { get; set; }
    public string? ParentSpanId { get; set; }

    public required string ClientId { get; set; }
    public string? ServiceName { get; set; }
    public string? ClientInstanceId { get; set; }
    public string? SourceName { get; set; }

    public SpanCategory Category { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public double DurationMs { get; set; }
    public int StatusCode { get; set; }
    public string? StatusMessage { get; set; }
    public string? ErrorType { get; set; }

    /// <summary>Best session identifier derivable from this span alone (chat session / parent chat session / conversation id).</summary>
    public string? SessionKey { get; set; }
    public string? ChatSessionId { get; set; }
    public string? ParentChatSessionId { get; set; }
    public string? ConversationId { get; set; }
    public string? DebugLabel { get; set; }

    public string? AgentName { get; set; }
    public string? RequestModel { get; set; }
    public string? ResponseModel { get; set; }

    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? CacheReadTokens { get; set; }
    public long? CacheCreationTokens { get; set; }
    public long? ReasoningTokens { get; set; }
    public long? MaxPromptTokens { get; set; }
    public long? MaxOutputTokens { get; set; }
    public double? TimeToFirstTokenMs { get; set; }
    public string? FinishReasons { get; set; }

    public string? ToolName { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolType { get; set; }

    public string? HookType { get; set; }
    public string? HookDecision { get; set; }

    /// <summary>Short user prompt text (agent spans) – truncated for list views.</summary>
    public string? UserPrompt { get; set; }

    public bool HasContent { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    public string Model => ResponseModel ?? RequestModel ?? "unknown";

    public SpanRecord CloneMetadata() => (SpanRecord)MemberwiseClone();
}

/// <summary>A trace's saved token attribution (<see cref="Analysis.SavedBreakdown"/>), valid while its fingerprints still match.</summary>
public sealed class TraceBreakdownRow
{
    public required string TraceId { get; set; }
    public string? SessionKey { get; set; }

    /// <summary>The trace's own spans when it was computed (count and latest receive time).</summary>
    public required string Fingerprint { get; set; }

    /// <summary>The chain fingerprint of the session's previous trace when it was computed ("" when there was none).</summary>
    public required string PredecessorChain { get; set; }

    public required string Json { get; set; }

    /// <summary>The main loop's last prompt + response (gzip JSON), continued by the session's next trace.</summary>
    public byte[]? CarryTranscript { get; set; }

    /// <summary>True when a local transcript / request-body log supplied a full prompt.</summary>
    public bool UsedTranscript { get; set; }

    public long ComputedAt { get; set; }
}

public sealed class SettingRow
{
    public required string Key { get; set; }
    public string? Value { get; set; }
}

/// <summary>Per-trace summary used to order a session's traces and detect changes without loading spans.</summary>
public sealed record TraceIndexEntry(string TraceId, string SessionKey, int SpanCount, long MaxReceivedTicks, DateTimeOffset Start, DateTimeOffset End)
{
    public string Fingerprint => $"{SpanCount}:{MaxReceivedTicks}";
}

public sealed class SpanContent
{
    public required string Id { get; set; }
    public string? InputMessages { get; set; }
    public string? OutputMessages { get; set; }
    public string? SystemInstructions { get; set; }
    public string? ToolDefinitions { get; set; }
    public string? ToolArguments { get; set; }
    public string? ToolResult { get; set; }
    public string? UserRequest { get; set; }
    public string? ReasoningContent { get; set; }
    public string? AttributesJson { get; set; }
    public string? ResourceJson { get; set; }
    public string? EventsJson { get; set; }

    /// <summary>Not stored. Set when <see cref="InputMessages"/> holds the full prompt rebuilt from this local session log.</summary>
    public string? TranscriptPath { get; set; }

    /// <summary>Not stored. Name of the log <see cref="TranscriptPath"/> points to, e.g. "Copilot session log".</summary>
    public string? TranscriptSource { get; set; }

    /// <summary>Not stored. The (partial) input messages the telemetry carried, when replaced from a transcript.</summary>
    public string? TelemetryInputMessages { get; set; }
}
