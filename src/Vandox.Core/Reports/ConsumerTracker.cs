using Vandox.Core.Incidents;

namespace Vandox.Core.Reports;

#pragma warning disable RH2003, S2325

/// <summary>
/// Tracks the memory use of every program over all snapshots and ranks the permanent and the transient consumers.
/// </summary>
public sealed class ConsumerTracker
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsumerTracker"/> class with the default cap.
    /// </summary>
    public ConsumerTracker()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsumerTracker"/> class.
    /// </summary>
    /// <param name="maxPrograms">The most programs tracked</param>
    public ConsumerTracker(int maxPrograms)
    {
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of snapshots added.
    /// </summary>
    public long Snapshots => throw new NotImplementedException();

    /// <summary>
    /// Gets the program entries not tracked because the cap was reached.
    /// </summary>
    public long UntrackedEntries => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a snapshot.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="programs">The programs of the snapshot</param>
    public void Add(DateTimeOffset capturedAt, IReadOnlyDictionary<string, ProgramUsage> programs)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Lists the programs present in at least 90 percent of the snapshots, ranked by mean RSS.
    /// </summary>
    /// <param name="count">The most entries</param>
    /// <returns>The permanent consumers</returns>
    public IReadOnlyList<ConsumerSummary> Permanent(int count)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Lists the programs present in fewer than 90 percent of the snapshots, ranked by peak RSS.
    /// </summary>
    /// <param name="count">The most entries</param>
    /// <returns>The transient consumers</returns>
    public IReadOnlyList<ConsumerSummary> Transient(int count)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}