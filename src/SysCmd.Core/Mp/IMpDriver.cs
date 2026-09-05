using SysCmd.Core.Configuration;

namespace SysCmd.Core.Mp;

/// <summary>Outcome of one management-processor task, including the session transcript.</summary>
public sealed record MpResult(bool Success, PowerState State, string Transcript, string? Error = null)
{
    /// <summary>
    /// True when the attempt failed before any task command was sent - a refused connection, or a
    /// session dropped during login. Vintage service processors routinely refuse a reconnect for a
    /// moment after the previous session closes, so these are safe to retry: nothing was issued.
    /// A failure part-way through a task is never marked transient, because re-running it could
    /// issue a power command twice.
    /// </summary>
    public bool Transient { get; init; }
}

/// <summary>
/// How the app talks to a management processor. The only implementation today runs the YAML
/// expect scripts, but the seam is here so an HTTP-based driver (iLO Redfish, for instance) can be
/// added without disturbing the orchestration above it.
/// </summary>
public interface IMpDriver
{
    /// <summary>Can this driver handle the given mp-type definition?</summary>
    bool CanHandle(MpTypeDefinition type);

    /// <summary>
    /// Run a named task ("poweron", "poweroff", "reset", "status") against a machine's MP.
    /// Progress lines are surfaced to the job so the UI can show what the MP is doing.
    /// </summary>
    Task<MpResult> RunTaskAsync(
        ConfigSnapshot snapshot,
        MachineConfig machine,
        string task,
        Action<string>? progress,
        CancellationToken ct);
}
