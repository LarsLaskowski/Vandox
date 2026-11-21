using Vandox.Storage;

namespace Vandox.Backend.History;

/// <summary>
/// The snapshot closest to a moment.
/// </summary>
public sealed class ClosestSnapshot
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ClosestSnapshot"/> class.
    /// </summary>
    /// <param name="record">The record</param>
    /// <param name="before">The capture time of the stamp before the moment when the record is the one after it</param>
    public ClosestSnapshot(StoredRecord record, DateTimeOffset? before)
    {
        Record = record;
        Before = before;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the record.
    /// </summary>
    public StoredRecord Record { get; }

    /// <summary>
    /// Gets the capture time of the latest snapshot before the moment when the record shown is later.
    /// </summary>
    public DateTimeOffset? Before { get; }

    #endregion // Properties
}