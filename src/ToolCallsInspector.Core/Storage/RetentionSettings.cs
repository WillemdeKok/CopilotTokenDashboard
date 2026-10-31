using ToolCallsInspector.Core.Ingestion;

namespace ToolCallsInspector.Core.Storage;

/// <summary>
/// How long telemetry is kept. The value saved from the Setup page wins over the configured default
/// (<c>Inspector:RetentionDays</c>); 0 keeps everything.
/// </summary>
public sealed class RetentionSettings(ISpanStore store, InspectorEvents events, int configuredDays = 0)
{
    public const string SettingKey = "RetentionDays";

    private int? _days;

    public event Action? Changed;

    public int ConfiguredDays { get; } = Math.Max(0, configuredDays);

    public async Task<int> GetDaysAsync(CancellationToken cancellationToken = default)
    {
        if (_days is { } days)
        {
            return days;
        }

        var saved = await store.GetSettingAsync(SettingKey, cancellationToken);
        _days = int.TryParse(saved, out var parsed) && parsed >= 0 ? parsed : ConfiguredDays;
        return _days.Value;
    }

    public async Task SetDaysAsync(int days, CancellationToken cancellationToken = default)
    {
        days = Math.Max(0, days);
        await store.SetSettingAsync(SettingKey, days.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        _days = days;
        Changed?.Invoke();
    }

    /// <summary>Deletes traces that ended before the retention window; returns the number removed (0 when retention is off).</summary>
    public async Task<int> ApplyAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var days = await GetDaysAsync(cancellationToken);
        if (days <= 0)
        {
            return 0;
        }

        var removed = await store.DeleteOlderThanAsync(now.AddDays(-days), cancellationToken);
        if (removed > 0)
        {
            events.PublishCleared();
        }

        return removed;
    }
}
