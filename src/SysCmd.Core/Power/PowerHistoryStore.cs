using System.Globalization;
using Microsoft.Extensions.Logging;

namespace SysCmd.Core.Power;

/// <summary>One PDU reading at one instant.</summary>
public sealed record PowerSample(DateTimeOffset Timestamp, string PduId, double Watts, double Amps, double Volts);

/// <summary>
/// Append-only power history in monthly CSV files. No database by design: these files are easy to
/// inspect, easy to graph elsewhere, and cheap to roll or delete.
/// </summary>
public sealed class PowerHistoryStore
{
    private const string Header = "timestamp,pduId,watts,amps,volts";

    private readonly string _dir;
    private readonly ILogger<PowerHistoryStore> _log;
    private readonly Lock _writeLock = new();

    public PowerHistoryStore(string dataRoot, ILogger<PowerHistoryStore> log)
    {
        _dir = Path.Combine(dataRoot, "power");
        _log = log;
        Directory.CreateDirectory(_dir);
    }

    private string FileFor(DateTimeOffset when) => Path.Combine(_dir, $"{when:yyyy-MM}.csv");

    public void Append(IEnumerable<PowerSample> samples)
    {
        var rows = samples.ToList();
        if (rows.Count == 0) return;

        try
        {
            lock (_writeLock)
            {
                foreach (var group in rows.GroupBy(s => FileFor(s.Timestamp)))
                {
                    var isNew = !File.Exists(group.Key);
                    using var writer = new StreamWriter(group.Key, append: true);
                    if (isNew) writer.WriteLine(Header);
                    foreach (var s in group)
                        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"{s.Timestamp:O},{s.PduId},{s.Watts:F1},{s.Amps:F2},{s.Volts:F0}"));
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not append power history");
        }
    }

    /// <summary>Read samples in a window, walking only the monthly files that can contain it.</summary>
    public IReadOnlyList<PowerSample> Read(DateTimeOffset from, DateTimeOffset to, string? pduId = null)
    {
        var samples = new List<PowerSample>();
        var month = new DateTimeOffset(from.Year, from.Month, 1, 0, 0, 0, from.Offset);

        while (month <= to)
        {
            var file = FileFor(month);
            if (File.Exists(file))
            {
                foreach (var line in File.ReadLines(file).Skip(1))
                {
                    if (Parse(line) is not { } sample) continue;
                    if (sample.Timestamp < from || sample.Timestamp > to) continue;
                    if (pduId is not null && !sample.PduId.Equals(pduId, StringComparison.OrdinalIgnoreCase)) continue;
                    samples.Add(sample);
                }
            }
            month = month.AddMonths(1);
        }

        samples.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return samples;
    }

    private static PowerSample? Parse(string line)
    {
        var parts = line.Split(',');
        if (parts.Length < 5) return null;
        if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
            return null;
        return new PowerSample(
            ts,
            parts[1],
            Num(parts[2]),
            Num(parts[3]),
            Num(parts[4]));

        static double Num(string s) => double.TryParse(s, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
