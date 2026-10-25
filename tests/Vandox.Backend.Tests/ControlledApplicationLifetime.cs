using Microsoft.Extensions.Hosting;

namespace Vandox.Backend.Tests;

/// <summary>
/// An application lifetime whose stop signal the test fires, for tests that build an ingest endpoint without a host.
/// </summary>
internal sealed class ControlledApplicationLifetime : IHostApplicationLifetime, IDisposable
{
    #region Fields

    private readonly CancellationTokenSource _stopping = new();

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Fires the stop signal and runs its callbacks on the calling thread.
    /// </summary>
    internal void Stop()
    {
        _stopping.Cancel();
    }

    #endregion // Methods

    #region IHostApplicationLifetime

    /// <inheritdoc />
    public CancellationToken ApplicationStarted => CancellationToken.None;

    /// <inheritdoc />
    public CancellationToken ApplicationStopping => _stopping.Token;

    /// <inheritdoc />
    public CancellationToken ApplicationStopped => CancellationToken.None;

    /// <inheritdoc />
    public void StopApplication()
    {
        _stopping.Cancel();
    }

    #endregion // IHostApplicationLifetime

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Dispose();
    }

    #endregion // IDisposable
}