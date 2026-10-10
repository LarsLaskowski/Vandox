namespace Vandox.Core.Tests;

/// <summary>
/// A read-only stream that produces its content lazily: a header, a pattern repeated up to a length, and a trailer.
/// Reads complete synchronously and the content is never held in memory.
/// </summary>
internal sealed class PatternStream : Stream
{
    #region Fields

    private readonly byte[] _header;
    private readonly byte[] _pattern;
    private readonly long _patternLength;
    private readonly byte[] _trailer;
    private long _position;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="PatternStream"/> class.
    /// </summary>
    /// <param name="header">The bytes that come first</param>
    /// <param name="pattern">The bytes that are repeated</param>
    /// <param name="patternLength">The number of bytes of repeated pattern, the last repetition cut if needed</param>
    /// <param name="trailer">The bytes that come last</param>
    internal PatternStream(byte[] header, byte[] pattern, long patternLength, byte[] trailer)
    {
        _header = header;
        _pattern = Expand(pattern);
        _patternLength = patternLength;
        _trailer = trailer;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Repeats a short pattern up to a whole number of repetitions of at least 8 KiB, so that a read copies large blocks.
    /// </summary>
    /// <param name="pattern">The pattern</param>
    /// <returns>The expanded pattern</returns>
    private static byte[] Expand(byte[] pattern)
    {
        var repeat = Math.Max(1, 8192 / pattern.Length);
        var expanded = new byte[pattern.Length * repeat];

        for (var index = 0; index < repeat; index++)
        {
            pattern.CopyTo(expanded, index * pattern.Length);
        }

        return expanded;
    }

    /// <summary>
    /// Copies as many bytes as fit.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="target">The target</param>
    /// <returns>The number of bytes copied</returns>
    private static int Copy(ReadOnlySpan<byte> source, Span<byte> target)
    {
        var count = Math.Min(source.Length, target.Length);

        source[..count].CopyTo(target);

        return count;
    }

    /// <summary>
    /// Copies the part of the content at the current position that belongs to one section into the target.
    /// </summary>
    /// <param name="target">The target</param>
    /// <returns>The number of bytes copied</returns>
    private int FillAt(Span<byte> target)
    {
        if (_position < _header.Length)
        {
            return Copy(_header.AsSpan((int)_position), target);
        }

        var inPattern = _position - _header.Length;

        if (inPattern < _patternLength)
        {
            var remaining = _patternLength - inPattern;
            var limit = target[..(int)Math.Min(target.Length, remaining)];
            var offset = (int)(inPattern % _pattern.Length);

            return Copy(_pattern.AsSpan(offset), limit);
        }

        return Copy(_trailer.AsSpan((int)(inPattern - _patternLength)), target);
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
    public override long Length => _header.Length + _patternLength + _trailer.Length;

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

        while (written < buffer.Length && _position < Length)
        {
            var target = buffer[written..];
            var count = Math.Min(target.Length, FillAt(target));

            written += count;
            _position += count;
        }

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