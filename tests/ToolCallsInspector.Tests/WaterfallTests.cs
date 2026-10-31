using ToolCallsInspector.Core.Analysis;
using ToolCallsInspector.Web.Services;

namespace ToolCallsInspector.Tests;

public class WaterfallTests
{
    private static readonly DateTimeOffset Start = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static TraceAnalysis Trace(double totalMs) => new()
    {
        TraceId = "t",
        SessionKey = "s",
        ClientId = "c",
        Roots = [],
        LlmCalls = [],
        ToolCalls = [],
        Totals = new TokenTotals(),
        Start = Start,
        End = Start.AddMilliseconds(totalMs),
    };

    [Theory]
    [InlineData(0, 10_000)]       // whole trace
    [InlineData(9_999.9, 0.01)]   // tiny span at the very end (previously threw)
    [InlineData(10_000, 0)]       // zero-length span at the end
    [InlineData(-50, 5)]          // clock skew before trace start
    public void Bars_always_fit_inside_the_track(double offsetMs, double durationMs)
    {
        var (left, width) = Waterfall.Position(Trace(10_000), Start.AddMilliseconds(offsetMs), durationMs);

        Assert.InRange(left, 0, 100);
        Assert.InRange(width, 0.4, 100);
        Assert.True(left + width <= 100.0001, $"left {left} + width {width} overflows");
    }

    [Fact]
    public void Cli_timing_spans_are_hidden_unless_requested()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        var firstTurn = f.Custom("session.first_turn", agent, 0, 1, new());
        f.Custom("session.timing.mcp_catalog", firstTurn, 0, 0.5, new());
        f.Custom("session.timing.model_request_preparation", agent, 1, 1.1, new());
        f.Chat(1, 2, input: 100, output: 5, agent);
        var trace = f.Analyze();

        var hidden = Waterfall.Build(trace, hideTimings: true);
        var shown = Waterfall.Build(trace, hideTimings: false);

        Assert.Equal(3, ViewPreferences.CountTimings(trace.Roots));
        Assert.DoesNotContain(hidden, r => r.Node.Span.Name.StartsWith("session.", StringComparison.Ordinal));
        Assert.Contains(hidden, r => r.Node.Category == Core.Storage.SpanCategory.LlmCall);
        Assert.Equal(hidden.Count + 3, shown.Count);
    }
}
