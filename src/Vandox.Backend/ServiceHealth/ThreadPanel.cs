using Vandox.Backend.History;

namespace Vandox.Backend.ServiceHealth;

/// <summary>
/// The section Long-running queries: the list of the snapshots and the threads of one of them.
/// </summary>
public sealed class ThreadPanel
{
    #region Properties

    /// <summary>
    /// Gets the snapshots of the range, newest first.
    /// </summary>
    public IReadOnlyList<ThreadStampRow> Stamps { get; init; } = [];

    /// <summary>
    /// Gets the text of the snapshots beyond the listed ones; empty when there are none.
    /// </summary>
    public string More { get; init; } = string.Empty;

    /// <summary>
    /// Gets the heading of the thread table.
    /// </summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the line on the sleeping connections; empty when there is none.
    /// </summary>
    public string Sleeping { get; init; } = string.Empty;

    /// <summary>
    /// Gets the table of the threads; <see langword="null"/> when no snapshot is shown.
    /// </summary>
    public SnapshotTableView? Threads { get; init; }

    /// <summary>
    /// Gets the notes of the section, in order.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    #endregion // Properties
}