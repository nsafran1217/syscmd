namespace SysCmd.Core.Power;

/// <summary>
/// Reducing a long run of readings to something a chart can draw.
/// </summary>
public static class PowerSeries
{
    /// <summary>
    /// Average the readings into at most <paramref name="maxPointsPerPdu"/> evenly spaced buckets
    /// per PDU.
    ///
    /// Averaging rather than sampling every Nth reading, because the two say different things. A
    /// year of 30-second readings drawn as 500 of them is 500 instants: a brief spike either lands
    /// on one of the chosen instants and fills a whole pixel column, or falls between them and
    /// disappears entirely, and which of those happens is decided by arithmetic on the sample
    /// count. An average over the bucket is a claim the data actually supports.
    ///
    /// Bucketing is per PDU because each one is its own line: two PDUs read at the same instant
    /// averaged together would draw a line through the middle of both.
    /// </summary>
    public static IReadOnlyList<PowerSample> Downsample(
        IReadOnlyList<PowerSample> samples, int maxPointsPerPdu)
    {
        if (samples.Count == 0) return samples;
        if (maxPointsPerPdu < 1) maxPointsPerPdu = 1;

        var start = samples.Min(s => s.Timestamp);
        var span = samples.Max(s => s.Timestamp) - start;

        // Everything at one instant, or few enough to draw as they are: hand back the readings
        // themselves rather than averages of one.
        if (span <= TimeSpan.Zero) return samples;

        var width = span.Ticks / (double)maxPointsPerPdu;
        var reduced = new List<PowerSample>();

        foreach (var series in samples.GroupBy(s => s.PduId, StringComparer.OrdinalIgnoreCase))
        {
            var readings = series.ToList();
            if (readings.Count <= maxPointsPerPdu)
            {
                reduced.AddRange(readings);
                continue;
            }

            foreach (var bucket in readings
                .GroupBy(s => Math.Min((int)((s.Timestamp - start).Ticks / width), maxPointsPerPdu - 1))
                .OrderBy(b => b.Key))
            {
                reduced.Add(Average(series.Key, [.. bucket]));
            }
        }

        reduced.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return reduced;
    }

    /// <summary>
    /// One point standing for a bucket. Its timestamp is the mean of the readings actually in the
    /// bucket rather than the bucket's own midpoint: a bucket holding a single reading at its edge
    /// should be drawn where that reading happened, not shifted to the middle of an empty span.
    /// </summary>
    private static PowerSample Average(string pduId, IReadOnlyList<PowerSample> bucket)
    {
        var meanUtcTicks = (long)bucket.Average(s => (double)s.Timestamp.UtcTicks);
        var at = new DateTimeOffset(meanUtcTicks, TimeSpan.Zero).ToOffset(bucket[0].Timestamp.Offset);

        return new PowerSample(
            at,
            pduId,
            bucket.Average(s => s.Watts),
            bucket.Average(s => s.Amps),
            bucket.Average(s => s.Volts));
    }
}
