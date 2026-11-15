namespace Vandox.Storage.Tests;

/// <summary>
/// Wraps a <see cref="ITimeSeriesReader"/> and records every call with the limit it was made with.
/// </summary>
internal sealed class CountingTimeSeriesReader : ITimeSeriesReader
{
    #region Fields

    private readonly ITimeSeriesReader _inner;

    private readonly List<(string Name, int Limit)> _calls = [];

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="CountingTimeSeriesReader"/> class.
    /// </summary>
    /// <param name="inner">The reader the calls go to</param>
    internal CountingTimeSeriesReader(ITimeSeriesReader inner)
    {
        _inner = inner;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets or sets the action run after each call returned, with the name of the call.
    /// </summary>
    internal Action<string>? AfterCall { get; set; }

    /// <summary>
    /// Gets the names of the calls in order.
    /// </summary>
    internal IReadOnlyList<string> Calls => _calls.Select(call => call.Name).ToList();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Counts the calls of one read.
    /// </summary>
    /// <param name="name">The name of the read, for example <c>GapRecordsAsync</c></param>
    /// <returns>The number of calls</returns>
    internal int CountOf(string name)
    {
        return _calls.Count(call => call.Name == name);
    }

    /// <summary>
    /// Gets the limits of the calls of one read in order: the limit of a range read, the time limit of a raw read, the line limit of a step read, the number of units of a previous-state read.
    /// </summary>
    /// <param name="name">The name of the read</param>
    /// <returns>The limits</returns>
    internal IReadOnlyList<int> LimitsOf(string name)
    {
        return _calls.Where(call => call.Name == name).Select(call => call.Limit).ToList();
    }

    /// <summary>
    /// Records a call.
    /// </summary>
    /// <param name="name">The name of the read</param>
    /// <param name="limit">The limit of the call</param>
    private void Record(string name, int limit)
    {
        _calls.Add((name, limit));
    }

    /// <summary>
    /// Runs a read on the inner reader, records it and runs <see cref="AfterCall"/>.
    /// </summary>
    /// <typeparam name="T">The result type</typeparam>
    /// <param name="name">The name of the read</param>
    /// <param name="limit">The limit of the call</param>
    /// <param name="read">The read</param>
    /// <returns>A task that returns the result</returns>
    private async Task<T> RunAsync<T>(string name, int limit, Func<Task<T>> read)
    {
        Record(name, limit);

        var result = await read();

        AfterCall?.Invoke(name);

        return result;
    }

    #endregion // Methods

    #region ITimeSeriesReader

    /// <inheritdoc/>
    public Task<LinePage> RawValuesAsync(RawValueQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(RawValuesAsync), query.MaxTimes, () => _inner.RawValuesAsync(query, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<LinePage> RollupStepsAsync(RollupStepQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(RollupStepsAsync), query.MaxLines, () => _inner.RollupStepsAsync(query, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<AgentGap>> GapsInRangeAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(GapsInRangeAsync), query.Limit, () => _inner.GapsInRangeAsync(query, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<DateTimeOffset?> EarliestUnfoldedAsync(CancellationToken cancellationToken)
    {
        return RunAsync(nameof(EarliestUnfoldedAsync), 0, () => _inner.EarliestUnfoldedAsync(cancellationToken));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ServiceStateRow>> ServiceChangesAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(ServiceChangesAsync), query.Limit, () => _inner.ServiceChangesAsync(query, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ServiceStateRow>> PreviousServiceStatesAsync(IReadOnlyList<string> units, DateTimeOffset before, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(PreviousServiceStatesAsync), units.Count, () => _inner.PreviousServiceStatesAsync(units, before, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<BootTime>> BootTimesAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(BootTimesAsync), query.Limit, () => _inner.BootTimesAsync(query, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<GapRecordRow>> GapRecordsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(GapRecordsAsync), query.Limit, () => _inner.GapRecordsAsync(query, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<KernelEventRow>> KernelEventsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(KernelEventsAsync), query.Limit, () => _inner.KernelEventsAsync(query, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<BackupRunRow>> BackupRunsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(BackupRunsAsync), query.Limit, () => _inner.BackupRunsAsync(query, cancellationToken));
    }

    #endregion // ITimeSeriesReader
}