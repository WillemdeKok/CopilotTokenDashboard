using Microsoft.Extensions.Logging;
using ToolCallsInspector.Core.Normalization;
using ToolCallsInspector.Core.Storage;
using ToolCallsInspector.Core.Telemetry;

namespace ToolCallsInspector.Core.Ingestion;

public sealed record SpansIngestedEvent(IReadOnlyCollection<string> TraceIds, int Count, string SourceName, DateTimeOffset At);

/// <summary>In-process notification hub so the UI can refresh live as telemetry arrives.</summary>
public sealed class InspectorEvents
{
    public event Action<SpansIngestedEvent>? SpansIngested;
    public event Action? DataCleared;

    public void PublishIngested(SpansIngestedEvent e) => SpansIngested?.Invoke(e);

    public void PublishCleared() => DataCleared?.Invoke();
}

/// <summary>Tracks ingestion health per source for the setup/status page.</summary>
public sealed class IngestionStatus
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, SourceStatus> _sources = new();

    public IReadOnlyList<SourceStatus> Sources
    {
        get
        {
            lock (_lock)
            {
                return _sources.Values.Select(s => s with { }).ToList();
            }
        }
    }

    public void Record(string source, int spans, string? error = null)
    {
        lock (_lock)
        {
            var current = _sources.GetValueOrDefault(source) ?? new SourceStatus(source, 0, 0, null, null);
            _sources[source] = current with
            {
                Batches = current.Batches + 1,
                Spans = current.Spans + spans,
                LastReceived = DateTimeOffset.UtcNow,
                LastError = error ?? current.LastError,
            };
        }
    }
}

public sealed record SourceStatus(string Source, long Batches, long Spans, DateTimeOffset? LastReceived, string? LastError);

public sealed class TelemetryIngestor(
    ISpanNormalizer normalizer,
    ISpanStore store,
    InspectorEvents events,
    IngestionStatus status,
    ILogger<TelemetryIngestor> logger) : ITelemetryIngestor
{
    public async Task<int> IngestAsync(IReadOnlyList<RawSpan> spans, string sourceName, CancellationToken cancellationToken = default)
    {
        try
        {
            var normalized = spans.Select(s => normalizer.Normalize(s, sourceName)).ToList();
            await store.UpsertAsync(normalized, cancellationToken);
            status.Record(sourceName, normalized.Count);

            if (normalized.Count > 0)
            {
                var traceIds = normalized.Select(n => n.Record.TraceId).ToHashSet();
                events.PublishIngested(new SpansIngestedEvent(traceIds, normalized.Count, sourceName, DateTimeOffset.UtcNow));
                logger.LogDebug("Ingested {Count} spans from {Source}", normalized.Count, sourceName);
            }

            return normalized.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status.Record(sourceName, 0, ex.Message);
            logger.LogError(ex, "Failed to ingest {Count} spans from {Source}", spans.Count, sourceName);
            throw;
        }
    }
}
