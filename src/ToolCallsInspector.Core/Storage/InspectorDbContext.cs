using Microsoft.EntityFrameworkCore;

namespace ToolCallsInspector.Core.Storage;

public sealed class InspectorDbContext(DbContextOptions<InspectorDbContext> options) : DbContext(options)
{
    public DbSet<SpanRecord> Spans => Set<SpanRecord>();
    public DbSet<SpanContent> SpanContents => Set<SpanContent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var span = modelBuilder.Entity<SpanRecord>();
        span.ToTable("Spans");
        span.HasKey(s => s.Id);
        span.Ignore(s => s.Model);
        span.HasIndex(s => s.TraceId);
        span.HasIndex(s => s.SessionKey);
        span.HasIndex(s => s.StartTime);
        span.HasIndex(s => s.Category);

        // SQLite cannot order/compare DateTimeOffset natively; store as UTC ticks.
        span.Property(s => s.StartTime).HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        span.Property(s => s.EndTime).HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        span.Property(s => s.ReceivedAt).HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

        var content = modelBuilder.Entity<SpanContent>();
        content.ToTable("SpanContents");
        content.HasKey(c => c.Id);
        content.Ignore(c => c.TranscriptPath);
        content.Ignore(c => c.TranscriptSource);
        content.Ignore(c => c.TelemetryInputMessages);
    }
}
