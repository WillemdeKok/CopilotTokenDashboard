namespace ToolCallsInspector.Core.Telemetry;

/// <summary>
/// Entry point for every telemetry source. Push sources (the OTLP HTTP receiver) call this directly;
/// pull sources (<see cref="ITelemetrySource"/>) receive it when they are started.
/// </summary>
public interface ITelemetryIngestor
{
    Task<int> IngestAsync(IReadOnlyList<RawSpan> spans, string sourceName, CancellationToken cancellationToken = default);
}

/// <summary>
/// A pull-based telemetry source (e.g. tailing Copilot CLI session files). Implementations are
/// registered in DI and started by the host; they convert whatever they read into <see cref="RawSpan"/>s.
/// </summary>
public interface ITelemetrySource
{
    string Name { get; }

    Task RunAsync(ITelemetryIngestor ingestor, CancellationToken cancellationToken);
}
