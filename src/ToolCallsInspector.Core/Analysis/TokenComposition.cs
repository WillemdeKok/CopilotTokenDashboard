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
        TokenCategory.SystemPrompt => "Copilot's system instructions sent with every request.",
        TokenCategory.UserContext => "Your messages plus attached context (files, selections, instructions).",
        TokenCategory.AssistantHistory => "Previous assistant replies and tool-call arguments re-sent as history.",
        TokenCategory.ToolResults => "Output returned by tools and re-sent to the model on each later call.",
        TokenCategory.ToolSchemas => "Definitions of every available tool (name, description, parameters) sent with each request.",
        TokenCategory.Overhead => "Reported input with no captured content behind it: mostly earlier turns of the conversation (the Copilot CLI only exports messages added in the current turn), plus provider formatting.",
        _ => string.Empty,
    };
}

/// <summary>Breakdown of a single LLM call's input tokens.</summary>
public sealed class TokenComposition
{
    public const string UnknownTool = "(unknown tool)";

    public Dictionary<TokenCategory, double> ByCategory { get; } = new();
    public Dictionary<string, double> ToolResultsByTool { get; } = new(StringComparer.Ordinal);
    public AttributionMethod Method { get; set; }

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
    public double? TokenizerScale { get; private set; }
}
