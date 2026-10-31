using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToolCallsInspector.Core.Analysis;
using ToolCallsInspector.Core.Clients;
using ToolCallsInspector.Core.Ingestion;
using ToolCallsInspector.Core.Normalization;
using ToolCallsInspector.Core.Storage;
using ToolCallsInspector.Core.Telemetry;
using ToolCallsInspector.Core.Transcripts;

namespace ToolCallsInspector.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers storage, normalization, ingestion and analysis. Client profiles for VS Code, Copilot CLI and Claude Code are included.</summary>
    /// <param name="copilotSessionStatePath">
    /// Copilot session-state folder used to rebuild full prompts; null uses the default (<c>~/.copilot/session-state</c>), empty disables it.
    /// </param>
    /// <param name="claudeProjectsPath">
    /// Claude Code transcripts folder used to rebuild full prompts; null uses the default (<c>~/.claude/projects</c>), empty disables it.
    /// </param>
    /// <param name="claudeApiBodiesPath">
    /// Folder Claude Code writes request bodies to (<c>OTEL_LOG_RAW_API_BODIES=file:&lt;dir&gt;</c>); null uses the default
    /// (<c>~/.claude/api-bodies</c>), empty disables it.
    /// </param>
    /// <param name="retentionDays">Default retention in days (0 keeps everything); the Setup page can override it.</param>
    /// <param name="analysisCacheSize">Maximum number of trace analyses kept in memory.</param>
    public static IServiceCollection AddToolCallsInspectorCore(
        this IServiceCollection services,
        string databasePath,
        string? copilotSessionStatePath = null,
        string? claudeProjectsPath = null,
        string? claudeApiBodiesPath = null,
        int retentionDays = 0,
        int analysisCacheSize = 2000)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        services.AddDbContextFactory<InspectorDbContext>(o => o.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<ISpanStore, EfSpanStore>();
        services.AddSingleton(sp => new RetentionSettings(sp.GetRequiredService<ISpanStore>(), sp.GetRequiredService<InspectorEvents>(), retentionDays));

        services.AddSingleton<IClientProfile, DemoClientProfile>();
        services.AddSingleton<IClientProfile, VsCodeCopilotChatProfile>();
        services.AddSingleton<IClientProfile, CopilotCliProfile>();
        services.AddSingleton<IClientProfile, ClaudeCodeProfile>();
        services.AddSingleton<IClientCatalog, ClientCatalog>();
        services.AddSingleton<ISpanNormalizer, SpanNormalizer>();

        services.AddSingleton<InspectorEvents>();
        services.AddSingleton<IngestionStatus>();
        services.AddSingleton<ITelemetryIngestor, TelemetryIngestor>();

        services.AddSingleton<ITokenCounter, TiktokenTokenCounter>();
        services.AddSingleton<TokenAttributionEngine>();
        services.AddSingleton<TraceAnalyzer>();
        services.AddSingleton<ITranscriptSource>(new CopilotSessionTranscriptSource(copilotSessionStatePath ?? CopilotSessionTranscriptSource.DefaultRoot()));
        // Exact request bodies first; the transcript reconstruction is the fallback.
        services.AddSingleton<ITranscriptSource>(new ClaudeApiBodiesSource(claudeApiBodiesPath ?? ClaudeApiBodiesSource.DefaultRoot()));
        services.AddSingleton<ITranscriptSource>(new ClaudeCodeTranscriptSource(claudeProjectsPath ?? ClaudeCodeTranscriptSource.DefaultRoot()));
        services.AddSingleton<TranscriptEnricher>();
        services.AddSingleton(new InsightsOptions { AnalysisCacheSize = analysisCacheSize });
        services.AddSingleton<InsightsService>();
        return services;
    }

    /// <summary>Registers an additional pull-based telemetry source (e.g. a Copilot CLI session-file reader).</summary>
    public static IServiceCollection AddTelemetrySource<TSource>(this IServiceCollection services)
        where TSource : class, ITelemetrySource
    {
        services.AddSingleton<ITelemetrySource, TSource>();
        return services;
    }
}
