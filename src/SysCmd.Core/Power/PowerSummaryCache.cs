using SysCmd.Core.Configuration;

namespace SysCmd.Core.Power;

/// <summary>The energy and cost figures shown in the status overview.</summary>
public sealed record PowerSummary(
    double CurrentWatts,
    double TodayKwh,
    decimal TodayCost,
    double MonthKwh,
    decimal MonthCost,
    string Currency)
{
    public DateTimeOffset ComputedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>What the draw right now would cost over an hour if it held steady.</summary>
    public decimal CostPerHour { get; init; }
}

/// <summary>
/// Keeps running energy totals in memory so the dashboard never re-reads a month of CSV. Totals
/// are seeded from history at startup and advanced incrementally by each poll, which also means a
/// restart mid-month does not reset the figures.
/// </summary>
public sealed class PowerSummaryCache(ConfigStore config, PowerHistoryStore history)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PowerSample> _last = new(StringComparer.OrdinalIgnoreCase);

    private DateOnly _day = DateOnly.FromDateTime(DateTime.Now);
    private int _month = DateTime.Now.Month;
    private int _year = DateTime.Now.Year;

    private double _todayKwh;
    private double _monthKwh;
    private double _currentWatts;

    /// <summary>Rebuild today's and this month's totals from the CSV files. Called once at startup.</summary>
    public void Seed()
    {
        var now = DateTimeOffset.Now;
        var dayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);

        var monthSamples = history.Read(monthStart, now);

        lock (_lock)
        {
            _monthKwh = EnergyMath.KilowattHoursAcrossPdus(monthSamples);
            _todayKwh = EnergyMath.KilowattHoursAcrossPdus([.. monthSamples.Where(s => s.Timestamp >= dayStart)]);

            foreach (var group in monthSamples.GroupBy(s => s.PduId, StringComparer.OrdinalIgnoreCase))
                _last[group.Key] = group.MaxBy(s => s.Timestamp)!;
        }
    }

    /// <summary>Advance the totals with a fresh round of readings.</summary>
    public void Record(IReadOnlyList<PowerSample> samples)
    {
        if (samples.Count == 0) return;

        lock (_lock)
        {
            RollPeriods(samples[0].Timestamp);

            foreach (var sample in samples)
            {
                if (_last.TryGetValue(sample.PduId, out var previous))
                {
                    var span = sample.Timestamp - previous.Timestamp;
                    // Same gap rule as the historical integration, so the two agree.
                    if (span > TimeSpan.Zero && span <= TimeSpan.FromMinutes(10))
                    {
                        var kwh = (sample.Watts + previous.Watts) / 2 * span.TotalSeconds / 3600.0 / 1000.0;
                        _todayKwh += kwh;
                        _monthKwh += kwh;
                    }
                }
                _last[sample.PduId] = sample;
            }

            _currentWatts = _last.Values
                .Where(s => DateTimeOffset.Now - s.Timestamp < TimeSpan.FromMinutes(5))
                .Sum(s => s.Watts);
        }
    }

    /// <summary>Zero the day or month totals when the clock rolls over.</summary>
    private void RollPeriods(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        if (today != _day) { _todayKwh = 0; _day = today; }
        if (now.Month != _month || now.Year != _year) { _monthKwh = 0; _month = now.Month; _year = now.Year; }
    }

    public PowerSummary Current()
    {
        var cfg = config.Current.App.Power;
        lock (_lock)
        {
            return new PowerSummary(
                Math.Round(_currentWatts, 1),
                Math.Round(_todayKwh, 3),
                EnergyMath.Cost(_todayKwh, cfg.CostPerKwh),
                Math.Round(_monthKwh, 3),
                EnergyMath.Cost(_monthKwh, cfg.CostPerKwh),
                cfg.Currency)
            {
                // An hour at the present draw. Kept at four places because an idle lab can sit
                // well under a penny an hour, and rounding that to zero says nothing.
                CostPerHour = Math.Round((decimal)(_currentWatts / 1000.0) * cfg.CostPerKwh, 4,
                    MidpointRounding.AwayFromZero),
            };
        }
    }
}
