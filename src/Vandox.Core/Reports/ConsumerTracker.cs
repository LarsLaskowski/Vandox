using Vandox.Core.Incidents;

namespace Vandox.Core.Reports;

/// <summary>
/// Tracks the memory use of every program over all snapshots and ranks the permanent and the transient consumers.
/// </summary>
public sealed class ConsumerTracker
{
    #region Constants

    private const long PermanentNumerator = 9;
    private const long PermanentDenominator = 10;

    #endregion // Constants

    #region Fields

    private readonly int _maxPrograms;
    private readonly Dictionary<string, ProgramStatistics> _programs = new(StringComparer.Ordinal);

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsumerTracker"/> class with the default cap.
    /// </summary>
    public ConsumerTracker()
        : this(ReportLimits.MaxPrograms)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsumerTracker"/> class.
    /// </summary>
    /// <param name="maxPrograms">The most programs tracked</param>
    public ConsumerTracker(int maxPrograms)
    {
        _maxPrograms = maxPrograms;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of snapshots added.
    /// </summary>
    public long Snapshots { get; private set; }

    /// <summary>
    /// Gets the program entries not tracked because the cap was reached.
    /// </summary>
    public long UntrackedEntries { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a snapshot. Its programs are keyed by name; a name that is a path is reduced to its last part.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="programs">The programs of the snapshot</param>
    public void Add(DateTimeOffset capturedAt, IReadOnlyDictionary<string, ProgramUsage> programs)
    {
        Snapshots++;

        foreach (var (name, usage) in programs)
        {
            var key = MemoryAnalysis.ProgramKey(name);

            if (_programs.TryGetValue(key, out var statistics))
            {
                statistics.Add(capturedAt, usage.RssBytes);
            }
            else if (_programs.Count < _maxPrograms)
            {
                statistics = new ProgramStatistics(key);
                statistics.Add(capturedAt, usage.RssBytes);
                _programs[key] = statistics;
            }
            else
            {
                UntrackedEntries++;
            }
        }
    }

    /// <summary>
    /// Lists the programs present in at least 90 percent of the snapshots, ranked by mean RSS.
    /// </summary>
    /// <param name="count">The most entries</param>
    /// <returns>The permanent consumers</returns>
    public IReadOnlyList<ConsumerSummary> Permanent(int count)
    {
        return _programs.Values
                        .Where(IsPermanent)
                        .Select(statistics => statistics.ToSummary(Snapshots))
                        .OrderByDescending(summary => summary.MeanRssBytes)
                        .ThenBy(summary => summary.Program, StringComparer.Ordinal)
                        .Take(count)
                        .ToList();
    }

    /// <summary>
    /// Lists the programs present in fewer than 90 percent of the snapshots, ranked by peak RSS.
    /// </summary>
    /// <param name="count">The most entries</param>
    /// <returns>The transient consumers</returns>
    public IReadOnlyList<ConsumerSummary> Transient(int count)
    {
        return _programs.Values
                        .Where(IsTransient)
                        .Select(statistics => statistics.ToSummary(Snapshots))
                        .OrderByDescending(summary => summary.PeakRssBytes)
                        .ThenBy(summary => summary.Program, StringComparer.Ordinal)
                        .Take(count)
                        .ToList();
    }

    /// <summary>
    /// Tells whether a program is in at least 90 percent of the snapshots, computed in integers.
    /// </summary>
    /// <param name="statistics">The program</param>
    /// <returns><c>true</c> for a permanent program</returns>
    private bool IsPermanent(ProgramStatistics statistics)
    {
        return statistics.Snapshots * PermanentDenominator >= Snapshots * PermanentNumerator;
    }

    /// <summary>
    /// Tells whether a program is in fewer than 90 percent of the snapshots.
    /// </summary>
    /// <param name="statistics">The program</param>
    /// <returns><c>true</c> for a transient program</returns>
    private bool IsTransient(ProgramStatistics statistics)
    {
        return statistics.Snapshots * PermanentDenominator < Snapshots * PermanentNumerator;
    }

    #endregion // Methods
}