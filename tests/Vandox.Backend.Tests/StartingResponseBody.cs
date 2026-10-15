namespace Vandox.Backend.Tests;

/// <summary>
/// A response body in memory that starts its <see cref="StartingResponseFeature"/> with the first byte.
/// </summary>
internal sealed class StartingResponseBody : MemoryStream
{
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
    public override void Write(byte[] buffer, int offset, int count)
    {
        _feature.Started = _feature.Started || count > 0;
        base.Write(buffer, offset, count);
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _feature.Started = _feature.Started || buffer.Length > 0;
        base.Write(buffer);
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _feature.Started = _feature.Started || buffer.Length > 0;

        return base.WriteAsync(buffer, cancellationToken);
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _feature.Started = _feature.Started || count > 0;

        return base.WriteAsync(buffer, offset, count, cancellationToken);
    }

    #endregion // Stream
}