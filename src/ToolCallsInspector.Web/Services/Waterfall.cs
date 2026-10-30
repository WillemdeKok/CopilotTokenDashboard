using ToolCallsInspector.Core.Analysis;
using ToolCallsInspector.Core.Storage;

namespace ToolCallsInspector.Web.Services;

public sealed record WaterfallRow(SpanNode Node, int Depth, double LeftPercent, double WidthPercent, bool HasChildren);

/// <summary>Flattens a trace's span tree into time-positioned rows for waterfall rendering.</summary>
public static class Waterfall
{
    public static double TotalMs(TraceAnalysis trace) => Math.Max(1, (trace.End - trace.Start).TotalMilliseconds);

    public static IReadOnlyList<WaterfallRow> Build(TraceAnalysis trace, ISet<string>? collapsed = null)
    {
        var rows = new List<WaterfallRow>();
        foreach (var root in trace.Roots.OrderBy(r => r.Span.StartTime))
        {
            Add(root, 0);
        }

        return rows;

        void Add(SpanNode node, int depth)
        {
            var (left, width) = Position(trace, node.Span.StartTime, node.Span.DurationMs);
            rows.Add(new WaterfallRow(node, depth, left, width, node.Children.Count > 0));
            if (collapsed?.Contains(node.Span.Id) == true)
            {
                return;
            }

            foreach (var child in node.Children.OrderBy(c => c.Span.StartTime))
            {
                Add(child, depth + 1);
            }
        }
    }

    public static (double Left, double Width) Position(TraceAnalysis trace, DateTimeOffset start, double durationMs)
    {
        var total = TotalMs(trace);
        var width = Math.Clamp(durationMs / total * 100, 0.4, 100);
        var left = Math.Clamp((start - trace.Start).TotalMilliseconds / total * 100, 0, 100 - width);
        return (left, width);
    }

    public static string CssClass(SpanNode node) => node.Category switch
    {
        SpanCategory.LlmCall => "llm",
        SpanCategory.Tool => node.Span.StatusCode == 2 || node.Span.ErrorType is not null ? "tool err" : "tool",
        SpanCategory.Agent => "agent",
        SpanCategory.Hook => "hook",
        _ => "other",
    };

    /// <summary>Axis tick labels at 0/25/50/75/100% of the trace duration.</summary>
    public static IEnumerable<(double Percent, string Label)> Ticks(TraceAnalysis trace)
    {
        var total = TotalMs(trace);
        for (var i = 0; i <= 4; i++)
        {
            yield return (i * 25, i == 0 ? "0" : Fmt.Duration(total * i / 4));
        }
    }
}
