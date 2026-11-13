namespace SysCmd.Server.Components.Shared;

/// <summary>
/// A span of power history to report on. <see cref="End"/> is where the span nominally finishes,
/// which for "today" or "this month" is still in the future: the chart is drawn to that end so the
/// line stops where the day has got to, and the figures only ever count readings up to now.
/// </summary>
public sealed record PowerPeriod(string Key, string Label, DateTimeOffset From, DateTimeOffset End, bool Rolling = false)
{
    /// <summary>
    /// Whether readings are still arriving inside this span. A rolling one always is: its end is
    /// whatever "now" was when it was resolved, which is already the past by the time anyone asks.
    /// </summary>
    public bool IsLive(DateTimeOffset now) => Rolling || End > now;

    /// <summary>Where the readings stop: now, for a span that has not finished yet.</summary>
    public DateTimeOffset DataEnd(DateTimeOffset now) => End < now ? End : now;
}

public static class PowerPeriods
{
    public const string Custom = "custom";

    /// <summary>The choices, in the order the picker lists them.</summary>
    public static IReadOnlyList<(string Key, string Label)> Choices { get; } =
    [
        ("6h", "Last 6 hours"),
        ("24h", "Last 24 hours"),
        ("7d", "Last 7 days"),
        ("30d", "Last 30 days"),
        ("90d", "Last 90 days"),
        ("365d", "Last 12 months"),
        ("today", "Today"),
        ("yesterday", "Yesterday"),
        ("week", "This week"),
        ("lastweek", "Last week"),
        ("month", "This month"),
        ("lastmonth", "Last month"),
        ("year", "This year"),
        ("lastyear", "Last year"),
        (Custom, "Custom range..."),
    ];

    public static bool IsKnown(string? key) => Choices.Any(c => c.Key == key);

    /// <summary>
    /// Resolve a choice against the local clock. A custom range runs from the start of
    /// <paramref name="customFrom"/> to the end of <paramref name="customTo"/>, both included, the
    /// way anyone reading "1 March to 31 March" means it. Weeks start on Monday.
    /// </summary>
    public static PowerPeriod Resolve(string key, DateTime now, DateOnly customFrom, DateOnly customTo)
    {
        var today = now.Date;
        var label = Choices.FirstOrDefault(c => c.Key == key).Label ?? key;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var month = new DateTime(today.Year, today.Month, 1);
        var year = new DateTime(today.Year, 1, 1);

        PowerPeriod Span(DateTime from, DateTime end, string? text = null) =>
            new(key, text ?? label, Local(from), Local(end));

        PowerPeriod Rolling(TimeSpan span) =>
            new(key, label, Local(now) - span, Local(now), Rolling: true);

        return key switch
        {
            "6h" => Rolling(TimeSpan.FromHours(6)),
            "24h" => Rolling(TimeSpan.FromHours(24)),
            "7d" => Rolling(TimeSpan.FromDays(7)),
            "30d" => Rolling(TimeSpan.FromDays(30)),
            "90d" => Rolling(TimeSpan.FromDays(90)),
            "365d" => Rolling(TimeSpan.FromDays(365)),
            "today" => Span(today, today.AddDays(1)),
            "yesterday" => Span(today.AddDays(-1), today),
            "week" => Span(monday, monday.AddDays(7)),
            "lastweek" => Span(monday.AddDays(-7), monday),
            "month" => Span(month, month.AddMonths(1), $"{month:MMMM yyyy}"),
            "lastmonth" => Span(month.AddMonths(-1), month, $"{month.AddMonths(-1):MMMM yyyy}"),
            "year" => Span(year, year.AddYears(1), $"{year:yyyy}"),
            "lastyear" => Span(year.AddYears(-1), year, $"{year.AddYears(-1):yyyy}"),
            Custom => CustomSpan(customFrom, customTo),
            _ => Resolve("24h", now, customFrom, customTo),
        };

        PowerPeriod CustomSpan(DateOnly a, DateOnly b)
        {
            // Entered backwards is still a range; there is no reason to refuse it.
            if (b < a) (a, b) = (b, a);
            var from = a.ToDateTime(TimeOnly.MinValue);
            var end = b.ToDateTime(TimeOnly.MinValue).AddDays(1);
            var text = a == b ? $"{a:dd MMM yyyy}" : $"{a:dd MMM yyyy} to {b:dd MMM yyyy}";
            return new PowerPeriod(Custom, text, Local(from), Local(end));
        }
    }

    /// <summary>A local wall-clock time with the offset in force on that date, not today's.</summary>
    private static DateTimeOffset Local(DateTime wall) =>
        new(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(wall));
}
