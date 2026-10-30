using ToolCallsInspector.Core.Storage;
using ToolCallsInspector.Core.Telemetry;

namespace ToolCallsInspector.Core.Clients;

/// <summary>VS Code GitHub Copilot Chat (service.name = "copilot-chat").</summary>
public sealed class VsCodeCopilotChatProfile : IClientProfile
{
    public const string Id = "vscode";

    public string ClientId => Id;
    public string DisplayName => "VS Code Copilot Chat";
    public int Priority => 10;

    public bool Matches(RawSpan span)
    {
        var service = span.ResourceAttributes.GetString("service.name");
        return string.Equals(service, "copilot-chat", StringComparison.OrdinalIgnoreCase)
               || span.Attributes.Keys.Any(k => k.StartsWith("copilot_chat.", StringComparison.Ordinal));
    }

    public void Enrich(RawSpan span, SpanRecord record, SpanContent content)
    {
        // VS Code exposes the window id as the "session.id" resource attribute.
        record.ClientInstanceId ??= span.ResourceAttributes.GetString("session.id");
    }
}

/// <summary>
/// GitHub Copilot CLI / Copilot app. The CLI shares the <c>github.copilot.*</c> OTel conventions with VS Code,
/// so the generic normalizer handles most attributes; this profile only identifies the client.
/// </summary>
public sealed class CopilotCliProfile : IClientProfile
{
    public const string Id = "copilot-cli";

    public string ClientId => Id;
    public string DisplayName => "Copilot CLI / app";
    public int Priority => 20;

    public bool Matches(RawSpan span)
    {
        var service = span.ResourceAttributes.GetString("service.name") ?? string.Empty;
        return service.Contains("copilot-cli", StringComparison.OrdinalIgnoreCase)
               || service.Contains("copilotcli", StringComparison.OrdinalIgnoreCase)
               || service.Equals("github-copilot", StringComparison.OrdinalIgnoreCase);
    }

    public void Enrich(RawSpan span, SpanRecord record, SpanContent content)
    {
        record.ClientInstanceId ??= span.ResourceAttributes.GetString("session.id", "service.instance.id");
    }
}

/// <summary>Fallback for any other GenAI semantic-convention compliant emitter.</summary>
public sealed class GenericGenAiClientProfile : IClientProfile
{
    public static readonly GenericGenAiClientProfile Instance = new();

    public string ClientId => "otel";
    public string DisplayName => "Other OTel GenAI client";
    public int Priority => int.MaxValue;

    public bool Matches(RawSpan span) => true;
}
