using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// A MariaDB history reader that answers from scripted status, stamp and snapshot data, records its calls in order, can hold or fail all calls or the calls of one member, and
/// can run an action when a call entered. The list of stamps answers in the order of the store, newest first, within the range and limit of the query.
/// </summary>
internal sealed class FakeMariaDbHistoryReader : IMariaDbHistoryReader, IDisposable
{
    #region Fields

    /// <summary>
    /// Guards the recorded calls, the counters and the holds.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// The recorded calls.
    /// </summary>
    private readonly List<(string Name, object? Argument, CancellationToken Token)> _calls = [];

    /// <summary>
    /// The holds of single members by member name.
    /// </summary>
    private readonly Dictionary<string, Task> _holds = [];

    /// <summary>
    /// The failures of single members by member name.
    /// </summary>
    private readonly Dictionary<string, Exception> _failures = [];

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
    /// Gets or sets an action that runs after each call entered, or <c>null</c> for none.
    /// </summary>
    internal Action? Entered { get; set; }

    /// <summary>
    /// Gets or sets the answer of <see cref="MariaDbStatusAsync"/>.
    /// </summary>
    internal MariaDbStatusResult Status { get; set; } = new();

    /// <summary>
    /// Gets or sets a function that gives the status of a query instead of <see cref="Status"/>, or <c>null</c> for none.
    /// </summary>
    internal Func<MariaDbStatusQuery, MariaDbStatusResult>? AnswerStatus { get; set; }

    /// <summary>
    /// Gets the stamps <see cref="ThreadSnapshotsAsync"/> chooses from.
    /// </summary>
    internal List<ThreadSnapshotStamp> Stamps { get; } = [];

    /// <summary>
    /// Gets or sets the answer of <see cref="ClosestThreadSnapshotAsync"/>.
    /// </summary>
    internal ThreadSnapshotStamp? Closest { get; set; }

    /// <summary>
    /// Gets the snapshots <see cref="ThreadSnapshotAsync"/> answers by record ID.
    /// </summary>
    internal Dictionary<long, ThreadSnapshot> Snapshots { get; } = [];

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
    /// Gets the recorded calls of one member.
    /// </summary>
    /// <param name="name">The member name</param>
    /// <returns>The calls in order</returns>
    internal IReadOnlyList<(string Name, object? Argument, CancellationToken Token)> CallsOf(string name)
    {
        return [.. Calls.Where(call => call.Name == name)];
    }

    /// <summary>
    /// Makes the calls of one member wait for a task, the calls of the other members not.
    /// </summary>
    /// <param name="name">The member name</param>
    /// <param name="hold">The task</param>
    internal void HoldFor(string name, Task hold)
    {
        lock (_lock)
        {
            _holds[name] = hold;
        }
    }

    /// <summary>
    /// Makes the calls of one member fail, the calls of the other members not.
    /// </summary>
    /// <param name="name">The member name</param>
    /// <param name="failure">The exception</param>
    internal void FailFor(string name, Exception failure)
    {
        lock (_lock)
        {
            _failures[name] = failure;
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
        Task? hold;
        Exception? failure;

        lock (_lock)
        {
            _calls.Add((name, argument, cancellationToken));
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);
            hold = _holds.GetValueOrDefault(name) ?? Hold;
            failure = _failures.GetValueOrDefault(name) ?? Failure;
        }

        _entered.Release();
        Entered?.Invoke();

        try
        {
            if (hold is not null)
            {
                await hold.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (failure is not null)
            {
                throw failure;
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

    #endregion // Methods

    #region IMariaDbHistoryReader

    /// <inheritdoc/>
    public Task<MariaDbStatusResult> MariaDbStatusAsync(MariaDbStatusQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(MariaDbStatusAsync), query, () => AnswerStatus is null ? Status : AnswerStatus(query), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ThreadSnapshotStamp>> ThreadSnapshotsAsync(ThreadSnapshotQuery query, CancellationToken cancellationToken)
    {
        return RunAsync<IReadOnlyList<ThreadSnapshotStamp>>(nameof(ThreadSnapshotsAsync),
                                                            query,
                                                            () => [.. Stamps.Where(stamp => stamp.CapturedAt >= query.From && stamp.CapturedAt <= query.To).OrderByDescending(stamp => stamp.CapturedAt).Take(query.Limit)],
                                                            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<ThreadSnapshotStamp?> ClosestThreadSnapshotAsync(DateTimeOffset at, TimeSpan window, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(ClosestThreadSnapshotAsync), (at, window), () => Closest, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<ThreadSnapshot?> ThreadSnapshotAsync(long recordId, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(ThreadSnapshotAsync), recordId, () => Snapshots.GetValueOrDefault(recordId), cancellationToken);
    }

    #endregion // IMariaDbHistoryReader

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        _entered.Dispose();
    }

    #endregion // IDisposable
}