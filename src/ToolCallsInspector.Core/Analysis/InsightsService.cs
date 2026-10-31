using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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

public sealed class InsightsOptions
{
    /// <summary>Maximum number of trace analyses kept in memory (least recently used are evicted).</summary>
    public int AnalysisCacheSize { get; set; } = 2000;
}

/// <summary>
/// Query façade used by the UI. Traces are analysed per session in chronological order, because a trace's main loop continues
/// from the previous trace's last prompt. Each analysis is keyed by a fingerprint of its own spans and its predecessors', so a
/// new span invalidates exactly the traces it can affect. Completed traces are saved to the database and reused while unchanged,
/// so historical numbers survive deleted transcripts and restarts don't recompute everything.
/// </summary>
public sealed class InsightsService : IDisposable
{
    /// <summary>Bump to invalidate saved breakdowns when the attribution logic changes.</summary>
    private const string AttributionVersion = "1";
    private const int ChunkSize = 50;

    private readonly ISpanStore _store;
    private readonly TraceAnalyzer _analyzer;
    private readonly InspectorEvents _events;
    private readonly TranscriptEnricher? _transcripts;
    private readonly int _cacheSize;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _analysisLock = new(1, 1);
    private long _clock;
    private long _clearGeneration;

    public InsightsService(ISpanStore store, TraceAnalyzer analyzer, InspectorEvents events, TranscriptEnricher? transcripts = null, InsightsOptions? options = null)
    {
        _store = store;
        _analyzer = analyzer;
        _events = events;
        _transcripts = transcripts;
        _cacheSize = Math.Max(1, options?.AnalysisCacheSize ?? new InsightsOptions().AnalysisCacheSize);
        _events.DataCleared += OnCleared;
    }

    /// <summary>Number of trace analyses currently held in memory.</summary>
    public int CachedTraceCount => _cache.Count;

    public async Task<IReadOnlyList<SessionAnalysis>> GetSessionsAsync(DateTimeOffset? since = null, CancellationToken cancellationToken = default)
    {
        var index = await _store.GetTraceIndexAsync(since, cancellationToken);
        var traces = await AnalyzeAsync(index, cancellationToken);
        return BuildSessions(traces)
            .Where(s => since is null || s.End >= since)
            .OrderByDescending(s => s.End)
            .ToList();
    }

