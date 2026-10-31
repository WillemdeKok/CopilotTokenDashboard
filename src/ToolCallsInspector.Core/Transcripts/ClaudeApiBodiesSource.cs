using System.Text.Json;

namespace ToolCallsInspector.Core.Transcripts;

/// <summary>
/// Reads the exact Messages API requests Claude Code writes with <c>OTEL_LOG_RAW_API_BODIES=file:&lt;dir&gt;</c>.
/// Each successful response appends a line to <c>&lt;dir&gt;/index.jsonl</c> (Claude Code v2.1.274+) with the
/// <c>session_id</c>, the <c>request_id</c> (equal to the <c>claude_code.llm_request</c> span's <c>request_id</c>) and
/// the request and response files. Unlike the transcript, a request body includes the system prompt and tool schemas,
/// so the prompt is complete. Register it before <see cref="ClaudeCodeTranscriptSource"/>, which remains the fallback.
/// </summary>
public sealed class ClaudeApiBodiesSource : ITranscriptSource
{
    public const string IndexFile = "index.jsonl";
    private static readonly TimeSpan IndexRetry = TimeSpan.FromSeconds(2);
    private readonly string? _root;
    private readonly object _gate = new();
    private ApiBodiesIndex? _index;

    public ClaudeApiBodiesSource(string? root) =>
        _root = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);

    public string Name => "Claude Code API request log";

    /// <summary>The folder to pass to <c>OTEL_LOG_RAW_API_BODIES=file:</c>, or null when disabled.</summary>
    public string? Root => _root;

    /// <summary>Request bodies are not line-oriented; the Raw log view uses the Claude Code transcript instead.</summary>
    public bool ProvidesEvents => false;

    public static string DefaultRoot() => Path.Combine(ClaudeCodeTranscriptSource.ClaudeHome(), "api-bodies");

    public bool HasTranscript(string? conversationId)
    {
        try
        {
            return !string.IsNullOrEmpty(conversationId) && Index()?.HasSession(conversationId, IndexRetry) == true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public TranscriptPrompt? TryGetPrompt(string conversationId, string responseId)
    {
        try
        {
            return string.IsNullOrEmpty(responseId) ? null : Index()?.TryGetPrompt(responseId) is { } p ? p with { SourceName = Name } : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public TranscriptEvents? TryGetEvents(string conversationId, string callId) => null;

    private ApiBodiesIndex? Index()
    {
        if (_index is not null || _root is null)
        {
            return _index;
        }

        var path = Path.Combine(_root, IndexFile);
        lock (_gate)
        {
            return _index ??= File.Exists(path) ? new ApiBodiesIndex(path, _root) : null;
        }
    }
}

internal sealed class ApiBodiesIndex(string path, string root) : JsonlTranscript(path)
{
    private const int MaxCachedPrompts = 32;
    private readonly HashSet<string> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (TranscriptPrompt? Prompt, long LastUse)> _prompts = new(StringComparer.Ordinal);
    private readonly string _root = root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? root : root + System.IO.Path.DirectorySeparatorChar;
    private long? _lastRefresh;

    /// <summary>True when the index has a request of the session. Re-reads the index at most once per <paramref name="retry"/> for unknown sessions.</summary>
    public bool HasSession(string sessionId, TimeSpan retry)
    {
        lock (_sessions)
        {
            if (_sessions.Contains(sessionId) || Environment.TickCount64 - _lastRefresh < retry.TotalMilliseconds)
            {
                return _sessions.Contains(sessionId);
            }
        }

        return Query(() =>
        {
            lock (_sessions)
            {
                _lastRefresh = Environment.TickCount64;
                return _sessions.Contains(sessionId);
            }
        });
    }

    protected override bool HasCall(string responseId) => _entries.ContainsKey(responseId);

    protected override TranscriptPrompt? BuildPrompt(string responseId)
    {
        if (!_entries.TryGetValue(responseId, out var entry))
        {
            return null;
        }

        if (!_prompts.TryGetValue(responseId, out var cached))
        {
            if (_prompts.Count >= MaxCachedPrompts)
            {
                _prompts.Remove(_prompts.MinBy(kv => kv.Value.LastUse).Key);
            }

            cached = (Load(entry), 0);
        }

        _prompts[responseId] = cached with { LastUse = Environment.TickCount64 };
        return cached.Prompt;
    }

    protected override void OnReset()
    {
        lock (_sessions)
        {
            _sessions.Clear();
        }

        _entries.Clear();
        _prompts.Clear();
    }

    protected override void Apply(JsonElement root, LineRef line)
    {
        if (Str(root, "request_id") is not { } requestId || Str(root, "request_file") is not { } requestFile)
        {
            return;
        }

        if (Str(root, "session_id") is { } sessionId)
        {
            lock (_sessions)
            {
                _sessions.Add(sessionId);
            }
        }

        _entries[requestId] = new Entry(requestFile, Str(root, "response_file"));
    }

    private TranscriptPrompt? Load(Entry entry)
    {
        if (Resolve(entry.RequestFile) is not { } requestPath)
        {
            return null;
        }

        using var request = Parse(requestPath);
        if (request?.RootElement is not { ValueKind: JsonValueKind.Object } body)
        {
            return null;
        }

        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var messages = new List<TranscriptMessage>();
        if (body.TryGetProperty("messages", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in list.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (Str(message, "role") == "assistant")
                {
                    AnthropicContent.ReadAssistant(message, messages, toolNames);
                }
                else
                {
                    AnthropicContent.ReadUser(message, messages, toolNames);
                }
            }
        }

        var system = body.TryGetProperty("system", out var s) ? AnthropicContent.SystemText(s) : null;
        var tools = body.TryGetProperty("tools", out var t) && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0 ? t.GetRawText() : null;

        return new TranscriptPrompt(Serialize(messages), system, messages.Count, requestPath, ToolDefinitionsJson: tools, OutputMessagesJson: LoadResponse(entry, toolNames));
    }

    private string? LoadResponse(Entry entry, Dictionary<string, string> toolNames)
    {
        if (entry.ResponseFile is null || Resolve(entry.ResponseFile) is not { } path)
        {
            return null;
        }

        using var response = Parse(path);
        if (response?.RootElement is not { ValueKind: JsonValueKind.Object } body)
        {
            return null;
        }

        var reply = new List<TranscriptMessage>();
        AnthropicContent.ReadAssistant(body, reply, toolNames);
        return reply.Count > 0 ? Serialize(reply) : null;
    }

    /// <summary>The body file named by the index, only when it lies inside the configured folder.</summary>
    private string? Resolve(string file)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(_root, file));
        return full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    private static JsonDocument? Parse(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonDocument.Parse(stream);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Entry(string RequestFile, string? ResponseFile);
}
