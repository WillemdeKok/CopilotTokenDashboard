using System.Text;
using ToolCallsInspector.Ingestion.Otlp;

namespace ToolCallsInspector.Tests;

public class OtlpJsonTraceParserTests
{
    private const string Payload = """
        {
          "resourceSpans": [{
            "resource": { "attributes": [ { "key": "service.name", "value": { "stringValue": "copilot-chat" } } ] },
            "scopeSpans": [{
              "scope": { "name": "copilot-chat" },
              "spans": [{
                "traceId": "0102030405060708090a0b0c0d0e0f10",
                "spanId": "a1a2a3a4a5a6a7a8",
                "parentSpanId": "",
                "name": "chat gpt-4o",
                "kind": 3,
                "startTimeUnixNano": "1735732800000000000",
                "endTimeUnixNano": "1735732801500000000",
                "status": { "code": 2, "message": "boom" },
                "attributes": [
                  { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "1234" } },
                  { "key": "gen_ai.usage.output_tokens", "value": { "intValue": 56 } },
                  { "key": "ratio", "value": { "doubleValue": 0.5 } },
                  { "key": "flag", "value": { "boolValue": true } },
                  { "key": "gen_ai.response.finish_reasons", "value": { "arrayValue": { "values": [ { "stringValue": "stop" }, { "stringValue": "length" } ] } } },
                  { "key": "nested", "value": { "kvlistValue": { "values": [ { "key": "a", "value": { "stringValue": "b" } } ] } } }
                ],
                "events": [ { "name": "user_message", "timeUnixNano": "1735732800100000000", "attributes": [ { "key": "content", "value": { "stringValue": "hi" } } ] } ]
              }]
            }]
          }]
        }
        """;

    [Fact]
    public void Parses_ids_times_status_and_attributes()
    {
        var span = Assert.Single(OtlpJsonTraceParser.Parse(Encoding.UTF8.GetBytes(Payload)));

        Assert.Equal("0102030405060708090a0b0c0d0e0f10", span.TraceId);
        Assert.Equal("a1a2a3a4a5a6a7a8", span.SpanId);
        Assert.True(string.IsNullOrEmpty(span.ParentSpanId));
        Assert.Equal("chat gpt-4o", span.Name);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero), span.StartTime);
        Assert.Equal(1500, (span.EndTime - span.StartTime).TotalMilliseconds);
        Assert.Equal(2, span.StatusCode);
        Assert.Equal("boom", span.StatusMessage);
        Assert.Equal("copilot-chat", span.ResourceAttributes["service.name"]);
        Assert.Equal(1234L, Convert.ToInt64(span.Attributes["gen_ai.usage.input_tokens"]));
        Assert.Equal(56L, Convert.ToInt64(span.Attributes["gen_ai.usage.output_tokens"]));
        Assert.Equal(0.5, span.Attributes["ratio"]);
        Assert.Equal(true, span.Attributes["flag"]);
        Assert.Equal(["stop", "length"], ((IEnumerable<object?>)span.Attributes["gen_ai.response.finish_reasons"]!).Cast<string>());
        Assert.NotNull(span.Attributes["nested"]);
        var evt = Assert.Single(span.Events);
        Assert.Equal("user_message", evt.Name);
        Assert.Equal("hi", evt.Attributes["content"]);
    }

    [Fact]
    public void Accepts_base64_ids()
    {
        var json = """
            {"resourceSpans":[{"scopeSpans":[{"spans":[{"traceId":"AQIDBAUGBwgJCgsMDQ4PEA==","spanId":"oaKjpKWmp6g=","name":"x","startTimeUnixNano":"0","endTimeUnixNano":"0"}]}]}]}
            """;
        var span = Assert.Single(OtlpJsonTraceParser.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", span.TraceId);
        Assert.Equal("a1a2a3a4a5a6a7a8", span.SpanId);
    }

    [Fact]
    public void Empty_payload_yields_no_spans() =>
        Assert.Empty(OtlpJsonTraceParser.Parse(Encoding.UTF8.GetBytes("""{"resourceSpans":[]}""")));
}
