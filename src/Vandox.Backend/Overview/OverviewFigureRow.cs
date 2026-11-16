using Vandox.Backend.Logs;

namespace Vandox.Backend.Overview;

/// <summary>
/// The row of a key figure.
/// </summary>
public sealed class OverviewFigureRow
{
    #region Properties

    /// <summary>
    /// Gets or sets the name of the figure.
    /// </summary>
    public string Figure { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the mount as text runs, empty for a figure without a mount.
    /// </summary>
    public IReadOnlyList<TextRun> Mount { get; set; } = [];

    /// <summary>
    /// Gets or sets the value text.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the time text.
    /// </summary>
    public string Time { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the value is stale.
    /// </summary>
    public bool Stale { get; set; }

    #endregion // Properties
}