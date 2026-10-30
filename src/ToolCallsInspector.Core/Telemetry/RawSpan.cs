namespace ToolCallsInspector.Core.Telemetry;

/// <summary>
/// Transport-neutral span representation. Every ingestion source (OTLP JSON, OTLP protobuf,
/// future file/hook based sources) converts its payload into this shape before normalization.
/// Attribute values are one of: string, long, double, bool, IReadOnlyList&lt;object?&gt;, IReadOnlyDictionary&lt;string, object?&gt;.
/// </summary>
public sealed class RawSpan
{
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
    public string? ParentSpanId { get; init; }
    public required string Name { get; init; }
    public int Kind { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public int StatusCode { get; init; }
    public string? StatusMessage { get; init; }
    public string? ScopeName { get; init; }
    public IReadOnlyDictionary<string, object?> Attributes { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyDictionary<string, object?> ResourceAttributes { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyList<RawSpanEvent> Events { get; init; } = [];
}

public sealed class RawSpanEvent
{
    public required string Name { get; init; }
    public DateTimeOffset Time { get; init; }
    public IReadOnlyDictionary<string, object?> Attributes { get; init; } = new Dictionary<string, object?>();
}
