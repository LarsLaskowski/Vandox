using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.History;

// Skeleton: removed with the implementation
#pragma warning disable RH2003

/// <summary>
/// The panel of a process snapshot.
/// </summary>
public sealed class ProcessSnapshotView
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
    /// Gets the programs table.
    /// </summary>
    public SnapshotTableView Programs { get; init; } = new();

    /// <summary>
    /// Gets the processes table.
    /// </summary>
    public SnapshotTableView Processes { get; init; } = new();

    /// <summary>
    /// Gets the text on the listed and the total number of processes; empty when the list is complete.
    /// </summary>
    public string ListedOfTotal { get; init; } = string.Empty;

    /// <summary>
    /// Gets the note on an incomplete snapshot; empty when there is none.
    /// </summary>
    public string Note { get; init; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Builds the panel of a process snapshot record.
    /// </summary>
    /// <param name="record">The stored snapshot record</param>
    /// <returns>The view</returns>
    public static ProcessSnapshotView FromProcesses(StoredRecord record)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}

#pragma warning restore RH2003