namespace SysCmd.Core.Events;

public enum EventLevel { Debug, Info, Warning, Error }

/// <summary>One line in the application's activity log.</summary>
public sealed record LabEvent(
    DateTimeOffset Timestamp,
    EventLevel Level,
    string Category,
    string Message,
    string? MachineId = null,
    string? JobId = null)
{
    /// <summary>Optional multi-line payload, e.g. an MP session transcript. Kept out of the summary line.</summary>
    public string? Detail { get; init; }
}
