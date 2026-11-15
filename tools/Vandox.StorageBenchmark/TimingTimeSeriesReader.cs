using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using Vandox.Storage;

namespace Vandox.StorageBenchmark;

/// <summary>
/// Wraps the store as the reader of the time-series engine and records the calls and the elapsed time of every member, the reader lease included.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class TimingTimeSeriesReader : ITimeSeriesReader
{
    #region Constants

    /// <summary>
    /// The part of the rollup steps of <c>cpu_percent</c>.
    /// </summary>
    internal const string RollupCpu = "rollup steps (cpu_percent)";

    /// <summary>
    /// The part of the rollup steps of the other series.
    /// </summary>
    internal const string RollupOthers = "rollup steps (other seven)";

    /// <summary>
    /// The part of the earliest unfolded read.
    /// </summary>
    internal const string Earliest = "earliest unfolded";

    /// <summary>
    /// The part of the gap rows.
    /// </summary>
    internal const string GapRows = "gap rows";

    /// <summary>
    /// The part of the gap records.
    /// </summary>
    internal const string GapRecords = "gap records";

    /// <summary>
    /// The part of the kernel events.
    /// </summary>
    internal const string KernelEvents = "kernel events";

    /// <summary>
    /// The part of the service changes.
    /// </summary>
    internal const string ServiceChanges = "service changes";

    /// <summary>
    /// The part of the previous service states.
    /// </summary>
    internal const string PreviousStates = "previous states";

    /// <summary>
    /// The part of the boot reads.
    /// </summary>
    internal const string BootReads = "boot reads";

    /// <summary>
    /// The part of the backup runs.
    /// </summary>
    internal const string BackupRuns = "backup runs";

    #endregion // Constants

    #region Fields

    private readonly ITimeSeriesReader _inner;

    private readonly Dictionary<string, (int Calls, long Ticks)> _parts = [];

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="TimingTimeSeriesReader"/> class.
    /// </summary>
    /// <param name="inner">The store</param>
    internal TimingTimeSeriesReader(ITimeSeriesReader inner)
    {
        _inner = inner;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the parts in the order they are printed.
    /// </summary>
    internal static IReadOnlyList<string> Parts { get; } = [RollupCpu, RollupOthers, Earliest, GapRows, GapRecords, KernelEvents, ServiceChanges, PreviousStates, BootReads, BackupRuns];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Clears the counts and times.
    /// </summary>
    internal void Reset()
    {
        _parts.Clear();
    }

    /// <summary>
    /// Gets the calls and the elapsed time of a part since the last reset.
    /// </summary>
    /// <param name="part">The part</param>
    /// <returns>The number of calls and the time in milliseconds</returns>
    internal (int Calls, double Milliseconds) Get(string part)
    {
        return _parts.TryGetValue(part, out var value) ? (value.Calls, Stopwatch.GetElapsedTime(0, value.Ticks).TotalMilliseconds) : (0, 0);
    }

    /// <summary>
    /// Runs a read and adds its call and time to a part.
    /// </summary>
    /// <typeparam name="T">The result type</typeparam>
    /// <param name="part">The part</param>
    /// <param name="read">The read</param>
    /// <returns>A task that returns the result of the read</returns>
    private async Task<T> TimeAsync<T>(string part, Func<Task<T>> read)
    {
        var start = Stopwatch.GetTimestamp();

        try
        {
            return await read().ConfigureAwait(false);
        }
        finally
        {
            var elapsed = Stopwatch.GetTimestamp() - start;
            var (calls, ticks) = _parts.GetValueOrDefault(part);

            _parts[part] = (calls + 1, ticks + elapsed);
        }
    }

    #endregion // Methods

    #region ITimeSeriesReader

    /// <inheritdoc/>
    public Task<LinePage> RawValuesAsync(RawValueQuery query, CancellationToken cancellationToken)
    {
        return TimeAsync(RollupOthers, () => _inner.RawValuesAsync(query, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<LinePage> RollupStepsAsync(RollupStepQuery query, CancellationToken cancellationToken)
    {
        return TimeAsync(query.Name == "cpu_percent" ? RollupCpu : RollupOthers, () => _inner.RollupStepsAsync(query, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<AgentGap>> GapsInRangeAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return TimeAsync(GapRows, () => _inner.GapsInRangeAsync(query, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<DateTimeOffset?> EarliestUnfoldedAsync(CancellationToken cancellationToken)
    {
        return TimeAsync(Earliest, () => _inner.EarliestUnfoldedAsync(cancellationToken));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ServiceStateRow>> ServiceChangesAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return TimeAsync(ServiceChanges, () => _inner.ServiceChangesAsync(query, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ServiceStateRow>> PreviousServiceStatesAsync(IReadOnlyList<string> units, DateTimeOffset before, CancellationToken cancellationToken)
    {
        return TimeAsync(PreviousStates, () => _inner.PreviousServiceStatesAsync(units, before, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<BootTime>> BootTimesAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return TimeAsync(BootReads, () => _inner.BootTimesAsync(query, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<GapRecordRow>> GapRecordsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return TimeAsync(GapRecords, () => _inner.GapRecordsAsync(query, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<KernelEventRow>> KernelEventsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return TimeAsync(KernelEvents, () => _inner.KernelEventsAsync(query, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<BackupRunRow>> BackupRunsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return TimeAsync(BackupRuns, () => _inner.BackupRunsAsync(query, cancellationToken));
    }

    #endregion // ITimeSeriesReader
}