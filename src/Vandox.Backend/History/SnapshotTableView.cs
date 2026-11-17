namespace Vandox.Backend.History;

/// <summary>
/// A table of a snapshot panel.
/// </summary>
public sealed class SnapshotTableView
{
    #region Properties

    /// <summary>
    /// Gets the heading.
    /// </summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the column names.
    /// </summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>
    /// Gets the rows.
    /// </summary>
    public IReadOnlyList<SnapshotRowView> Rows { get; init; } = [];

    /// <summary>
    /// Gets the text of the rows beyond the listed ones; empty when there are none.
    /// </summary>
    public string More { get; init; } = string.Empty;

    #endregion // Properties
}