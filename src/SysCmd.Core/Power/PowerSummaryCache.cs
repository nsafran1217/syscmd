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
///
/// Totals are kept per PDU rather than for the lab as a whole, so a viewer can leave a PDU out of
/// the figures - a rack that is not part of the lab, say - without the cache knowing who asked.
/// </summary>
public sealed class PowerSummaryCache(ConfigStore config, PowerHistoryStore history)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PowerSample> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Totals> _totals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The draw of each PDU whose last reading was recent enough to count as "now".</summary>
    private readonly Dictionary<string, double> _currentWatts = new(StringComparer.OrdinalIgnoreCase);

    private DateOnly _day = DateOnly.FromDateTime(DateTime.Now);
    private int _month = DateTime.Now.Month;
    private int _year = DateTime.Now.Year;

    private sealed class Totals
    {
        public double TodayKwh;
        public double MonthKwh;
    }

    /// <summary>Rebuild today's and this month's totals from the CSV files. Called once at startup.</summary>
    public void Seed()
    {
        var now = DateTimeOffset.Now;
        var dayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);

        var monthSamples = history.Read(monthStart, now);

        lock (_lock)
        {
            foreach (var group in monthSamples.GroupBy(s => s.PduId, StringComparer.OrdinalIgnoreCase))
            {
                var samples = group.ToList();
                var totals = TotalsFor(group.Key);
                totals.MonthKwh = EnergyMath.KilowattHours(samples);
                totals.TodayKwh = EnergyMath.KilowattHours([.. samples.Where(s => s.Timestamp >= dayStart)]);
                _last[group.Key] = samples.MaxBy(s => s.Timestamp)!;
            }
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
                        var totals = TotalsFor(sample.PduId);
                        totals.TodayKwh += kwh;
                        totals.MonthKwh += kwh;
                    }
                }
                _last[sample.PduId] = sample;
            }

            _currentWatts.Clear();
            foreach (var (pduId, last) in _last)
            {
                if (DateTimeOffset.Now - last.Timestamp < TimeSpan.FromMinutes(5))
                    _currentWatts[pduId] = last.Watts;
            }
        }
    }

    private Totals TotalsFor(string pduId)
    {
        if (!_totals.TryGetValue(pduId, out var totals))
            _totals[pduId] = totals = new Totals();
        return totals;
    }

    /// <summary>Zero the day or month totals when the clock rolls over.</summary>
    private void RollPeriods(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var newDay = today != _day;
        var newMonth = now.Month != _month || now.Year != _year;

        foreach (var totals in _totals.Values)
        {
            if (newDay) totals.TodayKwh = 0;
            if (newMonth) totals.MonthKwh = 0;
        }

        _day = today;
        _month = now.Month;
        _year = now.Year;
    }

    /// <summary>
    /// The figures across every PDU, or across all but <paramref name="excludedPduIds"/>. An id
    /// that names no PDU is simply never matched.
    /// </summary>
    public PowerSummary Current(IReadOnlySet<string>? excludedPduIds = null)
    {
        var cfg = config.Current.App.Power;
        bool Counted(string pduId) => excludedPduIds is null || !excludedPduIds.Contains(pduId);

        lock (_lock)
        {
            var currentWatts = _currentWatts.Where(kv => Counted(kv.Key)).Sum(kv => kv.Value);
            var counted = _totals.Where(kv => Counted(kv.Key)).Select(kv => kv.Value).ToList();
            var todayKwh = counted.Sum(t => t.TodayKwh);
            var monthKwh = counted.Sum(t => t.MonthKwh);

            return new PowerSummary(
                Math.Round(currentWatts, 1),
                Math.Round(todayKwh, 3),
                EnergyMath.Cost(todayKwh, cfg.CostPerKwh),
                Math.Round(monthKwh, 3),
                EnergyMath.Cost(monthKwh, cfg.CostPerKwh),
                cfg.Currency)
            {
                // An hour at the present draw. Kept at four places because an idle lab can sit
                // well under a penny an hour, and rounding that to zero says nothing.
                CostPerHour = Math.Round((decimal)(currentWatts / 1000.0) * cfg.CostPerKwh, 4,
                    MidpointRounding.AwayFromZero),
            };
        }
    }
}
