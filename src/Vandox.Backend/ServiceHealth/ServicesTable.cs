using Vandox.Backend.History;

namespace Vandox.Backend.ServiceHealth;

/// <summary>
/// The tables of the section Services with its notes.
/// </summary>
public sealed class ServicesTable
{
    #region Properties

    /// <summary>
    /// Gets the table of the units.
    /// </summary>
    public SnapshotTableView Units { get; init; } = new();

    /// <summary>
    /// Gets the table of the restarts.
    /// </summary>
    public SnapshotTableView Restarts { get; init; } = new();

    /// <summary>
    /// Gets the notes of the section, in order.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether no service state is stored in the range.
    /// </summary>
    public bool Empty { get; init; }

    #endregion // Properties
}