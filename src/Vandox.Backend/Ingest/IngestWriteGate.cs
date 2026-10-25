namespace Vandox.Backend.Ingest;

/// <summary>
/// Counts the writes in flight under one lock and refuses every write that tries to start once the gate is closed, so no write
/// can register after the drain began.
/// </summary>
internal sealed class IngestWriteGate
{
    #region Fields

    /// <summary>
    /// Guards the state below.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// The number of writes in flight.
    /// </summary>
    private int _inFlight;

    /// <summary>
    /// Whether the gate is closed.
    /// </summary>
    private bool _closed;

    /// <summary>
    /// Completes when the gate is closed and the last write left; <c>null</c> until somebody waits for it.
    /// </summary>
    private TaskCompletionSource? _drained;

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Registers a write that is about to start.
    /// </summary>
    /// <returns><c>true</c> when the write may start; <c>false</c> once the gate is closed</returns>
    internal bool TryEnter()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return false;
            }

            _inFlight++;

            return true;
        }
    }

    /// <summary>
    /// Unregisters a write that ended.
    /// </summary>
    internal void Exit()
    {
        lock (_lock)
        {
            _inFlight--;

            if (_closed && _inFlight == 0)
            {
                _drained?.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Closes the gate at once and atomically; the endpoint calls it from its <c>ApplicationStopping</c> registration, so from the stop
    /// signal on no write starts. Calling it again changes nothing.
    /// </summary>
    internal void Close()
    {
        lock (_lock)
        {
            _closed = true;
        }
    }

    /// <summary>
    /// Closes the gate, when it is not closed yet, and waits until the writes in flight have ended or the token is cancelled.
    /// </summary>
    /// <param name="cancellationToken">Ends the wait</param>
    /// <returns>A task that completes when no write is in flight; it is cancelled when the token is cancelled while writes remain</returns>
    internal Task CloseAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _closed = true;

            if (_inFlight == 0)
            {
                return Task.CompletedTask;
            }

            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            return _drained.Task.WaitAsync(cancellationToken);
        }
    }

    #endregion // Methods
}