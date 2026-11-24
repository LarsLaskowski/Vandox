using Vandox.Backend.History;

namespace Vandox.Backend.Network;

/// <summary>
/// The table of the top remote addresses with its notes.
/// </summary>
public sealed class NetworkAddresses
{
    #region Properties

    /// <summary>
    /// Gets the table.
    /// </summary>
    public SnapshotTableView Table { get; init; } = new();

    /// <summary>
    /// Gets the notes of the table.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    #endregion // Properties
}