namespace Vandox.Storage;

/// <summary>
/// What a batch changed in an agent's gaps and clock state.
/// </summary>
public sealed class TimelineChange
{
    #region Properties

    /// <summary>
    /// Gets the number of gaps newly detected by the batch: the sequence gap of a jump in numbers and each new time-gap span; a row split off an existing gap counts in none.
    /// </summary>
    public int Opened { get; init; }

    /// <summary>
    /// Gets the number of gap rows the batch moved to closed.
    /// </summary>
    public int Closed { get; init; }

    /// <summary>
    /// Gets the number of sequence gaps the batch made final plus the time gaps it detected final.
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