using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ToolCallsInspector.Core.Analysis;
using ToolCallsInspector.Core.Storage;
using ToolCallsInspector.Ingestion.Otlp;
using ToolCallsInspector.Web.Services;

namespace ToolCallsInspector.Tests;

public class DemoPayloadTests
{
    [Fact]
    public void Demo_payload_analyzes_into_one_session_with_consistent_attribution()
    {
        var json = DemoDataService.BuildPayload(DateTimeOffset.UtcNow.AddMinutes(-10));
        var spans = OtlpJsonTraceParser.Parse(Encoding.UTF8.GetBytes(json));

        var traces = SpanFactory.Analyze(spans);

        Assert.Single(traces.Select(t => t.SessionKey).Distinct());
        Assert.All(traces, t => Assert.Equal(Core.Clients.DemoClientProfile.Id, t.ClientId));
        Assert.Contains(traces, t => t.IsBackground);
        Assert.Equal(2, traces.Count(t => !t.IsBackground));
        Assert.All(traces.Where(t => !t.IsBackground), t => Assert.False(string.IsNullOrEmpty(t.Prompt)));

        foreach (var call in traces.SelectMany(t => t.LlmCalls))
        {
            Assert.NotNull(call.Composition);
            Assert.Equal(call.Span.InputTokens!.Value, call.Composition!.Total, 1);
        }

        // Every tool call is linked to the LLM call that requested it.
        Assert.All(traces.SelectMany(t => t.ToolCalls), t => Assert.NotNull(t.RequestedBy));

        // Both attribution modes are exercised.
        var methods = traces.SelectMany(t => t.LlmCalls).Select(c => c.Composition!.Method).Distinct().ToList();
        Assert.Contains(AttributionMethod.Content, methods);
        Assert.Contains(AttributionMethod.Delta, methods);

        // The sub-agent is nested under its runSubagent tool call.
        var sub = traces.SelectMany(t => t.All()).Single(n => n.Category == SpanCategory.Agent && n.Parent is not null);
        Assert.Equal("runSubagent", sub.Parent!.Span.ToolName);
        Assert.NotNull(sub.Parent.SubAgent);
        Assert.True(sub.Parent.SubAgent!.LlmCalls > 0);
    }
}

public sealed class OtlpEndpointTests : IClassFixture<OtlpEndpointTests.Factory>, IAsyncLifetime
{
    private readonly Factory _factory;

