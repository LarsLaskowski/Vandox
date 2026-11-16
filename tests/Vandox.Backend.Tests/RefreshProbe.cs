namespace Vandox.Backend.Tests;

/// <summary>
/// A refresh delegate for the overview refresher that counts its runs, records their tokens and can hold a run or wait for its cancellation.
/// </summary>
internal sealed class RefreshProbe : IDisposable
{
    #region Fields

    /// <summary>
    /// Guards the counters and the tokens.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// The tokens of the runs in start order.
    /// </summary>
    private readonly List<CancellationToken> _tokens = [];

    /// <summary>
    /// Released once per started run.
    /// </summary>
    private readonly SemaphoreSlim _started = new(0);

    /// <summary>
    /// Released once per ended run.
    /// </summary>
    private readonly SemaphoreSlim _ended = new(0);

    /// <summary>
    /// The number of runs in flight.
    /// </summary>
    private int _inFlight;

    /// <summary>
    /// The highest number of runs in flight at once.
    /// </summary>
    private int _maxInFlight;

    /// <summary>
    /// The number of ended runs.
    /// </summary>
    private int _ends;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets a task each run waits for, ignoring its token, or <c>null</c> to end at once.
    /// </summary>
    internal Task? Hold { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether each run waits until its token is cancelled and then throws the cancellation.
    /// </summary>
    internal bool WaitForCancellation { get; set; }

    /// <summary>
    /// Gets the number of started runs.
    /// </summary>
    internal int Starts
    {
        get
        {
            lock (_lock)
            {
                return _tokens.Count;
            }
        }
    }

    /// <summary>
    /// Gets the number of ended runs.
    /// </summary>
    internal int Ends
    {
        get
        {
            lock (_lock)
            {
                return _ends;
            }
        }
    }

    /// <summary>
    /// Gets the highest number of runs in flight at once.
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

    /// <summary>
    /// Gets the tokens of the runs in start order.
    /// </summary>
    internal IReadOnlyList<CancellationToken> Tokens
    {
        get
        {
            lock (_lock)
            {
                return [.. _tokens];
            }
        }
    }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Runs once as the refresh delegate.
    /// </summary>
    /// <param name="cancellationToken">The token the refresher passes</param>
    /// <returns>A task that completes when the run ends</returns>
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _tokens.Add(cancellationToken);
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);
        }

        _started.Release();

        try
        {
            if (Hold is not null)
            {
                await Hold.ConfigureAwait(false);
            }

            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_lock)
            {
                _inFlight--;
                _ends++;
            }

            _ended.Release();
        }
    }

    /// <summary>
    /// Waits until one more run has started.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait</param>
    /// <returns>A task that returns whether a run started within 20 seconds</returns>
    internal Task<bool> WaitForStartAsync(CancellationToken cancellationToken)
    {
        return _started.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
    }

    /// <summary>
    /// Waits until one more run has ended.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait</param>
    /// <returns>A task that returns whether a run ended within 20 seconds</returns>
    internal Task<bool> WaitForEndAsync(CancellationToken cancellationToken)
    {
        return _ended.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        _started.Dispose();
        _ended.Dispose();
    }

    #endregion // IDisposable
}