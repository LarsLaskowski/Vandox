namespace Vandox.Backend.Tests;

/// <summary>
/// A response body in memory that starts its <see cref="StartingResponseFeature"/> with the first byte and refuses synchronous I/O as the server does.
/// </summary>
internal sealed class StartingResponseBody : MemoryStream
{
    #region Constants

    private const string SynchronousRefusal = "Synchronous operations are disallowed.";

    #endregion // Constants

    #region Fields

    private readonly StartingResponseFeature _feature;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="StartingResponseBody"/> class.
    /// </summary>
    /// <param name="feature">The feature that reports the start</param>
    internal StartingResponseBody(StartingResponseFeature feature)
    {
        _feature = feature;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a value indicating whether bytes were written.
    /// </summary>
    internal bool Started => _feature.Started;

    #endregion // Properties

    #region Stream

    /// <inheritdoc />
    public override void Flush()
    {
        throw new InvalidOperationException(SynchronousRefusal);
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new InvalidOperationException(SynchronousRefusal);
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        throw new InvalidOperationException(SynchronousRefusal);
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        _feature.Started = _feature.Started || buffer.Length > 0;
        base.Write(buffer.Span);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        _feature.Started = _feature.Started || count > 0;
        base.Write(buffer, offset, count);

        return Task.CompletedTask;
    }

    #endregion // Stream
}