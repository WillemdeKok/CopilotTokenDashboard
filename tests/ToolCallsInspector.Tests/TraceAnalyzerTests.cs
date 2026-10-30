using ToolCallsInspector.Core.Normalization;
using ToolCallsInspector.Core.Storage;
using static ToolCallsInspector.Tests.SpanFactory;

namespace ToolCallsInspector.Tests;

public class TraceAnalyzerTests
{
    [Fact]
    public void In_progress_cli_turns_join_their_conversation_and_take_the_prompt_from_the_first_request()
    {
        var f = new SpanFactory();
        const string missingRootAgent = "00000000000000ff";
        var history = SpanFactory.Messages(
            SpanFactory.Msg("user", SpanFactory.Text("Earlier question")),
            SpanFactory.Msg("assistant", SpanFactory.Text("Earlier answer")),
            SpanFactory.Msg("user", SpanFactory.Text("<current_datetime>2026-10-30</current_datetime>\n\nGroup traces by conversation?")));
        f.Custom("chat claude", missingRootAgent, 1, 2, new()
        {
            [GenAiKeys.OperationName] = "chat",
            [GenAiKeys.RequestModel] = "claude",
            [GenAiKeys.ConversationId] = "conv-42",
            [GenAiKeys.InputTokens] = 100L,
            [GenAiKeys.OutputTokens] = 10L,
            [GenAiKeys.InputMessages] = history,
        });

        var trace = f.Analyze();

        Assert.True(trace.InProgress);
        Assert.Equal("conv-42", trace.SessionKey);
        Assert.Equal("Group traces by conversation?", trace.Prompt);
    }

    [Fact]
    public void Shell_tools_with_a_non_zero_exit_code_are_failures()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        f.Chat(1, 2, 1000, 50, agent);
        var failed = f.Tool(2.1, 3, "powershell", "call_a", agent, "\n<shellId: 47 completed with exit code 35>", exitCode: 35);
        var ok = f.Tool(3.1, 4, "powershell", "call_b", agent, "Sunny, +12°C", exitCode: 0);

        var trace = f.Analyze();

