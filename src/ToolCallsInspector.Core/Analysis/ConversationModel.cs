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

    public SpanCategory Category => Span.Category;

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
