using Microsoft.AspNetCore.Components;

namespace SysCmd.Server.Components.Shared;

/// <summary>
/// Base for pages that show live hardware state. Reloads on a timer while the circuit is open,
/// and only once the component is actually interactive — during prerender there is no point
/// starting a refresh loop that is about to be thrown away.
/// </summary>
public abstract class LivePage : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>How often to re-read state. Fast enough to feel live, slow enough not to flood SNMP.</summary>
    protected virtual TimeSpan RefreshInterval => TimeSpan.FromSeconds(3);

    /// <summary>Load everything the page renders. Called on the timer and on demand.</summary>
    protected abstract Task LoadAsync(CancellationToken ct);

    /// <summary>Hook for subscribing to service events. Unsubscribe in <see cref="Unsubscribe"/>.</summary>
    protected virtual void Subscribe() { }
    protected virtual void Unsubscribe() { }

    protected CancellationToken PageToken => _cts?.Token ?? CancellationToken.None;

    protected override async Task OnInitializedAsync()
    {
        _cts = new CancellationTokenSource();

        try { await LoadAsync(_cts.Token); }
        catch (OperationCanceledException) { }

        if (!RendererInfo.IsInteractive) return;

        Subscribe();
        _loop = RefreshLoopAsync(_cts.Token);
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await LoadAsync(ct);
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) { /* the circuit went away */ }
        catch (ObjectDisposedException) { /* same */ }
    }

    /// <summary>Re-read immediately, for use right after the user does something.</summary>
    protected async Task RefreshNowAsync()
    {
        try
        {
            await LoadAsync(PageToken);
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Marshal a service callback raised on a background thread onto the render thread.</summary>
    protected void RequestRender()
    {
        try { _ = InvokeAsync(StateHasChanged); }
        catch (ObjectDisposedException) { /* the circuit is gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        Unsubscribe();

        if (_cts is not null) await _cts.CancelAsync();
        if (_loop is not null) { try { await _loop; } catch { /* shutting down */ } }
        _cts?.Dispose();

        GC.SuppressFinalize(this);
    }
}
