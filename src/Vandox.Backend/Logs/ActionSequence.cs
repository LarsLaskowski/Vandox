namespace Vandox.Backend.Logs;

/// <summary>
/// Runs the actions of a page one after another: a new action supersedes the running one and starts only after it has ended.
/// </summary>
internal sealed class ActionSequence : IDisposable
{
    #region Fields

    private readonly TimeProvider _clock;

    private ActionRun? _current;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ActionSequence"/> class.
    /// </summary>
    /// <param name="clock">The clock of the deadlines</param>
    internal ActionSequence(TimeProvider clock)
    {
        _clock = clock;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Supersedes the running action and waits for its end; the returned run is the new action. The caller disposes it when the action has ended.
    /// </summary>
    /// <returns>A task that returns the run, which is superseded when a still newer action arrived while it waited</returns>
    internal async Task<ActionRun> BeginAsync()
    {
        var run = new ActionRun(_clock);
        var previous = _current;

        _current = run;
        previous?.Cancel();

        if (previous is not null)
        {
            await previous.Finished.ConfigureAwait(true);
        }

        run.Start();

        return run;
    }

    /// <summary>
    /// Supersedes the running action without starting another one.
    /// </summary>
    internal void CancelCurrent()
    {
        _current?.Cancel();
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        CancelCurrent();
    }

    #endregion // IDisposable
}