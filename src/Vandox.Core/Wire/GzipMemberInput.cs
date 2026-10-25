namespace Vandox.Core.Wire;

/// <summary>
/// Hands the inflater the reader's buffered input one byte per read, so the inflater cannot take a byte past the end of its member.
/// </summary>
internal sealed class GzipMemberInput : Stream
{
    #region Fields

    /// <summary>
    /// The reader whose buffer is read.
    /// </summary>
    private readonly GzipMemberReader _owner;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GzipMemberInput"/> class.
    /// </summary>
    /// <param name="owner">The reader whose buffer is read</param>
    internal GzipMemberInput(GzipMemberReader owner)
    {
        _owner = owner;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the next byte when the buffer of the reader is empty.
    /// </summary>
    /// <param name="buffer">The buffer to read into</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The number of bytes read, 0 at the end of the input</returns>
    private async ValueTask<int> ReadFromInputAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var value = await _owner.TryReadByteAsync(cancellationToken).ConfigureAwait(false);

        if (value < 0)
        {
            return 0;
        }

        buffer.Span[0] = (byte)value;

        return 1;
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
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (_owner.TryTakeBuffered(out var buffered))
        {
            buffer[0] = buffered;

            return 1;
        }

        var one = new byte[1];
        var read = ReadAsync(one, CancellationToken.None).AsTask().GetAwaiter().GetResult();

        if (read > 0)
        {
            buffer[0] = one[0];
        }

        return read;
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return new ValueTask<int>(0);
        }

        if (_owner.TryTakeBuffered(out var buffered))
        {
            buffer.Span[0] = buffered;

            return new ValueTask<int>(1);
        }

        return ReadFromInputAsync(buffer, cancellationToken);
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