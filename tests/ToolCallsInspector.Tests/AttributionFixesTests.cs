using ToolCallsInspector.Core.Analysis;
using ToolCallsInspector.Core.Clients;
using ToolCallsInspector.Core.Storage;
using static ToolCallsInspector.Tests.SpanFactory;

namespace ToolCallsInspector.Tests;

public class AttributionFixesTests
{
    [Fact]
    public void Cache_reads_are_split_over_categories_earliest_content_first()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        var system = Words(300, "system");
        var user = Words(200, "user");
        var file = Words(1500, "file");
        f.Chat(1, 2, input: 900, output: 40, agent,
            inputMessages: Messages(Msg("system", Text(system)), Msg("user", Text(user))), system: system, userRequest: user);
        f.Tool(2.1, 2.2, "read_file", "call_1", agent, file);
        f.Chat(3, 4, input: 5000, output: 60, agent, cacheRead: 1500,
            inputMessages: Messages(
                Msg("system", Text(system)),
                Msg("user", Text(user)),
                Msg("assistant", ToolCall("call_1", "read_file")),
                Msg("tool", Text(file))));

        var trace = f.Analyze();
        var comp = trace.LlmCalls.Single(c => c.CallIndex == 2).Composition!;

        Assert.Equal(5000, comp.Total, 3);
        Assert.Equal(1500, comp.CachedTotal, 3);
        Assert.Equal(comp.Get(TokenCategory.SystemPrompt), comp.GetCached(TokenCategory.SystemPrompt), 3);
        Assert.Equal(comp.Get(TokenCategory.UserContext), comp.GetCached(TokenCategory.UserContext), 3);
        Assert.All(Enum.GetValues<TokenCategory>(), c => Assert.True(comp.GetCached(c) <= comp.Get(c) + 0.001));
        Assert.True(comp.GetCached(TokenCategory.ToolResults) < comp.Get(TokenCategory.ToolResults), "the newest tool result is mostly fresh");
        Assert.Equal(comp.GetCached(TokenCategory.ToolResults), comp.CachedToolResultsByTool.Values.Sum(), 3);

        // The first call had no cache reads: its whole input is fresh.
        Assert.Equal(0, trace.LlmCalls.Single(c => c.CallIndex == 1).Composition!.CachedTotal);

        // Totals keep the exact sums and add the cached share.
        var totals = trace.Totals;
        Assert.Equal(1500, totals.CachedInputByCategory.Values.Sum(), 3);
        Assert.Equal(5900, totals.InputByCategory.Values.Sum(), 3);
        var tool = totals.ByTool["read_file"];
        Assert.Equal(comp.ToolResultsByTool["read_file"], tool.ConsumedInputTokens, 3);
        Assert.Equal(comp.CachedToolResultsByTool["read_file"], tool.CachedConsumedInputTokens, 3);
        Assert.Equal(tool.ConsumedInputTokens - tool.CachedConsumedInputTokens, tool.FreshConsumedInputTokens, 3);

