namespace Vandox.Core.Tests;

/// <summary>
/// A read-only stream that produces a list of byte segments, each repeated a given number of times, without keeping the repeated bytes in memory.
/// </summary>
internal sealed class SegmentStream : Stream
{
    #region Fields

    private readonly IReadOnlyList<(byte[] Bytes, long Repeat)> _segments;
    private readonly long _length;
    private int _segment;
    private long _repetition;
    private int _offset;
    private long _position;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentStream"/> class.
    /// </summary>
    /// <param name="segments">The segments with the number of times each one is repeated</param>
    internal SegmentStream(IReadOnlyList<(byte[] Bytes, long Repeat)> segments)
    {
        _segments = segments;
        _length = segments.Sum(segment => segment.Bytes.Length * segment.Repeat);
    }

    #endregion // Constructors

    #region Stream

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _length;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
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
        var written = 0;

        while (written < buffer.Length && _segment < _segments.Count)
        {
            var (bytes, repeat) = _segments[_segment];

            if (_repetition >= repeat || bytes.Length == 0)
            {
                _segment++;
                _repetition = 0;
                _offset = 0;

                continue;
            }

            var count = Math.Min(buffer.Length - written, bytes.Length - _offset);

            bytes.AsSpan(_offset, count).CopyTo(buffer[written..]);
            written += count;
            _offset += count;

            if (_offset == bytes.Length)
            {
                _offset = 0;
                _repetition++;
            }
        }

        _position += written;

        return written;
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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