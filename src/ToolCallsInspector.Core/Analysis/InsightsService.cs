using System.Collections.Concurrent;
using ToolCallsInspector.Core.Ingestion;
using ToolCallsInspector.Core.Storage;
using ToolCallsInspector.Core.Transcripts;

namespace ToolCallsInspector.Core.Analysis;

public sealed class GlobalInsights
{
    public required IReadOnlyList<SessionAnalysis> Sessions { get; init; }
    public required TokenTotals Totals { get; init; }
    public required IReadOnlyDictionary<string, TokenTotals> ByClient { get; init; }
}

/// <summary>
/// Query façade used by the UI. Trace analyses are cached and invalidated per trace as new spans arrive,
/// so live sessions stay cheap to refresh.
/// </summary>
public sealed class InsightsService : IDisposable
{
    private readonly ISpanStore _store;
    private readonly TraceAnalyzer _analyzer;
    private readonly InspectorEvents _events;
    private readonly TranscriptEnricher? _transcripts;
    private readonly ConcurrentDictionary<string, TraceAnalysis> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.Ordinal);
    private long _clearGeneration;

    public InsightsService(ISpanStore store, TraceAnalyzer analyzer, InspectorEvents events, TranscriptEnricher? transcripts = null)
    {
        _store = store;
        _analyzer = analyzer;
        _events = events;
        _transcripts = transcripts;
        _events.SpansIngested += OnIngested;
        _events.DataCleared += OnCleared;
    }

    public async Task<IReadOnlyList<SessionAnalysis>> GetSessionsAsync(DateTimeOffset? since = null, CancellationToken cancellationToken = default)
    {
        var metadata = await _store.GetAllMetadataAsync(cancellationToken);
        var traces = await GetTraceAnalysesAsync(metadata, cancellationToken);
        return BuildSessions(traces)
            .Where(s => since is null || s.End >= since)
            .OrderByDescending(s => s.End)
            .ToList();
    }

    public async Task<SessionAnalysis?> GetSessionAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        var sessions = await GetSessionsAsync(null, cancellationToken);
        return sessions.FirstOrDefault(s => s.SessionKey == sessionKey);
    }

    public async Task<GlobalInsights> GetGlobalAsync(DateTimeOffset? since = null, CancellationToken cancellationToken = default)
    {
        var sessions = await GetSessionsAsync(null, cancellationToken);
        var traces = sessions.SelectMany(s => s.Traces).Where(t => since is null || t.End >= since).ToList();

        var totals = new TokenTotals();
        var byClient = new Dictionary<string, TokenTotals>();
        foreach (var trace in traces)
        {
            totals.Add(trace.Totals);
            if (!byClient.TryGetValue(trace.ClientId, out var clientTotals))
            {
                byClient[trace.ClientId] = clientTotals = new TokenTotals();
            }

            clientTotals.Add(trace.Totals);
        }

        return new GlobalInsights
        {
            Sessions = sessions.Where(s => since is null || s.End >= since).ToList(),
            Totals = totals,
            ByClient = byClient,
        };
    }

    public async Task<TraceAnalysis?> GetTraceAsync(string traceId, CancellationToken cancellationToken = default)
    {
        var metadata = await _store.GetTraceMetadataAsync([traceId], cancellationToken);
        return metadata.Count == 0 ? null : (await GetTraceAnalysesAsync(metadata, cancellationToken)).FirstOrDefault();
    }

    public async Task<(TraceAnalysis Trace, SpanNode Node)?> FindSpanAsync(string spanRecordId, CancellationToken cancellationToken = default)
    {
        var span = await _store.GetSpanAsync(spanRecordId, cancellationToken);
        if (span is null)
        {
            return null;
        }

        var metadata = await _store.GetTraceMetadataAsync([span.TraceId], cancellationToken);
        var trace = (await GetTraceAnalysesAsync(metadata, cancellationToken)).FirstOrDefault();
        var node = trace?.Roots.Concat(trace.Roots.SelectMany(r => r.Descendants())).FirstOrDefault(n => n.Span.Id == spanRecordId);
        return trace is null || node is null ? null : (trace, node);
    }

    public async Task<IReadOnlyDictionary<string, SpanContent>> GetContentAsync(IEnumerable<string> spanRecordIds, CancellationToken cancellationToken = default) =>
        Enrich(await _store.GetContentAsync(spanRecordIds, cancellationToken));

    private IReadOnlyDictionary<string, SpanContent> Enrich(IReadOnlyDictionary<string, SpanContent> content) =>
        _transcripts?.Enrich(content) ?? content;

    private async Task<IReadOnlyList<TraceAnalysis>> GetTraceAnalysesAsync(IReadOnlyList<SpanRecord> metadata, CancellationToken cancellationToken)
    {
        var result = new List<TraceAnalysis>();
        var missing = new List<IGrouping<string, SpanRecord>>();

        foreach (var trace in metadata.GroupBy(s => s.TraceId))
        {
            if (_cache.TryGetValue(trace.Key, out var cached))
            {
                result.Add(cached);
            }
            else
            {
                missing.Add(trace);
            }
        }

        foreach (var chunk in missing.Chunk(50))
        {
            var versions = chunk.ToDictionary(t => t.Key, t => _versions.GetValueOrDefault(t.Key));
            var generation = Interlocked.Read(ref _clearGeneration);
            var contentIds = chunk.SelectMany(t => t)
                .Where(s => s.Category is SpanCategory.LlmCall or SpanCategory.Tool)
                .Select(s => s.Id);
            var content = Enrich(await _store.GetContentAsync(contentIds, cancellationToken));

            foreach (var trace in chunk)
            {
                var spans = trace.ToList();
                var analysis = _analyzer.Analyze(trace.Key, spans, content, TraceAnalyzer.ResolveSessionKey(trace.Key, spans));
                result.Add(analysis);

                if (_versions.GetValueOrDefault(trace.Key) == versions[trace.Key] && Interlocked.Read(ref _clearGeneration) == generation)
                {
                    _cache[trace.Key] = analysis;
                }
            }
        }

        return result;
    }

    private static IEnumerable<SessionAnalysis> BuildSessions(IEnumerable<TraceAnalysis> traces)
    {
        // Host-only telemetry (e.g. the Copilot app's session.provisioning / session.timing.* traces) has no LLM or tool calls.
        // It can share a conversation id with real turns, so it is dropped per trace rather than per session.
        foreach (var group in traces.Where(t => t.Totals.LlmCalls > 0 || t.Totals.ToolCalls > 0).GroupBy(t => t.SessionKey))
        {
            var ordered = group.OrderBy(t => t.Start).ToList();
            var totals = new TokenTotals();
            ordered.ForEach(t => totals.Add(t.Totals));

            yield return new SessionAnalysis
            {
                SessionKey = group.Key,
                ClientId = ordered.GroupBy(t => t.ClientId).OrderByDescending(g => g.Count()).First().Key,
                Traces = ordered,
                Totals = totals,
            };
        }
    }

    private void OnIngested(SpansIngestedEvent e)
    {
        foreach (var traceId in e.TraceIds)
        {
            _versions.AddOrUpdate(traceId, 1, (_, v) => v + 1);
            _cache.TryRemove(traceId, out _);
        }
    }

    private void OnCleared()
    {
        Interlocked.Increment(ref _clearGeneration);
        _cache.Clear();
    }

    public void Dispose()
    {
        _events.SpansIngested -= OnIngested;
        _events.DataCleared -= OnCleared;
    }
}
