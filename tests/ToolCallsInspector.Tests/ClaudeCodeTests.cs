using System.Text.Json.Nodes;
using ToolCallsInspector.Core.Analysis;
using ToolCallsInspector.Core.Clients;
using ToolCallsInspector.Core.Storage;
using ToolCallsInspector.Core.Telemetry;
using ToolCallsInspector.Core.Transcripts;
using static ToolCallsInspector.Tests.SpanFactory;

namespace ToolCallsInspector.Tests;

public sealed class ClaudeCodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tci-claude-" + Guid.NewGuid().ToString("N"));
    private readonly string _session = Guid.NewGuid().ToString();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Maps_claude_code_spans_onto_agent_llm_call_and_tool()
    {
        var f = Trace(out _, out _, out _);
        var spans = f.Spans.Select(Normalize).ToDictionary(s => s.Record.Name, s => s);

        Assert.All(spans.Values, s => Assert.Equal(ClaudeCodeProfile.Id, s.Record.ClientId));
        Assert.All(spans.Values, s => Assert.Equal(_session, s.Record.SessionKey));

        var interaction = spans["claude_code.interaction"].Record;
        Assert.Equal(SpanCategory.Agent, interaction.Category);
        Assert.Equal("Claude Code", interaction.AgentName);
        Assert.Equal("what is the weather?", interaction.UserPrompt);

        var llm = spans["claude_code.llm_request"].Record;
        Assert.Equal(SpanCategory.LlmCall, llm.Category);
        Assert.Equal(_session, llm.ConversationId);
        // Claude Code's input_tokens excludes the prompt cache; input is everything the model read.
        Assert.Equal(12 + 9000 + 500, llm.InputTokens);
        Assert.Equal(9000, llm.CacheReadTokens);
        Assert.Equal(500, llm.CacheCreationTokens);
        Assert.Equal(80, llm.OutputTokens);
        Assert.Equal("claude-sonnet-4-5", llm.Model);
        Assert.Equal("tool_use", llm.FinishReasons);
        Assert.Equal(450, llm.TimeToFirstTokenMs);

        var (tool, toolContent) = spans["claude_code.tool"];
        Assert.Equal(SpanCategory.Tool, tool.Category);
        Assert.Equal("Bash", tool.ToolName);
        Assert.Equal("toolu_1", tool.ToolCallId);
        Assert.Contains("git status", toolContent.ToolArguments);
        Assert.Equal("nothing to commit", toolContent.ToolResult);
        Assert.True(tool.HasContent);

        var execution = spans["claude_code.tool.execution"].Record;
        Assert.Equal(SpanCategory.Other, execution.Category);
        Assert.Equal("ShellError", execution.ErrorType);
    }

    [Fact]
    public void Redacted_prompt_is_not_shown_as_the_prompt()
    {
        var f = new SpanFactory { Service = "claude-code" };
        f.Custom("claude_code.interaction", null, 0, 1, new() { ["session.id"] = _session, ["user_prompt"] = "<REDACTED>" });

        Assert.Null(Normalize(f.Spans.Single()).Record.UserPrompt);
    }

    [Fact]
    public void Claude_code_span_names_identify_the_client_without_its_service_name()
    {
        var f = new SpanFactory { Service = "unknown_service" };
        f.Custom("claude_code.llm_request", null, 0, 1, new() { ["input_tokens"] = 5L });

        Assert.Equal(ClaudeCodeProfile.Id, Normalize(f.Spans.Single()).Record.ClientId);
    }

    [Fact]
    public void Analyzes_a_claude_code_turn_as_one_interaction()
    {
        var trace = Trace(out _, out _, out _).Analyze();

        Assert.Equal(ClaudeCodeProfile.Id, trace.ClientId);
        Assert.Equal(_session, trace.SessionKey);
        Assert.Equal("what is the weather?", trace.Prompt);
        Assert.False(trace.IsBackground);
        Assert.Equal(9512, trace.LlmCalls.Single().Span.InputTokens);
        Assert.Equal("Bash", trace.ToolCalls.Single().Span.ToolName);
        Assert.True(trace.All().Single(n => n.Span.Name == "claude_code.tool.execution").IsTiming);
    }

    [Fact]
    public void Replays_the_branch_leading_to_each_call()
    {
        WriteTranscript();
        var source = new ClaudeCodeTranscriptSource(_root);

        Assert.Equal(1, source.TryGetPrompt(_session, "req_1")!.MessageCount);

        var prompt = source.TryGetPrompt(_session, "req_3")!;
        Assert.Equal("Claude Code transcript", prompt.SourceName);
        var messages = GenAiMessageParser.TryParse(prompt.InputMessagesJson)!;
        Assert.Equal(["user", "assistant", "tool", "assistant", "user"], messages.Select(m => m.Role));
        // The streamed text and tool_use lines of one response form one message.
        Assert.Equal([MessagePartKind.Text, MessagePartKind.ToolCall], messages[1].Parts.Select(p => p.Kind));
        var result = messages[2].Parts.Single();
        Assert.Equal("sunny", result.Content);
        Assert.Equal("get_weather", result.ToolName);
        Assert.DoesNotContain(messages, m => m.Parts.Any(p => p.Content.Contains("rewound")));
        Assert.DoesNotContain(messages, m => m.Parts.Any(p => p.Content.Contains("API Error")));
    }

    [Fact]
    public void Compaction_starts_a_new_history_with_the_summary()
    {
        WriteTranscript();

        var messages = GenAiMessageParser.TryParse(new ClaudeCodeTranscriptSource(_root).TryGetPrompt(_session, "req_4")!.InputMessagesJson)!;

        Assert.Equal("SUMMARY", messages.Single().Parts.Single().Content);
    }

    [Fact]
    public void Returns_the_raw_lines_about_a_call()
    {
        WriteTranscript();
        var source = new ClaudeCodeTranscriptSource(_root);

        Assert.True(source.HasTranscript(_session));
        Assert.False(source.HasTranscript("not-a-guid"));
        Assert.False(source.HasTranscript(Guid.NewGuid().ToString()));

        var tool = source.TryGetEvents(_session, "toolu_1")!;
        Assert.Equal(["assistant", "user"], tool.Events.Select(e => e.Type));
        Assert.Equal("Claude Code transcript", tool.SourceName);
        Assert.Equal(2, source.TryGetEvents(_session, "req_1")!.Events.Count);
    }

    [Fact]
    public void Transcript_fills_in_the_prompt_claude_code_does_not_export()
    {
        WriteTranscript(toolResult: Words(3000, "weather"));
        var f = new SpanFactory { Service = "claude-code" };
        var interaction = f.Custom("claude_code.interaction", null, 0, 10, new() { ["session.id"] = _session });
        f.Custom("claude_code.llm_request", interaction, 1, 2, new()
        {
            ["session.id"] = _session, ["model"] = "claude-sonnet-4-5", ["input_tokens"] = 20L, ["cache_read_tokens"] = 3980L,
            ["output_tokens"] = 5L, ["request_id"] = "req_3", ["gen_ai.response.id"] = "req_3",
        });
        var enricher = new TranscriptEnricher(new CopilotSessionTranscriptSource(Path.Combine(_root, "none")), new ClaudeCodeTranscriptSource(_root));

        var withoutTranscript = f.Analyze().LlmCalls.Single().Composition!;
        var withTranscript = f.Analyze(enricher).LlmCalls.Single().Composition!;

        Assert.True(withoutTranscript.Get(TokenCategory.ToolResults) == 0);
        Assert.True(withTranscript.Get(TokenCategory.ToolResults) > 2500);
        Assert.Contains("get_weather", withTranscript.ToolResultsByTool.Keys);
        var llm = Normalize(f.Spans[1]);
        Assert.Equal("Claude Code transcript", enricher.Enrich(new Dictionary<string, SpanContent> { [llm.Record.Id] = llm.Content })[llm.Record.Id].TranscriptSource);
        Assert.Same(enricher.SourceFor(_session), enricher.SourceFor(_session));
        Assert.IsType<ClaudeCodeTranscriptSource>(enricher.SourceFor(_session));
    }

    [Fact]
    public void Missing_system_prompt_stays_not_captured_instead_of_inflating_the_transcript()
    {
        WriteTranscript();
        var f = new SpanFactory { Service = "claude-code" };
        var interaction = f.Custom("claude_code.interaction", null, 0, 10, new() { ["session.id"] = _session });
        // ~18K of system prompt and tool schemas that the transcript does not record, on top of a few short messages.
        f.Custom("claude_code.llm_request", interaction, 1, 2, new()
        {
            ["session.id"] = _session, ["input_tokens"] = 20L, ["cache_read_tokens"] = 18000L, ["output_tokens"] = 5L, ["request_id"] = "req_3",
        });

        var composition = f.Analyze(new TranscriptEnricher(new ClaudeCodeTranscriptSource(_root))).LlmCalls.Single().Composition!;

        Assert.Null(composition.TokenizerScale);
        Assert.True(composition.Get(TokenCategory.ToolResults) < 20);
        Assert.True(composition.Get(TokenCategory.Overhead) > 17000);
    }

    [Fact]
    public void Api_request_log_supplies_the_exact_request_with_system_prompt_and_tools()
    {
        var bodies = WriteApiBodies();
        var source = new ClaudeApiBodiesSource(bodies);

        var prompt = source.TryGetPrompt(_session, "req_3")!;

        Assert.False(source.ProvidesEvents);
        Assert.True(source.HasTranscript(_session));
        Assert.StartsWith("You are Claude Code.\n\nBe concise.\n\nrule", prompt.SystemPrompt);
        Assert.Contains("get_weather", prompt.ToolDefinitionsJson);
        Assert.Contains("\"tool\"", prompt.InputMessagesJson);
        Assert.Contains("Rain tomorrow.", prompt.OutputMessagesJson);
        Assert.Equal("Claude Code API request log", prompt.SourceName);
        Assert.Null(source.TryGetPrompt(_session, "req_missing"));
        Assert.Null(new ClaudeApiBodiesSource(Path.Combine(_root, "none")).TryGetPrompt(_session, "req_3"));
    }

    [Fact]
    public void Api_request_log_is_preferred_and_leaves_nothing_uncaptured()
    {
        WriteTranscript();
        var bodies = WriteApiBodies();
        var f = new SpanFactory { Service = "claude-code" };
        var interaction = f.Custom("claude_code.interaction", null, 0, 10, new() { ["session.id"] = _session });
        f.Custom("claude_code.llm_request", interaction, 1, 2, new()
        {
            ["session.id"] = _session, ["input_tokens"] = 20L, ["cache_read_tokens"] = 1800L, ["output_tokens"] = 5L, ["request_id"] = "req_3",
        });
        f.Custom("claude_code.llm_request", interaction, 3, 4, new()
        {
            ["session.id"] = _session, ["input_tokens"] = 20L, ["cache_read_tokens"] = 1800L, ["output_tokens"] = 5L, ["request_id"] = "req_4",
        });
        var enricher = new TranscriptEnricher(new ClaudeApiBodiesSource(bodies), new ClaudeCodeTranscriptSource(_root));

        var calls = f.Analyze(enricher).LlmCalls.OrderBy(c => c.Span.StartTime).ToList();
        var exact = calls[0].Composition!;
        var fallback = calls[1].Composition!;

        Assert.True(exact.Get(TokenCategory.SystemPrompt) > 500);
        Assert.True(exact.Get(TokenCategory.ToolSchemas) > 0);
        Assert.Equal(0, exact.Get(TokenCategory.Overhead));
        Assert.True(fallback.Get(TokenCategory.Overhead) > 1000);
        var llm = Normalize(f.Spans[1]);
        var enriched = enricher.Enrich(new Dictionary<string, SpanContent> { [llm.Record.Id] = llm.Content })[llm.Record.Id];
        Assert.Equal("Claude Code API request log", enriched.TranscriptSource);
        Assert.NotNull(enriched.ToolDefinitions);
        Assert.IsType<ClaudeCodeTranscriptSource>(enricher.SourceFor(_session));
    }

    [Fact]
    public void Api_request_log_ignores_files_outside_its_folder()
    {
        var bodies = WriteApiBodies();
        File.Copy(Path.Combine(bodies, "r3.request.json"), Path.Combine(_root, "outside.request.json"));
        File.AppendAllText(Path.Combine(bodies, ClaudeApiBodiesSource.IndexFile), new JsonObject
        {
            ["session_id"] = _session, ["request_id"] = "req_evil", ["request_file"] = Path.Combine("..", "outside.request.json"),
        }.ToJsonString() + "\n");

        Assert.Null(new ClaudeApiBodiesSource(bodies).TryGetPrompt(_session, "req_evil"));
    }

    private string WriteApiBodies()
    {
        var dir = Path.Combine(_root, "api-bodies");
        Directory.CreateDirectory(dir);
        var request = new JsonObject
        {
            ["model"] = "claude-sonnet-4-5",
            ["system"] = new JsonArray(Block("text", ("text", "You are Claude Code.")), Block("text", ("text", "Be concise."))),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = "get_weather", ["description"] = Words(200, "weather"),
                ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["city"] = new JsonObject { ["type"] = "string" } } },
            }),
            ["messages"] = new JsonArray(
                User("what is the weather?"),
                Assistant("msg_1", Block("text", ("text", "Let me check.")), ToolUse("toolu_1", "get_weather")),
                User(new JsonArray(ToolResultBlock("toolu_1", "sunny"))),
                Assistant("msg_2", Block("text", ("text", "It is sunny."))),
                User("and tomorrow?")),
        };
        // A long system prompt like Claude Code's, so most of the input is system text.
        request["system"]!.AsArray().Add(Block("text", ("text", Words(900, "rule"))));
        File.WriteAllText(Path.Combine(dir, "r3.request.json"), request.ToJsonString());
        File.WriteAllText(Path.Combine(dir, "req_3.response.json"), Assistant("msg_3", Block("text", ("text", "Rain tomorrow."))).ToJsonString());
        var index = new JsonObject
        {
            ["timestamp"] = "2025-01-01T12:00:00.000Z", ["session_id"] = _session, ["query_source"] = "repl_main_thread", ["model"] = "claude-sonnet-4-5",
            ["request_id"] = "req_3", ["message_id"] = "msg_3", ["request_file"] = Path.Combine(dir, "r3.request.json"), ["response_file"] = "req_3.response.json",
        };
        File.WriteAllText(Path.Combine(dir, ClaudeApiBodiesSource.IndexFile), index.ToJsonString() + "\n");
        return dir;
    }

    private SpanFactory Trace(out string interaction, out string llm, out string tool)
    {
        var f = new SpanFactory { Service = "claude-code" };
        interaction = f.Custom("claude_code.interaction", null, 0, 10, new()
        {
            ["span.type"] = "claude_code.interaction", ["session.id"] = _session, ["user_prompt"] = "what is the weather?",
        });
        llm = f.Custom("claude_code.llm_request", interaction, 1, 2, new()
        {
            ["session.id"] = _session, ["model"] = "claude-sonnet-4-5", ["gen_ai.system"] = "anthropic", ["gen_ai.request.model"] = "claude-sonnet-4-5",
            ["input_tokens"] = 12L, ["cache_read_tokens"] = 9000L, ["cache_creation_tokens"] = 500L, ["output_tokens"] = 80L,
            ["request_id"] = "req_1", ["gen_ai.response.id"] = "req_1", ["stop_reason"] = "tool_use", ["ttft_ms"] = 450.0,
        });
        tool = f.Custom("claude_code.tool", interaction, 2, 3, new()
        {
            ["session.id"] = _session, ["tool_name"] = "Bash", ["tool_use_id"] = "toolu_1", ["gen_ai.tool.call.id"] = "toolu_1", ["full_command"] = "git status",
        }, new RawSpanEvent { Name = "tool.output", Attributes = new Dictionary<string, object?> { ["output"] = "nothing to commit" } });
        f.Custom("claude_code.tool.execution", tool, 2.1, 2.9, new() { ["session.id"] = _session, ["success"] = false, ["error_class"] = "ShellError" });
        return f;
    }

    private void WriteTranscript(string toolResult = "sunny")
    {
        var dir = Path.Combine(_root, "D--repos-demo");
        Directory.CreateDirectory(dir);
        string[] lines =
        [
            Line("user", "u1", null, User("what is the weather?")),
            Line("assistant", "a1", "u1", Assistant("msg_1", Block("text", ("text", "Let me check."))), "req_1"),
            Line("assistant", "a2", "a1", Assistant("msg_1", ToolUse("toolu_1", "get_weather")), "req_1"),
            Line("user", "r1", "a2", User(new JsonArray(ToolResultBlock("toolu_1", toolResult)))),
            // The user rewound to the first prompt and tried something else: not part of later prompts.
            Line("user", "x1", "u1", User("rewound attempt")),
            Line("assistant", "a3", "r1", Assistant("msg_2", Block("text", ("text", "It is sunny."))), "req_2"),
            Line("assistant", "s1", "a3", new JsonObject { ["role"] = "assistant", ["model"] = "<synthetic>", ["content"] = new JsonArray(Block("text", ("text", "API Error: overloaded"))) }),
            Line("user", "u2", "a3", User("and tomorrow?")),
            Line("assistant", "a4", "u2", Assistant("msg_3", Block("thinking", ("thinking", "hmm")), Block("text", ("text", "Rain."))), "req_3"),
            Line("system", "c1", null, null, extra: o => o["subtype"] = "compact_boundary"),
            Line("user", "u3", "c1", User("SUMMARY"), extra: o => o["isCompactSummary"] = true),
            Line("assistant", "a5", "u3", Assistant("msg_4", Block("text", ("text", "ok"))), "req_4"),
        ];
        File.WriteAllText(Path.Combine(dir, _session + ".jsonl"), string.Join("\n", lines) + "\n");
    }

    private string Line(string type, string uuid, string? parent, JsonObject? message, string? requestId = null, Action<JsonObject>? extra = null)
    {
        var line = new JsonObject
        {
            ["parentUuid"] = parent, ["isSidechain"] = false, ["sessionId"] = _session, ["type"] = type, ["uuid"] = uuid,
            ["timestamp"] = "2025-01-01T12:00:00.000Z",
        };
        if (message is not null) line["message"] = message;
        if (requestId is not null) line["requestId"] = requestId;
        extra?.Invoke(line);
        return line.ToJsonString();
    }

    private static JsonObject User(JsonNode content) => new() { ["role"] = "user", ["content"] = content };

    private static JsonObject User(string content) => User(JsonValue.Create(content));

    private static JsonObject Assistant(string id, params JsonObject[] blocks) => new()
    {
        ["id"] = id, ["type"] = "message", ["role"] = "assistant", ["model"] = "claude-sonnet-4-5",
        ["content"] = new JsonArray(blocks.Select(b => (JsonNode)b).ToArray()),
    };

    private static JsonObject Block(string type, params (string Key, string Value)[] fields)
    {
        var block = new JsonObject { ["type"] = type };
        foreach (var (key, value) in fields) block[key] = value;
        return block;
    }

    private static JsonObject ToolUse(string id, string name) =>
        new() { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject { ["city"] = "Lisbon" } };

    private static JsonObject ToolResultBlock(string id, string text) =>
        new() { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = new JsonArray(Block("text", ("text", text))) };
}
