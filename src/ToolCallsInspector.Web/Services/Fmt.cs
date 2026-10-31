using System.Globalization;
using ToolCallsInspector.Core.Analysis;

namespace ToolCallsInspector.Web.Services;

public static class Fmt
{
    public static string Tokens(double value) => value switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (value / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (value / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => Math.Round(value).ToString("#,0", CultureInfo.InvariantCulture),
    };

    public static string Tokens(long value) => Tokens((double)value);

    public static string Tokens(int value) => Tokens((double)value);

    public static string Tokens(long? value) => value is null ? "–" : Tokens((double)value.Value);

    public static string Exact(double value) => Math.Round(value).ToString("#,0", CultureInfo.InvariantCulture);

    public static string Pct(double percent) => percent.ToString(percent is > 0 and < 1 ? "0.0" : "0", CultureInfo.InvariantCulture) + "%";

    public static string Ratio(double ratio) => Pct(ratio * 100);

    public static string Duration(double ms) => ms switch
    {
        < 1 => "<1 ms",
        < 1000 => $"{ms:0} ms",
        < 60_000 => $"{ms / 1000:0.0} s",
        < 3_600_000 => $"{(int)(ms / 60_000)}m {(int)(ms % 60_000 / 1000)}s",
        _ => $"{(int)(ms / 3_600_000)}h {(int)(ms % 3_600_000 / 60_000)}m",
    };

    public static string Time(DateTimeOffset value) => value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public static string DateTime(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public static string Relative(DateTimeOffset value)
    {
        var delta = DateTimeOffset.UtcNow - value;
        return delta.TotalSeconds switch
        {
            < 60 => "just now",
            < 3600 => $"{(int)delta.TotalMinutes}m ago",
            < 86400 => $"{(int)delta.TotalHours}h ago",
            _ => DateTime(value),
        };
    }

    public static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max] + "…";

    public static string OneLine(string? value, int max) => Truncate(value?.ReplaceLineEndings(" ").Trim(), max);

    public static string SessionUrl(string key) => $"sessions/{Uri.EscapeDataString(key)}";

    public static string SpanUrl(string id) => $"spans/{id}";

    public static string TraceUrl(string traceId, string? spanId = null) =>
        spanId is null ? $"traces/{traceId}" : $"traces/{traceId}?span={Uri.EscapeDataString(spanId)}";
}

/// <summary>Consistent colors for token categories across all charts.</summary>
public static class Palette
{
    public static string For(string key) => key switch
    {
        TokenTotals.OutputKey => "#6366f1",
        nameof(TokenCategory.SystemPrompt) => "#f59e0b",
        nameof(TokenCategory.UserContext) => "#10b981",
        nameof(TokenCategory.AssistantHistory) => "#a855f7",
        nameof(TokenCategory.ToolResults) => "#ef4444",
        nameof(TokenCategory.ToolSchemas) => "#0ea5e9",
        nameof(TokenCategory.Overhead) => "#94a3b8",
        _ => Series(StableHash(key)),
    };

    private static int StableHash(string value)
    {
        var hash = 17;
        foreach (var c in value)
        {
            hash = unchecked(hash * 31 + c);
        }

        return Math.Abs(hash % 1000);
    }

    public static string For(TokenCategory category) => For(category.ToString());

    private static readonly string[] SeriesColors =
        ["#0ea5e9", "#f97316", "#84cc16", "#ec4899", "#14b8a6", "#eab308", "#8b5cf6", "#f43f5e", "#22c55e", "#06b6d4"];

    public static string Series(int index) => SeriesColors[index % SeriesColors.Length];
}

