using System.Collections.Concurrent;

namespace SysCmd.Core.Jobs;

/// <summary>
/// One lock per machine, so two jobs can never drive the same box at once — a power-on racing a
/// power-off against a vintage MP is a good way to end up in an unknown state.
/// </summary>
public sealed class MachineLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Await exclusive access to a machine; dispose the result to release it.</summary>
    public async Task<IDisposable> AcquireAsync(string machineId, CancellationToken ct)
    {
        var sem = _locks.GetOrAdd(machineId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        return new Release(sem);
    }

    /// <summary>Try to take the lock without waiting; returns null when another job holds it.</summary>
    public IDisposable? TryAcquire(string machineId)
    {
        var sem = _locks.GetOrAdd(machineId, _ => new SemaphoreSlim(1, 1));
        return sem.Wait(0) ? new Release(sem) : null;
    }

    private sealed class Release(SemaphoreSlim sem) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) sem.Release();
        }
    }
}
