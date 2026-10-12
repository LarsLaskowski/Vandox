using System.Buffers;

namespace Vandox.Storage;

/// <summary>
/// A buffer writer that keeps at most a given number of bytes; beyond it the bytes are dropped.
/// </summary>
internal sealed class BoundedBufferWriter : IBufferWriter<byte>
{
    #region Constants

    /// <summary>
    /// The smallest size handed out when the caller gives no size hint.
    /// </summary>
    private const int DefaultSize = 256;

    #endregion // Constants

    #region Fields

    private readonly int _maxBytes;
    private byte[]? _buffer;
    private byte[]? _scratch;
    private int _written;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Creates a writer.
    /// </summary>
    /// <param name="maxBytes">The most bytes kept</param>
    /// <exception cref="ArgumentOutOfRangeException">The limit is negative</exception>
    internal BoundedBufferWriter(int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        _maxBytes = maxBytes;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a value indicating whether more than the limit was advanced.
    /// </summary>
    internal bool Exceeded { get; private set; }

    /// <summary>
    /// Gets the bytes written; empty once the limit is exceeded.
    /// </summary>
    internal ReadOnlySpan<byte> WrittenSpan => Exceeded || _buffer is null ? ReadOnlySpan<byte>.Empty : _buffer.AsSpan(0, _written);

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the array to write to, with room for at least the hinted size behind the written bytes.
    /// </summary>
    /// <param name="sizeHint">The size hint</param>
    /// <returns>The array and the offset to write at</returns>
    private ArraySegment<byte> Prepare(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);

        var hint = Math.Max(sizeHint, DefaultSize);

        if (Exceeded)
        {
            if (_scratch is null || _scratch.Length < hint)
            {
                _scratch = new byte[hint];
            }

            return new ArraySegment<byte>(_scratch);
        }

        var needed = (long)_written + hint;

        if (_buffer is null || needed > _buffer.Length)
        {
            var capacity = _buffer is null ? 0L : (long)_buffer.Length * 2;
            var size = Math.Max(needed, Math.Min(capacity, (long)_maxBytes + hint));

            if (needed > Array.MaxLength)
            {
                throw new InvalidOperationException("The buffer cannot grow any further.");
            }

            var grown = new byte[Math.Min(size, Array.MaxLength)];

            _buffer?.CopyTo(grown, 0);

            _buffer = grown;
        }

        return new ArraySegment<byte>(_buffer, _written, _buffer.Length - _written);
    }

    #endregion // Methods

    #region IBufferWriter

    /// <inheritdoc />
    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (Exceeded)
        {
            return;
        }

        if (_buffer is null ? count > 0 : count > _buffer.Length - _written)
        {
            throw new InvalidOperationException("Cannot advance past the end of the buffer.");
        }

        _written += count;

        if (_written > _maxBytes)
        {
            Exceeded = true;
            _buffer = null;
            _written = 0;
        }
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        return Prepare(sizeHint);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        return Prepare(sizeHint);
    }

    #endregion // IBufferWriter
}