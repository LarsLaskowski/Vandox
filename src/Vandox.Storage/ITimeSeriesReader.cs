namespace Vandox.Storage;

/// <summary>
/// Reads the series, markers and context rows of time-series queries.
/// </summary>
public interface ITimeSeriesReader
{
    #region Methods

    /// <summary>
    /// Reads the raw values of one series.
    /// </summary>
    /// <param name="query">The series and range</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns a page of lines</returns>
    Task<LinePage> RawValuesAsync(RawValueQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the rollup steps of one series.
    /// </summary>
    /// <param name="query">The series, tier, step and range</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns a page of lines</returns>
    Task<LinePage> RollupStepsAsync(RollupStepQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the agent gaps that touch a range.
    /// </summary>
    /// <param name="query">The range and limit</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the gaps</returns>
    Task<IReadOnlyList<AgentGap>> GapsInRangeAsync(TimeRangeQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the earliest time not yet folded into rollups.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the time, or null when there is none</returns>
    Task<DateTimeOffset?> EarliestUnfoldedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the first service state per unit and the changes in a range, with the previous state.
    /// </summary>
    /// <param name="query">The range and limit</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the rows</returns>
    Task<IReadOnlyList<ServiceStateRow>> ServiceChangesAsync(TimeRangeQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the latest service state of each unit before a time.
    /// </summary>
    /// <param name="units">The unit names</param>
    /// <param name="before">The instant the states precede</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the rows</returns>
    Task<IReadOnlyList<ServiceStateRow>> PreviousServiceStatesAsync(IReadOnlyList<string> units, DateTimeOffset before, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the boots recorded in a range.
    /// </summary>
    /// <param name="query">The range and limit</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the boots</returns>
    Task<IReadOnlyList<BootTime>> BootTimesAsync(TimeRangeQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the gap records of a range.
    /// </summary>
    /// <param name="query">The range and limit</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the rows</returns>
    Task<IReadOnlyList<GapRecordRow>> GapRecordsAsync(TimeRangeQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the kernel events of a range.
    /// </summary>
    /// <param name="query">The range and limit</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the rows</returns>
    Task<IReadOnlyList<KernelEventRow>> KernelEventsAsync(TimeRangeQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the backup runs of a range.
    /// </summary>
    /// <param name="query">The range and limit</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the rows</returns>
    Task<IReadOnlyList<BackupRunRow>> BackupRunsAsync(TimeRangeQuery query, CancellationToken cancellationToken);

    #endregion // Methods
}