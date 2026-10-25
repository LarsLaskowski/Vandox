namespace Vandox.Core.Wire;

#pragma warning disable RH2003, S2325
/// <summary>
/// Reads the decompressed bytes of a gzip stream made of one or more members and checks the framing of each member
/// (header, CRC-32 and size trailer) by the rules of the wire format.
/// </summary>
internal sealed class GzipMemberReader : Stream
{
    #region Fields

    private readonly Stream _input;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GzipMemberReader"/> class.
    /// </summary>
    /// <param name="input">The compressed stream</param>
    internal GzipMemberReader(Stream input)
    {
        _input = input;
    }

    #endregion // Constructors

    #region Stream

    /// <inheritdoc />
    public override bool CanRead => _input.CanRead;

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
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
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