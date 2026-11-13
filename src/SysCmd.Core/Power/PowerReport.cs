namespace SysCmd.Core.Power;

/// <summary>One point on the whole-lab draw chart: the mean total draw across a bucket.</summary>
public sealed record PowerPoint(DateTimeOffset Timestamp, double Watts);

/// <summary>
/// Energy, cost-relevant totals and a chartable series for one span of history, across whichever
/// PDUs are counted.
/// </summary>
public sealed record PowerReport(
    DateTimeOffset From,
    DateTimeOffset To,
    double Kwh,
    double PeakWatts,
    double MeanWatts,
    int Readings,
    IReadOnlyList<IReadOnlyList<PowerPoint>> Segments)
{
    public static PowerReport Empty(DateTimeOffset from, DateTimeOffset to) => new(from, to, 0, 0, 0, 0, []);
}

public static class PowerReports
{
    /// <summary>
    /// Reduce a stream of readings to a report in one pass and constant memory, so the period can
    /// be a year as easily as an hour.
    ///
    /// Energy is integrated per PDU with the same gap rule as <see cref="EnergyMath"/>, so the
    /// figure agrees with the running totals. Every PDU in one poll shares a timestamp, so the lab
    /// total at an instant is the sum of a contiguous run of rows; peak and mean are taken over
    /// those totals, exactly as the chart used to sum them. The chart itself is those totals
    /// averaged into <paramref name="buckets"/> evenly spaced buckets over
    /// <paramref name="from"/>..<paramref name="axisEnd"/> - averaged rather than sampled, for the
    /// reason <see cref="PowerSeries.Downsample"/> gives - and broken into segments wherever the
    /// readings stopped for longer than the gap rule allows, so an outage is drawn as one.
    /// </summary>
    public static PowerReport Build(
        IEnumerable<PowerSample> samples,
        DateTimeOffset from,
        DateTimeOffset axisEnd,
        int buckets,
        Func<string, bool>? counted = null,
        TimeSpan? maxGap = null)
    {
        var gap = maxGap ?? TimeSpan.FromMinutes(10);
        buckets = Math.Max(1, buckets);
        var width = Math.Max(1, (axisEnd - from).Ticks / (double)buckets);

        var sums = new double[buckets];
        var counts = new int[buckets];
        var ticks = new double[buckets];

        var last = new Dictionary<string, PowerSample>(StringComparer.OrdinalIgnoreCase);
        var wattSeconds = 0.0;

        DateTimeOffset? instant = null;
        var instantWatts = 0.0;
        var instants = 0;
        var totalWatts = 0.0;
        var peak = 0.0;
        DateTimeOffset? lastAt = null;

        void Flush()
        {
            if (instant is not { } at) return;
            instants++;
            totalWatts += instantWatts;
            peak = Math.Max(peak, instantWatts);

            var i = (int)Math.Clamp((at - from).Ticks / width, 0, buckets - 1);
            sums[i] += instantWatts;
            counts[i]++;
            ticks[i] += at.UtcTicks;
        }

        foreach (var s in samples)
        {
            if (counted is not null && !counted(s.PduId)) continue;

            if (last.TryGetValue(s.PduId, out var previous))
            {
                var span = s.Timestamp - previous.Timestamp;
                if (span > TimeSpan.Zero && span <= gap)
                    wattSeconds += (s.Watts + previous.Watts) / 2 * span.TotalSeconds;
            }
            last[s.PduId] = s;

            if (instant != s.Timestamp)
            {
                Flush();
                instant = s.Timestamp;
                instantWatts = 0;
            }
            instantWatts += s.Watts;
            lastAt = s.Timestamp;
        }
        Flush();

        if (instants == 0) return PowerReport.Empty(from, axisEnd);

        // Neighbouring buckets always join; a run of empty ones only breaks the line when the
        // readings either side of it are further apart than the energy integration would bridge.
        var join = gap + TimeSpan.FromTicks((long)(2 * width));
        var offset = lastAt!.Value.Offset;
        var segments = new List<IReadOnlyList<PowerPoint>>();
        List<PowerPoint>? current = null;

        for (var i = 0; i < buckets; i++)
        {
            if (counts[i] == 0) continue;
            var at = new DateTimeOffset((long)(ticks[i] / counts[i]), TimeSpan.Zero).ToOffset(offset);
            var point = new PowerPoint(at, sums[i] / counts[i]);

            if (current is null || at - current[^1].Timestamp > join)
            {
                current = [];
                segments.Add(current);
            }
            current.Add(point);
        }

        return new PowerReport(
            from, axisEnd,
            wattSeconds / 3600.0 / 1000.0,
            peak,
            totalWatts / instants,
            instants,
            segments);
    }
}
