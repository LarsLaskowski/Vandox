using Vandox.Backend.Logs;

namespace Vandox.Backend.Overview;

/// <summary>
/// The row of a service unit.
/// </summary>
public sealed class OverviewUnitRow
{
    #region Properties

    /// <summary>
    /// Gets or sets the unit name as text runs.
    /// </summary>
    public IReadOnlyList<TextRun> Unit { get; set; } = [];

    /// <summary>
    /// Gets or sets the state text.
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the time the unit became active.
    /// </summary>
    public string ActiveSince { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the restart counter text.
    /// </summary>
    public string Restarts { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the recording time text.
    /// </summary>
    public string Recorded { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the unit failed.
    /// </summary>
    public bool Failed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the record is stale.
    /// </summary>
    public bool Stale { get; set; }

    #endregion // Properties
}