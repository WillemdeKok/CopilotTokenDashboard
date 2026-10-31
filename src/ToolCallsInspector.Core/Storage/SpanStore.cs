using Microsoft.EntityFrameworkCore;

namespace ToolCallsInspector.Core.Storage;

public interface ISpanStore
{
    Task EnsureCreatedAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(IReadOnlyList<(SpanRecord Record, SpanContent Content)> spans, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SpanRecord>> GetAllMetadataAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SpanRecord>> GetTraceMetadataAsync(IEnumerable<string> traceIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, SpanContent>> GetContentAsync(IEnumerable<string> spanIds, CancellationToken cancellationToken = default);
    Task<SpanRecord?> GetSpanAsync(string id, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task<StoreStats> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One entry per trace with its resolved session key. With <paramref name="since"/>, only sessions (and session-less traces)
    /// with activity at or after it are returned, each with all of its traces.
    /// </summary>
    Task<IReadOnlyList<TraceIndexEntry>> GetTraceIndexAsync(DateTimeOffset? since = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, TraceBreakdownRow>> GetBreakdownsAsync(IEnumerable<string> traceIds, CancellationToken cancellationToken = default);
    Task SaveBreakdownsAsync(IReadOnlyList<TraceBreakdownRow> rows, CancellationToken cancellationToken = default);

    /// <summary>Deletes every trace whose last span ended before <paramref name="cutoff"/>; returns the number of traces removed.</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    /// <summary>Deletes every trace with a span from <paramref name="clientId"/> (or ingested from <paramref name="sourceName"/>); returns the number removed.</summary>
    Task<int> DeleteClientAsync(string clientId, string? sourceName = null, CancellationToken cancellationToken = default);

    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default);
    Task SetSettingAsync(string key, string? value, CancellationToken cancellationToken = default);
}

public sealed class EfSpanStore(IDbContextFactory<InspectorDbContext> factory) : ISpanStore
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        foreach (var sql in InspectorDbContext.SchemaUpgrades)
        {
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        }

        // Backfill for spans stored before non-zero process exit codes were treated as tool failures (idempotent).
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE Spans
            SET ErrorType = 'exit code ' || (SELECT json_extract(c.AttributesJson, '$."process.exit.code"') FROM SpanContents c WHERE c.Id = Spans.Id)
            WHERE Category = 3 AND ErrorType IS NULL AND EXISTS (
                SELECT 1 FROM SpanContents c
                WHERE c.Id = Spans.Id AND json_valid(c.AttributesJson)
                  AND CAST(json_extract(c.AttributesJson, '$."process.exit.code"') AS INTEGER) <> 0)
            """, cancellationToken);
    }

    public async Task UpsertAsync(IReadOnlyList<(SpanRecord Record, SpanContent Content)> spans, CancellationToken cancellationToken = default)
    {
        if (spans.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var ids = spans.Select(s => s.Record.Id).ToList();
            var existing = await db.Spans.Where(s => ids.Contains(s.Id)).Select(s => s.Id).ToListAsync(cancellationToken);
            var existingSet = existing.ToHashSet();

            foreach (var (record, content) in spans.DistinctBy(s => s.Record.Id))
            {
                if (existingSet.Contains(record.Id))
                {
                    db.Spans.Update(record);
                    db.SpanContents.Update(content);
                }
                else
                {
                    db.Spans.Add(record);
                    db.SpanContents.Add(content);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<SpanRecord>> GetAllMetadataAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Spans.AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SpanRecord>> GetTraceMetadataAsync(IEnumerable<string> traceIds, CancellationToken cancellationToken = default)
    {
        var ids = traceIds.Distinct().ToList();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Spans.AsNoTracking().Where(s => ids.Contains(s.TraceId)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, SpanContent>> GetContentAsync(IEnumerable<string> spanIds, CancellationToken cancellationToken = default)
    {
        var ids = spanIds.Distinct().ToList();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var result = new Dictionary<string, SpanContent>();
        foreach (var chunk in ids.Chunk(500))
        {
            var rows = await db.SpanContents.AsNoTracking().Where(c => chunk.Contains(c.Id)).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                result[row.Id] = row;
            }
        }

        return result;
    }

    public async Task<SpanRecord?> GetSpanAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Spans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await db.SpanContents.ExecuteDeleteAsync(cancellationToken);
            await db.Spans.ExecuteDeleteAsync(cancellationToken);
            await db.TraceBreakdowns.ExecuteDeleteAsync(cancellationToken);

            // Reclaim disk space: rebuild the file without the deleted pages, then truncate the WAL
            // (in WAL mode VACUUM itself writes through the WAL).
            await db.Database.ExecuteSqlRawAsync("VACUUM;", cancellationToken);
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<StoreStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var spans = await db.Spans.CountAsync(cancellationToken);
        var traces = await db.Spans.Select(s => s.TraceId).Distinct().CountAsync(cancellationToken);
        return new StoreStats(spans, traces);
    }

    public async Task<IReadOnlyList<TraceIndexEntry>> GetTraceIndexAsync(DateTimeOffset? since = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        const string select = """
            SELECT TraceId, SessionKey, COUNT(*) AS SpanCount, MAX(ReceivedAt) AS MaxReceived, MIN(StartTime) AS StartTicks, MAX(EndTime) AS EndTicks
            FROM Spans
            """;
        const string inRange = """
            WHERE TraceId IN (
                SELECT TraceId FROM Spans WHERE EndTime >= {0}
                UNION
                SELECT s.TraceId FROM Spans s WHERE s.SessionKey IN (SELECT SessionKey FROM Spans WHERE EndTime >= {0} AND SessionKey IS NOT NULL))
            """;
        var rows = since is null
            ? await db.Database.SqlQueryRaw<TraceIndexRow>($"{select}\nGROUP BY TraceId, SessionKey").ToListAsync(cancellationToken)
            : await db.Database.SqlQueryRaw<TraceIndexRow>($"{select}\n{inRange}\nGROUP BY TraceId, SessionKey", since.Value.UtcTicks).ToListAsync(cancellationToken);

        var traces = rows.GroupBy(r => r.TraceId, StringComparer.Ordinal).ToList();

        // In-progress turns may have no session key yet: fall back to the most common conversation id, as TraceAnalyzer does.
        var keyless = traces.Where(t => t.All(r => string.IsNullOrEmpty(r.SessionKey))).Select(t => t.Key).ToList();
        var conversations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in keyless.Chunk(500))
        {
            var spans = await db.Spans.AsNoTracking()
                .Where(s => chunk.Contains(s.TraceId) && s.ConversationId != null)
                .Select(s => new { s.TraceId, s.ConversationId })
                .ToListAsync(cancellationToken);
            foreach (var trace in spans.GroupBy(s => s.TraceId))
            {
                conversations[trace.Key] = trace.GroupBy(s => s.ConversationId!).OrderByDescending(g => g.Count()).First().Key;
            }
        }

        return traces.Select(t =>
        {
            var sessionKey = t.Where(r => !string.IsNullOrEmpty(r.SessionKey)).OrderByDescending(r => r.SpanCount).Select(r => r.SessionKey).FirstOrDefault()
                             ?? conversations.GetValueOrDefault(t.Key)
                             ?? t.Key;
            return new TraceIndexEntry(
                t.Key,
                sessionKey!,
                t.Sum(r => r.SpanCount),
                t.Max(r => r.MaxReceived),
                new DateTimeOffset(t.Min(r => r.StartTicks), TimeSpan.Zero),
                new DateTimeOffset(t.Max(r => r.EndTicks), TimeSpan.Zero));
        }).ToList();
    }

    public async Task<IReadOnlyDictionary<string, TraceBreakdownRow>> GetBreakdownsAsync(IEnumerable<string> traceIds, CancellationToken cancellationToken = default)
    {
        var ids = traceIds.Distinct().ToList();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var result = new Dictionary<string, TraceBreakdownRow>(StringComparer.Ordinal);
        foreach (var chunk in ids.Chunk(500))
        {
            foreach (var row in await db.TraceBreakdowns.AsNoTracking().Where(b => chunk.Contains(b.TraceId)).ToListAsync(cancellationToken))
            {
                result[row.TraceId] = row;
            }
        }

        return result;
    }

    public async Task SaveBreakdownsAsync(IReadOnlyList<TraceBreakdownRow> rows, CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var unique = rows.GroupBy(r => r.TraceId).Select(g => g.Last()).ToList();
            var ids = unique.Select(r => r.TraceId).ToList();
            var existing = (await db.TraceBreakdowns.Where(b => ids.Contains(b.TraceId)).Select(b => b.TraceId).ToListAsync(cancellationToken)).ToHashSet();

            // Only keep breakdowns for traces that still exist (a clear or retention run may have raced the analysis).
            var live = (await db.Spans.Where(s => ids.Contains(s.TraceId)).Select(s => s.TraceId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
            foreach (var row in unique.Where(r => live.Contains(r.TraceId)))
            {
                if (existing.Contains(row.TraceId))
                {
                    db.TraceBreakdowns.Update(row);
                }
                else
                {
                    db.TraceBreakdowns.Add(row);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var traceIds = await db.Database
            .SqlQueryRaw<string>("SELECT TraceId AS Value FROM Spans GROUP BY TraceId HAVING MAX(EndTime) < {0}", cutoff.UtcTicks)
            .ToListAsync(cancellationToken);
        await DeleteTracesAsync(traceIds, cancellationToken);
        return traceIds.Count;
    }

    public async Task<int> DeleteClientAsync(string clientId, string? sourceName = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var traceIds = await db.Spans.AsNoTracking()
            .Where(s => s.ClientId == clientId || (sourceName != null && s.SourceName == sourceName))
            .Select(s => s.TraceId)
            .Distinct()
            .ToListAsync(cancellationToken);
        await DeleteTracesAsync(traceIds, cancellationToken);
        return traceIds.Count;
    }

    private async Task DeleteTracesAsync(IReadOnlyList<string> traceIds, CancellationToken cancellationToken)
    {
        if (traceIds.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            foreach (var chunk in traceIds.Chunk(500))
            {
                await db.SpanContents.Where(c => db.Spans.Any(s => s.Id == c.Id && chunk.Contains(s.TraceId))).ExecuteDeleteAsync(cancellationToken);
                await db.Spans.Where(s => chunk.Contains(s.TraceId)).ExecuteDeleteAsync(cancellationToken);
                await db.TraceBreakdowns.Where(b => chunk.Contains(b.TraceId)).ExecuteDeleteAsync(cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task SetSettingAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
            if (row is null)
            {
                db.Settings.Add(new SettingRow { Key = key, Value = value });
            }
            else
            {
                row.Value = value;
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private sealed class TraceIndexRow
    {
        public string TraceId { get; set; } = string.Empty;
        public string? SessionKey { get; set; }
        public int SpanCount { get; set; }
        public long MaxReceived { get; set; }
        public long StartTicks { get; set; }
        public long EndTicks { get; set; }
    }
}

public sealed record StoreStats(int Spans, int Traces);
