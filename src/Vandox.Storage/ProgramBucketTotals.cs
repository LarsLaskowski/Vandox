namespace Vandox.Storage;

/// <summary>
/// The totals of the values one fold step adds to a program in a bucket.
/// </summary>
internal sealed class ProgramBucketTotals
{
    #region Properties

    /// <summary>
    /// Gets the number of snapshots that name the program.
    /// </summary>
    internal long Seen { get; private set; }

    /// <summary>
    /// Gets the smallest resident memory.
    /// </summary>
    internal double RssMinimum { get; private set; }

    /// <summary>
    /// Gets the largest resident memory.
    /// </summary>
    internal double RssMaximum { get; private set; }

    /// <summary>
    /// Gets the sum of the resident memory.
    /// </summary>
    internal double RssSum { get; private set; }

    /// <summary>
    /// Gets the smallest proportional memory, or <c>null</c> when no snapshot had one.
    /// </summary>
    internal double? PssMinimum { get; private set; }

    /// <summary>
    /// Gets the largest proportional memory, or <c>null</c> when no snapshot had one.
    /// </summary>
    internal double? PssMaximum { get; private set; }

    /// <summary>
    /// Gets the sum of the proportional memory of the snapshots that had one.
    /// </summary>
    internal double PssSum { get; private set; }

    /// <summary>
    /// Gets the smallest CPU percentage.
    /// </summary>
    internal double CpuMinimum { get; private set; }

    /// <summary>
    /// Gets the largest CPU percentage.
    /// </summary>
    internal double CpuMaximum { get; private set; }

    /// <summary>
    /// Gets the sum of the CPU percentages.
    /// </summary>
    internal double CpuSum { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds the values of one snapshot.
    /// </summary>
    /// <param name="rss">The resident memory of the program in the snapshot</param>
    /// <param name="pss">The proportional memory of the program, or <c>null</c> when the snapshot has none</param>
    /// <param name="cpu">The CPU percentage of the program</param>
    internal void Add(double rss, double? pss, double cpu)
    {
        if (Seen == 0)
        {
            RssMinimum = rss;
            RssMaximum = rss;
            CpuMinimum = cpu;
            CpuMaximum = cpu;
        }
        else
        {
            RssMinimum = Math.Min(RssMinimum, rss);
            RssMaximum = Math.Max(RssMaximum, rss);
            CpuMinimum = Math.Min(CpuMinimum, cpu);
            CpuMaximum = Math.Max(CpuMaximum, cpu);
        }

        Seen++;
        RssSum = Clamp(RssSum + rss);
        CpuSum = Clamp(CpuSum + cpu);

        if (pss is not { } value)
        {
            return;
        }

        PssMinimum = PssMinimum is { } minimum ? Math.Min(minimum, value) : value;
        PssMaximum = PssMaximum is { } maximum ? Math.Max(maximum, value) : value;
        PssSum = Clamp(PssSum + value);
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