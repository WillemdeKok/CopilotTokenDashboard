namespace ToolCallsInspector.Core.Analysis;

/// <summary>What a slice of an LLM call's input (prompt) consisted of.</summary>
public enum TokenCategory
{
    SystemPrompt,
    UserContext,
    AssistantHistory,
    ToolResults,
    ToolSchemas,
    Overhead,
}

public enum AttributionMethod
{
    /// <summary>No reported input tokens – nothing to attribute.</summary>
    None,

    /// <summary>Prompt content was captured and tokenized, then scaled to the reported input tokens.</summary>
    Content,

    /// <summary>Derived from the growth of reported input tokens between consecutive LLM calls.</summary>
    Delta,
}

public static class TokenCategoryInfo
{
    public static string Label(TokenCategory category) => category switch
    {
        TokenCategory.SystemPrompt => "System prompt",
        TokenCategory.UserContext => "User prompt & context",
        TokenCategory.AssistantHistory => "Assistant history",
        TokenCategory.ToolResults => "Tool results",
        TokenCategory.ToolSchemas => "Tool schemas",
        TokenCategory.Overhead => "Not captured",
        _ => category.ToString(),
    };

    public static string Description(TokenCategory category) => category switch
    {
        TokenCategory.SystemPrompt => "The client's system instructions sent with every request.",
        TokenCategory.UserContext => "Your messages plus attached context (files, selections, instructions).",
        TokenCategory.AssistantHistory => "Previous assistant replies and tool-call arguments re-sent as history.",
        TokenCategory.ToolResults => "Output returned by tools and re-sent to the model on each later call.",
        TokenCategory.ToolSchemas => "Definitions of every available tool (name, description, parameters) sent with each request.",
        TokenCategory.Overhead => "Reported input with no captured content behind it: earlier turns the client didn't export (the Copilot CLI only exports messages added in the current turn), or, for Claude Code, its system prompt, tool schemas and CLAUDE.md, which neither its telemetry nor its transcript records. Plus provider formatting.",
        _ => string.Empty,
    };
}

/// <summary>Breakdown of a single LLM call's input tokens.</summary>
public sealed class TokenComposition
{
    public const string UnknownTool = "(unknown tool)";

    public Dictionary<TokenCategory, double> ByCategory { get; } = new();
    public Dictionary<string, double> ToolResultsByTool { get; } = new(StringComparer.Ordinal);

    /// <summary>Estimated part of each category that was a prompt-cache read (see <see cref="ApplyCacheReads"/>).</summary>
    public Dictionary<TokenCategory, double> CachedByCategory { get; } = new();

    /// <summary>Estimated part of each tool's results that was a prompt-cache read.</summary>
    public Dictionary<string, double> CachedToolResultsByTool { get; } = new(StringComparer.Ordinal);

    public AttributionMethod Method { get; set; }

    public double CachedTotal => CachedByCategory.Values.Sum();

    public double GetCached(TokenCategory category) => CachedByCategory.GetValueOrDefault(category);

    public double Total => ByCategory.Values.Sum();

    public double Get(TokenCategory category) => ByCategory.GetValueOrDefault(category);

    /// <summary>
    /// Reported input the captured content couldn't account for (e.g. earlier turns the client didn't export),
    /// or null when it isn't a meaningful share (at least 1,000 tokens and 10% of the input).
    /// </summary>
    public double? SignificantNotCaptured(double inputTokens)
    {
        if (Method != AttributionMethod.Content || inputTokens <= 0)
        {
            return null;
        }

        var missing = Get(TokenCategory.Overhead);
        return missing >= 1000 && missing / inputTokens >= 0.1 ? missing : null;
    }

    public void Add(TokenCategory category, double value, string? toolName = null)
    {
        if (value == 0)
        {
            return;
        }

        ByCategory[category] = Get(category) + value;
        if (category == TokenCategory.ToolResults)
        {
            var key = string.IsNullOrEmpty(toolName) ? UnknownTool : toolName;
            ToolResultsByTool[key] = ToolResultsByTool.GetValueOrDefault(key) + value;
        }
    }

    public TokenComposition Clone(AttributionMethod? method = null)
    {
        var copy = new TokenComposition { Method = method ?? Method };
        foreach (var (k, v) in ByCategory)
        {
            copy.ByCategory[k] = v;
        }

        foreach (var (k, v) in ToolResultsByTool)
        {
            copy.ToolResultsByTool[k] = v;
        }

        foreach (var (k, v) in CachedByCategory)
        {
            copy.CachedByCategory[k] = v;
        }

        foreach (var (k, v) in CachedToolResultsByTool)
        {
            copy.CachedToolResultsByTool[k] = v;
        }

        return copy;
    }