        Assert.Equal("exit code 35", trace.All().Single(n => n.Span.SpanId == failed).Span.ErrorType);
        Assert.Null(trace.All().Single(n => n.Span.SpanId == ok).Span.ErrorType);
    }

    [Fact]
    public void Session_naming_runs_are_background_tasks_titled_with_their_subject()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 3, name: "invoke_agent",
            prompt: "<current_datetime>2026-10-30T17:43:35</current_datetime>\n\nName this session based on the user's first message:\n\n<user_message>\nWhat is the weather going to be today?\n</user_message>");
        f.Chat(0.1, 2.9, 159, 224, agent);

        var trace = f.Analyze();

        Assert.True(trace.IsBackground);
        Assert.Equal("Session naming", trace.BackgroundTask);
        Assert.Equal("Background: Session naming · “What is the weather going to be today?”", trace.DisplayTitle);
    }

    [Theory]
    [InlineData("Name this session based on the user's first message:", "Session naming", null)]
    [InlineData("Name this session based on the user's first message: Fix the build", "Session naming", "Fix the build")]
    [InlineData("What is the weather going to be today?", null, null)]
    [InlineData("Can you name this session after me?", null, null)]
    public void Utility_prompts_are_recognised(string prompt, string? label, string? subject)
    {
        var match = Core.Analysis.UtilityPrompts.Match(prompt);
        Assert.Equal(label, match?.Label);
        Assert.Equal(subject, match?.Subject);
    }

    [Fact]
    public void Links_tools_to_the_llm_call_that_requested_them()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        var c1 = f.Chat(1, 2, 1000, 50, agent);
        var t1 = f.Tool(2.1, 2.5, "read_file", "call_a", agent);
        var t2 = f.Tool(2.1, 2.6, "grep_search", "call_b", agent);
        var c2 = f.Chat(3, 4, 1500, 20, agent);

        var trace = f.Analyze();
        var call1 = trace.All().Single(n => n.Span.SpanId == c1);

        Assert.Equal([t1, t2], call1.RequestedTools.Select(t => t.Span.SpanId).Order());
        Assert.All(call1.RequestedTools, t => Assert.Same(call1, t.RequestedBy));
        Assert.Empty(trace.All().Single(n => n.Span.SpanId == c2).RequestedTools);

        // Timeline: tools are nested under their call, not repeated at the agent level.
        var root = Assert.Single(trace.Roots);
        Assert.Equal([c1, c2], root.TimelineItems().Select(n => n.Span.SpanId));
    }

    [Fact]
    public void Creates_placeholder_root_when_agent_span_has_not_arrived()
    {
        var f = new SpanFactory();
        const string missingAgent = "00000000000000ff";
        f.Chat(1, 2, 1000, 50, missingAgent);
        f.Tool(2.1, 2.2, "read_file", "c1", missingAgent);

        var trace = f.Analyze();

        var root = Assert.Single(trace.Roots);
        Assert.True(root.IsPlaceholder);
        Assert.True(trace.InProgress);
        Assert.False(trace.IsBackground);
        Assert.Equal(2, root.Children.Count);
        Assert.Equal("session-1", trace.SessionKey);
    }

    [Fact]
    public void Nests_subagent_under_its_runSubagent_tool_and_attributes_chains_separately()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 20);
        f.Chat(1, 2, 1000, 50, agent);
        var runSub = f.Tool(2.1, 10, "runSubagent", "call_sub", agent, "summary from explore agent");
        var sub = f.Agent(2.2, 9.9, runSub, "Explore", session: "sub-session", parentSession: "session-1");
        f.Chat(3, 4, 400, 30, sub, session: "sub-session", parentSession: "session-1");
        f.Chat(5, 6, 700, 30, sub, session: "sub-session", parentSession: "session-1");
        f.Chat(11, 12, 1300, 40, agent);

        var trace = f.Analyze();
        var subNode = trace.All().Single(n => n.Span.SpanId == sub);

        Assert.Equal(runSub, subNode.Parent!.Span.SpanId);
        Assert.Equal(2, subNode.Children.Count(c => c.Category == SpanCategory.LlmCall));
        Assert.Equal([1, 2], subNode.Children.Where(c => c.Category == SpanCategory.LlmCall).Select(c => c.CallIndex!.Value).Order());
        Assert.Equal(4, trace.LlmCalls.Count);
        Assert.Equal(1000 + 400 + 700 + 1300, trace.Totals.Input);
        Assert.Equal("session-1", trace.SessionKey);
    }

    [Fact]
    public void Agent_usage_is_not_double_counted()
    {
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        f.Chat(1, 2, 1000, 50, agent);

        var trace = f.Analyze();

        Assert.Equal(1000, trace.Totals.Input);
        Assert.Equal(50, trace.Totals.Output);
        Assert.Equal(1, trace.Totals.LlmCalls);
    }

    [Fact]
    public void Orphan_chat_with_parent_session_is_a_background_request_in_that_session()
    {
        var f = new SpanFactory();
        f.Chat(1, 2, 300, 10, parent: null, session: "title-request", parentSession: "session-1");

        var trace = f.Analyze();

        Assert.True(trace.IsBackground);
        Assert.Equal("session-1", trace.SessionKey);
    }

    [Fact]
    public void User_prompt_is_taken_from_agent_input_messages()
    {
        var f = new SpanFactory();
        var raw = f.Agent(0, 10);
        var spans = f.Spans.ToList();
        var agentSpan = spans.Single(s => s.SpanId == raw);
        var withPrompt = new Core.Telemetry.RawSpan
        {
            TraceId = agentSpan.TraceId,
            SpanId = agentSpan.SpanId,
            Name = agentSpan.Name,
            StartTime = agentSpan.StartTime,
            EndTime = agentSpan.EndTime,
            ResourceAttributes = agentSpan.ResourceAttributes,
            Attributes = new Dictionary<string, object?>(agentSpan.Attributes)
            {
                ["gen_ai.input.messages"] = Messages(Msg("user", Text("Fix the bug in parser.cs"))),
            },
        };

        var trace = Analyze([withPrompt]).Single();

        Assert.Equal("Fix the bug in parser.cs", trace.Prompt);
    }
}
