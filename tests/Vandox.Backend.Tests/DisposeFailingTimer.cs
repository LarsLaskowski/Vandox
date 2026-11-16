namespace Vandox.Backend.Tests;

/// <summary>
/// A timer that works like the one it wraps but fails when it is disposed asynchronously, so a test can make a disposal path throw.
/// </summary>
internal sealed class DisposeFailingTimer : ITimer
{
    #region Fields

    /// <summary>
    /// The wrapped timer.
    /// </summary>
    private readonly ITimer _inner;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="DisposeFailingTimer"/> class.
    /// </summary>
    /// <param name="inner">The wrapped timer</param>
    internal DisposeFailingTimer(ITimer inner)
    {
        _inner = inner;
    }

    #endregion // Constructors

    #region ITimer

    /// <inheritdoc/>
    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        return _inner.Change(dueTime, period);
    }

    #endregion // ITimer

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        _inner.Dispose();
    }

    #endregion // IDisposable

    #region IAsyncDisposable

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();

        throw new InvalidOperationException("the timer failed to dispose");
    }

    #endregion // IAsyncDisposable
}