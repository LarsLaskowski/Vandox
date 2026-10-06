namespace Vandox.Import;

/// <summary>
/// A read-only, forward-only stream that observes what it passes on from another stream.
/// </summary>
internal abstract class FilterStream : Stream
{
    #region Fields

    private readonly Stream _inner;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterStream"/> class.
    /// </summary>
    /// <param name="inner">The stream to read</param>
    protected FilterStream(Stream inner)
    {
        _inner = inner;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Called with the bytes of every successful read.
    /// </summary>
    /// <param name="data">The bytes read</param>
    protected virtual void Observe(ReadOnlySpan<byte> data)
    {
    }

    /// <summary>
    /// Called with an exception the inner stream threw, before it is passed on.
    /// </summary>
    /// <param name="exception">The exception</param>
    protected virtual void Failed(Exception exception)
    {
    }

    /// <summary>
    /// Called before every read; throws to stop the read.
    /// </summary>
    protected virtual void BeforeRead()
    {
    }

    #endregion // Methods

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
        BeforeRead();

        try
        {
            var read = _inner.Read(buffer);

            if (read > 0)
            {
                Observe(buffer[..read]);
            }

            return read;
        }
        catch (Exception exception)
        {
            Failed(exception);

            throw;
        }
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        BeforeRead();

        try
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read > 0)
            {
                Observe(buffer.Span[..read]);
            }

            return read;
        }
        catch (Exception exception)
        {
            Failed(exception);

            throw;
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
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