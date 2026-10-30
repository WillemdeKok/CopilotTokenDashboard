using ToolCallsInspector.Core.Telemetry;

namespace ToolCallsInspector.Web.Services;

public sealed record InspectorOptions(string DatabasePath);

/// <summary>Starts every registered pull-based <see cref="ITelemetrySource"/> (e.g. future Copilot CLI file readers).</summary>
public sealed class TelemetrySourceHost(IEnumerable<ITelemetrySource> sources, ITelemetryIngestor ingestor, ILogger<TelemetrySourceHost> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(sources.Select(source => RunSourceAsync(source, stoppingToken)));

    private async Task RunSourceAsync(ITelemetrySource source, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("Starting telemetry source {Source}", source.Name);
                await source.RunAsync(ingestor, stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Telemetry source {Source} failed; restarting in 5s", source.Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
