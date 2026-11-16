namespace Vandox.Backend.Security;

/// <summary>
/// One open pause of a <see cref="SessionActivity"/>; disposing it more than once ends the pause only once.
/// </summary>
internal sealed class SessionPauseScope : IDisposable
{
    #region Fields

    private readonly SessionActivity _activity;

    private int _released;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionPauseScope"/> class.
    /// </summary>
    /// <param name="activity">The activity whose pause this scope holds</param>
    internal SessionPauseScope(SessionActivity activity)
    {
        _activity = activity;
    }

    #endregion // Constructors

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _activity.Release();
        }
    }

    #endregion // IDisposable
}