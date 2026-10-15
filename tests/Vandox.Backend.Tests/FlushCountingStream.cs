namespace Vandox.Backend.Tests;

/// <summary>
/// A memory stream that counts its flushes.
/// </summary>
internal sealed class FlushCountingStream : MemoryStream
{
    #region Properties

    /// <summary>
    /// Gets the number of asynchronous flushes.
    /// </summary>
    internal int AsyncFlushes { get; private set; }

    /// <summary>
    /// Gets the number of synchronous flushes.
    /// </summary>
    internal int SyncFlushes { get; private set; }

    #endregion // Properties

    #region Stream

    /// <inheritdoc />
    public override void Flush()
    {
        SyncFlushes++;
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        AsyncFlushes++;

        return Task.CompletedTask;
    }

    #endregion // Stream
}