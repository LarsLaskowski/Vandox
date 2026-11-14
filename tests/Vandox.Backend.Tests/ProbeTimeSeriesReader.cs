using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// A time-series reader that answers every read with an empty result, records the calls and the highest number in flight, and can hold every call back
/// until it is released.
/// </summary>
internal sealed class ProbeTimeSeriesReader : ITimeSeriesReader, IDisposable
{
    #region Fields

    private readonly Lock _lock = new();

    private readonly List<(string Name, object? Argument, CancellationToken Token)> _calls = [];

    private readonly SemaphoreSlim _entered = new(0);

    private int _inFlight;

    private int _maxInFlight;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the task every call waits for before it answers; <see langword="null"/> answers at once.
    /// </summary>
    internal Task? Hold { get; set; }

    /// <summary>
    /// Gets a copy of the calls in the order they began: the member name, the query or first argument, and the token.
    /// </summary>
    internal IReadOnlyList<(string Name, object? Argument, CancellationToken Token)> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>
    /// Gets the highest number of calls that were in flight at once.
    /// </summary>
    internal int MaxInFlight
    {
        get
        {
            lock (_lock)
            {
                return _maxInFlight;
            }
        }
    }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Waits until another call has begun.
    /// </summary>
    /// <param name="cancellationToken">Ends the wait</param>
    /// <returns>A task that completes when a call began, or fails after 20 seconds</returns>
    internal Task WaitForEntryAsync(CancellationToken cancellationToken)
    {
        return _entered.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
    }

    /// <summary>
    /// Records a call, waits for the hold and answers.
    /// </summary>
    /// <typeparam name="TResult">The type of the answer</typeparam>
    /// <param name="name">The name of the member</param>
    /// <param name="argument">The query or first argument</param>
    /// <param name="answer">The answer</param>
    /// <param name="cancellationToken">The token of the call</param>
    /// <returns>A task that returns the answer</returns>
    private async Task<TResult> RunAsync<TResult>(string name, object? argument, TResult answer, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _calls.Add((name, argument, cancellationToken));
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);
        }

        _entered.Release();

        try
        {
            if (Hold is not null)
            {
                await Hold.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return answer;
        }
        finally
        {
            lock (_lock)
            {
                _inFlight--;
            }
        }
    }

    #endregion // Methods

    #region ITimeSeriesReader

    /// <inheritdoc />
    public Task<LinePage> RawValuesAsync(RawValueQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(RawValuesAsync), query, new LinePage(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<LinePage> RollupStepsAsync(RollupStepQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(RollupStepsAsync), query, new LinePage(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentGap>> GapsInRangeAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync<IReadOnlyList<AgentGap>>(nameof(GapsInRangeAsync), query, [], cancellationToken);
    }

    /// <inheritdoc />
    public Task<DateTimeOffset?> EarliestUnfoldedAsync(CancellationToken cancellationToken)
    {
        return RunAsync<DateTimeOffset?>(nameof(EarliestUnfoldedAsync), null, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ServiceStateRow>> ServiceChangesAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync<IReadOnlyList<ServiceStateRow>>(nameof(ServiceChangesAsync), query, [], cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ServiceStateRow>> PreviousServiceStatesAsync(IReadOnlyList<string> units, DateTimeOffset before, CancellationToken cancellationToken)
    {
        return RunAsync<IReadOnlyList<ServiceStateRow>>(nameof(PreviousServiceStatesAsync), units, [], cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<BootTime>> BootTimesAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync<IReadOnlyList<BootTime>>(nameof(BootTimesAsync), query, [], cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GapRecordRow>> GapRecordsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync<IReadOnlyList<GapRecordRow>>(nameof(GapRecordsAsync), query, [], cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<KernelEventRow>> KernelEventsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync<IReadOnlyList<KernelEventRow>>(nameof(KernelEventsAsync), query, [], cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<BackupRunRow>> BackupRunsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync<IReadOnlyList<BackupRunRow>>(nameof(BackupRunsAsync), query, [], cancellationToken);
    }

    #endregion // ITimeSeriesReader

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _entered.Dispose();
    }

    #endregion // IDisposable
}