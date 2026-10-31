using System.Text;
using System.Text.Json;

namespace ToolCallsInspector.Core.Transcripts;

/// <summary>The complete prompt sent for one LLM call, rebuilt from a client's local session log.</summary>
/// <param name="InputMessagesJson">Messages in the OTel GenAI <c>gen_ai.input.messages</c> format.</param>
public sealed record TranscriptPrompt(string InputMessagesJson, string? SystemPrompt, int MessageCount, string SourcePath);

/// <summary>One raw line of a session log.</summary>
/// <param name="LineNumber">1-based line number in the log file.</param>
public sealed record TranscriptEvent(int LineNumber, string? Type, string Json);

/// <summary>The raw session-log lines that belong to one LLM call or tool call.</summary>
public sealed record TranscriptEvents(string SourcePath, IReadOnlyList<TranscriptEvent> Events);

public interface ITranscriptSource
{
    /// <summary>Full prompt for the call with <paramref name="responseId"/>, or null when no local transcript has it.</summary>
    TranscriptPrompt? TryGetPrompt(string conversationId, string responseId);

    /// <summary>
    /// Raw log lines that reference <paramref name="callId"/> (an LLM call's response id or a tool call id),
    /// or null when no local transcript exists for the conversation.
    /// </summary>
    TranscriptEvents? TryGetEvents(string conversationId, string callId);
}

/// <summary>
/// Reads Copilot CLI / Copilot app session logs (<c>~/.copilot/session-state/{sessionId}/events.jsonl</c>).
/// OTel telemetry only carries the messages added during the current turn; the session log holds the whole
/// conversation, so the exact history before each call can be replayed. Calls are matched on
/// <c>apiCallId</c>, which equals the span's <c>gen_ai.response.id</c>; the session id equals <c>gen_ai.conversation.id</c>.
/// </summary>
public sealed class CopilotSessionTranscriptSource(string? sessionStateRoot) : ITranscriptSource
{
    private const int MaxCachedTranscripts = 16;
    private readonly Dictionary<string, CopilotTranscript> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string? Root { get; } = string.IsNullOrWhiteSpace(sessionStateRoot) ? null : sessionStateRoot;

    public static string DefaultRoot()
    {
        var home = Environment.GetEnvironmentVariable("COPILOT_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
        }

        return Path.Combine(home, "session-state");
    }

    public TranscriptPrompt? TryGetPrompt(string conversationId, string responseId) =>
        string.IsNullOrEmpty(responseId) ? null : Read(conversationId, t => t.TryGetPrompt(responseId));

    public TranscriptEvents? TryGetEvents(string conversationId, string callId) =>
        string.IsNullOrEmpty(callId) ? null : Read(conversationId, t => t.GetEvents(callId));

    /// <summary>True when a session log exists for the conversation.</summary>
    public bool HasTranscript(string? conversationId) => PathFor(conversationId) is { } path && File.Exists(path);

    private string? PathFor(string? conversationId) =>
        // The conversation id comes from untrusted telemetry: only a GUID may become part of a path.
        Root is not null && Guid.TryParse(conversationId, out var sessionId)
            ? Path.Combine(Root, sessionId.ToString("D"), "events.jsonl")
            : null;

