using Microsoft.EntityFrameworkCore;

namespace ToolCallsInspector.Core.Storage;

public sealed class InspectorDbContext(DbContextOptions<InspectorDbContext> options) : DbContext(options)
{
    public DbSet<SpanRecord> Spans => Set<SpanRecord>();
    public DbSet<SpanContent> SpanContents => Set<SpanContent>();
    public DbSet<TraceBreakdownRow> TraceBreakdowns => Set<TraceBreakdownRow>();
    public DbSet<SettingRow> Settings => Set<SettingRow>();

    /// <summary>
    /// Tables and indexes added after the first release. <c>EnsureCreated</c> only creates a schema for a new database, so
    /// these run (idempotently) on every start to upgrade existing databases in place.
    /// </summary>
    internal static readonly string[] SchemaUpgrades =
    [
        """
        CREATE TABLE IF NOT EXISTS "TraceBreakdowns" (
            "TraceId" TEXT NOT NULL CONSTRAINT "PK_TraceBreakdowns" PRIMARY KEY,
            "SessionKey" TEXT NULL,
            "Fingerprint" TEXT NOT NULL,
            "PredecessorChain" TEXT NOT NULL,
            "Json" TEXT NOT NULL,
            "CarryTranscript" BLOB NULL,
            "UsedTranscript" INTEGER NOT NULL,
            "ComputedAt" INTEGER NOT NULL
        );
        """,
        """CREATE INDEX IF NOT EXISTS "IX_TraceBreakdowns_SessionKey" ON "TraceBreakdowns" ("SessionKey");""",
        """
        CREATE TABLE IF NOT EXISTS "Settings" (
            "Key" TEXT NOT NULL CONSTRAINT "PK_Settings" PRIMARY KEY,
            "Value" TEXT NULL
        );
        """,
        """CREATE INDEX IF NOT EXISTS "IX_Spans_EndTime" ON "Spans" ("EndTime");""",
        """CREATE INDEX IF NOT EXISTS "IX_Spans_ClientId" ON "Spans" ("ClientId");""",
    ];

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var span = modelBuilder.Entity<SpanRecord>();
        span.ToTable("Spans");
        span.HasKey(s => s.Id);
        span.Ignore(s => s.Model);
        span.HasIndex(s => s.TraceId);
        span.HasIndex(s => s.SessionKey);
        span.HasIndex(s => s.StartTime);
        span.HasIndex(s => s.EndTime);
        span.HasIndex(s => s.ClientId);
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

        var breakdown = modelBuilder.Entity<TraceBreakdownRow>();
        breakdown.ToTable("TraceBreakdowns");
        breakdown.HasKey(b => b.TraceId);
        breakdown.HasIndex(b => b.SessionKey);

        var setting = modelBuilder.Entity<SettingRow>();
        setting.ToTable("Settings");
        setting.HasKey(s => s.Key);
    }
}
