namespace Vandox.Backend.Tests;

/// <summary>
/// The memory a piece of work used at its peak, as the ingest memory tests measure it.
/// </summary>
internal sealed class MemoryPeak
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryPeak"/> class.
    /// </summary>
    /// <param name="heapBytes">The highest managed heap that stayed alive during the work, above the heap before it</param>
    /// <param name="allocatedBytes">The managed bytes the work allocated in total</param>
    /// <param name="nativeBytes">The highest native memory of SQLite during the work, above its use before it</param>
    internal MemoryPeak(long heapBytes, long allocatedBytes, long nativeBytes)
    {
        HeapBytes = heapBytes;
        AllocatedBytes = allocatedBytes;
        NativeBytes = nativeBytes;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the highest managed heap that stayed alive during the work, above the heap before it.
    /// </summary>
    internal long HeapBytes { get; }

    /// <summary>
    /// Gets the managed bytes the work allocated in total.
    /// </summary>
    internal long AllocatedBytes { get; }

    /// <summary>
    /// Gets the highest native memory of SQLite during the work, above its use before it; 0 when the work used no SQLite.
    /// </summary>
    internal long NativeBytes { get; }

    /// <summary>
    /// Gets the sum of the live managed heap and the native memory at the peak, in bytes.
    /// </summary>
    internal long TotalBytes => HeapBytes + NativeBytes;

    #endregion // Properties
}