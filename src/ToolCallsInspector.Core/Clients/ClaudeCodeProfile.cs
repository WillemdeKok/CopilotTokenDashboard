using System.Text.Json;
using System.Text.Json.Nodes;
using ToolCallsInspector.Core.Storage;
using ToolCallsInspector.Core.Telemetry;

namespace ToolCallsInspector.Core.Clients;

/// <summary>
/// Anthropic Claude Code (<c>service.name = "claude-code"</c>, spans named <c>claude_code.*</c>). Claude Code uses its own
/// span names and bare attribute names rather than the GenAI operation conventions, so this profile maps them:
/// <c>claude_code.interaction</c> (one per user prompt) → agent, <c>claude_code.llm_request</c> → LLM call,
/// <c>claude_code.tool</c> → tool, <c>claude_code.hook</c> → hook. Permission waits and tool execution are timing spans.
/// </summary>
/// <remarks>
/// Claude Code reports <c>input_tokens</c> without prompt-cache tokens, whereas the GenAI convention (and the token
/// attribution) counts every prompt token as input, so input is reported as input + cache read + cache creation.
/// Message content (<c>new_context</c>, <c>response.model_output</c>, <c>tool_input</c>) only exists with detailed beta tracing.
/// </remarks>
public sealed class ClaudeCodeProfile : IClientProfile
{
    public const string Id = "claude-code";
    private const string SpanPrefix = "claude_code.";
    private const string Redacted = "<REDACTED>";

    public string ClientId => Id;
    public string DisplayName => "Claude Code";
    public int Priority => 15;

    // Claude Code sends the whole conversation with every request.
    public InputCapture InputCapture => InputCapture.Full;

    public bool Matches(RawSpan span) =>
        span.Name.StartsWith(SpanPrefix, StringComparison.Ordinal)
        || string.Equals(span.ResourceAttributes.GetString("service.name"), "claude-code", StringComparison.OrdinalIgnoreCase);

    public SpanCategory? Categorize(RawSpan span) => (span.Attributes.GetString("span.type") ?? span.Name) switch
    {
        "claude_code.interaction" => SpanCategory.Agent,
        "claude_code.llm_request" => SpanCategory.LlmCall,
        "claude_code.tool" => SpanCategory.Tool,
        "claude_code.hook" => SpanCategory.Hook,
        _ => SpanCategory.Other,
    };

    public void Enrich(RawSpan span, SpanRecord record, SpanContent content)
    {
        var a = span.Attributes;
        var sessionId = a.GetString("session.id") ?? span.ResourceAttributes.GetString("session.id");
        record.ChatSessionId ??= sessionId;
        record.ConversationId ??= sessionId;
        record.ClientInstanceId ??= span.ResourceAttributes.GetString("service.instance.id");
        if (string.Equals(a.GetString("success"), "false", StringComparison.OrdinalIgnoreCase))
        {
            record.ErrorType ??= a.GetString("error_class", "error") ?? "failed";
        }

        switch (record.Category)
        {
            case SpanCategory.Agent:
                record.AgentName ??= DisplayName;
                var prompt = Unredacted(a.GetString("user_prompt")) ?? Unredacted(a.GetString("new_context"));
                content.UserRequest ??= prompt;
                record.UserPrompt ??= prompt;
                break;

            case SpanCategory.LlmCall:
                EnrichLlmCall(a, record, content);
                break;

            case SpanCategory.Tool:
                record.ToolName ??= a.GetString("tool_name");
                record.ToolCallId ??= a.GetString("tool_use_id");
                content.ToolArguments ??= a.GetString("tool_input") ?? ToolDetails(a);
                content.ToolResult ??= a.GetString("new_context") ?? ToolOutput(span);
                break;

            case SpanCategory.Hook:
                record.HookType ??= a.GetString("hook_name", "hook_event");
                break;
        }
    }

    private static void EnrichLlmCall(IReadOnlyDictionary<string, object?> a, SpanRecord record, SpanContent content)
    {
        var input = a.GetLong("input_tokens");
        var cacheRead = a.GetLong("cache_read_tokens");
        var cacheCreation = a.GetLong("cache_creation_tokens");
        if (input is not null || cacheRead is not null || cacheCreation is not null)
        {
            record.InputTokens = (input ?? 0) + (cacheRead ?? 0) + (cacheCreation ?? 0);
        }

        record.OutputTokens ??= a.GetLong("output_tokens");
        record.CacheReadTokens ??= cacheRead;
        record.CacheCreationTokens ??= cacheCreation;
        record.RequestModel ??= a.GetString("model");
        record.ResponseModel ??= a.GetString("model");
        record.TimeToFirstTokenMs ??= a.GetDouble("ttft_ms");
        record.FinishReasons ??= a.GetString("stop_reason");
        // Sub-agent requests name the agent that issued them, e.g. "agent.builtin.general-purpose".
        record.DebugLabel ??= a.GetString("query_source", "query_source_safe");

        content.InputMessages ??= Message("user", Unredacted(a.GetString("new_context")));
        content.OutputMessages ??= Message("assistant", Unredacted(a.GetString("response.model_output")));
        // Only the first 500 characters of the system prompt are exported; the remainder shows as not captured.
        content.SystemInstructions ??= Unredacted(a.GetString("system_prompt_preview")) ?? Unredacted(a.GetString("user_system_prompt"));
    }

    /// <summary>Without <c>tool_input</c>, the stable per-tool details (gated by OTEL_LOG_TOOL_DETAILS) still describe the call.</summary>
    private static string? ToolDetails(IReadOnlyDictionary<string, object?> a)
    {
        var details = new JsonObject();
        foreach (var key in (ReadOnlySpan<string>)["file_path", "full_command", "skill_name", "subagent_type"])
        {
            if (a.GetString(key) is { } value)
            {
                details[key] = value;
            }
        }

        return details.Count == 0 ? null : details.ToJsonString();
    }

    /// <summary>The <c>tool.output</c> span event (gated by OTEL_LOG_TOOL_CONTENT) holds the result of Read, Bash, Edit, Write, MCP and web tools.</summary>
    private static string? ToolOutput(RawSpan span) =>
        span.Events.FirstOrDefault(e => e.Name == "tool.output")?.Attributes.GetString("output", "content", "diff");

    private static string? Message(string role, string? text) => string.IsNullOrEmpty(text)
        ? null
        : JsonSerializer.Serialize(new[] { new { role, parts = new[] { new { type = "text", content = text } } } });

    private static string? Unredacted(string? value) => string.IsNullOrEmpty(value) || value == Redacted ? null : value;
}
