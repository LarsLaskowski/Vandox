using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.History;

// Skeleton: removed with the implementation
#pragma warning disable RH2003

/// <summary>
/// The panel of a connection snapshot.
/// </summary>
public sealed class ConnectionSnapshotView
{
    #region Properties

    /// <summary>
    /// Gets the capture time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Gets the source as text runs.
    /// </summary>
    public IReadOnlyList<TextRun> Source { get; init; } = [];

    /// <summary>
    /// Gets the table of the counts per protocol and state.
    /// </summary>
    public SnapshotTableView States { get; init; } = new();

    /// <summary>
    /// Gets the processes table.
    /// </summary>
    public SnapshotTableView Processes { get; init; } = new();

    /// <summary>
    /// Gets the remote addresses table.
    /// </summary>
    public SnapshotTableView Remotes { get; init; } = new();

    /// <summary>
    /// Gets the note on incomplete counts; empty when there is none.
    /// </summary>
    public string Note { get; init; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Builds the panel of a connection snapshot record.
    /// </summary>
    /// <param name="record">The stored snapshot record</param>
    /// <returns>The view</returns>
    public static ConnectionSnapshotView FromConnections(StoredRecord record)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}

#pragma warning restore RH2003