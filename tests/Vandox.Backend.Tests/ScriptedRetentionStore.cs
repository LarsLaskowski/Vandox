using Microsoft.Data.Sqlite;

using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// A retention store that plays scripted fold and removal steps, serves scripted kernel events to the kill scan, records every call in order and signals waiters when a call arrives.
/// </summary>
internal sealed class ScriptedRetentionStore : IRetentionStore
{
    #region Constants

    /// <summary>
    /// The SQLite result code of a busy database.
    /// </summary>
    internal const int SqliteBusy = 5;

    /// <summary>
    /// The SQLite result code of a disk I/O error.
    /// </summary>
    internal const int SqliteIoError = 10;

    /// <summary>
    /// The name of a fold call in <see cref="Calls"/>.
    /// </summary>
    internal const string Fold = "fold";

    /// <summary>
    /// The name of a read of the scan progress in <see cref="Calls"/>.
    /// </summary>
    internal const string Progress = "progress";

    /// <summary>
    /// The name of a read of kernel events in <see cref="Calls"/>.
    /// </summary>
    internal const string Scan = "scan";

    /// <summary>
    /// The name of a save of holds in <see cref="Calls"/>.
    /// </summary>
    internal const string Save = "save";

    /// <summary>
    /// The name of a removal call in <see cref="Calls"/>.
    /// </summary>
    internal const string Remove = "remove";

    #endregion // Constants

    #region Fields

    private readonly Lock _lock = new();

    private readonly Queue<Func<Task<RollupStepResult>>> _folds = new();

    private readonly Queue<Func<Task<RemovalStepResult>>> _removals = new();

    private readonly Queue<Exception> _saveFailures = new();

    private readonly List<string> _calls = [];

    private readonly List<DateTimeOffset> _foldTimes = [];

    private readonly List<DateTimeOffset> _removalTimes = [];

    private readonly List<RemovalCursor?> _cursors = [];

    private readonly List<RetentionPolicy> _policies = [];

    private readonly List<KillScanQuery> _scanQueries = [];

    private readonly List<RetentionHold> _holds = [];

    private readonly List<KillScanProgress> _saves = [];

    private readonly List<(string Call, int Count, TaskCompletionSource Source)> _waiters = [];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the progress the kill scan reads.
    /// </summary>
    internal KillScanProgress ScanProgress { get; set; } = new();

    /// <summary>
    /// Gets the kernel events the kill scan reads, in ID order.
    /// </summary>
    internal List<StoredRecord> KernelEvents { get; } = [];

    /// <summary>
    /// Gets the calls in the order they arrived.
    /// </summary>
    internal IReadOnlyList<string> Calls => Copy(_calls);

    /// <summary>
    /// Gets the current times passed to the fold steps.
    /// </summary>
    internal IReadOnlyList<DateTimeOffset> FoldTimes => Copy(_foldTimes);

    /// <summary>
    /// Gets the current times passed to the removal steps.
    /// </summary>
    internal IReadOnlyList<DateTimeOffset> RemovalTimes => Copy(_removalTimes);

    /// <summary>
    /// Gets the cursors passed to the removal steps.
    /// </summary>
    internal IReadOnlyList<RemovalCursor?> Cursors => Copy(_cursors);

    /// <summary>
    /// Gets the policies passed to the fold and removal steps.
    /// </summary>
    internal IReadOnlyList<RetentionPolicy> Policies => Copy(_policies);

    /// <summary>
    /// Gets the queries of the kill scan.
    /// </summary>
    internal IReadOnlyList<KillScanQuery> ScanQueries => Copy(_scanQueries);

    /// <summary>
    /// Gets every hold saved, in the order of the saves.
    /// </summary>
    internal IReadOnlyList<RetentionHold> SavedHolds => Copy(_holds);

    /// <summary>
    /// Gets the progress of every save.
    /// </summary>
    internal IReadOnlyList<KillScanProgress> SavedProgress => Copy(_saves);

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates the exception the store throws for a busy database.
    /// </summary>
    /// <param name="context">The operation text of the store</param>
    /// <returns>The exception</returns>
    internal static StoreException Busy(string context)
    {
        return new StoreException($"{context}: database is locked", new SqliteException("database is locked", SqliteBusy));
    }

    /// <summary>
    /// Creates the exception the store throws for a failed step.
    /// </summary>
    /// <param name="context">The operation text of the store</param>
    /// <returns>The exception</returns>
    internal static StoreException Failed(string context)
    {
        return new StoreException($"{context}: disk I/O error", new SqliteException("disk I/O error", SqliteIoError));
    }

    /// <summary>
    /// Adds a fold step that returns a result.
    /// </summary>
    /// <param name="values">The values folded</param>
    /// <param name="buckets">The buckets written</param>
    /// <param name="more">Whether more is left to fold</param>
    internal void AddFold(int values, int buckets, bool more)
    {
        var result = new RollupStepResult
                     {
                         Values = values,
                         Buckets = buckets,
                         More = more
                     };

        AddFold(() => Task.FromResult(result));
    }

    /// <summary>
    /// Adds a fold step that throws.
    /// </summary>
    /// <param name="exception">The exception</param>
    internal void AddFold(Exception exception)
    {
        AddFold(() => Task.FromException<RollupStepResult>(exception));
    }

