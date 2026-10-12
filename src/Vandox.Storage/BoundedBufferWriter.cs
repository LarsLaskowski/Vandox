using System.Buffers;

namespace Vandox.Storage;

#pragma warning disable RH2003, S2325

/// <summary>
/// A buffer writer that keeps at most a given number of bytes; beyond it the bytes are dropped.
/// </summary>
internal sealed class BoundedBufferWriter : IBufferWriter<byte>
{
    #region Constructors

    /// <summary>
    /// Creates a writer.
    /// </summary>
    /// <param name="maxBytes">The most bytes kept</param>
    /// <exception cref="ArgumentOutOfRangeException">The limit is negative</exception>
    internal BoundedBufferWriter(int maxBytes)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a value indicating whether more than the limit was advanced.
    /// </summary>
    internal bool Exceeded => throw new NotImplementedException();

    /// <summary>
    /// Gets the bytes written; empty once the limit is exceeded.
    /// </summary>
    internal ReadOnlySpan<byte> WrittenSpan => throw new NotImplementedException();

    #endregion // Properties

    #region IBufferWriter

    /// <inheritdoc />
    public void Advance(int count)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        throw new NotImplementedException();
    }

    #endregion // IBufferWriter
}