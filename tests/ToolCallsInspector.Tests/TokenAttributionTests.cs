using ToolCallsInspector.Core.Analysis;
using ToolCallsInspector.Core.Storage;
using static ToolCallsInspector.Tests.SpanFactory;

namespace ToolCallsInspector.Tests;

public class TokenAttributionTests
{
    [Fact]
    public void Content_mode_sums_to_reported_input_and_maps_tool_results_by_name()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        var system = Words(300, "system");
        var user = Words(200, "user");
        var fileContent = Words(1500, "file");

        f.Chat(1, 2, input: 900, output: 40, agent,
            inputMessages: Messages(Msg("system", Text(system)), Msg("user", Text(user))), system: system, userRequest: user);
        f.Tool(2.1, 2.2, "read_file", "call_1", agent, fileContent);
        f.Chat(3, 4, input: 5000, output: 60, agent,
            inputMessages: Messages(
                Msg("system", Text(system)),
                Msg("user", Text(user)),
                Msg("assistant", ToolCall("call_1", "read_file")),
                Msg("tool", Text(fileContent))));

        var trace = f.Analyze();
        var second = trace.LlmCalls.Single(c => c.CallIndex == 2);

        Assert.Equal(AttributionMethod.Content, second.Composition!.Method);
        Assert.Equal(5000, second.Composition.Total, 3);
        Assert.True(second.Composition.Get(TokenCategory.ToolResults) > 1000);
        Assert.True(second.Composition.Get(TokenCategory.SystemPrompt) > 200);
        Assert.True(second.Composition.Get(TokenCategory.Overhead) > 0, "unexplained remainder goes to overhead");
        Assert.Contains("read_file", second.Composition.ToolResultsByTool.Keys);
    }

    [Fact]
    public void Tool_definitions_are_attributed_to_tool_schemas_not_the_uncaptured_remainder()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        var user = Words(100, "user");
        var schemas = Words(2000, "schema");
        f.Chat(1, 2, input: 10_000, output: 10, agent,
            inputMessages: Messages(Msg("user", Text(user))), toolDefinitions: schemas);

        var comp = f.Analyze().LlmCalls.Single().Composition!;

        Assert.Equal(AttributionMethod.Content, comp.Method);
        Assert.True(comp.Get(TokenCategory.ToolSchemas) > 1500);
        Assert.True(comp.Get(TokenCategory.Overhead) > 5000, "history the client did not export stays in the remainder");
        Assert.Equal(10_000, comp.Total, 3);
        Assert.Equal(comp.Get(TokenCategory.Overhead), comp.SignificantNotCaptured(10_000));
        Assert.Null(comp.SignificantNotCaptured(1_000_000));
    }

    [Fact]
    public void Content_mode_scales_down_when_estimate_exceeds_reported()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        var big = Words(5000);
        f.Chat(1, 2, input: 1000, output: 10, agent, inputMessages: Messages(Msg("user", Text(big))));

        var call = f.Analyze().LlmCalls.Single();

        Assert.Equal(1000, call.Composition!.Total, 3);
        Assert.Equal(1000, call.Composition.Get(TokenCategory.UserContext), 3);
    }

    [Fact]
    public void Delta_mode_splits_growth_between_assistant_history_and_tools()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        f.Chat(1, 2, input: 1000, output: 100, agent);
        var toolId = f.Tool(2.1, 2.2, "grep_search", "call_1", agent, "some result");
        f.Chat(3, 4, input: 1600, output: 50, agent);

        var trace = f.Analyze();
        var first = trace.LlmCalls.Single(c => c.CallIndex == 1);
        var second = trace.LlmCalls.Single(c => c.CallIndex == 2);
        var tool = trace.All().Single(n => n.Span.SpanId == toolId);

        Assert.Equal(AttributionMethod.Delta, second.Composition!.Method);
        Assert.Equal(1000, first.Composition!.Total, 3);
        Assert.Equal(1600, second.Composition.Total, 3);
        Assert.Equal(100, second.Composition.Get(TokenCategory.AssistantHistory), 3);
        Assert.Equal(500, second.Composition.ToolResultsByTool["grep_search"], 3);
        Assert.Equal(500, tool.IntroducedTokens!.Value, 3);
        Assert.False(tool.IntroducedIsEstimate);
    }

    [Fact]
    public void Delta_mode_excludes_reasoning_from_assistant_history()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        f.Chat(1, 2, input: 1000, output: 300, agent, reasoning: 250);
        f.Chat(3, 4, input: 1200, output: 50, agent);

        var second = f.Analyze().LlmCalls.Single(c => c.CallIndex == 2);

        Assert.Equal(50, second.Composition!.Get(TokenCategory.AssistantHistory), 3);
        Assert.Equal(150, second.Composition.Get(TokenCategory.UserContext), 3);
    }

    [Fact]
    public void Delta_mode_scales_composition_when_context_is_compacted()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        f.Chat(1, 2, input: 1000, output: 100, agent);
        f.Tool(2.1, 2.2, "read_file", "c1", agent, "x");
        f.Chat(3, 4, input: 3000, output: 100, agent);
        f.Chat(5, 6, input: 1500, output: 100, agent);

        var calls = f.Analyze().LlmCalls.OrderBy(c => c.CallIndex).ToList();
        var before = calls[1].Composition!;
        var after = calls[2].Composition!;

        Assert.Equal(1500, after.Total, 3);
        Assert.Equal(before.Get(TokenCategory.ToolResults) / 2, after.Get(TokenCategory.ToolResults), 3);
    }

    [Fact]
    public void Calls_without_usage_are_not_attributed()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        f.Chat(1, 2, input: 0, output: 0, agent);

        var call = f.Analyze().LlmCalls.Single();

        Assert.Equal(AttributionMethod.None, call.Composition!.Method);
        Assert.Equal(0, call.Composition.Total);
    }

    [Fact]
    public void Totals_slices_cover_all_tokens()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        f.Chat(1, 2, input: 1000, output: 100, agent);
        f.Tool(2.1, 2.2, "read_file", "c1", agent, "x");
        f.Chat(3, 4, input: 1600, output: 50, agent);

        var totals = f.Analyze().Totals;
        var slices = totals.Slices();

        Assert.Equal(totals.Total, slices.Sum(s => s.Value), 3);
        Assert.Equal(100, slices.Sum(s => s.Percent), 3);
        Assert.Equal(150, slices.Single(s => s.Key == TokenTotals.OutputKey).Value);
        Assert.Equal(500, totals.ByTool["read_file"].ConsumedInputTokens, 3);
    }

    [Fact]
    public void Incremental_messages_are_accumulated_into_the_full_prompt()
    {
        // The Copilot CLI only captures the messages added since the previous call.
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        var system = Words(300, "system");
        var user = Words(200, "user");
        var weather = Words(1500, "sunny");

        f.Chat(1, 2, input: 900, output: 40, agent, system: system,
            inputMessages: Messages(Msg("user", Text(user))),
            outputMessages: Messages(Msg("assistant", Text(Words(30, "thinking")), ToolCall("toolu_1", "warm-weather-get_weather_for_date"))));
        f.Tool(2.1, 2.2, "warm-weather-get_weather_for_date", "toolu_1", agent, weather);
        f.Chat(3, 4, input: 3000, output: 60, agent, system: system,
            inputMessages: Messages(Msg("tool", ToolResponse("toolu_1", weather))));

        var second = f.Analyze().LlmCalls.Single(c => c.CallIndex == 2).Composition!;

        Assert.Equal(AttributionMethod.Content, second.Method);
        Assert.Equal(3000, second.Total, 3);
        Assert.True(second.Get(TokenCategory.UserContext) > 150, "the user prompt from call #1 is still part of the prompt");
        Assert.True(second.Get(TokenCategory.AssistantHistory) > 20, "call #1's response is part of the prompt");
        Assert.True(second.ToolResultsByTool["warm-weather-get_weather_for_date"] > 1000);
        Assert.DoesNotContain(TokenComposition.UnknownTool, second.ToolResultsByTool.Keys);
    }

    [Fact]
    public void Mcp_tool_results_are_named_from_the_cli_mcp_attributes()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        var response = ToolResponse("toolu_9", Words(400, "warm"));
        response["mcp_server_name"] = "warm-weather";
        response["mcp_tool_name"] = "get_weather_forecast";
        f.Chat(1, 2, input: 2000, output: 10, agent, inputMessages: Messages(Msg("user", Text("hi")), Msg("tool", response)));

        var call = f.Analyze().LlmCalls.Single().Composition!;

        Assert.Contains("warm-weather-get_weather_forecast", call.ToolResultsByTool.Keys);
    }

    [Theory]
    [InlineData("<current_datetime>2026-10-30T16:29:01</current_datetime>\nWhat is the weather?\n<system_reminder>\n<sql_tables>todos</sql_tables>\n</system_reminder>", "What is the weather?")]
    [InlineData("<current_datetime>2026</current_datetime> Weather tomorrow? <system_reminder>\n<sql_tables>Available tables: to…", "Weather tomorrow?")]
    [InlineData("Explain a < b and <T> generics", "Explain a < b and <T> generics")]
    [InlineData("Did it pick up?\n\n<canvas-context>\n- name=\"doc:{&quot;scope&quot;}\" type=\"editor\"\n</canvas-context>", "Did it pick up?")]
    [InlineData("Did it pick up? <canvas-context> - name=\"doc:{&quot;scope&quot;:&quot;wo…", "Did it pick up?")]
    [InlineData("<only_context>nothing typed</only_context>", "<only_context>nothing typed</only_context>")]
    [InlineData("<current_datetime>2026</current_datetime>\n\nName this session based on the user's first message:\n\n<user_message>\nWhat is the weather?\n</user_message>", "Name this session based on the user's first message: What is the weather?")]
    public void Prompts_are_cleaned_of_injected_context(string raw, string expected) =>
        Assert.Equal(expected, GenAiMessageParser.CleanPrompt(raw));
}
