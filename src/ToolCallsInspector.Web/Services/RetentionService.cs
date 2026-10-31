using ToolCallsInspector.Core.Storage;

namespace ToolCallsInspector.Web.Services;

/// <summary>Deletes telemetry older than the retention setting: at startup, hourly, and whenever the setting changes.</summary>
public sealed class RetentionService(RetentionSettings settings, ILogger<RetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _wake = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        settings.Changed += OnChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var removed = await settings.ApplyAsync(DateTimeOffset.UtcNow, stoppingToken);
                    if (removed > 0)
                    {
                        logger.LogInformation("Retention removed {Count} old trace(s).", removed);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Retention cleanup failed.");
                }

                await _wake.WaitAsync(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            settings.Changed -= OnChanged;
        }
    }

    private void OnChanged()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }
}
