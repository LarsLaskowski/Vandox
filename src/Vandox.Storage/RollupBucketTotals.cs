namespace Vandox.Storage;

/// <summary>
/// The totals of the values one fold step adds to a bucket.
/// </summary>
internal sealed class RollupBucketTotals
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="RollupBucketTotals"/> class with its first value.
    /// </summary>
    /// <param name="id">The record ID of the value</param>
    /// <param name="capturedAt">The capture time of the value</param>
    /// <param name="value">The value</param>
    internal RollupBucketTotals(long id, long capturedAt, double value)
    {
        Minimum = value;
        Maximum = value;
        Sum = value;
        Count = 1;
        Last = value;
        LastAt = capturedAt;
        LastId = id;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the smallest value.
    /// </summary>
    internal double Minimum { get; private set; }

    /// <summary>
    /// Gets the largest value.
    /// </summary>
    internal double Maximum { get; private set; }

    /// <summary>
    /// Gets the sum of the values.
    /// </summary>
    internal double Sum { get; private set; }

    /// <summary>
    /// Gets the number of values.
    /// </summary>
    internal long Count { get; private set; }

    /// <summary>
    /// Gets the value with the latest capture time, of the higher record ID on a tie.
    /// </summary>
    internal double Last { get; private set; }

    /// <summary>
    /// Gets the capture time of the last value.
    /// </summary>
    internal long LastAt { get; private set; }

    /// <summary>
    /// Gets the record ID of the last value.
    /// </summary>
    internal long LastId { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a value.
    /// </summary>
    /// <param name="id">The record ID of the value</param>
    /// <param name="capturedAt">The capture time of the value</param>
    /// <param name="value">The value</param>
    internal void Add(long id, long capturedAt, double value)
    {
        Minimum = Math.Min(Minimum, value);
        Maximum = Math.Max(Maximum, value);
        Sum += value;
        Count++;

        if (capturedAt > LastAt || (capturedAt == LastAt && id > LastId))
        {
            Last = value;
            LastAt = capturedAt;
            LastId = id;
        }
    }

    #endregion // Methods
}