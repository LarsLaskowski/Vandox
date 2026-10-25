using Microsoft.Extensions.Hosting;

namespace Vandox.Backend.Tests;

/// <summary>
/// An application lifetime whose tokens never fire, for tests that build an ingest endpoint without a host.
/// </summary>
internal sealed class StoppedApplicationLifetime : IHostApplicationLifetime
{
    #region IHostApplicationLifetime

    /// <inheritdoc />
    public CancellationToken ApplicationStarted => CancellationToken.None;

    /// <inheritdoc />
    public CancellationToken ApplicationStopping => CancellationToken.None;

    /// <inheritdoc />
    public CancellationToken ApplicationStopped => CancellationToken.None;

    /// <inheritdoc />
    public void StopApplication()
    {
        // Nothing runs in a test without a host.
    }

    #endregion // IHostApplicationLifetime
}