using System.Text.Json.Nodes;
using ToolCallsInspector.Core.Analysis;
using ToolCallsInspector.Core.Transcripts;
using static ToolCallsInspector.Tests.SpanFactory;

namespace ToolCallsInspector.Tests;

public sealed class CopilotTranscriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tci-transcripts-" + Guid.NewGuid().ToString("N"));
    private readonly string _session = Guid.NewGuid().ToString();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Replays_the_history_each_call_was_sent_across_turns()
    {
        Write(
            Event("system.message", new JsonObject { ["content"] = "SYSTEM" }),
            UserMessage("what is the weather?"),
            Assistant("call-1", toolCallId: "t1", toolName: "get_weather"),
            ToolResult("t1", "sunny, 28C"),
            Assistant("call-2", text: "It is sunny."),
            UserMessage("now, what time is it?"),
            Assistant("call-3", text: "18:15"));
        var source = new CopilotSessionTranscriptSource(_root);

        var first = source.TryGetPrompt(_session, "call-1")!;
        var third = source.TryGetPrompt(_session, "call-3")!;

        Assert.Equal(1, first.MessageCount);
        Assert.Equal("SYSTEM", third.SystemPrompt);
        var messages = GenAiMessageParser.TryParse(third.InputMessagesJson)!;
        Assert.Equal(["user", "assistant", "tool", "assistant", "user"], messages.Select(m => m.Role));
        var toolResult = messages[2].Parts.Single();
        Assert.Equal(MessagePartKind.ToolCallResponse, toolResult.Kind);
        Assert.Equal("sunny, 28C", toolResult.Content);
        Assert.Equal("get_weather", toolResult.ToolName);
        Assert.Equal("transformed: now, what time is it?", messages[4].Parts.Single().Content);
    }

    [Fact]
    public void Compaction_replaces_removed_messages_with_the_summary()
    {
        Write(
            UserMessage("one"),
            Assistant("a1", text: "r1"),
            UserMessage("two"),
            Assistant("a2", text: "r2"),
            Event("session.compaction_complete", new JsonObject
            {
                ["success"] = true, ["summaryContent"] = "SUMMARY", ["preCompactionMessagesLength"] = 4, ["messagesRemoved"] = 3,
            }),
            UserMessage("three"),
            Assistant("a3", text: "r3"));
        var source = new CopilotSessionTranscriptSource(_root);

        var messages = GenAiMessageParser.TryParse(source.TryGetPrompt(_session, "a3")!.InputMessagesJson)!;
        Assert.Equal(["SUMMARY", "r2", "transformed: three"], messages.Select(m => m.Parts.Single().Content));
        Assert.Equal(3, source.TryGetPrompt(_session, "a2")!.MessageCount);
    }

    [Fact]
    public void Picks_up_appended_events_and_ignores_a_partially_written_line()
    {
        Write(UserMessage("hi"), Assistant("a1", text: "hello"));
        var source = new CopilotSessionTranscriptSource(_root);
        Assert.NotNull(source.TryGetPrompt(_session, "a1"));
        Assert.Null(source.TryGetPrompt(_session, "a2"));

        var partial = Assistant("a2", text: "later");
        File.AppendAllText(EventsPath, UserMessage("again") + "\n" + partial[..20]);
        Assert.Null(source.TryGetPrompt(_session, "a2"));

        File.AppendAllText(EventsPath, partial[20..] + "\n");
        Assert.Equal(3, source.TryGetPrompt(_session, "a2")!.MessageCount);
    }

    [Theory]
    [InlineData("..\\..\\Windows")]
    [InlineData("not-a-guid")]
    public void Only_guid_conversation_ids_resolve_to_files(string conversationId)
    {
        Write(UserMessage("hi"), Assistant("a1"));
        Assert.Null(new CopilotSessionTranscriptSource(_root).TryGetPrompt(conversationId, "a1"));
    }

    [Fact]
    public void Full_prompt_turns_uncaptured_history_into_attributed_categories()
    {
        var earlierResult = Words(3000, "weather");
        Write(
            UserMessage("what is the weather?"),
            Assistant("call-1", toolCallId: "t1", toolName: "get_weather"),
            ToolResult("t1", earlierResult),
            Assistant("call-2", text: "It is sunny."),
            UserMessage("now, what time is it?"),
            Assistant("call-3", text: "18:15"));

        // A new turn: telemetry only carries the latest user message.
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        f.Chat(1, 2, input: 4000, output: 5, agent, inputMessages: Messages(Msg("user", Text("now, what time is it?"))),
            conversationId: _session, responseId: "call-3");

        var withoutTranscript = f.Analyze().LlmCalls.Single().Composition!;
        var withTranscript = f.Analyze(new TranscriptEnricher(new CopilotSessionTranscriptSource(_root))).LlmCalls.Single().Composition!;

        Assert.True(withoutTranscript.Get(TokenCategory.Overhead) > 3000);
        Assert.True(withTranscript.Get(TokenCategory.ToolResults) > 2500);
        Assert.Contains("get_weather", withTranscript.ToolResultsByTool.Keys);
        Assert.True(withTranscript.Get(TokenCategory.Overhead) < 1000);
    }

    [Fact]
    public void Complete_prompt_scales_estimates_to_the_reported_tokens_instead_of_reporting_a_gap()
    {
        Write(
            Event("system.message", new JsonObject { ["content"] = "You are a helpful assistant." }),
            UserMessage("what is the weather?"),
            Assistant("call-1", toolCallId: "t1", toolName: "get_weather"),
            ToolResult("t1", Words(3000, "weather")),
            Assistant("call-2", text: "It is sunny."));

        // The model's tokenizer counts roughly 1.5x what o200k does for this prompt.
        var f = new SpanFactory();
        var agent = f.Agent(0, 10);
        f.Chat(1, 2, input: 4800, output: 5, agent, inputMessages: Messages(Msg("user", Text("what is the weather?"))),
            conversationId: _session, responseId: "call-2");

        var composition = f.Analyze(new TranscriptEnricher(new CopilotSessionTranscriptSource(_root))).LlmCalls.Single().Composition!;

        Assert.Equal(0, composition.Get(TokenCategory.Overhead));
        Assert.Equal(4800, composition.Total, 3);
        Assert.InRange(composition.TokenizerScale!.Value, 1.2, 2.0);
        Assert.True(composition.Get(TokenCategory.ToolResults) > 4000);
    }

    [Fact]
    public void Returns_the_raw_lines_that_reference_an_llm_call_or_tool_call()
    {
        var assistant = Assistant("call-1", toolCallId: "t1", toolName: "get_weather");
        var start = Event("tool.execution_start", new JsonObject { ["toolCallId"] = "t1", ["toolName"] = "get_weather" });
        var result = ToolResult("t1", "sunny");
        var usage = Event("session.usage_record", new JsonObject { ["usage"] = new JsonObject { ["apiCallId"] = "call-1", ["inputTokens"] = 10 } });
        Write(UserMessage("weather?"), assistant, start, result, usage);
        var source = new CopilotSessionTranscriptSource(_root);

        var call = source.TryGetEvents(_session, "call-1")!;
        var tool = source.TryGetEvents(_session, "t1")!;

        Assert.Equal(EventsPath, call.SourcePath);
        Assert.Equal([(2, "assistant.message", assistant), (5, "session.usage_record", usage)], call.Events.Select(e => (e.LineNumber, e.Type, e.Json)));
        Assert.Equal([(3, "tool.execution_start", start), (4, "tool.execution_complete", result)], tool.Events.Select(e => (e.LineNumber, e.Type, e.Json)));
        Assert.Empty(source.TryGetEvents(_session, "unknown")!.Events);
        Assert.Null(source.TryGetEvents(Guid.NewGuid().ToString(), "call-1"));
    }

    private string EventsPath => Path.Combine(_root, _session, "events.jsonl");

    private void Write(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(EventsPath)!);
        File.WriteAllLines(EventsPath, lines);
    }

    private static string Event(string type, JsonObject data) =>
        new JsonObject { ["type"] = type, ["data"] = data, ["id"] = Guid.NewGuid().ToString() }.ToJsonString();

    private static string UserMessage(string text) =>
        Event("user.message", new JsonObject { ["content"] = text, ["transformedContent"] = "transformed: " + text });

    private static string Assistant(string apiCallId, string? text = null, string? toolCallId = null, string? toolName = null)
    {
        var requests = new JsonArray();
        if (toolCallId is not null)
        {
            requests.Add(new JsonObject { ["toolCallId"] = toolCallId, ["name"] = toolName, ["arguments"] = new JsonObject { ["q"] = 1 } });
        }

        return Event("assistant.message", new JsonObject { ["apiCallId"] = apiCallId, ["content"] = text ?? "", ["toolRequests"] = requests });
    }

    private static string ToolResult(string toolCallId, string content) =>
        Event("tool.execution_complete", new JsonObject
        {
            ["toolCallId"] = toolCallId, ["success"] = true, ["result"] = new JsonObject { ["content"] = content },
        });
}
