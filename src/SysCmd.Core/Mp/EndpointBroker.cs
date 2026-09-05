using System.Collections.Concurrent;

namespace SysCmd.Core.Mp;

/// <summary>Raised when an endpoint is already in use and the caller would not wait.</summary>
public sealed class EndpointBusyException(NetworkEndpoint endpoint, string? holder)
    : Exception($"{endpoint.Description} ({endpoint}) is already in use{(holder is null ? "" : $" by {holder}")}.")
{
    public NetworkEndpoint Endpoint { get; } = endpoint;
    public string? Holder { get; } = holder;
}

/// <summary>
/// Serialises access to console endpoints. A serial console server port accepts exactly one TCP
/// session, so an open browser console and a power job must not both grab it. Everything that
/// opens a session — MP driver and console bridge alike — takes a lease first.
/// </summary>
public sealed class EndpointBroker
{
    private sealed class Slot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? Holder;
    }

    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Who currently holds this endpoint, or null when it is free.</summary>
    public string? HolderOf(NetworkEndpoint endpoint)
        => _slots.TryGetValue(endpoint.Key, out var slot) ? slot.Holder : null;

    public bool IsBusy(NetworkEndpoint endpoint) => HolderOf(endpoint) is not null;

    /// <summary>
    /// Take the endpoint for <paramref name="holder"/>. Waits up to <paramref name="wait"/>, then
    /// throws <see cref="EndpointBusyException"/> rather than blocking a job forever behind an
    /// idle console window.
    ///
    /// Set <paramref name="exclusive"/> to false for devices that genuinely accept concurrent
    /// sessions - an HP Integrity MP at its own address, for instance - so an open console window
    /// does not needlessly block a power job. It must stay true for anything reached through a
    /// console server, which is one physical serial wire.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireAsync(
        NetworkEndpoint endpoint, string holder, TimeSpan wait, CancellationToken ct, bool exclusive = true)
    {
        if (!exclusive) return NullLease.Instance;

        var slot = _slots.GetOrAdd(endpoint.Key, _ => new Slot());
        if (!await slot.Gate.WaitAsync(wait, ct))
            throw new EndpointBusyException(endpoint, slot.Holder);

        slot.Holder = holder;
        return new Lease(slot);
    }

    /// <summary>Used when the device tolerates concurrent sessions and no gating is needed.</summary>
    private sealed class NullLease : IAsyncDisposable
    {
        public static readonly NullLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Lease(Slot slot) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                slot.Holder = null;
                slot.Gate.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
