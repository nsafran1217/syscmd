using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SysCmd.Core.Jobs;

/// <summary>The work a job performs. Progress is reported through the job itself.</summary>
public delegate Task JobHandler(Job job, CancellationToken ct);

/// <summary>
/// Accepts jobs and hands them to <see cref="JobRunner"/> over a channel, retaining recent
/// completed jobs so the UI and API can report on work that has already finished.
/// </summary>
public sealed class JobQueue
{
    private const int HistoryLimit = 200;

    private readonly Channel<QueuedJob> _channel = Channel.CreateUnbounded<QueuedJob>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly ConcurrentQueue<string> _order = new();

    internal record QueuedJob(Job Job, JobHandler Handler);

    /// <summary>Raised when a job is created or changes state, for live UI updates.</summary>
    public event Action<Job>? JobChanged;

    public Job Enqueue(
        JobKind kind, string title, string target, JobHandler handler,
        string? machineId = null, bool forced = false, string? parentJobId = null)
    {
        var job = new Job
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            Kind = kind,
            Title = title,
            Target = target,
            MachineId = machineId,
            Forced = forced,
            ParentJobId = parentJobId,
        };

        job.Updated += j => JobChanged?.Invoke(j);
        _jobs[job.Id] = job;
        _order.Enqueue(job.Id);
        Trim();

        _channel.Writer.TryWrite(new QueuedJob(job, handler));
        JobChanged?.Invoke(job);
        return job;
    }

    internal IAsyncEnumerable<QueuedJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    public Job? Get(string id) => _jobs.GetValueOrDefault(id);

    /// <summary>
    /// Stop a running or queued job. A group's children are stopped with it, since leaving them
    /// running after killing the parent would be surprising.
    /// </summary>
    public bool Cancel(string id)
    {
        if (_jobs.GetValueOrDefault(id) is not { } job || job.IsComplete) return false;

        job.RequestCancel();
        foreach (var child in _jobs.Values.Where(j => j.ParentJobId == id && !j.IsComplete))
            child.RequestCancel();

        return true;
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<Job> All(int limit = 100)
        => _jobs.Values.OrderByDescending(j => j.CreatedAt).Take(limit).ToList();

    public IReadOnlyList<Job> Active()
        => _jobs.Values.Where(j => !j.IsComplete).OrderBy(j => j.CreatedAt).ToList();

    /// <summary>The in-flight job touching a machine, if any. Drives the amber LED in the UI.</summary>
    public Job? ActiveForMachine(string machineId)
        => _jobs.Values.FirstOrDefault(j => !j.IsComplete && j.MachineId == machineId);

    /// <summary>The in-flight job touching an outlet, whether addressed by machine or directly.</summary>
    public Job? ActiveForOutlet(string pduId, int outlet)
        => _jobs.Values.FirstOrDefault(j => !j.IsComplete && j.Target == $"{pduId}:{outlet}");

    /// <summary>
    /// Drop the oldest completed jobs once history grows past the cap. Running jobs are pushed to
    /// the back rather than evicted; the attempt counter stops that from spinning when every
    /// retained job is still in flight.
    /// </summary>
    private void Trim()
    {
        var attempts = _order.Count;
        while (attempts-- > 0 && _order.Count > HistoryLimit && _order.TryDequeue(out var id))
        {
            if (!_jobs.TryGetValue(id, out var job)) continue;
            if (job.IsComplete) _jobs.TryRemove(id, out _);
            else _order.Enqueue(id);
        }
    }
}
