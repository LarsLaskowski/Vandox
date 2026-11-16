using System.Globalization;

using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// A history reader that records its calls in order, answers the stamp reads from scripted stamps as the store does, can hold a call until released and can fail.
/// </summary>
internal sealed class FakeHistoryReader : IHistoryReader, IDisposable
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
    /// The recorded call sequence.
    /// </summary>
    private readonly List<string> _sequence = [];

    /// <summary>
    /// Released once per call that entered.
    /// </summary>
    private readonly SemaphoreSlim _entered = new(0);

    /// <summary>
    /// The number of calls running.
    /// </summary>
    private int _inFlight;

    /// <summary>
    /// The highest number of calls that ran at once.
    /// </summary>
    private int _maxInFlight;

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
    /// Gets or sets the answer of <see cref="StoredMetricsAsync"/>.
    /// </summary>
    internal StoredMetricList Metrics { get; set; } = new();

    /// <summary>
    /// Gets the stored snapshot stamps the stamp reads choose from.
    /// </summary>
    internal List<SnapshotStamp> Stamps { get; } = [];

    /// <summary>
    /// Gets the stored records by ID that <see cref="SnapshotAsync"/> returns.
    /// </summary>
    internal Dictionary<long, StoredRecord> Records { get; } = [];

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
    /// Gets the call sequence as <c>StoredMetrics</c>, <c>Stamp &lt;kind&gt; &lt;side&gt;</c> and <c>Snapshot &lt;id&gt;</c>.
    /// </summary>
    internal IReadOnlyList<string> Sequence
    {
        get
        {
            lock (_lock)
            {
                return [.. _sequence];
            }
        }
    }

    /// <summary>
    /// Gets the highest number of calls that ran at once.
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
    /// <param name="sequence">The entry of the call sequence</param>
    /// <param name="answer">Gives the answer</param>
    /// <param name="cancellationToken">The token of the call</param>
    /// <returns>A task that returns the answer</returns>
    private async Task<TResult> RunAsync<TResult>(string name, object? argument, string sequence, Func<TResult> answer, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _calls.Add((name, argument, cancellationToken));
            _sequence.Add(sequence);
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

            if (Failure is not null)
            {
                throw Failure;
            }

            return answer();
        }
        finally
        {
            lock (_lock)
            {
                _inFlight--;
            }
        }
    }

    /// <summary>
    /// Chooses the stamp a store gives for a query: the closest on the side inside the window, the lower ID on equal times.
    /// </summary>
    /// <param name="query">The query</param>
    /// <returns>The stamp, or <c>null</c></returns>
    private SnapshotStamp? Choose(SnapshotQuery query)
    {
        var ofKind = Stamps.Where(stamp => stamp.Kind == query.Kind);

        if (query.Side == SnapshotSide.AtOrBefore)
        {
            return ofKind.Where(stamp => stamp.CapturedAt <= query.At && stamp.CapturedAt >= query.At - query.Window)
                         .OrderByDescending(stamp => stamp.CapturedAt)
                         .ThenBy(stamp => stamp.Id)
                         .FirstOrDefault();
        }

        return ofKind.Where(stamp => stamp.CapturedAt > query.At && stamp.CapturedAt <= query.At + query.Window)
                     .OrderBy(stamp => stamp.CapturedAt)
                     .ThenBy(stamp => stamp.Id)
                     .FirstOrDefault();
    }

    #endregion // Methods

    #region IHistoryReader

    /// <inheritdoc/>
    public Task<StoredMetricList> StoredMetricsAsync(int limit, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(StoredMetricsAsync), limit, "StoredMetrics", () => Metrics, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<SnapshotStamp?> SnapshotStampAsync(SnapshotQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(SnapshotStampAsync), query, $"Stamp {query.Kind} {query.Side}", () => Choose(query), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<StoredRecord?> SnapshotAsync(SnapshotStamp stamp, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(SnapshotAsync), stamp, string.Create(CultureInfo.InvariantCulture, $"Snapshot {stamp.Id}"), () => Records.GetValueOrDefault(stamp.Id), cancellationToken);
    }

    #endregion // IHistoryReader

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        _entered.Dispose();
    }

    #endregion // IDisposable
}