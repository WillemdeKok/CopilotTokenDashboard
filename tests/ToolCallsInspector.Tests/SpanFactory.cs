using System.Text.Json.Nodes;
using ToolCallsInspector.Core.Analysis;
using ToolCallsInspector.Core.Clients;
using ToolCallsInspector.Core.Normalization;
using ToolCallsInspector.Core.Storage;
using ToolCallsInspector.Core.Telemetry;

namespace ToolCallsInspector.Tests;

/// <summary>Builds VS Code–shaped spans and runs them through the normalizer and analyzer.</summary>
internal sealed class SpanFactory
{
    private static readonly DateTimeOffset Origin = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly List<RawSpan> _spans = [];
    private int _counter;

    public string TraceId { get; } = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
    public string Session { get; init; } = "session-1";

    public IReadOnlyList<RawSpan> Spans => _spans;

    public string Agent(double start, double end, string? parent = null, string name = "GitHub Copilot Chat", string? session = null, string? parentSession = null, string? prompt = null)
    {
        var attrs = new Dictionary<string, object?>
        {
            [GenAiKeys.OperationName] = "invoke_agent",
            [GenAiKeys.AgentName] = name,
            [GenAiKeys.ConversationId] = session ?? Session,
            [GenAiKeys.ChatSessionId] = session ?? Session,
        };
        if (parentSession is not null) attrs[GenAiKeys.ParentChatSessionId] = parentSession;
        if (prompt is not null) attrs[GenAiKeys.InputMessages] = Messages(Msg("user", Text(prompt)));
        return Add($"invoke_agent {name}", parent, start, end, attrs);
    }

    public string Chat(double start, double end, long input, long output, string? parent, string? inputMessages = null,
        string? system = null, string? userRequest = null, long reasoning = 0, string? session = null, string? parentSession = null,
        string? outputMessages = null)
    {
        var attrs = new Dictionary<string, object?>
        {
            [GenAiKeys.OperationName] = "chat",
            [GenAiKeys.RequestModel] = "gpt-test",
            [GenAiKeys.ConversationId] = Guid.NewGuid().ToString(),
            [GenAiKeys.InputTokens] = input,
            [GenAiKeys.OutputTokens] = output,
            [GenAiKeys.ReasoningTokens] = reasoning,
            [GenAiKeys.ChatSessionId] = session ?? Session,
            [GenAiKeys.InputMessages] = inputMessages,
            [GenAiKeys.OutputMessages] = outputMessages,
            [GenAiKeys.SystemInstructions] = system,
            [GenAiKeys.UserRequest] = userRequest,
        };
        if (parentSession is not null) attrs[GenAiKeys.ParentChatSessionId] = parentSession;
        return Add("chat gpt-test", parent, start, end, attrs);
    }

    public string Tool(double start, double end, string name, string callId, string? parent, string? result = null, string? session = null, long? exitCode = null)
    {
        var attrs = new Dictionary<string, object?>
        {
            [GenAiKeys.OperationName] = "execute_tool",
            [GenAiKeys.ToolName] = name,
            [GenAiKeys.ToolCallId] = callId,
            [GenAiKeys.ToolCallArguments] = "{}",
            [GenAiKeys.ToolCallResult] = result,
            [GenAiKeys.ChatSessionId] = session ?? Session,
        };
        if (exitCode is not null) attrs[GenAiKeys.ProcessExitCode] = exitCode;
        return Add($"execute_tool {name}", parent, start, end, attrs);
    }

    public string Custom(string name, string? parent, double start, double end, Dictionary<string, object?> attrs) => Add(name, parent, start, end, attrs);

    private string Add(string name, string? parent, double start, double end, Dictionary<string, object?> attrs)
    {
        var spanId = (++_counter).ToString("x16");
        _spans.Add(new RawSpan
        {
            TraceId = TraceId,
            SpanId = spanId,
            ParentSpanId = parent,
            Name = name,
            StartTime = Origin.AddSeconds(start),
            EndTime = Origin.AddSeconds(end),
            Attributes = attrs.Where(kv => kv.Value is not null).ToDictionary(),
            ResourceAttributes = new Dictionary<string, object?> { ["service.name"] = "copilot-chat" },
        });
        return spanId;
    }

    public TraceAnalysis Analyze() => Analyze(_spans).Single();

    public static IReadOnlyList<TraceAnalysis> Analyze(IEnumerable<RawSpan> spans)
    {
        var normalizer = new SpanNormalizer(new ClientCatalog([new VsCodeCopilotChatProfile(), new CopilotCliProfile()]));
        var normalized = spans.Select(s => normalizer.Normalize(s, "test")).ToList();
        var content = normalized.ToDictionary(n => n.Record.Id, n => n.Content);
        var analyzer = new TraceAnalyzer(new TokenAttributionEngine(new TiktokenTokenCounter()));
        return normalized.Select(n => n.Record).GroupBy(r => r.TraceId)
            .Select(g => analyzer.Analyze(g.Key, g.ToList(), content, TraceAnalyzer.ResolveSessionKey(g.Key, g)))
            .ToList();
    }

    public static string Messages(params JsonObject[] messages) => new JsonArray(messages.Select(m => (JsonNode)m).ToArray()).ToJsonString();

    public static JsonObject Msg(string role, params JsonObject[] parts) =>
        new() { ["role"] = role, ["parts"] = new JsonArray(parts.Select(p => (JsonNode)p).ToArray()) };

    public static JsonObject Text(string content) => new() { ["type"] = "text", ["content"] = content };

    public static JsonObject ToolCall(string id, string name) =>
        new() { ["type"] = "tool_call", ["id"] = id, ["name"] = name, ["arguments"] = new JsonObject() };

    public static JsonObject ToolResponse(string id, string response) =>
        new() { ["type"] = "tool_call_response", ["id"] = id, ["response"] = response };

    public static string Words(int count, string word = "lorem") => string.Join(' ', Enumerable.Repeat(word, count));
}

internal static class NodeExtensions
{
    public static IEnumerable<SpanNode> All(this TraceAnalysis trace) => trace.Roots.Concat(trace.Roots.SelectMany(r => r.Descendants()));

    public static SpanNode Single(this TraceAnalysis trace, SpanCategory category, Func<SpanNode, bool>? predicate = null) =>
        trace.All().Single(n => n.Category == category && (predicate?.Invoke(n) ?? true));
}
