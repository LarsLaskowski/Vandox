namespace Vandox.Backend.Overview;

/// <summary>
/// Refreshes the overview on a timer and when the connection is restored. Refreshes never overlap, a refresh that is due while one runs or
/// while the connection is down is skipped, and each refresh gets a token that is cancelled after the deadline or when the refresher is disposed.
/// </summary>
public sealed class OverviewRefresher : IAsyncDisposable
{
    #region Fields

    /// <summary>
    /// The clock of the timer and the deadline.
    /// </summary>
    private readonly TimeProvider _clock;

    /// <summary>
    /// The connection of the circuit.
    /// </summary>
    private readonly CircuitConnection _connection;

    /// <summary>
    /// The refresh to run.
    /// </summary>
    private readonly Func<CancellationToken, Task> _refresh;

    /// <summary>
    /// The callback for an exception of a refresh.
    /// </summary>
    private readonly Action<Exception> _fault;

    /// <summary>
    /// Cancelled when the refresher is disposed.
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// Guards <see cref="_current"/> and <see cref="_disposed"/>.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// The timer of the refreshes.
    /// </summary>
    private ITimer? _timer;

    /// <summary>
    /// The running refresh, or the last one.
    /// </summary>
    private Task _current = Task.CompletedTask;

    /// <summary>
    /// Whether a refresh runs.
    /// </summary>
    private int _running;

    /// <summary>
    /// Whether the refresher was disposed.
    /// </summary>
    private bool _disposed;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="OverviewRefresher"/> class.
    /// </summary>
    /// <param name="clock">The clock of the timer and the deadline</param>
    /// <param name="connection">The connection of the circuit</param>
    /// <param name="refresh">The refresh to run</param>
    /// <param name="fault">Receives an exception of a refresh other than a cancellation</param>
    public OverviewRefresher(TimeProvider clock, CircuitConnection connection, Func<CancellationToken, Task> refresh, Action<Exception> fault)
    {
        _clock = clock;
        _connection = connection;
        _refresh = refresh;
        _fault = fault;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the task of the running refresh, which completes only after the refresh cleared its running state, or a completed task when none runs.
    /// </summary>
    internal Task Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Runs one refresh and starts the timer.
    /// </summary>
    public void Start()
    {
        _connection.Restored += OnRestored;
        StartRefresh();

        lock (_lock)
        {
            if (_disposed == false)
            {
                _timer = _clock.CreateTimer(OnTick, null, OverviewView.RefreshInterval, OverviewView.RefreshInterval);
            }
        }
    }

    /// <summary>
    /// Starts a refresh unless one runs, the connection is down or the refresher was disposed.
    /// </summary>
    private void StartRefresh()
    {
        if (_connection.IsConnected == false)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                return;
            }

            _current = Task.Run(RunAsync, CancellationToken.None);
        }
    }

    /// <summary>
    /// Runs one refresh with its deadline and hands an exception that is no cancellation to the fault callback.
    /// </summary>
    /// <returns>A task that completes when the refresh ended</returns>
    private async Task RunAsync()
    {
        try
        {
            using var deadline = new CancellationTokenSource(OverviewView.RefreshDeadline, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, deadline.Token);

            await _refresh(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the deadline or the disposal ended the refresh, which is no fault
        }
        catch (Exception exception)
        {
            _fault(exception);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// Starts a refresh when the timer ticks.
    /// </summary>
    /// <param name="state">The unused state</param>
    private void OnTick(object? state)
    {
        StartRefresh();
    }

    /// <summary>
    /// Starts a refresh when the connection is restored.
    /// </summary>
    /// <param name="sender">The connection</param>
    /// <param name="arguments">The unused arguments</param>
    private void OnRestored(object? sender, EventArgs arguments)
    {
        StartRefresh();
    }

    #endregion // Methods

    #region IAsyncDisposable

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Task current;
        ITimer? timer;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            current = _current;
            timer = _timer;
        }

        _connection.Restored -= OnRestored;

        if (timer is not null)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await current.ConfigureAwait(false);
        _lifetime.Dispose();
    }

    #endregion // IAsyncDisposable
}