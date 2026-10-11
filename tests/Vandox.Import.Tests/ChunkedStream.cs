namespace Vandox.Import.Tests;

/// <summary>
/// A read-only stream over bytes that returns at most a fixed number of bytes per read.
/// </summary>
internal sealed class ChunkedStream : Stream
{
    #region Fields

    private readonly byte[] _data;
    private readonly int _chunk;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ChunkedStream"/> class.
    /// </summary>
    /// <param name="data">The bytes</param>
    /// <param name="chunk">The most bytes one read returns</param>
    internal ChunkedStream(byte[] data, int chunk)
    {
        _data = data;
        _chunk = chunk;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of bytes handed out so far.
    /// </summary>
    internal int Consumed { get; private set; }

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
        var count = Math.Min(Math.Min(buffer.Length, _chunk), _data.Length - Consumed);

        _data.AsSpan(Consumed, count).CopyTo(buffer);
        Consumed += count;

        return count;
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(Read(buffer.Span));
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