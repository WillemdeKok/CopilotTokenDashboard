namespace ToolCallsInspector.Core.Transcripts;

/// <summary>The complete prompt sent for one LLM call, rebuilt from a client's local session log.</summary>
/// <param name="InputMessagesJson">Messages in the OTel GenAI <c>gen_ai.input.messages</c> format.</param>
/// <param name="SourceName">Human-readable name of the log, e.g. "Copilot session log".</param>
/// <param name="ToolDefinitionsJson">JSON array of the tool schemas sent with the call, when the log records them.</param>
/// <param name="OutputMessagesJson">The model's reply in the <c>gen_ai.output.messages</c> format, when the log records it.</param>
public sealed record TranscriptPrompt(
    string InputMessagesJson,
    string? SystemPrompt,
    int MessageCount,
    string SourcePath,
    string SourceName = "session log",
    string? ToolDefinitionsJson = null,
    string? OutputMessagesJson = null);

/// <summary>One raw line of a session log.</summary>
/// <param name="LineNumber">1-based line number in the log file.</param>
public sealed record TranscriptEvent(int LineNumber, string? Type, string Json);

/// <summary>The raw session-log lines that belong to one LLM call or tool call.</summary>
public sealed record TranscriptEvents(string SourcePath, IReadOnlyList<TranscriptEvent> Events, string SourceName = "session log");

/// <summary>
/// A client's local conversation log. Telemetry usually only carries the messages added during the current turn;
/// a transcript holds the whole conversation, so the exact history before each call can be replayed.
/// Register one per client; <see cref="TranscriptEnricher"/> asks them in registration order.
/// </summary>
public interface ITranscriptSource
{
    /// <summary>Human-readable name, e.g. "Copilot session log".</summary>
    string Name { get; }

    /// <summary>
    /// False for sources that only supply prompts (e.g. per-request body files), so the Raw log view uses
    /// the next source with line-oriented logs for the conversation.
    /// </summary>
    bool ProvidesEvents => true;

    /// <summary>True when a local log exists for the conversation.</summary>
    bool HasTranscript(string? conversationId);

    /// <summary>Full prompt for the call with <paramref name="responseId"/>, or null when no local transcript has it.</summary>
    TranscriptPrompt? TryGetPrompt(string conversationId, string responseId);

    /// <summary>
    /// Raw log lines that reference <paramref name="callId"/> (an LLM call's response id or a tool call id),
    /// or null when no local transcript exists for the conversation.
    /// </summary>
    TranscriptEvents? TryGetEvents(string conversationId, string callId);
}
