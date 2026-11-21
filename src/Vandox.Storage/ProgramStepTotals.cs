namespace Vandox.Storage;

/// <summary>
/// The totals of the snapshots of a source per step of a per-program read; a step without a total has no snapshots.
/// </summary>
internal sealed class ProgramStepTotals
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgramStepTotals"/> class.
    /// </summary>
    /// <param name="grid">The steps</param>
    internal ProgramStepTotals(ProgramStepGrid grid)
    {
        var count = grid.Count;

        Grid = grid;
        Named = new double[count];
        Snapshots = new long[count];
        PssSnapshots = new long[count];
        Incomplete = new long[count];
        Rss = new double[count];
        Pss = new double[count];
        Cpu = new double[count];
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the steps.
    /// </summary>
    internal ProgramStepGrid Grid { get; }

    /// <summary>
    /// Gets the clamped sums of the named programs per step, which the read of their points fills.
    /// </summary>
    internal double[] Named { get; }

    /// <summary>
    /// Gets the number of snapshots per step.
    /// </summary>
    internal long[] Snapshots { get; }

    /// <summary>
    /// Gets the number of snapshots with PSS per step.
    /// </summary>
    internal long[] PssSnapshots { get; }

    /// <summary>
    /// Gets the number of incomplete snapshots per step.
    /// </summary>
    internal long[] Incomplete { get; }

    /// <summary>
    /// Gets the clamped RSS sums of all programs per step.
    /// </summary>
    internal double[] Rss { get; }

    /// <summary>
    /// Gets the clamped PSS sums of all programs per step.
    /// </summary>
    internal double[] Pss { get; }

    /// <summary>
    /// Gets the clamped CPU sums of all programs per step.
    /// </summary>
    internal double[] Cpu { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Tells whether every snapshot of the range has PSS.
    /// </summary>
    /// <returns><c>true</c> when the PSS snapshots are as many as the snapshots</returns>
    internal bool AllPss()
    {
        return Snapshots.Sum() == PssSnapshots.Sum();
    }

    /// <summary>
    /// Returns the sums of the measure per step.
    /// </summary>
    /// <param name="measure">The measure</param>
    /// <param name="key">The memory key</param>
    /// <returns>The sums</returns>
    internal double[] Sums(ProgramMeasure measure, ProgramMemoryKey key)
    {
        if (measure == ProgramMeasure.Cpu)
        {
            return Cpu;
        }

        return key == ProgramMemoryKey.Pss ? Pss : Rss;
    }

    #endregion // Methods
}