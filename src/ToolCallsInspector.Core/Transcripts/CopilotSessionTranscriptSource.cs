using System.Text.Json;

namespace ToolCallsInspector.Core.Transcripts;

/// <summary>
/// Reads Copilot CLI / Copilot app session logs (<c>~/.copilot/session-state/{sessionId}/events.jsonl</c>).
/// Calls are matched on <c>apiCallId</c>, which equals the span's <c>gen_ai.response.id</c>; the session id equals
/// <c>gen_ai.conversation.id</c>.
/// </summary>
public sealed class CopilotSessionTranscriptSource(string? sessionStateRoot) : LocalTranscriptSource(sessionStateRoot)
{
    public override string Name => "Copilot session log";

    public static string DefaultRoot()
    {
        var home = Environment.GetEnvironmentVariable("COPILOT_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
        }

        return Path.Combine(home, "session-state");
    }

    protected override string FindLog(string root, string sessionId) => Path.Combine(root, sessionId, "events.jsonl");

    private protected override JsonlTranscript Open(string path) => new CopilotTranscript(path);
}

internal sealed class CopilotTranscript(string path) : JsonlTranscript(path)
{
    private readonly Dictionary<string, Snapshot> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private List<TranscriptMessage> _history = [];
    private string? _system;

    protected override bool HasCall(string responseId) => _calls.ContainsKey(responseId);

    protected override TranscriptPrompt? BuildPrompt(string responseId) =>
        _calls.TryGetValue(responseId, out var snapshot)
            ? new TranscriptPrompt(Serialize(snapshot.History.Take(snapshot.Count)), snapshot.System, snapshot.Count, Path)
            : null;

    protected override void OnReset()
    {
        _calls.Clear();
        _toolNames.Clear();
        _history = [];
        _system = null;
    }

    protected override void Apply(JsonElement root, LineRef line)
    {
        if (Obj(root, "data") is not { } data)
        {
            return;
        }

        // Lines about an LLM call (apiCallId) or tool call (toolCallId), to show them raw later.
        Index(line, Str(data, "apiCallId"), Str(data, "toolCallId"), Obj(data, "usage") is { } usage ? Str(usage, "apiCallId") : null);

        // Defensive: events of a nested agent never belong to the parent conversation's prompt.
        if (Str(data, "parentToolCallId") is { Length: > 0 })
        {
            return;
        }

        switch (line.Type)
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

        var response = True(data, "success")
            ? Obj(data, "result") is { } result ? Str(result, "content") : null
            : Obj(data, "error") is { } error ? Str(error, "message") : null;
        _history.Add(new TranscriptMessage("tool", [new TranscriptPart("tool_call_response", response ?? string.Empty, id, name)]));
    }

    private void OnCompaction(JsonElement data)
    {
        if (!True(data, "success") || Str(data, "summaryContent") is not { Length: > 0 } summary)
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

    private sealed record Snapshot(List<TranscriptMessage> History, int Count, string? System);
}