    public void Scale(double factor)
    {
        foreach (var key in ByCategory.Keys.ToList())
        {
            ByCategory[key] *= factor;
        }

        foreach (var key in ToolResultsByTool.Keys.ToList())
        {
            ToolResultsByTool[key] *= factor;
        }

        foreach (var key in CachedByCategory.Keys.ToList())
        {
            CachedByCategory[key] *= factor;
        }

        foreach (var key in CachedToolResultsByTool.Keys.ToList())
        {
            CachedToolResultsByTool[key] *= factor;
        }
    }

    /// <summary>
    /// Splits <paramref name="cacheReadTokens"/> (the provider-reported prompt-cache hit) over the categories. A cache hit is
    /// always a prefix of the request, so it is assumed to cover the earliest content first: the system prompt, then the
    /// tool schemas, then the history that was already part of the <paramref name="previous"/> call's prompt (older
    /// content), and only then the content added since.
    /// </summary>
    public void ApplyCacheReads(double cacheReadTokens, TokenComposition? previous)
    {
        CachedByCategory.Clear();
        CachedToolResultsByTool.Clear();
        var remaining = Math.Min(Math.Max(0, cacheReadTokens), Total);
        if (remaining <= 0)
        {
            return;
        }

        foreach (var category in (ReadOnlySpan<TokenCategory>)[TokenCategory.SystemPrompt, TokenCategory.ToolSchemas])
        {
            var take = Math.Min(remaining, Get(category));
            AddCached(category, take);
            remaining -= take;
        }

        TokenCategory[] history = [TokenCategory.Overhead, TokenCategory.UserContext, TokenCategory.AssistantHistory, TokenCategory.ToolResults];

        // Content that was already sent with the previous call (older history) first, then whatever is left.
        var older = history.ToDictionary(c => c, c => previous is null ? 0 : Math.Min(Get(c), previous.Get(c)));
        remaining = Distribute(remaining, older);
        var rest = history.ToDictionary(c => c, c => Math.Max(0, Get(c) - GetCached(c)));
        Distribute(remaining, rest);

        // Per tool: the same rule within the tool results category.
        var cachedResults = GetCached(TokenCategory.ToolResults);
        if (cachedResults <= 0 || ToolResultsByTool.Count == 0)
        {
            return;
        }

        var olderTools = ToolResultsByTool.ToDictionary(
            kv => kv.Key, kv => previous is null ? 0 : Math.Min(kv.Value, previous.ToolResultsByTool.GetValueOrDefault(kv.Key)), StringComparer.Ordinal);
        var left = DistributeTools(cachedResults, olderTools);
        var restTools = ToolResultsByTool.ToDictionary(
            kv => kv.Key, kv => Math.Max(0, kv.Value - CachedToolResultsByTool.GetValueOrDefault(kv.Key)), StringComparer.Ordinal);
        DistributeTools(left, restTools);
    }

    private double Distribute(double amount, Dictionary<TokenCategory, double> capacity)
    {
        var sum = capacity.Values.Sum();
        if (amount <= 0 || sum <= 0)
        {
            return amount;
        }

        var share = Math.Min(1, amount / sum);
        foreach (var (category, cap) in capacity)
        {
            AddCached(category, cap * share);
        }

        return amount - sum * share;
    }

    private double DistributeTools(double amount, Dictionary<string, double> capacity)
    {
        var sum = capacity.Values.Sum();
        if (amount <= 0 || sum <= 0)
        {
            return amount;
        }

        var share = Math.Min(1, amount / sum);
        foreach (var (tool, cap) in capacity.Where(kv => kv.Value > 0))
        {
            CachedToolResultsByTool[tool] = CachedToolResultsByTool.GetValueOrDefault(tool) + cap * share;
        }

        return amount - sum * share;
    }

    private void AddCached(TokenCategory category, double value)
    {
        if (value > 0)
        {
            CachedByCategory[category] = GetCached(category) + value;
        }
    }

    /// <summary>
    /// Makes the composition sum to the reported input tokens: scales down when estimates overshoot,
    /// otherwise assigns the remainder to <see cref="TokenCategory.Overhead"/>. When the captured prompt is known to be
    /// <paramref name="complete"/>, an undershoot is a tokenizer difference (o200k vs. the model's own), so it scales up instead.
    /// </summary>
    public void FitTo(double reportedTotal, bool complete = false)
    {
        if (reportedTotal <= 0)
        {
            return;
        }

        var total = Total;
        if (complete && total > 0 && total < reportedTotal)
        {
            TokenizerScale = reportedTotal / total;
            Scale(TokenizerScale.Value);
        }
        else if (total > reportedTotal)
        {
            Scale(reportedTotal / total);
        }
        else if (total < reportedTotal)
        {
            Add(TokenCategory.Overhead, reportedTotal - total);
        }
    }

    /// <summary>
    /// Factor by which estimates of a complete prompt were scaled up to match the reported input tokens,
    /// i.e. how many more tokens the model's tokenizer produced than o200k. Null when not applied.
    /// </summary>
    public double? TokenizerScale { get; set; }
}