    private T? Read<T>(string conversationId, Func<CopilotTranscript, T?> read)
        where T : class
    {
        if (PathFor(conversationId) is not { } path || !File.Exists(path))
        {
            return null;
        }

        CopilotTranscript transcript;
        lock (_cache)
        {
            if (!_cache.TryGetValue(path, out transcript!))
            {
                if (_cache.Count >= MaxCachedTranscripts)
                {
                    _cache.Remove(_cache.MinBy(kv => kv.Value.LastAccess).Key);
                }

                _cache[path] = transcript = new CopilotTranscript(path);
            }
        }

        try
        {
            return read(transcript);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Incrementally replays one session log; only bytes appended since the last read are parsed.</summary>
internal sealed class CopilotTranscript(string path)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Snapshot> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<LineRef>> _lines = new(StringComparer.Ordinal);
    private List<TranscriptMessage> _history = [];
    private string? _system;
    private long _offset;
    private int _lineCount;

    public long LastAccess { get; private set; }

    public TranscriptEvents GetEvents(string callId)
    {
        List<LineRef> refs;
        lock (_gate)
        {
            LastAccess = Environment.TickCount64;
            CatchUp();
            refs = _lines.TryGetValue(callId, out var found) ? [.. found] : [];
        }

        var events = new List<TranscriptEvent>(refs.Count);
        if (refs.Count > 0)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (var line in refs)
            {
                if (line.Offset + line.Length > stream.Length)
                {
                    continue;
                }

                var buffer = new byte[line.Length];
                stream.Seek(line.Offset, SeekOrigin.Begin);
                stream.ReadExactly(buffer);
                events.Add(new TranscriptEvent(line.Number, line.Type, Encoding.UTF8.GetString(buffer)));
            }
        }

        return new TranscriptEvents(path, events);
    }

    public TranscriptPrompt? TryGetPrompt(string responseId)
    {
        lock (_gate)
        {
            LastAccess = Environment.TickCount64;
            if (!_calls.ContainsKey(responseId))
            {
                CatchUp();
            }

            return _calls.TryGetValue(responseId, out var snapshot)
                ? new TranscriptPrompt(Serialize(snapshot.History, snapshot.Count), snapshot.System, snapshot.Count, path)
                : null;
        }
    }

    private void CatchUp()
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _offset)
        {
            Reset();
        }

        if (stream.Length == _offset)
        {
            return;
        }

        stream.Seek(_offset, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - _offset];
        stream.ReadExactly(buffer);

        // Only consume complete lines; the writer may be mid-line.
        var end = Array.LastIndexOf(buffer, (byte)'\n');
        if (end < 0)
        {
            return;
        }

        var start = 0;
        while (start <= end)
        {
            var newline = Array.IndexOf(buffer, (byte)'\n', start, end - start + 1);
            _lineCount++;
            if (newline > start)
            {
                // Trim a trailing CR so offsets cover exactly the JSON text.
                var length = buffer[newline - 1] == (byte)'\r' ? newline - start - 1 : newline - start;
                Apply(buffer.AsMemory(start, length), _offset + start, _lineCount);
            }

            start = newline + 1;
        }

