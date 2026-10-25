using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// A record writer that keeps every batch and token it gets, counts the writes in flight, can hold a write until the test releases it, and can fail with a
/// scripted exception. With an inner writer it passes the batch on after the hold, so a real store can sit behind the gate.
/// </summary>
internal sealed class RecordingRecordWriter : IRecordWriter, IDisposable
{
    #region Fields

    private readonly IRecordWriter? _inner;

    private readonly Lock _lock = new();

    private readonly SemaphoreSlim _permits = new(0);

    private readonly SemaphoreSlim _entered = new(0);

    private readonly Queue<Exception> _failures = new();

    private bool _holding;

    private int _calls;

    private int _inFlight;

    private int _maxInFlight;

    private int _completed;

    private int _ended;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingRecordWriter"/> class.
    /// </summary>
    /// <param name="inner">The writer that stores the batches, <c>null</c> for a writer that stores nothing</param>
    internal RecordingRecordWriter(IRecordWriter? inner = null)
    {
        _inner = inner;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the batches in the order the writes started.
    /// </summary>
    internal List<RecordBatch> Batches { get; } = [];

    /// <summary>
    /// Gets the cancellation token of every write in the order the writes started.
    /// </summary>
    internal List<CancellationToken> Tokens { get; } = [];

    /// <summary>
    /// Gets or sets the result of a write without an inner writer; the batch's records counted as stored when <c>null</c>.
    /// </summary>
    internal Func<RecordBatch, WriteResult>? Outcome { get; set; }

    /// <summary>
    /// Gets the number of writes that started.
    /// </summary>
    internal int Calls => Volatile.Read(ref _calls);

    /// <summary>
    /// Gets the number of writes in flight right now.
    /// </summary>
    internal int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Gets the highest number of writes that were in flight at once.
    /// </summary>
    internal int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>
    /// Gets the number of writes that returned a result.
    /// </summary>
    internal int Completed => Volatile.Read(ref _completed);

    /// <summary>
    /// Gets the number of writes that ended in any way.
    /// </summary>
    internal int Ended => Volatile.Read(ref _ended);

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Makes every write that starts from now on wait until it is released.
    /// </summary>
    internal void Hold()
    {
        lock (_lock)
        {
            _holding = true;
        }
    }

    /// <summary>
    /// Lets one waiting (or the next) held write go on.
    /// </summary>
    internal void ReleaseOne()
    {
        _permits.Release();
    }

    /// <summary>
    /// Lets every held write go on and stops holding.
    /// </summary>
    internal void ReleaseAll()
    {
        lock (_lock)
        {
            _holding = false;
        }

        _permits.Release(1000);
    }

    /// <summary>
    /// Scripts the next write to fail with the exception.
    /// </summary>
    /// <param name="exception">The exception</param>
    internal void FailNext(Exception exception)
    {
        lock (_lock)
        {
            _failures.Enqueue(exception);
        }
    }

    /// <summary>
    /// Waits until the given number of writes have started, and fails at once when one of the watched requests ends before: it would never reach the writer.
    /// </summary>
    /// <param name="count">The number of writes</param>
    /// <param name="cancellationToken">Stops the wait</param>
    /// <param name="watched">Requests that are expected to stay in the writer until the test releases them</param>
    /// <returns>A task that completes when that many writes have started</returns>
    internal async Task WaitForCallsAsync(int count, CancellationToken cancellationToken, params Task[] watched)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        limit.CancelAfter(TimeSpan.FromSeconds(30));

        while (Calls < count)
        {
            var ended = Array.Find(watched, task => task.IsCompleted);

            if (ended is not null)
            {
                await ended;

                throw new InvalidOperationException($"a request ended before the write {count} started");
            }

            await Task.WhenAny([_entered.WaitAsync(limit.Token), .. watched]);
        }
    }

    #endregion // Methods

    #region IRecordWriter

    /// <inheritdoc />
    public async Task<WriteResult> WriteBatchAsync(RecordBatch batch, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        bool holding;

        lock (_lock)
        {
            Batches.Add(batch);
            Tokens.Add(cancellationToken);
            holding = _holding;

            if (_failures.Count > 0)
            {
                failure = _failures.Dequeue();
            }

            _calls++;
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);
        }

        _entered.Release();

        try
        {
            if (holding)
            {
                await _permits.WaitAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (failure is not null)
            {
                throw failure;
            }

            var result = _inner is null ? Outcome?.Invoke(batch) ?? new WriteResult(batch.Records.Count, 0) : await _inner.WriteBatchAsync(batch, cancellationToken);

            Interlocked.Increment(ref _completed);

            return result;
        }
        finally
        {
            lock (_lock)
            {
                _inFlight--;
                _ended++;
            }
        }
    }

    #endregion // IRecordWriter

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _permits.Dispose();
        _entered.Dispose();
    }

    #endregion // IDisposable
}