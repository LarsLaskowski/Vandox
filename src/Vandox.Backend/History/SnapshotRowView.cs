using Vandox.Backend.Logs;

namespace Vandox.Backend.History;

/// <summary>
/// A row of a snapshot table.
/// </summary>
public sealed class SnapshotRowView
{
    #region Properties

    /// <summary>
    /// Gets the cells, each a list of text runs.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<TextRun>> Cells { get; init; } = [];

    #endregion // Properties
}