using Microsoft.AspNetCore.Components;
using ToolCallsInspector.Core.Ingestion;

namespace ToolCallsInspector.Web.Services;

/// <summary>Base for pages that reload automatically (debounced) whenever new telemetry is ingested.</summary>
public abstract class LiveComponentBase : ComponentBase, IDisposable
{
    private CancellationTokenSource? _debounce;

    [Inject] protected InspectorEvents Events { get; set; } = default!;

    protected bool Loading { get; private set; } = true;

    protected DateTimeOffset? LastUpdate { get; private set; }

    protected abstract Task LoadAsync();

    protected virtual bool IsRelevant(SpansIngestedEvent e) => true;

    protected override async Task OnInitializedAsync()
    {
        Events.SpansIngested += OnSpansIngested;
        Events.DataCleared += OnCleared;
        await ReloadAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_parametersSetOnce)
        {
            _parametersSetOnce = true;
            return;
        }

        await ReloadAsync();
    }

    private bool _parametersSetOnce;

    protected async Task ReloadAsync()
    {
        await LoadAsync();
        Loading = false;
        LastUpdate = DateTimeOffset.UtcNow;
    }

    private void OnSpansIngested(SpansIngestedEvent e)
    {
        if (IsRelevant(e))
        {
            ScheduleRefresh();
        }
    }

    private void OnCleared() => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMilliseconds(400), cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled)
            {
                return;
            }

            await InvokeAsync(async () =>
            {
                await ReloadAsync();
                StateHasChanged();
            });
        }, TaskScheduler.Default);
    }

    public virtual void Dispose()
    {
        Events.SpansIngested -= OnSpansIngested;
        Events.DataCleared -= OnCleared;
        _debounce?.Cancel();
        GC.SuppressFinalize(this);
    }
}
