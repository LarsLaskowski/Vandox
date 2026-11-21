namespace Vandox.Storage;

/// <summary>
/// The totals of the snapshots one fold step adds to the bucket of a source.
/// </summary>
internal sealed class ProgramSourceTotals
{
    #region Properties

    /// <summary>
    /// Gets the number of snapshots.
    /// </summary>
    internal long Snapshots { get; private set; }

    /// <summary>
    /// Gets the number of snapshots that carry proportional memory.
    /// </summary>
    internal long PssSnapshots { get; private set; }

    /// <summary>
    /// Gets the number of snapshots that do not list every program.
    /// </summary>
    internal long Incomplete { get; private set; }

    /// <summary>
    /// Gets the sum of the resident memory of all programs.
    /// </summary>
    internal double RssSum { get; private set; }

    /// <summary>
    /// Gets the sum of the proportional memory of all programs of the snapshots that carry it.
    /// </summary>
    internal double PssSum { get; private set; }

    /// <summary>
    /// Gets the sum of the CPU percentages of all programs.
    /// </summary>
    internal double CpuSum { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Includes one snapshot.
    /// </summary>
    /// <param name="rss">The resident memory of all programs of the snapshot</param>
    /// <param name="pss">The proportional memory of all programs, or <c>null</c> when the snapshot has none</param>
    /// <param name="cpu">The CPU percentage of all programs</param>
    /// <param name="incomplete">Whether the snapshot's programs do not account for all of its processes</param>
    internal void Include(double rss, double? pss, double cpu, bool incomplete)
    {
        Snapshots++;
        RssSum = Clamp(RssSum + rss);
        CpuSum = Clamp(CpuSum + cpu);

        if (pss is { } value)
        {
            PssSnapshots++;
            PssSum = Clamp(PssSum + value);
        }

        if (incomplete)
        {
            Incomplete++;
        }
    }

    /// <summary>
    /// Limits a sum to the range of a double.
    /// </summary>
    /// <param name="sum">The sum, which may be infinite</param>
    /// <returns>The sum within the largest finite values</returns>
    private static double Clamp(double sum)
    {
        return Math.Clamp(sum, -double.MaxValue, double.MaxValue);
    }

    #endregion // Methods
}