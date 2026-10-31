using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToolCallsInspector.Core;
using ToolCallsInspector.Core.Analysis;
using ToolCallsInspector.Core.Clients;
using ToolCallsInspector.Core.Ingestion;
using ToolCallsInspector.Core.Normalization;
using ToolCallsInspector.Core.Storage;
using ToolCallsInspector.Core.Telemetry;
using ToolCallsInspector.Core.Transcripts;
using ToolCallsInspector.Ingestion.Otlp;
using ToolCallsInspector.Web.Services;
using static ToolCallsInspector.Tests.SpanFactory;

namespace ToolCallsInspector.Tests;

/// <summary>Storage-backed behaviour: saved breakdowns, schema upgrades, retention, demo data and the analysis cache.</summary>
public sealed class PersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Origin = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tci-tests", Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = default!;

    private ISpanStore Store => _services.GetRequiredService<ISpanStore>();
    private InspectorEvents Events => _services.GetRequiredService<InspectorEvents>();

    public async Task InitializeAsync()
    {
        _services = new ServiceCollection()
            .AddLogging()
            .AddToolCallsInspectorCore(Path.Combine(_directory, "inspector.db"), copilotSessionStatePath: "", claudeProjectsPath: "", claudeApiBodiesPath: "")
            .BuildServiceProvider();
        await Store.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private InsightsService Insights(ITranscriptSource? source = null, int cacheSize = 2000) =>
        new(Store, CreateAnalyzer(), Events, source is null ? null : new TranscriptEnricher(source), new InsightsOptions { AnalysisCacheSize = cacheSize });

    private Task StoreAsync(IEnumerable<RawSpan> spans, string sourceName = "test")
    {
        var normalizer = new SpanNormalizer(Catalog);
        return Store.UpsertAsync(spans.Select(s => normalizer.Normalize(s, sourceName)).ToList());
    }

    private static SpanFactory Turn(string session, double offsetSeconds, string? conversationId = null, string? responseId = null)
    {
        var f = new SpanFactory { Session = session };
        var agent = f.Agent(offsetSeconds, offsetSeconds + 10);
        f.Chat(offsetSeconds + 1, offsetSeconds + 2, input: 3000, output: 20, agent,
            inputMessages: Messages(Msg("user", Text("latest question"))), conversationId: conversationId, responseId: responseId);
        return f;
    }

    [Fact]
    public async Task Saved_breakdown_is_used_after_the_transcript_is_deleted()
    {
        var source = new FakeTranscriptSource("conv-1", Messages(
            Msg("user", Text("first question")),
            Msg("assistant", ToolCall("t1", "read_file")),
            Msg("tool", ToolResponse("t1", Words(2500, "file"))),
            Msg("user", Text("latest question"))));
        var turn = Turn("session-1", 0, conversationId: "conv-1", responseId: "r1");
        await StoreAsync(turn.Spans);

        var first = (await Insights(source).GetSessionsAsync()).Single().Traces.Single().LlmCalls.Single();
        Assert.Equal("Fake log", first.PromptSource);
        Assert.True(first.Composition!.Get(TokenCategory.ToolResults) > 2000);

        var row = Assert.Single(await Store.GetBreakdownsAsync([turn.TraceId]));
        Assert.True(row.Value.UsedTranscript);

        // The local log is gone; a fresh service (e.g. after a restart) still shows the original numbers.
        source.Available = false;
        var trace = (await Insights(source).GetSessionsAsync()).Single().Traces.Single();
        var call = trace.LlmCalls.Single();

        Assert.True(trace.FromSavedBreakdown);
        Assert.Equal("Fake log", call.PromptSource);
        Assert.Equal(first.Composition.Get(TokenCategory.ToolResults), call.Composition!.Get(TokenCategory.ToolResults), 6);
        Assert.Equal(first.Composition.Get(TokenCategory.Overhead), call.Composition.Get(TokenCategory.Overhead), 6);

        // Without the saved row, the numbers fall back to what the telemetry alone shows.
        await Store.ClearAsync();
        await StoreAsync(turn.Spans);
        var recomputed = (await Insights(source).GetSessionsAsync()).Single().Traces.Single().LlmCalls.Single();
        Assert.Null(recomputed.PromptSource);
        Assert.Equal(0, recomputed.Composition!.Get(TokenCategory.ToolResults));
    }

    [Fact]
    public async Task Saved_breakdown_is_recomputed_when_the_trace_changes()
    {
        var turn = Turn("session-1", 0);
        await StoreAsync(turn.Spans);
        await Insights().GetSessionsAsync();
        var before = (await Store.GetBreakdownsAsync([turn.TraceId]))[turn.TraceId].Fingerprint;

        turn.Chat(5, 6, input: 4000, output: 10, turn.Spans[0].SpanId);
        await StoreAsync(turn.Spans);
        var trace = (await Insights().GetSessionsAsync()).Single().Traces.Single();

        Assert.False(trace.FromSavedBreakdown);
        Assert.Equal(2, trace.LlmCalls.Count);
        Assert.NotEqual(before, (await Store.GetBreakdownsAsync([turn.TraceId]))[turn.TraceId].Fingerprint);
    }

    [Fact]
    public async Task New_spans_in_a_session_invalidate_later_traces_only()
    {
        var turn1 = Turn("session-1", 0);
        var turn2 = Turn("session-1", 60);
        await StoreAsync(turn1.Spans.Concat(turn2.Spans));
        var insights = Insights();
        var before = (await insights.GetSessionsAsync()).Single().Traces;

        // A late span for turn 1 changes what turn 2 carries in, so both are recomputed.
        turn1.Tool(3, 4, "read_file", "late", turn1.Spans[0].SpanId, "late result");
        await StoreAsync(turn1.Spans);
        var after = (await insights.GetSessionsAsync()).Single().Traces;

        Assert.NotSame(before.Single(t => t.TraceId == turn1.TraceId), after.Single(t => t.TraceId == turn1.TraceId));
        Assert.NotSame(before.Single(t => t.TraceId == turn2.TraceId), after.Single(t => t.TraceId == turn2.TraceId));

        // A late span for turn 2 leaves turn 1 untouched.
        turn2.Tool(63, 64, "read_file", "late", turn2.Spans[0].SpanId, "late result");
        await StoreAsync(turn2.Spans);
        var last = (await insights.GetSessionsAsync()).Single().Traces;

        Assert.Same(after.Single(t => t.TraceId == turn1.TraceId), last.Single(t => t.TraceId == turn1.TraceId));
        Assert.NotSame(after.Single(t => t.TraceId == turn2.TraceId), last.Single(t => t.TraceId == turn2.TraceId));
    }

    [Fact]
    public async Task Sessions_are_filtered_by_time_range_and_the_cache_is_bounded()
    {
        var old = Turn("old", 0);
        var recent = Turn("recent", TimeSpan.FromDays(10).TotalSeconds);
        var other = Turn("other", TimeSpan.FromDays(11).TotalSeconds);
        await StoreAsync(old.Spans.Concat(recent.Spans).Concat(other.Spans));
        var insights = Insights(cacheSize: 1);

        var inRange = await insights.GetSessionsAsync(Origin.AddDays(5));
        Assert.Equal(["other", "recent"], inRange.Select(s => s.SessionKey).Order());
        Assert.Equal(3, (await insights.GetSessionsAsync()).Count);
        Assert.True(insights.CachedTraceCount <= 1);
    }

    [Fact]
    public async Task Schema_upgrade_adds_new_tables_to_an_existing_database()
    {
        await StoreAsync(Turn("session-1", 0).Spans);
        var factory = _services.GetRequiredService<IDbContextFactory<InspectorDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            // Simulate a database created by an older version.
            await db.Database.ExecuteSqlRawAsync("DROP TABLE TraceBreakdowns; DROP TABLE Settings; DROP INDEX IF EXISTS IX_Spans_EndTime; DROP INDEX IF EXISTS IX_Spans_ClientId;");
        }

        await Store.EnsureCreatedAsync();
        await Store.EnsureCreatedAsync();

        await Store.SetSettingAsync("key", "value");
        Assert.Equal("value", await Store.GetSettingAsync("key"));
        var session = Assert.Single(await Insights().GetSessionsAsync());
        Assert.Single(await Store.GetBreakdownsAsync(session.Traces.Select(t => t.TraceId)));
        Assert.Equal(1, (await Store.GetStatsAsync()).Traces);
    }

    [Fact]
    public async Task Retention_deletes_traces_older_than_the_setting()
    {
        var old = Turn("old", 0);
        var recent = Turn("recent", TimeSpan.FromDays(10).TotalSeconds);
        await StoreAsync(old.Spans.Concat(recent.Spans));
        await Insights().GetSessionsAsync();
        var cleared = 0;
        Events.DataCleared += () => cleared++;

        var retention = new RetentionSettings(Store, Events, configuredDays: 0);
        Assert.Equal(0, await retention.GetDaysAsync());
        Assert.Equal(0, await retention.ApplyAsync(Origin.AddDays(12)));

        var changed = false;
        retention.Changed += () => changed = true;
        await retention.SetDaysAsync(7);
        Assert.True(changed);
        Assert.Equal(7, await new RetentionSettings(Store, Events, configuredDays: 30).GetDaysAsync());

        Assert.Equal(1, await retention.ApplyAsync(Origin.AddDays(12)));
        Assert.Equal(1, cleared);
        var index = await Store.GetTraceIndexAsync();
        Assert.Equal(recent.TraceId, Assert.Single(index).TraceId);
        Assert.Empty(await Store.GetBreakdownsAsync([old.TraceId]));
        Assert.Single(await Store.GetBreakdownsAsync([recent.TraceId]));
    }

    [Fact]
    public async Task Configured_retention_applies_until_overridden()
    {
        var retention = new RetentionSettings(Store, Events, configuredDays: 30);
        Assert.Equal(30, await retention.GetDaysAsync());
        await retention.SetDaysAsync(0);
        Assert.Equal(0, await retention.GetDaysAsync());
    }

    [Fact]
    public async Task Demo_data_is_its_own_client_and_can_be_removed()
    {
        var demo = OtlpJsonTraceParser.Parse(Encoding.UTF8.GetBytes(DemoDataService.BuildPayload(DateTimeOffset.UtcNow.AddMinutes(-10))));
        await StoreAsync(demo, DemoDataService.SourceName);
        await StoreAsync(Turn("real", 0).Spans);

        var sessions = await Insights().GetSessionsAsync();
        Assert.Equal([DemoClientProfile.Id, VsCodeCopilotChatProfile.Id], sessions.Select(s => s.ClientId).Order());
        Assert.Equal("Demo data", _services.GetRequiredService<IClientCatalog>().GetDisplayName(DemoClientProfile.Id));

        var service = new DemoDataService(_services.GetRequiredService<ITelemetryIngestor>(), Store, Events);
        await service.RemoveAsync();

        var remaining = Assert.Single(await Insights().GetSessionsAsync());
        Assert.Equal("real", remaining.SessionKey);
    }

    private sealed class FakeTranscriptSource(string conversationId, string messages) : ITranscriptSource
    {
        public bool Available { get; set; } = true;

        public string Name => "Fake log";

        public bool HasTranscript(string? id) => Available && id == conversationId;

        public TranscriptPrompt? TryGetPrompt(string id, string responseId) =>
            HasTranscript(id) ? new TranscriptPrompt(messages, null, 4, "C:\\fake\\events.jsonl", Name) : null;

        public TranscriptEvents? TryGetEvents(string id, string callId) => null;
    }
}

public class IngestionEventTests
{
    [Fact]
    public void Ingestion_events_report_which_sessions_and_time_range_they_touch()
    {
        var at = new DateTimeOffset(2025, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var e = new SpansIngestedEvent(["t1"], 1, "test", at, ["s1"], at);

        Assert.True(e.Touches(["s1"], []));
        Assert.True(e.Touches([], ["t1"]));
        Assert.False(e.Touches(["s2"], ["t2"]));
        Assert.True(e.InRange(null));
        Assert.True(e.InRange(at.AddDays(-1)));
        Assert.False(e.InRange(at.AddDays(1)));

        // Events without session information are always treated as relevant.
        var unknown = new SpansIngestedEvent(["t1"], 1, "test", at);
        Assert.True(unknown.Touches(["s2"], ["t2"]));
        Assert.True(unknown.InRange(at.AddDays(1)));
    }
}