    public async Task<SessionAnalysis?> GetSessionAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        var index = await _store.GetTraceIndexAsync(null, cancellationToken);
        var traces = await AnalyzeAsync(index.Where(e => e.SessionKey == sessionKey).ToList(), cancellationToken);
        return BuildSessions(traces).FirstOrDefault();
    }

    public async Task<GlobalInsights> GetGlobalAsync(DateTimeOffset? since = null, CancellationToken cancellationToken = default)
    {
        var sessions = await GetSessionsAsync(since, cancellationToken);
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
            Sessions = sessions,
            Totals = totals,
            ByClient = byClient,
        };
    }

    public async Task<TraceAnalysis?> GetTraceAsync(string traceId, CancellationToken cancellationToken = default)
    {
        var index = await _store.GetTraceIndexAsync(null, cancellationToken);
        var target = index.FirstOrDefault(e => e.TraceId == traceId);
        if (target is null)
        {
            return null;
        }

        // Only the session's traces up to this one can affect it.
        var session = Order(index.Where(e => e.SessionKey == target.SessionKey)).ToList();
        var upTo = session.TakeWhile(e => e.TraceId != traceId).Append(target).ToList();
        var traces = await AnalyzeAsync(upTo, cancellationToken);
        return traces.FirstOrDefault(t => t.TraceId == traceId);
    }

    public async Task<(TraceAnalysis Trace, SpanNode Node)?> FindSpanAsync(string spanRecordId, CancellationToken cancellationToken = default)
    {
        var span = await _store.GetSpanAsync(spanRecordId, cancellationToken);
        if (span is null)
        {
            return null;
        }

        var trace = await GetTraceAsync(span.TraceId, cancellationToken);
        var node = trace?.Roots.Concat(trace.Roots.SelectMany(r => r.Descendants())).FirstOrDefault(n => n.Span.Id == spanRecordId);
        return trace is null || node is null ? null : (trace, node);
    }

    public async Task<IReadOnlyDictionary<string, SpanContent>> GetContentAsync(IEnumerable<string> spanRecordIds, CancellationToken cancellationToken = default) =>
        Enrich(await _store.GetContentAsync(spanRecordIds, cancellationToken));

    private IReadOnlyDictionary<string, SpanContent> Enrich(IReadOnlyDictionary<string, SpanContent> content) =>
        _transcripts?.Enrich(content) ?? content;

    private static IEnumerable<TraceIndexEntry> Order(IEnumerable<TraceIndexEntry> entries) =>
        entries.OrderBy(e => e.Start).ThenBy(e => e.TraceId, StringComparer.Ordinal);

    /// <summary>Analyses the given traces (whole sessions, or a session prefix), reusing cached and saved results where still valid.</summary>
    private async Task<IReadOnlyList<TraceAnalysis>> AnalyzeAsync(IReadOnlyList<TraceIndexEntry> index, CancellationToken cancellationToken)
    {
        // Chain fingerprints: a trace's result depends on its own spans and (through the carried prompt) on every earlier trace.
        var work = new List<WorkItem>();
        foreach (var session in index.GroupBy(e => e.SessionKey, StringComparer.Ordinal))
        {
            WorkItem? previous = null;
            foreach (var entry in Order(session))
            {
                var predecessorChain = previous?.Chain ?? string.Empty;
                var item = new WorkItem(entry, previous, predecessorChain, Hash($"{OwnFingerprint(entry)}|{predecessorChain}"));
                work.Add(item);
                previous = item;
            }
        }

        var results = new Dictionary<string, TraceAnalysis>(StringComparer.Ordinal);
        var misses = new List<WorkItem>();
        foreach (var item in work)
        {
            if (_cache.TryGetValue(item.Entry.TraceId, out var cached) && cached.Chain == item.Chain)
            {
                cached.LastAccess = Interlocked.Increment(ref _clock);
                results[item.Entry.TraceId] = cached.Analysis;
            }
            else
            {
                misses.Add(item);
            }
        }

        if (misses.Count > 0)
        {
            await _analysisLock.WaitAsync(cancellationToken);
            try
            {
                await AnalyzeMissesAsync(misses, results, cancellationToken);
            }
            finally
            {
                _analysisLock.Release();
            }

            Evict();
        }

        return work.Select(w => results.GetValueOrDefault(w.Entry.TraceId)).OfType<TraceAnalysis>().ToList();
    }

    private async Task AnalyzeMissesAsync(List<WorkItem> misses, Dictionary<string, TraceAnalysis> results, CancellationToken cancellationToken)
    {
        // Misses are in session order, so a trace's predecessor is either already in results or earlier in this loop.
        foreach (var chunk in misses.Chunk(ChunkSize))
        {
            var generation = Interlocked.Read(ref _clearGeneration);
            var pending = new List<WorkItem>();
            foreach (var item in chunk)
            {
                // Another caller may have filled the cache while we waited for the lock.
                if (_cache.TryGetValue(item.Entry.TraceId, out var cached) && cached.Chain == item.Chain)
                {
                    cached.LastAccess = Interlocked.Increment(ref _clock);
                    results[item.Entry.TraceId] = cached.Analysis;
                }
                else
                {
                    pending.Add(item);
                }
            }

            if (pending.Count == 0)
            {
                continue;
            }

            var ids = pending.Select(p => p.Entry.TraceId).ToList();
            var metadata = (await _store.GetTraceMetadataAsync(ids, cancellationToken)).ToLookup(s => s.TraceId, StringComparer.Ordinal);
            var savedRows = await _store.GetBreakdownsAsync(ids, cancellationToken);

            var saved = new Dictionary<string, SavedBreakdown>(StringComparer.Ordinal);
            foreach (var item in pending)
            {
                if (savedRows.TryGetValue(item.Entry.TraceId, out var row)
                    && row.Fingerprint == OwnFingerprint(item.Entry)
                    // A missing predecessor (e.g. removed by retention) doesn't invalidate what was computed from it.
                    && (item.Previous is null || row.PredecessorChain == item.PredecessorChain)
                    && SavedBreakdown.Deserialize(row.Json, row.CarryTranscript) is { } breakdown)
                {
                    saved[item.Entry.TraceId] = breakdown;
                }
            }

            // Saved traces only need stored content (to link tool calls); the rest also get local transcripts.
            var rawContent = await _store.GetContentAsync(
                pending.SelectMany(p => metadata[p.Entry.TraceId])
                    .Where(s => s.Category is SpanCategory.LlmCall or SpanCategory.Tool)
                    .Select(s => s.Id),
                cancellationToken);
            IReadOnlyDictionary<string, SpanContent>? enriched = null;
            IReadOnlyDictionary<string, SpanContent> EnrichedContent()
            {
                if (enriched is null)
                {
                    var recompute = pending.Where(p => !saved.ContainsKey(p.Entry.TraceId)).SelectMany(p => metadata[p.Entry.TraceId]).Select(s => s.Id).ToHashSet();
                    var merged = rawContent.ToDictionary(kv => kv.Key, kv => kv.Value);
                    foreach (var (id, value) in Enrich(rawContent.Where(kv => recompute.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value)))
                    {
                        merged[id] = value;
                    }

                    enriched = merged;
                }

                return enriched;
            }

            var toSave = new List<TraceBreakdownRow>();
            foreach (var item in pending)
            {
                var spans = metadata[item.Entry.TraceId].ToList();
                var carryIn = item.Previous is null ? null : results.GetValueOrDefault(item.Previous.Entry.TraceId)?.CarryOut;
                TraceAnalysis analysis;
                if (saved.TryGetValue(item.Entry.TraceId, out var breakdown))
                {
                    analysis = _analyzer.Analyze(item.Entry.TraceId, spans, rawContent, item.Entry.SessionKey, carryIn, breakdown);
                    if (!analysis.FromSavedBreakdown)
                    {
                        analysis = _analyzer.Analyze(item.Entry.TraceId, spans, Enrich(rawContent), item.Entry.SessionKey, carryIn);
                    }
                }
                else
                {
                    analysis = _analyzer.Analyze(item.Entry.TraceId, spans, EnrichedContent(), item.Entry.SessionKey, carryIn);
                }

                results[item.Entry.TraceId] = analysis;
                if (!analysis.FromSavedBreakdown && !analysis.InProgress && analysis.Totals.LlmCalls > 0)
                {
                    var capture = SavedBreakdown.Capture(analysis);
                    toSave.Add(new TraceBreakdownRow
                    {
                        TraceId = item.Entry.TraceId,
                        SessionKey = item.Entry.SessionKey,
                        Fingerprint = OwnFingerprint(item.Entry),
                        PredecessorChain = item.PredecessorChain,
                        Json = capture.Serialize(),
                        CarryTranscript = SavedBreakdown.CompressTranscript(capture.CarryTranscript),
                        UsedTranscript = capture.UsedTranscript,
                        ComputedAt = DateTimeOffset.UtcNow.UtcTicks,
                    });
                }

                if (Interlocked.Read(ref _clearGeneration) == generation)
                {
                    _cache[item.Entry.TraceId] = new CacheEntry(analysis, item.Chain) { LastAccess = Interlocked.Increment(ref _clock) };
                }
            }

            if (toSave.Count > 0 && Interlocked.Read(ref _clearGeneration) == generation)
            {
                await _store.SaveBreakdownsAsync(toSave, cancellationToken);
            }
        }
    }

    private void Evict()
    {
        if (_cache.Count <= _cacheSize)
        {
            return;
        }

        var remove = _cache.Count - _cacheSize + _cacheSize / 10;
        foreach (var (key, _) in _cache.OrderBy(kv => kv.Value.LastAccess).Take(remove).ToList())
        {
            _cache.TryRemove(key, out _);
        }
    }

    private static string OwnFingerprint(TraceIndexEntry entry) => $"{AttributionVersion}|{entry.Fingerprint}";

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0, 12);

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

    private void OnCleared()
    {
        Interlocked.Increment(ref _clearGeneration);
        _cache.Clear();
    }

    public void Dispose()
    {
        _events.DataCleared -= OnCleared;
    }

    private sealed record WorkItem(TraceIndexEntry Entry, WorkItem? Previous, string PredecessorChain, string Chain);

    private sealed class CacheEntry(TraceAnalysis analysis, string chain)
    {
        public TraceAnalysis Analysis { get; } = analysis;
        public string Chain { get; } = chain;
        public long LastAccess { get; set; }
    }
}