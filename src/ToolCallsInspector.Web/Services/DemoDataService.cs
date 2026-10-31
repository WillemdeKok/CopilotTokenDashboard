using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ToolCallsInspector.Core.Clients;
using ToolCallsInspector.Core.Ingestion;
using ToolCallsInspector.Core.Storage;
using ToolCallsInspector.Core.Telemetry;
using ToolCallsInspector.Ingestion.Otlp;

namespace ToolCallsInspector.Web.Services;

/// <summary>
/// Generates a realistic VS Code Copilot Chat OTLP/JSON payload (agent loop, tool calls, a sub-agent,
/// a hook and a background title request) and feeds it through the real OTLP parser and ingestion pipeline.
/// It is sent under its own service name, so it shows up as the "Demo data" client and can be removed on its own.
/// </summary>
public sealed class DemoDataService(ITelemetryIngestor ingestor, ISpanStore store, InspectorEvents events)
{
    public const string SourceName = "demo-data";

    public async Task<int> LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = BuildPayload(DateTimeOffset.UtcNow.AddMinutes(-12));
        var spans = OtlpJsonTraceParser.Parse(Encoding.UTF8.GetBytes(json));
        return await ingestor.IngestAsync(spans, SourceName, cancellationToken);
    }

    /// <summary>Deletes every demo trace (including demo data loaded by older versions, which was labelled as VS Code); returns the number of traces removed.</summary>
    public async Task<int> RemoveAsync(CancellationToken cancellationToken = default)
    {
        var removed = await store.DeleteClientAsync(DemoClientProfile.Id, SourceName, cancellationToken);
        if (removed > 0)
        {
            events.PublishCleared();
        }

        return removed;
    }

    public static string BuildPayload(DateTimeOffset start)
    {
        var b = new PayloadBuilder(start);
        var session = "demo-" + Guid.NewGuid().ToString("N")[..8];
        const string model = "claude-sonnet-4.5";
        var system = SystemPrompt();
        string[] tools = ["read_file", "replace_string_in_file", "run_in_terminal", "grep_search", "create_file", "runSubagent"];

        // Interaction 1: message content captured -> content-based attribution.
        {
            var trace = PayloadBuilder.NewTraceId();
            var agent = PayloadBuilder.NewSpanId();
            const string prompt = "Add input validation to the signup form and write unit tests for it.";
            var userContext = prompt + "\n\n<attachments>\n" + FakeCode("SignupForm.tsx", 150) + "</attachments>";
            var file1 = FakeCode("validation.ts", 260);
            var file2 = FakeCode("SignupForm.test.tsx", 200);
            var testOutput = FakeTestOutput();
            const string args1 = """{"filePath":"src/validation.ts"}""";
            const string args2 = """{"filePath":"src/SignupForm.test.tsx"}""";
            const string args3 = """{"filePath":"src/validation.ts","oldString":"export function validate","newString":"export function validateSignup"}""";
            const string args4 = """{"command":"npm test -- SignupForm"}""";

            var history = new List<JsonObject> { Msg("system", Text(system)), Msg("user", Text(userContext)) };
            b.Chat(new ChatSpec(trace, agent, session, model, 0.3, 3.9, 17_200, 236, 0, 17_000)
            {
                Messages = history,
                System = system,
                UserRequest = userContext,
                Reply = [ToolCall("call_1", "read_file", args1), ToolCall("call_2", "read_file", args2)],
            });
            b.Tool(new ToolSpec(trace, agent, session, "read_file", "call_1", 4.0, 4.1, args1, file1));
            b.Tool(new ToolSpec(trace, agent, session, "read_file", "call_2", 4.0, 4.2, args2, file2));

            history.Add(Msg("assistant", ToolCall("call_1", "read_file", args1), ToolCall("call_2", "read_file", args2)));
            history.Add(Msg("tool", Text(file1)));
            history.Add(Msg("tool", Text(file2)));
            b.Chat(new ChatSpec(trace, agent, session, model, 4.4, 11.8, 25_600, 612, 17_100, 8_400)
            {
                Messages = history,
                System = system,
                UserRequest = userContext,
                Reply = [Text("I'll add a validation schema and update the tests."), ToolCall("call_3", "replace_string_in_file", args3), ToolCall("call_4", "run_in_terminal", args4)],
            });
            b.Tool(new ToolSpec(trace, agent, session, "replace_string_in_file", "call_3", 11.9, 12.1, args3, "The file was edited successfully."));
            b.Hook(trace, agent, session, "PostToolUse", 12.1, 12.3, "pass");
            b.Tool(new ToolSpec(trace, agent, session, "run_in_terminal", "call_4", 12.3, 19.6, args4, testOutput));

            history.Add(Msg("assistant", Text("I'll add a validation schema and update the tests."), ToolCall("call_3", "replace_string_in_file", args3), ToolCall("call_4", "run_in_terminal", args4)));
            history.Add(Msg("tool", Text("The file was edited successfully.")));
            history.Add(Msg("tool", Text(testOutput)));
            b.Chat(new ChatSpec(trace, agent, session, model, 19.8, 27.5, 27_300, 845, 25_500, 1_700)
            {
                Messages = history,
                System = system,
                UserRequest = userContext,
                Reply = [Text("Validation is in place and all 14 tests pass. Summary of changes: ...")],
                Finish = "stop",
            });

            b.Agent(new AgentSpec(trace, agent, null, session, "copilot", 0.0, 27.9, prompt, 70_100, 1_693, tools));
        }

        // Background request: chat title generation (no agent span; child session of the main chat).
        {
            var trace = PayloadBuilder.NewTraceId();
            b.Chat(new ChatSpec(trace, null, session + "-title", "gpt-4o-mini", 28.2, 28.9, 412, 9, 0, 0)
            {
                Messages = [Msg("system", Text("Generate a short title for this conversation.")), Msg("user", Text("Add input validation to the signup form and write unit tests for it."))],
                Reply = [Text("Signup form validation")],
                Finish = "stop",
                AgentName = "title",
                ParentSession = session,
            });
        }

        // Interaction 2: no message content (as when content is truncated) -> delta-based attribution, plus a sub-agent.
        {
            var trace = PayloadBuilder.NewTraceId();
            var agent = PayloadBuilder.NewSpanId();
            const string prompt = "Now refactor the validation into a reusable hook and find other forms that could use it.";
            const string subPrompt = "Find all forms that do manual validation";
            const string subArgs = """{"prompt":"Find all forms that do manual validation"}""";

            b.Chat(new ChatSpec(trace, agent, session, model, 61.0, 64.2, 28_400, 168, 27_200, 1_100)
            {
                System = system,
                UserRequest = prompt,
                Reply = [ToolCall("call_10", "runSubagent", subArgs)],
            });

            var subagentTool = b.Tool(new ToolSpec(trace, agent, session, "runSubagent", "call_10", 64.3, 82.0, subArgs,
                "Found 3 forms with manual validation: LoginForm.tsx, ProfileForm.tsx, CheckoutForm.tsx."));

            var sub = PayloadBuilder.NewSpanId();
            var subSession = session + "-sub1";
            const string subModel = "claude-haiku-4.5";
            const string subSystem = "You are a code search agent. Locate relevant code and report concise findings.";
            b.Chat(new ChatSpec(trace, sub, subSession, subModel, 64.6, 66.0, 9_120, 96, 0, 9_000)
            {
                System = subSystem, UserRequest = subPrompt, ParentSession = session,
                Reply = [ToolCall("call_s1", "grep_search", """{"query":"onSubmit"}""")],
            });
            b.Tool(new ToolSpec(trace, sub, subSession, "grep_search", "call_s1", 66.1, 66.4, """{"query":"onSubmit"}""", FakeGrep()) { ParentSession = session });
            b.Chat(new ChatSpec(trace, sub, subSession, subModel, 66.6, 69.8, 11_430, 214, 9_100, 2_300)
            {
                System = subSystem, UserRequest = subPrompt, ParentSession = session,
                Reply = [ToolCall("call_s2", "read_file", """{"filePath":"src/LoginForm.tsx"}""")],
            });
            b.Tool(new ToolSpec(trace, sub, subSession, "read_file", "call_s2", 69.9, 70.0, """{"filePath":"src/LoginForm.tsx"}""", FakeCode("LoginForm.tsx", 80)) { ParentSession = session });
            b.Chat(new ChatSpec(trace, sub, subSession, subModel, 70.2, 81.5, 13_520, 310, 11_400, 2_100)
            {
                System = subSystem, UserRequest = subPrompt, ParentSession = session,
                Reply = [Text("Found 3 forms with manual validation...")], Finish = "stop",
            });
            b.Agent(new AgentSpec(trace, sub, subagentTool, subSession, "Explore", 64.5, 81.8, subPrompt, 34_070, 620, ["grep_search", "read_file"]) { ParentSession = session });

            b.Chat(new ChatSpec(trace, agent, session, model, 82.2, 90.4, 28_700, 702, 28_350, 300)
            {
                System = system, UserRequest = prompt,
                Reply = [ToolCall("call_11", "create_file", """{"filePath":"src/hooks/useValidation.ts"}""")],
            });
            b.Tool(new ToolSpec(trace, agent, session, "create_file", "call_11", 90.5, 90.7, """{"filePath":"src/hooks/useValidation.ts"}""", "File created."));
            b.Chat(new ChatSpec(trace, agent, session, model, 90.9, 99.6, 29_450, 931, 28_650, 750)
            {
                System = system, UserRequest = prompt,
                Reply = [Text("Created useValidation and identified 3 forms to migrate.")], Finish = "stop",
            });

            b.Agent(new AgentSpec(trace, agent, null, session, "copilot", 60.8, 99.9, prompt, 86_550, 1_801, tools));
        }

        return b.Build();
    }

    private static JsonObject Text(string content) => new() { ["type"] = "text", ["content"] = content };

    private static JsonObject ToolCall(string id, string name, string argsJson) =>
        new() { ["type"] = "tool_call", ["id"] = id, ["name"] = name, ["arguments"] = JsonNode.Parse(argsJson) };

    private static JsonObject Msg(string role, params JsonObject[] parts) =>
        new() { ["role"] = role, ["parts"] = new JsonArray(parts.Select(p => (JsonNode)p.DeepClone()).ToArray()) };

    private static string SystemPrompt()
    {
        var sb = new StringBuilder("You are an expert AI programming assistant, working with a user in the VS Code editor.\n");
        string[] rules =
        [
            "Follow the user's requirements carefully and to the letter.",
            "Use the available tools to gather context before making changes.",
            "Prefer small, surgical edits and keep existing code style.",
            "When running terminal commands, avoid interactive prompts.",
            "Never reveal secrets or credentials found in the workspace.",
            "Explain your reasoning briefly before calling tools.",
        ];
        for (var i = 0; i < 150; i++)
        {
            sb.AppendLine($"<instruction id=\"{i}\">{rules[i % rules.Length]} Consider edge cases, keep the user informed of progress, and validate results after each step.</instruction>");
        }

        return sb.ToString();
    }

    private static string FakeCode(string file, int lines)
    {
        var sb = new StringBuilder($"// {file}\n");
        for (var i = 0; i < lines; i++)
        {
            sb.AppendLine((i % 5) switch
            {
                0 => $"export function handler{i}(input: FormValues): ValidationResult {{",
                1 => $"  const errors: Record<string, string> = {{}}; // line {i}",
                2 => "  if (!input.email?.includes('@')) errors.email = 'Invalid email address';",
                3 => "  return { valid: Object.keys(errors).length === 0, errors };",
                _ => "}",
            });
        }

        return sb.ToString();
    }

    private static string FakeTestOutput()
    {
        var sb = new StringBuilder("> npm test -- SignupForm\n\n PASS  src/SignupForm.test.tsx\n");
        for (var i = 1; i <= 14; i++)
        {
            sb.AppendLine($"  ✓ validates signup scenario {i} with mixed inputs and asserts the rendered error state ({i * 3} ms)");
        }

        sb.AppendLine("\nTest Suites: 1 passed, 1 total\nTests:       14 passed, 14 total\nTime:        2.81 s");
        return sb.ToString();
    }

    private static string FakeGrep()
    {
        var sb = new StringBuilder();
        string[] files = ["LoginForm.tsx", "ProfileForm.tsx", "CheckoutForm.tsx", "SignupForm.tsx", "SearchBar.tsx"];
        for (var i = 0; i < 40; i++)
        {
            sb.AppendLine($"src/{files[i % files.Length]}:{10 + i}: const onSubmit = (e: FormEvent) => {{ if (!values.field{i}) setError('required'); }}");
        }

        return sb.ToString();
    }

    private sealed record ChatSpec(string Trace, string? Parent, string Session, string Model, double Start, double End,
        long InputTokens, long OutputTokens, long CacheRead, long CacheWrite)
    {
        public IEnumerable<JsonObject>? Messages { get; init; }
        public string? System { get; init; }
        public string? UserRequest { get; init; }
        public JsonObject[] Reply { get; init; } = [];
        public string Finish { get; init; } = "tool_calls";
        public string? AgentName { get; init; }
        public string? ParentSession { get; init; }
    }

    private sealed record ToolSpec(string Trace, string Parent, string Session, string Name, string CallId, double Start, double End, string Args, string Result)
    {
        public string? ParentSession { get; init; }
    }

    private sealed record AgentSpec(string Trace, string SpanId, string? Parent, string Session, string Name, double Start, double End,
        string Prompt, long TotalInput, long TotalOutput, string[] Tools)
    {
        public string? ParentSession { get; init; }
    }

    private sealed class PayloadBuilder(DateTimeOffset origin)
    {
        private readonly JsonArray _spans = [];

        public static string NewTraceId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        public static string NewSpanId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

        public void Chat(ChatSpec c)
        {
            var attrs = new List<(string, object)>
            {
                ("gen_ai.operation.name", "chat"),
                ("gen_ai.provider.name", "github"),
                ("gen_ai.request.model", c.Model),
                ("gen_ai.response.model", c.Model),
                ("gen_ai.conversation.id", Guid.NewGuid().ToString()),
                ("gen_ai.request.max_tokens", 16_000L),
                ("gen_ai.usage.input_tokens", c.InputTokens),
                ("gen_ai.usage.output_tokens", c.OutputTokens),
                ("gen_ai.usage.cache_read.input_tokens", c.CacheRead),
                ("gen_ai.usage.cache_creation.input_tokens", c.CacheWrite),
                ("gen_ai.response.finish_reasons", new[] { c.Finish }),
                ("copilot_chat.request.max_prompt_tokens", 128_000L),
                ("copilot_chat.time_to_first_token", Math.Round((c.End - c.Start) * 180)),
                ("copilot_chat.chat_session_id", c.Session),
                ("gen_ai.agent.name", c.AgentName ?? "panel/editAgent"),
                ("gen_ai.output.messages", new JsonArray(new JsonObject
                {
                    ["role"] = "assistant",
                    ["parts"] = new JsonArray(c.Reply.Select(p => (JsonNode)p.DeepClone()).ToArray()),
                    ["finish_reason"] = c.Finish,
                }).ToJsonString()),
            };
            if (c.ParentSession is not null) attrs.Add(("copilot_chat.parent_chat_session_id", c.ParentSession));
            if (c.Messages is not null) attrs.Add(("gen_ai.input.messages", new JsonArray(c.Messages.Select(m => (JsonNode)m.DeepClone()).ToArray()).ToJsonString()));
            if (c.System is not null) attrs.Add(("gen_ai.system_instructions", c.System));
            if (c.UserRequest is not null) attrs.Add(("copilot_chat.user_request", c.UserRequest));

            Add(c.Trace, NewSpanId(), c.Parent, $"chat {c.Model}", 3, c.Start, c.End, attrs);
        }

        public string Tool(ToolSpec t)
        {
            var id = NewSpanId();
            var attrs = new List<(string, object)>
            {
                ("gen_ai.operation.name", "execute_tool"),
                ("gen_ai.tool.name", t.Name),
                ("gen_ai.tool.type", "function"),
                ("gen_ai.tool.call.id", t.CallId),
                ("gen_ai.tool.call.arguments", t.Args),
                ("gen_ai.tool.call.result", t.Result),
                ("copilot_chat.chat_session_id", t.Session),
            };
            if (t.ParentSession is not null) attrs.Add(("copilot_chat.parent_chat_session_id", t.ParentSession));
            Add(t.Trace, id, t.Parent, $"execute_tool {t.Name}", 1, t.Start, t.End, attrs);
            return id;
        }

        public void Hook(string trace, string parent, string session, string hookType, double start, double end, string decision) =>
            Add(trace, NewSpanId(), parent, $"execute_hook {hookType}", 1, start, end,
            [
                ("gen_ai.operation.name", "execute_hook"),
                ("copilot_chat.hook_type", hookType),
                ("github.copilot.hook.decision", decision),
                ("copilot_chat.chat_session_id", session),
            ]);

        public void Agent(AgentSpec a)
        {
            var attrs = new List<(string, object)>
            {
                ("gen_ai.operation.name", "invoke_agent"),
                ("gen_ai.provider.name", "github"),
                ("gen_ai.agent.name", a.Name),
                ("gen_ai.conversation.id", a.Session),
                ("copilot_chat.chat_session_id", a.Session),
                ("gen_ai.usage.input_tokens", a.TotalInput),
                ("gen_ai.usage.output_tokens", a.TotalOutput),
                ("gen_ai.input.messages", new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(Text(a.Prompt)) }).ToJsonString()),
                ("gen_ai.tool.definitions", new JsonArray(a.Tools.Select(t => (JsonNode)new JsonObject { ["type"] = "function", ["name"] = t }).ToArray()).ToJsonString()),
            };
            if (a.ParentSession is not null) attrs.Add(("copilot_chat.parent_chat_session_id", a.ParentSession));
            Add(a.Trace, a.SpanId, a.Parent, $"invoke_agent {a.Name}", 1, a.Start, a.End, attrs);
        }

        public string Build()
        {
            var payload = new JsonObject
            {
                ["resourceSpans"] = new JsonArray(new JsonObject
                {
                    ["resource"] = new JsonObject
                    {
                        ["attributes"] = Attributes([("service.name", DemoClientProfile.ServiceName), ("service.version", "demo"), ("session.id", "demo-window")]),
                    },
                    ["scopeSpans"] = new JsonArray(new JsonObject
                    {
                        ["scope"] = new JsonObject { ["name"] = "copilot-chat" },
                        ["spans"] = _spans,
                    }),
                }),
            };
            return payload.ToJsonString();
        }

        private void Add(string trace, string spanId, string? parent, string name, int kind, double start, double end, List<(string, object)> attrs)
        {
            _spans.Add(new JsonObject
            {
                ["traceId"] = trace,
                ["spanId"] = spanId,
                ["parentSpanId"] = parent ?? string.Empty,
                ["name"] = name,
                ["kind"] = kind,
                ["startTimeUnixNano"] = Nanos(start),
                ["endTimeUnixNano"] = Nanos(end),
                ["attributes"] = Attributes(attrs),
                ["status"] = new JsonObject { ["code"] = 1 },
            });
        }

        private string Nanos(double seconds) =>
            ((origin.AddSeconds(seconds) - DateTimeOffset.UnixEpoch).Ticks * 100).ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static JsonArray Attributes(IEnumerable<(string Key, object Value)> attrs) =>
            new(attrs.Select(a => (JsonNode)new JsonObject { ["key"] = a.Key, ["value"] = AnyValue(a.Value) }).ToArray());

        private static JsonObject AnyValue(object value) => value switch
        {
            string s => new JsonObject { ["stringValue"] = s },
            long l => new JsonObject { ["intValue"] = l.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            double d => new JsonObject { ["doubleValue"] = d },
            bool b => new JsonObject { ["boolValue"] = b },
            string[] arr => new JsonObject { ["arrayValue"] = new JsonObject { ["values"] = new JsonArray(arr.Select(x => (JsonNode)new JsonObject { ["stringValue"] = x }).ToArray()) } },
            _ => new JsonObject { ["stringValue"] = value.ToString() },
        };
    }
}

