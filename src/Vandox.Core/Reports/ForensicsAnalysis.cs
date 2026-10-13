using Vandox.Core.Incidents;
using Vandox.Core.Model;

namespace Vandox.Core.Reports;

#pragma warning disable RH2003, S2325

/// <summary>
/// The forensics analysis over the whole period. Use order: <c>AddLogLine</c>, <c>AddBoot</c> and <c>AddProcessSnapshot</c>, then
/// <see cref="Seal"/>, then <see cref="AddIncident"/> for each incident, then <see cref="Summarize"/>; a call out of order throws
/// <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class ForensicsAnalysis
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ForensicsAnalysis"/> class with the default caps.
    /// </summary>
    /// <param name="lookback">The lookback before an outage</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lookback"/> is not positive</exception>
    public ForensicsAnalysis(TimeSpan lookback)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ForensicsAnalysis"/> class.
    /// </summary>
    /// <param name="lookback">The lookback before an outage</param>
    /// <param name="maxSnapshots">The most snapshot points kept</param>
    /// <param name="maxBoots">The most boots kept</param>
    /// <param name="maxObservedRuns">The most runs of observed hours kept</param>
    /// <param name="maxPrograms">The most programs tracked</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lookback"/> is not positive</exception>
    public ForensicsAnalysis(TimeSpan lookback, int maxSnapshots, int maxBoots, int maxObservedRuns, int maxPrograms)
    {
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the log sources whose lines count as observation of the system: <c>journal</c> and <c>syslog</c>.
    /// </summary>
    public static IReadOnlyList<string> SystemLogSources => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a log line; only lines of the system log sources count as observed hours.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="source">The log source</param>
    public void AddLogLine(DateTimeOffset capturedAt, string source)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Adds a boot.
    /// </summary>
    /// <param name="at">The time of the boot</param>
    public void AddBoot(DateTimeOffset at)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Adds a process snapshot.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="snapshot">The snapshot</param>
    public void AddProcessSnapshot(DateTimeOffset capturedAt, ProcessSnapshot snapshot)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Ends the collection of lines, boots and snapshots and computes the threshold and the base rates.
    /// </summary>
    public void Seal()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Judges what ran or happened before an incident.
    /// </summary>
    /// <param name="incident">The incident</param>
    /// <returns>The activities</returns>
    public IncidentActivities Activities(Incident incident)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Counts an incident. Only counters are kept (24 hour counts, the yes and known counts per activity, the first and last
    /// outage, the kill sum, saturating), never a list of incidents.
    /// </summary>
    /// <param name="incident">The incident</param>
    public void AddIncident(Incident incident)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Evaluates the rules and collects the whole-period result.
    /// </summary>
    /// <param name="generatedAt">The generation time</param>
    /// <returns>The summary</returns>
    public ForensicsSummary Summarize(DateTimeOffset generatedAt)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}