        _offset += end + 1;
    }

    private void Reset()
    {
        _calls.Clear();
        _toolNames.Clear();
        _lines.Clear();
        _history = [];
        _system = null;
        _offset = 0;
        _lineCount = 0;
    }

    private void Apply(ReadOnlyMemory<byte> line, long offset, int number)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            Index(data, Str(root, "type"), offset, line.Length, number);

            // Defensive: events of a nested agent never belong to the parent conversation's prompt.
            if (Str(data, "parentToolCallId") is { Length: > 0 })
            {
                return;
            }

            switch (Str(root, "type"))
            {
                case "system.message":
                    _system = Str(data, "content") ?? _system;
                    break;

                case "user.message":
                    AddText("user", Str(data, "transformedContent") ?? Str(data, "content"));
                    break;

                case "system.notification":
                    AddText("user", Str(data, "content"));
                    break;

                case "assistant.message":
                    OnAssistant(data);
                    break;

                case "tool.execution_complete":
                    OnToolResult(data);
                    break;

                case "session.compaction_complete":
                    OnCompaction(data);
                    break;
            }
        }
    }

    /// <summary>Remembers where lines about an LLM call (<c>apiCallId</c>) or tool call (<c>toolCallId</c>) are, to show them raw later.</summary>
    private void Index(JsonElement data, string? type, long offset, int length, int number)
    {
        var usageCallId = data.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object ? Str(usage, "apiCallId") : null;
        string?[] ids = [Str(data, "apiCallId"), Str(data, "toolCallId"), usageCallId];
        for (var i = 0; i < ids.Length; i++)
        {
            if (ids[i] is not { Length: > 0 } id || Array.IndexOf(ids, id) < i)
            {
                continue;
            }

            if (!_lines.TryGetValue(id, out var refs))
            {
                _lines[id] = refs = [];
            }

            refs.Add(new LineRef(offset, length, number, type));
        }
    }

    private void OnAssistant(JsonElement data)
    {
        // The history at this point is exactly what the call that produced this message was sent.
        if (Str(data, "apiCallId") is { Length: > 0 } apiCallId && !_calls.ContainsKey(apiCallId))
        {
            _calls[apiCallId] = new Snapshot(_history, _history.Count, _system);
        }

        var parts = new List<TranscriptPart>();
        if (Str(data, "content") is { Length: > 0 } text)
        {
            parts.Add(new TranscriptPart("text", text));
        }

        if (data.TryGetProperty("toolRequests", out var requests) && requests.ValueKind == JsonValueKind.Array)
        {
            foreach (var request in requests.EnumerateArray())
            {
                var id = Str(request, "toolCallId");
                var name = Str(request, "name");
                if (id is not null && name is not null)
                {
                    _toolNames[id] = name;
                }

                var arguments = request.TryGetProperty("arguments", out var a)
                    ? a.ValueKind == JsonValueKind.String ? a.GetString() : a.GetRawText()
                    : null;
                parts.Add(new TranscriptPart("tool_call", arguments, id, name));
            }
        }

        if (parts.Count > 0)
        {
            _history.Add(new TranscriptMessage("assistant", parts));
        }
    }

    private void OnToolResult(JsonElement data)
    {
        // Only results of tools requested in this conversation are part of its prompt.
        if (Str(data, "toolCallId") is not { } id || !_toolNames.TryGetValue(id, out var name))
        {
            return;
        }

        var success = data.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        var response = success
            ? data.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object ? Str(result, "content") : null
            : data.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? Str(error, "message") : null;
        _history.Add(new TranscriptMessage("tool", [new TranscriptPart("tool_call_response", response ?? string.Empty, id, name)]));
    }

    private void OnCompaction(JsonElement data)
    {
        if (!(data.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True) || Str(data, "summaryContent") is not { Length: > 0 } summary)
        {
            return;
        }

        // Newer logs report how many leading messages the summary replaced; the rest are kept verbatim.
        var kept = Int(data, "preCompactionMessagesLength") is { } before && Int(data, "messagesRemoved") is { } removed
            ? Math.Clamp(before - removed, 0, _history.Count)
            : 0;

        // A new list keeps earlier snapshots (which reference the old list by prefix length) intact.
        _history = [new TranscriptMessage("user", [new TranscriptPart("text", summary)]), .. _history.TakeLast(kept)];
    }

    private void AddText(string role, string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _history.Add(new TranscriptMessage(role, [new TranscriptPart("text", text)]));
        }
    }

    private static string Serialize(List<TranscriptMessage> history, int count)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (var i = 0; i < count; i++)
            {
                var message = history[i];
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                writer.WriteStartArray("parts");
                foreach (var part in message.Parts)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", part.Type);
                    if (part.Id is not null) writer.WriteString("id", part.Id);
                    if (part.Name is not null) writer.WriteString("name", part.Name);
                    writer.WriteString(part.Type switch { "tool_call" => "arguments", "tool_call_response" => "response", _ => "content" }, part.Content ?? string.Empty);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;

    private sealed record Snapshot(List<TranscriptMessage> History, int Count, string? System);

    private readonly record struct LineRef(long Offset, int Length, int Number, string? Type);

    private sealed record TranscriptMessage(string Role, List<TranscriptPart> Parts);

    private sealed record TranscriptPart(string Type, string? Content, string? Id = null, string? Name = null);
}
