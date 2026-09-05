using SysCmd.Core.Events;
using SysCmd.Core.Jobs;

namespace SysCmd.Server.Api;

/// <summary>Converts internal types into the DTOs the API exposes, keeping the wire shape stable.</summary>
public static class ApiMapping
{
    public static JobDto ToDto(this Job job) => new(
        job.Id,
        job.Kind.ToString(),
        job.Title,
        job.Target,
        job.Status.ToString(),
        job.Forced,
        job.MachineId,
        job.ParentJobId,
        job.Error,
        job.CanCancel,
        job.CreatedAt,
        job.StartedAt,
        job.FinishedAt,
        [.. job.Progress.Select(p => $"{p.Timestamp:HH:mm:ss} {p.Message}")]);

    public static JobAccepted ToAccepted(this Job job) => new(job.Id, job.Title, job.Status.ToString());

    public static EventDto ToDto(this LabEvent e) => new(
        e.Timestamp, e.Level.ToString(), e.Category, e.Message, e.MachineId, e.JobId, e.Detail);
}
