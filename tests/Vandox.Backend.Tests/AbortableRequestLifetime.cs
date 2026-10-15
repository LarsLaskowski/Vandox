using Microsoft.AspNetCore.Http.Features;

namespace Vandox.Backend.Tests;

/// <summary>
/// A request lifetime whose token is cancelled by <see cref="Abort"/> or <see cref="Close"/>, as the server does when the connection ends.
/// </summary>
internal sealed class AbortableRequestLifetime : IHttpRequestLifetimeFeature, IDisposable
{
    #region Fields

    private readonly CancellationTokenSource _source = new();

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets a value indicating whether <see cref="Abort"/> was called.
    /// </summary>
    internal bool Aborted { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Ends the request as a client that closes the connection does, without a call of <see cref="Abort"/>.
    /// </summary>
    internal void Close()
    {
        _source.Cancel();
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _source.Dispose();
    }

    #endregion // IDisposable

    #region IHttpRequestLifetimeFeature

    /// <inheritdoc />
    public CancellationToken RequestAborted
    {
        get => _source.Token;
        set => throw new NotSupportedException("the token belongs to the source");
    }

    /// <inheritdoc />
    public void Abort()
    {
        Aborted = true;
        _source.Cancel();
    }

    #endregion // IHttpRequestLifetimeFeature
}