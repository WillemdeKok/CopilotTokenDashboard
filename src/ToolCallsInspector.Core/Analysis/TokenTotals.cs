namespace ToolCallsInspector.Core.Analysis;

public sealed class ModelTotals
{
    public long Calls { get; set; }
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long Reasoning { get; set; }
    public double DurationMs { get; set; }
    public long Total => Input + Output;
}

public sealed class ToolTotals
{
    public required string ToolName { get; init; }
    public long Calls { get; set; }
    public long Failures { get; set; }
    public double DurationMs { get; set; }

    /// <summary>Tokens the tool's results added to the context the first time they were sent.</summary>
    public double IntroducedTokens { get; set; }

    /// <summary>Input tokens consumed by this tool's results across every LLM call they were re-sent in.</summary>
    public double ConsumedInputTokens { get; set; }

    /// <summary>Estimated part of <see cref="ConsumedInputTokens"/> that was served from the prompt cache.</summary>
    public double CachedConsumedInputTokens { get; set; }

    public double FreshConsumedInputTokens => Math.Max(0, ConsumedInputTokens - CachedConsumedInputTokens);

    /// <summary>LLM calls made by sub-agents this tool started (e.g. <c>runSubagent</c>). Already part of the global totals.</summary>
    public long SubAgentLlmCalls { get; set; }

    public long SubAgentInput { get; set; }

    public long SubAgentOutput { get; set; }

    public long SubAgentTotal => SubAgentInput + SubAgentOutput;
}

/// <summary>A share of the processed tokens; <paramref name="Cached"/> is the estimated prompt-cache part of an input slice.</summary>
public sealed record BreakdownSlice(string Key, string Label, string Description, double Value, double Percent, double? Cached = null)
{
    public double Fresh => Math.Max(0, Value - (Cached ?? 0));
}

/// <summary>Aggregated token figures for an LLM call set (trace, session, or global).</summary>
public sealed class TokenTotals
{
    public const string OutputKey = "Output";

    public long LlmCalls { get; private set; }
    public long ToolCalls { get; private set; }
    public long Input { get; private set; }
    public long Output { get; private set; }
    public long CacheRead { get; private set; }
    public long CacheCreation { get; private set; }
    public long Reasoning { get; private set; }
    public double LlmDurationMs { get; private set; }
    public double ToolDurationMs { get; private set; }

    public Dictionary<TokenCategory, double> InputByCategory { get; } = new();

