namespace Vandox.Backend.Reports;

/// <summary>
/// A write-only view of a stream that only accepts asynchronous writes. A <see cref="StreamWriter"/> that is disposed calls the synchronous
/// <see cref="Flush"/> of its stream even when nothing is buffered, and the response stream of the server refuses it; here it does nothing, and the
/// stream below stays open.
/// </summary>
internal sealed class AsyncOnlyStream : Stream
{
    #region Fields

    private readonly Stream _inner;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncOnlyStream"/> class.
    /// </summary>
    /// <param name="inner">The stream the bytes go to</param>
    internal AsyncOnlyStream(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
    }

    #endregion // Constructors

    #region Stream

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("the stream has no length");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("the stream has no position");
        set => throw new NotSupportedException("the stream has no position");
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // Nothing is buffered here: every write goes to the stream below at once.
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return _inner.FlushAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("the stream is write-only");
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException("the stream cannot seek");
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        throw new NotSupportedException("the stream has no length");
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("the stream accepts asynchronous writes only");
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return _inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    #endregion // Stream
}