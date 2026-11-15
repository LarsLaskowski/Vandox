namespace Vandox.Storage;

/// <summary>
/// The ladder of steps of a query: 5, 10, 15 and 30 minutes, 1, 2, 3, 6 and 12 hours, 1 day, then whole days.
/// </summary>
internal static class SeriesLadder
{
    #region Fields

    private static readonly TimeSpan[] _steps = [
                                                    TimeSpan.FromMinutes(5),
                                                    TimeSpan.FromMinutes(10),
                                                    TimeSpan.FromMinutes(15),
                                                    TimeSpan.FromMinutes(30),
                                                    TimeSpan.FromHours(1),
                                                    TimeSpan.FromHours(2),
                                                    TimeSpan.FromHours(3),
                                                    TimeSpan.FromHours(6),
                                                    TimeSpan.FromHours(12),
                                                    TimeSpan.FromDays(1)
                                                ];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Chooses the smallest step for which at most the target steps overlap the range; a step under an hour needs the 5-minute rollups to reach the range.
    /// </summary>
    /// <param name="range">The range</param>
    /// <param name="policy">The retention policy</param>
    /// <returns>The step</returns>
    internal static TimeSpan Choose(SeriesRange range, RetentionPolicy policy)
    {
        var fromTicks = range.From.UtcTicks - SeriesTime.EpochTicks;
        var endTicks = range.End.UtcTicks - SeriesTime.EpochTicks;
        var fiveMinutes = policy.FiveMinuteRollups is null || range.From >= range.Now - policy.FiveMinuteRollups.Value;
        var found = _steps.FirstOrDefault(step => (fiveMinutes || step >= TimeSpan.FromHours(1)) && SeriesTime.Overlapping(fromTicks, endTicks, step.Ticks) <= range.Target);

        return found > TimeSpan.Zero ? found : DayStep(fromTicks, endTicks, range.Target);
    }

    /// <summary>
    /// Finds the smallest whole number of days, above one, for which at most the target steps overlap the range.
    /// </summary>
    /// <param name="fromTicks">The start of the range in ticks since the epoch</param>
    /// <param name="endTicks">The end of the range in ticks since the epoch</param>
    /// <param name="target">The target</param>
    /// <returns>The step</returns>
    private static TimeSpan DayStep(long fromTicks, long endTicks, int target)
    {
        var days = Math.Max(2, (endTicks - fromTicks) / (TimeSpan.TicksPerDay * target));

        while (SeriesTime.Overlapping(fromTicks, endTicks, days * TimeSpan.TicksPerDay) > target)
        {
            days++;
        }

        return TimeSpan.FromTicks(days * TimeSpan.TicksPerDay);
    }

    #endregion // Methods
}