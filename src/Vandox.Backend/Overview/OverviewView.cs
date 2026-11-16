using Vandox.Storage;

namespace Vandox.Backend.Overview;

// Skeleton: removed with the implementation
#pragma warning disable RH2003
/// <summary>
/// Turns the reads of the overview into its model.
/// </summary>
public static class OverviewView
{
    #region Constants

    /// <summary>
    /// The number of newest metric records scanned for a live one.
    /// </summary>
    public const int LiveScanLimit = 20000;

    /// <summary>
    /// The maximum number of units shown.
    /// </summary>
    public const int MaxUnits = 256;

    /// <summary>
    /// The maximum number of mounts shown.
    /// </summary>
    public const int MaxMounts = 16;

    /// <summary>
    /// The number of incident summaries read.
    /// </summary>
    public const int IncidentSummaries = 20;

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The interval of the refresh.
    /// </summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The deadline of one refresh.
    /// </summary>
    public static readonly TimeSpan RefreshDeadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The age after which data counts as stale.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The age after which the record of a unit counts as stale.
    /// </summary>
    public static readonly TimeSpan UnitStaleAfter = TimeSpan.FromMinutes(65);

    /// <summary>
    /// The age after which a unit that reports no more is left out.
    /// </summary>
    public static readonly TimeSpan UnitRetirement = TimeSpan.FromHours(2);

    /// <summary>
    /// The window in which the values of the mounts are read.
    /// </summary>
    public static readonly TimeSpan MountWindow = TimeSpan.FromMinutes(10);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the series the overview reads.
    /// </summary>
    public static IReadOnlyList<SeriesKey> Series { get; } = [];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Builds the model of the overview.
    /// </summary>
    /// <param name="data">The reads of one refresh</param>
    /// <param name="now">The current time</param>
    /// <returns>The model</returns>
    public static OverviewModel Build(OverviewData data, DateTimeOffset now)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}
#pragma warning restore RH2003