    /// <summary>
    /// Adds a fold step that runs a function.
    /// </summary>
    /// <param name="step">The step</param>
    internal void AddFold(Func<Task<RollupStepResult>> step)
    {
        lock (_lock)
        {
            _folds.Enqueue(step);
        }
    }

    /// <summary>
    /// Adds a removal step that returns a result.
    /// </summary>
    /// <param name="result">The result</param>
    internal void AddRemoval(RemovalStepResult result)
    {
        AddRemoval(() => Task.FromResult(result));
    }

    /// <summary>
    /// Adds a removal step that throws.
    /// </summary>
    /// <param name="exception">The exception</param>
    internal void AddRemoval(Exception exception)
    {
        AddRemoval(() => Task.FromException<RemovalStepResult>(exception));
    }

    /// <summary>
    /// Adds a removal step that runs a function.
    /// </summary>
    /// <param name="step">The step</param>
    internal void AddRemoval(Func<Task<RemovalStepResult>> step)
    {
        lock (_lock)
        {
            _removals.Enqueue(step);
        }
    }

    /// <summary>
    /// Makes the next save of holds throw.
    /// </summary>
    /// <param name="exception">The exception</param>
    internal void FailNextSave(Exception exception)
    {
        lock (_lock)
        {
            _saveFailures.Enqueue(exception);
        }
    }

    /// <summary>
    /// Waits until a call has arrived the given number of times.
    /// </summary>
    /// <param name="call">The name of the call</param>
    /// <param name="count">The number of calls</param>
    /// <param name="cancellationToken">Cancels the wait</param>
    /// <returns>A task that completes when the calls have arrived</returns>
    internal Task WaitForCallsAsync(string call, int count, CancellationToken cancellationToken)
    {
        TaskCompletionSource source;

        lock (_lock)
        {
            if (_calls.Count(candidate => candidate == call) >= count)
            {
                return Task.CompletedTask;
            }

            source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((call, count, source));
        }

        return source.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Copies a list under the lock.
    /// </summary>
    /// <typeparam name="T">The element type</typeparam>
    /// <param name="list">The list</param>
    /// <returns>The copy</returns>
    private List<T> Copy<T>(List<T> list)
    {
        lock (_lock)
        {
            return [.. list];
        }
    }

    /// <summary>
    /// Records a call and releases the waiters it satisfies; must be called under the lock.
    /// </summary>
    /// <param name="call">The name of the call</param>
    private void Record(string call)
    {
        _calls.Add(call);

        foreach (var waiter in _waiters.Where(candidate => _calls.Count(name => name == candidate.Call) >= candidate.Count).ToList())
        {
            _waiters.Remove(waiter);
            waiter.Source.TrySetResult();
        }
    }

    #endregion // Methods

    #region IRetentionStore

    /// <inheritdoc/>
    public Task<RollupStepResult> FoldRollupsAsync(RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Func<Task<RollupStepResult>>? step;

        lock (_lock)
        {
            _foldTimes.Add(now);
            _policies.Add(policy);
            _folds.TryDequeue(out step);
            Record(Fold);
        }

        return step?.Invoke() ?? Task.FromResult(new RollupStepResult());
    }

    /// <inheritdoc/>
    public Task<KillScanProgress> KillScanProgressAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Record(Progress);

            return Task.FromResult(ScanProgress);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<StoredRecord>> ScanRecordsAsync(KillScanQuery query, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _scanQueries.Add(query);
            Record(Scan);

            IReadOnlyList<StoredRecord> page = KernelEvents.Where(record => record.Id > query.AboveId).OrderBy(record => record.Id).Take(query.Limit).ToList();

            return Task.FromResult(page);
        }
    }

    /// <inheritdoc/>
    public Task SaveKillScanAsync(IReadOnlyList<RetentionHold> holds, KillScanProgress progress, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Record(Save);

            if (_saveFailures.TryDequeue(out var failure))
            {
                return Task.FromException(failure);
            }

            _holds.AddRange(holds);
            _saves.Add(progress);
            ScanProgress = new KillScanProgress
                           {
                               ScannedId = progress.ScannedId,
                               HighestId = ScanProgress.HighestId
                           };

            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public Task<RemovalStepResult> RemoveExpiredAsync(RetentionPolicy policy, DateTimeOffset now, RemovalCursor? cursor, CancellationToken cancellationToken)
    {
        Func<Task<RemovalStepResult>>? step;

        lock (_lock)
        {
            _removalTimes.Add(now);
            _cursors.Add(cursor);
            _policies.Add(policy);
            _removals.TryDequeue(out step);
            Record(Remove);
        }

        return step?.Invoke() ?? Task.FromResult(new RemovalStepResult());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<RetentionHold>> HoldsAsync(DateTimeOffset rangeStart, DateTimeOffset rangeEnd, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<RetentionHold> holds = _holds.Where(hold => hold.Start <= rangeEnd && hold.End >= rangeStart).OrderBy(hold => hold.Start).ToList();

            return Task.FromResult(holds);
        }
    }

    #endregion // IRetentionStore
}