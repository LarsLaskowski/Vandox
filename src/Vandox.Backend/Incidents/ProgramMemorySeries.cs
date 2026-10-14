#pragma warning disable RH2003, S2325

using Vandox.Backend.Components.Charts;
using Vandox.Core.Incidents;

namespace Vandox.Backend.Incidents;

/// <summary>
/// Collects the memory per program of the snapshots of a window into a stacked chart.
/// </summary>
public sealed class ProgramMemorySeries
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgramMemorySeries"/> class.
    /// </summary>
    public ProgramMemorySeries()
    {
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of snapshots added.
    /// </summary>
    public int Count => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a snapshot.
    /// </summary>
    /// <param name="capturedAt">The time of the snapshot</param>
    /// <param name="programs">The memory per program</param>
    /// <returns><c>false</c> when the snapshot is skipped because it is not later than the previous one</returns>
    public bool Add(DateTimeOffset capturedAt, IReadOnlyDictionary<string, ProgramUsage> programs)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Builds the stacked chart: the five programs with the highest peak and the other programs summed, in MiB.
    /// </summary>
    /// <returns>The chart, or <c>null</c> without a snapshot</returns>
    public ChartData? ToChart()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}