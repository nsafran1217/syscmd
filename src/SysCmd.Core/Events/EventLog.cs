using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SysCmd.Core.Events;

/// <summary>
/// The app's activity log. Every event goes three places: an in-memory ring the dashboard reads,
/// a daily JSONL file on disk for history, and an event other components subscribe to for live UI
/// updates. There is no database, so the JSONL files are the archive.
/// </summary>
public sealed class EventLog
{
    private const int RingCapacity = 1000;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ConcurrentQueue<LabEvent> _ring = new();
    private readonly Lock _fileLock = new();
    private readonly string _dir;
    private readonly ILogger<EventLog> _log;

    public EventLog(string dataRoot, ILogger<EventLog> log)
    {
        _dir = Path.Combine(dataRoot, "events");
        _log = log;
        Directory.CreateDirectory(_dir);
        LoadTodaysTail();
    }

    /// <summary>Raised on every write, so the Blazor log window can tail without polling.</summary>
    public event Action<LabEvent>? Written;

    public void Write(EventLevel level, string category, string message,
        string? machineId = null, string? jobId = null, string? detail = null)
    {
        var evt = new LabEvent(DateTimeOffset.Now, level, category, message, machineId, jobId) { Detail = detail };

        _ring.Enqueue(evt);
        while (_ring.Count > RingCapacity) _ring.TryDequeue(out _);

        Append(evt);

        // Mirror into the ASP.NET logger so console output and the UI agree.
        _log.Log(level switch
        {
            EventLevel.Debug => LogLevel.Debug,
            EventLevel.Warning => LogLevel.Warning,
            EventLevel.Error => LogLevel.Error,
            _ => LogLevel.Information,
        }, "[{Category}] {Message}", category, message);

        Written?.Invoke(evt);
    }

    public void Info(string category, string message, string? machineId = null, string? jobId = null, string? detail = null)
        => Write(EventLevel.Info, category, message, machineId, jobId, detail);

    public void Warn(string category, string message, string? machineId = null, string? jobId = null, string? detail = null)
        => Write(EventLevel.Warning, category, message, machineId, jobId, detail);

    public void Error(string category, string message, string? machineId = null, string? jobId = null, string? detail = null)
        => Write(EventLevel.Error, category, message, machineId, jobId, detail);

    /// <summary>Most recent events first, newest at index 0.</summary>
    public IReadOnlyList<LabEvent> Recent(int limit = 200, EventLevel? minLevel = null, string? machineId = null)
        => _ring.Reverse()
            .Where(e => minLevel is null || e.Level >= minLevel)
            .Where(e => machineId is null || e.MachineId == machineId)
            .Take(limit)
            .ToList();

    private string FileFor(DateTimeOffset when) => Path.Combine(_dir, $"{when:yyyy-MM-dd}.jsonl");

    private void Append(LabEvent evt)
    {
        try
        {
            lock (_fileLock)
                File.AppendAllText(FileFor(evt.Timestamp), JsonSerializer.Serialize(evt, Json) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Never let a logging failure break an operation the user asked for.
            _log.LogError(ex, "Could not append to the event log");
        }
    }

    /// <summary>Repopulate the ring from today's file so a restart does not blank the log window.</summary>
    private void LoadTodaysTail()
    {
        var file = FileFor(DateTimeOffset.Now);
        if (!File.Exists(file)) return;
        try
        {
            foreach (var line in File.ReadLines(file).TakeLast(RingCapacity))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (JsonSerializer.Deserialize<LabEvent>(line, Json) is { } evt) _ring.Enqueue(evt);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not replay today's event log");
        }
    }
}
