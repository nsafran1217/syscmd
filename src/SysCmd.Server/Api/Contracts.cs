using SysCmd.Core.Configuration;

namespace SysCmd.Server.Api;

/// <summary>Body of a power request from any client: the web UI, a CLI, or a script.</summary>
public sealed record PowerRequest(string Action, bool Force = false)
{
    /// <summary>
    /// Parse the action name, so callers get a clear 400 rather than a 500. Deliberately not
    /// called TryParse: minimal APIs would treat that as a route-binding convention.
    /// </summary>
    public bool TryGetAction(out PowerAction action) =>
        Enum.TryParse(Action?.Trim(), ignoreCase: true, out action);
}

/// <summary>Returned by every endpoint that starts background work.</summary>
public sealed record JobAccepted(string JobId, string Title, string Status);

/// <summary>A job as served over the API.</summary>
public sealed record JobDto(
    string Id,
    string Kind,
    string Title,
    string Target,
    string Status,
    bool Forced,
    string? MachineId,
    string? ParentJobId,
    string? Error,
    bool CanCancel,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<string> Progress);

/// <summary>An event as served over the API.</summary>
public sealed record EventDto(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message,
    string? MachineId,
    string? JobId,
    string? Detail);

/// <summary>A power history point, thinned for charting.</summary>
public sealed record PowerPointDto(DateTimeOffset Timestamp, string PduId, double Watts);
