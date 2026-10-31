using ToolCallsInspector.Core.Storage;
using ToolCallsInspector.Core.Telemetry;

namespace ToolCallsInspector.Core.Clients;

/// <summary>How a client fills <c>gen_ai.input.messages</c> on each LLM call.</summary>
public enum InputCapture
{
    /// <summary>Not known: a prompt that doesn't start where the previous one did is treated as incremental.</summary>
    Unknown,

    /// <summary>Every call carries the whole prompt (history included); it is never appended to the previous prompt.</summary>
    Full,

    /// <summary>Each call only carries the messages added since the previous call (Copilot CLI).</summary>
    Incremental,
}

/// <summary>
/// Describes a Copilot client (VS Code, Copilot CLI, ...). Profiles identify which client emitted a span
/// and can enrich the normalized record with client-specific attributes. Add a new client by registering
/// another implementation in DI – no changes to the pipeline are required.
/// </summary>
public interface IClientProfile
{
    string ClientId { get; }

    string DisplayName { get; }

    /// <summary>Lower values are evaluated first.</summary>
    int Priority { get; }

    bool Matches(RawSpan span);

    /// <summary>Whether captured input messages hold the full prompt or only what was added since the previous call.</summary>
    InputCapture InputCapture => InputCapture.Unknown;

    /// <summary>Category for clients that don't follow the GenAI operation names; null uses the standard mapping.</summary>
    SpanCategory? Categorize(RawSpan span) => null;

    /// <summary>
    /// Maps client-specific attributes after the standard GenAI mapping. Runs before derived fields
    /// (tool/agent name, user prompt, session key, ...) are filled in, which only fill values still empty.
    /// </summary>
    void Enrich(RawSpan span, SpanRecord record, SpanContent content)
    {
    }
}

public interface IClientCatalog
{
    IReadOnlyList<IClientProfile> Profiles { get; }

    IClientProfile Resolve(RawSpan span);

    string GetDisplayName(string clientId);

    IClientProfile? Find(string clientId) => Profiles.FirstOrDefault(p => p.ClientId == clientId);
}

public sealed class ClientCatalog(IEnumerable<IClientProfile> profiles) : IClientCatalog
{
    private readonly IReadOnlyList<IClientProfile> _profiles = profiles.OrderBy(p => p.Priority).ToList();

    public IReadOnlyList<IClientProfile> Profiles => _profiles;

    public IClientProfile Resolve(RawSpan span) =>
        _profiles.FirstOrDefault(p => p.Matches(span)) ?? GenericGenAiClientProfile.Instance;

    public string GetDisplayName(string clientId) =>
        _profiles.FirstOrDefault(p => p.ClientId == clientId)?.DisplayName ?? clientId;
}
