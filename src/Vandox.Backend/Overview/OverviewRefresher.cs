namespace Vandox.Backend.Overview;

// Skeleton: removed with the implementation
#pragma warning disable RH2003, S2325, S4487
/// <summary>
/// Refreshes the overview on a timer and when the connection is restored.
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

    #region Methods

    /// <summary>
    /// Runs one refresh and starts the timer.
    /// </summary>
    public void Start()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods

    #region IAsyncDisposable

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    #endregion // IAsyncDisposable
}
#pragma warning restore RH2003, S2325, S4487