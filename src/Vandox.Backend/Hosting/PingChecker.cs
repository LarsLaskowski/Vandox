namespace Vandox.Backend;

/// <summary>
/// Runs at most one database ping at a time. A request that arrives while a ping runs waits for that ping's result; a ping
/// that never returns is abandoned by the requests, never stacked.
/// </summary>
internal sealed class PingChecker
{
    #region Fields

    private readonly Func<CancellationToken, Task> _ping;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private Task? _inflight;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="PingChecker"/> class.
    /// </summary>
    /// <param name="ping">The check of the database</param>
    /// <param name="timeout">The limit of one ping, and of one request waiting for it</param>
    internal PingChecker(Func<CancellationToken, Task> ping, TimeSpan timeout)
    {
        _ping = ping;
        _timeout = timeout;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Returns when the shared ping answered.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait</param>
    /// <returns>A task that completes when the database answered</returns>
    /// <exception cref="TimeoutException">The database did not answer within the timeout</exception>
    internal Task CheckAsync(CancellationToken cancellationToken)
    {
        return Join().WaitAsync(_timeout, cancellationToken);
    }

    /// <summary>
    /// Returns the running ping, starting one when none runs.
    /// </summary>
    /// <returns>The ping</returns>
    private Task Join()
    {
        lock (_gate)
        {
            if (_inflight is null)
            {
                _inflight = PingAsync();
            }

            return _inflight;
        }
    }

    /// <summary>
    /// Runs the database check with its own deadline, independent of any single request.
    /// </summary>
    /// <returns>A task that completes with the result of the check</returns>
    private async Task PingAsync()
    {
        await Task.Yield();

        try
        {
            using var deadline = new CancellationTokenSource(_timeout);

            await _ping(deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _inflight = null;
            }
        }
    }

    #endregion // Methods
}