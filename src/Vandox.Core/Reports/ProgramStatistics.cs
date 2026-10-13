namespace Vandox.Core.Reports;

/// <summary>
/// The memory use of one program over the snapshots that hold it.
/// </summary>
internal sealed class ProgramStatistics
{
    #region Fields

    private UInt128 _sum;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgramStatistics"/> class.
    /// </summary>
    /// <param name="program">The program key</param>
    internal ProgramStatistics(string program)
    {
        Program = program;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the program key.
    /// </summary>
    internal string Program { get; }

    /// <summary>
    /// Gets the number of snapshots that hold the program.
    /// </summary>
    internal long Snapshots { get; private set; }

    /// <summary>
    /// Gets the highest RSS in bytes.
    /// </summary>
    internal ulong PeakRssBytes { get; private set; }

    /// <summary>
    /// Gets the capture time of the first snapshot that reached the peak.
    /// </summary>
    internal DateTimeOffset PeakAt { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Counts the program in one more snapshot.
    /// </summary>
    /// <param name="capturedAt">The capture time of the snapshot</param>
    /// <param name="rssBytes">The summed RSS of the program in it</param>
    internal void Add(DateTimeOffset capturedAt, ulong rssBytes)
    {
        Snapshots++;
        _sum += rssBytes;

        if (Snapshots == 1 || rssBytes > PeakRssBytes)
        {
            PeakRssBytes = rssBytes;
            PeakAt = capturedAt;
        }
    }

    /// <summary>
    /// Summarizes the program.
    /// </summary>
    /// <param name="totalSnapshots">The number of all snapshots</param>
    /// <returns>The summary</returns>
    internal ConsumerSummary ToSummary(long totalSnapshots)
    {
        return new ConsumerSummary
               {
                   Program = Program,
                   Snapshots = Snapshots,
                   Share = (double)Snapshots / totalSnapshots,
                   MeanRssBytes = (ulong)(_sum / (ulong)Snapshots),
                   PeakRssBytes = PeakRssBytes,
                   PeakAt = PeakAt
               };
    }

    #endregion // Methods
}