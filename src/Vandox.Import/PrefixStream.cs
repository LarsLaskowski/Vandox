namespace Vandox.Import;

/// <summary>
/// A read-only, forward-only stream that returns bytes already read from a stream (its head) before the rest of that stream.
/// </summary>
internal sealed class PrefixStream : FilterStream
{
    #region Fields

    private readonly byte[] _head;
    private int _position;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="PrefixStream"/> class.
    /// </summary>
    /// <param name="head">The bytes to return first</param>
    /// <param name="rest">The stream the head was read from</param>
    internal PrefixStream(byte[] head, Stream rest)
        : base(rest)
    {
        _head = head;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Copies the next bytes of the head into a buffer.
    /// </summary>
    /// <param name="buffer">The buffer</param>
    /// <returns>The number of bytes copied</returns>
    private int TakeHead(Span<byte> buffer)
    {
        var count = Math.Min(buffer.Length, _head.Length - _position);

        _head.AsSpan(_position, count).CopyTo(buffer);
        _position += count;

        return count;
    }

    #endregion // Methods

    #region Stream

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (_position < _head.Length)
        {
            return TakeHead(buffer);
        }

        return base.Read(buffer);
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < _head.Length)
        {
            return ValueTask.FromResult(TakeHead(buffer.Span));
        }

        return base.ReadAsync(buffer, cancellationToken);
    }

    #endregion // Stream
}