    public OtlpEndpointTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), "tci-tests", Guid.NewGuid().ToString("N"), "inspector.db");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
            builder.UseSetting("Inspector:DatabasePath", DatabasePath);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path.GetDirectoryName(DatabasePath)!, recursive: true); } catch (IOException) { }
        }
    }

    public async Task InitializeAsync()
    {
        await _factory.Services.GetRequiredService<ISpanStore>().ClearAsync();
        _factory.Services.GetRequiredService<Core.Ingestion.InspectorEvents>().PublishCleared();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Uses_the_configured_database_path() =>
        Assert.Equal(_factory.DatabasePath, _factory.Services.GetRequiredService<InspectorOptions>().DatabasePath);

    [Fact]
    public async Task Json_export_is_ingested_and_visible_in_ui()
    {
        var client = _factory.CreateClient();
        var json = DemoDataService.BuildPayload(DateTimeOffset.UtcNow.AddMinutes(-5));

        var response = await client.PostAsync("/v1/traces", new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());

        var insights = _factory.Services.GetRequiredService<InsightsService>();
        var session = Assert.Single(await insights.GetSessionsAsync());
        Assert.True(session.Totals.LlmCalls >= 8);
        Assert.True(session.Totals.ToolCalls >= 5);

        Assert.Equal(Core.Clients.DemoClientProfile.Id, session.ClientId);
        var trace = session.Traces.First(t => !t.IsBackground);
        var llmCall = trace.LlmCalls[0];
        var subAgentTool = session.Traces.SelectMany(t => t.ToolCalls).Single(t => t.SubAgent is not null);
        foreach (var url in new[]
                 {
                     "/", "/sessions", "/tools", "/setup", "/traces", "/traces?view=calls",
                     "/" + Fmt.SessionUrl(session.SessionKey), "/" + Fmt.SpanUrl(llmCall.Span.Id),
                     "/" + Fmt.TraceUrl(trace.TraceId), "/" + Fmt.TraceUrl(trace.TraceId, llmCall.Span.Id),
                     "/" + Fmt.SpanUrl(subAgentTool.Span.Id),
                 })
        {
            var page = await client.GetAsync(url);
            Assert.True(page.StatusCode == HttpStatusCode.OK, $"{url} returned {page.StatusCode}");
        }

        var traceHtml = await client.GetStringAsync("/" + Fmt.TraceUrl(trace.TraceId, llmCall.Span.Id));
        Assert.Contains("wf-bar llm", traceHtml);
        Assert.Contains("Response", traceHtml);
        Assert.Contains(trace.TraceId, await client.GetStringAsync("/traces"));

        Assert.Contains("Sub-agent tokens", await client.GetStringAsync("/" + Fmt.SpanUrl(subAgentTool.Span.Id)));
        var sessionHtml = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/" + Fmt.SessionUrl(session.SessionKey)));
        Assert.Contains("T1·#1", sessionHtml);
        Assert.Contains("sub-agent call", sessionHtml);
        Assert.Contains("demo", await client.GetStringAsync("/sessions"));
    }

    [Fact]
    public async Task Sessions_without_llm_or_tool_calls_are_hidden()
    {
        var client = _factory.CreateClient();
        var now = DateTimeOffset.UtcNow;
        var start = now.AddSeconds(-2).ToUnixTimeMilliseconds() * 1_000_000;
        var end = now.ToUnixTimeMilliseconds() * 1_000_000;
        var timingOnly = $$$"""
            {"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"github-copilot"}}]},
             "scopeSpans":[{"scope":{"name":"github.copilot"},"spans":[
              {"traceId":"0123456789abcdef0123456789abcdef","spanId":"0123456789abcdef","name":"session.provisioning","kind":1,"startTimeUnixNano":"{{{start}}}","endTimeUnixNano":"{{{end}}}"},
              {"traceId":"0123456789abcdef0123456789abcdef","spanId":"1123456789abcdef","parentSpanId":"0123456789abcdef","name":"session.timing.session_construction","kind":1,"startTimeUnixNano":"{{{start}}}","endTimeUnixNano":"{{{end}}}"}
             ]}]}]}
            """;

        (await client.PostAsync("/v1/traces", new StringContent(timingOnly, Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
        var insights = _factory.Services.GetRequiredService<InsightsService>();
        Assert.Empty(await insights.GetSessionsAsync());

        var demo = DemoDataService.BuildPayload(now.AddMinutes(-5));
        (await client.PostAsync("/v1/traces", new StringContent(demo, Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
        var session = Assert.Single(await insights.GetSessionsAsync());
        Assert.True(session.Totals.LlmCalls > 0);
    }

    [Fact]
    public async Task Gzipped_payload_is_accepted()
    {
        var client = _factory.CreateClient();
        var json = Encoding.UTF8.GetBytes(DemoDataService.BuildPayload(DateTimeOffset.UtcNow));
        using var buffer = new MemoryStream();
        await using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            await gzip.WriteAsync(json);
        }

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new("application/json");
        content.Headers.ContentEncoding.Add("gzip");

        var response = await client.PostAsync("/v1/traces", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Clearing_removes_all_data_and_compacts_the_database()
    {
        var client = _factory.CreateClient();
        var json = DemoDataService.BuildPayload(DateTimeOffset.UtcNow.AddMinutes(-5));
        (await client.PostAsync("/v1/traces", new StringContent(json, Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();

        var store = _factory.Services.GetRequiredService<ISpanStore>();
        var insights = _factory.Services.GetRequiredService<InsightsService>();
        Assert.True((await store.GetStatsAsync()).Spans > 0);
        Assert.NotEmpty(await insights.GetSessionsAsync());

        await store.ClearAsync();
        _factory.Services.GetRequiredService<Core.Ingestion.InspectorEvents>().PublishCleared();

        Assert.Equal(new StoreStats(0, 0), await store.GetStatsAsync());
        Assert.Empty(await insights.GetSessionsAsync());
        Assert.False(File.Exists(_factory.DatabasePath + "-wal") && new FileInfo(_factory.DatabasePath + "-wal").Length > 0, "WAL should be truncated");
    }

    [Fact]
    public async Task Malformed_payload_returns_400()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/v1/traces", new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Logs_and_metrics_are_acknowledged()
    {
        var client = _factory.CreateClient();
        foreach (var path in new[] { "/v1/logs", "/v1/metrics" })
        {
            var response = await client.PostAsync(path, new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
