using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using ToolCallsInspector.Ingestion.Otlp;

namespace ToolCallsInspector.Tests;

public class OtlpProtobufTraceParserTests
{
    [Fact]
    public void Round_trips_a_protobuf_export_request()
    {
        var span = new Span
        {
            TraceId = ByteString.CopyFrom(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10")),
            SpanId = ByteString.CopyFrom(Convert.FromHexString("a1a2a3a4a5a6a7a8")),
            ParentSpanId = ByteString.CopyFrom(Convert.FromHexString("b1b2b3b4b5b6b7b8")),
            Name = "execute_tool read_file",
            StartTimeUnixNano = 1_735_732_800_000_000_000UL,
            EndTimeUnixNano = 1_735_732_800_250_000_000UL,
            Status = new Status { Code = Status.Types.StatusCode.Ok },
        };
        span.Attributes.Add(new KeyValue { Key = "gen_ai.tool.name", Value = new AnyValue { StringValue = "read_file" } });
        span.Attributes.Add(new KeyValue { Key = "gen_ai.usage.input_tokens", Value = new AnyValue { IntValue = 42 } });

        var request = new ExportTraceServiceRequest();
        var resource = new Resource();
        resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "github-copilot" } });
        var scopeSpans = new ScopeSpans();
        scopeSpans.Spans.Add(span);
        var resourceSpans = new ResourceSpans { Resource = resource };
        resourceSpans.ScopeSpans.Add(scopeSpans);
        request.ResourceSpans.Add(resourceSpans);

        var parsed = Assert.Single(OtlpProtobufTraceParser.Parse(request.ToByteArray()));

        Assert.Equal("0102030405060708090a0b0c0d0e0f10", parsed.TraceId);
        Assert.Equal("a1a2a3a4a5a6a7a8", parsed.SpanId);
        Assert.Equal("b1b2b3b4b5b6b7b8", parsed.ParentSpanId);
        Assert.Equal(250, (parsed.EndTime - parsed.StartTime).TotalMilliseconds);
        Assert.Equal("read_file", parsed.Attributes["gen_ai.tool.name"]);
        Assert.Equal(42L, Convert.ToInt64(parsed.Attributes["gen_ai.usage.input_tokens"]));
        Assert.Equal("github-copilot", parsed.ResourceAttributes["service.name"]);
    }
}
