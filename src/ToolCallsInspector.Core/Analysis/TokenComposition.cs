namespace ToolCallsInspector.Core.Analysis;

/// <summary>What a slice of an LLM call's input (prompt) consisted of.</summary>
public enum TokenCategory
{
    SystemPrompt,
    UserContext,
    AssistantHistory,
    ToolResults,
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
        TokenCategory.Overhead => "Tool schemas & other context",
        _ => category.ToString(),
    };

    public static string Description(TokenCategory category) => category switch
    {
        TokenCategory.SystemPrompt => "Copilot's system instructions sent with every request.",
        TokenCategory.UserContext => "Your messages plus attached context (files, selections, instructions).",
        TokenCategory.AssistantHistory => "Previous assistant replies and tool-call arguments re-sent as history.",
        TokenCategory.ToolResults => "Output returned by tools and re-sent to the model on each later call.",
        TokenCategory.Overhead => "Remaining reported input not attributable to captured content (mainly tool schemas, prior turns, formatting).",
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
    /// otherwise assigns the remainder to <see cref="TokenCategory.Overhead"/>.
    /// </summary>
    public void FitTo(double reportedTotal)
    {
        if (reportedTotal <= 0)
        {
            return;
        }

        var total = Total;
        if (total > reportedTotal)
        {
            Scale(reportedTotal / total);
        }
        else if (total < reportedTotal)
        {
            Add(TokenCategory.Overhead, reportedTotal - total);
        }
    }
}
