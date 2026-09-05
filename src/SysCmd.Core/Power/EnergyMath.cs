namespace SysCmd.Core.Power;

/// <summary>Turns a series of instantaneous wattage readings into energy and money.</summary>
public static class EnergyMath
{
    /// <summary>
    /// Trapezoidal integration of watts over time, in kWh. Gaps longer than
    /// <paramref name="maxGap"/> are not bridged: if the app was down for six hours we should not
    /// invent six hours of consumption.
    /// </summary>
    public static double KilowattHours(IReadOnlyList<PowerSample> samples, TimeSpan? maxGap = null)
    {
        if (samples.Count < 2) return 0;
        var limit = maxGap ?? TimeSpan.FromMinutes(10);

        var wattSeconds = 0.0;
        for (var i = 1; i < samples.Count; i++)
        {
            var span = samples[i].Timestamp - samples[i - 1].Timestamp;
            if (span <= TimeSpan.Zero || span > limit) continue;
            wattSeconds += (samples[i].Watts + samples[i - 1].Watts) / 2 * span.TotalSeconds;
        }

        return wattSeconds / 3600.0 / 1000.0;
    }

    /// <summary>Sum energy across PDUs, integrating each series separately before adding them up.</summary>
    public static double KilowattHoursAcrossPdus(IReadOnlyList<PowerSample> samples, TimeSpan? maxGap = null)
        => samples.GroupBy(s => s.PduId, StringComparer.OrdinalIgnoreCase)
            .Sum(g => KilowattHours([.. g], maxGap));

    public static decimal Cost(double kilowattHours, decimal costPerKwh)
        => Math.Round((decimal)kilowattHours * costPerKwh, 2, MidpointRounding.AwayFromZero);
}