        // Only the second call's system prompt was a cache hit.
        var slice = totals.Slices().Single(s => s.Key == nameof(TokenCategory.SystemPrompt));
        Assert.Equal(comp.GetCached(TokenCategory.SystemPrompt), slice.Cached!.Value, 3);
        Assert.Equal(slice.Value - slice.Cached.Value, slice.Fresh, 3);
    }

    [Fact]
    public void Clients_declare_how_they_capture_input_messages()
    {
        Assert.Equal(InputCapture.Full, new VsCodeCopilotChatProfile().InputCapture);
        Assert.Equal(InputCapture.Incremental, new CopilotCliProfile().InputCapture);
        Assert.Equal(InputCapture.Full, new ClaudeCodeProfile().InputCapture);
        Assert.Equal(InputCapture.Full, new DemoClientProfile().InputCapture);
        Assert.Equal(InputCapture.Unknown, ((IClientProfile)new GenericGenAiClientProfile()).InputCapture);
    }

    [Fact]
    public void Full_prompt_clients_never_prepend_the_previous_transcript()
    {
        // VS Code sends the full prompt; after compaction the first message differs, which the old heuristic took for
        // "only new messages" and prepended the previous call's history (double counting the tool result).
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        f.Chat(1, 2, input: 500, output: 10, agent, inputMessages: Messages(Msg("user", Text(Words(300, "question")))));
        f.Tool(2.1, 2.2, "read_file", "call_1", agent, Words(1500, "file"));
        f.Chat(3, 4, input: 400, output: 10, agent, inputMessages: Messages(Msg("user", Text(Words(250, "summary")))));

        var second = f.Analyze().LlmCalls.Single(c => c.CallIndex == 2).Composition!;

        Assert.Equal(AttributionMethod.Content, second.Method);
        Assert.Equal(0, second.Get(TokenCategory.ToolResults));
    }

    [Fact]
    public void Incremental_clients_carry_history_into_the_next_turn()
    {
        var turn1 = new SpanFactory { Service = "github-copilot" };
        var a1 = turn1.Agent(0, 10);
        turn1.Chat(1, 2, input: 400, output: 20, a1, inputMessages: Messages(Msg("user", Text(Words(200, "question")))));
        turn1.Tool(2.1, 2.2, "read_file", "call_1", a1, Words(1500, "file"));
        turn1.Chat(3, 4, input: 2200, output: 30, a1,
            inputMessages: Messages(Msg("assistant", ToolCall("call_1", "read_file")), Msg("tool", ToolResponse("call_1", Words(1500, "file")))));

        var turn2 = new SpanFactory { Service = "github-copilot" };
        var a2 = turn2.Agent(20, 30);
        turn2.Chat(21, 22, input: 1900, output: 10, a2, inputMessages: Messages(Msg("user", Text(Words(100, "next")))));

        var traces = Analyze(turn1.Spans.Concat(turn2.Spans));
        var carried = traces.Single(t => t.TraceId == turn2.TraceId).LlmCalls.Single().Composition!;
        var alone = Analyze(turn2.Spans).Single().LlmCalls.Single().Composition!;

        Assert.Single(traces.Select(t => t.SessionKey).Distinct());
        Assert.True(alone.Get(TokenCategory.Overhead) > 1500, "without the previous turn the history is not captured");
        Assert.True(carried.Get(TokenCategory.ToolResults) > 1000, "the earlier tool result is carried into the new turn");
        Assert.Contains("read_file", carried.ToolResultsByTool.Keys);
        Assert.True(carried.Get(TokenCategory.Overhead) < 200, string.Join(", ", carried.ByCategory.Select(kv => $"{kv.Key}={kv.Value:0}")));
        Assert.Null(carried.SignificantNotCaptured(1900));
        Assert.Equal(1900, carried.Total, 3);
    }

    [Fact]
    public void Full_prompt_clients_are_not_affected_by_the_carry()
    {
        var file = Words(1500, "file");
        var turn1 = new SpanFactory();
        var a1 = turn1.Agent(0, 10);
        turn1.Chat(1, 2, input: 400, output: 20, a1, inputMessages: Messages(Msg("user", Text(Words(200, "question")))));
        turn1.Tool(2.1, 2.2, "read_file", "call_1", a1, file);

        var turn2 = new SpanFactory();
        var a2 = turn2.Agent(20, 30);
        turn2.Chat(21, 22, input: 2600, output: 10, a2, inputMessages: Messages(
            Msg("user", Text(Words(200, "question"))),
            Msg("assistant", ToolCall("call_1", "read_file")),
            Msg("tool", ToolResponse("call_1", file)),
            Msg("user", Text(Words(100, "next")))));

        var carried = Analyze(turn1.Spans.Concat(turn2.Spans)).Single(t => t.TraceId == turn2.TraceId).LlmCalls.Single().Composition!;
        var alone = Analyze(turn2.Spans).Single().LlmCalls.Single().Composition!;

        foreach (var category in Enum.GetValues<TokenCategory>())
        {
            Assert.Equal(alone.Get(category), carried.Get(category), 3);
        }
    }

    [Fact]
    public void Delta_attribution_continues_across_turns()
    {
        var turn1 = new SpanFactory { Service = "github-copilot" };
        var a1 = turn1.Agent(0, 10);
        turn1.Chat(1, 2, input: 1000, output: 100, a1);
        turn1.Tool(2.1, 2.2, "grep_search", "call_1", a1, "result");
        turn1.Chat(3, 4, input: 1600, output: 50, a1);

        var turn2 = new SpanFactory { Service = "github-copilot" };
        var a2 = turn2.Agent(20, 30);
        turn2.Chat(21, 22, input: 1800, output: 10, a2);

        var first = Analyze(turn1.Spans.Concat(turn2.Spans)).Single(t => t.TraceId == turn2.TraceId).LlmCalls.Single().Composition!;

        Assert.Equal(AttributionMethod.Delta, first.Method);
        Assert.Equal(500, first.ToolResultsByTool["grep_search"], 3);
        Assert.Equal(1800, first.Total, 3);
    }

    [Fact]
    public void Sub_agent_usage_is_rolled_up_on_the_tool_without_double_counting()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 20);
        f.Chat(1, 2, input: 1000, output: 50, agent);
        var tool = f.Tool(2.1, 10, "runSubagent", "call_1", agent, "sub-agent summary");
        var sub = f.Agent(2.2, 9.9, parent: tool, name: "Explore");
        f.Chat(3, 4, input: 2000, output: 100, sub, cacheRead: 500);
        f.Tool(4.1, 4.2, "read_file", "call_2", sub, "contents");
        f.Chat(5, 6, input: 3000, output: 200, sub, cacheRead: 1500);
        f.Chat(11, 12, input: 1200, output: 40, agent);

        var trace = f.Analyze();
        var toolNode = trace.All().Single(n => n.Span.SpanId == tool);

        Assert.Equal(new SubAgentUsage(2, 5000, 300, 2000), toolNode.SubAgent);
        Assert.Equal(5300, toolNode.SubAgent!.Total);
        Assert.All(trace.LlmCalls.Where(c => c.Parent?.Span.SpanId == sub), c => Assert.True(c.IsInSubAgent));
        Assert.All(trace.LlmCalls.Where(c => c.Parent?.Span.SpanId == agent), c => Assert.False(c.IsInSubAgent));

        var totals = trace.Totals;
        Assert.Equal(1000 + 2000 + 3000 + 1200, totals.Input);
        Assert.Equal(50 + 100 + 200 + 40, totals.Output);
        Assert.Equal(4, totals.LlmCalls);
        var byTool = totals.ByTool["runSubagent"];
        Assert.Equal(2, byTool.SubAgentLlmCalls);
        Assert.Equal(5000, byTool.SubAgentInput);
        Assert.Equal(300, byTool.SubAgentOutput);
        Assert.Equal(0, totals.ByTool["read_file"].SubAgentLlmCalls);

        var session = new TokenTotals();
        session.Add(totals);
        session.Add(totals);
        Assert.Equal(4, session.ByTool["runSubagent"].SubAgentLlmCalls);
    }

    [Fact]
    public void Saved_breakdown_round_trips_and_replaces_recomputation()
    {
        var turn = new SpanFactory { Service = "github-copilot" };
        var agent = turn.Agent(0, 10);
        turn.Chat(1, 2, input: 400, output: 20, agent, inputMessages: Messages(Msg("user", Text(Words(200, "question")))));
        var toolId = turn.Tool(2.1, 2.2, "read_file", "call_1", agent, Words(1500, "file"));
        turn.Chat(3, 4, input: 2200, output: 30, agent, cacheRead: 400,
            inputMessages: Messages(Msg("assistant", ToolCall("call_1", "read_file")), Msg("tool", ToolResponse("call_1", Words(1500, "file")))));
        var original = turn.Analyze();

        var saved = SavedBreakdown.Capture(original);
        var restored = SavedBreakdown.Deserialize(saved.Serialize(), SavedBreakdown.CompressTranscript(saved.CarryTranscript))!;

        Assert.NotNull(original.CarryOut?.Transcript);
        var carry = restored.RestoreCarry()!;
        Assert.Equal(original.CarryOut!.InputTokens, carry.InputTokens);
        Assert.Equal(original.CarryOut.Transcript!.Count, carry.Transcript!.Count);
        Assert.Equal(original.CarryOut.Transcript.Select(m => m.Role), carry.Transcript.Select(m => m.Role));

        // Re-analysing without any content (e.g. transcript deleted) uses the saved numbers.
        var records = turn.Spans.Select(s => Normalize(s).Record).ToList();
        var reanalysed = CreateAnalyzer().Analyze(turn.TraceId, records, new Dictionary<string, SpanContent>(), saved: restored);

        Assert.True(reanalysed.FromSavedBreakdown);
        foreach (var call in original.LlmCalls)
        {
            var copy = reanalysed.LlmCalls.Single(c => c.Span.SpanId == call.Span.SpanId).Composition!;
            Assert.Equal(call.Composition!.Method, copy.Method);
            foreach (var category in Enum.GetValues<TokenCategory>())
            {
                Assert.Equal(call.Composition.Get(category), copy.Get(category), 6);
                Assert.Equal(call.Composition.GetCached(category), copy.GetCached(category), 6);
            }

            Assert.Equal(call.Composition.ToolResultsByTool, copy.ToolResultsByTool);
        }

        Assert.Equal(
            original.All().Single(n => n.Span.SpanId == toolId).IntroducedTokens,
            reanalysed.All().Single(n => n.Span.SpanId == toolId).IntroducedTokens);
    }

    [Fact]
    public void Saved_breakdown_is_ignored_when_it_misses_an_llm_call()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        f.Chat(1, 2, input: 1000, output: 100, agent);
        var saved = SavedBreakdown.Capture(f.Analyze());

        f.Chat(3, 4, input: 1500, output: 10, agent);
        var records = f.Spans.Select(s => Normalize(s).Record).ToList();
        var trace = CreateAnalyzer().Analyze(f.TraceId, records, new Dictionary<string, SpanContent>(), saved: saved);

        Assert.False(trace.FromSavedBreakdown);
        Assert.All(trace.LlmCalls, c => Assert.Equal(c.Span.InputTokens!.Value, c.Composition!.Total, 3));
    }
}
