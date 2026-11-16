using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// A time-series reader for a real <see cref="TimeSeriesService"/>: it answers the line reads from scripted lines per series and the marker reads from scripted
/// kernel events, answers every other read with nothing, records its calls in order, can hold a call until released and can fail.
/// </summary>
internal sealed class ScriptedTimeSeriesReader : ITimeSeriesReader, IDisposable
{
    #region Fields

    /// <summary>
    /// Guards the recorded calls and the counters.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// The recorded calls.
    /// </summary>
    private readonly List<(string Name, object? Argument, CancellationToken Token)> _calls = [];

    /// <summary>
    /// The scripted lines by source and name.
    /// </summary>
    private readonly Dictionary<(string Source, string Name), List<MetricLine>> _lines = [];

    /// <summary>
    /// Released once per call that entered.
    /// </summary>
    private readonly SemaphoreSlim _entered = new(0);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets a task every call waits for before it answers, or <c>null</c> to answer at once.
    /// </summary>
    internal Task? Hold { get; set; }

    /// <summary>
    /// Gets or sets an exception every call throws after it entered, or <c>null</c> to answer.
    /// </summary>
    internal Exception? Failure { get; set; }

    /// <summary>
    /// Gets or sets an action that runs after each call entered, or <c>null</c> for none.
    /// </summary>
    internal Action? Entered { get; set; }

    /// <summary>
    /// Gets the scripted kernel events the marker reads choose from.
    /// </summary>
    internal List<KernelEventRow> Kernel { get; } = [];

    /// <summary>
    /// Gets the recorded calls: the member name, its argument and its token.
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
    /// Gets the queries of the raw value reads, in order.
    /// </summary>
    internal IReadOnlyList<RawValueQuery> RawQueries => [.. Calls.Where(call => call.Name == nameof(RawValuesAsync)).Select(call => (RawValueQuery)call.Argument!)];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates a point whose minimum, average, maximum and last value are one value.
    /// </summary>
    /// <param name="at">The time</param>
    /// <param name="value">The value</param>
    /// <returns>The point</returns>
    internal static LinePoint Point(DateTimeOffset at, double value)
    {
        return new LinePoint
               {
                   At = at,
                   LastAt = at,
                   Minimum = value,
                   Average = value,
                   Maximum = value,
                   Last = value,
                   Count = 1
               };
    }

    /// <summary>
    /// Creates a kernel event row of an OOM kill.
    /// </summary>
    /// <param name="id">The record ID</param>
    /// <param name="capturedAt">The time</param>
    /// <param name="command">The victim command</param>
    /// <param name="pid">The victim process ID</param>
    /// <returns>The row</returns>
    internal static KernelEventRow Kill(long id, DateTimeOffset capturedAt, string command, long pid)
    {
        return new KernelEventRow
               {
                   Id = id,
                   CapturedAt = capturedAt,
                   Type = KernelEvent.TypeOomKill,
                   BootId = string.Empty,
                   VictimCommand = command,
                   VictimPid = pid
               };
    }

    /// <summary>
    /// Scripts one line of a series: its points at the given times and values, with the label set <c>labels</c>.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="name">The metric name</param>
    /// <param name="labels">The label set of the line</param>
    /// <param name="points">The points</param>
    internal void AddLine(string source, string name, string labels, params LinePoint[] points)
    {
        lock (_lock)
        {
            if (_lines.TryGetValue((source, name), out var lines) == false)
            {
                lines = [];
                _lines[(source, name)] = lines;
            }

            lines.Add(new MetricLine
                      {
                          Labels = labels,
                          Points = points
                      });
        }
    }

    /// <summary>
    /// Waits until one more call has entered.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait</param>
    /// <returns>A task that returns whether a call entered within 20 seconds</returns>
    internal Task<bool> WaitForEntryAsync(CancellationToken cancellationToken)
    {
        return _entered.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
    }

    /// <summary>
    /// Records a call, waits for the hold and answers or fails.
    /// </summary>
    /// <typeparam name="TResult">The type of the answer</typeparam>
    /// <param name="name">The member name</param>
    /// <param name="argument">The argument</param>
    /// <param name="answer">Gives the answer</param>
    /// <param name="cancellationToken">The token of the call</param>
    /// <returns>A task that returns the answer</returns>
    private async Task<TResult> RunAsync<TResult>(string name, object? argument, Func<TResult> answer, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _calls.Add((name, argument, cancellationToken));
        }

        _entered.Release();
        Entered?.Invoke();

        if (Hold is not null)
        {
            await Hold.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        return answer();
    }

    /// <summary>
    /// Gets the scripted lines of a series.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="name">The metric name</param>
    /// <returns>The page of lines</returns>
    private LinePage LinesOf(string source, string name)
    {
        lock (_lock)
        {
            return new LinePage
                   {
                       Lines = _lines.TryGetValue((source, name), out var lines) ? [.. lines] : []
                   };
        }
    }

    /// <summary>
    /// Gets the scripted kernel events of a range in the order of the store.
    /// </summary>
    /// <param name="query">The query</param>
    /// <returns>The rows</returns>
    private IReadOnlyList<KernelEventRow> KernelOf(TimeRangeQuery query)
    {
        return [.. Kernel.Where(row => row.CapturedAt >= query.From && row.CapturedAt <= query.To).OrderBy(row => row.CapturedAt).ThenBy(row => row.Id)];
    }

    #endregion // Methods

    #region ITimeSeriesReader

    /// <inheritdoc />
    public Task<LinePage> RawValuesAsync(RawValueQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(RawValuesAsync), query, () => LinesOf(query.Source, query.Name), cancellationToken);
    }

    /// <inheritdoc />
    public Task<LinePage> RollupStepsAsync(RollupStepQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(RollupStepsAsync), query, () => LinesOf(query.Source, query.Name), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentGap>> GapsInRangeAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync<IReadOnlyList<AgentGap>>(nameof(GapsInRangeAsync), query, () => [], cancellationToken);
    }

    /// <inheritdoc />
    public Task<DateTimeOffset?> EarliestUnfoldedAsync(CancellationToken cancellationToken)
    {
        return RunAsync<DateTimeOffset?>(nameof(EarliestUnfoldedAsync), null, () => null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ServiceStateRow>> ServiceChangesAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync<IReadOnlyList<ServiceStateRow>>(nameof(ServiceChangesAsync), query, () => [], cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ServiceStateRow>> PreviousServiceStatesAsync(IReadOnlyList<string> units, DateTimeOffset before, CancellationToken cancellationToken)
    {
        return RunAsync<IReadOnlyList<ServiceStateRow>>(nameof(PreviousServiceStatesAsync), units, () => [], cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<BootTime>> BootTimesAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync<IReadOnlyList<BootTime>>(nameof(BootTimesAsync), query, () => [], cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GapRecordRow>> GapRecordsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync<IReadOnlyList<GapRecordRow>>(nameof(GapRecordsAsync), query, () => [], cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<KernelEventRow>> KernelEventsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(KernelEventsAsync), query, () => KernelOf(query), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<BackupRunRow>> BackupRunsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return RunAsync<IReadOnlyList<BackupRunRow>>(nameof(BackupRunsAsync), query, () => [], cancellationToken);
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