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
}

public sealed class EfSpanStore(IDbContextFactory<InspectorDbContext> factory) : ISpanStore
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);

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
}

public sealed record StoreStats(int Spans, int Traces);
