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
    /// <summary>Registers storage, normalization, ingestion and analysis. Client profiles for VS Code and Copilot CLI are included.</summary>
    /// <param name="copilotSessionStatePath">
    /// Copilot session-state folder used to rebuild full prompts; null uses the default (<c>~/.copilot/session-state</c>), empty disables it.
    /// </param>
    public static IServiceCollection AddToolCallsInspectorCore(this IServiceCollection services, string databasePath, string? copilotSessionStatePath = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        services.AddDbContextFactory<InspectorDbContext>(o => o.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<ISpanStore, EfSpanStore>();

        services.AddSingleton<IClientProfile, VsCodeCopilotChatProfile>();
        services.AddSingleton<IClientProfile, CopilotCliProfile>();
        services.AddSingleton<IClientCatalog, ClientCatalog>();
        services.AddSingleton<ISpanNormalizer, SpanNormalizer>();

        services.AddSingleton<InspectorEvents>();
        services.AddSingleton<IngestionStatus>();
        services.AddSingleton<ITelemetryIngestor, TelemetryIngestor>();

        services.AddSingleton<ITokenCounter, TiktokenTokenCounter>();
        services.AddSingleton<TokenAttributionEngine>();
        services.AddSingleton<TraceAnalyzer>();
        var transcripts = new CopilotSessionTranscriptSource(copilotSessionStatePath ?? CopilotSessionTranscriptSource.DefaultRoot());
        services.AddSingleton(transcripts);
        services.AddSingleton<ITranscriptSource>(transcripts);
        services.AddSingleton<TranscriptEnricher>();
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
