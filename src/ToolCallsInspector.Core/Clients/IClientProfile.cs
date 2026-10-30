using ToolCallsInspector.Core.Storage;
using ToolCallsInspector.Core.Telemetry;

namespace ToolCallsInspector.Core.Clients;

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

    void Enrich(RawSpan span, SpanRecord record, SpanContent content)
    {
    }
}

public interface IClientCatalog
{
    IReadOnlyList<IClientProfile> Profiles { get; }

    IClientProfile Resolve(RawSpan span);

    string GetDisplayName(string clientId);
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
