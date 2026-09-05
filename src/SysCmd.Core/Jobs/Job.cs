using System.Collections.Concurrent;

namespace SysCmd.Core.Jobs;

public enum JobStatus { Queued, Running, Succeeded, Failed, Cancelled }

public enum JobKind { MachinePower, OutletControl, GroupPower }

/// <summary>
/// A unit of work the UI can watch: powering a machine, switching an outlet, or fanning out to a
/// group. Jobs live in memory only; their outcome is recorded in the event log.
/// </summary>
public sealed class Job
{
    private readonly ConcurrentQueue<JobProgress> _progress = new();

    public required string Id { get; init; }
    public required JobKind Kind { get; init; }
    public required string Title { get; init; }

    /// <summary>Machine id, "pduId:outlet", or group id, depending on <see cref="Kind"/>.</summary>
    public required string Target { get; init; }

    public string? MachineId { get; init; }
    public string? ParentJobId { get; init; }

    /// <summary>True when the operator asked to skip management-processor safety checks.</summary>
    public bool Forced { get; init; }

    public JobStatus Status { get; internal set; } = JobStatus.Queued;
    public string? Error { get; internal set; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }

    public bool IsComplete => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;

    /// <summary>True once someone has asked for this job to stop.</summary>
    public bool CancelRequested { get; private set; }

    /// <summary>Whether offering a stop button makes sense right now.</summary>
    public bool CanCancel => !IsComplete && !CancelRequested;
    public TimeSpan Elapsed => (FinishedAt ?? DateTimeOffset.Now) - (StartedAt ?? CreatedAt);

    public IReadOnlyList<JobProgress> Progress => [.. _progress];

    /// <summary>Latest progress line, for the compact view on an outlet button.</summary>
    public string? CurrentStep => _progress.LastOrDefault()?.Message;

    /// <summary>
    /// Cancels this job's work. Created by the runner when the job starts, so a job stopped while
    /// still queued is caught by <see cref="CancelRequested"/> instead.
    /// </summary>
    internal CancellationTokenSource? Cancellation { get; set; }

    /// <summary>Raised whenever status or progress changes, so the UI can re-render.</summary>
    public event Action<Job>? Updated;

    /// <summary>
    /// Ask the job to stop. Safe at any point: the orchestration only cuts an outlet after a
    /// machine has confirmed it is off, so an abandoned power-off leaves the outlet on.
    /// </summary>
    internal void RequestCancel()
    {
        if (IsComplete) return;
        CancelRequested = true;
        Report("Stop requested");
        try { Cancellation?.Cancel(); }
        catch (ObjectDisposedException) { /* it finished as we asked */ }
        RaiseUpdated();
    }

    internal void Report(string message)
    {
        _progress.Enqueue(new JobProgress(DateTimeOffset.Now, message));
        Updated?.Invoke(this);
    }

    internal void RaiseUpdated() => Updated?.Invoke(this);
}

public sealed record JobProgress(DateTimeOffset Timestamp, string Message);
