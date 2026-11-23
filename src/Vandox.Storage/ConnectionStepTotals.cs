namespace Vandox.Storage;

/// <summary>
/// The totals of the snapshots of a source per step of a connection read; a step without a total has no snapshots.
/// </summary>
internal sealed class ConnectionStepTotals
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionStepTotals"/> class.
    /// </summary>
    /// <param name="grid">The steps</param>
    internal ConnectionStepTotals(ProgramStepGrid grid)
    {
        var count = grid.Count;

        Grid = grid;
        Snapshots = new long[count];
        Incomplete = new long[count];
        Capped = new long[count];
        ProcessSums = new double[count];
        Named = new double[count];
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the steps.
    /// </summary>
    internal ProgramStepGrid Grid { get; }

    /// <summary>
    /// Gets the number of snapshots per step.
    /// </summary>
    internal long[] Snapshots { get; }

    /// <summary>
    /// Gets the number of incomplete snapshots per step.
    /// </summary>
    internal long[] Incomplete { get; }

    /// <summary>
    /// Gets the number of snapshots per step whose addresses the bucket cap dropped.
    /// </summary>
    internal long[] Capped { get; }

    /// <summary>
    /// Gets the clamped sums of the connections of all processes per step.
    /// </summary>
    internal double[] ProcessSums { get; }

    /// <summary>
    /// Gets the clamped sums of the named programs per step, which the read of their points fills.
    /// </summary>
    internal double[] Named { get; }

    #endregion // Properties
}