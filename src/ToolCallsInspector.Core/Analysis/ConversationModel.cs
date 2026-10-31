using ToolCallsInspector.Core.Storage;

namespace ToolCallsInspector.Core.Analysis;

/// <summary>A span in the correlated conversation tree.</summary>
public sealed class SpanNode
{
    public required SpanRecord Span { get; init; }
    public SpanNode? Parent { get; set; }
    public List<SpanNode> Children { get; } = [];

    /// <summary>True when the parent span has not been received yet (e.g. the agent span is still running).</summary>
    public bool IsPlaceholder { get; init; }

    // LLM call data
    public int? CallIndex { get; set; }
    public TokenComposition? Composition { get; set; }
    public List<SpanNode> RequestedTools { get; } = [];

    // Tool call data
    public SpanNode? RequestedBy { get; set; }
    public double? IntroducedTokens { get; set; }
    public bool IntroducedIsEstimate { get; set; }

    /// <summary>Tokens used by a sub-agent this tool started (e.g. <c>runSubagent</c>); null when it started none.</summary>
    public SubAgentUsage? SubAgent { get; set; }

    /// <summary>
    /// Name of the local log the call's full prompt was rebuilt from (e.g. "Copilot session log") when the breakdown was
    /// computed. Saved breakdowns keep it, so it stays known after the log is deleted.
    /// </summary>
    public string? PromptSource { get; set; }

    public SpanCategory Category => Span.Category;

    /// <summary>True when an ancestor tool call already accounts for this node's sub-agent usage.</summary>
    public bool HasSubAgentToolAncestor
    {
        get
        {
            for (var p = Parent; p is not null; p = p.Parent)
            {
                if (p.Category == SpanCategory.Tool && p.SubAgent is not null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>True for nodes beneath a tool call, i.e. LLM calls made by a sub-agent rather than the main agent loop.</summary>
    public bool IsInSubAgent
    {
        get
        {
            for (var p = Parent; p is not null; p = p.Parent)
            {
                if (p.Category == SpanCategory.Tool)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Diagnostic timing spans: the Copilot CLI's <c>session.timing.*</c> (and containers such as <c>session.first_turn</c>),
    /// i.e. uncategorized <c>session.*</c> spans with nothing but other timing spans beneath them, and Claude Code's
    /// per-tool permission-wait and execution spans (<c>claude_code.tool.blocked_on_user</c>, <c>claude_code.tool.execution</c>).
    /// </summary>
    public bool IsTiming =>
        Category == SpanCategory.Other
        && (Span.Name.StartsWith("session.", StringComparison.Ordinal) || Span.Name.StartsWith("claude_code.tool.", StringComparison.Ordinal))
        && Children.All(c => c.IsTiming);

    public IEnumerable<SpanNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var d in child.Descendants())
            {
                yield return d;
            }
        }
    }

    /// <summary>
    /// Ordered items for timeline rendering: LLM calls (with the tools they requested nested beneath)
    /// interleaved with any other children (hooks, unassigned tools, nested agents).
    /// </summary>
    public IReadOnlyList<SpanNode> TimelineItems() =>
        Children.Where(c => c.RequestedBy is null || c.RequestedBy.Parent != this)
            .OrderBy(c => c.Span.StartTime)
            .ToList();
}

/// <summary>Token usage of a sub-agent, rolled up onto the tool call that started it (display only; already part of the totals).</summary>
public sealed record SubAgentUsage(long LlmCalls, long Input, long Output, long CacheRead)
{
    public long Total => Input + Output;
}

/// <summary>
/// The state of a session's main agent loop at the end of a trace, handed to the first LLM call of the session's next
/// trace so its prompt is not treated as starting from scratch.
/// </summary>
public sealed class AttributionCarry
{
    public required TokenComposition Composition { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public DateTimeOffset Start { get; init; }

    /// <summary>The previous call's prompt plus response, when its messages were captured.</summary>
    public IReadOnlyList<ChatMessage>? Transcript { get; init; }
}

/// <summary>One trace = one user interaction (agent invocation) or one background LLM request.</summary>
public sealed class TraceAnalysis
{
    public required string TraceId { get; init; }
    public required string SessionKey { get; init; }
    public required string ClientId { get; init; }
    public required IReadOnlyList<SpanNode> Roots { get; init; }
    public required IReadOnlyList<SpanNode> LlmCalls { get; init; }
    public required IReadOnlyList<SpanNode> ToolCalls { get; init; }
    public required TokenTotals Totals { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public string? Prompt { get; init; }
    public string? AgentName { get; init; }

    /// <summary>No agent span: e.g. title generation, progress messages, commit message generation.</summary>
    public bool IsBackground { get; init; }

    /// <summary>Set when the client ran an agent for its own housekeeping, e.g. "Session naming". <see cref="Prompt"/> then holds its subject.</summary>
    public string? BackgroundTask { get; init; }

    /// <summary>The root agent span has not been received yet.</summary>
    public bool InProgress { get; init; }

    /// <summary>What the session's next trace continues from (the carry-in it received when it has no main agent loop).</summary>
    public AttributionCarry? CarryOut { get; init; }

    /// <summary>Set when the token breakdown was read from the database instead of being recomputed.</summary>
    public bool FromSavedBreakdown { get; init; }

    public string DisplayTitle => BackgroundTask is { } task
        ? Prompt is { Length: > 0 } subject ? $"Background: {task} · “{subject}”" : $"Background: {task}"
        : Prompt is { Length: > 0 } p
        ? p
        : IsBackground
            ? $"Background: {LlmCalls.FirstOrDefault()?.Span.AgentName ?? LlmCalls.FirstOrDefault()?.Span.DebugLabel ?? "LLM request"}"
            : AgentName ?? "Interaction";
}

public sealed class SessionAnalysis
{
    public required string SessionKey { get; init; }
    public required string ClientId { get; init; }
    public required IReadOnlyList<TraceAnalysis> Traces { get; init; }
    public required TokenTotals Totals { get; init; }

    public DateTimeOffset Start => Traces.Min(t => t.Start);
    public DateTimeOffset End => Traces.Max(t => t.End);
    public int InteractionCount => Traces.Count(t => !t.IsBackground);

    public string Title =>
        Traces.Where(t => !t.IsBackground && !string.IsNullOrWhiteSpace(t.Prompt)).OrderBy(t => t.Start).FirstOrDefault()?.Prompt
        ?? Traces.OrderBy(t => t.Start).First().DisplayTitle;
}
