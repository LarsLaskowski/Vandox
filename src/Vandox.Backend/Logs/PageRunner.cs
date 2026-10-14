#pragma warning disable RH2003, S2325

using Vandox.Backend.Hosting;
using Vandox.Storage;

namespace Vandox.Backend.Logs;

/// <summary>
/// Runs the store calls of one page action by action, with the deadline, and turns the failures into the fixed texts of the page.
/// </summary>
internal sealed class PageRunner : IDisposable
{
    #region Fields

    private readonly ActionSequence _actions;

    private readonly ILogger _logger;

    private readonly string _deadlineText;

    private readonly string _failureText;

    private readonly Action<string> _fail;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="PageRunner"/> class.
    /// </summary>
    /// <param name="clock">The clock of the deadlines</param>
    /// <param name="logger">The logger of the page</param>
    /// <param name="deadlineText">The text shown when an action runs into the deadline</param>
    /// <param name="failureText">The text shown when the store fails</param>
    /// <param name="fail">Shows a text on the page</param>
    internal PageRunner(TimeProvider clock, ILogger logger, string deadlineText, string failureText, Action<string> fail)
    {
        _actions = new ActionSequence(clock);
        _logger = logger;
        _deadlineText = deadlineText;
        _failureText = failureText;
        _fail = fail;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PageRunner"/> class that reports a failure through a callback.
    /// </summary>
    /// <param name="clock">The clock of the deadlines</param>
    /// <param name="logFailure">Logs the failure of the store</param>
    /// <param name="deadlineText">The text shown when an action runs into the deadline</param>
    /// <param name="failureText">The text shown when the store fails</param>
    /// <param name="fail">Shows a text on the page</param>
    internal PageRunner(TimeProvider clock, Action<Exception> logFailure, string deadlineText, string failureText, Action<string> fail)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Runs an action after the running one has ended. An action that is superseded ends silently; a refused query, the deadline and a failure of the store show their
    /// text, and only a failure is logged.
    /// </summary>
    /// <param name="action">The action; it runs its store calls one after another with the token it gets</param>
    /// <returns>A task that completes when the action has ended</returns>
    internal async Task RunAsync(Func<CancellationToken, Task> action)
    {
        using var run = await _actions.BeginAsync().ConfigureAwait(true);

        if (run.Superseded)
        {
            return;
        }

        try
        {
            await action(run.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Report(run, run.DeadlineExceeded ? _deadlineText : string.Empty);
        }
        catch (InvalidQueryException exception)
        {
            Report(run, LogsView.Refused(exception));
        }
        catch (Exception exception) when (exception is StoreException or InvalidOperationException)
        {
            if (run.Superseded)
            {
                return;
            }

            _logger.LogsReadFailed(exception);
            Report(run, _failureText);
        }
    }

    /// <summary>
    /// Supersedes the running action without starting another one.
    /// </summary>
    internal void CancelCurrent()
    {
        _actions.CancelCurrent();
    }

    /// <summary>
    /// Shows a text unless the action was superseded or the text is empty.
    /// </summary>
    /// <param name="run">The action</param>
    /// <param name="text">The text</param>
    private void Report(ActionRun run, string text)
    {
        if (run.Superseded || text.Length == 0)
        {
            return;
        }

        _fail(text);
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _actions.Dispose();
    }

    #endregion // IDisposable
}