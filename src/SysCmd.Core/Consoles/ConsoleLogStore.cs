using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SysCmd.Core.Configuration;
using SysCmd.Core.Events;
using SysCmd.Core.Mp;

namespace SysCmd.Core.Consoles;

/// <summary>One recorded console session, as it sits on disk.</summary>
public sealed record ConsoleLogFile(
    string MachineId,
    string Name,
    ConsoleTarget Target,
    DateTimeOffset Started,
    DateTimeOffset LastWritten,
    long Bytes,
    bool Active);

/// <summary>
/// Console sessions written to text files: one file per session, in a directory per machine, begun
/// when the console connects and closed when it does. Plain files like the rest of data/ - easy to
/// grep, easy to copy off the box, and cheap to throw away, which retention does on a timer.
/// </summary>
public sealed partial class ConsoleLogStore
{
    private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

    private readonly ConfigStore _config;
    private readonly EventLog _events;
    private readonly ILogger<ConsoleLogStore> _log;

    // Files a session is still writing. Retention leaves them alone however old their last write:
    // a console can sit idle for longer than the retention window and still be open.
    private readonly ConcurrentDictionary<string, byte> _open = new(StringComparer.Ordinal);

    // Held while a session makes its directory and while pruning removes empty ones, so a session
    // cannot find its directory deleted between creating it and opening its file.
    private readonly Lock _dirLock = new();

    public ConsoleLogStore(string dataRoot, ConfigStore config, EventLog events, ILogger<ConsoleLogStore> log)
    {
        Root = Path.GetFullPath(Path.Combine(dataRoot, "console-logs"));
        _config = config;
        _events = events;
        _log = log;
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})_(mp|serial)(-\d+)?\.log$")]
    private static partial Regex LogName();

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeChars();

    /// <summary>
    /// Begin the log for a console that has just connected, or null when logging is turned off or
    /// the file cannot be made. Never throws: a console is worth more than its transcript.
    /// </summary>
    public ConsoleLogWriter? Start(MachineConfig machine, ConsoleTarget target, NetworkEndpoint endpoint)
    {
        if (!_config.Current.App.ConsoleLogs.Enabled) return null;

        var started = DateTimeOffset.Now;
        var stamp = started.ToString(StampFormat, CultureInfo.InvariantCulture);
        var kind = target == ConsoleTarget.Mp ? "mp" : "serial";

        try
        {
            string path;
            FileStream stream;
            lock (_dirLock)
            {
                var dir = Path.Combine(Root, DirectoryFor(machine.Id));
                Directory.CreateDirectory(dir);

                // An MP that takes several logins can have the same console opened from two
                // browsers in the same second; the second gets a suffix rather than the first's file.
                for (var n = 1; ; n++)
                {
                    path = Path.Combine(dir, $"{stamp}_{kind}{(n == 1 ? "" : $"-{n}")}.log");
                    try
                    {
                        stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        break;
                    }
                    catch (IOException) when (File.Exists(path) && n < 100) { }
                }
            }

            _open[path] = 0;
            var writer = new ConsoleLogWriter(path, stream, () => _open.TryRemove(path, out _), _log);

            var label = target == ConsoleTarget.Mp ? "management processor" : "serial console";
            writer.Note($"{machine.DisplayName} {label} at {endpoint}, opened {started:yyyy-MM-dd HH:mm:ss zzz}");
            return writer;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not start a console log for {Machine}", machine.Id);
            _events.Warn("console", $"The console on {machine.DisplayName} is not being logged: {ex.Message}", machine.Id);
            return null;
        }
    }

    /// <summary>Every log on disk, newest first.</summary>
    public IReadOnlyList<ConsoleLogFile> List()
    {
        var files = new List<ConsoleLogFile>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Root))
                foreach (var path in Directory.EnumerateFiles(dir, "*.log"))
                    if (Describe(path) is { } file) files.Add(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not list console logs");
        }

        return [.. files.OrderByDescending(f => f.Started).ThenByDescending(f => f.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The file for one log, or null when there is no such log. Both parts arrive from a URL, so
    /// neither is trusted to be a plain name: the log's name must be one this store would have
    /// written, and the directory must be a single path segment.
    /// </summary>
    public string? PathOf(string machineId, string name)
    {
        if (string.IsNullOrEmpty(machineId) || UnsafeChars().IsMatch(machineId) || machineId is "." or "..")
            return null;
        if (!LogName().IsMatch(name)) return null;

        var path = Path.Combine(Root, machineId, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The end of a log, at most <paramref name="maxBytes"/> of it. A session left open for a week
    /// is not something to hand a browser whole; the download is for that.
    /// </summary>
    public string? ReadTail(string machineId, string name, int maxBytes, out bool truncated)
    {
        truncated = false;
        if (PathOf(machineId, name) is not { } path) return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - maxBytes);
            truncated = start > 0;
            stream.Seek(start, SeekOrigin.Begin);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();

            // Starting part way in lands mid-line, and possibly mid-character; begin at the next
            // whole line instead.
            if (truncated && text.IndexOf('\n') is var newline and >= 0) text = text[(newline + 1)..];
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read console log {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Remove logs that have gone longer than the retention setting without being written to.
    /// Returns how many went. A retention of zero keeps everything.
    /// </summary>
    public int Prune()
    {
        var days = _config.Current.App.ConsoleLogs.RetentionDays;
        if (days <= 0) return 0;

        var cutoff = DateTime.Now.AddDays(-days);
        var removed = 0;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Root).ToList())
            {
                foreach (var path in Directory.EnumerateFiles(dir, "*.log").ToList())
                {
                    if (_open.ContainsKey(path)) continue;
                    try
                    {
                        if (File.GetLastWriteTime(path) >= cutoff) continue;
                        File.Delete(path);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _log.LogWarning(ex, "Could not remove old console log {Path}", path);
                    }
                }

                lock (_dirLock)
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next pass */ }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not prune console logs");
        }

        if (removed > 0)
            _events.Info("console",
                $"Removed {removed} console {(removed == 1 ? "log" : "logs")} not written to in {days} days");

        return removed;
    }

    private ConsoleLogFile? Describe(string path)
    {
        var name = Path.GetFileName(path);
        if (LogName().Match(name) is not { Success: true } match) return null;
        if (!DateTime.TryParseExact(match.Groups[1].Value, StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var started))
            return null;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;

            return new ConsoleLogFile(
                MachineId: Path.GetFileName(Path.GetDirectoryName(path)!),
                Name: name,
                Target: match.Groups[2].Value == "mp" ? ConsoleTarget.Mp : ConsoleTarget.Serial,
                Started: new DateTimeOffset(started),
                LastWritten: new DateTimeOffset(info.LastWriteTime),
                Bytes: info.Length,
                Active: _open.ContainsKey(path));
        }
        catch (IOException)
        {
            return null;    // removed between being listed and being looked at
        }
    }

    /// <summary>
    /// A machine id as a directory name. Ids come from file stems, but a hand edit can still put a
    /// slash in one, and that must not become a path.
    /// </summary>
    private static string DirectoryFor(string machineId)
    {
        var safe = UnsafeChars().Replace(machineId, "_");
        return safe is "" or "." or ".." ? "_" : safe;
    }
}
