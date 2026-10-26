namespace Vandox.Storage;

/// <summary>
/// What a batch changed in an agent's gaps and clock state.
/// </summary>
public sealed class TimelineChange
{
    #region Properties

    /// <summary>
    /// Gets the number of gaps opened by the batch.
    /// </summary>
    public int Opened { get; init; }

    /// <summary>
    /// Gets the number of gaps closed by the batch.
    /// </summary>
    public int Closed { get; init; }

    /// <summary>
    /// Gets the number of gaps made final by the batch.
    /// </summary>
    public int Final { get; init; }

    /// <summary>
    /// Gets the number of open sequence gaps after the batch.
    /// </summary>
    public int Open { get; init; }

    /// <summary>
    /// Gets the clock offset in nanoseconds after the batch, if it is known.
    /// </summary>
    public long? OffsetNs { get; init; }

    /// <summary>
    /// Gets a value that is <see langword="true"/> when the batch moved the offset beyond the allowance, <see langword="false"/> when it moved it back, and <see langword="null"/> when it crossed nothing.
    /// </summary>
    public bool? ClockOff { get; init; }

    #endregion // Properties
}