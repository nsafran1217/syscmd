using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SysCmd.Core.Events;

namespace SysCmd.Core.Jobs;

/// <summary>
/// Drains the job channel with a small pool of workers. Vintage management processors are slow,
/// so several jobs run concurrently, but never two against the same machine: that serialisation
/// is enforced by <see cref="MachineLocks"/>.
/// </summary>
public sealed class JobRunner(JobQueue queue, EventLog events, ILogger<JobRunner> log) : BackgroundService
{
    private const int WorkerCount = 4;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, WorkerCount).Select(i => Worker(i, stoppingToken));
        return Task.WhenAll(workers);
    }

    private async Task Worker(int index, CancellationToken ct)
    {
        log.LogDebug("Job worker {Index} started", index);

        await foreach (var queued in queue.ReadAllAsync(ct))
        {
            var job = queued.Job;

            // Someone may have stopped it while it sat in the queue.
            if (job.CancelRequested)
            {
                job.Status = JobStatus.Cancelled;
                job.Error = "Stopped before it started.";
                job.FinishedAt = DateTimeOffset.Now;
                job.RaiseUpdated();
                events.Warn("job", $"Stopped before it started: {job.Title}", job.MachineId, job.Id);
                continue;
            }

            // Linked so the job can be stopped on its own, or by the host shutting down.
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            job.Cancellation = cancellation;

            job.Status = JobStatus.Running;
            job.StartedAt = DateTimeOffset.Now;
            job.RaiseUpdated();

            events.Info("job", $"Started: {job.Title}", job.MachineId, job.Id);

            try
            {
                await queued.Handler(job, cancellation.Token);
                job.Status = JobStatus.Succeeded;
                events.Info("job", $"Completed: {job.Title} ({job.Elapsed.TotalSeconds:F0}s)", job.MachineId, job.Id);
            }
            catch (OperationCanceledException) when (job.CancelRequested && !ct.IsCancellationRequested)
            {
                job.Status = JobStatus.Cancelled;
                job.Error = "Stopped on request. Any outlet this job had not yet switched was left alone.";
                events.Warn("job", $"Stopped: {job.Title}", job.MachineId, job.Id,
                    detail: string.Join("\n", job.Progress.Select(p => $"{p.Timestamp:HH:mm:ss}  {p.Message}")));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                job.Status = JobStatus.Cancelled;
                job.Error = "Cancelled because the application is shutting down.";
                events.Warn("job", $"Cancelled: {job.Title}", job.MachineId, job.Id);
            }
            catch (Exception ex)
            {
                job.Status = JobStatus.Failed;
                job.Error = ex.Message;
                events.Error("job", $"Failed: {job.Title} — {ex.Message}", job.MachineId, job.Id,
                    detail: string.Join("\n", job.Progress.Select(p => $"{p.Timestamp:HH:mm:ss}  {p.Message}")));
            }
            finally
            {
                job.Cancellation = null;
                job.FinishedAt = DateTimeOffset.Now;
                job.RaiseUpdated();
            }
        }
    }
}