    /// <summary>Estimated prompt-cache part of <see cref="InputByCategory"/>.</summary>
    public Dictionary<TokenCategory, double> CachedInputByCategory { get; } = new();
    public Dictionary<string, ModelTotals> ByModel { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ToolTotals> ByTool { get; } = new(StringComparer.Ordinal);

    public long Total => Input + Output;
    public long FreshInput => Math.Max(0, Input - CacheRead);
    public double CacheHitRate => Input > 0 ? (double)CacheRead / Input : 0;

    public void AddLlmCall(SpanNode node)
    {
        var s = node.Span;
        LlmCalls++;
        Input += s.InputTokens ?? 0;
        Output += s.OutputTokens ?? 0;
        CacheRead += s.CacheReadTokens ?? 0;
        CacheCreation += s.CacheCreationTokens ?? 0;
        Reasoning += s.ReasoningTokens ?? 0;
        LlmDurationMs += s.DurationMs;

        var model = Model(s.Model);
        model.Calls++;
        model.Input += s.InputTokens ?? 0;
        model.Output += s.OutputTokens ?? 0;
        model.CacheRead += s.CacheReadTokens ?? 0;
        model.Reasoning += s.ReasoningTokens ?? 0;
        model.DurationMs += s.DurationMs;

        if (node.Composition is { } comp)
        {
            foreach (var (category, value) in comp.ByCategory)
            {
                InputByCategory[category] = InputByCategory.GetValueOrDefault(category) + value;
            }

            foreach (var (tool, value) in comp.ToolResultsByTool)
            {
                Tool(tool).ConsumedInputTokens += value;
            }

            foreach (var (category, value) in comp.CachedByCategory)
            {
                CachedInputByCategory[category] = CachedInputByCategory.GetValueOrDefault(category) + value;
            }

            foreach (var (tool, value) in comp.CachedToolResultsByTool)
            {
                Tool(tool).CachedConsumedInputTokens += value;
            }
        }
    }

    public void AddToolCall(SpanNode node)
    {
        var s = node.Span;
        ToolCalls++;
        ToolDurationMs += s.DurationMs;
        var tool = Tool(s.ToolName ?? TokenComposition.UnknownTool);
        tool.Calls++;
        tool.DurationMs += s.DurationMs;
        tool.IntroducedTokens += node.IntroducedTokens ?? 0;
        if (s.StatusCode == 2 || s.ErrorType is not null)
        {
            tool.Failures++;
        }

        // Nested sub-agents are already included in the outermost tool's figures.
        if (node.SubAgent is { } sub && !node.HasSubAgentToolAncestor)
        {
            tool.SubAgentLlmCalls += sub.LlmCalls;
            tool.SubAgentInput += sub.Input;
            tool.SubAgentOutput += sub.Output;
        }
    }

    public void Add(TokenTotals other)
    {
        LlmCalls += other.LlmCalls;
        ToolCalls += other.ToolCalls;
        Input += other.Input;
        Output += other.Output;
        CacheRead += other.CacheRead;
        CacheCreation += other.CacheCreation;
        Reasoning += other.Reasoning;
        LlmDurationMs += other.LlmDurationMs;
        ToolDurationMs += other.ToolDurationMs;

        foreach (var (k, v) in other.InputByCategory)
        {
            InputByCategory[k] = InputByCategory.GetValueOrDefault(k) + v;
        }

        foreach (var (k, v) in other.CachedInputByCategory)
        {
            CachedInputByCategory[k] = CachedInputByCategory.GetValueOrDefault(k) + v;
        }

        foreach (var (k, v) in other.ByModel)
        {
            var m = Model(k);
            m.Calls += v.Calls;
            m.Input += v.Input;
            m.Output += v.Output;
            m.CacheRead += v.CacheRead;
            m.Reasoning += v.Reasoning;
            m.DurationMs += v.DurationMs;
        }

        foreach (var (k, v) in other.ByTool)
        {
            var t = Tool(k);
            t.Calls += v.Calls;
            t.Failures += v.Failures;
            t.DurationMs += v.DurationMs;
            t.IntroducedTokens += v.IntroducedTokens;
            t.ConsumedInputTokens += v.ConsumedInputTokens;
            t.CachedConsumedInputTokens += v.CachedConsumedInputTokens;
            t.SubAgentLlmCalls += v.SubAgentLlmCalls;
            t.SubAgentInput += v.SubAgentInput;
            t.SubAgentOutput += v.SubAgentOutput;
        }
    }

    /// <summary>
    /// Share of all tokens processed (input + output): LLM output plus each input category.
    /// Input that could not be attributed (e.g. calls without usage data) is folded into "Not captured".
    /// </summary>
    public IReadOnlyList<BreakdownSlice> Slices()
    {
        var total = (double)Total;
        var slices = new List<BreakdownSlice>
        {
            new(OutputKey, "LLM output", "Tokens generated by the model (including reasoning).", Output, Pct(Output, total)),
        };

        var attributed = InputByCategory.Values.Sum();
        var unattributed = Math.Max(0, Input - attributed);
        // Cache reads of calls without a breakdown belong to the unattributed remainder.
        var unattributedCached = Math.Min(unattributed, Math.Max(0, CacheRead - CachedInputByCategory.Values.Sum()));
        foreach (var category in Enum.GetValues<TokenCategory>())
        {
            var value = InputByCategory.GetValueOrDefault(category);
            var cached = CachedInputByCategory.GetValueOrDefault(category);
            if (category == TokenCategory.Overhead)
            {
                value += unattributed;
                cached += unattributedCached;
            }

            slices.Add(new BreakdownSlice(category.ToString(), TokenCategoryInfo.Label(category), TokenCategoryInfo.Description(category), value, Pct(value, total), cached));
        }

        return slices;
    }

    private static double Pct(double value, double total) => total > 0 ? value / total * 100 : 0;

    private ModelTotals Model(string name)
    {
        if (!ByModel.TryGetValue(name, out var m))
        {
            ByModel[name] = m = new ModelTotals();
        }

        return m;
    }

    private ToolTotals Tool(string name)
    {
        if (!ByTool.TryGetValue(name, out var t))
        {
            ByTool[name] = t = new ToolTotals { ToolName = name };
        }

        return t;
    }
}
