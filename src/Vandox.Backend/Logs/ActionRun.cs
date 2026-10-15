namespace Vandox.Backend.Logs;

#pragma warning disable S4487

/// <summary>
/// One action of a page (a search, a page change, a context): it runs under a deadline and ends when a newer action supersedes it.
/// </summary>
internal sealed class ActionRun : IDisposable
{
    #region Fields

    private readonly object _lock = new();

    private readonly TimeProvider _clock;

    private readonly TimeSpan _limit;

    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenSource? _deadline;

    private CancellationToken _token = new(true);

    private bool _superseded;

    private bool _disposed;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ActionRun"/> class.
    /// </summary>
    /// <param name="clock">The clock of the deadline</param>
    internal ActionRun(TimeProvider clock)
        : this(clock, LogsView.Deadline)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ActionRun"/> class with its own deadline.
    /// </summary>
    /// <param name="clock">The clock of the deadline</param>
    /// <param name="deadline">The time the action may take</param>
    internal ActionRun(TimeProvider clock, TimeSpan deadline)
    {
        _clock = clock;
        _limit = deadline;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a task that completes when the action has ended.
    /// </summary>
    internal Task Finished => _finished.Task;

    /// <summary>
    /// Gets the token of the action: it ends at the deadline or when the action is superseded. A run that never started has an ended token.
    /// </summary>
    internal CancellationToken Token
    {
        get
        {
            lock (_lock)
            {
                return _token;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a newer action or the end of the page superseded the action.
    /// </summary>
    internal bool Superseded
    {
        get
        {
            lock (_lock)
            {
                return _superseded;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the deadline ended the action.
    /// </summary>
    internal bool DeadlineExceeded
    {
        get
        {
            lock (_lock)
            {
                if (_superseded)
                {
                    return false;
                }

                return _token.IsCancellationRequested;
            }
        }
    }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Starts the deadline unless the action was superseded while it waited.
    /// </summary>
    internal void Start()
    {
        lock (_lock)
        {
            if (_superseded || _disposed)
            {
                return;
            }

            _deadline = new CancellationTokenSource(LogsView.Deadline, _clock);
            _token = _deadline.Token;
        }
    }

    /// <summary>
    /// Supersedes the action: its token ends.
    /// </summary>
    internal void Cancel()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _superseded = true;
            _deadline?.Cancel();
        }
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _deadline?.Dispose();
        }

        _finished.TrySetResult();
    }

    #endregion // IDisposable
}