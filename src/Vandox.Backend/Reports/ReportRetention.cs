namespace Vandox.Backend.Reports;

/// <summary>
/// The periods after which the report skips agent data; a null period is unlimited.
/// </summary>
public sealed class ReportRetention
{
    #region Properties

    /// <summary>
    /// Gets how long log lines are kept, or <c>null</c> for unlimited.
    /// </summary>
    public TimeSpan? Logs { get; init; }

    /// <summary>
    /// Gets how long process snapshots are kept, or <c>null</c> for unlimited.
    /// </summary>
    public TimeSpan? Snapshots { get; init; }

    #endregion // Properties
}