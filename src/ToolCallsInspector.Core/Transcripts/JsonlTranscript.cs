using System.Text;
using System.Text.Json;

namespace ToolCallsInspector.Core.Transcripts;

/// <summary>
/// Base for sources that keep one JSONL log per session under a local folder, named by the session GUID
/// (which equals the span's conversation id). Parsed logs are cached and tailed incrementally.
/// </summary>
public abstract class LocalTranscriptSource : ITranscriptSource
{
    private const int MaxCachedTranscripts = 16;
    private readonly Dictionary<string, JsonlTranscript> _cache = new(StringComparer.OrdinalIgnoreCase);

    protected LocalTranscriptSource(string? root) => Root = string.IsNullOrWhiteSpace(root) ? null : root;

    public string? Root { get; }

    public abstract string Name { get; }

    public TranscriptPrompt? TryGetPrompt(string conversationId, string responseId) =>
        string.IsNullOrEmpty(responseId) ? null : Read(conversationId, t => t.TryGetPrompt(responseId) is { } p ? p with { SourceName = Name } : null);

    public TranscriptEvents? TryGetEvents(string conversationId, string callId) =>
        string.IsNullOrEmpty(callId) ? null : Read(conversationId, t => t.GetEvents(callId) with { SourceName = Name });

    public bool HasTranscript(string? conversationId) => PathFor(conversationId) is not null;

    /// <summary>Path of the log for <paramref name="sessionId"/> (a validated, lower-case GUID), or null when there is none.</summary>
    protected abstract string? FindLog(string root, string sessionId);

    private protected abstract JsonlTranscript Open(string path);

    private string? PathFor(string? conversationId)
    {
        // The conversation id comes from untrusted telemetry: only a GUID may become part of a path.
        if (Root is null || !Guid.TryParse(conversationId, out var sessionId))
        {
            return null;
        }

        try
        {
            return FindLog(Root, sessionId.ToString("D")) is { } path && File.Exists(path) ? path : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private T? Read<T>(string conversationId, Func<JsonlTranscript, T?> read)
        where T : class
    {
        if (PathFor(conversationId) is not { } path)
        {
            return null;
        }

        JsonlTranscript transcript;
        lock (_cache)
        {
            if (!_cache.TryGetValue(path, out transcript!))
            {
                if (_cache.Count >= MaxCachedTranscripts)
                {
                    _cache.Remove(_cache.MinBy(kv => kv.Value.LastAccess).Key);
                }

                _cache[path] = transcript = Open(path);
            }
        }

        try
        {
            return read(transcript);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Incrementally replays one JSONL session log: only bytes appended since the last read are parsed.
/// Subclasses interpret lines; this class tracks where lines about each call are so they can be shown raw.
/// </summary>
internal abstract class JsonlTranscript(string path)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<LineRef>> _lines = new(StringComparer.Ordinal);
    private long _offset;
    private int _lineCount;

    public string Path => path;

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
            if (!HasCall(responseId))
            {
                CatchUp();
            }

            return BuildPrompt(responseId);
        }
    }

    /// <summary>True when the call with <paramref name="responseId"/> has been seen.</summary>
    protected abstract bool HasCall(string responseId);

    /// <summary>Reads lines appended since the last read, then evaluates <paramref name="query"/> under the same lock.</summary>
    protected T Query<T>(Func<T> query)
    {
        lock (_gate)
        {
            LastAccess = Environment.TickCount64;
            CatchUp();
            return query();
        }
    }

    protected abstract TranscriptPrompt? BuildPrompt(string responseId);

    /// <summary>Interprets one parsed line. Call <see cref="Index"/> for every call id the line is about.</summary>
    protected abstract void Apply(JsonElement root, LineRef line);

    /// <summary>The file shrank (rewritten): forget everything derived from it.</summary>
    protected abstract void OnReset();

    /// <summary>Remembers that <paramref name="line"/> is about each of <paramref name="ids"/>, to show it raw later.</summary>
    protected void Index(LineRef line, params ReadOnlySpan<string?> ids)
    {
        for (var i = 0; i < ids.Length; i++)
        {
            if (ids[i] is not { Length: > 0 } id || ids[..i].Contains(id))
            {
                continue;
            }

            if (!_lines.TryGetValue(id, out var refs))
            {
                _lines[id] = refs = [];
            }

            refs.Add(line);
        }
    }

    private void CatchUp()
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _offset)
        {
            _lines.Clear();
            _offset = 0;
            _lineCount = 0;
            OnReset();
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
                Parse(buffer.AsMemory(start, length), _offset + start, _lineCount);
            }

            start = newline + 1;
        }

        _offset += end + 1;
    }

    private void Parse(ReadOnlyMemory<byte> line, long offset, int number)
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
            if (root.ValueKind == JsonValueKind.Object)
            {
                Apply(root, new LineRef(offset, line.Length, number, Str(root, "type")));
            }
        }
    }

    protected static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    protected static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;

    protected static bool True(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    protected static JsonElement? Obj(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    /// <summary>Writes messages in the OTel GenAI <c>gen_ai.input.messages</c> format.</summary>
    protected static string Serialize(IEnumerable<TranscriptMessage> messages)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var message in messages)
            {
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

    internal readonly record struct LineRef(long Offset, int Length, int Number, string? Type);
}

internal sealed record TranscriptMessage(string Role, List<TranscriptPart> Parts);

/// <param name="Type"><c>text</c>, <c>tool_call</c> or <c>tool_call_response</c>.</param>
internal sealed record TranscriptPart(string Type, string? Content, string? Id = null, string? Name = null);
