namespace Vandox.Storage;

/// <summary>
/// The stamp of a thread snapshot: the record and the counts of its threads.
/// </summary>
public sealed class ThreadSnapshotStamp
{
    #region Properties

    /// <summary>
    /// Gets the ID of the record.
    /// </summary>
    public long RecordId { get; init; }

    /// <summary>
    /// Gets the capture time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Gets the number of threads.
    /// </summary>
    public int Threads { get; init; }

    /// <summary>
    /// Gets the number of threads that are not sleeping.
    /// </summary>
    public int Running { get; init; }

    /// <summary>
    /// Gets the seconds of the longest running thread, capped at <see cref="ThreadSnapshot.MaxSeconds"/>, <c>null</c> when none is running.
    /// </summary>
    public long? LongestSeconds { get; init; }

    #endregion // Properties
}