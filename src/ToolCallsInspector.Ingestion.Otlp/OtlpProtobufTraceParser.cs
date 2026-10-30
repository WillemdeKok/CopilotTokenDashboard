using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using ToolCallsInspector.Core.Telemetry;

namespace ToolCallsInspector.Ingestion.Otlp;

/// <summary>Parses OTLP/protobuf <c>ExportTraceServiceRequest</c> payloads (used by Copilot CLI and most OTel SDKs).</summary>
public static class OtlpProtobufTraceParser
{
    public static IReadOnlyList<RawSpan> Parse(byte[] payload)
    {
        var request = ExportTraceServiceRequest.Parser.ParseFrom(payload);
        var spans = new List<RawSpan>();

        foreach (var rs in request.ResourceSpans)
        {
            var resourceAttrs = ToDictionary(rs.Resource?.Attributes);
            foreach (var ss in rs.ScopeSpans)
            {
                foreach (var s in ss.Spans)
                {
                    spans.Add(new RawSpan
                    {
                        TraceId = OtlpIds.FromBytes(s.TraceId.Span) ?? "unknown",
                        SpanId = OtlpIds.FromBytes(s.SpanId.Span) ?? Guid.NewGuid().ToString("N")[..16],
                        ParentSpanId = OtlpIds.FromBytes(s.ParentSpanId.Span),
                        Name = s.Name,
                        Kind = (int)s.Kind,
                        StartTime = OtlpIds.FromUnixNanos(s.StartTimeUnixNano),
                        EndTime = OtlpIds.FromUnixNanos(s.EndTimeUnixNano),
                        StatusCode = (int)(s.Status?.Code ?? 0),
                        StatusMessage = string.IsNullOrEmpty(s.Status?.Message) ? null : s.Status!.Message,
                        ScopeName = ss.Scope?.Name,
                        Attributes = ToDictionary(s.Attributes),
                        ResourceAttributes = resourceAttrs,
                        Events = s.Events.Select(e => new RawSpanEvent
                        {
                            Name = e.Name,
                            Time = OtlpIds.FromUnixNanos(e.TimeUnixNano),
                            Attributes = ToDictionary(e.Attributes),
                        }).ToList(),
                    });
                }
            }
        }

        return spans;
    }

    private static Dictionary<string, object?> ToDictionary(IEnumerable<KeyValue>? attributes)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (attributes is null)
        {
            return result;
        }

        foreach (var kv in attributes)
        {
            result[kv.Key] = Convert(kv.Value);
        }

        return result;
    }

    private static object? Convert(AnyValue? value) => value?.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => value.StringValue,
        AnyValue.ValueOneofCase.BoolValue => value.BoolValue,
        AnyValue.ValueOneofCase.IntValue => value.IntValue,
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue,
        AnyValue.ValueOneofCase.BytesValue => System.Convert.ToBase64String(value.BytesValue.Span),
        AnyValue.ValueOneofCase.ArrayValue => value.ArrayValue.Values.Select(Convert).ToList(),
        AnyValue.ValueOneofCase.KvlistValue => ToDictionary(value.KvlistValue.Values),
        _ => null,
    };
}
