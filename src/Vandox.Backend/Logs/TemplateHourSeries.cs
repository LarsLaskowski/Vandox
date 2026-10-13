using Vandox.Backend.Components.Charts;
using Vandox.Storage;

namespace Vandox.Backend.Logs;

#pragma warning disable RH2003, S2325

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
    /// Builds the series of a template.
    /// </summary>
    /// <param name="hours">The hours with lines of the template</param>
    /// <param name="coveredHours">The hours in which the source has lines</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <returns>The chart data</returns>
    public static ChartData Build(IReadOnlyList<TemplateHour> hours, IReadOnlyList<DateTimeOffset> coveredHours, DateTimeOffset from, DateTimeOffset to)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}