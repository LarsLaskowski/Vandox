using Vandox.Core.Incidents;

namespace Vandox.Core.Reports;

#pragma warning disable RH2003, S2325

/// <summary>
/// A capped series of compact process snapshot points that answers the lookback questions.
/// </summary>
public sealed class SnapshotSeries
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotSeries"/> class with the default capacity.
    /// </summary>
    public SnapshotSeries()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotSeries"/> class.
    /// </summary>
    /// <param name="capacity">The most points kept</param>
    public SnapshotSeries(int capacity)
    {
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of points kept.
    /// </summary>
    public int Count => throw new NotImplementedException();

    /// <summary>
    /// Gets a value indicating whether points beyond the capacity were dropped.
    /// </summary>
    public bool Truncated => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a snapshot.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="programs">The programs of the snapshot</param>
    /// <exception cref="ArgumentException">The time is earlier than the previous one</exception>
    public void Add(DateTimeOffset capturedAt, IReadOnlyDictionary<string, ProgramUsage> programs)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Computes the panel memory that counts as high: the nearest-rank percentile of the panel memory of all snapshots.
    /// </summary>
    /// <returns>The threshold in bytes; 0 without panel memory</returns>
    public ulong PanelThresholdBytes()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Judges whether the backup program ran in a range.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <returns>Unknown without a snapshot in the range, yes with the backup program there, else no</returns>
    public Indicator Backup(DateTimeOffset from, DateTimeOffset to)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Judges whether the panel memory was high in a range.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="thresholdBytes">The threshold in bytes</param>
    /// <returns>Unknown without a threshold or a snapshot in the range, yes at or above the threshold, else no</returns>
    public Indicator PanelHigh(DateTimeOffset from, DateTimeOffset to, ulong thresholdBytes)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}