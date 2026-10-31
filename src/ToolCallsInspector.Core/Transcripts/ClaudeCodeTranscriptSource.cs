using System.Collections.Concurrent;
using System.Text.Json;

namespace ToolCallsInspector.Core.Transcripts;

/// <summary>
/// Reads Claude Code session transcripts (<c>~/.claude/projects/{project}/{sessionId}.jsonl</c>). The session id equals
/// the spans' <c>session.id</c>; assistant lines carry the API <c>requestId</c>, which equals the <c>claude_code.llm_request</c>
/// span's <c>request_id</c> / <c>gen_ai.response.id</c>. Lines form a tree through <c>parentUuid</c>; the prompt of a call is
/// the chain of lines leading up to its first assistant line, so rewound or abandoned branches are left out.
/// </summary>
public sealed class ClaudeCodeTranscriptSource(string? projectsRoot) : LocalTranscriptSource(projectsRoot)
{
    private static readonly TimeSpan MissRetry = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, string> _found = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _missed = new(StringComparer.OrdinalIgnoreCase);

    public override string Name => "Claude Code transcript";

    public static string DefaultRoot() => Path.Combine(ClaudeHome(), "projects");

    /// <summary><c>CLAUDE_CONFIG_DIR</c>, or <c>~/.claude</c>.</summary>
    public static string ClaudeHome()
    {
        var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : home;
    }

    protected override string? FindLog(string root, string sessionId)
    {
        if (_found.TryGetValue(sessionId, out var known) && File.Exists(known))
        {
            return known;
        }

        // Transcripts are grouped per project folder; scanning is cheap, but don't repeat it for every span of a session without one.
        if (_missed.TryGetValue(sessionId, out var missedAt) && Environment.TickCount64 - missedAt < MissRetry.TotalMilliseconds)
        {
            return null;
        }

        if (Directory.Exists(root))
        {
            foreach (var project in Directory.EnumerateDirectories(root))
            {
                var candidate = Path.Combine(project, sessionId + ".jsonl");
                if (File.Exists(candidate))
                {
                    _missed.TryRemove(sessionId, out _);
                    return _found[sessionId] = candidate;
                }
            }
        }

        _missed[sessionId] = Environment.TickCount64;
        return null;
    }

    private protected override JsonlTranscript Open(string path) => new ClaudeTranscript(path);
}

internal sealed class ClaudeTranscript(string path) : JsonlTranscript(path)
{
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);

    protected override bool HasCall(string responseId) => _requests.ContainsKey(responseId);

    protected override TranscriptPrompt? BuildPrompt(string responseId)
    {
        if (!_requests.TryGetValue(responseId, out var uuid))
        {
            return null;
        }

        var chain = new List<Node>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var parent = _nodes[uuid].Parent; parent is not null && seen.Add(parent) && _nodes.TryGetValue(parent, out var node); parent = node.Parent)
        {
            chain.Add(node);
        }

        chain.Reverse();
        var messages = new List<TranscriptMessage>();
        string? lastAssistantId = null;
        foreach (var node in chain)
        {
            foreach (var message in node.Messages)
            {
                // Streamed responses are written as one line per content block; the API saw them as one message.
                if (message.Role == "assistant" && node.MessageId is not null && node.MessageId == lastAssistantId && messages.Count > 0)
                {
                    messages[^1] = messages[^1] with { Parts = [.. messages[^1].Parts, .. message.Parts] };
                    continue;
                }

                messages.Add(message);
                lastAssistantId = message.Role == "assistant" ? node.MessageId : null;
            }
        }

        return new TranscriptPrompt(Serialize(messages), null, messages.Count, Path);
    }

    protected override void OnReset()
    {
        _nodes.Clear();
        _requests.Clear();
        _toolNames.Clear();
    }

    protected override void Apply(JsonElement root, LineRef line)
    {
        var message = Obj(root, "message");
        var requestId = Str(root, "requestId");
        var ids = new List<string?> { requestId, Str(root, "toolUseID"), Str(root, "sourceToolUseID") };
        var messages = new List<TranscriptMessage>();

        if (message is { } m)
        {
            ids.Add(Str(m, "id"));
            switch (line.Type)
            {
                case "user":
                    AnthropicContent.ReadUser(m, messages, _toolNames, ids);
                    break;

                // Synthetic messages (API errors, interruptions) are written locally and never sent to the model.
                case "assistant" when Str(m, "model") != "<synthetic>" && !True(root, "isApiErrorMessage"):
                    AnthropicContent.ReadAssistant(m, messages, _toolNames, ids);
                    break;
            }
        }

        Index(line, [.. ids]);

        if (Str(root, "uuid") is not { } uuid)
        {
            return;
        }

        _nodes[uuid] = new Node(Str(root, "parentUuid"), message is { } msg ? Str(msg, "id") : null, messages);
        if (line.Type == "assistant" && requestId is not null)
        {
            _requests.TryAdd(requestId, uuid);
        }
    }

    /// <summary>One transcript line: its parent in the conversation tree and the messages it adds.</summary>
    private sealed record Node(string? Parent, string? MessageId, List<TranscriptMessage> Messages);
}
