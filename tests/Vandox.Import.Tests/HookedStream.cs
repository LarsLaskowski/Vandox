namespace Vandox.Import.Tests;

/// <summary>
/// A read-only stream that calls an action before every read of the stream it wraps.
/// </summary>
internal sealed class HookedStream : Stream
{
    #region Fields

    private readonly Stream _inner;
    private readonly Action _beforeRead;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="HookedStream"/> class.
    /// </summary>
    /// <param name="inner">The stream to read</param>
    /// <param name="beforeRead">Called before every read</param>
    internal HookedStream(Stream inner, Action beforeRead)
    {
        _inner = inner;
        _beforeRead = beforeRead;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of reads started so far.
    /// </summary>
    internal int Reads { get; private set; }

    #endregion // Properties

    #region Stream

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();

        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        Reads++;
        _beforeRead();

        return _inner.Read(buffer);
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Reads++;
        _beforeRead();

        return _inner.ReadAsync(buffer, cancellationToken);
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    #endregion // Stream
}