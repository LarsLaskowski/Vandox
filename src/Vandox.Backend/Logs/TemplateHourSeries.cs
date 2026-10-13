using Vandox.Backend.Components.Charts;
using Vandox.Storage;

namespace Vandox.Backend.Logs;

/// <summary>
/// Builds the hourly chart of one template.
/// </summary>
public static class TemplateHourSeries
{
    #region Constants

    /// <summary>
    /// The largest number of hours of the series.
    /// </summary>
    public const int MaxHours = 2160;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Builds the series of a template: one point per hour from the first to the last hour of the range that holds a count or a covered hour, at most the last
    /// <see cref="MaxHours"/>. An hour with lines of the template has its count, a covered hour without the template is 0, an hour the source did not log is a gap.
    /// </summary>
    /// <param name="hours">The hours with lines of the template</param>
    /// <param name="coveredHours">The hours in which the source has lines</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <returns>The chart data</returns>
    public static ChartData Build(IReadOnlyList<TemplateHour> hours, IReadOnlyList<DateTimeOffset> coveredHours, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(hours);
        ArgumentNullException.ThrowIfNull(coveredHours);

        var first = new DateTimeOffset(from.UtcDateTime.Date.AddHours(from.UtcDateTime.Hour), TimeSpan.Zero);
        var counts = hours.Where(hour => hour.Hour >= first && hour.Hour < to).GroupBy(hour => hour.Hour).ToDictionary(group => group.Key, group => group.Sum(hour => hour.Count));
        var covered = coveredHours.Where(hour => hour >= first && hour < to).ToHashSet();
        var all = counts.Keys.Concat(covered).ToList();

        if (all.Count == 0)
        {
            return new ChartData([], [new ChartSeries(LogsView.ChartSeries, [])]);
        }

        var last = all.Max();
        var start = all.Min();
        var count = (int)Math.Min((last - start).TotalHours + 1, MaxHours);
        var times = new List<DateTimeOffset>(count);
        var values = new List<double?>(count);

        start = last.AddHours(1 - count);

        for (var index = 0; index < count; index++)
        {
            var hour = start.AddHours(index);

            times.Add(hour);
            values.Add(ValueOf(hour, counts, covered));
        }

        return new ChartData(times, [new ChartSeries(LogsView.ChartSeries, values)]);
    }

    /// <summary>
    /// Gets the point of an hour.
    /// </summary>
    /// <param name="hour">The start of the hour</param>
    /// <param name="counts">The counts of the template by hour</param>
    /// <param name="covered">The hours in which the source has lines</param>
    /// <returns>The count, 0 for a covered hour without the template, or <c>null</c> for a gap</returns>
    private static double? ValueOf(DateTimeOffset hour, Dictionary<DateTimeOffset, long> counts, HashSet<DateTimeOffset> covered)
    {
        if (counts.TryGetValue(hour, out var value))
        {
            return value;
        }

        return covered.Contains(hour) ? 0 : null;
    }

    #endregion // Methods
}